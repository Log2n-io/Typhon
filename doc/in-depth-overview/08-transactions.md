# 08 — Transactions

**Code:** [`src/Typhon.Engine/Transactions/`](../../src/Typhon.Engine/Transactions/)

Transactions are how mutations enter Typhon. A `Transaction` is the unit of *isolation* (MVCC snapshot, conflict detection, rollback) sitting inside a `UnitOfWork` — the unit of *durability* (when WAL records become crash-safe). Together with the `TransactionChain` (visibility horizon) and the `UowRegistry` (persistent UoW ID allocator), they form the commit pipeline that bridges ECS mutations ([06-ecs](06-ecs.md)) and the WAL ([11-durability](11-durability.md)).

This is the doc to read when you want to know what `tx.Commit()` actually does, what `DurabilityMode` controls, why transactions are single-thread-affine, and where the engine puts the seam between "isolation" and "durability".

<a href="assets/typhon-uow-lifecycle-states.svg">
  <img src="assets/typhon-uow-lifecycle-states.svg" width="678" alt="UoW lifecycle states">
</a>
<br>
<sub>The two interacting state machines: the UoW lifecycle (Idle → Pending → WalDurable → Committed, plus the crash-recovery Void path) and the Transaction lifecycle (Pool → Created → InProgress → Committed/Rollbacked). <code>DurabilityMode</code> drives the Pending → WalDurable transition; each UoW state also carries its durability guarantee — Volatile (crash → data lost) → Durable (crash → replayed from WAL) → Checkpointed (crash → nothing to replay).</sub>

---

## 1. Overview — the transaction model

Three tiers, top to bottom:

| Tier | What it is | Owns |
|---|---|---|
| `DatabaseEngine` | Process-wide engine instance | Everything below |
| `UnitOfWork` | Durability boundary — N transactions, one WAL/checkpoint cycle | `UowId`, optional shared `ChangeSet` |
| `Transaction` | Isolation boundary — one writer, one snapshot TSN | Per-component dirty state, deferred-cleanup batch |

A few invariants the rest of this doc assumes:

- **Single-thread-affine.** A `Transaction` is created on a thread and only that thread may operate on it. There's no internal locking on the `Transaction` object itself — the type is `[NoCopy]` in spirit and asserts thread affinity in DEBUG builds.
- **Scheduler-managed in production.** Inside the runtime ([10-runtime](10-runtime.md)), each tick creates *one* `UnitOfWork`, and each system that mutates gets *one* `Transaction` from that UoW. `PipelineSystem`s have no `Transaction` — they read at a snapshot via `PointInTimeAccessor` ([06-ecs](06-ecs.md) §5). Application code that uses the engine directly (Shell, tests) follows the same shape but builds the UoW itself.
- **TSN is allocated at Transaction construction**, not at commit. `TransactionChain._nextFreeId` is `Interlocked.Increment`-ed every time a `Transaction` is created (or a `PointInTimeAccessor` reads). The TSN is the snapshot point for reads and gets stamped on every revision element this transaction writes.
- **`UowId` is allocated at UoW construction** from the `UowRegistry`, persistent (survives crash), and stamps every revision element so recovery can void the writes of a UoW that didn't commit ([11-durability](11-durability.md) §4).

### Storage modes — what's transactional, what isn't

Every component declares a **storage mode** — `Versioned` (default), `SingleVersion`, or `Transient` ([06-ecs §8](06-ecs.md)). Don't assume this whole chapter applies uniformly to all three: **entity lifecycle is transactional in every mode, but only Versioned *component data* flows through the isolation / rollback / commit-durability machinery described below.**

| Aspect | Versioned (default) | SingleVersion | Transient |
|---|---|---|---|
| `Spawn` / `Destroy` (entity existence) | transactional — staged, applied at commit | transactional | transactional |
| Component **data** write | staged, MVCC-isolated, reverts on `Rollback` | in-place, immediate | in-place, immediate |
| Visible to other transactions pre-commit | no (IsolationFlag) | yes, immediately | yes, immediately |
| Conflict detection at commit | yes | no (last-writer-wins) | no (last-writer-wins) |
| Durability of data | commit-WAL, per `DurabilityMode` | tick-fence WAL (≤ 1 tick loss) | never (in-memory) |

So the **commit path, MVCC conflict detection, `Rollback`, and `DurabilityMode`** sections below govern *Versioned component data*. For SV/Transient components a transaction still gives you entity-lifecycle atomicity, thread affinity, and a consistent read snapshot — but their *data* writes land immediately, can't be rolled back, and don't ride the commit-WAL. See [06-ecs §8](06-ecs.md) for the full per-mode contract.

The doc walks the stack bottom-up: durability semantics (`UnitOfWork` + `DurabilityMode`), then isolation (`Transaction`, commit), then the chain and registry that back them, then deadlines and metrics.

---

## 2. UnitOfWork — the durability boundary

[`Transactions/public/UnitOfWork.cs`](../../src/Typhon.Engine/Transactions/public/UnitOfWork.cs)

A `UnitOfWork` is one *durability decision*. It allocates a `UowId` from the registry, batches one-or-more `Transaction`s, and decides when the WAL records or dirty pages cross from "in-process buffer" to "on stable storage".

```csharp
using var uow = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit);
using var tx1 = uow.CreateTransaction();
// ... mutations ...
tx1.Commit();
// uow.Dispose() runs the flush per its DurabilityMode
```

### 2.1 `DurabilityMode`

[`Transactions/public/DurabilityMode.cs`](../../src/Typhon.Engine/Transactions/public/DurabilityMode.cs)

Three values, all sit at the UoW level (per-transaction override exists in the type system as `DurabilityOverride` but is not wired into any commit overload — Commit takes only `(ref UnitOfWorkContext, ConcurrencyConflictHandler)`).

| Mode | When records become crash-safe | Commit latency | Data-at-risk window |
|---|---|---|---|
| `Deferred` (default) | Only after explicit `Flush()` / `FlushAsync()` | ~1-2 µs | Until next flush — caller responsibility |
| `GroupCommit` | WAL writer auto-flushes every ~5 ms | ~1-2 µs | ≤ group-commit interval |
| `Immediate` | FUA on every `tx.Commit()` — blocks until WAL record is on stable media | ~15-85 µs | Zero |

The pricing argument is straightforward: `Deferred` amortizes fsync over a whole tick of mutations (the runtime's model); `GroupCommit` is the general-purpose middle ground; `Immediate` is for state changes you cannot reverse (financial trades, irreversible-side-effect commands).

### 2.2 The ChangeSet model

`Deferred` and `GroupCommit` share a single `ChangeSet` across all transactions in the UoW — the page mutations from every transaction land in one batch. `Immediate` gives each transaction its *own* `ChangeSet` so its `Commit()` can flush in isolation. The `ChangeSet` is the dirty-page accounting layer that the page cache ([02-storage](02-storage.md) §5) uses to decide what to write.

The UoW pre-allocates the shared `ChangeSet` before allocating the `UowId`. That ordering is deliberate: registry page mutations (writing the new `UowRegistryEntry`) piggyback on this `ChangeSet` instead of triggering a synchronous I/O on whatever thread is calling `CreateUnitOfWork`.

### 2.3 Commit lifecycle

`UnitOfWork` itself has no "commit" method — *transactions* commit, the UoW *flushes*. Lifecycle:

1. `CreateUnitOfWork` → state `Pending`, `UowId` allocated, shared `ChangeSet` created (if applicable).
2. Each `tx.Commit()` writes revision elements (stamped with `UowId`) and, in WAL mode, serializes a UoW-prefixed WAL record via `WalSerializer.SerializeToWal` (`Transactions/internals/WalSerializer.cs`).
3. `uow.Flush()` or `uow.FlushAsync()` (or `Dispose` for non-`Deferred`) advances state to `WalDurable` and calls `UowRegistry.RecordCommit(uowId, 0, ChangeSet)` to mark the slot as committed-in-the-registry.
4. The checkpoint later transitions `WalDurable → Committed` once data pages are fsynced ([11-durability](11-durability.md) §5).

### 2.4 `ReleaseExcessDirtyMarks` on Dispose (WAL mode)

In WAL mode the `ChangeSet` accumulates dirty-page marks that *never* get balanced by `SaveChangesAsync` — only the checkpoint thread writes those pages. Left alone, `DirtyCounter` would inflate across many UoWs. `Dispose` calls `ChangeSet.ReleaseExcessDirtyMarks()` which caps the counter at 1 (so the page stays dirty for the next checkpoint, but one checkpoint cycle is enough to make it evictable). This is the lifecycle hook that bounds DC inflation across long-running workloads.

---

## 3. Transaction — isolation, mutation, commit

[`Transactions/public/Transaction.cs`](../../src/Typhon.Engine/Transactions/public/Transaction.cs), [`Transaction.ECS.cs`](../../src/Typhon.Engine/Transactions/public/Transaction.ECS.cs)

`Transaction` extends `EntityAccessor` ([06-ecs](06-ecs.md) §5) — the same `Open` / `OpenMut` surface that read-only accessors use, plus mutation hooks (`Spawn`, `Destroy`, write paths) and the `Commit` / `Rollback` finalization.

### 3.1 Creating a transaction

Three entry points:

```csharp
// Inside a UoW — the canonical pattern.
using var uow = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit);
using var tx  = uow.CreateTransaction();

// Convenience for single-transaction UoWs. Disposing tx also disposes the backing UoW.
using var tx  = dbe.CreateQuickTransaction();

// Read-only — no UoW, no ChangeSet, write methods throw, Commit is a no-op.
using var tx  = dbe.CreateReadOnlyTransaction();
```

`CreateQuickTransaction` ([`DatabaseEngineExtensions.cs`](../../src/Typhon.Engine/Transactions/public/DatabaseEngineExtensions.cs)) is sugar that creates a UoW, creates a transaction inside it, and sets `tx.OwnsUnitOfWork = true` — `Dispose` propagates and disposes the owning UoW.

### 3.2 Read-only path

`IsReadOnly` is set at `Init`. Read-only transactions:

- Don't allocate a `ChangeSet`.
- Don't allocate a `UowId` (the parent UoW does, but a read-only tx isn't created via a UoW — there's no parent in this path).
- Throw `InvalidOperationException` from `EnsureMutable` on any write attempt.
- `Commit()` returns `true` immediately. `Dispose()` exits the epoch scope and removes from the chain — no commit/rollback path, no `DeferredCleanupManager` interaction.

### 3.3 `Commit(ref UnitOfWorkContext, ConcurrencyConflictHandler)`

The full signature is:

```csharp
public bool Commit(ref UnitOfWorkContext ctx, ConcurrencyConflictHandler handler = null);
public bool Commit(ConcurrencyConflictHandler handler = null); // synthesizes ctx from TimeoutOptions.Current.DefaultCommitTimeout
```

There is no `Commit(DurabilityOverride)` overload — `DurabilityOverride` is defined in [`DurabilityMode.cs`](../../src/Typhon.Engine/Transactions/public/DurabilityMode.cs) but is not referenced by any production code path. Likewise no `tx.Durability` property, no `tx.WaitForDurability()` method, no `DurabilityGuarantee` enum — those exist in older design sketches and never reached the engine.

Commit walks every modified component type and, per entity:

1. Acquires a shared lock on `TransactionChain.Control` once (not per-entity) to determine `IsTail` and `NextMinTSN`.
2. Calls `PrepareEcsDestroys` — turns ECS `Destroy(EntityId)` calls into per-component tombstone revisions so the per-entity path below handles them uniformly.
3. For each component type, calls `CommitComponentCore` per entity:
   - Optionally takes the per-entity revision chain exclusive lock when a `ConcurrencyConflictHandler` is provided.
   - Detects write-write conflicts via `CommitSequence` + invisible-commit TSN checks; with no handler, falls back to "last writer wins".
   - When a conflict fires, copies the read / committed / committing / to-commit pointers into a thread-local `ConcurrencyConflictSolver` and invokes the handler.
   - Calls `IndexMaintainer.UpdateIndices` / `RemoveSecondaryIndices` for B+Tree maintenance (per-table indexes) or `CommitClusterVersionedSlot` for cluster archetypes (per-archetype indexes + copy committed value into cluster slot).
   - Updates `LastCommitRevisionIndex`, increments `CommitSequence`, clears the revision element's `IsolationFlag`.
4. `FlushEcsPendingOperations` → `FinalizeSpawns` — walks pending spawns from `Transaction.ECS.cs`, allocates final `EntityRecord`s, stamps `BornTSN = TSN`, copies into the cluster layout for cluster-eligible archetypes.
5. `PersistAndFinalize`:
   - In WAL mode: `WalSerializer.SerializeToWal(...)` writes the UoW-prefixed record set to the WAL ring buffer.
   - For `DurabilityMode.Immediate` in WAL mode: `_dbe.WalManager.RequestFlush()` then `_dbe.WalManager.WaitForDurable(lsn, ref ctx)`.
   - For `DurabilityMode.Immediate` in WAL-less mode: flush all `ChangeSet` pages and `MMF.FlushToDisk()` before returning.
   - Transition state to `Committed`, record duration metric.

### 3.4 `ConcurrencyConflictHandler`

[`Transactions/internals/CommitContext.cs`](../../src/Typhon.Engine/Transactions/internals/CommitContext.cs):

```csharp
public delegate void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver);
```

Returns `void`. The handler *mutates* the solver's `ToCommitData<T>()` buffer (initialized to `CommittingData` for last-writer-wins semantics). There is no `ConcurrencyConflictResult { Resolved, Rollback, Skip }` enum — that doesn't exist anywhere. The solver gives you `ReadData`, `CommittedData`, `CommittingData`, `ToCommitData` (refs into page memory) and convenience helpers `TakeRead`, `TakeCommitted`, `TakeCommitting`.

### 3.5 `Rollback`

`Rollback(ref UnitOfWorkContext, TransactionRollbackReason)` walks every modified component, voids each revision entry, frees content chunks (for Created entries it deletes the rev-table chunk outright), and enqueues entities for `DeferredCleanupManager`. Auto-fires on `Dispose` if the transaction was never committed (with `reason = AutoOnDispose`).

The whole rollback body runs inside a `ctx.EnterHoldoff()` block — once you've started undoing state, you finish.

---

## 4. TransactionChain — visibility horizon

[`Transactions/internals/TransactionChain.cs`](../../src/Typhon.Engine/Transactions/internals/TransactionChain.cs)

One instance per `DatabaseEngine`. Owns the live set of `Transaction`s, the global TSN counter, and the transaction pool.

### 4.1 Structure — **singly-linked Head → Tail with `Next` pointer**

Yes, singly-linked. Earlier docs called this doubly-linked; the code has only `Transaction.Next`. New transactions are prepended at Head; the Tail is the oldest live transaction. `MinTSN = Tail.TSN` — this is the visibility horizon: no live transaction can be reading at a snapshot older than `MinTSN`, so revisions with `DeadTSN < MinTSN` are unreachable and can be cleaned up.

```
       newest                                       oldest
        ┌─────────────┐    Next    ┌──────────────┐    Next    ┌──────────────┐
Head ──►│ Tx (TSN=42) │ ─────────► │ Tx (TSN=41)  │ ─────────► │ Tx (TSN=37)  │ ◄── Tail
        └─────────────┘            └──────────────┘            └──────────────┘
                                                                MinTSN = 37
```

### 4.2 `PushHead` — lock-free CAS

```csharp
do {
    oldHead = Volatile.Read(ref _head);
    transaction.Next = oldHead;
} while (Interlocked.CompareExchange(ref _head, transaction, oldHead) != oldHead);
```

Pure CAS loop on `_head`. Concurrent with other `PushHead`s (CAS contention only) and with `Remove` (which serializes via the chain's exclusive lock). `_tail` is established via a one-shot `Interlocked.CompareExchange(ref _tail, transaction, null)` on the first push.

### 4.3 `Remove` — exclusive lock + re-scan fallback

`Remove` acquires `Control.EnterExclusiveAccess` and scans head-to-target for the predecessor. The interesting case is when the target is at Head: a concurrent `PushHead` may have inserted nodes between the Volatile.Read of `_head` and the CAS. The fallback re-scans from the new `_head` to locate the new predecessor of `transaction` and splices it out. No retry loop required — the scan converges because `Remove` holds the exclusive lock and any new pushes can only happen *before* the current `_head`, not in the middle.

### 4.4 `ComputeNextMinTSN` — second-to-last

When the tail commits or rolls back, we need to know what the *new* tail's TSN will be so the `DeferredCleanupManager` knows what's safe to release. `ComputeNextMinTSN` walks head→tail and returns the second-to-last node's TSN (or `_nextFreeId + 1` if the chain is empty after this removal). Caller must hold the shared lock on `Control`.

### 4.5 Pooling

`PoolMaxSize = 16`. Backing store: `ConcurrentQueue<Transaction>`. On `CreateTransaction`, dequeue or allocate new; on `Remove`, enqueue if pool isn't full (else GC the instance). Pool count is observable via `PoolCount` for the `TxChainPoolSize` gauge.

### 4.6 TSN allocation

```csharp
internal long AllocateTSN() => Interlocked.Increment(ref _nextFreeId);
```

`PointInTimeAccessor` uses this directly without creating a `Transaction`. Regular `CreateTransaction` increments `_nextFreeId` and passes the result to `Transaction.Init`. There's no separate `TimeManager` or `ExecutionFrame` class — the chain *is* the TSN allocator. There's also no public `TSN` struct wrapper — TSN is a plain `long` on `Transaction.TSN`.

---

## 5. UowRegistry — persistent UoW slots

[`Transactions/internals/UowRegistry.cs`](../../src/Typhon.Engine/Transactions/internals/UowRegistry.cs)

The chain stores live transactions in memory; the **registry** stores UoW slots *persistently* so crash recovery can decide which UoWs reached `WalDurable` and which were `Pending` at crash time (and must have their writes voided).

### 5.1 `UowRegistryEntry`

| Field | Bytes |
|---|---|
| `State` (`UnitOfWorkState`) | 1 |
| `Reserved` | 1 |
| `Reserved2` | 2 |
| `TransactionCount` | 4 |
| `CreatedTicks` | 8 |
| `CommittedTicks` | 8 |
| `MaxTSN` | 8 |
| `Reserved3` | 8 |
| **Total** | **40** |

40 bytes divides evenly into both page layouts: **`RootCapacity = 150`** (6000 / 40) and **`OverflowCapacity = 200`** (8000 / 40). Zero waste. **`MaxUowId = 32767`** — 15-bit ID space (the 16th bit on revision elements is the `IsolationFlag`).

`State = 0 (Free)` means a zeroed page is interpreted as all-free, so growth doesn't need explicit initialization.

### 5.2 Allocation

`AllocateUowId(ref WaitContext)` scans an in-memory allocation bitmap (512 × 64 bits = 32768 bits) for a free slot via `BitOperations.TrailingZeroCount`, claims via `Interlocked.And` against an inverted mask. If the bitmap is full, blocks on a `SemaphoreSlim` that `Release(uowId, ChangeSet)` signals.

The mutation of the registry page is registered against the caller's `ChangeSet` rather than triggering a synchronous `SaveChanges` — that's how `CreateUnitOfWork` avoids a fsync on the TickDriver thread.

### 5.3 `RecordCommit` and `Release`

```csharp
public void RecordCommit(ushort uowId, long maxTSN, ChangeSet externalCs = null);
public void Release(ushort uowId, ChangeSet externalCs = null);
```

`RecordCommit` is called from `UnitOfWork.TransitionToWalDurable` when the UoW becomes durable — it sets the entry to `Committed`, sets the committed-bitmap bit, increments cumulative counters. **`MaxTSN` is passed as `0`** — the field is recorded on the entry but is not read by any production code path; gauge increment is the observable effect.

`Release` is called from `UnitOfWork.Dispose` to free the slot. **Both calls pass the shared `ChangeSet`** so the registry page mutation piggybacks on the UoW's dirty-page accounting instead of forcing its own I/O. The on-disk registry copy is a checkpoint cache; the WAL record carries the authoritative state.

### 5.4 Recovery integration

On engine start, `LoadFromDiskRaw` scans every entry up to `_currentCapacity`, rebuilds both bitmaps, counts `Pending`/`WalDurable`/`Committed`/`Void` slots. WAL recovery then promotes `Pending → WalDurable` for UoWs whose commit marker survived; whatever's left in `Pending` gets voided via `VoidRemainingPending`. See [11-durability](11-durability.md) §7 for the full sequence.

---

## 6. DeferredCleanupManager — tail-driven cleanup

[`Ecs/internals/DeferredCleanupManager.cs`](../../src/Typhon.Engine/Ecs/internals/DeferredCleanupManager.cs)

When a transaction commits, the entities it modified can't have their old revisions freed immediately — older live transactions might still be reading them. The DCM holds those cleanup requests keyed by the *blocking TSN* (the tail's TSN at the time of commit) and releases them when the tail finally moves past.

```csharp
public int ProcessDeferredCleanups(long completedTSN, long nextMinTSN, DatabaseEngine dbe, ChangeSet changeSet);
```

Invoked in three places:

1. **`Transaction.Commit`** — when the committing transaction is the tail (or the chain just became empty), drain everything blocked by TSNs ≤ `completedTSN`.
2. **`Transaction.Rollback`** — same logic on the rollback path (rollback also frees entries the caller would have cleaned up at commit).
3. **`Transaction.Dispose`** — via `ProcessDeferredCleanups()`, picks up any work missed because the commit path didn't see this transaction as tail at the time.

Reverse-indexed by `(ComponentTable, PrimaryKey)` for O(1) dedup so an entity touched by many transactions while a long tail is blocking doesn't accumulate multiple cleanup entries. There's also a separate `ChunkFreeQueueSize` for content-chunk freeing keyed by `safeAfterTSN` — chunks free when `nextMinTSN >= key`.

---

## 7. Deadlines & timeouts

[`Foundation/Concurrency/public/UnitOfWorkContext.cs`](../../src/Typhon.Engine/Foundation/Concurrency/public/UnitOfWorkContext.cs), [`Ecs/public/TimeoutOptions.cs`](../../src/Typhon.Engine/Ecs/public/TimeoutOptions.cs)

Every commit takes a `ref UnitOfWorkContext` — see [01-foundation §2](01-foundation.md). When you call `tx.Commit()` without a context, the engine synthesizes one from:

```csharp
TimeoutOptions.Current.DefaultCommitTimeout  // default 30 s
```

Inside `Commit`, individual lock acquisitions compose tighter deadlines with subsystem-specific budgets via `ComposeWaitContext(ref ctx, subsystemTimeout)`:

| Option | Default | Used for |
|---|---|---|
| `DefaultCommitTimeout` | 30 s | The umbrella budget for the whole commit |
| `RevisionChainLockTimeout` | 5 s | Per-entity exclusive lock during conflict resolution |
| `TransactionChainLockTimeout` | 10 s | Shared lock on the chain to determine `IsTail` |
| `DefaultUowTimeout` | 30 s | UoW lifetime budget when `CreateUnitOfWork` gets no timeout |

The composition uses `Deadline.Min(outer, subsystem-derived-deadline)` — whichever fires first wins. A `LockTimeoutException` from any of these locks carries the resource name (e.g., `RevisionChain/CommitConflict`) and elapsed duration.

`UnitOfWorkContext.None` (infinite deadline, no cancellation) is used by internal cleanup paths like rollback that must complete regardless of the caller's deadline.

---

## 8. Metrics

`TransactionChain` exposes three cumulative gauges (monotonic, sampled per tick; viewer derives per-tick rates by subtraction):

| Field | Incremented from |
|---|---|
| `_createdTotal` | `CreateTransaction` — every dequeue or fresh allocation |
| `_commitTotal` | `Transaction.Commit` entry (before the body — see code comment for why "attempted ≈ successful") |
| `_rollbackTotal` | `Transaction.Rollback` entry, including auto-rollback on dispose |

Plus `PoolCount` (idle transactions in the pool, max 16) and `ActiveCount` (currently live in the chain). `UowRegistry` carries its own `CreatedTotal` and `CommittedTotal` — separate from per-transaction counters because one UoW hosts N transactions.

---

## See also

<a href="assets/typhon-uow-swimlane.svg">
  <img src="assets/typhon-uow-swimlane.svg" width="1200" alt="UoW commit swimlane">
</a>
<br>
<sub>Which layer owns each step of the commit path — Application / Data Engine / Durability / Storage swimlanes, from <code>CreateUnitOfWork</code> through conflict detection, <code>ApplyChanges</code> (clear IsolationFlag), WAL serialize, and the durability decision.</sub>

<a href="assets/typhon-uow-registry-lifecycle.svg">
  <img src="assets/typhon-uow-registry-lifecycle.svg" width="1200" alt="UoW registry lifecycle">
</a>
<br>
<sub>The <code>UowRegistryEntry</code> state machine: Free → Pending → WalDurable → Committed (slot released back to Free when no live reader can see the UoW), plus the crash-recovery Pending → Void path.</sub>

<a href="assets/typhon-uow-data-impact.svg">
  <img src="assets/typhon-uow-data-impact.svg" width="1200" alt="UoW impact on data">
</a>
<br>
<sub>Step-by-step data impact of a UoW: allocate <code>UowId</code> from the registry, rent a <code>UnitOfWorkContext</code>, ECS operations stamp the <code>UowId</code> at write time, commit clears the IsolationFlag, and the registry entry advances as durability progresses.</sub>

<a href="assets/typhon-immediate-commit-sequence.svg">
  <img src="assets/typhon-immediate-commit-sequence.svg" width="1200" alt="Immediate commit sequence">
</a>
<br>
<sub>The Immediate-durability commit sequence: conflict check → <code>ApplyChanges</code> → clear IsolationFlag → <code>WalSerializer.SerializeToWal</code> → <code>WalManager.RequestFlush</code> → block on <code>WaitForDurable(lsn)</code> until the FUA write completes.</sub>

---

- [01-foundation](01-foundation.md) — `UnitOfWorkContext`, `WaitContext`, `Deadline`, `EpochManager` (the primitives every transaction operation sits on)
- [05-revision](05-revision.md) — how `UowId` is encoded into revision elements and consumed by the visibility check
- [06-ecs](06-ecs.md) — `Spawn` / `Destroy` live on `Transaction`; the commit pipeline calls `PrepareEcsDestroys` → `FlushEcsPendingOperations` → `FinalizeSpawns` → cluster-versioned slot commit
- [11-durability](11-durability.md) — WAL integration (`SerializeToWal`, `RequestFlush`, `WaitForDurable`), checkpoint pipeline, the `Pending → WalDurable → Committed → Free` UoW state machine
