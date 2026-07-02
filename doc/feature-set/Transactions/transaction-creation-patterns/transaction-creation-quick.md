# CreateQuickTransaction (single-shot, auto-dispose)
> One call gives you a `UnitOfWork` and a `Transaction` fused into a single disposable.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Transactions](../README.md)

## 🎯 What it solves

Single-shot writes — a REPL command, a test fixture, a one-off background task — don't need a `UnitOfWork` the
caller manages separately from its one `Transaction`; that's two objects and two `using` statements to express what
is conceptually one operation. Without this convenience, every single-write call site would repeat the same
two-line `CreateUnitOfWork` + `CreateTransaction` boilerplate and would need to remember to dispose both in the
right order.

## ⚙️ How it works (in brief)

`dbe.CreateQuickTransaction(mode, discipline)` allocates a `UnitOfWork` exactly as the standard path does, then
immediately creates one `Transaction` from it and marks that transaction as owning the UoW
(`Transaction.OwnsUnitOfWork = true`). When the returned `Transaction` is disposed, its own `Dispose()` also
disposes the backing `UnitOfWork` — so a single `using var tx = ...` is enough to get correct cleanup of both
tiers, in the right order, even if the transaction is never explicitly committed.

## 💻 Usage

```csharp
[Component("Game.Position", 1, StorageMode = StorageMode.SingleVersion)]
struct Position { public float X, Y, Z; }

[Archetype(42)]
partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Pos = Register<Position>();
}

// Single-shot write — UoW and Transaction are created and disposed together.
using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
tx.Spawn<Unit>(Unit.Pos.Set(new Position { X = 1, Y = 0, Z = 0 }));
tx.Commit();                      // tx.Dispose() also disposes its owning UoW

// Forgetting to commit is safe — Dispose() auto-rolls-back the transaction
// and still disposes (and, for non-Deferred modes, flushes) the owned UoW.
using (var tx2 = dbe.CreateQuickTransaction())
{
    tx2.Spawn<Unit>(Unit.Pos.Set(new Position { X = 2 }));
    // no tx2.Commit() here — rolled back automatically on Dispose
}
```

| Parameter | Default | Effect |
|---|---|---|
| `durabilityMode` | `DurabilityMode.Deferred` | Durability mode of the hidden `UnitOfWork` — see [Durability Modes](../durability-modes/README.md). |
| `discipline` | `DurabilityDiscipline.TickFence` | `SingleVersion` write discipline for the one transaction — see [Durability Discipline](../durability-discipline/README.md). |

## ⚠️ Guarantees & limits

- Exactly one `Transaction` is created per call; there is no way to draw a second transaction from the hidden
  `UnitOfWork` — if you need more than one, use the [standard pattern](./transaction-creation-standard.md) instead.
- Disposal order is fixed: the `Transaction`'s own cleanup (auto-rollback if uncommitted, deferred-cleanup
  processing, epoch exit) runs before the owned `UnitOfWork` is disposed.
- `DurabilityMode.Deferred` (the default) means `Dispose()` does **not** flush the owned UoW — the same rule as the
  standard pattern applies; pick `GroupCommit` or `Immediate` if the single write must be durable before the
  `using` block exits.
- This is a convenience wrapper, not a distinct code path — the resulting `Transaction` and `UnitOfWork` behave
  identically to ones created through the standard pattern once constructed.

## 🧪 Tests

- [UnitOfWorkTests](../../../../test/Typhon.Engine.Tests/Execution/UnitOfWorkTests.cs) —
  `QuickTx_CommitThenDispose_CleanLifecycle` (fused UoW+tx dispose ordering), `QuickTx_DisposesUoW`,
  `QuickTx_DurabilityMode_Passthrough` (mode flows through to the hidden `UnitOfWork`)

## 🔗 Related

- Code: `src/Typhon.Engine/Transactions/public/DatabaseEngineExtensions.cs` (`CreateQuickTransaction`)
- Related features: [Standard creation](./transaction-creation-standard.md), [Unit of Work](../unit-of-work.md)
- Parent feature: [Transaction Creation Patterns](./README.md)

<!-- Deep dive: claude/design/Transactions/transaction-overview.md §1 Three-Tier API Hierarchy (#1-three-tier-api-hierarchy) -->
