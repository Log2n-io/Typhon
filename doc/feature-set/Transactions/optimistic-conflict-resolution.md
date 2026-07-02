# Optimistic Concurrency Conflict Resolution
> Plug a handler into `Commit` to reconcile write-write conflicts per entity, instead of accepting silent last-writer-wins.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Transactions](./README.md)

## 🎯 What it solves

Typhon transactions take no locks during execution (ADR-003) — two transactions can read the same entity and both
intend to write it, and by default the second one to commit simply overwrites the first. That's fine for most
state, but some writes are not safe to clobber blindly: counters, deltas, accumulators, or anything where "last
writer wins" silently discards real work. Without a way to inspect what changed underneath a transaction before
its write lands, the only options are accept data loss or build retry/merge logic entirely outside the engine.

## ⚙️ How it works (in brief)

Pass a `ConcurrencyConflictHandler` to `Commit`. While committing, each entity is checked for a conflict (another
transaction committed a newer value since this transaction's read); detection uses a monotonic per-entity commit
counter rather than chain position, so background revision-chain compaction can't produce a false negative. When a
conflict fires, a `ConcurrencyConflictSolver` is populated with the entity's read/committed/committing state and the
handler is invoked once for that entity, under the entity's revision-chain lock — detection and resolution are one
atomic step, no other writer can interleave. The handler resolves the conflict by writing the value it wants
committed; if it writes nothing, the pre-seeded default (the committing transaction's own value) is what commits.

## 💻 Usage

```csharp
void RebaseDelta(ref ConcurrencyConflictSolver solver)
{
    var delta = solver.CommittingData<Gold>().Amount - solver.ReadData<Gold>().Amount;
    solver.ToCommitData<Gold>().Amount = solver.CommittedData<Gold>().Amount + delta;
}

using var t = dbe.CreateQuickTransaction();
t.Open(playerId).Read(GoldArch.Gold);
ref var w = ref t.OpenMut(playerId).Write(GoldArch.Gold);
w.Amount -= 10;                 // spend 10 gold

t.Commit(RebaseDelta);          // conflicting writers rebase instead of clobbering each other
```

| Helper | Effect |
|---|---|
| `solver.TakeRead<T>()` | Discard the change, revert to the read snapshot |
| `solver.TakeCommitted<T>()` | Accept the other transaction's committed value |
| `solver.TakeCommitting<T>()` | Keep this transaction's value (also the default with no handler) |
| `solver.ToCommitData<T>() = ...` | Write a custom resolution (rebase, merge, clamp, …) |

## ⚠️ Guarantees & limits

- **No handler = last writer wins** — the default `Commit()` overload never inspects conflicts; the committing
  value always replaces the prior one.
- **One call per conflicting entity**, not per field — a multi-component commit can invoke the handler several
  times in one `Commit()`, once per entity that actually conflicted.
- **Runs under the revision-chain lock** — detection, handler execution, and publish are atomic; keep handlers
  fast and allocation-light (no I/O, no calling back into the engine, no blocking waits).
- **`ConcurrencyConflictSolver` is a reused, thread-local instance** — valid only for the duration of the handler
  call; don't store a reference to it or its data pointers beyond that call.
- **No abort path** — the handler can't reject the conflict and fail the commit; it can only choose what value
  commits. Application-level "give up and roll back" logic has to be expressed by writing back the read value
  (`TakeRead`) and checking for that outcome afterward, if that distinction matters.
- **Versioned components only** — applies where a revision chain exists (`StorageMode.Versioned`); `Committed`/
  `SingleVersion` writes have no prior revision to compare against.

## 🧪 Tests

- [ConcurrencyConflictTests](../../../test/Typhon.Engine.Tests/Data/ConcurrencyConflictTests.cs) — no-handler
  last-writer-wins, delta-rebase and `TakeCommitted`/`TakeRead` resolutions, per-entity handler invocation on
  multi-entity commits, concurrent-thread rebase race, `ConcurrencyConflictSolver` thread-local reuse

## 🔗 Related

- Related feature: [Write-Conflict Baseline Tracking](../Revision/optimistic-conflict-baseline.md) (the underlying per-entity baseline/CommitSequence mechanism), [Unit of Work](./unit-of-work.md)
- Source: [`ConcurrencyConflictSolver`](../../../src/Typhon.Engine/Transactions/public/ConcurrencyConflictSolver.cs), [`Transaction.DetectAndResolveConflict`/`Commit`](../../../src/Typhon.Engine/Transactions/public/Transaction.cs), [`CommitContext`](../../../src/Typhon.Engine/Transactions/internals/CommitContext.cs)

<!-- Deep dive: claude/design/Transactions/transaction-overview.md §4.2 (#42-conflict-detection--two-paths) -->
<!-- ADR: 003 — MVCC Snapshot Isolation — claude/adr/003-mvcc-snapshot-isolation.md -->
