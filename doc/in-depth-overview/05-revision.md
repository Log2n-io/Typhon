---
uid: overview-revision
title: '05 — Revision (MVCC)'
description: '💡 Scope. Typhon supports three per-component storage modes — Versioned (full MVCC), SingleVersion (in-place, no isolation), and Transient (in-memory, no…'
---

# 05 — Revision (MVCC)

**Code:** [`src/Typhon.Engine/Revision/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Revision) (+ [`Ecs/public/ComponentTable.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/ComponentTable.cs) where `CompRevStorageElement` lives, [`Ecs/internals/EnabledBitsOverrides.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/EnabledBitsOverrides.cs) / [`EnabledBitsHistory.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/EnabledBitsHistory.cs), [`Transactions/internals/RevisionWalker.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Transactions/internals/RevisionWalker.cs))

> 💡 **Scope.** Typhon supports three per-component storage modes — **Versioned** (full MVCC), **SingleVersion** (in-place, no isolation), and **Transient** (in-memory, no persistence). 
> 
> **This doc covers only the Versioned mode.** SV and Transient bypass the revision chain entirely: writes go straight to the component slot. The mode is set per component type via the `StorageMode` argument on its `[Component]` attribute (`[Component("name", rev, StorageMode = StorageMode.SingleVersion)]`). See [06-ecs §8](06-ecs.md) for the full comparison.

Revision is where Typhon's MVCC (Multi-Version Concurrency Control) lives. Every Versioned component update appends a new entry to a per-entity **revision chain**; every read walks the chain and picks the latest version whose Transaction Sequence Number (TSN) is ≤ the reader's snapshot TSN. That single mechanism is what makes concurrent reads and writes coexist without locks on the read path.

This is engine internals — application code never touches `CompRevStorageElement` or `ComponentRevisionManager` directly. You hit MVCC through the ECS API ([06-ecs](06-ecs.md)) and the transaction model ([08-transactions](08-transactions.md)). Read this doc when you want to understand *why* `Open(id)` at TSN 1000 still returns the pre-commit version of an entity that a concurrent transaction is mutating at TSN 1042, or when you're debugging a revision-chain cleanup issue.

---

## 1. Overview — MVCC mechanics

The contract: **every reader sees a consistent snapshot of the database identified by its TSN**. Writers don't block readers; readers don't block writers; the only synchronization is on the per-entity revision chain itself (a small `AccessControlSmall` on each chain head, taken in exclusive mode for chain extension, shared mode for chain walks).

```
TSN axis →

    [tx-A reads at TSN=100]                  [tx-B reads at TSN=200]
              ↓                                          ↓
   entity X: rev{TSN=42}    rev{TSN=150}    rev{TSN=180}
              ↑               ↑                ↑
              tx-A sees this  tx-A blind      tx-B sees this
                              (TSN > 100)
```

- **Snapshot isolation:** a transaction's TSN is fixed at start. All Versioned reads through that transaction walk the chain to find the *latest committed entry with TSN ≤ that snapshot TSN*.
- **No phantom reads, no non-repeatable reads** within a transaction: the snapshot is immutable; later commits by other transactions are invisible.
- **Write-write conflicts** are detected at commit time (see [08-transactions](08-transactions.md) for the conflict detection and resolution path). The Revision layer just records the entries; conflict resolution is a transactional concern.

Why it matters for performance: the read path is **lock-free in the common single-entry case**, allocation-free, and runs in nanoseconds per entity. The chain walk only happens when the entity has multiple versions present — which only happens while concurrent transactions are active. Steady-state entities (no in-flight write) have a one-entry chain that's resolved in a single chunk read.

---

## 2. Revision element layout

[`Ecs/public/ComponentTable.cs:89`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/ComponentTable.cs)

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct CompRevStorageElement
{
    public int      ComponentChunkId;       // offset 0, 4 bytes
    private uint    _packedTickHigh;        // offset 4, 4 bytes — TSN bits 16..47
    private ushort  _packedTickLow;         // offset 8, 2 bytes — TSN bits  0..15
    private ushort  _packedUowId;           // offset 10, 2 bytes — UowId in bits 0..14, IsolationFlag in bit 15
}
```

**12 bytes total**, `[Pack = 2]`. Why packed across `uint + ushort` for the TSN? Layout density. The root revision chunk is 64 bytes; with a 28-byte header it leaves 36 bytes for elements = exactly **3 elements per root chunk** ((64 − 28) / 12 = 3). Overflow chunks have only a 4-byte "next chunk" header → 60 bytes for elements = **5 elements per overflow chunk** (60 / 12 = 5). One byte more per element and the chunk math would be wasteful.

| Field | Bits | What |
|---|---|---|
| `ComponentChunkId` | 32 | Chunk ID of the component's data payload. **0 = tombstone** (delete entry). |
| `_packedTickHigh` + `_packedTickLow` | 48 | Full TSN reassembled as `(packedTickHigh << 16) \| packedTickLow`. 48-bit range — 280+ trillion transactions. |
| `_packedUowId` bits 0–14 | 15 | `UowId` — the Unit of Work that produced this revision. Max 32,767 concurrent UoWs ([08-transactions](08-transactions.md) §UowRegistry). |
| `_packedUowId` bit 15 | 1 | `IsolationFlag`. **Set while uncommitted**: the revision is visible to its own writer but invisible to all other readers. Cleared at commit. |

The `IsVoid` predicate — `ComponentChunkId == 0 && _packedTickHigh == 0 && _packedTickLow == 0 && _packedUowId == 0` — distinguishes a *cleared/rolled-back* slot (all zeros) from a *tombstone* (zero ComponentChunkId but a real TSN). Cleanup uses this to skip voided entries during chain compaction.

**Important:** `TSN` is *not* a separate struct — it's a plain `long` carried on `EntityAccessor.TSN` and packed across the two `_packedTick*` fields on the storage element. There's no `readonly struct TSN { ... }` wrapper.

---

## 3. Revision chain

Each Versioned component, per entity, owns a small linked list of 64-byte chunks in `CompRevTableSegment`:

```
       ┌────────── root chunk (64 B) ─────────────────────┐
       │ CompRevStorageHeader (28 B):                     │
       │   NextChunkId, Control (AccessControlSmall),     │
       │   FirstItemIndex, ItemCount, ChainLength,        │
       │   LastCommitRevisionIndex, EntityPK,             │
       │   CommitSequence                                 │
       │ ─────────────────────────────                    │
       │ CompRevStorageElement[3]   (36 B = 3 × 12)       │
       └──────────────┬───────────────────────────────────┘
                      │ NextChunkId (when chain grows)
                      ▼
       ┌────────── overflow chunk (64 B) ─────────────────┐
       │ NextChunkId (4 B)                                │
       │ CompRevStorageElement[5]   (60 B = 5 × 12)       │
       └──────────────┬───────────────────────────────────┘
                      ▼
                    (… circular)
```

- **Singly linked**, `NextChunkId` only — there is no back pointer.
- **Circular buffer:** `FirstItemIndex` advances as the oldest entries get pruned by cleanup. The chain grows by linking a new overflow chunk at the appropriate position (`GrowChain` in `ComponentRevisionManager`). When the chain wraps, walks honour `FirstItemIndex` as the starting position; `RevisionEnumerator` carries a `HasLopped` flag for the wrap.
- **Per-chain lock:** the header carries an `AccessControlSmall` (4 bytes). Shared mode for chain walks (multiple readers); exclusive mode for chain extension and cleanup. The lock is the *only* synchronization on the revision data structure — page-level concurrency is handled by the storage layer ([02-storage](02-storage.md)).

### Write-time UowId stamping

This is the single most important invariant to understand:

> **The UowId is stamped on every revision element at write time, inside `AddCompRev` — NOT in a commit-time loop.**

Look at `ComponentRevisionManager.AddCompRev`:

```csharp
internal static unsafe void AddCompRev(
    ComponentInfo info,
    ref ComponentInfo.CompRevInfo compRevInfo,
    long tsn,
    ushort uowId,           // ← passed in at write time
    bool isDelete,
    bool lockAlreadyHeld = false)
{
    // ...
    curChunkElements[indexInChunk].TSN = tsn;
    curChunkElements[indexInChunk].IsolationFlag = true;   // uncommitted
    curChunkElements[indexInChunk].UowId = uowId;          // ← stamped HERE
    curChunkElements[indexInChunk].ComponentChunkId = componentChunkId;
}
```

The caller is `Transaction.UpdateComponent` / `Transaction.DestroyComponent` / `Transaction.ECS.cs` (the spawn path). It reads `UowId => OwningUnitOfWork?.UowId ?? 0` from the current `UnitOfWork` ([01-foundation](01-foundation.md) — `UnitOfWorkContext`, [08-transactions](08-transactions.md) §UowRegistry).

At commit time the only Revision-layer mutation is **clearing the IsolationFlag** (and updating `LastCommitRevisionIndex` on the chain header) — the UowId is already there from the write. There is no second pass that walks the chain and stamps UowIds.

Why does this matter? Two reasons:

1. **Cleanup correctness.** The deferred cleanup path uses `IsolationFlag == true` as the signal that "an active uncommitted transaction wrote this; do not touch the chunk it points to even if its TSN looks old" — see `CleanUpUnusedEntriesCore` line 256. If UowIds were stamped at commit, cleanup would have no way to identify the writing UoW from the element alone.
2. **Crash recovery.** Recovery rebuilds committed revisions directly from the logical WAL records — `RecoveryApplier.CreateVersionedChainRoot` (`Durability/internals/`) allocates the content chunk and a single committed revision-chain root via `ComponentRevisionManager.AllocCompRevStorage`, committing it at the record's TSN — reconstructing the same committed chain shape the live spawn/commit path produces.

### Insert path

For the very first write to an entity's component, the chain doesn't exist yet. `AllocCompRevStorage` allocates a brand-new root chunk, initializes the header, and writes element 0 with `TSN`, `UowId`, and `IsolationFlag = true`. Subsequent writes for the *same* component on the *same* entity reuse the chain — `AddCompRev` either appends to the current chunk or grows the chain by one overflow chunk (when `ItemCount == ComputeRevElementCount(chainLength)`).

---

## 4. Snapshot read path

The shared walk logic lives in [`Transactions/internals/RevisionWalker.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Transactions/internals/RevisionWalker.cs) — `RevisionChainReader.WalkChain`. Conceptually:

```
input:  compRevFirstChunkId, transactionTSN
output: RevisionReadStatus + CompRevInfo (for Success/Deleted)

if chain has exactly one entry  AND  the entry is committed  AND  TSN ≤ snapshot:
    return Success (or Deleted if ComponentChunkId == 0)        ← fast path

else:
    walk chain in storage order (using RevisionEnumerator under shared lock):
        skip if IsVoid
        if IsolationFlag set            → invisible (uncommitted by someone else)
        if TSN > transactionTSN         → invisible (future commit)
        otherwise                       → candidate; remember as current visible
    after walk:
        if no candidate                 → SnapshotInvisible
        if candidate.ComponentChunkId == 0 → Deleted (tombstone)
        otherwise                       → Success
```

A few details that matter:

- **TSN order is not guaranteed.** Cleanup compaction and the circular-buffer layout mean entries are *not* monotonically TSN-sorted. The walker can't break early on `TSN > snapshot` — it has to scan the whole live portion. That's why the chain is kept short: ~3 entries in steady state, more only while concurrent writers are active.
- **`IsolationFlag` interaction.** An entry with `IsolationFlag = true` is treated as invisible regardless of its TSN. This is what makes a writer's own uncommitted writes invisible to other readers, even if the writer's TSN happens to be ≤ another reader's snapshot TSN. The same-UoW reader sees its own writes through `CompRevInfo.CurCompContentChunkId` cached on the `ComponentInfo` (the "single cache" path in `Transaction.ECS.cs`) — not by walking the chain.
- **Tombstone vs invisible.** A `Deleted` result still returns a `CompRevInfo` payload, because callers (`UpdateComponent`, the conflict detector) need to know the metadata of the entity even when the latest visible revision is a delete.
- **`readCommitSequence`** is read from the chain header *under the same shared lock* and adjusted to a snapshot-isolated revision number: `CS − totalCommitted + visibleOrdinal`. This is how `EcsView` and `ViewDelta` ([09-querying](09-querying.md)) report stable per-snapshot revision numbers even after cleanup compacts the chain.

### Fast path

When `skipTimeout=true` (the `PointInTimeAccessor` path — no concurrent writers expected), the walker first checks for the single-entry case and short-circuits without lock acquisition, `WaitContext` construction, or `Stopwatch.GetTimestamp` overhead. This is the dominant case for read-heavy `QuerySystem` workloads where >99% of entities have one revision.

<a href="assets/typhon-data-mvcc-read.svg">
  <img src="assets/typhon-data-mvcc-read.svg" width="1200" alt="MVCC read path">
</a>
<br>
<sub>The Versioned read path: <code>Open(id)</code> resolves the archetype via <code>ArchetypeRegistry</code>, looks up the per-archetype <code>EntityMap</code> for the <code>EntityRecord</code> (which carries <code>compRevFirstChunkId</code> per slot plus <code>BornTSN</code>/<code>DiedTSN</code> visibility), then <code>RevisionChainReader.WalkChain</code> finds the latest revision visible at the snapshot TSN. Single committed-entry chains short-circuit without locking.</sub>

---

## 5. Snapshot write path

Writes flow through the ECS mutation API (`OpenMut → Write<T>`, `Spawn`, `Destroy`) on a `Transaction`. The actual revision-layer mutation is `ComponentRevisionManager.AddCompRev`:

1. **Lock the chain header** in exclusive mode (`AccessControlSmall.EnterExclusiveAccess`, deadline from `TimeoutOptions.Current.RevisionChainLockTimeout`) — unless the caller already holds it (the `lockAlreadyHeld` parameter, used during conflict resolution).
2. **Grow the chain** if the current chunk is full (`ItemCount == ComputeRevElementCount(ChainLength)`).
3. **Locate the slot** for the new entry in the current tail chunk.
4. **Allocate the content chunk** (`info.CompContentSegment.AllocateChunk`) — unless this is a `Destroy` (tombstone: `ComponentChunkId = 0`).
5. **Stamp the element:** `TSN = txTSN`, `IsolationFlag = true`, `UowId = txUowId`, `ComponentChunkId = newContentChunkId`.
6. **Update `CompRevInfo`** in the transaction's per-component cache: `CurRevisionIndex`, `CurCompContentChunkId`, with the previous values rotated into `Prev*` slots.
7. **Increment `ItemCount`** in the chain header.
8. **Release the chain lock.**

After this, the writing transaction can see its own change (it caches `CurCompContentChunkId` and reads it directly on subsequent `Read<T>` calls on the same `OpenMut` accessor). Other readers still see the *previous* version: their chain walks will reach the new entry, see `IsolationFlag = true`, and skip it.

### Isolation invariants

- An uncommitted entry (`IsolationFlag = true`) is **invisible to every reader except its own writer**. The writer doesn't walk the chain to see its own writes — it uses the cached `CurCompContentChunkId` directly.
- Cleanup **must not touch a chunk referenced by an `IsolationFlag = true` element**: that chunk's data still belongs to the writing transaction. The skip-phase logic in `CleanUpUnusedEntriesCore` explicitly tests `!enumerator.Current.IsolationFlag` before considering an entry for compaction (line 256).
- At commit, the per-component commit step (`Transaction.CommitComponentCore`) acquires the chain lock if a conflict handler is provided, runs `DetectAndResolveConflict`, possibly calls `RelocateRevisionEntry` to push the entry past `LastCommitRevisionIndex`, then **clears `IsolationFlag`** on the entry and updates `LastCommitRevisionIndex` on the header. The element is now visible to any reader with TSN ≥ this revision's TSN.

<a href="assets/typhon-data-mvcc-write.svg">
  <img src="assets/typhon-data-mvcc-write.svg" width="1200" alt="MVCC write path">
</a>
<br>
<sub>The Versioned write path: <code>OpenMut(id).Write&lt;T&gt;(comp)</code> buffers in the ChangeSet; at commit the engine allocates a content chunk, appends a revision element via <code>AddCompRev</code> stamping <code>(TSN, UowId)</code> with IsolationFlag set, updates indexes, serializes to the WAL ring, and (Immediate mode) blocks on <code>WaitForDurable</code>.</sub>

### Snapshot isolation, top-down

<a href="assets/typhon-data-snapshot-isolation.svg">
  <img src="assets/typhon-data-snapshot-isolation.svg" width="728" alt="Snapshot isolation">
</a>
<br>
<sub>Conceptual view of MVCC snapshot isolation: each transaction holds a fixed TSN, sees revisions with TSN ≤ snapshot, ignores those with <code>IsolationFlag = true</code>. The interplay between TSN comparison (chronology) and IsolationFlag (committedness) is what gives Typhon repeatable reads without read locks.</sub>

---

## 6. EnabledBits history

`EnabledBits` is a 16-bit per-entity mask that tracks which components on the entity are *logically enabled* — a disabled component is present in storage but excluded from queries, query stats, and view system deltas. It's the cheap component-toggle mechanism used by ECS without re-archetyping. See [06-ecs §6](06-ecs.md).

`EnabledBits` lives on `EntityRecord` inline (the `EntityMap` row). For MVCC correctness, however, *changes* to `EnabledBits` must be hidden from snapshots that started before the change, the same way revision content is. The Revision layer does not carry these bits — they live alongside the entity record. The MVCC story for them is a sibling system:

### `EnabledBitsOverrides`

[`Ecs/internals/EnabledBitsOverrides.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/EnabledBitsOverrides.cs)

Engine-wide `ConcurrentDictionary<long entityKey, EnabledBitsHistory>`. **Transaction-local** in the sense that uncommitted bit changes route through here at commit time — when a commit changes `EnabledBits` and older transactions are still active, the *old* bits get recorded into the override history before the inline value is updated. The fast path is the common case: `_overrideCount == 0` → no dictionary lookup, return the inline bits directly.

```csharp
public ushort ResolveEnabledBits(long entityKey, ushort inlineBits, long txTsn)
{
    if (_overrideCount == 0) return inlineBits;        // fast path
    if (!_overrides.TryGetValue(entityKey, out var history)) return inlineBits;
    return history.ResolveAt(txTsn, inlineBits);
}
```

### `EnabledBitsHistory`

[`Ecs/internals/EnabledBitsHistory.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/EnabledBitsHistory.cs)

Per-entity sorted list of `(changeTSN, oldBits)` pairs. `ResolveAt(txTsn, currentBits)` walks newest-to-oldest: if any `changeTSN > txTsn`, return the corresponding `oldBits` — that's what the older transaction was seeing before the change. Otherwise the inline (current) bits are correct.

### Pruning

Entries are pruned when `MinTSN` advances past their `changeTSN` (no active transaction needs them anymore). The dictionary itself is removed when its history becomes empty. A high-water-mark warning fires once if `_overrideCount` exceeds 10,000 — usually a sign that a stale long-running transaction is blocking cleanup.

This is structurally separate from the revision chain because EnabledBits is so much cheaper to track (16 bits per entity) that maintaining a full chain for it would be wasteful — the override dictionary is empty almost always, populated only during the window between an `EnableComponent`/`DisableComponent` commit and the moment older transactions complete.

---

## 7. Read result model

[`Revision/public/RevisionReadStatus.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Revision/public/RevisionReadStatus.cs)

The chain walk returns a `Result<CompRevInfo, RevisionReadStatus>`. The status enum is the entire MVCC visibility contract distilled to one byte:

```csharp
public enum RevisionReadStatus : byte
{
    Success           = 0,  // Revision found and visible at this snapshot tick.
    NotFound          = 1,  // Entity has no revision chain (never created).
    SnapshotInvisible = 2,  // Revision exists but not visible at the reader's snapshot tick.
    Deleted           = 3,  // Entity was tombstoned at or before the reader's snapshot tick.
}
```

| Status | Caller interpretation |
|---|---|
| `Success` | Use `CompRevInfo.CurCompContentChunkId` to read the component data. |
| `NotFound` | The PK index doesn't have a chain head for this entity — never created. |
| `SnapshotInvisible` | The entity exists *somewhere in time*, but no revision is visible to us. Treat as "doesn't exist for this snapshot" — typically returned as "not found" to the application, with no MVCC fingerprints leaking out. |
| `Deleted` | The latest visible revision is a tombstone. `CompRevInfo` is still populated (callers like `UpdateComponent` need the metadata) but the data is gone — `ComponentChunkId == 0`. |

The `Deleted` variant is what enables **time-travel reads of deleted entities**: until cleanup reclaims the tombstone, a transaction with a TSN before the delete can still read the entity's pre-delete content (the tombstone's `Prev*` chunks live on the chain until cleanup).

---

## 8. ComponentRevisionManager

[`Revision/internals/ComponentRevisionManager.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Revision/internals/ComponentRevisionManager.cs)

The static orchestrator for everything above. Not a class you instantiate — a `ref struct` namespace for the chain primitives. The non-trivial operations:

| Operation | Purpose |
|---|---|
| `AllocCompRevStorage(info, tsn, uowId, firstChunkId, pk)` | Allocate the root chunk for an entity's revision chain. First write only. Element 0 is stamped `(tsn, uowId, IsolationFlag=true)`. |
| `AddCompRev(info, ref compRevInfo, tsn, uowId, isDelete, lockAlreadyHeld=false)` | Append a new revision to an existing chain. Allocates content chunk (or `0` for tombstone), takes the chain lock unless caller holds it. **Stamps UowId at this point.** |
| `GetRevisionElement(accessor, firstChunkId, revisionIndex)` | Return an `ElementRevisionHandle` for the element at the given index — walks overflow chunks via the chain header's `NextChunkId`. Takes a shared lock if not already held. |
| `CleanUpUnusedEntriesCore(ct, firstChunkId, nextMinTSN, ...)` | Compact the chain in place: drop entries with `TSN < nextMinTSN` (excluding active uncommitted entries with `IsolationFlag = true`), keep a *sentinel* entry as the read baseline for transactions at MinTSN, free orphaned content chunks (immediately or via `DeferredCleanupManager`). |
| `FindRevisionIndexByChunkId(accessor, firstChunkId, componentChunkId, tsn=0)` | Recovery after chain compaction. Cached `CurRevisionIndex` values can become stale after cleanup — this rescans the chain to find the entry by content chunk ID (or by TSN for delete entries). |

### `AddCompRev` contract

Called by:

- `Transaction.ECS.cs` — `OpenMut(id).Write<T>(...)` and `Destroy(id)` paths, **at the moment of mutation**.
- `Transaction.cs` — `DetectAndResolveConflict` and `RelocateRevisionEntry` (commit-time conflict resolution; these append *additional* entries to handle write-write races, not the original write).
- *(Crash recovery does **not** go through `AddCompRev`: `RecoveryApplier` in `Durability/internals/` rebuilds committed chain roots directly via `ComponentRevisionManager.AllocCompRevStorage`.)*

**Not called at commit.** The commit step (`Transaction.CommitComponentCore`) clears `IsolationFlag` on already-stamped entries and updates `LastCommitRevisionIndex` on the chain header. The TSN on the entry was set when the mutation happened; the UowId likewise. If a commit needs to *insert* additional entries (because of conflict resolution), that goes through `AddCompRev` too — but the original user-driven mutation already wrote its entry well before the commit started.

### Why the split: write-time vs commit-time

The natural-sounding alternative — "stamp UowId during a commit-time loop over all touched entities" — was rejected for several reasons:

1. **Cleanup needs the IsolationFlag at mutation time**, not at commit time. Other concurrent cleanups must be safe to run during a transaction's open window.
2. **WAL replay records the UowId per record.** Stamping at write time means replay uses the exact same `AddCompRev` contract.
3. **Conflict detection at commit reads `UowId` to identify the writer** — if commit-time stamping were the model, you'd have a chicken-and-egg ordering problem when one transaction's commit observes another's uncommitted entry.
4. **Hot path simplicity.** Commit becomes a flag flip, not a chain walk — important when a transaction may have written hundreds of components.

---

## See also

- [01-foundation](01-foundation.md) — `UnitOfWorkContext` carries the `UowId` stamped on revision elements; `EpochManager`/`EpochGuard` protect chain page reads from cache eviction
- [02-storage](02-storage.md) — `ChunkBasedSegment<PersistentStore>` backs the revision table; `ChunkAccessor` is the read primitive used by `RevisionEnumerator`
- [06-ecs](06-ecs.md) — the user-visible API on top of MVCC: `Spawn`, `Open`, `OpenMut`, `EntityRef`, storage modes (Versioned vs SingleVersion vs Transient)
- [08-transactions](08-transactions.md) — `UowId` allocation via `UowRegistry`, commit timing, the `Transaction.CommitComponentCore` path that clears `IsolationFlag`, deferred cleanup orchestration
