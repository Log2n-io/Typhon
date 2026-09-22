---
uid: feature-spatial-index
title: 'Spatial'
description: 'Typhon''s spatial layer gives any component field a declarative [SpatialIndex], and one index answers it — the per-cell cluster broadphase…'
---

# Spatial
> Typhon's spatial layer gives any component field a declarative `[SpatialIndex]`, and one index answers it: the per-cell cluster broadphase, standing on an engine-wide sparse cell grid and serving AABB/Radius/Ray/Frustum/kNN queries for every archetype. `[SpatialIndex]` contributes field metadata (offset, shape, mode, category) and allocates no storage segment of its own. On top of the same grid, a tiered-simulation system lets game code assign per-cell simulation frequency, sleep idle clusters, and parallelize neighbor-touching systems safely — so a large world spends its CPU budget where the player actually is.

> 🔬 **Recommended:** read [in-depth-overview/07-spatial.md](../../in-depth-overview/07-spatial.md) (Chapter 07: Spatial) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [What Spatial Costs You](spatial-cost-model.md) | The four cost centres, why cluster tightness governs query cost, and how to choose a cell size — read before declaring a second spatial archetype | ✅ Implemented | 🟢 Start Here |
| [Spatial Architecture Overview](spatial-architecture-overview.md) | One spatial index, two levels: the grid locates a cell, the cell's cluster broadphase answers the query. Which feature to read next | ✅ Implemented | 🔵 Core |
| [Field Attribute & Schema Integration](spatial-field-attribute/README.md) | Declare a component field as spatially indexed via `[SpatialIndex]`, validated against schema rules at registration time | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Storage-Mode Compatibility (SingleVersion / Versioned)](spatial-field-attribute/spatial-storage-mode-compat.md) | The same `[SpatialIndex]` field works on both storage modes — only *when* the cluster bound and the cell index catch up differs | ✅ Implemented | 🔵 Core |
| [Spatial Query API (AABB / Radius / Ray / Frustum)](spatial-query-api.md) | Public fluent `EcsQuery` predicates `WhereNearby`/`WhereInAABB`/`WhereRay`/`WhereFrustum`, resolved as a cell walk then a per-cluster narrowphase | ✅ Implemented | 🔵 Core |
| [Spatial Grid Configuration & Tier Control](spatial-grid-config.md) | Engine-wide grid sizing plus the per-cell `SimTier` control surface for multi-resolution simulation | ✅ Implemented | 🔵 Core |
| [Tuning the Spatial Grid](spatial-tuning.md) | Every `SpatialGridConfig` parameter with the symptom that says to change it, the hysteresis/relocation/repair escalation, and starting recipes per world shape | ✅ Implemented | 🟣 Advanced |
| [Reading Spatial Telemetry](spatial-telemetry.md) | Each migration counter paired with the parameter it tunes, plus worked readings for a world that is throttling, starving or evicting | ✅ Implemented | 🟣 Advanced |
| [Static / Dynamic Separation](spatial-rtree-index/spatial-rtree-static-dynamic.md) *(part of [Per-Cell Cluster Index Internals](spatial-rtree-index/README.md))* | Every cell keeps two halves — a tick-fence-exempt static one and a dynamic one refreshed each fence — and a query unions them | ✅ Implemented | 🟣 Advanced |
| [Cluster-Bound Motion Hysteresis](cluster-bound-hysteresis.md) | A cluster's bound covers up to 64 entities, so most movement changes nothing; only a move that pushes it outward costs a per-axis widen plus a fence recompute | ✅ Implemented | 🟣 Advanced |
| [Category Filtering](spatial-category-filtering.md) | Bitmask pruning skips whole clusters before geometry tests via `[SpatialIndex(Category = ...)]` + `ClusterSpatialQuery<TArch>` | ✅ Implemented | 🟣 Advanced |
| [Spatially-Coherent Entity Clustering](spatial-coherent-clustering.md) | Every entity in a cluster shares one grid cell, so spatial bookkeeping is per-cluster, not per-entity | ✅ Implemented | 🟣 Advanced |
| [Tiered Simulation Dispatch](tiered-simulation-dispatch.md) | One simulation tier per spatial cell, four dispatch frequencies, zero per-entity distance checks | ✅ Implemented | 🟣 Advanced |
| [Checkerboard Dispatch](checkerboard-dispatch.md) | Opt-in two-phase Red/Black cluster partitioning for systems that write across cell boundaries, dispatched as one DAG node with two internal phases | ✅ Implemented | 🟣 Advanced |

## Internal Features

> Engine machinery below this line backs the public features above but is never directly instantiated or called by application code — kept here for engine contributors.

| Feature | Summary | Status |
|---|---|---|
| [Per-Cell Cluster Index Internals](spatial-rtree-index/README.md) | Node layout, fanout and the lock protocol of the R-Tree a cell half holds once its density crosses the promotion threshold — most cells stay well below it and scan a linear list instead | ✅ Implemented |
| [Trigger Volumes (Enter / Leave / Stay)](spatial-trigger-volumes.md) | Region occupancy diffed against the per-cell cluster index each cycle to emit Enter/Leave/Stay events at a configurable per-region frequency, reached through `dbe.SpatialTriggers<T>()` | ✅ Implemented |
| [Interest Management (Delta Spatial Queries)](spatial-interest-management.md) | Per-observer "what changed near me" delta queries via an archived dirty-bitmap ring buffer, with full-sync fallback for stale observers, reached through `dbe.SpatialObservers<T>()` | 🚧 Partial |
| [Cluster Spatial Queries](cluster-spatial-queries.md) | Per-cell broadphase + per-entity narrowphase AABB/Radius queries for cluster-eligible archetypes; raw enumerator needs an engine-internal `EpochGuard` scope, app code reaches the same path via the public `EcsQuery` predicates above | 🚧 Partial |
| [Cluster Dormancy (Sleep / Wake)](cluster-dormancy.md) | Clusters with no component writes for N ticks sleep and skip dispatch entirely, waking within one tick of being touched; no public configuration API yet | ✅ Implemented |