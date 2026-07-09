---
uid: overview-spatial
title: '07 — Spatial'
description: 'Spatial indexing in Typhon answers "which entities are near this point / inside this box / hit by this ray?" — the kinds of queries games, simulations, and…'
---

# 07 — Spatial

**Code:** [`src/Typhon.Engine/Spatial/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Spatial) (+ geometric primitives in [`src/Typhon.Schema.Definition/SpatialTypes.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/SpatialTypes.cs))

Spatial indexing in Typhon answers "which entities are near this point / inside this box / hit by this ray?" — the kinds of queries games, simulations, and geospatial workloads run thousands of times per tick. The subsystem is two complementary structures stacked on top of each other:

- A **shared coarse spatial grid** — engine-wide, one cell size, used as a broadphase. Per-archetype cluster storage hangs off this grid so a cluster's entities can be located in O(1) from its (x, y).
- A **per-component R-Tree** — fine-grained spatial index over an entity's "fat" AABB. One tree per spatially-indexed component table; serves AABB, radius, ray, frustum, kNN, and count queries.

Application code never instantiates either directly. You annotate a component field with `[SpatialIndex(margin, cellSize)]` ([§5](#5-ecs-integration)) and use spatial operators on `EcsQuery` (`WhereInAABB`, `WhereNearby`, `WhereRay`) — the engine picks the right path, threads the back-pointers, and keeps the index coherent across spawns, updates, and destroys.

This doc covers what the index does, what guarantees it offers, and how it integrates with ECS — not every micro-optimisation in the code (split policy, OLC validation, back-pointer chase logic). For those, the source is well-commented.

---

## 1. Overview

Two structures, two purposes:

| Structure | Granularity | Use |
|---|---|---|
| **`SpatialGrid`** ([`Spatial/internals/SpatialGrid.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialGrid.cs)) | Coarse — world divided into cells of `CellSize` world units | Broadphase: locate the handful of cells overlapping a query, iterate the clusters bucketed in them |
| **`SpatialRTree<TStore>`** ([`Spatial/internals/SpatialRTree.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialRTree.cs)) | Fine — per-entity (or per-cluster) AABB hierarchy | Narrowphase: AABB / radius / ray / frustum / kNN; subtree-shortcut counting |

The grid is **one per `DatabaseEngine`** — configured once at startup via `DatabaseEngine.ConfigureSpatialGrid(SpatialGridConfig)` before archetypes are initialized. All spatial archetypes share it. Per-archetype differences (tier filters, category masks) are layered above; the grid itself is uniform.

R-Trees are **per `ComponentTable`** — a component table only has one when its declaring field carries `[SpatialIndex]`. The tree's state lives on [`SpatialIndexState`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialIndexState.cs), which the `ComponentTable` exposes as a non-null `SpatialIndex` property when the component is spatial. With per-component `SpatialMode` (Static or Dynamic), exactly one of `StaticTree`/`DynamicTree` is non-null per component table.

Cluster archetypes ([06-ecs §7](06-ecs.md)) plug into the grid through [`ArchetypeClusterState`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/ArchetypeClusterState.cs): each cluster carries a `ClusterSpatialAabb` (six floats + category mask, [`ClusterSpatialAabb.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/ClusterSpatialAabb.cs)) summarising every entity it holds, and the grid's [`CellState`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/CellDescriptor.cs) tracks how many clusters and entities sit in each cell. That bookkeeping is what makes the grid useful as a broadphase: an AABB query maps to a small set of cells, each cell yields a small set of clusters, and only those clusters are visited at the entity level.

Per-entity (non-clustered) archetypes use the R-Tree directly — there's no grid bucketing for them. The grid only earns its keep when clustering compresses many entities into one bucket.

---

## 2. Spatial grid

The grid divides 2D world space into fixed-size cells. It's deliberately 2D even when the archetype's spatial field is 3D — Z is filtered at the narrowphase, not at the cell level. This keeps the cell key small (32 bits) and lets a single grid serve mixed 2D/3D workloads.

### `SpatialGridConfig`

[`Spatial/public/SpatialGridConfig.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialGridConfig.cs)

Immutable, validated at construction:

| Field | Meaning |
|---|---|
| `WorldMin` / `WorldMax` | `Vector2` world-space bounds. Max strictly > Min on both axes. |
| `CellSize` | World units per cell. Must be > 0. |
| `MigrationHysteresisRatio` | Fraction of cell size used as dead zone for cluster migration (default 0.05). |
| `GridWidth` / `GridHeight` | Derived. `ceil((Max - Min) / CellSize)`. |
| `KeySpaceDim` | Derived. Padded to next power of two when Morton keys are enabled. |
| `CellCount` | Derived. `KeySpaceDim²` for Morton, `GridWidth × GridHeight` otherwise. |
| `InverseCellSize` | Precomputed `1 / CellSize`. |

Cell keys are encoded via [`MortonKeys`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/MortonKeys.cs) (Z-order curve) when `SpatialConfig.UseMortonCellKeys` is true — that's the production default. Morton interleaves 16 bits per axis into a 32-bit key, capping each axis at 32 768; the constructor throws if you exceed that. The fallback row-major form (`cellY * GridWidth + cellX`) is kept behind a const-bool flag for diagnostics.

### `SpatialGrid` and `CellState`

[`Spatial/internals/SpatialGrid.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialGrid.cs), [`Spatial/internals/CellDescriptor.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/CellDescriptor.cs)

The grid owns a flat `CellState[]` of length `CellCount`. Each `CellState` is **64 bytes — one full cache line**, by `[StructLayout(LayoutKind.Explicit, Size = 64)]`:

| Offset | Field | Meaning |
|---|---|---|
| 0 | `byte Tier` | SimTier assignment (single-bit flag; multi-tier rejected). |
| 1 | `byte Flags` | Reserved. |
| 2 | `ushort Reserved` | Reserved. |
| 4 | `int ClusterCount` | Sum of clusters in this cell across **all** archetypes sharing the grid. `Interlocked` only. |
| 8 | `int EntityCount` | Sum of `PopCount(OccupancyBits)` across all clusters in this cell. `Interlocked` only. |
| 12–63 | — | Padding / future use. |

The cache-line padding is non-negotiable — fence workers concurrently mutate `EntityCount`/`ClusterCount` for *different* cells, and without padding adjacent cells would false-share.

Per-cell **cluster lists** (which clusters live in a cell, per archetype) don't live on `CellState` — they're per archetype, stored inside each `ArchetypeClusterState`'s own [`CellClusterPool`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/CellClusterPool.cs) / [`CellSpatialIndex`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/CellSpatialIndex.cs). The grid is the shared global counter; archetypes own their own per-cell linkage.

All grid state is **transient** — nothing in `CellState` is persisted. After a database reopen, `RebuildCellState` reconstructs it by replaying entity positions.

### Coordinate utilities

The grid is the place to convert between world space and cells — it's a pure stateless mapping over `Config`:

```csharp
int cellKey = grid.WorldToCellKey(worldX, worldY);
grid.WorldToCellRange(minX, minY, maxX, maxY,
    out int cellMinX, out int cellMinY, out int cellMaxX, out int cellMaxY);
int cellKey2 = grid.ComputeCellKey(cellX, cellY);
(int x, int y) = grid.CellKeyToCoords(cellKey);
```

Out-of-bounds inputs are clamped to the grid extent. NaN / infinite inputs **throw** — silently producing a meaningless cell would be a debugging nightmare. The `ReadSpatialCenter2D(byte* fieldPtr, SpatialFieldType, out posX, out posY)` static unpacks a center from any of the four supported field types (AABB2F/3F, BSphere2F/3F) — used by both world-to-cell mapping and the cluster-migration cell-crossing detector.

### `SpatialGridAccessor`

[`Spatial/public/SpatialGridAccessor.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialGridAccessor.cs)

8-byte `readonly struct` — the game-facing handle exposed on `TickContext.SpatialGrid`. Thin wrapper over `SpatialGrid` for tier assignment (`SetCellTier`, `SetCellTierMin`, `ResetAllTiers`, `SetTierInAABB`) and coordinate queries. `IsValid` is false when the engine has no configured grid (non-spatial games or shutdown).

---

## 3. R-Tree

The R-Tree is Typhon's narrowphase. Entries are **fat AABBs** — each entity's tight bounds enlarged by a per-component `margin`, so small motions stay inside the same leaf entry and don't trigger a tree mutation. The fat-AABB trick is what makes the index cheap for moving entities: the *containment check* on update is ~25 ns, and a full remove + reinsert only happens when an entity escapes its margin.

### Variants and layout

One implementation, four variants — selected by [`SpatialVariant`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialVariant.cs): `R2Df32`, `R3Df32`, `R2Df64`, `R3Df64`. The layout is described by a [`SpatialNodeDescriptor`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialNodeDescriptor.cs) — a `readonly struct` of node-layout constants the JIT promotes to literal values, so generic code over `SpatialRTree<TStore>` doesn't pay polymorphism cost.

Each node is one chunk of a [`ChunkBasedSegment<TStore>`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/ChunkBasedSegment.cs) — **512 B for 2D and 3D-f32, 768 B for 3D-f64**. The layout is **SOA within the node** (separate arrays for coordinates / IDs / category masks rather than an array of entry structs), which keeps geometric scans dense. Header layout:

```
[ OlcVersion(4) | Control(4) | ParentChunkId(4) | NodeMBR(coords) | UnionCategoryMask(4) ]
```

…followed by the entry area. Leaf entries carry `[coords | EntityId(8) | ComponentChunkId(4) | CategoryMask(4)]`; internal entries carry `[coords | ChildChunkId(4)]`. Leaf fanout is in the range 19–38 depending on variant; internal fanout is higher because internal entries are smaller.

Tree metadata (root chunk id, node count, entity count, depth, variant) lives in **chunk 0** of the segment. The tree state struct itself is in-memory only:

| Field | Meaning |
|---|---|
| `_rootChunkId` | Chunk id of the current root. |
| `_nodeCount` / `_entityCount` / `_depth` | Tree statistics. |
| `_mutationVersion` | Monotonic counter, bumped on every `Insert`/`Remove` — consumed by the trigger system for cache invalidation. |
| `BackPointerSegment` | When set, every leaf entry's back-pointer is kept in this CBS so a component lookup yields `(leafChunkId, slotIndex)` in O(1). |

### Concurrency model — `SpinWriteLock` (its own variant)

Each R-Tree node has a 32-bit [`OlcLatch`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/OlcLatch.cs) embedded in its header. Reads use optimistic concurrency control — capture the version before the read, do the work, validate after; restart if anything changed. Writes need exclusive access, and the R-Tree takes it through a **plain `SpinWait` loop**:

```csharp
private static void SpinWriteLock(byte* nodeBase, out OlcLatch latch)
{
    latch = GetLatch(nodeBase);
    SpinWait spin = default;
    while (!latch.TryWriteLock())
    {
        spin.SpinOnce();
    }
}
```

That's it — no deadline, no holdoff, no telemetry hooks, no two-phase progression. **This is different from the B+Tree's `SpinWriteLock`** ([03-indexing](03-indexing.md)), which runs a two-phase 64-iteration PAUSE → yield-capped escalation tuned to avoid `Sleep(1)`. The R-Tree variant is unconditional `SpinOnce` and accepts whatever progression .NET's `SpinWait` gives it (`Thread.SpinWait` for the first iterations, then `Thread.Yield()` / `Thread.Sleep(0/1)` after the threshold).

Why the simpler model? Two reasons:

1. **Lock-hold time is shorter.** A leaf write — append-or-swap, refit the leaf MBR — is ~tens of nanoseconds. A B+Tree node mutation in contrast can involve a split, key shuffling, and ancestor updates, so its lock-hold variance is much wider and the two-phase escalation pays for itself.
2. **OLC absorbs most contention.** Readers don't take the lock at all — they validate the version on the way out, and restart on conflict (`return default` from `TryInsert`, or `RestartFromRoot` in the query enumerators). Writers contend only with other writers on the same node; the spinner rarely runs for long.

The contrast matters: if you're hunting a "lock not advancing" symptom on the R-Tree, you're looking at `SpinWait.SpinOnce()` behavior; don't transplant your B+Tree mental model. If you're hunting it on the B+Tree, the two-phase escalation is the place to look.

Tree-level metadata writes (`SyncMetadata` on chunk 0) take a separate **plain `Lock`** to serialise concurrent root-pointer / depth updates.

### Operations

[`SpatialRTree.Insert.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialRTree.Insert.cs), [`SpatialRTree.Query.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialRTree.Query.cs), [`SpatialRTree.Remove.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialRTree.Remove.cs), [`SpatialRTree.Split.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialRTree.Split.cs), [`SpatialRTree.BulkLoad.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialRTree.BulkLoad.cs)

| Operation | Notes |
|---|---|
| **Insert** | OLC descent picks the best leaf (smallest MBR enlargement, tie-break smallest area). Write-locks the leaf, appends, refits MBR up the recorded descent path. Splits when the leaf is full ([Split.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialRTree.Split.cs)). Restarts on OLC version mismatch — capped at 255 restart attempts before a hard fail. |
| **Remove** | Reads the back-pointer to get `(leafChunkId, slotIndex)` directly — O(1), no descent. Swap-with-last in the leaf, refit MBR, walk ancestors bottom-up via `ParentChunkId`. When the last entry leaves a non-root leaf, the leaf chunk is recycled (`RemoveEmptyLeaf`). |
| **QueryAABB** | Stack-based DFS, OLC validate per node, leaf-level overlap test fully unrolled for 2D and 3D. Returns `AABBQueryEnumerator` (ref struct, zero allocation). |
| **QueryRadius** | Coarse filter — converts radius to enclosing AABB. False positive rate ~21 % (2D) / ~48 % (3D). Caller post-filters by squared distance. |
| **QueryRay** | Min-heap priority queue (64 entries), front-to-back order. Ray–AABB slab intersection (`SpatialGeometry.RayAABBIntersect`). |
| **QueryFrustum** | Half-space classification (`ClassifyAABBAgainstPlanes`). Inside / Outside / Intersecting — Inside subtrees skip per-entry plane tests entirely (the leaf scan still happens, just without geometry). |
| **QueryKNN** | Iterative radius expansion (converges in 1–2 iterations for k < 20). Returns candidate `EntityId`s with `distSq = 0` — caller recomputes actual distance from component data. |
| **CountInAABB / CountInRadius** | Subtree counting shortcut: fully-contained subtrees count their entries without per-entry overlap tests (~30× speedup for large fully-covered regions). |

Every operation emits a tier-2-gated `TyphonEvent` span (`Spatial:RTree:Insert`, `Spatial:Query:Aabb`, `Spatial:Query:Radius`, `Spatial:Query:Ray`, …) populated with result counts, nodes visited, leaves entered, and OLC restart counts. See [12-observability](12-observability.md).

### Category masks

Every leaf entry carries a 32-bit `CategoryMask`; every node carries a `UnionCategoryMask` = OR of all descendants' masks. Queries pass a `categoryMask` and entire subtrees whose union mask doesn't intersect are pruned (`(unionMask & queryMask) == 0`). For leaves, the leaf-entry mask must match the query mask `(leafMask & queryMask) == queryMask`. A query mask of `0` is a sentinel that bypasses filtering — useful when you want every result.

Category masks are **archetype-level** in cluster archetypes (every entity in an archetype has the same value), so the per-cluster union is effectively a constant — incremental OR on spawn, no recompute on destroy.

---

## 4. Geometric predicates

The geometric primitives — `AABB2F/3F/2D/3D`, `BSphere2F/3F/2D/3D` — live in the sibling [`Typhon.Schema.Definition`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/SpatialTypes.cs) project, not in `Typhon.Engine`. That's because schema-defined component fields need to reference them without dragging in the full engine. See [01-foundation §9](01-foundation.md#9-schema-definition-types-sibling-project) for why this split exists.

Helpers ([`Spatial/internals/SpatialGeometry.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialGeometry.cs)) — all `[MethodImpl(AggressiveInlining)]`, scalar-only in v1 (SOA layout enables drop-in SIMD later):

| Predicate / op | Variants |
|---|---|
| `Overlaps(a, b)` | AABB×AABB, all 4 variants |
| `Contains(outer, inner)` | AABB×AABB, all 4 variants |
| `Enlarge(box, margin)` | All 4 variants — turns a tight AABB into a fat one |
| `Union(a, b)` | All 4 variants |
| `Area(box)` / `Volume(box)` | 2D / 3D respectively |
| `Enclosing(BSphere)` | Sphere → enclosing AABB (used to convert radius queries to AABB queries) |
| `IsDegenerate(box)` | NaN / inverted-bounds check |
| `RayAABBIntersect(origin, invDir, coords, count)` | Slab method, ~6–8 float ops per box |
| `ClassifyAABBAgainstPlanes(coords, planes, planeCount, dim)` | Positive/negative vertex method — returns Inside / Intersecting / Outside |
| `SquaredDistanceToCenter(point, coords, count)` | For kNN ranking |

A `SpatialFieldType` enum maps the schema-side `FieldType` to a compact 0–7 byte: `AABB2F=0`, `AABB3F=1`, `BSphere2F=2`, `BSphere3F=3`, `AABB2D=4`, …. `SpatialFieldInfo.ToVariant()` then maps that to the right `SpatialVariant` for the tree.

Currently supported by the grid: **f32 only** (2D and 3D — Z is filtered at narrowphase). f64 variants are valid as field types and the R-Tree handles them, but the grid bucketing only operates on f32. `SpatialGrid.ValidateSupportedFieldType` enforces this at `ConfigureSpatialGrid` time.

---

## 5. ECS integration

You mark a component field as spatially indexed with `[SpatialIndex(margin, cellSize)]` from `Typhon.Schema.Definition`:

```csharp
public struct Position
{
    [SpatialIndex(margin: 5.0f, cellSize: 100.0f, Mode = SpatialMode.Dynamic, Category = 1 << 0)]
    public AABB2F Bounds;
}
```

[`SpatialIndexAttribute`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/Attributes.cs) (lines 138–169) carries:

| Property | Meaning |
|---|---|
| `Margin` | World units added on each axis when going from tight → fat AABB. Larger margin → fewer reinserts, more false positives at the leaf. |
| `CellSize` | Per-component cell size for the optional Layer-1 occupancy filter. `0` = disabled. |
| `Mode` | `Dynamic` (default — fat-AABB updates at tick fence) or `Static` (bulk-loaded once, skipped by the tick fence). |
| `Category` | Archetype-level 32-bit mask used by the cluster broadphase. Defaults to `uint.MaxValue` (accept every query). |

At schema registration, the engine reads the attribute, infers the `SpatialFieldType` from the field's C# type (`AABB2F`, `BSphere3F`, …), builds a [`SpatialFieldInfo`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialFieldInfo.cs), allocates the right R-Tree variant via `SpatialNodeDescriptor.ForVariant`, and stores everything in a `SpatialIndexState` on the component table. Cluster archetypes that contain a spatial component also get registered on the state's `ClusterArchetypes` list for fan-out at query time.

### Query operators

`EcsQuery<TArchetype>` ([06-ecs §5](06-ecs.md), [09-querying](09-querying.md)) exposes three spatial filters — defined in [`Ecs/public/EcsQuery.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/EcsQuery.cs):

```csharp
tx.Query<Ant>()
  .WhereInAABB<Position>(minX, minY, minZ, maxX, maxY, maxZ)
  .Execute();

tx.Query<Ant>()
  .WhereNearby<Position>(centerX, centerY, centerZ, radius)
  .Execute();

tx.Query<Ant>()
  .WhereRay<Position>(originX, originY, originZ, dirX, dirY, dirZ, maxDist)
  .Execute();
```

Each operator records the query type and parameters on the `EcsQuery`, then `Execute` dispatches to the appropriate spatial path:

- For **cluster archetypes**: through `ClusterSpatialQuery<TArch>` ([`Spatial/public/ClusterSpatialQuery.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/ClusterSpatialQuery.cs)) → per-cell broadphase via `SpatialGrid.WorldToCellRange` → for each overlapping cell, scan its `CellSpatialIndex` clusters → narrowphase per cluster reads entity AABBs and tests against the query.
- For **per-entity archetypes**: through `SpatialQuery<T>` ([`Spatial/internals/SpatialQuery.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialQuery.cs)) → directly hits the R-Tree's `QueryAABB` / `QueryRadius` / `QueryRay` enumerators.

Both paths emit an `Ecs:Query:Spatial:Attach` instant event so traces show the bounding region the predicate is filtering on.

The `Debug.Assert` inside each `Where*` method requires the component to carry `[SpatialIndex]` — calling `WhereNearby<NotSpatial>(…)` is a usage error caught in Debug builds.

---

## 6. Insert / Query / Remove flows

When a spatially-indexed component changes value, the index has to stay coherent. The mechanics differ slightly between Versioned and SingleVersion/Transient components (see [05-revision](05-revision.md) / [06-ecs §8](06-ecs.md)), but the index-side work funnels through [`SpatialMaintainer`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialMaintainer.cs).

### Insert (entity spawned)

Triggered at `FinalizeSpawns` for SingleVersion/Transient and at commit for Versioned. [`SpatialMaintainer.InsertSpatial`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialMaintainer.cs):

1. Read the tight bounds from the component data via `ReadAndValidateBounds`. Degenerate bounds (NaN / inverted) emit a warning and **skip** the index — the entity exists in ECS, but isn't queryable spatially.
2. Enlarge tight → fat AABB by `Margin`.
3. `tree.Insert(entityPK, componentChunkId, coords, ref accessor, changeSet)` — returns `(leafChunkId, slotIndex)`.
4. Write the back-pointer via `SpatialBackPointerHelper.Write(ref bpAccessor, componentChunkId, leafChunkId, (short)slotIndex, treeSelector)` so future updates / removes are O(1).
5. Layer-1 (optional, when `CellSize > 0` on the field): increment the per-cell occupancy map.

For cluster archetypes the path is different — `ClusterRef.WriteSpatial` ([`Ecs/public/ClusterRef.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/ClusterRef.cs)) updates the cluster's `ClusterSpatialAabb` inline (CAS per axis), sets the cluster's bit in `ClusterProcessBitmap`, and flags axes for shrink rescan or cells for migration as needed. The tick fence then processes those flags in bulk.

### Update (component value changes)

Triggered at the tick fence for SV/Transient and at commit for Versioned. [`SpatialMaintainer.UpdateSpatialCore`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialMaintainer.cs):

1. Read current tight bounds, validate.
2. Read the back-pointer → leaf chunk id + slot index.
3. **Fast path**: read the existing fat AABB from the leaf, check if the new tight AABB is still contained. If yes, return — no tree mutation, no lock taken. ~25 ns.
4. **Slow path**: entity escaped its fat AABB. `tree.Remove(leafChunkId, slotIndex)` (which swap-with-lasts in the leaf and returns the swapped entity's id), enlarge the new tight bounds, `tree.Insert` at the new position, write a fresh back-pointer. If the remove caused a swap (the removed entry wasn't the last), update the swapped entity's back-pointer too. ~500–700 ns.

The fast/slow ratio is monitored via the `SpatialMaintainerEscapeRate` telemetry. A high escape rate (> 10 %) means the margin is too small for the workload's motion characteristics; the maintainer logs a warning suggesting a larger margin.

### Remove (entity destroyed)

[`SpatialMaintainer.RemoveFromSpatial`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialMaintainer.cs):

1. Read the back-pointer. If `LeafChunkId == 0`, the entity was never inserted (degenerate bounds at spawn) — skip.
2. Route to the correct tree (Static or Dynamic) via the back-pointer's `TreeSelector`.
3. Read the fat AABB coords (needed for the Layer-1 decrement).
4. `tree.Remove(leafChunkId, slotIndex)`. Handle the swap-with-last back-pointer update if necessary.
5. Decrement Layer-1 occupancy.

### Cluster migration

When an entity in a cluster archetype moves into a different grid cell (with hysteresis around the boundary so jitter at the edge doesn't oscillate), the tick fence migrates it: removes the cluster slot's contribution from the source cell's `EntityCount`, adds it to the destination cell, and updates the per-cell cluster lists. This is the [`MigrationRequest`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/MigrationRequest.cs) / [`SpatialMaintainer`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialMaintainer.cs) path — outside the per-entity R-Tree, but the reason `CellState.EntityCount` exists. A migration storm (many migrations per tick) emits a warning — usually means a viewport warp, teleport, or unphysical speed.

### Trigger and interest systems

Two further consumers sit on the spatial index:

- [`SpatialTriggerSystem`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialTriggerSystem.cs) — volume occupancy tracking ("which entities are inside this static AABB?"). Uses `QueryAABBOccupants` to populate occupant bitmaps indexed by component chunk id, with `_mutationVersion`-driven invalidation when the tree changes.
- [`SpatialInterestSystem`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialInterestSystem.cs) — observer-driven interest management for distributed scenarios.

Both are lazily created on first use (`GetOrCreateTriggerSystem` / `GetOrCreateInterestSystem` on `SpatialIndexState`).

---

## See also

- [01-foundation](01-foundation.md) — `OlcLatch` (used in R-Tree node headers), epoch model (every R-Tree mutation enters `EpochGuard`), `SpatialTypes` live in the sibling `Typhon.Schema.Definition` project (§9)
- [03-indexing](03-indexing.md) — B+Tree contrast: same OLC philosophy, but B+Tree uses a two-phase `SpinWriteLock` tuned for longer lock holds (the R-Tree uses a plain `SpinWait` loop instead)
- [06-ecs](06-ecs.md) — cluster storage and `ArchetypeClusterState`, where per-archetype spatial bookkeeping (cluster AABBs, cell links, migration flags) lives
- [09-querying](09-querying.md) — `EcsQuery` and the dispatch from `WhereInAABB` / `WhereNearby` / `WhereRay` to the right spatial path
- [12-observability](12-observability.md) — spatial span families (`Spatial:RTree:*`, `Spatial:Query:*`, `Spatial:Maintain:*`)
