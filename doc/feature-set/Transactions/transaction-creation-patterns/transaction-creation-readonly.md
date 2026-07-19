---
uid: feature-transactions-transaction-creation-patterns-transaction-creation-readonly
title: 'CreateReadOnlyTransaction (snapshot reads)'
description: 'A Transaction with no UnitOfWork behind it at all — for callers that never write.'
---

# CreateReadOnlyTransaction (snapshot reads)
> A `Transaction` with no `UnitOfWork` behind it at all — for callers that never write.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Transactions](../README.md)

## 🎯 What it solves

A pure read — query dispatch, a REPL inspection command, a background reporting pass — never produces a WAL record,
so allocating a `UnitOfWork` for it (a durability mode, a scarce UoW Registry slot, a `ChangeSet`) is pure overhead
with no corresponding benefit. Worse, that slot is shared, bounded capacity that a concurrent write workload could
otherwise use. `CreateReadOnlyTransaction` is the entry point for "I only need to read at a consistent snapshot" —
it skips the durability tier entirely while keeping full MVCC read semantics.

## ⚙️ How it works (in brief)

`dbe.CreateReadOnlyTransaction()` draws a `Transaction` from the same pool as every other transaction and gives it
a TSN and a `TransactionChain` slot — it is fully visible to and governed by MVCC snapshot isolation exactly like a
write transaction — but no `UnitOfWork` is created and `IsReadOnly` is set, which also skips `ChangeSet`
allocation. Any write entry point on the returned transaction throws immediately rather than silently no-opping;
`Commit()` is a no-op that returns `true`, since there is nothing to persist.

## 💻 Usage

```csharp
[Component("Game.Position", 1, StorageMode = StorageMode.SingleVersion)]
struct Position { public float X, Y, Z; }

[Archetype]
partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Pos = Register<Position>();
}

using var rtx = dbe.CreateReadOnlyTransaction();

if (rtx.TryOpen(unitId, out EntityRef e))
{
    ref readonly Position pos = ref e.Read(Unit.Pos);
    Console.WriteLine($"{pos.X}, {pos.Y}, {pos.Z}");
}

rtx.Commit();   // no-op, returns true — there is nothing buffered to flush
```

## ⚠️ Guarantees & limits

- No `UnitOfWork`, no `UowId`, no `ChangeSet` — a read-only transaction never consumes a UoW Registry slot and
  never contributes to registry back-pressure.
- Every write entry point (`Spawn`, `Destroy`, component `Write`, collection mutation) throws
  `InvalidOperationException` immediately, before touching any state — there is no silent no-op fallback.
- Still occupies a `TransactionChain` slot and TSN — a long-lived read-only transaction holds back `MinTSN` exactly
  like a write transaction, delaying MVCC revision cleanup. Don't hold one open longer than the read needs.
- Only `DatabaseEngine.CreateReadOnlyTransaction()` creates one — there is no `UnitOfWork.CreateReadOnlyTransaction()`
  overload, because by definition this pattern bypasses the UoW tier.
- Full read surface, identical to a write transaction — `Open`/`TryOpen`, `Read`, index enumeration, and queries
  all behave the same under the same snapshot-isolation guarantee.

## 🧪 Tests

- [TransactionChainTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/TransactionChainTests.cs) —
  `ReadOnly_CanRead_ThrowsOnWrite` (read works, every write entry point throws), `ReadOnly_Dispose_ActiveCountZero`,
  `ReadOnly_SnapshotIsolation` (doesn't see later commits), `ReadOnly_PoolRecycling_ClearsFlag` (pool reuse across
  read-only/read-write)

## 🔗 Related

- Code: `src/Typhon.Engine/Transactions/public/DatabaseEngineExtensions.cs` (`CreateReadOnlyTransaction`)
- Parent feature: [Transaction Creation Patterns](./README.md)
- Sibling: [Standard (UnitOfWork + CreateTransaction)](./transaction-creation-standard.md) — the write-capable
  counterpart that does allocate a `UnitOfWork`

<!-- Deep dive: claude/design/Transactions/transaction-overview.md §1 Three-Tier API Hierarchy (#1-three-tier-api-hierarchy) -->
<!-- ADR: 038 — Transaction Throws on Invalid State — claude/adr/038-transaction-throw-on-invalid-state.md -->
