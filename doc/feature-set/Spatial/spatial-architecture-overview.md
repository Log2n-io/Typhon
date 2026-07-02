# Spatial Architecture Overview
> Two independent spatial mechanisms — a per-component R-Tree for precise geometric queries, and an engine-wide grid for coarse tiering and clustering — that share vocabulary but not implementation.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Spatial](./README.md)

## 🎯 What it solves

Landing on the Spatial category for the first time is disorienting: every feature page already assumes you know how the pieces relate, but nothing tells you there *are* separate pieces. The word "grid" and the parameter `cellSize` both appear in two unrelated places — the R-Tree's internal query accelerator and the engine-wide simulation-tiering grid — and conflating them is the single most common way to misread this category. This page is the map: what exists, what each piece is for, and which feature to read next depending on what you're actually trying to do.

## ⚙️ How it works (in brief)

Two independent, optionally-combined mechanisms sit under Spatial:

1. **The R-Tree** — a page-backed spatial index attached to any component field carrying an AABB/BSphere, declared once via `[SpatialIndex]` ([Field Attribute & Schema Integration](./spatial-field-attribute/README.md)). It answers precise geometric questions — "what overlaps this box/radius/ray" — in O(log n) via the [Spatial Query API](./spatial-query-api.md). Internally it has its own optional acceleration layer: a coarse occupancy hashmap that rejects entirely-empty query regions before the tree is touched at all, sized by the attribute's own `cellSize` parameter. This is the "Layer 1" mentioned in the R-Tree's internal docs, and it is **not** the grid described next, despite the shared name and the shared parameter name.
2. **The spatial grid** — one engine-wide coordinate grid, configured once via [Spatial Grid Configuration & Tier Control](./spatial-grid-config.md) (`ConfigureSpatialGrid`, its own separate `cellSize`). It answers coarse questions — "how often should this region simulate," "which entities are near enough to batch together" — cheaply, per cell instead of per entity. It drives two downstream features: [Spatially-Coherent Entity Clustering](./spatial-coherent-clustering.md) (every entity in a cluster shares one grid cell) and [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md) (per-cell simulation frequency), with [Checkerboard Dispatch](./checkerboard-dispatch.md) as a safe-parallelism refinement on top of tiering.

The two mechanisms can be used independently or together. A component only needs `[SpatialIndex]` to get R-Tree queries — that alone never touches the grid. It additionally benefits from the grid only if `ConfigureSpatialGrid` has been called *and* its archetype is cluster-eligible (see [Entity Clusters](../Ecs/entity-clusters.md)). Nothing in the API forces you to use both, and using one says nothing about whether the other is configured.

## Decision table

| You want to... | Read |
|---|---|
| Query "what's near this point / in this box / along this ray" | [Field Attribute & Schema Integration](./spatial-field-attribute/README.md) → [Spatial Query API](./spatial-query-api.md) |
| Simulate a large world at multiple frequencies (near the player vs. far away) | [Spatial Grid Configuration & Tier Control](./spatial-grid-config.md) → [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md) |
| Keep spatially-nearby entities in the same cluster for cheap per-cell operations | [Spatially-Coherent Entity Clustering](./spatial-coherent-clustering.md) (requires the grid, above) |
| Tune R-Tree maintenance cost for rarely-moving vs. every-tick data | [Static / Dynamic Tree Separation](./spatial-rtree-index/spatial-rtree-static-dynamic.md) |
| Skip whole subtrees/clusters by a bitmask before geometry tests | [Category Filtering](./spatial-category-filtering.md) |

## ⚠️ Guarantees & limits

- The R-Tree's Layer-1 occupancy filter and the engine-wide `SpatialGridConfig` are separate structures with independently-set `cellSize` values — changing one never affects the other, and neither is derived from the other.
- `[SpatialIndex]` alone is sufficient for R-Tree queries; it does not require `ConfigureSpatialGrid` to have been called, and registering it never implicitly configures the grid.
- The grid (`ConfigureSpatialGrid`) is engine-wide and singular — every participating spatial archetype shares the same cell size and bounds. The R-Tree has no such restriction — each field's Layer-1 filter, if enabled, is sized independently per `[SpatialIndex(cellSize: ...)]` declaration.
- This page describes architecture, not an API surface of its own — there is nothing here to call; every code example lives on the linked feature pages.

## 🔗 Related

- Sibling: [Field Attribute & Schema Integration](./spatial-field-attribute/README.md) — the R-Tree side's entry point
- Sibling: [Spatial Query API](./spatial-query-api.md) — the R-Tree side's read path
- Sibling: [Spatial Grid Configuration & Tier Control](./spatial-grid-config.md) — the grid side's entry point
- Sibling: [Spatially-Coherent Entity Clustering](./spatial-coherent-clustering.md) — the grid side's clustering consumer
- Sibling: [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md) — the grid side's dispatch consumer
- Deep dive on the R-Tree's own two-layer design: [Spatial R-Tree Index](./spatial-rtree-index/README.md) (Internal)

<!-- Deep dive: claude/design/Spatial/SpatialIndex/01-architecture.md (two-layer R-Tree design, Layer 1/Layer 2) -->
<!-- Deep dive: claude/design/Spatial/SpatialTiers/01-spatial-clusters.md (grid/cluster architecture) -->
