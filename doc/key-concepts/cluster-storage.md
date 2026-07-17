---
uid: concept-cluster-storage
title: 'Cluster storage'
description: 'Batched SoA storage that packs N same-archetype entities contiguously, turning per-entity hashmap + page-fetch lookups into sequential array scans. Automatic and implicit — your component storage-mode mix decides whether an archetype is clustered, and it changes bulk-iteration cost by ~50×.'
---

# Cluster storage

> **In one line:** N same-archetype entities packed contiguously in **batched SoA** — sequential array scans instead of per-entity hashmap + page-fetch lookups. **You never switch it on; your storage-mode mix does.**

Per-entity storage pays a hash-map lookup plus a scattered page fetch for *every* component of *every* entity, *every* tick — at 100K+ entities that indirection, not your logic, dominates the cost. Cluster storage (the engine calls it *Entity Clusters*) packs N entities (8–64, auto-sized per [archetype](xref:concept-archetype) to fill a page) into one contiguous chunk, each component laid out as its own packed array — `Position[N]`, `Velocity[N]`, … . Bulk iteration becomes a linear scan the hardware prefetcher loves; random `Open`/`OpenMut` still works unchanged, transparently resolving to the same cluster slot.

> ⚠️ **This is the highest-leverage *invisible* decision in the data model.** An archetype is clustered **iff it has at least one [`SingleVersion` or `Transient`](xref:concept-storage-mode) component**; a pure-`Versioned` archetype stays on the legacy per-entity path. There is no opt-in and no opt-out — and the two paths perform *nothing alike*:

| Your archetype's components | Storage path | Bulk-iteration cost (100K, Zen 4) |
|---|---|---|
| **≥ 1 `SingleVersion` or `Transient`** | **Clustered** (batched SoA) | **~2.7 ns/entity** |
| all `Versioned` (pure) | legacy per-entity | ~134 ns/entity — **~50× slower** |

So adding a single `SingleVersion` field to an all-`Versioned` archetype can make bulk iteration **~50× faster** and shrink the working set (19.2 MB → 2.5 MB — L3 down to L2). Because nothing in your code says "cluster," you have to *know* this to design for it — hence this page. *(Fine print: a `Transient` component carrying an `[Index]` field stays on the legacy path.)*

Guarantees are untouched — MVCC visibility, B+Tree / spatial indexes, and all three [storage modes](xref:concept-storage-mode) behave exactly as before; a `Versioned` component keeps its HEAD in the cluster and its revision chain separate. Two things to know when you use the bulk path: direct span writes (`GetSpan`) **bypass dirty tracking**, so you must call `MarkCurrentDirty()` or the write never reaches the WAL/checkpoint; and clustering trades a slightly larger checkpoint stride (~1.6×) for the iteration win.

## How it relates

- **[Archetype](xref:concept-archetype)** — the unit that clusters; eligibility is a property of its component *set*, decided once at registration.
- **[Storage mode](xref:concept-storage-mode)** — the deciding input: one `SingleVersion`/`Transient` component flips the whole archetype to clustered.
- **[Page cache & paged store](xref:concept-page-cache)** — clusters are the *layout within* the paged store's pages, not a separate store.
- **[Spatial index](xref:concept-spatial-index)** — an archetype with a `[SpatialIndex]` field additionally packs clusters by grid cell (spatially-coherent clustering).

## In the API

- **Bulk path:** [`GetClusterEnumerator()`](xref:Typhon.Engine.EntityAccessor.GetClusterEnumerator*) → [`ClusterRef<TArch>`](xref:Typhon.Engine.ClusterRef`1), with [`GetSpan<T>`](xref:Typhon.Engine.ClusterRef`1.GetSpan*) / [`GetReadOnlySpan<T>`](xref:Typhon.Engine.ClusterRef`1.GetReadOnlySpan*) and [`OccupancyBits`](xref:Typhon.Engine.ClusterRef`1.OccupancyBits) for branch-free per-slot access; flag writes with `MarkCurrentDirty()` or `MarkSlotDirty(slot)`.
- **Random path:** `Open` / `OpenMut` — unchanged and transparently cluster-backed; no code change needed to benefit.
- The layout itself is an internal subsystem — you interact with it only through these accessors, never by allocating or sizing a cluster.

## Learn & use

- **Feature detail:** [Entity Clusters (Batched SoA Storage)](xref:feature-ecs-entity-clusters) — sizing, eligibility, dirty semantics, per-operation benchmarks
- **Narrative:** [Guide ch.2 — modeling](xref:guide-modeling) (the storage-mode choice that silently flips it) · [ch.5 — systems](xref:guide-systems) (bulk iteration over clusters)
