---
uid: concept-cluster-storage
title: 'Cluster storage'
description: 'Batched SoA storage that packs N same-archetype entities contiguously, turning per-entity hashmap + page-fetch lookups into sequential array scans. Every archetype is cluster-backed; a component''s storage mode decides where its array lives inside the cluster.'
---

# Cluster storage

> **In one line:** N same-archetype entities packed contiguously in **batched SoA** — sequential array scans instead of per-entity hashmap + page-fetch lookups. **Every archetype gets it; there is nothing to switch on.**

Per-entity storage pays a hash-map lookup plus a scattered page fetch for *every* component of *every* entity, *every* tick — at 100K+ entities that indirection, not your logic, dominates the cost. Cluster storage (the engine calls it *Entity Clusters*) packs N entities (8–64, auto-sized per [archetype](xref:concept-archetype) to fill a page) into one contiguous chunk, each component laid out as its own packed array — `Position[N]`, `Velocity[N]`, … . Bulk iteration becomes a linear scan the hardware prefetcher loves; random `Open`/`OpenMut` resolves through the EntityMap to the same cluster slot.

> 📌 **Every archetype is cluster-backed**, and that is not something you configure — no opt-in, no opt-out, and nothing in your component mix can put an archetype on a different path. What a component's [storage mode](xref:concept-storage-mode) decides is not *whether* you get a cluster but *where that component's array lives inside it*:

| Component's storage mode | Where its array lives | What that gives you |
|---|---|---|
| `SingleVersion` | in the cluster chunk | packed SoA, read and written straight from the span |
| `Versioned` | HEAD in the cluster chunk, revision chain kept separate | MVCC snapshot isolation; reading any version but the HEAD leaves the packed array |
| `Transient` | a parallel segment with the same SoA layout | the same iteration pattern, with no page-cache backing and no persistence |

Reads behave the same whichever way you iterate — MVCC visibility, B+Tree and spatial indexes. Writes differ in one way: direct span writes (`GetSpan` / `Get`) **bypass WAL-record emission and change detection**, so you must call `MarkDirty(comp)` (or `MarkCurrentDirty()`) or the write will not appear in the WAL and will be invisible to reactive systems. The page is recorded for the next checkpoint automatically, so the write survives a checkpoint but is not crash-safe in the gap between checkpoints without a WAL record. A `SingleVersion` or `Transient` component with an `[Index]` field cannot be written through a span at all — `GetSpan` / `Get` throw on it, because the index moves an entity's entry from the key recorded at its write and a span records nothing. Read it with `GetReadOnlySpan` and write it per entity with `ClusterRef.Set(comp, slot, value)` — or per cluster with `ClusterRef.Set(comp, slotMask, values)` — which does the bookkeeping (old keys, dirty mark) in the same call — or with `OpenMut(...).Set(...)`, the only path for a component that defaults to the Commit discipline or a transaction running under it, whose writes are staged.

## How it relates

- **[Archetype](xref:concept-archetype)** — the unit that clusters; its cluster size is auto-chosen once at registration to fill a page.
- **[Storage mode](xref:concept-storage-mode)** — decides where each component's array sits inside the cluster, not whether there is one.
- **[Page cache & paged store](xref:concept-page-cache)** — clusters are the *layout within* the paged store's pages, not a separate store.
- **[Spatial index](xref:concept-spatial-index)** — an archetype with a `[SpatialIndex]` field additionally packs clusters by grid cell (spatially-coherent clustering).

## In the API

- **Bulk path:** [`GetClusterEnumerator()`](xref:Typhon.Engine.EntityAccessor.GetClusterEnumerator*) → [`ClusterRef<TArch>`](xref:Typhon.Engine.ClusterRef`1), with [`GetSpan<T>`](xref:Typhon.Engine.ClusterRef`1.GetSpan*) / [`GetReadOnlySpan<T>`](xref:Typhon.Engine.ClusterRef`1.GetReadOnlySpan*) and [`OccupancyBits`](xref:Typhon.Engine.ClusterRef`1.OccupancyBits) for branch-free per-slot access; flag writes with `MarkCurrentDirty()` or `MarkSlotDirty(slot)`.
- **Random path:** `Open` / `OpenMut` — transparently cluster-backed; it resolves to a cluster slot without any work on your side.
- The layout itself is an internal subsystem — you interact with it only through these accessors, never by allocating or sizing a cluster.

## Learn & use

- **Feature detail:** [Entity Clusters (Batched SoA Storage)](xref:feature-ecs-entity-clusters) — sizing, layout, dirty semantics, per-operation benchmarks
- **Narrative:** [Guide ch.2 — modeling](xref:guide-modeling) (choosing a storage mode) · [ch.5 — systems](xref:guide-systems) (bulk iteration over clusters)
