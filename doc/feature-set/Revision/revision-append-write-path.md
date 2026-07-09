---
uid: feature-revision-revision-append-write-path
title: 'Revision Append & Chain Growth'
description: 'Every write to a Versioned component creates a new immutable revision instead of overwriting the old one.'
---

# Revision Append & Chain Growth
> Every write to a `Versioned` component creates a new immutable revision instead of overwriting the old one.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Revision](./README.md)

## 🎯 What it solves

Snapshot isolation requires that a transaction reading a `Versioned` component keeps seeing the value as of its
own snapshot, even while other transactions concurrently write newer values. Overwriting a component in place
would tear those reads and destroy the history that conflict detection and AS-OF / point-in-time queries depend
on. Every `Spawn`, `Write<T>()`, and `Destroy` on a `Versioned` component therefore produces a brand-new,
immutable revision instead of mutating the previous one — older readers keep a valid, untouched value to read.

## ⚙️ How it works (in brief)

`Spawn` allocates an entity's first revision directly. Every subsequent `Write<T>()` is copy-on-write: it
appends a new revision to that component's per-entity chain — a small circular buffer that grows into 64-byte
overflow chunks automatically once it fills, with no size limit you need to plan for up front. `Destroy` appends
a tombstone revision (no payload) rather than deleting anything immediately. Each new revision is stamped with
the writing transaction's TSN and Unit-of-Work id and marked provisional — invisible to every other transaction
— until commit publishes it. The prior revision is left exactly as it was; nothing is freed at write time.

## 💻 Usage

```csharp
[Component("Game.Stats", 1)]                 // StorageMode.Versioned is the default
struct Stats { public int Hp; public int Mana; }

[Archetype(7)]
partial class Hero : Archetype<Hero>
{
    public static readonly Comp<Stats> Stats = Register<Stats>();
}

using var uow = dbe.CreateUnitOfWork();
using var tx = uow.CreateTransaction();

var id = tx.Spawn<Hero>(Hero.Stats.Set(new Stats { Hp = 100, Mana = 50 }));   // revision #1
tx.Commit();

// Each write below appends a new revision — concurrent snapshot readers are unaffected.
for (var i = 0; i < 5; i++)
{
    using var hitUow = dbe.CreateUnitOfWork();
    using var hitTx = hitUow.CreateTransaction();
    ref var stats = ref hitTx.OpenMut(id).Write(Hero.Stats);   // copy-on-write: new revision appended
    stats.Hp -= 10;                                            // chain grows into an overflow chunk past 3 entries
    hitTx.Commit();
}

using var deathUow = dbe.CreateUnitOfWork();
using var deathTx = deathUow.CreateTransaction();
deathTx.Destroy(id);          // appends a tombstone revision per Versioned component the entity carries
deathTx.Commit();
```

## ⚠️ Guarantees & limits

- A write never blocks or invalidates a concurrent reader: the previous revision is untouched, so any
  transaction already holding it keeps a valid value for the rest of its lifetime.
- Chain growth is fully automatic — applications never size or manage the revision chain; the first 3 revisions
  fit in the entity's root storage, every 5 beyond that grow the chain by one more chunk.
- A new revision is provisional (invisible to other transactions) until the writing transaction commits; a
  rolled-back transaction's revisions are voided, not left dangling.
- Revisions are append-only and not reclaimed by the write path itself — the chain keeps growing with every
  write until garbage collection trims revisions no longer needed by any active snapshot (see Related).
- Appending briefly takes the entity's per-component revision-chain lock; under sustained timeout (default via
  `TimeoutOptions.Current.RevisionChainLockTimeout`) the write throws `LockTimeoutException` rather than
  blocking indefinitely.
- Only `StorageMode.Versioned` components use this path — `SingleVersion` and `Transient` components write
  in place and never allocate a revision (see [Committed Durability Discipline](../Durability/durability-modes/committed-discipline.md)
  for the cheaper alternative when isolation isn't needed).

## 🧪 Tests

- [EcsSpawnMvccTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EcsSpawnMvccTests.cs) — `Spawn_Versioned_RevisionChainCreated` (chunk allocated on `Spawn`), `Write_Versioned_CopyOnWrite_NewChunk`/`Write_CreatedEntity_NoCopyOnWrite` (append vs. same-tx reuse), rollback frees the chunk (`Spawn_Versioned_Rollback_FreesRevisionChunk`, `Write_Versioned_Rollback_FreesNewChunk`)
- [TransactionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/TransactionTests.cs) — `ComponentRevisionTortureTest` drives 100 mixed commit/rollback writes against one entity and asserts the accumulated revision count, then compacts to 1 after the blocking reader completes

## 🔗 Related

- Related feature: [Revision Chain Storage](./revision-chain-storage.md) (the layout this populates), [MVCC Snapshot Visibility](./mvcc-snapshot-visibility.md), [Write-Conflict Baseline Tracking](./optimistic-conflict-baseline.md) (Prev/Cur are first set here), [Revision Garbage Collection & Compaction](./revision-gc-compaction.md)
- Source: [`ComponentRevisionManager.AddCompRev`/`AllocCompRevStorage`/`GrowChain`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Revision/internals/ComponentRevisionManager.cs), [`Transaction.Spawn`/`Transaction.Destroy`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Transactions/public/Transaction.ECS.cs), [`EntityRef.Write`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/EntityRef.cs)

<!-- Deep dive: claude/design/Revision/01-revision-chain-storage.md, claude/design/Revision/README.md -->
<!-- ADR: claude/adr/003-mvcc-snapshot-isolation.md -->
<!-- Overview: claude/overview/04-data.md §4.5–4.6 -->
