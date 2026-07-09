---
uid: feature-spatial-spatial-coherent-clustering
title: 'Spatially-Coherent Entity Clustering'
description: 'Every entity in a cluster shares one grid cell, so spatial bookkeeping is per-cluster, not per-entity.'
---

# Spatially-Coherent Entity Clustering
> Every entity in a cluster shares one grid cell, so spatial bookkeeping is per-cluster, not per-entity.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Spatial](./README.md)

## 🎯 What it solves

[Entity Clusters](../Ecs/entity-clusters.md) pack many same-archetype entities into one contiguous chunk for
cache-friendly bulk iteration — but a cluster built with no spatial awareness can end up containing entities
scattered anywhere in the world. Any cell-based operation (tier assignment, dormancy, per-cell broadphase
queries) would then need a position check on every entity in every cluster to know which cell it belongs to.
Spatially-Coherent Entity Clustering constrains cluster membership so all entities in a cluster share one coarse
grid cell, collapsing that check from per-entity to per-cluster — typically 50-100x fewer checks — and keeps it
that way as entities move, without the caller doing anything extra.

## ⚙️ How it works (in brief)

Spawning an entity with a `[SpatialIndex]` field places it in a cluster already assigned to its cell, or
allocates a fresh one for that cell if none has room. As entities move, the engine detects when a new position
exits the current cell by more than a small hysteresis margin (so walking along a boundary doesn't thrash) and
queues a migration instead of moving it inline. All queued migrations for a tick are drained together at the
tick fence, after every system has run: entity data (including any Transient components), index entries, and
the spatial back-pointer are moved atomically into a cluster of the destination cell, and the per-cluster AABB
is recomputed. None of this cell/cluster bookkeeping is persisted — it's derived from entity positions and
rebuilt on every startup.

## 💻 Usage

```csharp
[Component("Game.AntPos", 1, StorageMode = StorageMode.SingleVersion)]
public struct AntPos
{
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;
}

[Archetype(50)]
public partial class Ant : Archetype<Ant>
{
    public static readonly Comp<AntPos> Pos = Register<AntPos>();
}

// Opt in once, before InitializeArchetypes (see Spatial Grid Configuration).
dbe.ConfigureSpatialGrid(new SpatialGridConfig(
    worldMin: new Vector2(0, 0), worldMax: new Vector2(1000, 1000), cellSize: 100f));
dbe.InitializeArchetypes();

// Spawn — lands in a cluster that already occupies the entity's cell, or a fresh one.
using var t = dbe.CreateQuickTransaction();
var id = t.Spawn<Ant>(Ant.Pos.Set(new AntPos { Bounds = new AABB2F { MinX = 5, MinY = 5, MaxX = 5, MaxY = 5 } }));
t.Commit();

// Move it across a cell boundary — migration is detected and executed automatically
// at the next tick fence; no explicit migration call.
using var wt = dbe.CreateQuickTransaction();
var ant = wt.OpenMut(id);
ref var pos = ref ant.Write(Ant.Pos);
pos.Bounds = new AABB2F { MinX = 105, MinY = 5, MaxX = 105, MaxY = 5 };
wt.Commit();
```

| Config field (`SpatialGridConfig`) | Default | Effect |
|---|---|---|
| `MigrationHysteresisRatio` | `0.05` (5% of cell size) | Dead-zone margin past the cell boundary an entity must cross before a migration is queued; absorbs boundary-walking oscillation |

## ⚠️ Guarantees & limits

- **Cluster-cell invariant always holds** — every entity in a cluster belongs to that cluster's assigned cell; enforced at spawn (cell-aware slot claim) and re-enforced by migration, never violated mid-cluster.
- **Migration is deferred and batched, not inline** — movement systems only write positions; the engine detects crossings and executes all of a tick's migrations together at the tick fence, after all systems complete and before the WAL flush, so migration writes are durable in the same fsync as normal commits.
- **One tick of spatial staleness is accepted** — between a position write and the fence, entity data is always correct via direct lookup (EntityMap), but a per-cell spatial query may miss an entity that just crossed a boundary for that one tick.
- **Migration moves everything atomically** — component data (Persistent and Transient), secondary index entries, and the spatial back-pointer move together inside one durability unit; a crash mid-tick can't leave an entity split across clusters.
- **Hysteresis suppresses boundary thrash** — an entity oscillating within the margin around a cell edge never triggers a migration; only a true cross-and-stay does.
- **No compaction policy** — an emptied cluster is deallocated immediately, but a cluster with internal gaps (e.g. 10/64 occupied) is never compacted or merged; occupancy-bit scanning makes gaps cheap to skip.
- **Opt-in, all-or-nothing per engine** — clustering only engages for archetypes with a `[SpatialIndex]` field once `ConfigureSpatialGrid` has been called before `InitializeArchetypes`; non-spatial archetypes are entirely unaffected.
- **Fully transient, rebuilt every startup** — the cluster→cell map, per-cell cluster lists, and cluster AABBs are never written to disk; they're reconstructed from live cluster data after crash recovery or reopen (~1.5ms for 150K clusters), so there's no persisted state to corrupt.

## 🧪 Tests

- [ClusterSpatialCoherenceTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ClusterSpatialCoherenceTests.cs) — cluster-cell invariant at spawn/overflow (`Spawn_ManyEntitiesInSameCell_LandInSameCluster`, `Spawn_BeyondClusterCapacity_AllocatesSecondClusterInSameCell`), grid-config opt-in guards, reopen rebuild
- [ClusterMigrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ClusterMigrationTests.cs) — hysteresis (`PositionChangeWithinHysteresis_NoMigration`/`_BeyondHysteresis_Migrates`), atomic cross-cluster migration incl. Transient data, empty-cluster deallocation, reopen remapping

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/internals/CellClusterPool.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/CellClusterPool.cs)
- Source: [src/Typhon.Engine/Spatial/internals/MigrationRequest.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/MigrationRequest.cs)
- Source: [src/Typhon.Engine/Ecs/internals/ArchetypeClusterState.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/ArchetypeClusterState.cs) (`ClaimSlotInCell`, `RebuildCellState`, `RebuildClusterAabbs`)
- Source: [src/Typhon.Engine/Ecs/public/DatabaseEngine.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/DatabaseEngine.cs) (`DetectClusterMigrations`, `ExecuteMigrations`, `ConfigureSpatialGrid`)
- Related catalog entry: [Entity Clusters](../Ecs/entity-clusters.md) (the base SoA storage this constrains)
- Related catalog entry: [Spatial Grid Configuration & Tier Control](./spatial-grid-config.md) (the grid this clustering shares)
- Related catalog entry: [Cluster Spatial Queries](./cluster-spatial-queries.md) (the query layer this coherence makes cheap)

<!-- Deep dive: claude/design/Spatial/SpatialTiers/01-spatial-clusters.md (cell-cluster mapping, migration, hysteresis, AABB maintenance, bulk loading) -->
<!-- Deep dive: claude/design/Spatial/SpatialTiers/04-tick-integration.md (tick-fence ordering, migration-fence flow) -->
<!-- Rules: claude/rules/spatial.md (modules CC-01, CA-01, MD-01, MD-02, MD-03) -->
