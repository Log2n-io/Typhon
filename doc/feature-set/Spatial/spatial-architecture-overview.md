---
uid: feature-spatial-spatial-architecture-overview
title: 'Spatial Architecture Overview'
description: 'One spatial index — the per-cell cluster broadphase — the grid it stands on, and which feature page answers which question.'
---

# Spatial Architecture Overview
> One spatial index — the per-cell cluster broadphase — the grid it stands on, and which feature page answers which question.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Spatial](./README.md)

**Start with:** [What Spatial Costs You](./spatial-cost-model.md)

## 🎯 What it solves

Landing on the Spatial category for the first time is disorienting: every feature page assumes you already know
how the pieces relate. This page is the map — what exists, what each piece is for, and which feature to read next
depending on what you are trying to do. It is a map, not a starting point: read
[What Spatial Costs You](./spatial-cost-model.md) first, because it teaches what a spatial archetype signs you up
for, and this page then tells you where each of those pieces is documented.

## ⚙️ How it works (in brief)

**There is exactly one spatial index, and it is the per-cell cluster index.** Every query shape — box, radius,
ray, frustum and k-nearest — resolves through it. A component carrying `[SpatialIndex]` allocates no spatial
storage segment of its own; the attribute contributes field metadata (offset, shape, mode, category) and nothing
else. A reflection test walks the whole engine assembly to hold that true, rather than a text search over the
source.

Two layers make that one index work, and they are levels of the same structure rather than alternatives:

1. **The cell grid** — one engine-wide coordinate grid, configured once via
   [Spatial Grid Configuration & Tier Control](./spatial-grid-config.md). It is sparse and three-level, so empty
   regions cost nothing, and it answers coarse questions cheaply per cell instead of per entity. It underpins both
   the index and the dispatch features: [Spatially-Coherent Entity Clustering](./spatial-coherent-clustering.md)
   (every entity in a cluster shares one grid cell), [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md)
   (per-cell simulation frequency), and [Checkerboard Dispatch](./checkerboard-dispatch.md) (safe parallelism on
   top of tiering).
2. **The per-cell cluster broadphase** — inside each occupied cell, a linear array of cluster bounding boxes per
   archetype, split into a static and a dynamic half. A query expands into the cells it overlaps, scans those
   boxes, and tests entities individually only in the clusters that overlap. See
   [Cluster Spatial Queries](./cluster-spatial-queries.md) for the mechanism and
   [Spatial Query API](./spatial-query-api.md) for the surface that reaches it.

**Storage mode does not select a query path.** Every archetype is cluster-backed, whatever its components'
storage modes, so storage mode decides only where component data sits inside the cluster — a `Versioned`
component keeps its HEAD in the cluster slot and its revision chain separate. It decides nothing about which
structure answers a query, because there is only one.

**The grid is therefore required, not optional.** An archetype that declares a `[SpatialIndex]` field and finds no
configured grid throws `InvalidOperationException` at `InitializeArchetypes`, naming the archetype and telling you
to call `ConfigureSpatialGrid` during startup or drop the attribute. It fails at startup on purpose, while you can
still act on it, rather than at the first spawn. Every spatial example in the guide calls `ConfigureSpatialGrid`
for this reason.

## Decision table

| You want to... | Read |
|---|---|
| Understand what a spatial archetype costs per tick, and choose a cell size | [What Spatial Costs You](./spatial-cost-model.md) |
| Query "what's near this point / in this box / along this ray" | [Field Attribute & Schema Integration](./spatial-field-attribute/README.md) → [Spatial Query API](./spatial-query-api.md) |
| Simulate a large world at multiple frequencies (near the player vs. far away) | [Spatial Grid Configuration & Tier Control](./spatial-grid-config.md) → [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md) |
| Keep spatially-nearby entities in the same cluster for cheap per-cell operations | [Spatially-Coherent Entity Clustering](./spatial-coherent-clustering.md) (requires the grid, above) |
| Separate rarely-moving from every-tick data so maintenance skips the former | [Static / Dynamic Separation](./spatial-rtree-index/spatial-rtree-static-dynamic.md) |
| Skip whole clusters by a bitmask before geometry tests | [Category Filtering](./spatial-category-filtering.md) |
| Change a parameter because tick time or query cost moved | [Tuning the Spatial Grid](./spatial-tuning.md), after [Reading Spatial Telemetry](./spatial-telemetry.md) |

## ⚠️ Guarantees & limits

- **One index** — the per-cell cluster broadphase serves every spatial query, and it is the only index home. Nothing bypasses the grid, and no configuration adds a second structure alongside it.
- **`[SpatialIndex]` alone is not sufficient** — it requires `ConfigureSpatialGrid` to have been called before `InitializeArchetypes`, and registering it never configures the grid implicitly. The failure is a startup exception naming the archetype, not a degraded query.
- **The grid is engine-wide and singular** — every spatial archetype shares one cell size and one set of world bounds, fixed before `InitializeArchetypes` and immutable afterwards. There is no per-archetype grid.
- **Two `cellSize` parameters exist, but only one shapes queries** — the engine-wide `SpatialGridConfig.CellSize`. The `cellSize` argument on `[SpatialIndex]` is carried in schema metadata and sizes no live structure.
- **The world is bounded** — positions outside the configured bounds are clamped into the nearest edge cell rather than rejected. See [What Spatial Costs You](./spatial-cost-model.md) for what that means in practice.
- **This page describes architecture, not an API surface of its own** — there is nothing here to call; every code example lives on the linked feature pages.

## 🔗 Related

- Start here instead: [What Spatial Costs You](./spatial-cost-model.md) — the four recurring costs and the cell-size rule
- Sibling: [Field Attribute & Schema Integration](./spatial-field-attribute/README.md) — declaring a spatial field
- Sibling: [Spatial Query API](./spatial-query-api.md) — the read path
- Sibling: [Spatial Grid Configuration & Tier Control](./spatial-grid-config.md) — the grid's entry point
- Sibling: [Spatially-Coherent Entity Clustering](./spatial-coherent-clustering.md) — the cluster-cell invariant
- Sibling: [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md) — spending the simulation budget per cell
- Deep dive: [Cluster Spatial Queries](./cluster-spatial-queries.md) (Internal) — broadphase and narrowphase in detail

<!-- Deep dive: claude/design/Spatial/vdb-cell-grid-and-migration.md (cell grid, migration, drift, repair) -->
<!-- Deep dive: claude/design/Spatial/SpatialTiers/01-spatial-clusters.md (grid/cluster architecture) -->
<!-- Rules: rules/spatial.md (module SH-01 — one spatial index home) -->
