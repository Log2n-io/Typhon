---
uid: feature-querying-spatial-predicates
title: 'Spatial Query Predicates'
description: 'R-Tree-backed AABB, radius, and ray filters attached directly to a fluent ECS query.'
---

# Spatial Query Predicates
> R-Tree-backed AABB, radius, and ray filters attached directly to a fluent ECS query.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Querying](./README.md)

## 🎯 What it solves

Game-server and simulation workloads constantly ask spatial questions — "what's in this room", "what's within aggro range", "what does this ray hit" — that a B+Tree field index cannot answer efficiently. Brute-force scanning every entity's position component to test bounds is O(n) per query and falls apart well before 10K entities at 60Hz. Spatial Query Predicates give `EcsQuery` four index-driven spatial filters (AABB overlap, radius/sphere, ray intersection, frustum) that run against the spatial index instead of a linear scan, and compose with the rest of the query builder (archetype constraints, field predicates, opaque post-filters).

## ⚙️ How it works (in brief)

Mark the field that defines an entity's bounds — an AABB or bounding-sphere type (`AABB2F`, `AABB3F`, `BSphere2F`, `BSphere3F`, …) — with `[SpatialIndex]`, and configure the engine's spatial grid (`ConfigureSpatialGrid`) before archetypes are initialized. The engine indexes each archetype's clusters per grid cell by their bounding boxes, updated as entities spawn, move, and despawn. `WhereInAABB<T>`, `WhereNearby<T>`, `WhereRay<T>`, and `WhereFrustum<T>` attach one spatial predicate to the query — the index (not a table scan) produces the candidate set, which is then narrowed by archetype mask, any `.Where()`/`.WhereField()` predicate chained onto the same query (AND semantics), and visibility. Only one spatial predicate is allowed per query; `InRealm(realm)` scopes it to one realm (realm 0 by default).

## 💻 Usage

```csharp
[Component("Game.Position", 1, StorageMode = StorageMode.SingleVersion)]
public struct Position
{
    [SpatialIndex]
    public AABB3F Bounds;
}

using var t = dbe.CreateQuickTransaction();

// AABB overlap — entities whose Position bounds intersect the box
var inRoom = t.Query<UnitArch>()
    .WhereInAABB<Position>(minX: 0, minY: 0, minZ: 0, maxX: 50, maxY: 0, maxZ: 50)
    .Execute();                                          // → HashSet<EntityId>

// Radius — entities within range of a point
var nearby = t.Query<UnitArch>()
    .WhereNearby<Position>(centerX: 10, centerY: 0, centerZ: 10, radius: 15)
    .Without<Dead>()
    .Execute();

// Ray — first-hit-style candidate set along a direction
var hit = t.Query<UnitArch>()
    .WhereRay<Position>(originX: 0, originY: 1, originZ: 0, dirX: 1, dirY: 0, dirZ: 0, maxDist: 100)
    .Any();
```

| Constructor arg (`[SpatialIndex]`) | Default | Effect |
|---|---|---|
| `cellSize` | `0` (off) | Cell size, in world units, of a coarse-grid broadphase occupancy filter; `0` disables the filter |
| `Mode` | `SpatialMode.Dynamic` | `Static` skips tick-fence index updates — for entities that never move |
| `Category` | `uint.MaxValue` | Archetype-level bitmask; lets a per-cluster broadphase skip whole archetype clusters that don't overlap the query |

## ⚠️ Guarantees & limits

- **One spatial predicate per query** — a second `WhereNearby`/`WhereInAABB`/`WhereRay`/`WhereFrustum` call throws `InvalidOperationException`; run separate queries and combine in application code.
- **Composable with `Where`/`WhereField`** — both are applied as an AND post-filter over the spatial candidate set, not a second index probe.
- **Archetype-mask filtered** — results respect `.With<T>()`/`.Without<T>()`/`.Exclude<T>()` the same as any other query.
- **Not supported on `foreach`** — iterating the query directly only applies the archetype scan + `.Where()`; a spatial predicate throws. Call `.Execute()` (or `.Any()`/`.Count()`) instead.
- **Not supported with `WhereField` on `ToView()`** — a reactive view cannot combine an indexed field predicate with a spatial predicate; a spatial-only (or spatial + opaque `Where`) view falls back to full re-query (pull mode) on every `Refresh()`, not incremental delta tracking.
- **Not supported with `ExecuteOrdered`** — spatial predicates don't carry an ordering; use `Execute()` and sort in application code if needed.
- **Sub-microsecond at 10K entities**; ~600µs total spatial budget at 100K entities for a 60Hz tick (3.6% of frame).
- **kNN-style "nearest N" is not exposed** at this layer — only set-membership filters (AABB/radius/ray/frustum).

## 🧪 Tests

- [SpatialEcsIntegrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialEcsIntegrationTests.cs) — `WhereInAABB`/`Count`/`Any`, composition with `Where`/`WhereField`, and the one-spatial-predicate-per-query guard

## 🔗 Related

- Source: [src/Typhon.Engine/Ecs/public/EcsQuery.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/EcsQuery.cs)
- Sibling: [Spatial Query API (AABB / Radius / Ray / Frustum / kNN / Count)](../Spatial/spatial-query-api.md) — the underlying R-Tree query algorithms this feature exposes through the fluent builder

<!-- Deep dive: claude/overview/05-query.md (§5.12 ECS Query API — spatial predicates) -->
<!-- Deep dive: claude/adr/044-spatial-rtree-architecture.md (R-Tree architecture, fat-AABB update strategy, category filtering) -->
