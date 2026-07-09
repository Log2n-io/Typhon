---
uid: feature-transactions-unit-of-work
title: 'Unit of Work (durability boundary)'
description: 'Batches one or more Transactions under a single flush/durability boundary.'
---

# Unit of Work (durability boundary)
> Batches one or more Transactions under a single flush/durability boundary.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Transactions](./README.md)

## 🎯 What it solves

A single flat `CreateTransaction()` call cannot express "how many writes should share one fsync." Some workloads
want thousands of writes behind one flush (a game tick), others want every write durable before it returns (a
financial trade) — and that choice is a property of the *batch*, not of any one write. The Unit of Work is the
object that owns this choice: it is the durability boundary, separate from the Transaction's atomicity/isolation
boundary, so callers pick batching granularity once per UoW instead of accepting a one-size-fits-all default per
write.

## ⚙️ How it works (in brief)

`DatabaseEngine.CreateUnitOfWork(mode, timeout)` allocates a UoW: a `UowId` (stamped on every revision created
within it, used for crash recovery), an absolute deadline, and — for `Deferred`/`GroupCommit` — a `ChangeSet` shared
by every `Transaction` the UoW creates. `uow.CreateTransaction()` draws transactions from this scope; each commits
independently (its own atomicity/isolation), but none of them control *when* their WAL records become crash-safe —
that is `Flush()`/`FlushAsync()`'s job, driven by the UoW's `DurabilityMode`. `Dispose()` always releases the
`UowId` back to the registry; for `GroupCommit`/`Immediate` it also flushes for durability, while `Deferred` leaves
that decision to the caller.

## 💻 Usage

```csharp
// One UoW batches many transactions; caller controls the flush.
using var uow = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit, timeout: TimeSpan.FromSeconds(5));

foreach (var move in pendingMoves)
{
    using var tx = uow.CreateTransaction();
    tx.Spawn<Unit>(Unit.Pos.Set(move.Position));
    tx.Commit();                 // ~1-2µs, visible immediately
}

await uow.FlushAsync();          // waits for this UoW's records to reach stable media
Console.WriteLine(uow.CommittedTransactionCount);
```

| Option | Default | Effect |
|---|---|---|
| `durabilityMode` | `DurabilityMode.Deferred` | See [Durability Modes](./durability-modes/README.md) — controls when `Flush`/`Dispose` make WAL records crash-safe. |
| `timeout` | `TimeoutOptions.Current.DefaultUowTimeout` | Absolute deadline for everything created under this UoW (transactions, locks, flush waits). |

## ⚠️ Guarantees & limits

- **Flat, not nestable** — there is no API to open a UoW "inside" another UoW's scope; doing so just creates a
  second, independent UoW with its own `UowId` and deadline.
- **One shared `ChangeSet` per UoW** (`Deferred`/`GroupCommit`) — every transaction's dirty pages funnel through it,
  so the checkpoint (not a per-UoW write) is what eventually persists data pages; `Immediate` gives each transaction
  its own.
- **`Deferred` dispose does not flush** — WAL records committed under a `Deferred` UoW stay volatile until an
  explicit `Flush()`/`FlushAsync()` (or the WAL buffer's own back-pressure forces an earlier write); `GroupCommit`
  and `Immediate` flush automatically on `Dispose()`.
- **Bounded registry slots** — `UowId` is allocated from a fixed-size registry; under sustained pressure
  `CreateUnitOfWork` blocks until a slot frees and throws `ResourceExhaustedException` if its deadline expires first.
- **Cross-UoW concurrency is cheap** — independent UoWs on different threads contend only if they touch the same
  page or the same B+Tree index node; otherwise they run fully in parallel.
- **Counters are observational only** — `TransactionCount`/`CommittedTransactionCount` track activity for
  diagnostics; they do not gate `Flush()` or `Dispose()`.

## 🧪 Tests

- [UnitOfWorkTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Execution/UnitOfWorkTests.cs) — lifecycle (`Create`/`Dispose`
  state transitions), shared UoW identity across transactions, durability-mode preservation, registry-backed
  `UowId` allocation, empty-UoW `Flush`/`FlushAsync`

## 🔗 Related

- Related features: [Transaction Creation Patterns](./transaction-creation-patterns/README.md) (`CreateQuickTransaction`/`CreateReadOnlyTransaction`), [Transaction Lifecycle, Thread Affinity & Pooling](./transaction-lifecycle-pooling.md), [Durability Modes](./durability-modes/README.md), [Deadline & Cooperative Cancellation](./deadline-cancellation.md)

<!-- Deep dive: claude/design/Transactions/transaction-overview.md, claude/design/Transactions/05-unit-of-work.md -->
<!-- Overview: claude/overview/02-execution.md §2.1 Unit of Work (#21-unit-of-work) -->
<!-- ADR: 001 — Three-Tier API Hierarchy — claude/adr/001-three-tier-api-hierarchy.md, 005 — Durability Mode Per UoW — claude/adr/005-durability-mode-per-uow.md -->
