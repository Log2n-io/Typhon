---
uid: concept-page-cache
title: 'Page cache & paged store'
description: 'Persistent components live in a memory-mapped, paged store with a bounded cache. The cache size bounds the resident working set, not capacity — the database can far exceed RAM, with cold pages paged in on demand.'
---

# Page cache & paged store

> **In one line:** persistent data lives in a **memory-mapped, paged store** with a **bounded cache** — the working set is resident, everything else lives on disk and pages in on demand.

You never allocate a page or write a save file — declaring a [component](xref:concept-component) is the whole interaction. The key property: **`DatabaseCacheSize` bounds the resident working set, not how much you can store.** The on-disk database can be many times the cache; cold pages (persistent data, indexes, the entity map) page out and back in on demand — entity count and data size scale with *disk*, not RAM. This is the SQL/SQLite model, and it's what separates Typhon from in-memory ECS frameworks.

The one exception is [`Transient`](xref:concept-storage-mode) components — RAM-only scratch by design, never paged to disk. The default cache is **256 MiB** — a production-sane working set for a single engine; size `DatabaseCacheSize` up for larger workloads and let the engine self-manage eviction. (An 8 MiB minimum exists for tests that deliberately exercise the paging machinery under pressure.)

## How it relates

- **[DatabaseEngine](xref:concept-database-engine)** — owns and budgets the cache; you set the caps.
- **[Storage mode](xref:concept-storage-mode)** — `Versioned`/`SingleVersion` page to disk; `Transient` stays resident.
- **[WAL & checkpoint](xref:concept-wal-checkpoint)** — the checkpoint drains dirty pages from the cache to the data file.

## In the API

- Sized through [`DatabaseEngineOptions`](xref:Typhon.Engine.DatabaseEngineOptions) ([`Resources`](xref:Typhon.Engine.DatabaseEngineOptions.Resources), cache size) and the managed paged-file options; the store itself is an internal subsystem, not a type you call.

## Learn & use

- **Narrative:** [Guide ch.6 §2 — resource budgets](xref:guide-operating) · [ch.2 §5 — what the engine does for you](xref:guide-modeling)
- **Feature detail:** [storage](xref:feature-storage-index) · [page cache](xref:feature-storage-page-cache)
