# Transaction Lifecycle, Thread Affinity & Pooling
> The internals that make per-tick transaction churn cheap and misuse fail fast instead of corrupting state.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Transactions](./README.md)

## 🎯 What it solves

A game tick or request handler can create and dispose hundreds of `Transaction` objects per second; if each one were
a fresh heap allocation guarded by a global lock, that churn alone would dominate the tick budget. At the same time,
a `Transaction` is not thread-safe internally — using one from two threads, or calling CRUD on one that already
committed, are both programmer errors that must surface immediately and loudly, not corrupt a chunk or silently
no-op. And because every transaction's lifetime overlaps with MVCC snapshot isolation, *something* has to track which
transaction is the oldest still active, so revision-chain garbage collection knows how far back it's safe to reclaim.
This feature is the machinery behind `Transaction` that resolves all three: cheap reuse, fail-fast misuse detection,
and the bookkeeping that drives MVCC GC.

## ⚙️ How it works (in brief)

Every `Transaction` instance is created on one thread and asserts (`[Conditional("DEBUG")]`) that every subsequent
call comes from that same thread — `AssertThreadAffinity()` guards every public mutating method. A small state
machine (`Created → InProgress → Committed`/`Rollbacked`) backs every transaction; `EnsureMutable()` throws
`InvalidOperationException` if CRUD is attempted on an already-finished transaction, and `TransitionTo()` asserts
(Debug-only) that only legal transitions occur. Underneath, `TransactionChain` hands out and reclaims `Transaction`
instances: `CreateTransaction` allocates a monotonic TSN (`Interlocked.Increment`) and pushes the new transaction
onto a singly-linked chain via a lock-free CAS loop (`PushHead`) — concurrent creators never block each other.
`Dispose()` removes the transaction under a brief exclusive lock (`Remove`), recycles it into a 16-instance pool, and
— if the removed transaction was the chain's tail (the oldest active TSN) — recomputes the new minimum TSN, which is
the horizon the deferred-cleanup GC uses to know which revisions no MVCC snapshot can still see.

## 💻 Usage

```csharp
[Component("Game.Position", 1, StorageMode = StorageMode.SingleVersion)]
struct Position { public float X, Y, Z; }

[Archetype(42)]
partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Pos = Register<Position>();
}

// Pooling is transparent: each iteration draws/returns a Transaction from TransactionChain's
// 16-instance pool — no per-tick heap allocation as long as concurrency stays at or below 16.
for (int tick = 0; tick < 10_000; tick++)
{
    using var tx = dbe.CreateQuickTransaction();
    tx.Spawn<Unit>(Unit.Pos.Set(new Position { X = tick }));
    tx.Commit();
}   // Dispose() resets the instance and returns it to the pool

// The state machine fails fast: CRUD after Commit/Rollback throws instead of silently no-opping.
using var tx2 = dbe.CreateQuickTransaction();
tx2.Spawn<Unit>(Unit.Pos.Set(new Position { X = 1 }));
tx2.Commit();                                  // tx2.State == TransactionState.Committed
try
{
    tx2.Spawn<Unit>(Unit.Pos.Set(new Position { X = 2 }));
}
catch (InvalidOperationException)
{
    // "Cannot perform CRUD on a transaction in state Committed" — programmer error, not a no-op (ADR-038)
}
```

## ⚠️ Guarantees & limits

- **Thread affinity is Debug-only** — `AssertThreadAffinity()` is `[Conditional("DEBUG")]`; it is compiled out
  entirely in Release builds, so cross-thread use of one `Transaction` is caught during development but is undefined
  behavior (not a runtime exception) in Release. One `Transaction` per thread per tick is the supported pattern.
- **Fail-fast state machine, no silent sentinels** — `Created → InProgress → Committed`/`Rollbacked` are the only
  legal transitions; CRUD on an already-finished transaction throws `InvalidOperationException` rather than returning
  `-1`/`false` (ADR-038). `Commit()`/`Rollback()` themselves are exceptions to this: calling either on an
  already-finished transaction returns `false`, and on a still-`Created` (no-op) transaction returns `true`.
- **Lock-free creation, briefly-locked removal** — `PushHead` is a CAS loop with zero blocking between concurrent
  `CreateTransaction` callers; `Remove` (triggered by `Dispose()`) takes a short exclusive lock to unlink the node,
  scanning at most `MaxActiveTransactions` nodes (~500ns worst case at 128 active transactions).
- **Pooling is a perf optimization, not a hard cap** — up to 16 `Transaction` instances are recycled via a
  `ConcurrentQueue`; beyond that, new instances are heap-allocated. The engine separately caps total *active*
  transactions (`maxActiveTransactions`); exceeding it throws `ResourceExhaustedException` rather than blocking.
- **MinTSN drives MVCC GC, not just diagnostics** — only the chain's tail (the transaction holding the oldest active
  TSN) triggers `DeferredCleanupManager.ProcessDeferredCleanups` on `Dispose()`/`Commit()`/`Rollback()`. A long-lived
  transaction at the tail stalls revision-chain reclamation engine-wide — the same hazard PostgreSQL's vacuum has
  against `xmin`.
- **TSN space is large but finite** — TSNs are assigned via `Interlocked.Increment`; the engine logs a warning as
  the practical ceiling (`1 << 46`) approaches (no enforcement failure observed at realistic throughput).
- **Known scaling limit (not yet implemented)** — `Remove`'s exclusive lock and O(n) scan remain a serialization
  point projected to matter only past ~64-128 cores. A lock-free `TransactionRegistry` redesign is drafted (see
  Related) but not built; `TransactionChain` is the shipping implementation.

## 🧪 Tests

- [TransactionChainTests](../../../test/Typhon.Engine.Tests/Data/TransactionChainTests.cs) —
  `MPSC_ConcurrentCreates_AllTSNsUnique` (lock-free `PushHead` under concurrent creation),
  `MinTSN_DisposeTail_Advances` (MVCC-GC horizon on tail dispose), `Stress_ConcurrentCreateDispose_ActiveCountZeroAtEnd`;
  the file's second fixture `TransactionChainMaxActiveTests` covers the `maxActiveTransactions` cap and
  `ResourceExhaustedException`
- [TransactionTests](../../../test/Typhon.Engine.Tests/Data/TransactionTests.cs) — `TransitionTo_IllegalTransition_DebugAssertFails`
  (state-machine legality), `CrudAfterCommitOrRollback_ThrowsInvalidOperation`, `Dispose_Idempotent_SecondCallNoOp`

## 🔗 Related

- Related features: [Unit of Work](./unit-of-work.md) (the three-tier API these internals back), [Transaction Creation Patterns](./transaction-creation-patterns/README.md), [Commit / Rollback Pipeline](./commit-rollback-pipeline.md)

<!-- Deep dive: claude/design/Transactions/lock-free-transaction-chain.md, claude/design/Transactions/transaction-registry.md (drafted, not implemented) -->
<!-- Overview: claude/overview/02-execution.md §2.11 Lock-Free TransactionChain (#211-lock-free-transactionchain), §2.1 Unit of Work — transaction pooling lifecycle (#21-unit-of-work) -->
<!-- ADR: 038 — Transaction CRUD Throws on Invalid State — claude/adr/038-transaction-throw-on-invalid-state.md -->
