---
uid: feature-spatial-cluster-bound-hysteresis
title: 'Cluster-Bound Motion Hysteresis'
description: 'A cluster bound covering up to 64 entities absorbs most movement, so a moving entity usually costs a few comparisons and no index write.'
---

# Cluster-Bound Motion Hysteresis
> A cluster bound covering up to 64 entities absorbs most movement, so a moving entity usually costs a few comparisons and no index write.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Spatial](./README.md)

## 🎯 What it solves

A spatial index that tracks entities one at a time has to do per-entity work every time one moves, even by a single unit. At game-tick frequency with thousands of moving entities that becomes the dominant cost of keeping the index live. Typhon does not track entities individually. Entities are stored in clusters of up to 64 that all share one coarse grid cell, and the indexed unit is the cluster, carrying a single bound over its members. Because that bound is the union of up to 64 entities it is far wider than any one of them, so the overwhelming majority of small, continuous motion — walking, patrolling, physics jitter — stays inside a bound that is already correct and costs nothing to maintain. Only a move that pushes the bound outward does any work, and that work is a per-axis compare-and-swap on the cluster's own bound plus a recompute at the tick fence, never a per-entity removal and reinsertion into a tree.

## ⚙️ How it works (in brief)

The bound that absorbs motion is per **cluster**, not per entity. Every entity lives in a cluster of up to 64 same-archetype entities that all share one grid cell, and the cluster carries a single AABB covering all of them. Writing an indexed field through the `WriteSpatial` barrier compares the entity's new box against that stored cluster bound. A move that stays inside it is the fast path: a handful of float comparisons, no bookkeeping bit set, nothing else touched. A move that pushes past an edge widens the cluster bound in place with a per-axis compare-and-swap and marks the cluster for the tick fence; a move that vacates an edge instead flags that axis as shrink-pending, so the fence can retighten it. Because the cluster bound is the union of up to 64 entities it is already far wider than any one of them, and that is what gives small motion somewhere to go.

At the tick fence the marked clusters have their bounds recomputed from their occupied slots, and the fresh bound is written into the cell that owns the cluster. What that write costs depends on how the cell is indexed. By default a cell holds a linear structure-of-arrays list of its clusters' bounds, and the update is a direct indexed store — there is no containment test and nothing to escape. A cell whose cluster count crosses `CellTreePromoteThreshold` swaps that half of its index for a real R-Tree, and only there does a fast/slow split reappear: the new bounds are written into the leaf in place while they still fit that leaf's bounding rectangle, and the cluster is removed and reinserted when they do not. The swap runs both ways. A half promotes on insertion once it crosses the threshold and falls back to a linear index on removal once it drops to half of it, the gap being deliberate hysteresis — a cell hovering at the boundary would otherwise rebuild its whole structure twice per tick, and a rebuild is O(clusters) on a cell whose cluster count is by definition large. The two halves promote independently.

Which shape is right depends on how many clusters sit in one cell, and that is a property of your world rather than of your configuration: a sparse universe with a few dense pockets is exactly what a sparse grid is for, and no single cell size makes the dense pockets sparse without multiplying the cell count everywhere else. The measured crossover sat between 512 and 1 563 clusters in a cell, which is where the default threshold of 1024 comes from. It has since moved further out: vectorising both structures in September 2026 gained the linear scan more than the tree (67–70 % against 12–14 % on a selective query), so at 512 clusters the scan still wins and the tree first pulls ahead around 2 048. The update side is unchanged and is what the threshold really guards — the tree's update path costs 20x more per moved cluster, and every cluster whose bounds change pays it every tick. The motion hysteresis described above is what makes the update side affordable at all: it absorbs about 97 % of moves, which turns a 30x update penalty into roughly 2.7x.

> **The engine decides this, and it re-decides as the world changes.** `CellTreePromoteThreshold` is a number, not a mode: a cell half promotes the moment a cluster is inserted into it while it holds that many, and falls back the moment a removal drops it to half that. Nothing in the application asks for either. What you can set is where the boundary sits — `DatabaseEngineOptions.Spatial.CellTreePromoteThreshold`, in clusters per cell half, defaulting to 1024. `int.MaxValue` keeps every cell on the linear scan whatever its density.
>
> The threshold is read once per archetype, while the engine initialises its archetypes, so configure it before the database opens. There is no per-cell and no per-archetype override: density is a property of the world, and the engine observes it directly.
>
> Falling back is a full retirement, not a detach: the cell's tree is emptied and its node chunks returned to the segment the archetype shares between all of its cell trees. That segment is transient — heap-backed, with nothing watching it — so a cell that crosses the boundary repeatedly costs one `O(clusters)` rebuild each way and no accumulating memory.

**The hysteresis is structural, not a configured enlargement.** It comes from the cluster bound itself, and from four mechanisms that maintain it: the bound is the union of up to 64 members; growth is applied inline while shrinking waits for the fence, so within a tick the bound covers every position visited; an entity may sit outside its cell by `MigrationHysteresisRatio` before it migrates; and `ClusterDriftMarginRatio` is a dead zone for intra-cell drift. Together those absorb about 97 % of moves without touching an index.

## 💻 Usage

```csharp
// [SpatialIndex] goes on a box or sphere field — one of the eight AABB2F/AABB3F/BSphere2F/BSphere3F
// types and their f64 equivalents. Bare floats are rejected at registration.
[Component("Game.Bounds", 1)]
public struct Bounds
{
    [SpatialIndex]
    public AABB3F Box;
}

[Archetype]
partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Bounds> Box = Register<Bounds>();
}

// No extra API to call — fast/slow path selection happens automatically
// whenever the indexed field changes, at tick fence (SV) or commit (Versioned).
// Write through the WriteSpatial barrier so the engine sees the move.
cluster.WriteSpatial(Unit.Box, slotIndex, new Bounds
{
    Box = new AABB3F { MinX = x, MinY = y, MinZ = z, MaxX = x, MaxY = y, MaxZ = z }
});
```

## ⚠️ Guarantees & limits

- **Fast path — a few float comparisons, no index access**, when the entity's new box stays inside its cluster's bound. This is the common case, because that bound covers up to 64 entities.
- **Slow path — widen the cluster bound, then refresh it at the fence**, when the move pushes past an edge. A remove-and-reinsert happens only inside a cell that has been promoted to an R-Tree, which is a cell far denser than most.
- **The hysteresis cannot be tuned per archetype** — it is a property of the cluster bound, which every archetype maintains the same way. The two knobs that do move it, `MigrationHysteresisRatio` and `ClusterDriftMarginRatio`, are engine-wide grid settings expressed as fractions of the cell size rather than world units.
- **No escape-rate warning is emitted.** The engine keeps per-archetype AABB change and escape counters, but they are internal diagnostics that tests read; nothing logs when the ratio is high.
- **Transparent to query results** — the broadphase prunes on cluster bounds, but every candidate is then tested against the entity's own exact stored bounds, so a wider bound costs extra work and never changes an answer.
- **No action needed on teleports/large jumps** — these simply always take the slow path; correctness is unaffected, only cost.

## 🧪 Tests

- [ClusterSpatialTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ClusterSpatialTests.cs) — `TickFence_SmallMove_NoEscape_FastPath` and `TickFence_MovedEntity_FatAABBEscape_RTreeUpdated`. Neither observes the fast/slow split its name suggests: each moves one entity, runs a fence, and asserts the entity is still returned by a spatial query. They cover end-to-end queryability across a small move and a large one, not the fast/slow split.
- [SpatialPerfTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialPerfTests.cs) — `Bench_ContainmentCheck_2Df32` times a bare coordinate-containment call in a loop. It measures that primitive in isolation; it does not exercise a live index.

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/internals/SpatialMaintainer.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialMaintainer.cs)
- Source: [src/Typhon.Engine/Ecs/public/ClusterRef.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/ClusterRef.cs) (`WriteSpatial`, `MaybeGrowAndFlagShrink` — the write-time grow and shrink flag)
- Source: [src/Typhon.Engine/Spatial/internals/CellClusterTree.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/CellClusterTree.cs) (`UpdateAt` — the in-place / escape split, promoted cells only)
- Sibling: [Spatial R-Tree Index](./spatial-rtree-index/README.md) — the tree structure a promoted cell uses
- Sibling: [Static / Dynamic Tree Separation](./spatial-rtree-index/spatial-rtree-static-dynamic.md) — a cell keeps its static and dynamic clusters in separate indexes, and only the `Dynamic` half is refreshed at the tick fence

<!-- Deep dive: claude/design/Spatial/SpatialIndex/03-tree-operations.md § Fat AABB Update Protocol, Back-Pointer Storage -->
<!-- Deep dive: claude/adr/044-spatial-rtree-architecture.md (update-strategy rationale) -->
