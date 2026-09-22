---
uid: feature-ecs-entity-clusters
title: 'Entity Clusters (Batched SoA Storage)'
description: 'GPU-inspired batched SoA storage that turns per-entity hashmap/page lookups into sequential array scans.'
---

# Entity Clusters (Batched SoA Storage)
> GPU-inspired batched SoA storage that turns per-entity hashmap/page lookups into sequential array scans.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Ecs](./README.md)

**Assumes:** [Storage Modes](./storage-modes/README.md)

## 🎯 What it solves

Per-entity storage pays a hash-map lookup plus a scattered page fetch for every component, on every entity, every tick. At 100K+ entities that indirection — not the actual computation — dominates iteration cost (profiling found 66% of per-entity time in lookup/fetch overhead) and blows the working set past L2/L3 cache. Entity Clusters eliminate that indirection for bulk iteration (movement systems, AI ticks, batch queries) while leaving random single-entity access, MVCC isolation, indexes, and all three storage modes working exactly as before.

## ⚙️ How it works (in brief)

Eligible archetypes auto-compute a cluster size N (8-64, chosen to maximize entities-per-page) and pack N same-archetype entities into one contiguous chunk: an occupancy bitmask, an enabled-bitmask per component, an entity-key array, then each component's data as a separate contiguous array (SoA) — `Component0[N], Component1[N], ...`. `ClusterEnumerator<TArch>` walks an archetype's active clusters; each `ClusterRef<TArch>` exposes `OccupancyBits` plus `GetSpan<T>`/`Get<T>` for direct, branch-free access to a component's slot array — no hash lookup, no per-component page fetch. Random access (`Open`/`OpenMut`) is unchanged at the API level: it transparently resolves through the EntityMap to the same cluster + slot. `SingleVersion` and `Versioned` components live in the cluster (Versioned stores its HEAD there, the revision chain stays separate); `Transient` components live in a parallel heap-backed segment with identical layout so the SoA pattern is uniform across all three modes. When an archetype also declares a `[SpatialIndex]` field, cluster membership gains a further constraint: [Spatially-Coherent Entity Clustering](../Spatial/spatial-coherent-clustering.md) additionally packs entities by grid cell, not just by archetype, so per-cell spatial operations (tier assignment, dormancy, broadphase) stay per-cluster instead of per-entity — this is what a "cluster migration" (mentioned in the tick fence docs) actually is: an entity crossing its cluster's cell boundary and being moved to a cluster for its new cell.

## 💻 Usage

```csharp
[Archetype]
public class Ant : Archetype<Ant>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Movement> Movement = Register<Movement>();
}

// Bulk iteration — the cluster-native path, ~50x faster than per-entity Open/OpenMut.
var ants = ctx.Accessor.For<Ant>();
using var clusters = ants.GetClusterEnumerator();
while (clusters.MoveNext())
{
    var cluster = clusters.Current;
    var positions = cluster.GetSpan(Ant.Position);
    var movements = cluster.GetReadOnlySpan(Ant.Movement);

    ulong bits = cluster.OccupancyBits;
    while (bits != 0)
    {
        int idx = BitOperations.TrailingZeroCount(bits);
        bits &= bits - 1;
        positions[idx].X += movements[idx].VX * ctx.DeltaTime;
        positions[idx].Y += movements[idx].VY * ctx.DeltaTime;
    }
    clusters.MarkCurrentDirty(); // required: flags the writes for WAL/checkpoint — the cluster path skips dirty tracking otherwise
}

// Random single-entity access — same archetype, same API, transparently cluster-backed.
var entity = ants.OpenMut(someAntId);
ref var pos = ref entity.Write(Ant.Position);
pos.X += 1;
```

## ⚠️ Guarantees & limits

- **Cluster size is auto-computed, not chosen by the caller**: N ∈ [8, 64] is picked per archetype to maximize entities-per-page; iteration code is identical for every N.
- **Every archetype is cluster-backed, and there is no opt-out**: eligibility is unconditional, the pure-`Versioned` archetype included — a cluster stores the `Versioned` HEAD in the slot and keeps the revision chain separate, exactly as it does for a mixed archetype. Being unconditional is what makes exactly one spatial index possible; an opt-out would keep a second index home alive for every consumer to branch on.
- **Direct `GetSpan`/`Get` writes bypass dirty tracking**: call `MarkCurrentDirty()` (whole cluster) or `MarkSlotDirty(slot)` (single entity) after writing, or the change never reaches the WAL/checkpoint. Writing `Versioned` components through `GetSpan` is rejected by design (`Debug.Assert`) — use `OpenMut`/`Write` for those, which still goes through the revision chain.
- **Measured impact** (100K entities, 2-component archetype): per-entity cost 134 ns → ~2.7 ns (~50x), tick time ~10x, working set 19.2 MB → 2.5 MB (L3 → L2).
- **Random access stays correct, not just fast**: MVCC visibility (`BornTSN`/`DiedTSN`), B+Tree/spatial indexes, and `EnabledBits` are all maintained per entity; `Open`/`OpenMut` need no code changes to benefit.
- **Trade-offs are real, not hidden**: checkpoint I/O is ~1.6x larger (bigger stride per page); highly selective queries pay a zone-map scan floor (~19 µs); schema evolution rebuilds every cluster in the archetype (~C× more expensive than per-component migration, an offline operation).
- **`EntityId` format is unchanged** — clusters are a storage detail; entity identity, serialization, and WAL/network formats are unaffected.

## 🧪 Tests

- [ClusterStorageTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ClusterStorageTests.cs) — cluster sizing/cache-line-aligned stride, `GetClusterEnumerator` bulk iteration, random-access read/write/destroy through the cluster
- [ClusterDirtyTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ClusterDirtyTests.cs) — `MarkCurrentDirty`/per-slot dirty-bitmap semantics, tick-fence snapshot clearing, writes lost if dirty isn't marked
- [ClusterVersionedTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ClusterVersionedTests.cs) — `Versioned` HEAD stored in the cluster with the revision chain kept separate, bulk iteration seeing the latest HEAD after a write

## 🔗 Related

- Sibling: [Storage Modes](./storage-modes/README.md) — clustered component data still splits by `Versioned`/`SingleVersion`/`Transient` mode, just batched
- Sibling: [Spatially-Coherent Entity Clustering](../Spatial/spatial-coherent-clustering.md) — for archetypes with a `[SpatialIndex]` field, additionally constrains cluster membership to one grid cell, and defines cluster migration
- Sibling: [Cluster Spatial Queries](../Spatial/cluster-spatial-queries.md) — indexes cluster bounding boxes instead of per-entity, exploiting this same batched layout
- Sibling: [Spatial Tiers & Adaptive Dispatch](../Runtime/spatial-tiers-adaptive-dispatch/README.md) — per-cluster simulation tiers dispatch over this storage layer

<!-- Deep dive: claude/design/Ecs/EntityClusters/README.md (overview, sizing formula, eligibility, recovery/persistence) -->
<!-- Deep dive: claude/design/Ecs/EntityClusters/01-data-layout.md (byte layout, page geometry, occupancy/enabled bit semantics) -->
<!-- Deep dive: claude/design/Ecs/EntityClusters/04-entity-mapping.md (EntityMap evolution, random access path, ClusterLocation encoding) -->
<!-- Deep dive: claude/design/Ecs/EntityClusters/10-api-surface.md (public API surface, source-generated accessors) -->
<!-- ADR: 045-entity-cluster-storage (claude/adr/045-entity-cluster-storage.md) -->
