---
uid: feature-spatial-spatial-query-api
title: 'Spatial Query API (AABB / Radius / Ray / Frustum / kNN / Count)'
description: 'Six query algorithms over the per-component R-Tree, from zero-allocation engine hot loops to composable fluent ECS filters.'
---

# Spatial Query API (AABB / Radius / Ray / Frustum / kNN / Count)
> Six query algorithms over the per-component R-Tree, from zero-allocation engine hot loops to composable fluent ECS filters.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Spatial](./README.md)

## 🎯 What it solves

A spatial index is only as useful as the questions it can answer. "What's in this box", "what's within range", "what does this ray hit", "what's visible in this frustum", "what are the k nearest", and "how many without listing them" are all distinct access patterns with different traversal strategies — answering all of them with one generic algorithm wastes cycles on every call site. The Spatial Query API gives each pattern its own traversal over the same R-Tree (see [Spatial R-Tree Index](./spatial-rtree-index/README.md)), so AI/physics/replication code picks the cheapest primitive for the job instead of over-fetching and post-filtering by hand.

## ⚙️ How it works (in brief)

Two entry-point tiers sit over the same tree. Engine-internal hot loops (trigger evaluation, interest management, the spatial query planner's fallback path) use `SpatialQuery<T>`, a zero-allocation handle exposing all six algorithms. Application code reaches a subset of the same traversals through the public fluent `EcsQuery<TArchetype>` — `WhereNearby`, `WhereInAABB`, and `WhereRay` attach one spatial predicate that composes with `.With`/`.Without`/`.Where`/`.WhereField` (see [Spatial Query Predicates](../Querying/spatial-predicates.md) for composition rules). Every algorithm returns a `ref struct` enumerator — a stack-based DFS over R-Tree nodes with per-node optimistic-lock-coupling (OLC) version checks; a concurrent writer mid-traversal triggers a restart from the root rather than returning torn data, so results are always complete even under contention. AABB is the base traversal; Radius converts to an AABB pre-filter the caller post-filters by squared distance; Ray walks front-to-back via a min-heap on entry distance; Frustum prunes whole subtrees that classify fully INSIDE or OUTSIDE the half-space planes; kNN iteratively doubles a radius query until enough candidates are found; Count replaces the result buffer with a counter and skips descent into subtrees fully contained by the query region.

## 💻 Usage

```csharp
[Component("Game.Position", 1)]
public struct Position
{
    [SpatialIndex(margin: 5.0f)]
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

| Algorithm | Public entry point | Engine-internal entry point |
|---|---|---|
| AABB overlap | `EcsQuery.WhereInAABB<T>` | `SpatialQuery<T>.AABB` |
| Radius (sphere) | `EcsQuery.WhereNearby<T>` | `SpatialQuery<T>.Radius` |
| Ray (front-to-back) | `EcsQuery.WhereRay<T>` | `SpatialQuery<T>.Ray` |
| Frustum | — not yet exposed | `SpatialQuery<T>.Frustum` |
| kNN | — not yet exposed | `SpatialQuery<T>.Nearest` |
| Count (AABB/Radius) | — not yet exposed | `SpatialQuery<T>.CountInAABB` / `.CountInRadius` |

## ⚠️ Guarantees & limits

- **Query completeness, no false negatives** — every entity geometrically matching a query is returned; fat-AABB and union-category-mask staleness can produce extra candidates (filtered by the caller) but never drop a true match (rule SQ-01).
- **Zero heap allocation per query** — every enumerator is a `ref struct`; no boxing, no list materialization until the caller chooses to collect results.
- **Concurrent-safe via restart, not blocking** — OLC version mismatch mid-traversal restarts the DFS from the root; readers never block writers and vice versa.
- **Radius/kNN are AABB-box pre-filters** — false positive rate against the true sphere is ~21% in 2D, ~48% in 3D; callers post-filter by squared distance when exactness matters.
- **Ray query terminates early** — nodes are visited in increasing entry-distance order via a min-heap; traversal stops once the next candidate's entry distance exceeds `maxDist`, typically visiting only 5–15 nodes regardless of tree size.
- **Frustum pruning is subtree-level** — an MBR classified fully INSIDE yields its entire subtree without per-entry plane tests; only boundary-straddling subtrees pay the full classification cost.
- **kNN distances are not returned** — the tree stores fat AABBs, not tight bounds, so `Nearest` returns candidate `EntityId`s with `distSq = 0`; callers recompute exact distance from component data if exact ordering is needed.
- **Count avoids materialization** — `CountInAABB`/`CountInRadius` use the same subtree-containment shortcut as Frustum, up to ~30x faster than enumerating + counting when most of the query region is fully covered (rules SQ-03, SQ-04).
- **Frustum, kNN, and Count are engine-internal today** — only reachable from engine code via `SpatialQuery<T>`, not from the public `EcsQuery` fluent surface.
- **Public fluent surface allows one spatial predicate per query** — a second `WhereNearby`/`WhereInAABB`/`WhereRay` call throws; see [Spatial Query Predicates](../Querying/spatial-predicates.md) for full composition rules.

## 🧪 Tests

- [SpatialQueryTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialQueryTests.cs) — `SpatialQuery<T>` Radius/Ray/Frustum/kNN traversal correctness vs a brute-force reference, ray/AABB intersection edge cases
- [SpatialEcsIntegrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialEcsIntegrationTests.cs) — public fluent `WhereInAABB`/`WhereNearby`/`WhereRay`, composition with `WhereField`/`Where`, and the "one spatial predicate per query"/foreach guard exceptions

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/internals/SpatialQuery.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialQuery.cs) (engine-internal handle, all six algorithms)
- Source: [src/Typhon.Engine/Spatial/internals/SpatialRTree.Query.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialRTree.Query.cs) (enumerator implementations)
- Source: [src/Typhon.Engine/Ecs/public/EcsQuery.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/EcsQuery.cs) (public fluent surface)
- Related catalog entry: [Querying / Spatial Query Predicates](../Querying/spatial-predicates.md) (fluent composition rules)
- Related catalog entry: [Spatial R-Tree Index](./spatial-rtree-index/README.md) (the index structure these queries run against)
- Overview: [Spatial Architecture Overview](./spatial-architecture-overview.md) — how this fits with the separate spatial grid

<!-- Deep dive: claude/design/Spatial/SpatialIndex/04-query-api.md (API surface, traversal algorithms per query type) -->
<!-- Deep dive: claude/design/Spatial/SpatialIndex/08-game-features.md (category filtering, Count Queries feature rationale) -->
<!-- Rules: claude/rules/spatial.md (Module: Queries — SQ-01 through SQ-05) -->
<!-- ADR: claude/adr/044-spatial-rtree-architecture.md -->
