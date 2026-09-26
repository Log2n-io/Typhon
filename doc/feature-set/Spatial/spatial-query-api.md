---
uid: feature-spatial-spatial-query-api
title: 'Spatial Query API (AABB / Radius / Ray / Frustum / kNN / Count)'
description: 'Five query shapes over the one spatial index, the per-cell cluster broadphase, from zero-allocation enumerators to composable fluent ECS filters.'
---

# Spatial Query API (AABB / Radius / Ray / Frustum / kNN / Count)
> Five query shapes — AABB, radius, ray, frustum and k-nearest — over the one spatial index, the per-cell cluster broadphase, from zero-allocation enumerators to composable fluent ECS filters.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Spatial](./README.md)

## 🎯 What it solves

A spatial index is only as useful as the questions it can answer. "What's in this box", "what's within range", "what does this ray hit", "what's visible in this frustum", "what are the k nearest", and "how many without listing them" are all distinct access patterns with different traversal strategies — answering all of them with one generic algorithm wastes cycles on every call site. The Spatial Query API gives each pattern its own traversal over the same index — the per-cell cluster broadphase, which is the only spatial index there is — so AI/physics/replication code picks the cheapest primitive for the job instead of over-fetching and post-filtering by hand.

## ⚙️ How it works (in brief)

Two entry-point tiers sit over the same index, and there is only one index for them to sit over. Application code composes queries through the public fluent `EcsQuery<TArchetype>` — `WhereInAABB`, `WhereNearby`, `WhereRay` and `WhereFrustum` each attach one spatial predicate that combines with `.With`/`.Without`/`.Where`/`.WhereField` (see [Spatial Query Predicates](../Querying/spatial-predicates.md) for composition rules). Engine code and benchmarks that want hits without materializing a set use `dbe.ClusterSpatialQuery<TArch>()`, a zero-allocation `ref struct` exposing the AABB and radius shapes as enumerators. The type is public, but its enumerator documents a requirement to run inside an engine-internal `EpochGuard` scope, which is why [Cluster Spatial Queries](./cluster-spatial-queries.md) is still marked Partial. Both tiers drive the same two-stage walk: the query region expands into the grid cells it overlaps; inside each cell a scan over that archetype's array of per-cluster bounding boxes is the broadphase; each surviving cluster is then opened and its entities' own bounds are read out of cluster storage and tested, which is the narrowphase. AABB is the base walk. Radius uses the enclosing box as its broadphase and rejects at the narrowphase on closest-point-on-AABB distance, reporting that distance on each hit. Ray sweeps the cells the segment's bounding box covers and orders hits front-to-back on the slab-intersection distance. Frustum classifies each cluster box against the half-space planes, so a cluster fully inside skips per-entity plane tests. kNN takes cells in shells around the query point and stops when the k-th distance is inside the region already swept — sound because a cluster's box distance is a lower bound on its entities'. A cell whose cluster count crosses `DatabaseEngineOptions.Spatial.CellTreePromoteThreshold` swaps its linear array for a per-cell R-Tree over the same cluster boxes, and falls back below half that count (see [Per-Cell Cluster Index Internals](./spatial-rtree-index/README.md)). The engine decides both, from the cell's own density; results are identical either way, which the differential fixtures assert directly.

## 💻 Usage

```csharp
[Component("Game.Position", 1)]
public struct Position
{
    [SpatialIndex]
    public AABB3F Bounds;
}

using var t = dbe.CreateQuickTransaction();

// AABB — public fluent surface (composes with archetype/Where filters)
var inRoom = t.Query<UnitArch>()
    .WhereInAABB<Position>(minX: 0, minY: 0, minZ: 0, maxX: 50, maxY: 0, maxZ: 50)
    .Execute();                                                    // → HashSet<EntityId>

// Radius — same surface, distance-bounded
var nearby = t.Query<UnitArch>()
    .WhereNearby<Position>(centerX: 10, centerY: 0, centerZ: 10, radius: 15)
    .Where<Faction>(f => f.Id == 3)
    .Execute();

// Ray — front-to-back ordered candidates along a direction
var rayHits = t.Query<UnitArch>()
    .WhereRay<Position>(originX: 0, originY: 1, originZ: 0, dirX: 1, dirY: 0, dirZ: 0, maxDist: 100)
    .Execute();
```

| Shape | Fluent entry point | Zero-allocation entry point | Engine-internal walk |
|---|---|---|---|
| AABB overlap | `EcsQuery.WhereInAABB<T>` | `DatabaseEngine.ClusterSpatialQuery<TArch>().AABB<TBox>` | `ArchetypeClusterState.QueryAabb` |
| Radius (sphere) | `EcsQuery.WhereNearby<T>` | `DatabaseEngine.ClusterSpatialQuery<TArch>().Radius` | `ArchetypeClusterState.QueryRadius` |
| Ray (front-to-back) | `EcsQuery.WhereRay<T>` | — not exposed | `ArchetypeClusterState.QueryRay` |
| Frustum | `EcsQuery.WhereFrustum<T>` | — not exposed | `ArchetypeClusterState.QueryFrustum` |
| kNN | — not exposed | — not exposed | `ArchetypeClusterState.QueryNearest` |
| Count (AABB/Radius) | `EcsQuery.Count()` after a spatial predicate | — not exposed | — counts the materialized set; no counting shortcut |

## ⚠️ Guarantees & limits

- **Query completeness, no false negatives** — every entity geometrically matching a query is returned. A cluster's stored bounding box is allowed to be looser than the union of the entities it holds, so the broadphase offers extra candidates that the narrowphase then rejects; it may never be tighter, which is what would drop a true match.
- **Zero heap allocation on the enumerator path** — `ClusterSpatialQuery`'s AABB and radius shapes return a `ref struct` enumerator, and the ray, frustum and kNN walks write into a caller-supplied `Span`. The fluent surface is the deliberate exception: `EcsQuery.Execute()` materializes a `HashSet<EntityId>` because a set is its contract.
- **Bulk drains beat one call per hit** — the AABB/radius enumerator also offers `Count()`, which drains the query without writing a result, and `Fill(Span<ClusterSpatialQueryResult>)`, which writes up to a buffer's worth per call in `MoveNext` order. Both run the whole state machine in one frame; on the SWG Tatooine demo (x16) `Count()` cost 23 % less per hit than a `MoveNext` loop, `Fill` 17 % less.
- **Queries run inside an epoch guard, and a promoted cell restarts rather than blocks** — a cell served by the linear cluster array is scanned directly, and that array is mutated only on the spawn, destroy and tick-fence paths. A cell promoted to a per-cell R-Tree uses optimistic lock coupling: a version mismatch mid-traversal restarts the descent from the root instead of returning torn data.
- **Radius and kNN measure a real distance** — the enclosing box is only the broadphase. The narrowphase computes the squared distance from the query point to the closest point on each entity's own bounds, rejects anything past the radius, and reports it as `ClusterSpatialQueryResult.DistanceSq`. No caller-side post-filter is needed for the sphere.
- **Ray results are ordered, and the cell sweep is wasteful by design** — hits come back front-to-back on slab-intersection distance, and nothing past `maxDist` is reported. The cells visited are those covering the segment's bounding box rather than the cells the segment actually crosses, so some visited cells contribute nothing; a DDA walk is the named follow-up. A broadphase that is merely wasteful is a different class of thing from one that is wrong.
- **Frustum pruning is cluster-level** — a cluster box classified fully inside yields all its entities without per-entity plane tests, and one fully outside is skipped whole. Only boundary-straddling clusters pay the per-entity classification. The caller supplies the bounding box of the region: an over-generous box costs a few rejected cells, a box that does not contain the region is a false negative.
- **kNN returns real distances in order** — results come back ascending by squared distance to the closest point on each entity's tight bounds, so no caller-side re-sort is needed. Early termination is sound because a cluster box's distance is a lower bound on the distance to anything inside it.
- **Counting has no shortcut on the fluent path** — `EcsQuery.Count()` after a spatial predicate builds the result set and returns its size; `ClusterSpatialQuery`'s enumerator `Count()` does not (see above). The subtree-containment shortcut lives on `SpatialRTree.CountInAABB`, which no production path calls; it is exercised by tests and benchmarks only.
- **kNN is engine-internal today** — reachable only from engine code, with no fluent predicate and no `ClusterSpatialQuery` shape. Frustum, by contrast, is on the public fluent surface as `WhereFrustum`.
- **A query answers in one realm** — realm 0 unless `EcsQuery.InRealm(realm)` / `ClusterSpatialQuery<T>(realm)` names another; entities of another realm at the same coordinates are never returned (SQ-08, [Realms](./realms.md)). `InRealm` scopes the spatial predicate: a query that names a realm without one throws; to walk a realm's clusters use `GetClusterEnumerator(realm)`.
- **Public fluent surface allows one spatial predicate per query** — a second `WhereNearby`/`WhereInAABB`/`WhereRay`/`WhereFrustum` call throws; see [Spatial Query Predicates](../Querying/spatial-predicates.md) for full composition rules.

## 🧪 Tests

- [EntityIndexRetirementTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EntityIndexRetirementTests.cs) — `EveryQueryShape_AgreesWithBruteForce`, plus `Ray_MatchesBruteForce` and `Frustum_MatchesBruteForce` run twice each, once against a cell served by the linear array and once against a promoted cell, so both broadphase representations are compared with the same oracle
- [SpatialEcsIntegrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialEcsIntegrationTests.cs) — public fluent `WhereInAABB`/`WhereNearby`/`WhereRay`, composition with `WhereField`/`Where`, and the "one spatial predicate per query"/foreach guard exceptions
- [SpatialQueryTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialQueryTests.cs) — traversal correctness of the underlying `SpatialRTree` walks vs a brute-force reference, plus ray/AABB intersection edge cases; these run against a tree built directly, which is the structure a promoted cell holds

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/public/ClusterSpatialQuery.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/ClusterSpatialQuery.cs) (public zero-allocation handle, AABB and radius)
- Source: [src/Typhon.Engine/Spatial/public/AabbClusterEnumerator.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/AabbClusterEnumerator.cs) (the broadphase/narrowphase state machine both entry points drive)
- Source: [src/Typhon.Engine/Ecs/public/EcsQuery.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/EcsQuery.cs) (public fluent surface; `ExecuteSpatial` is the fan-out over cluster archetypes)
- Related catalog entry: [Querying / Spatial Query Predicates](../Querying/spatial-predicates.md) (fluent composition rules)
- Related catalog entry: [Cluster Spatial Queries](./cluster-spatial-queries.md) (the broadphase/narrowphase mechanism these queries run through)
- Related catalog entry: [Per-Cell Cluster Index Internals](./spatial-rtree-index/README.md) (the structure a promoted cell uses)
- Overview: [Spatial Architecture Overview](./spatial-architecture-overview.md) — how the one index and the grid it stands on fit together

<!-- Deep dive: claude/design/Spatial/SpatialIndex/04-query-api.md (API surface, traversal algorithms per query type) -->
<!-- Deep dive: claude/design/Spatial/SpatialIndex/08-game-features.md (category filtering, Count Queries feature rationale) -->
<!-- Rules: rules/spatial.md (Module: Queries — SQ-01 through SQ-05; SH-01 for the one-index invariant; CA-01/CA-02 for the cluster bound the broadphase prunes on) -->
<!-- ADR: claude/adr/044-spatial-rtree-architecture.md -->
