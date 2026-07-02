# Standard Transaction Creation (UnitOfWork + CreateTransaction)
> Open a `UnitOfWork` once, draw as many transactions from it as the batch needs.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Transactions](../README.md)

## 🎯 What it solves

Batched workloads — a game tick, a request handler processing several operations, a bulk import — need many
transactions to share one durability decision: how many writes ride behind a single WAL flush. Creating a fresh
durability boundary per transaction would mean a fresh `UowId` allocation and a separate flush decision per write,
defeating the point of batching. The standard pattern gives the caller one long-lived `UnitOfWork` and lets it mint
transactions on demand, each independently atomic but sharing the same flush policy.

## ⚙️ How it works (in brief)

`dbe.CreateUnitOfWork(mode, timeout)` allocates the `UnitOfWork` — a `UowId` from the registry, an absolute
deadline, and (for `Deferred`/`GroupCommit`) a shared `ChangeSet`. The caller then calls `uow.CreateTransaction()`
as many times as needed; each call pulls a `Transaction` from the pooled `TransactionChain`, stamps it with the
UoW's identity, and returns it ready for use. Each transaction still commits or rolls back independently — the UoW
only controls *when* the WAL records those commits produced become crash-safe, via `Flush()`/`FlushAsync()` or
automatically on `Dispose()` (mode-dependent).

## 💻 Usage

```csharp
[Component("Game.Position", 1, StorageMode = StorageMode.SingleVersion)]
struct Position { public float X, Y, Z; }

[Archetype(42)]
partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Pos = Register<Position>();
}

using var uow = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit, timeout: TimeSpan.FromSeconds(5));

foreach (var move in pendingMoves)
{
    using var tx = uow.CreateTransaction();
    tx.Spawn<Unit>(Unit.Pos.Set(move.Position));
    tx.Commit();                  // ~1-2µs, visible immediately, durable within the group-commit window
}

await uow.FlushAsync();           // wait for this batch's records to reach stable media
Console.WriteLine(uow.CommittedTransactionCount);
```

| Option | Default | Effect |
|---|---|---|
| `durabilityMode` | `DurabilityMode.Deferred` | See [Durability Modes](../durability-modes/README.md) — when this UoW's WAL records become crash-safe. |
| `timeout` | `TimeoutOptions.Current.DefaultUowTimeout` | Absolute deadline shared by every transaction, lock, and flush wait created under this UoW. |
| `discipline` (on `CreateTransaction`) | `DurabilityDiscipline.TickFence` | Per-transaction `SingleVersion` write discipline — see [Durability Discipline](../durability-discipline/README.md). |

## ⚠️ Guarantees & limits

- One `UnitOfWork` can back any number of sequential or concurrent-on-different-thread transactions; there is no
  cap on `CreateTransaction()` calls beyond the engine-wide active-transaction limit.
- The caller owns the `UnitOfWork`'s lifetime explicitly — it is not disposed automatically by any transaction it
  creates, unlike `CreateQuickTransaction`.
- UoWs are flat, not nestable — `CreateUnitOfWork()` called while another is "logically" in scope just allocates a
  second, independent UoW with its own `UowId` and deadline.
- `TransactionCount`/`CommittedTransactionCount` on the `UnitOfWork` are observational counters; they do not gate
  `Flush()` or `Dispose()`.
- `CreateUnitOfWork` blocks (and can throw `ResourceExhaustedException`) if the UoW Registry is full and the
  deadline expires before a slot frees — the standard pattern is the only one of the three that can hit this,
  since it is the only one that allocates a registry slot the caller controls the lifetime of.

## 🧪 Tests

- [UnitOfWorkTests](../../../../test/Typhon.Engine.Tests/Execution/UnitOfWorkTests.cs) —
  `UoW_CreateTransaction_ReturnsValidTx`, `UoW_MultipleTransactions_ShareIdentity` (shared `OwningUnitOfWork`
  identity), `MultipleUoWs_ConcurrentAccess` (independent UoWs read/write concurrently)

## 🔗 Related

- Code: `src/Typhon.Engine/Transactions/public/UnitOfWork.cs`, `src/Typhon.Engine/Ecs/public/DatabaseEngine.cs`
  (`CreateUnitOfWork`)
- Related features: [Unit of Work](../unit-of-work.md), [Durability Modes](../durability-modes/README.md)
- Parent feature: [Transaction Creation Patterns](./README.md)

<!-- Deep dive: claude/design/Transactions/transaction-overview.md §2 UnitOfWork — public surface (#2-unitofwork--public-surface), claude/design/Transactions/05-unit-of-work.md -->
