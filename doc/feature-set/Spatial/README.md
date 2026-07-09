---
uid: feature-spatial-index
title: 'Spatial'
description: 'Typhon''s spatial layer gives any component field a declarative [SpatialIndex] — backing it with a page-backed, crash-safe R-Tree for ad-hoc…'
---

# Spatial
> Typhon's spatial layer gives any component field a declarative `[SpatialIndex]` — backing it with a page-backed, crash-safe R-Tree for ad-hoc AABB/Radius/Ray/Frustum/kNN queries, or with the cluster-aware spatial grid for archetypes already using batched SoA storage. On top of that grid, a tiered-simulation system lets game code assign per-cell simulation frequency, sleep idle clusters, and parallelize neighbor-touching systems safely — so a large world spends its CPU budget where the player actually is.

> 🔬 **Recommended:** read [in-depth-overview/07-spatial.md](../../in-depth-overview/07-spatial.md) (Chapter 07: Spatial) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Spatial Architecture Overview](spatial-architecture-overview.md) | Explains how the per-component R-Tree and the engine-wide spatial grid are two independent mechanisms, and which feature to read next | ✅ Implemented | 🟢 Start Here |
| [Field Attribute & Schema Integration](spatial-field-attribute/README.md) | Declare a component field as spatially indexed via `[SpatialIndex]`, validated against schema rules at registration time | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Storage-Mode Compatibility (SingleVersion / Versioned)](spatial-field-attribute/spatial-storage-mode-compat.md) | The same `[SpatialIndex]` field works on both storage modes — only *when* the tree catches up differs | ✅ Implemented | 🔵 Core |
| [Spatial Query API (AABB / Radius / Ray)](spatial-query-api.md) | Public fluent `EcsQuery` predicates `WhereNearby`/`WhereInAABB`/`WhereRay` over the per-component spatial index | ✅ Implemented | 🔵 Core |
| [Spatial Grid Configuration & Tier Control](spatial-grid-config.md) | Engine-wide grid sizing plus the per-cell `SimTier` control surface for multi-resolution simulation | ✅ Implemented | 🔵 Core |
| [Static / Dynamic Tree Separation](spatial-rtree-index/spatial-rtree-static-dynamic.md) *(part of [Spatial R-Tree Index](spatial-rtree-index/README.md))* | A spatial field lands in one of two independent trees — tick-fence-exempt static, or fat-AABB-maintained dynamic — chosen once at schema time | ✅ Implemented | 🟣 Advanced |
| [Fat-AABB Incremental Update](fat-aabb-update.md) | Margin-enlarged bounds absorb small moves for ~25ns, with no tree mutation | ✅ Implemented | 🟣 Advanced |
| [Category Filtering](spatial-category-filtering.md) | Bitmask pruning skips whole clusters before geometry tests via `[SpatialIndex(Category = ...)]` + `ClusterSpatialQuery<TArch>` | ✅ Implemented | 🟣 Advanced |
| [Spatially-Coherent Entity Clustering](spatial-coherent-clustering.md) | Every entity in a cluster shares one grid cell, so spatial bookkeeping is per-cluster, not per-entity | ✅ Implemented | 🟣 Advanced |
| [Tiered Simulation Dispatch](tiered-simulation-dispatch.md) | One simulation tier per spatial cell, four dispatch frequencies, zero per-entity distance checks | ✅ Implemented | 🟣 Advanced |
| [Checkerboard Dispatch](checkerboard-dispatch.md) | Opt-in two-phase Red/Black cluster partitioning for systems that write across cell boundaries, dispatched as one DAG node with two internal phases | ✅ Implemented | 🟣 Advanced |

## Internal Features

> Engine machinery below this line backs the public features above but is never directly instantiated or called by application code — kept here for engine contributors.

| Feature | Summary | Status |
|---|---|---|
| [Spatial R-Tree Index](spatial-rtree-index/README.md) | Page-backed R-Tree attached to a component field, giving sub-microsecond AABB/radius/ray queries shared across every archetype that uses it | ✅ Implemented |
| [Trigger Volumes (Enter / Leave / Stay)](spatial-trigger-volumes.md) | Region entities diffed against the spatial tree(s) each cycle to emit Enter/Leave/Stay events at a configurable per-region frequency; no public entry point yet | ✅ Implemented |
| [Interest Management (Delta Spatial Queries)](spatial-interest-management.md) | Per-observer "what changed near me" delta queries via an archived dirty-bitmap ring buffer, with full-sync fallback for stale observers; no public entry point yet | 🚧 Partial |
| [Cluster Spatial Queries](cluster-spatial-queries.md) | Per-cell broadphase + per-entity narrowphase AABB/Radius queries for cluster-eligible archetypes; raw enumerator needs an engine-internal `EpochGuard` scope, app code reaches the same path via the public `EcsQuery` predicates above | 🚧 Partial |
| [Cluster Dormancy (Sleep / Wake)](cluster-dormancy.md) | Clusters with no component writes for N ticks sleep and skip dispatch entirely, waking within one tick of being touched; no public configuration API yet | ✅ Implemented |