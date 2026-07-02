# Spatial R-Tree Index
> Page-backed R-Tree attached to a component field, giving sub-microsecond AABB/radius/ray queries shared across every archetype that uses it.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Spatial](../README.md)

## 🎯 What it solves

Any component holding a position or bounds needs spatial answers — "what's near this point", "what's inside this box" — and a linear scan over every entity falls apart well before 10K entities at 60 Hz. A B+Tree field index doesn't help either: it orders on a scalar key, not on 2D/3D overlap. The R-Tree gives a component field a real spatial index: O(log n) descent instead of O(n) scan, durable across crashes and restarts like any other Typhon structure, and shared by every archetype that happens to use the component — no per-archetype index duplication.

## ⚙️ How it works (in brief)

Annotate a component field holding an `AABB2F`/`AABB3F`/`BSphere2F`/`BSphere3F` (or the `f64` equivalents) with `[SpatialIndex]`. The engine infers the field's spatial type, picks the matching tree variant (2D/3D × f32/f64), and builds one R-Tree per `ComponentTable` — not per archetype, so entities of different archetypes sharing the component land in the same tree. Each tree node is a single page-backed chunk (512 B for 2D/3D-f32, 768 B for 3D-f64) holding 12–24 entries depending on variant, so a handful of page reads resolve any query. Entries store a *fat* AABB — the tight bounds enlarged by `margin` — so small moves are absorbed without touching the tree; only a margin-escaping move costs a real remove+reinsert (see [Fat-AABB Incremental Update](../fat-aabb-update.md)). A back-pointer per entity gives O(1) leaf lookup on update/remove, and all node access goes through Optimistic Lock Coupling — readers never block writers, a concurrent mutation mid-traversal just restarts the descent. An optional Layer-1 coarse occupancy hashmap (`cellSize > 0`) rejects entirely-empty query regions before the tree is touched at all.

## Sub-features

| Sub-feature | Use it when... |
|---|---|
| [Static / Dynamic Tree Separation](./spatial-rtree-static-dynamic.md) | A component's spatial data is either rarely-moving (terrain, buildings, fixed triggers) or moves every tick (units, projectiles), and you want each to pay only its own maintenance cost |

## ⚠️ Guarantees & limits

- **Crash-safe** — tree mutations ride the same WAL/tick-fence durability path as the component's storage mode (tick fence for SingleVersion, transaction commit for Versioned); no separate persistence story to manage.
- **Zero boot cost** — the tree lives in the database file; reopening a database does not rebuild it.
- **No MVCC** — the tree always reflects current/latest-committed state, regardless of the component's storage discipline. Not available on `Transient` components (registration fails at schema time).
- **One tree per ComponentTable, not per archetype** — every archetype that shares the spatially-indexed component is queried by a single tree traversal; archetype filtering on results is a cheap bitmask check, not a separate index probe.
- **Query completeness is guaranteed** — every entity whose geometry matches a query is in the result set; node MBRs are refit on every mutation to keep this true (false positives at the leaf/fat-AABB level are expected and filtered by the caller; false negatives are not).
- **Fixed fanout per variant** — 20 (2D f32) / 14 (3D f32) / 12 (2D f64) / 13 (3D f64) leaf entries per node; not configurable per component.
- **Lazy underflow** — nodes below minimum fill are tolerated, not merged; only fully empty leaves are reclaimed. Slightly higher storage overhead under heavy churn, no correctness impact.
- **~600 µs total spatial budget** at 100K entities / 60 Hz (update + 100 queries/tick) — roughly 3.6% of a 16 ms frame.
- At most one `[SpatialIndex]` field per component, and exactly one R-Tree variant is selected for it at registration time — switching field type later means re-registering the schema, not a runtime reconfiguration.

## 🧪 Tests

- [SpatialRTreeTests](../../../../test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialRTreeTests.cs) — insert/split/remove correctness across all 4 variants, AABB query vs brute force, category-mask pruning
- [SpatialNodeDescriptorTests](../../../../test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialNodeDescriptorTests.cs) — node capacities/header sizes/SOA layout match the design doc, per variant
- [SpatialRTreeBulkTests](../../../../test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialRTreeBulkTests.cs) — many sequential inserts, `TreeValidator` structural invariant checks

## 🔗 Related

- Related catalog entry: [Field Attribute & Schema Integration](../spatial-field-attribute/README.md) (the `[SpatialIndex]` attribute that configures this index)
- Related catalog entry: [Spatial Query API](../spatial-query-api.md) and [Querying / Spatial Query Predicates](../../Querying/spatial-predicates.md) (the algorithms that run against this index)
- Related catalog entry: [Fat-AABB Incremental Update](../fat-aabb-update.md) (per-tick maintenance mechanics)
- Sub-features: [Static / Dynamic Tree Separation](./spatial-rtree-static-dynamic.md)
- Overview: [Spatial Architecture Overview](../spatial-architecture-overview.md) — clarifies that the Layer-1 occupancy hashmap below is not the engine-wide spatial grid

<!-- Deep dive: claude/design/Spatial/SpatialIndex/01-architecture.md (two-layer design, storage-mode compatibility, archetype convergence) -->
<!-- Deep dive: claude/design/Spatial/SpatialIndex/02-node-layout.md (SOA node layout, variant capacities) -->
<!-- Deep dive: claude/design/Spatial/SpatialIndex/03-tree-operations.md (insert/split/remove, fat-AABB protocol, correctness invariants) -->
<!-- Rules: claude/rules/spatial.md (R-Tree structure, query, fat-AABB invariants) -->
<!-- ADR: claude/adr/044-spatial-rtree-architecture.md -->
