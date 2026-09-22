---
uid: feature-spatial-spatial-rtree-index-index
title: 'Per-Cell Cluster Index Internals'
description: 'The R-Tree a dense cell promotes to — node layout, fanout per variant, and optimistic lock coupling.'
---

# Per-Cell Cluster Index Internals
> The R-Tree a dense cell's cluster half promotes to: node layout, fanout per variant, and optimistic lock coupling.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Spatial](../README.md)

## 🧭 Where this tree sits

There is exactly one spatial index and it is the per-cell cluster broadphase, held there by a reflection test that walks the whole engine assembly rather than by a text search. See [Spatial Architecture Overview](../spatial-architecture-overview.md) for the shape of it. Its usual form inside a cell is a linear array of cluster bounding boxes; a cell whose cluster count crosses a promotion threshold swaps that array for an R-Tree over the same boxes. **That tree is what this page documents.**

It is the `SpatialRTree<TStore>` implementation, parameterised as a cluster index:

| | Per-cell cluster tree |
|---|---|
| One per | promoted cell half, per archetype |
| Leaf payload | cluster chunk id |
| Entry bounds | the cluster's bounding box, expressed relative to its cell's origin |
| Storage | one heap-backed transient segment, shared by every cell of one archetype |
| Lifetime | rebuilt from cluster state; nothing reaches disk |

`[SpatialIndex]` carries `cellSize` as field metadata, and it does not size a live structure.

## 🎯 What it solves

A cell holding a handful of clusters is answered fastest by scanning their bounding boxes end to end — no descent, no pointer chasing, perfect locality. A cell holding thousands is not: the scan becomes the query cost, and a selective query pays for every cluster in the cell whether or not it is anywhere near the region asked about. The per-cell tree is the other end of that trade — `O(log C)` descent over the same cluster boxes — so a dense cell stops charging a selective query for its density.

Neither structure wins at both ends, and a real world contains both ends at once. Measured on the clumped sweep behind this design, the mean cell holds 1.8 clusters and the worst holds 102, for one and the same population. Hence a threshold rather than a wholesale replacement.

## ⚙️ How it works (in brief)

Each cell holds a `PerCellSpatialSlot` per archetype, split into a **dynamic** and a **static** half (see [Static / Dynamic Separation](./spatial-rtree-static-dynamic.md)). A half is **either** a linear `CellSpatialIndex` **or** a `CellClusterTree`, never both — keeping both live would pay the tree's update cost and the scan's on every write, which is the worst of both. `HasDynamicTree` / `HasStaticTree` is the discriminator every reader must consult; a reader that assumes the linear array reads a promoted cell as *empty* — a silent false negative rather than an error.

Promotion rebuilds the half in one `O(C)` pass and publishes the tree with a release store *before* clearing the linear reference, so a concurrent reader sees either the tree or the array it replaces, never neither. Demotion runs the other way at half the promote threshold, and that gap is what stops a cell oscillating across the boundary.

Every cell tree of an archetype shares one `ChunkBasedSegment<TransientStore>`, which is why the metadata mirror in chunk 0 is switched off: one mirror cannot describe many trees. Per-cell segments were rejected on a number: a segment spans at least two pages since the v4 directory-only root, so at the 128³ / 1 % baseline — 20 971 occupied cells — one segment per cell would cost roughly 328 MiB of mostly-empty segment per spatial archetype, against about 10 MiB for the whole cell-grid layer.

Each tree node is one chunk of that segment, laid out **SoA within the node** so geometric scans stay dense:

```
[ OlcVersion(4) | Control(4) | ParentChunkId(4) | NodeMBR(coords) | UnionCategoryMask(4) ]
```

…followed by the entry area. A leaf entry is `[coords | PayloadId(8) | CategoryMask(4)]` and an internal entry is `[coords | ChildChunkId(4)]`.

Node access goes through **optimistic lock coupling**. Readers take no lock: they capture a node's version, read, and validate on the way out, restarting the descent from the root on a mismatch rather than returning torn data. Writers take the node's latch through a plain `SpinWait` loop — deliberately simpler than the B+Tree's two-phase escalation, because a leaf write is tens of nanoseconds and OLC absorbs most of the contention before a writer ever spins.

Handles are the one thing a caller may not keep. A leaf split scatters both halves and a removal swaps the last entry into the freed slot, so an entry can be relocated by a mutation belonging to a *different* cluster. The tree repairs the shared back-pointer array on every such move, and reading it back is one indexed load — cheaper than any scheme for keeping a private copy honest.

## Sub-features

| Sub-feature | Use it when... |
|---|---|
| [Static / Dynamic Separation](./spatial-rtree-static-dynamic.md) | An archetype's spatial data is either rarely-moving (terrain, buildings, fixed triggers) or moves every tick (units, projectiles), and you want each to pay only its own per-tick maintenance |

## ⚠️ Guarantees & limits

- **Nothing here is persisted** — `CellClusterTree` is a `SpatialRTree<TransientStore>` over a heap-backed segment, so no leaf entry ever reaches a file. Cell trees are rebuilt from cluster state, and a reopen reconstructs the per-cell index rather than loading it.
- **The engine promotes and demotes on its own** — a cell half crosses to a tree on the insertion that takes it to `DatabaseEngineOptions.Spatial.CellTreePromoteThreshold` clusters (1024 by default) and falls back on the removal that drops it to half that. A second gate, `Spatial.CellTreePromoteTightness`, additionally requires the cell's mean cluster extent to be at or below 0.10 of the cell edge — a tree can only prune between clusters that are actually apart, and a cell under motion runs far looser than the sweep that calibrated the count. Nothing in the application requests either transition; the options move the boundary, `int.MaxValue` keeps every cell on the linear scan, and a tightness of `1` restores count-only promotion. Most cells never come close, so most databases build no tree at all.
- **The fall-back frees the tree, not just the reference to it** — a cell tree's nodes are chunks of a segment shared by every cell of the archetype, and that segment is transient, so nothing reclaims a structure whose last reference is dropped. Demotion empties the tree and releases its root, which is the one node the removals cannot free on their own (an empty root has no parent to unlink it from). Without that step each promote/fall-back cycle stranded one chunk for the life of the database.
- **Cell trees are always the 3D-f32 variant** — 2D is a degenerate Z axis rather than a second code path, and f64 would more than halve fanout (about 4 leaf entries per node against 13) for a structure whose whole purpose is the `O(log C)`.
- **One writer per promoted tree, enforced by the caller** — `SpatialRTree` is single-writer by specification and `CellClusterTree` adds no latch. The fence slices its AABB-refresh work by cluster rather than by cell, so two workers routinely carry clusters of one cell; each buffers its updates for a promoted cell and one thread replays them in cluster-id order after the phase barrier.
- **A handle lives in the back-pointer array and nowhere else** — that array doubles as the archetype's `ClusterSpatialIndexSlot`, and growing it reallocates, so every live tree is re-pointed on the same call that grew it. A tree writing handles into an abandoned array is that same stale-handle failure with one more layer of indirection.
- **An in-place update leaves the leaf MBR temporarily loose** — when a cluster's new bounds are contained by its leaf's current MBR, the entry is written without refitting ancestors. That makes the leaf a strict superset of the union of its entries until the end-of-pass refit closes the window. Too loose costs overlap tests; too tight would be a false negative, which is why the direction matters.
- **Query completeness is guaranteed** — every cluster whose box matches a query is in the result set; node MBRs are refit on every mutation to keep that true. Extra candidates are expected and rejected by the narrowphase; missing ones are not.
- **Fixed fanout per variant** — leaf entries per node: 17 (2D f32), 13 (3D f32), 10 (2D f64), 11 (3D f64). Internal entries: 24 / 16 / 12 / 13. Node stride is 512 B for every variant except 3D-f64, which is 768 B. Not configurable per component.
- **Lazy underflow** — nodes below minimum fill are tolerated, not merged; only fully empty leaves are reclaimed. Slightly higher storage overhead under heavy churn, no correctness impact.
- **The chunk-0 metadata mirror is switched off** — many trees share one segment, so a mirror there would hold one tree's values or none. `CellClusterTree` constructs with `mirrorMetadata: false`, which makes the sync a no-op and removes what would otherwise be a contended cache line per archetype. A tree that does not mirror cannot be loaded back from its segment, which is consistent with nothing here being persisted in the first place.
- **For what a spatial archetype costs per tick**, read [What Spatial Costs You](../spatial-cost-model.md). This page describes a structure, not a budget.

## 🧪 Tests

- [PerCellRTreeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/PerCellRTreeTests.cs) — the per-cell index end to end: queries spanning cells, disjoint cells, destroy and migration, generic tier matching, radius filtering and hit bounds
- [EntityIndexRetirementTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EntityIndexRetirementTests.cs) — `NoTypeOutsideTheCellLayerHoldsASpatialRTree` walks the engine assembly by reflection; `ASpatialComponentAllocatesNoSpatialSegment` counts segments by kind; every query shape is compared with a brute-force oracle against both an unpromoted and a promoted cell
- [SpatialRTreeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialRTreeTests.cs) — insert/split/remove correctness across all four variants, AABB query vs brute force, category-mask pruning
- [SpatialNodeDescriptorTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialNodeDescriptorTests.cs) — the capacities above stated as literals, plus the leaf SoA offsets, so a change that swaps one column for another of the same width cannot leave the fanout numbers looking right
- [SharedSegmentRTreeHarnessTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SharedSegmentRTreeHarnessTests.cs) — two trees over one segment through interleaved inserts and splits, each returning exactly its own payloads: the measurement that made the shared segment safe
- [SpatialRTreeBulkTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialRTreeBulkTests.cs) — many sequential inserts, `TreeValidator` structural invariant checks

## 🔗 Related

- Related catalog entry: [Cluster Spatial Queries](../cluster-spatial-queries.md) (the broadphase/narrowphase walk this structure serves)
- Related catalog entry: [Spatial Query API](../spatial-query-api.md) and [Querying / Spatial Query Predicates](../../Querying/spatial-predicates.md) (the surfaces that reach it)
- Related catalog entry: [Field Attribute & Schema Integration](../spatial-field-attribute/README.md) (the `[SpatialIndex]` attribute, and which of its arguments do something)
- Sub-features: [Static / Dynamic Separation](./spatial-rtree-static-dynamic.md)
- Overview: [Spatial Architecture Overview](../spatial-architecture-overview.md) — one index, and the grid it stands on

<!-- Deep dive: claude/design/Spatial/SpatialIndex/02-node-layout.md (SOA node layout, variant capacities) -->
<!-- Deep dive: claude/design/Spatial/SpatialIndex/03-tree-operations.md (insert/split/remove, correctness invariants) -->
<!-- Rules: rules/spatial.md (SH-01 one index home, SH-02 leaf entry layout, ST-01/ST-05/ST-07 tree structure, PC-01 per-cell promotion) -->
<!-- ADR: claude/adr/044-spatial-rtree-architecture.md -->
