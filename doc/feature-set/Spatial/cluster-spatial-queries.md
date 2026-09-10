---
uid: feature-spatial-cluster-spatial-queries
title: 'Cluster Spatial Queries'
description: 'Per-cell broadphase + per-entity narrowphase AABB/Radius queries for cluster-eligible archetypes.'
---

# Cluster Spatial Queries
> Per-cell broadphase + per-entity narrowphase AABB/Radius queries for cluster-eligible archetypes.

**Status:** 🚧 Partial · **Visibility:** Internal · **Category:** [Spatial](./README.md)

## 🎯 What it solves

Entity clusters (see [Entity Clusters](../Ecs/entity-clusters.md)) pack many entities of one archetype into a single contiguous chunk for cache-friendly storage. A per-entity spatial index defeats that locality — every entity move triggers an index update, and queries scatter-read leaf nodes across the tree. Cluster Spatial Queries indexes cluster *bounding boxes* instead of individual entities, so spatial lookups (find nearby units, find food in radius, AI sensing) stay cheap even as entity counts scale into the hundreds of thousands, without re-introducing per-entity index maintenance cost.

## ⚙️ How it works (in brief)

Each grid cell (see [Spatial Grid & Cells](./spatial-grid-config.md)) holds a small linear array of cluster AABBs for each cluster-eligible archetype occupying it — typically 15-80 entries. A query first expands its region into the overlapping cells, scans each cell's cluster AABBs (broadphase), and for every cluster whose AABB overlaps, scans that cluster's live entities individually (narrowphase). Cluster AABBs are recomputed once per tick for clusters that changed, not on every entity write. Static and dynamic clusters are indexed separately and both are checked by every query. Every archetype is cluster-backed, so every spatial query takes this path: there is no fallback and no second index to choose between. The R-Tree machinery sits one level up, as the structure a dense cell can be promoted to (see the promotion note under *Guarantees & limits*); [Spatial Architecture Overview](./spatial-architecture-overview.md) places the grid and the tree against each other.

## 💻 Usage

```csharp
[Component("Game.Position", 1)]
public struct Position
{
    [SpatialIndex]
    public AABB2F Bounds;
}

using var guard = EpochGuard.Enter(dbe.EpochManager);

// AABB — direct per-cell query, zero allocation
var box = new AABB2F { MinX = 0, MinY = 0, MaxX = 50, MaxY = 50 };
foreach (var hit in dbe.ClusterSpatialQuery<Ant>().AABB<AABB2F>(in box, categoryMask: 0x3))
{
    // hit.EntityId, hit.ClusterChunkId, hit.SlotIndex, hit.MinX/Y/Z, hit.MaxX/Y/Z
}

// Radius — same per-cell path, sphere distance filter
var sphere = new BSphere2F { CenterX = 10, CenterY = 10, Radius = 15 };
foreach (var hit in dbe.ClusterSpatialQuery<Ant>().Radius(in sphere))
{
    // hit.DistanceSq is populated (closest point on entity AABB to sphere center)
}

// Equivalent Radius/Nearest access via the fluent ECS query (routes through the same cluster path)
using var t = dbe.CreateQuickTransaction();
var nearby = t.Query<AntArch>()
    .WhereNearby<Position>(centerX: 10, centerY: 10, centerZ: 0, radius: 15)
    .Execute();
```

`TBox` must be `AABB2F` or `AABB3F` and must match the archetype's declared spatial field dimensionality/precision exactly — a mismatch throws `InvalidOperationException` at the call site, not silently truncating coordinates.

## ⚠️ Guarantees & limits

- **Raw enumerator requires an `EpochGuard` scope** — `ClusterSpatialQuery<TArch>` reads cluster pages directly, so the caller must hold `EpochGuard.Enter(dbe.EpochManager)` for the call's duration. `EpochGuard` is `internal`; reaching it directly (as above) requires the same `InternalsVisibleTo` boundary as Cluster Dormancy. Application code without that access goes through `EcsQuery.WhereNearby`/`WhereInAABB`, which manages the epoch scope internally.
- **Requires `ConfigureSpatialGrid`** before `InitializeArchetypes` — a cluster-eligible archetype with a `[SpatialIndex]` field and no configured grid throws at archetype initialization, not at first query.
- **The raw enumerator exposes AABB and Radius only** — `ClusterSpatialQuery<TArch>` has no Ray or Frustum entry point. Both shapes *are* served on this same cluster path, through `EcsQuery.WhereRay` / `WhereFrustum` (see [Spatial Query API](./spatial-query-api.md)).
- **f32 only** — `AABB2D`/`AABB3D` (f64) throw `NotSupportedException`; only `AABB2F`/`AABB3F` are implemented.
- **No false negatives** — cluster AABBs always contain every live entity in the cluster (recomputed at the tick fence for dirty clusters), so a query never misses a geometrically-matching entity.
- **Category mask is "any bit overlaps"** — a non-zero mask skips a cluster only if none of its entities' OR'd category bits intersect the query mask; pass `uint.MaxValue` (default) for no filtering.
- **Zero-allocation enumerator** — `AABB<TBox>` and `Radius` both return a `ref struct` enumerator; no heap allocation for the scan itself. `EcsQuery.WhereNearby`/`WhereInAABB` materialize results into a `HashSet<EntityId>` because the fluent API composes with archetype/Where filters.
- **Query cost is 300-400ns, and per-entity index maintenance is zero** — broadphase plus narrowphase costs roughly 300-400ns for a typical 4-cell query, and that walk is the whole cost: moving an entity updates no per-entity index, so nothing is charged per entity per tick.
- **No public `Nearest`/kNN on `ClusterSpatialQuery<TArch>` yet** — k-nearest is only reachable internally (consumed by `EcsQuery.WhereNearby`'s radius-expansion path); a dedicated public `.Nearest()` wrapper is deferred.
- **The broadphase changes shape with density, and the engine drives it** — above a per-cell cluster-count threshold a cell half swaps its linear array for a per-cell R-Tree (`CellClusterTree`), and falls back below half that count. The two are mutually exclusive, never both, so no cell pays for a tree and a scan at once. The threshold is `DatabaseEngineOptions.Spatial.CellTreePromoteThreshold`, and it defaults to `int.MaxValue` — never — because on a game-shaped workload a promoted cell made the tick slower at every density measured (its upkeep runs in the fence's serial tail, and the narrowphase dominates the query it speeds); set a count to opt in. A second gate, `CellTreePromoteTightness`, requires the cell's clusters to be tight enough for a tree to prune between them at all. Both structures were vectorised in September 2026 and the scan gained more than the tree did, so the crossover moved *away* from the tree: at 512 clusters a selective query costs about 102 ns scanned against 247 ns through the tree, and the tree first wins around 2 048 (403 ns scanned, 246 ns through the tree). An unselective query never favours the tree at any density, because it must test every internal-node box on top of every leaf entry. Most databases never reach that density and never build a tree; the ones that do stop paying a scan that grows without bound. Both structures answer the same question, which the differential fixtures assert directly.
- **The narrowphase is scalar** — the broadphase is not: a cell holding at least 16 clusters is tested 64 at a time with `Vector256`, and the per-cell tree's leaf and internal nodes likewise. Below 16 the batch costs more than it saves and the scalar loop runs instead. Vectorising the per-entity narrowphase remains a profiling-gated future optimisation.

## 🧪 Tests

- [ClusterSpatialTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ClusterSpatialTests.cs) — `SpatialQuery_AABB_ReturnsClusterEntities`/`SpatialQuery_Radius_ReturnsClusterEntities` (2D broadphase+narrowphase correctness)
- [ClusterSpatial3DTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ClusterSpatial3DTests.cs) — 3D Z-axis AABB/Radius filtering, Z-boundary overlap edge cases
- [CellSpatialIndexTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialGrid/CellSpatialIndexTests.cs) — per-cell cluster-AABB array growth/swap-removal, category-union computation backing the broadphase

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/public/ClusterSpatialQuery.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/ClusterSpatialQuery.cs)
- Source: [src/Typhon.Engine/Spatial/public/AabbClusterEnumerator.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/AabbClusterEnumerator.cs)
- Source: [src/Typhon.Engine/Spatial/public/ClusterSpatialAabb.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/ClusterSpatialAabb.cs)
- Source: [src/Typhon.Engine/Spatial/internals/CellSpatialIndex.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/CellSpatialIndex.cs)
- Related catalog entry: [Spatial Query API](./spatial-query-api.md) (the fluent `EcsQuery` spatial predicates, including the ray and frustum shapes this enumerator does not expose)
- Related catalog entry: [Entity Clusters](../Ecs/entity-clusters.md)

<!-- Deep dive: claude/design/Spatial/SpatialTiers/02-cluster-rtree.md (full design + phase history) -->
<!-- Deep dive: claude/design/Spatial/spatial-grid-api.md (public API surface inventory) -->
<!-- Rules: rules/spatial.md (Module: Cluster Spatial AABBs — CA-01) -->
