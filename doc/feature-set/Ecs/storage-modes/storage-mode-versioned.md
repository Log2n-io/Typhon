---
uid: feature-ecs-storage-modes-storage-mode-versioned
title: 'Versioned'
description: 'Full MVCC snapshot isolation and zero-loss durability for data that can never be lost or read torn.'
---

# Versioned
> Full MVCC snapshot isolation and zero-loss durability for data that can never be lost or read torn.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Ecs](../README.md)

## 🎯 What it solves

Some component data — inventory, currency, guild membership, progression — must never be lost on crash, must
never be visible to a concurrent transaction in a half-written state, and sometimes needs to be read "as of" a
prior point in time. `Versioned` is Typhon's default storage mode: every write is isolated until commit, every
commit is WAL-durable, and concurrent readers always see a consistent value.

## ⚙️ How it works (in brief)

A `Versioned` component keeps a per-entity revision chain alongside its data. `EntityRef.Write<T>()` is
copy-on-write — it allocates a new revision and copies forward rather than overwriting in place, so concurrent
readers keep seeing the prior committed value until your transaction commits. Every transaction is WAL-logged;
on commit the new revision becomes the visible HEAD and is durable. Reads walk the chain under transaction-
sequence-number visibility, giving each transaction a consistent snapshot of the data.

## 💻 Usage

```csharp
[Component("Game.Inventory", 1)]   // StorageMode.Versioned is the default — no need to set it explicitly
public struct Inventory
{
    [Index] public int ItemId;
    public int Quantity;
}

[Archetype(10)]
partial class Player : Archetype<Player>
{
    public static readonly Comp<Inventory> Inventory = Register<Inventory>();
}

using var tx = dbe.CreateQuickTransaction();
var id = tx.Spawn<Player>(Player.Inventory.Set(new Inventory { ItemId = 7, Quantity = 1 }));
tx.Commit();

using var tx2 = dbe.CreateQuickTransaction();
var e = tx2.OpenMut(id);
ref var inv = ref e.Write(Player.Inventory);
inv.Quantity += 1;            // copy-on-write — old revision still visible to concurrent readers
tx2.Commit();                 // new revision becomes HEAD, durable
```

## ⚠️ Guarantees & limits

- Zero data loss on crash — every committed write is WAL-durable and recoverable to the exact pre-crash state.
- Full snapshot isolation: concurrent transactions never see another transaction's uncommitted writes;
  conflicting writes are detected at commit.
- `tx.Rollback()` discards the staged revision entirely — the prior committed value is untouched.
- The most expensive mode: ~250 ns/write versus ~40 ns for `SingleVersion`/`Transient` — ~6× (copy-on-write
  allocation, revision-chain append, eventual chain GC).
- Carries per-entity chain memory overhead even when a component is rarely written.
- Required for `ReadsSnapshot` / temporal "AS OF" reads and TAIL (committed-value) indexes — these guarantees
  are `Versioned`-only; `SingleVersion` and `Transient` reject them.

## 🧪 Tests

- [EcsSpawnMvccTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EcsSpawnMvccTests.cs) — copy-on-write on `Write`, concurrent transaction still seeing old data, rollback freeing the new chunk, revision-chain creation

## 🔗 Related

- Sibling: [Committed Durability Discipline](./storage-mode-committed.md) — gets `Versioned`-grade commit atomicity on the cheaper `SingleVersion` layout, for writes that don't need snapshot isolation
- Sibling: [Revision](../../Revision/README.md) — the MVCC revision-chain subsystem `Versioned` writes append into
- Parent feature: [Storage Modes](./README.md)

<!-- Deep dive: claude/design/Ecs/06-storage-modes.md, claude/overview/04-data.md §4.6 Revision Chains / MVCC Deep Dive -->
