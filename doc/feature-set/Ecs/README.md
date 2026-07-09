---
uid: feature-ecs-index
title: 'Ecs'
description: 'Typhon''s archetype-based Entity-Component-System is the primary application-facing data model and read/write path: structured 64-bit entity identity, C#…'
---

# Ecs
> Typhon's archetype-based Entity-Component-System is the primary application-facing data model and read/write path: structured 64-bit entity identity, C# archetype classes, and typed zero-copy `Comp<T>` handles resolve any entity's components in O(1). Each component type independently picks a storage mode — `Versioned` (full MVCC), `SingleVersion` (tick-fence durable), or `Transient` (heap-only) — with a `Committed` discipline escalation for atomic zero-loss writes without a revision chain, layered under a three-tier query/reactive-view system, typed entity relationships with cascade delete, and a batched cluster storage engine that turns bulk iteration into ~50x-faster sequential array scans.

> 🔬 **Recommended:** read [in-depth-overview/06-ecs.md](../../in-depth-overview/06-ecs.md) (Chapter 06: ECS) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Entity & Archetype Model](entity-archetype-model.md) | Structured 64-bit `EntityId`, C# class-hierarchy archetypes, and typed `Comp<T>` handles resolving entities to component slots in O(1) | ✅ Implemented | 🟢 Start Here |
| [Entity Lifecycle & CRUD API](entity-lifecycle-crud/README.md) | Zero-copy `EntityRef` accessor for Spawn, Open, Read, Write, Destroy, and Enable/Disable — the sole entity manipulation API | ✅ Implemented | 🟢 Start Here |
| &nbsp;&nbsp;↳ [Generated Multi-Component Accessors](entity-lifecycle-crud/generated-multi-component-accessors.md) | Source-generated zero-copy `Refs`/`MutRefs` structs reading or writing every archetype component in one call | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Batch & SoA Spawn](entity-lifecycle-crud/batch-soa-spawn.md) | Bulk entity creation — shared-value batches or per-entity SoA spans — amortizing per-call overhead across thousands of entities | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Enable/Disable Components](entity-lifecycle-crud/enable-disable-components.md) | O(1) per-component bit-flip toggle — data preserved, not freed, with its own MVCC snapshot isolation independent of the component's StorageMode | ✅ Implemented | 🔵 Core |
| [Storage Modes](storage-modes/README.md) | Per-component-type choice of durability/performance tier (`Versioned` / `SingleVersion` / `Transient`), declared via `[Component(StorageMode = ...)]`, under one `Read<T>()`/`Write<T>()` API | ✅ Implemented | 🟢 Start Here |
| &nbsp;&nbsp;↳ [Versioned](storage-modes/storage-mode-versioned.md) | Full MVCC snapshot isolation and zero-loss durability for data that can never be lost or read torn | ✅ Implemented | 🟢 Start Here |
| &nbsp;&nbsp;↳ [SingleVersion (Tick-Fence Durable)](storage-modes/storage-mode-singleversion.md) | In-place writes at near-zero cost, durable to the last completed game tick | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Transient (Heap-Only)](storage-modes/storage-mode-transient.md) | Heap-only component storage for scratch data that should never touch disk | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Committed Durability Discipline](storage-modes/storage-mode-committed.md) | Zero-loss, atomic commits on the `SingleVersion` layout — without paying for a `Versioned` revision chain | ✅ Implemented | 🟣 Advanced |
| [Query System (EcsQuery)](query-system.md) | Three-tier constraint evaluation (`ArchetypeMask`, `EnabledBits`, WHERE) with planner-chosen broad or targeted scan, indexed/opaque predicates, FK joins, and spatial filters (full query/view feature set catalogued under Querying) | ✅ Implemented | 🟢 Start Here |
| [Reactive Views (EcsView)](reactive-views.md) | Persistent, incrementally-maintained query results with ring-buffer delta tracking across Incremental, OR, and Pull refresh modes (canonical doc lives under Querying) | 🚧 Partial | 🔵 Core |
| [Entity Relationships](entity-relationships.md) | Typed `EntityLink<T>` entity references used to build 1:1, 1:N, and N:M relationships, with declarative cascade delete and FK indexing | ✅ Implemented | 🔵 Core |
| [Component Collections](component-collections.md) | Per-entity variable-length lists (owned data or entity-reference lists) via a shared buffer pool, in-place for `SingleVersion` and copy-on-write for `Versioned` | ✅ Implemented | 🔵 Core |
| [Schema Versioning & Migration](schema-versioning-migration.md) | Detects struct/archetype layout drift at database open and migrates data automatically or via your own functions | 🚧 Partial | 🔵 Core |
| [Entity Clusters (Batched SoA Storage)](entity-clusters.md) | GPU-inspired batched SoA storage that turns per-entity hashmap/page lookups into sequential array scans (~50x faster bulk iteration) | ✅ Implemented | 🟣 Advanced |

## Internal Features

*No internal-only engine machinery documented separately in this category — every feature file above is directly reached from application code (`DatabaseEngine`, `Transaction`, `EntityRef`, `Comp<T>`/attributes, or `EcsQuery`/`EcsView`).*