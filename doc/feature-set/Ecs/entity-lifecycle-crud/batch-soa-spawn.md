---
uid: feature-ecs-entity-lifecycle-crud-batch-soa-spawn
title: 'Batch & SoA Spawn'
description: 'Bulk entity creation — shared-value batches or per-entity SoA spans — amortizing per-call overhead across thousands of entities.'
---

# Batch & SoA Spawn
> Bulk entity creation — shared-value batches or per-entity SoA spans — amortizing per-call overhead across thousands of entities.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Ecs](../README.md)

## 🎯 What it solves

Spawning entities one at a time pays per-call overhead (an `Interlocked.Increment` for the entity key, an
`EnsureMutable` check, a chunk allocation per component) on every iteration of the loop — too slow for bulk
world-load or stress-test scale (tens of thousands of entities at once). Batch & SoA Spawn amortizes that
overhead: one key-range reservation and one mutability check for the whole batch, and — for workloads that
supply distinct per-entity data — one slot/table/accessor resolution shared across every entity instead of one
per entity.

## ⚙️ How it works (in brief)

`SpawnBatch<TArch>` reserves N entity keys in a single atomic `Interlocked.Add` and applies the same shared
`ComponentValue`s to every entity in the batch. For per-entity data supplied as parallel SoA spans, the source
generator emits a `SpawnBatch(tx, span1, span2, ...)` overload on `partial` archetype classes, built from two
lower-level `Transaction` primitives: `SpawnBatchAllocate<TArch>` allocates N entities with every component
chunk pre-allocated (`EnabledBits = 0`), and `SpawnBatchWriteAll` writes one parallel `ReadOnlySpan<T>` across
the whole allocated range — resolving the slot, table, and accessor once, then looping with zero dictionary
lookups per entity and setting that component's enabled bit as it writes.

## 💻 Usage

```csharp
// ─── Shared values — every spawned entity gets the same component data ───
Span<EntityId> ids = stackalloc EntityId[100];
tx.SpawnBatch<Unit>(ids, Unit.Pos.Set(new Position { X = 0, Y = 0, Z = 0 }));

// ─── Per-entity SoA — source-generated overload (archetype must be 'partial') ───
ReadOnlySpan<Position> positions = ...;   // one value per entity, same order as velocities
ReadOnlySpan<Velocity> velocities = ...;
EntityId[] ids2 = Unit.SpawnBatch(tx, positions, velocities);

// ─── Hand-rolled SoA — what the generator emits, for custom hot paths ───
Span<EntityId> ids3 = new EntityId[positions.Length];
int baseIndex = tx.SpawnBatchAllocate<Unit>(positions.Length, ids3);
tx.SpawnBatchWriteAll(baseIndex, positions.Length, Unit.Pos, positions);
tx.SpawnBatchWriteAll(baseIndex, positions.Length, Unit.Vel, velocities);
tx.Commit();
```

## ⚠️ Guarantees & limits

- `SpawnBatch<TArch>(Span<EntityId>, params ComponentValue[])` applies identical values to every entity in the
  batch — for distinct per-entity data, use the SoA path instead.
- The caller supplies the pre-sized `ids` output span (`stackalloc` for small batches) — there is no internal
  allocation for the id list itself.
- Storage is allocated per component actually written, mirroring single `Spawn` semantics — any component not
  covered by shared values or a `SpawnBatchWriteAll` call stays absent and disabled.
- The generated SoA `SpawnBatch` overload requires the archetype be declared `partial`; all input spans must
  have equal length (`Debug.Assert`-checked — not validated in a Release build).
- Epoch refresh happens every 128 entities, not per-entity, bounding page-cache pressure during very large
  batches.
- Spawned entities follow the same MVCC visibility rule as single `Spawn` — invisible to other transactions
  until commit (`BornTSN = commit TSN`).
- `SpawnBatchAllocate`/`SpawnBatchWriteAll` are public `Transaction` members, not generator-internal-only — they
  can be called directly for hand-written hot paths that don't fit the generated shape (e.g. non-archetype
  bulk-load tooling).

## 🧪 Tests

- [BatchOperationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/BatchOperationTests.cs) — shared-value and source-generated per-entity SoA `SpawnBatch`, archetype inheritance, `DestroyBatch` incl. cascade
- [EntitySpawnTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EntitySpawnTests.cs) — shared-value `SpawnBatch` unique-key allocation, zero-value-stays-disabled semantics, commit-then-read-in-new-tx

## 🔗 Related

- Source: `src/Typhon.Engine/Transactions/public/Transaction.ECS.cs`
- Parent feature: [Entity Lifecycle & CRUD API](./README.md)

<!-- Deep dive: claude/design/Ecs/04-crud-api.md §Batch Spawn / §Per-Component SoA Spawn -->
