---
uid: feature-index
title: 'Typhon Feature Catalog'
description: 'Documentation for every feature in src/Typhon.Engine, tagged Public (usable by application developers embedding Typhon) or Internal (engine machinery, for…'
---

# Typhon Feature Catalog

> Documentation for every feature in `src/Typhon.Engine`, tagged **Public** (usable by application developers embedding Typhon) or **Internal** (engine machinery, for contributors). Each entry covers what it's for, what problem it solves, and how it works. Scoped to the engine itself — Workbench, TyphonShell, and Patate (apps built on Typhon) are out of scope.

> 📖 **New to Typhon?** This catalog is the *reference* — every feature, one page each, organized for lookup. If you want to *learn* Typhon end-to-end, start with the **[User Guide](../guide/README.md)** instead: a 6-chapter, read-as-you-go tutorial with a runnable example project. Come back here once you know what you're looking for.

> 🔬 **Want to understand *why*, not just *how*?** This catalog tells you what each feature does and how to use it — not the mechanism underneath. The **[In-Depth Overview](../in-depth-overview/README.md)** goes deeper: structures, invariants, and design trade-offs. That's useful whether you're embedding Typhon and want to reason about what it actually guarantees (durability, MVCC, the runtime model), or reading the engine's code for the first time — it's not contributor-only. A 14-chapter reference mirroring `src/Typhon.Engine/`'s folder layout; each category README below links to its corresponding chapter.

**Who it's for:** application developers embedding Typhon get a task-oriented "what exists and how do I use it" reference — the [Public](#public--what-you-can-use) index is a complete, self-contained reading list; stop there. Engine contributors who need the machinery behind those features continue into [Internal](#internal--engine-internals-for-contributors).

**Learning level:** every Public feature page is tagged 🟢 **Start Here** (needed for the simplest working app), 🔵 **Core** (what most production apps reach for), or 🟣 **Advanced** (specific use cases only) — see its badge line. Category tables below are ordered accordingly, easiest first.

**How to navigate:** skim the [category table](#categories) below — its Scope column flags whether a category is mostly Public, Internal-only, or a real mix of both — then drill into a category's README for its full feature list, or a feature page for usage details and code samples. Or jump straight to [Public](#public--what-you-can-use) or [Internal](#internal--engine-internals-for-contributors) below and Ctrl-F for a feature name.

## Categories

| Category | What it covers | Scope | README |
|---|---|---|---|
| Foundation | Lock-free primitives, epoch memory safety, deadlines/timeouts, pinned allocators, and hash maps every other layer is built on. | Mixed | [→](Foundation/README.md) |
| Storage | The memory-mapped page cache, allocation hierarchy (occupancy → segments → chunks), and pluggable backends underlying every persisted structure. | Mixed | [→](Storage/README.md) |
| Durability | WAL v2, the append-before-publish commit pipeline, checkpointing, and crash recovery — Typhon's crash-safety guarantees. | Mixed | [→](Durability/README.md) |
| Schema | Attribute-driven component/field declaration, FieldId stability, validation, and automatic/user-defined schema evolution. | Mixed | [→](Schema/README.md) |
| Revision | The per-component MVCC revision-chain subsystem — storage, snapshot visibility, conflict baselines, and GC/crash scrub. | Mixed | [→](Revision/README.md) |
| Ecs | The archetype-based entity/component data model — CRUD, storage modes, queries, views, relationships, collections, clusters. | Public | [→](Ecs/README.md) |
| Indexing | Concurrent B+Tree secondary indexes — key-width variants, lookup/range scan, versioned (MVCC) index history, diagnostics. | Mixed | [→](Indexing/README.md) |
| Spatial | R-Tree spatial indexing, spatial query predicates, trigger volumes, and cluster-based tiered simulation dispatch. | Mixed | [→](Spatial/README.md) |
| Querying | The fluent query builder, execution planning, statistics, and incrementally-refreshed persistent Views. | Public | [→](Querying/README.md) |
| Transactions | The three-tier execution model (Engine → UoW → Transaction) — durability modes/discipline, commit/rollback, conflict resolution. | Public | [→](Transactions/README.md) |
| Subscriptions | Server-driven, View-based client state replication over TCP — published Views, delta encoding, wire transport. | Public | [→](Subscriptions/README.md) |
| Runtime | The DAG-scheduled tick loop that dispatches systems — scheduling, system types, spatial-tier dispatch, overload management. | Public | [→](Runtime/README.md) |
| Resources | The runtime resource graph tracking every engine resource's metrics, budgets, snapshots, and exhaustion handling. | Mixed | [→](Resources/README.md) |
| Observability | Zero-overhead telemetry gating, distributed tracing, OpenTelemetry metrics export, and health/alerting. | Public | [→](Observability/README.md) |
| Errors | The unified exception hierarchy, error codes, timeouts, resource-exhaustion handling, and the zero-allocation Result type. | Public | [→](Errors/README.md) |
| Profiler | The embedded, zero-allocation typed-event profiler and its trace export/inspection tooling. | Mixed | [→](Profiler/README.md) |
| Hosting | DI extension methods to bootstrap a DatabaseEngine and its services into an IServiceCollection. | Public | [→](Hosting/README.md) |

## Public — What You Can Use

Every Public feature, one line each — the application-facing surface, complete on its own. Sub-features are indented under their parent with `↳`.

### Foundation

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Deadline & Timeout Propagation | Monotonic absolute-deadline timeouts bundled with cooperative cancellation, threaded through every Unit-of-Work via the 24-byte UnitOfWorkContext to eliminate timeout accumulation across nested calls. | ✅ Implemented | 🔵 Core | [→](Foundation/deadline-timeout-propagation.md) |

### Storage

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Transient Store (heap-backed) | Pinned heap blocks standing in for the page cache, so Transient components get raw-memory speed through the same segment code — tune via TransientOptions. | ✅ Implemented | 🔵 Core | [→](Storage/pluggable-storage-backend/transient-store.md) |
| Database File Locking & Lifecycle | Two-layer protection against concurrent multi-process opens — OS FileShare.Read plus an advisory .lock sidecar with stale/live/cross-machine PID detection — plus create/open/delete lifecycle handling. | ✅ Implemented | 🔵 Core | [→](Storage/file-locking-lifecycle.md) |
| Memory-Mapped Page Cache & Clock-Sweep Eviction | 8 KiB pages, 4-state lifecycle, clock-sweep eviction with sequential-allocation optimization, async I/O, and backpressure handling. | ✅ Implemented | 🟣 Advanced | [→](Storage/page-cache.md) |
| Page Integrity — CRC32C, Seqlock Snapshots & A/B Page Pairing | Hardware CRC32C page checksums, seqlock-protected checkpoint snapshots, and A/B slot pairing for structural pages that can't be rebuilt. | ✅ Implemented | 🟣 Advanced | [→](Storage/page-integrity.md) |
| Variable-Sized Buffer Storage (VSBS) | Linked-chunk-chain storage for variable-length, reference-counted buffers — backs multi-value B+Tree index entries and per-element-type ComponentCollection\<T\> pools. | ✅ Implemented | 🟣 Advanced | [→](Storage/vsbs.md) |
| Storage Introspection & Integrity Diagnostics | Read-only APIs exposing segment/page topology and auditing occupancy-vs-segment consistency, powering the Workbench Database File Map. | ✅ Implemented | 🟣 Advanced | [→](Storage/storage-introspection.md) |
| Page Compression (Future) | Planned LZ4-style compression adapter for cold/historical data, string-heavy tables, and backups — deliberately not implemented in v1 so hot real-time paths stay within microsecond latency targets. | 📋 Planned | 🟣 Advanced | [→](Storage/page-compression-future.md) |

### Durability

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Durability Modes | Per-Unit-of-Work control over when WAL records become crash-safe — pick latency vs. data-at-risk per workload. | ✅ Implemented | 🟢 Start Here | [→](Durability/durability-modes/README.md) |
| &nbsp;&nbsp;↳ Committed Durability Discipline | Zero-loss, atomic writes on Typhon's cheapest component layout (SingleVersion) without paying for an MVCC revision chain. | ✅ Implemented | 🟣 Advanced | [→](Durability/durability-modes/committed-discipline.md) |
| Write-Ahead Log (WAL v2 logical records) | The single source of durability truth: logical (EntityId, ComponentTypeId) records, one codec, a sequential CRC-chained log. | ✅ Implemented | 🟣 Advanced | [→](Durability/wal-v2.md) |
| Commit Pipeline (append-before-publish) | Transaction.Commit's VALIDATE→PREPARE→BUILD→APPEND→PUBLISH→WAIT ordering guarantees nothing is visible before its WAL record is appended, and publish never rolls back. | ✅ Implemented | 🟣 Advanced | [→](Durability/commit-pipeline.md) |
| Checkpoint v2 (SnapshotStore pipeline) | Background pipeline that consolidates dirty data pages into the data file, advances CheckpointLSN only over pages it actually wrote, and recycles WAL segments. | ✅ Implemented | 🟣 Advanced | [→](Durability/checkpoint-v2/README.md) |
| Crash Recovery (RecoveryDriver) | On open, scans the WAL's durably-committed prefix and replays it idempotently, in strict LSN order, through the engine's own write primitives. | ✅ Implemented | 🟣 Advanced | [→](Durability/crash-recovery/README.md) |
| Page Checksums & Seqlock Snapshots | CRC32C torn-page detection on every page, paired with a lock-free seqlock so checkpoints snapshot live pages without blocking writers. | ✅ Implemented | 🟣 Advanced | [→](Durability/page-checksums-seqlock.md) |
| BulkLoad Write Path | An opt-in, exclusive, throughput-first session API that skips per-row WAL and brackets the whole bulk with a BulkBegin/BulkEnd manifest pair plus a synchronous checkpoint barrier. | 🚧 Partial | 🟣 Advanced | [→](Durability/bulk-load.md) |
| Durability Health & Introspection | DurabilityHealth (Ok/Degraded/Fatal) and checkpoint/WAL-writer cycle counters via the Resource Graph let an operator observe the subsystem without reaching into internals. | ✅ Implemented | 🟣 Advanced | [→](Durability/durability-introspection.md) |
| Point-in-Time Incremental Backup | Forward-incremental .pack backups scoped to changed pages; restore reassembles a base and heals it through crash recovery's RecoveryDriver. | 📋 Planned | 🟣 Advanced | [→](Durability/pit-backup.md) |

### Schema

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Component & Field Declaration | Attribute-driven declaration of blittable component structs ([Component], [Field], [Index]) reflected into DBComponentDefinition at registration time. | ✅ Implemented | 🟢 Start Here | [→](Schema/component-field-declaration.md) |
| FieldId Stability | Persistent, name-based FieldId assignment (auto-assign once, match by name on reopen) so adding/removing/reordering fields never breaks index identity; PreviousName handles renames. | ✅ Implemented | 🔵 Core | [→](Schema/fieldid-stability.md) |
| Schema Validation (SchemaDiff) | On every reopen, diffs persisted vs. runtime schema, classifies every change by compatibility level, and fails loudly before any user transaction runs on unresolvable mismatches. | ✅ Implemented | 🔵 Core | [→](Schema/schema-validation.md) |
| Compatible Schema Evolution (Auto-Migration) | Automatically migrates entities at startup for field add/remove/reorder and lossless type widenings by allocating a new stride segment while preserving ChunkIds so indexes need no rebuild. | ✅ Implemented | 🔵 Core | [→](Schema/compatible-evolution/README.md) |
| &nbsp;&nbsp;↳ Migration Execution Strategy | Migration runs eagerly and synchronously at database open — before any user transaction — with progress events and an offline dry-run check. | ✅ Implemented | 🟣 Advanced | [→](Schema/compatible-evolution/migration-execution-strategy.md) |
| User-Defined Migration Functions | Register pure transform functions for breaking schema changes, with automatic multi-step chain resolution across revisions. | ✅ Implemented | 🟣 Advanced | [→](Schema/migration-functions.md) |
| Offline Schema Inspection & Dry-Run Validation | Read a database's persisted schema, or simulate a code upgrade against it, without opening it for real. | ✅ Implemented | 🟣 Advanced | [→](Schema/schema-inspection-dryrun.md) |
| Migration Progress Tracking | OnMigrationProgress event stream (Analyzing → AllocatingSegments → MigratingEntities → … → Complete) for observing long-running eager migrations in production. | ✅ Implemented | 🟣 Advanced | [→](Schema/migration-progress-tracking.md) |
| Schema History Audit Log | Append-only audit trail recording every applied schema change for production auditing, queried via dbe.GetSchemaHistory(). | ✅ Implemented | 🟣 Advanced | [→](Schema/schema-history-audit.md) |

### Revision

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Revision Append & Chain Growth | Every write to a Versioned component creates a new immutable revision instead of overwriting the old one — Spawn allocates, Write\<T\>() appends, Destroy tombstones. | ✅ Implemented | 🔵 Core | [→](Revision/revision-append-write-path.md) |
| MVCC Snapshot Visibility | Reads resolve to the latest revision committed at-or-before the reader's transaction TSN, with read-your-own-writes and explicit RevisionReadStatus outcomes. | ✅ Implemented | 🔵 Core | [→](Revision/mvcc-snapshot-visibility.md) |
| Write-Conflict Baseline Tracking | Every chain append records the new and prior revision as the comparison baseline used by commit-time conflict detection and ConcurrencyConflictHandlers. | ✅ Implemented | 🟣 Advanced | [→](Revision/optimistic-conflict-baseline.md) |
| Revision Garbage Collection & Compaction | Bounded-memory chain cleanup keyed off MinTSN, preserving a sentinel for in-flight readers and collapsing fully-dead chains to trigger entity removal. | ✅ Implemented | 🟣 Advanced | [→](Revision/revision-gc-compaction.md) |

### Ecs

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Entity & Archetype Model | Structured 64-bit entity identity, C# class-hierarchy archetypes, and typed zero-copy component handles — the schema backbone of every other ECS feature. | ✅ Implemented | 🟢 Start Here | [→](Ecs/entity-archetype-model.md) |
| Entity Lifecycle & CRUD API | Zero-copy EntityRef accessor for Spawn, Open, Read, Write, Destroy, Enable/Disable — the sole entity manipulation API. | ✅ Implemented | 🟢 Start Here | [→](Ecs/entity-lifecycle-crud/README.md) |
| &nbsp;&nbsp;↳ Generated Multi-Component Accessors | Source-generated zero-copy Refs/MutRefs structs reading or writing every archetype component in one call. | ✅ Implemented | 🔵 Core | [→](Ecs/entity-lifecycle-crud/generated-multi-component-accessors.md) |
| &nbsp;&nbsp;↳ Batch & SoA Spawn | Bulk entity creation — shared-value batches or per-entity SoA spans — amortizing per-call overhead across thousands of entities. | ✅ Implemented | 🔵 Core | [→](Ecs/entity-lifecycle-crud/batch-soa-spawn.md) |
| &nbsp;&nbsp;↳ Enable/Disable Components | O(1) per-component bit-flip toggle — data preserved, not freed, with its own MVCC snapshot isolation independent of the component's StorageMode. | ✅ Implemented | 🔵 Core | [→](Ecs/entity-lifecycle-crud/enable-disable-components.md) |
| Storage Modes | Pick durability and write cost per component type — from microsecond ACID to nanosecond scratch memory, in one engine. | ✅ Implemented | 🟢 Start Here | [→](Ecs/storage-modes/README.md) |
| &nbsp;&nbsp;↳ Versioned | Full MVCC snapshot isolation and zero-loss durability for data that can never be lost or read torn. | ✅ Implemented | 🟢 Start Here | [→](Ecs/storage-modes/storage-mode-versioned.md) |
| &nbsp;&nbsp;↳ SingleVersion (Tick-Fence Durable) | In-place writes at near-zero cost, durable to the last completed game tick. | ✅ Implemented | 🔵 Core | [→](Ecs/storage-modes/storage-mode-singleversion.md) |
| &nbsp;&nbsp;↳ Transient (Heap-Only) | Heap-only component storage for scratch data that should never touch disk. | ✅ Implemented | 🔵 Core | [→](Ecs/storage-modes/storage-mode-transient.md) |
| &nbsp;&nbsp;↳ Committed Durability Discipline | Zero-loss, atomic commits on the SingleVersion layout — without paying for a Versioned revision chain. | ✅ Implemented | 🟣 Advanced | [→](Ecs/storage-modes/storage-mode-committed.md) |
| Query System (EcsQuery) | Three-tier constraint evaluation with planner-chosen broad or targeted scan, indexed predicates, FK joins, and spatial filters. | ✅ Implemented | 🟢 Start Here | [→](Ecs/query-system.md) |
| Reactive Views (EcsView) | Persistent, incrementally-maintained query results that refresh in microseconds instead of re-scanning every tick. | 🚧 Partial | 🔵 Core | [→](Ecs/reactive-views.md) |
| Entity Relationships | Typed EntityLink\<T\> references plus declarative cascade delete and reactive FK joins. | ✅ Implemented | 🔵 Core | [→](Ecs/entity-relationships.md) |
| Component Collections | Per-entity variable-length lists — owned data or entity-reference lists — without breaking fixed-size component layout. | ✅ Implemented | 🔵 Core | [→](Ecs/component-collections.md) |
| Schema Versioning & Migration | Detects struct/archetype layout drift at database open and migrates data automatically or via your own functions. | 🚧 Partial | 🔵 Core | [→](Ecs/schema-versioning-migration.md) |
| Entity Clusters (Batched SoA Storage) | GPU-inspired batched SoA storage that turns per-entity hashmap/page lookups into sequential array scans. | ✅ Implemented | 🟣 Advanced | [→](Ecs/entity-clusters.md) |

### Indexing

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Secondary Index Storage Modes | An indexed field is either unique or AllowMultiple; the choice drives the on-disk value representation and which mutation API path is used. | ✅ Implemented | 🔵 Core | [→](Indexing/secondary-index-storage-modes/README.md) |
| &nbsp;&nbsp;↳ Unique (Single-Value) Secondary Index | One key maps to exactly one entity — the B+Tree value is a chunk-id directly, no buffer indirection. | ✅ Implemented | 🔵 Core | [→](Indexing/secondary-index-storage-modes/unique-secondary-index.md) |
| &nbsp;&nbsp;↳ Multi-Value Secondary Index (AllowMultiple) | Many entities share one key — the B+Tree value is a growable HEAD buffer of chunk-ids, at a fixed +4-byte-per-entity cost. | ✅ Implemented | 🔵 Core | [→](Indexing/secondary-index-storage-modes/multi-value-secondary-index.md) |
| Lookup and Range-Scan Operations | Lock-free point lookups and ordered range scans over any secondary index, MVCC-correct at your transaction's snapshot. | ✅ Implemented | 🔵 Core | [→](Indexing/lookup-and-range-scan.md) |
| Index Handle Resolution (IndexRef) | Opaque, zero-allocation handle to a PK or secondary index, resolved once on the cold path via GetPKIndexRef/GetIndexRef and reused on the hot path with O(1) schema-evolution staleness checks. | ✅ Implemented | 🟣 Advanced | [→](Indexing/index-ref-resolution.md) |
| Versioned (HEAD/TAIL) Secondary Indexes for MVCC | AllowMultiple indexes maintain a HEAD buffer (current set) plus an append-only TAIL of version transitions so index membership stays correct across updates and deletes. | ✅ Implemented | 🟣 Advanced | [→](Indexing/versioned-secondary-indexes.md) |
| Transaction-Local Index Overlay (Read-Your-Own-Writes) | Planned per-transaction overlay so index lookups see that transaction's own uncommitted writes. | 📋 Planned | 🟣 Advanced | [→](Indexing/transaction-local-index-overlay.md) |

### Spatial

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Spatial Architecture Overview | Explains how the per-component R-Tree and the engine-wide spatial grid are two independent mechanisms, and which feature to read next. | ✅ Implemented | 🟢 Start Here | [→](Spatial/spatial-architecture-overview.md) |
| Field Attribute & Schema Integration | Declare a component field as spatially indexed via [SpatialIndex], validated against schema rules at registration time. | ✅ Implemented | 🔵 Core | [→](Spatial/spatial-field-attribute/README.md) |
| &nbsp;&nbsp;↳ Storage-Mode Compatibility (SingleVersion / Versioned) | The same [SpatialIndex] field works on both storage modes -- only when the tree catches up differs. | ✅ Implemented | 🔵 Core | [→](Spatial/spatial-field-attribute/spatial-storage-mode-compat.md) |
| Spatial Query API (AABB / Radius / Ray / Frustum / kNN / Count) | Query entry points over the per-component R-Tree: engine-internal SpatialQuery\<T\> plus the public fluent EcsQuery WhereNearby/WhereInAABB/WhereRay. | ✅ Implemented | 🔵 Core | [→](Spatial/spatial-query-api.md) |
| Spatial Grid Configuration & Tier Control | Engine-wide grid sizing plus the per-cell SimTier control surface for multi-resolution simulation. | ✅ Implemented | 🔵 Core | [→](Spatial/spatial-grid-config.md) |
| Static / Dynamic Tree Separation | A spatial field lands in one of two independent trees -- tick-fence-exempt static, or fat-AABB-maintained dynamic -- chosen once at schema time. | ✅ Implemented | 🟣 Advanced | [→](Spatial/spatial-rtree-index/spatial-rtree-static-dynamic.md) |
| Fat-AABB Incremental Update | Margin-enlarged bounds absorb small moves for ~25ns, with no tree mutation. | ✅ Implemented | 🟣 Advanced | [→](Spatial/fat-aabb-update.md) |
| Category Filtering | Bitmask pruning skips whole subtrees and clusters before geometry tests -- AND-conjunctive at the R-Tree, any-bit-overlap at the cluster broadphase. | ✅ Implemented | 🟣 Advanced | [→](Spatial/spatial-category-filtering.md) |
| Spatially-Coherent Entity Clustering | Every entity in a cluster shares one grid cell, so spatial bookkeeping is per-cluster, not per-entity. | ✅ Implemented | 🟣 Advanced | [→](Spatial/spatial-coherent-clustering.md) |
| Tiered Simulation Dispatch | One simulation tier per spatial cell, four dispatch frequencies, zero per-entity distance checks. | ✅ Implemented | 🟣 Advanced | [→](Spatial/tiered-simulation-dispatch.md) |
| Checkerboard Dispatch | Opt-in two-phase Red/Black cluster partitioning for systems that write across cell boundaries, dispatched as one DAG node with two internal phases. | ✅ Implemented | 🟣 Advanced | [→](Spatial/checkerboard-dispatch.md) |

> Note: Static/Dynamic Tree Separation is a sub-feature of the (Internal) Spatial R-Tree Index — see the [Internal](#internal--engine-internals-for-contributors) section below for the tree itself.

### Querying

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Fluent Query API & Predicate Parsing | Archetype-rooted fluent builder that parses C# lambdas into index-driven plans, with structural/enabled-bit constraints, OR disjunction, and FK navigation joins. | ✅ Implemented | 🟢 Start Here | [→](Querying/fluent-query-api/README.md) |
| &nbsp;&nbsp;↳ Indexed Field Predicates (WhereField) | Expression-parsed predicate that drives a targeted B+Tree scan and powers incrementally-maintained reactive views. | ✅ Implemented | 🔵 Core | [→](Querying/fluent-query-api/wherefield-indexed-predicate.md) |
| &nbsp;&nbsp;↳ Opaque Post-Filter Predicates (Where) | Arbitrary per-entity C# delegate evaluated after a broad archetype scan, for logic the index system can't express. | ✅ Implemented | 🟢 Start Here | [→](Querying/fluent-query-api/where-opaque-postfilter.md) |
| &nbsp;&nbsp;↳ OR Disjunction (DNF Predicates) | `\|\|` in a WhereField predicate, normalized to Disjunctive Normal Form and evaluated as independent branches. | ✅ Implemented | 🟣 Advanced | [→](Querying/fluent-query-api/or-disjunction.md) |
| &nbsp;&nbsp;↳ Foreign-Key Navigation Joins (L4) | Join across an entity-reference field — filter source entities by predicates on the target entity they point to. | ✅ Implemented | 🟣 Advanced | [→](Querying/fluent-query-api/fk-navigation-joins.md) |
| Result Ordering & Pagination | Sorted, paged query results driven directly off a B+Tree index scan — no full-scan-then-sort. | ✅ Implemented | 🔵 Core | [→](Querying/ordering-pagination.md) |
| Persistent Views — Incremental Refresh & Delta Tracking | TSN-anchored persistent Views (ToView()) refreshed via lock-free MPSC ring-buffer change capture at commit time, exposing Added/Removed/Modified deltas. | 🚧 Partial | 🔵 Core | [→](Querying/persistent-views.md) |
| Spatial Query Predicates | R-Tree-backed AABB, radius, and ray filters attached directly to a fluent ECS query. | ✅ Implemented | 🟣 Advanced | [→](Querying/spatial-predicates.md) |
| Execution Planning & Pipeline Execution | Picks the most selective index as the scan driver and streams results into the caller's collection. | ✅ Implemented | 🟣 Advanced | [→](Querying/execution-planning-pipeline.md) |
| Statistics Infrastructure (HLL / MCV / Histogram) | Background-maintained per-field statistics feeding the selectivity estimator, refreshed by a tunable polling worker thread. | ✅ Implemented | 🟣 Advanced | [→](Querying/statistics-infrastructure.md) |
| ViewFactory — Parameterized Queries & View Pooling | Reusable query templates with a Rent/Return view pool, to remove per-session view setup cost. | 📋 Planned | 🟣 Advanced | [→](Querying/view-factory-pooling.md) |
| Temporal Queries (Point-in-Time Read & Revision History) | Opt-in per-component history retention enabling reads of past state and full revision timelines. | 📋 Planned | 🟣 Advanced | [→](Querying/temporal-queries.md) |

### Transactions

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Unit of Work (durability boundary) | Middle tier of the three-tier API hierarchy — batches Transactions under a single flush/durability boundary, owning the shared ChangeSet, deadline, and UoW identity. | ✅ Implemented | 🟢 Start Here | [→](Transactions/unit-of-work.md) |
| Durability Modes (Deferred / GroupCommit / Immediate) | Per-UoW control of WAL flush timing — trade commit latency for the data-at-risk window on crash. | ✅ Implemented | 🟢 Start Here | [→](Transactions/durability-modes/README.md) |
| &nbsp;&nbsp;↳ Per-Transaction Durability Override | Escalate one critical operation to zero-loss durability — via a dedicated `Immediate` UoW / side-transaction — without raising the surrounding batch's mode (the `DurabilityOverride` enum is declared but not yet a `Commit` parameter). | ✅ Implemented | 🟣 Advanced | [→](Transactions/durability-modes/durability-override-escalation.md) |
| Transaction Creation Patterns | Three ways to obtain a Transaction — explicit UoW + CreateTransaction, single-shot quick transaction, or UoW-less read-only snapshot transaction. | ✅ Implemented | 🟢 Start Here | [→](Transactions/transaction-creation-patterns/README.md) |
| &nbsp;&nbsp;↳ Standard (UnitOfWork + CreateTransaction) | Open a UnitOfWork once and draw as many transactions from it as a batch needs, sharing one durability/flush boundary. | ✅ Implemented | 🟢 Start Here | [→](Transactions/transaction-creation-patterns/transaction-creation-standard.md) |
| &nbsp;&nbsp;↳ CreateQuickTransaction (single-shot, auto-dispose) | One call fuses a UnitOfWork and its one Transaction into a single disposable for single-shot writes. | ✅ Implemented | 🟢 Start Here | [→](Transactions/transaction-creation-patterns/transaction-creation-quick.md) |
| &nbsp;&nbsp;↳ CreateReadOnlyTransaction (snapshot reads) | A Transaction with no UnitOfWork, UoW ID, or ChangeSet at all, for pure-read MVCC-snapshot workloads. | ✅ Implemented | 🔵 Core | [→](Transactions/transaction-creation-patterns/transaction-creation-readonly.md) |
| SingleVersion Durability Discipline (TickFence / Commit) | Per-transaction knob, orthogonal to DurabilityMode, that picks how a SingleVersion write becomes durable. | ✅ Implemented | 🔵 Core | [→](Transactions/durability-discipline/README.md) |
| &nbsp;&nbsp;↳ TickFence Discipline (Default) | The default, lowest-cost SingleVersion write — durable at the next tick fence, not at commit. | ✅ Implemented | 🔵 Core | [→](Transactions/durability-discipline/durability-discipline-tickfence.md) |
| &nbsp;&nbsp;↳ Commit Discipline (Variant-A Staging) | Atomic, zero-loss SingleVersion writes — durable and visible together at Commit(), with no revision chain. | ✅ Implemented | 🟣 Advanced | [→](Transactions/durability-discipline/durability-discipline-commit.md) |
| Commit / Rollback Pipeline (ACID Commit Path) | Transaction.Commit/Rollback overloads implementing the append-before-publish commit pipeline with atomic conflict resolution and always-completing rollback. | ✅ Implemented | 🔵 Core | [→](Transactions/commit-rollback-pipeline.md) |
| Optimistic Concurrency Conflict Resolution | Pluggable ConcurrencyConflictHandler invoked per conflicting entity during commit, exposing four data views via ConcurrencyConflictSolver; default with no handler is last-writer-wins. | ✅ Implemented | 🔵 Core | [→](Transactions/optimistic-conflict-resolution.md) |
| Deadline & Cooperative Cancellation | An absolute deadline rides every transaction commit, propagating through every lock and aborting cleanly only before work starts. | ✅ Implemented | 🔵 Core | [→](Transactions/deadline-cancellation.md) |
| UoW Identity & Crash-Safe Recovery Boundary | Each UoW gets a 15-bit ID from a persistent, back-pressured UowRegistry; on crash, still-Pending UoW IDs are voided so their revisions become instantly invisible with no replay. | ✅ Implemented | 🟣 Advanced | [→](Transactions/uow-identity-crash-recovery.md) |
| Transaction Lifecycle, Thread Affinity & Pooling | Transaction is single-thread-affine with a fail-fast state machine; TransactionChain provides lock-free CAS-based creation, exclusive-lock removal, 16-instance pooling, and MinTSN tracking for MVCC garbage collection. | ✅ Implemented | 🟣 Advanced | [→](Transactions/transaction-lifecycle-pooling.md) |
| Bulk Load Session | Opt-in, exclusive write path that batches writes through a recycled Transaction and commits the whole load atomically via a checkpoint barrier. | ✅ Implemented | 🟣 Advanced | [→](Transactions/bulk-load-session.md) |

### Subscriptions

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Published Views | Register a query View as a subscribable target via TyphonRuntime.PublishView, as either one shared instance for all clients or a per-client factory. | ✅ Implemented | 🟢 Start Here | [→](Subscriptions/published-views/README.md) |
| &nbsp;&nbsp;↳ Shared Views | One View instance, refreshed and diffed once per tick, fanned out to every subscriber. | ✅ Implemented | 🔵 Core | [→](Subscriptions/published-views/shared-views.md) |
| &nbsp;&nbsp;↳ Per-Client Views | A Func\<ClientContext, ViewBase\> that builds a fresh, parameterized View for each subscriber. | ✅ Implemented | 🔵 Core | [→](Subscriptions/published-views/per-client-views.md) |
| Subscription Management (SetSubscriptions) | Atomic, idempotent, diff-based API to set a client's full subscription list each tick. | ✅ Implemented | 🟢 Start Here | [→](Subscriptions/subscription-management/README.md) |
| &nbsp;&nbsp;↳ Server-Driven Subscriptions (v1) | Game code calls SetSubscriptions whenever game state changes; the runtime applies the diff-based transition on the next tick. | ✅ Implemented | 🔵 Core | [→](Subscriptions/subscription-management/subscription-server-driven.md) |
| &nbsp;&nbsp;↳ Client-Initiated Subscriptions (v2) | Clients request their own subscription changes via an OnClientSubscriptionRequest callback, validated server-side before being applied. | 📋 Planned | 🟣 Advanced | [→](Subscriptions/subscription-management/subscription-client-initiated.md) |
| Client Connections & Lifecycle | TCP listener thread accepts sockets and assigns each a ConnectionId; ClientContext is the only handle game code touches. | ✅ Implemented | 🔵 Core | [→](Subscriptions/client-connections.md) |
| Per-Tick Delta Computation & Encoding | After WriteTickFence, the Output phase diffs published Views into Added/Removed/Modified and encodes only the changed component bytes. | ✅ Implemented | 🔵 Core | [→](Subscriptions/delta-computation/README.md) |
| &nbsp;&nbsp;↳ Component-Level Dirty Encoding (v1) | Modified entities send full bytes for each component whose chunk was dirty this tick; unchanged components are omitted. | ✅ Implemented | 🔵 Core | [→](Subscriptions/delta-computation/delta-encoding-component-dirty.md) |
| &nbsp;&nbsp;↳ Per-Field Dirty Encoding (v1.1) | Planned output-phase field diffing to shrink Modified payloads to only the bytes of fields that actually changed. | 📋 Planned | 🟣 Advanced | [→](Subscriptions/delta-computation/delta-encoding-per-field-dirty.md) |
| Subscription Server Configuration | Tunable knobs for the TCP subscription listener: port, max clients, send buffer capacity, backpressure threshold, sync batch size, ring buffer capacity. | ✅ Implemented | 🔵 Core | [→](Subscriptions/server-configuration.md) |
| Reference C# Client SDK | Typhon.Client connects over TCP, decodes TickDeltaMessages, and maintains a per-View local entity cache that application code reads directly. | ✅ Implemented | 🔵 Core | [→](Subscriptions/reference-client-sdk.md) |
| Published/System-Input View Separation Guard | Runtime throws if a published View doubles as a system input (or vice versa), since the View's MPSC delta ring buffer can only have one consumer. | ✅ Implemented | 🟣 Advanced | [→](Subscriptions/published-view-isolation.md) |
| TCP Transport & Wire Format | One length-prefixed, MemoryPack-serialized TickDeltaMessage per client per tick over TCP_NODELAY. | ✅ Implemented | 🟣 Advanced | [→](Subscriptions/wire-transport.md) |
| Incremental Sync | New subscriptions to large Views sync in tick-sized batches instead of one giant first delta. | ✅ Implemented | 🟣 Advanced | [→](Subscriptions/incremental-sync.md) |
| Backpressure & Resync Recovery | A full client send buffer drops one tick's delta and triggers an automatic full-state resync — never an unbounded queue. | ✅ Implemented | 🟣 Advanced | [→](Subscriptions/backpressure-resync.md) |
| Subscription Priority & Overload Throttling | Critical/Normal/Low priority per published View; under overload Normal/Low Views are throttled while Critical Views always go out. | ✅ Implemented | 🟣 Advanced | [→](Subscriptions/priority-overload-throttling.md) |
| Subscription Telemetry & Tracing | Per-tick OutputPhaseMs/DeltasPushed/OverflowCount counters plus a live per-tick Output-phase trace span. | 🚧 Partial | 🟣 Advanced | [→](Subscriptions/subscription-telemetry.md) |

### Runtime

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Tick-Based Execution Engine | TyphonRuntime — the top-level host that owns the tick loop, creates one UoW per tick and one Transaction per system automatically, and drives startup/crash-recovery/shutdown. | ✅ Implemented | 🔵 Core | [→](Runtime/tick-execution-engine/README.md) |
| &nbsp;&nbsp;↳ Execution Modes | Today: a fixed-timestep tick loop plus a single-threaded debug variant; event-driven and hybrid request/response modes are designed but not yet built. | 🚧 Partial | 🟣 Advanced | [→](Runtime/tick-execution-engine/execution-modes.md) |
| &nbsp;&nbsp;↳ Worker Pool & Threading Model | Core allocation between a dedicated tick-metronome thread and a worker pool that executes the system DAG in parallel. | ✅ Implemented | 🟣 Advanced | [→](Runtime/tick-execution-engine/worker-pool-threading.md) |
| &nbsp;&nbsp;↳ Parallel Tick Fence | Spreads the post-tick WriteTickFence step (cluster migrations, AABB refresh, WAL publish) across the worker pool instead of running it on one thread. | ✅ Implemented | 🟣 Advanced | [→](Runtime/tick-execution-engine/parallel-tick-fence.md) |
| System Types | Five system base classes a developer picks per piece of game logic — proactive callbacks, reactive entity queries, chunk-parallel non-entity work, multi-stage pipelines, and sub-system grouping. | ✅ Implemented | 🔵 Core | [→](Runtime/system-types/README.md) |
| &nbsp;&nbsp;↳ CallbackSystem | Proactive system that runs every tick for non-entity work — timers, input draining, global state. | ✅ Implemented | 🔵 Core | [→](Runtime/system-types/callback-system.md) |
| &nbsp;&nbsp;↳ QuerySystem | Reactive per-entity system that auto-skips when nothing relevant changed, with optional automatic multi-core chunking. | ✅ Implemented | 🔵 Core | [→](Runtime/system-types/query-system.md) |
| &nbsp;&nbsp;↳ ChunkedCallbackSystem | Fan a CallbackSystem's body out across N workers for SIMD sweeps, reductions, and other non-entity chunkable work. | ✅ Implemented | 🟣 Advanced | [→](Runtime/system-types/chunked-callback-system.md) |
| &nbsp;&nbsp;↳ PipelineSystem | Reactive multi-stage gather/process/scatter system for bulk entity processing — full execution model pending Patate. | Implemented (chunk-dispatch only) | 🟣 Advanced | [→](Runtime/system-types/pipeline-system.md) |
| &nbsp;&nbsp;↳ CompoundSystem | Group related sub-systems' registration under one Configure call — one node from the outside, parallel inside. | ✅ Implemented | 🟣 Advanced | [→](Runtime/system-types/compound-system.md) |
| Declarative System Scheduling (Track → DAG → Phase, Auto-DAG) | Systems declare per-component read/write access and a DAG-local phase; the scheduler auto-derives execution edges and rejects unsafe write/write or stale-read conflicts at Build(). | ✅ Implemented | 🔵 Core | [→](Runtime/declarative-system-scheduling.md) |
| Parallel Entity Processing (QuerySystem.Parallel) | Automatic multi-core chunking with a reusable PointInTimeAccessor (no per-chunk Transaction for non-Versioned writes) across four dispatch paths selected by Versioned-write × change-filter. | ✅ Implemented | 🟣 Advanced | [→](Runtime/parallel-entity-processing.md) |
| Reactive Dispatch: Change Filters & Run Conditions | changeFilter limits a system's entity set to dirty ∪ Added by piggybacking on the View's ring buffer; shouldRun gives a zero-cost proactive skip predicate evaluated before any input work. | ✅ Implemented | 🟣 Advanced | [→](Runtime/reactive-dispatch-change-filters.md) |
| Typed Event Queues | Single-producer ring-buffer queues for inter-system signalling within a tick, enabling reactive cascade chains that early-out cheaply when dormant. | ✅ Implemented | 🟣 Advanced | [→](Runtime/typed-event-queues.md) |
| Side-Transactions for Immediate Durability | Per-tick CreateSideTransaction(Immediate) lets a system commit economy-critical writes durably mid-tick, independent of and invisible to the main tick UoW's snapshot. | ✅ Implemented | 🟣 Advanced | [→](Runtime/side-transactions.md) |
| Overload Management | Single-writer overload state machine that escalates/de-escalates through system throttling and tick-rate modulation (TiDi, up to 6x) and fires a critical-overload callback for game-decided player shedding. | 🚧 Partial | 🟣 Advanced | [→](Runtime/overload-management.md) |
| Telemetry & Runtime Inspection | Always-on, zero-allocation ring buffer of per-tick/per-system telemetry inspectable from game code; a pluggable IRuntimeInspector hook for remote tooling is designed but not implemented. | 🚧 Partial | 🟣 Advanced | [→](Runtime/telemetry-runtime-inspection.md) |
| Spatial Tiers & Adaptive Dispatch | Per-cluster simulation tiers let systems process near entities every tick and far entities at reduced/amortized/dormant rates, scoping dispatch automatically to the matching clusters. | ✅ Implemented | 🟣 Advanced | [→](Runtime/spatial-tiers-adaptive-dispatch/README.md) |
| &nbsp;&nbsp;↳ Tier-Filtered & Amortized Dispatch | Scope a system to clusters in matching tier cells, and optionally process just 1/N of them per tick. | ✅ Implemented | 🟣 Advanced | [→](Runtime/spatial-tiers-adaptive-dispatch/tier-filtered-amortized-dispatch.md) |
| &nbsp;&nbsp;↳ Cluster Dormancy (Sleep/Wake) | Clusters untouched for N ticks sleep and are skipped by every dispatch path, waking within one tick of being written to. | ✅ Implemented | 🟣 Advanced | [→](Runtime/spatial-tiers-adaptive-dispatch/cluster-dormancy.md) |
| &nbsp;&nbsp;↳ Checkerboard (Red/Black) Dispatch | Two-phase Red/Black parallel dispatch so neighbor-touching systems never race across a cell boundary. | ✅ Implemented | 🟣 Advanced | [→](Runtime/spatial-tiers-adaptive-dispatch/checkerboard-dispatch.md) |
| Data-Driven Timers / Scheduled Entities | Documented pattern for modeling respawns/expiries as entities with a time-of-expiry component and a CallbackSystem poll; no built-in timer/scheduling infrastructure exists yet. | 📋 Planned | 🟣 Advanced | [→](Runtime/data-driven-timers.md) |
| Declarative Scheduling — Auto-DAG (RFC 07) | Directory-form restructuring of Declarative System Scheduling (above) into two sub-pages; exists on disk but was never linked from this table until now — see the flagging note below. | ✅ Implemented | 🟣 Advanced | [→](Runtime/declarative-scheduling/README.md) |
| &nbsp;&nbsp;↳ Track → DAG → Phase Partitioning | Tracks order coarse execution stages, DAGs group independent dependency graphs, phases order systems within one DAG. | ✅ Implemented | 🟣 Advanced | [→](Runtime/declarative-scheduling/track-dag-phase-partitioning.md) |
| &nbsp;&nbsp;↳ Access Declarations & Build-Time Conflict Detection | Reads/Writes/ReadsFresh/ReadsSnapshot declare what a system touches; Build() derives safe ordering and rejects unsafe overlaps. | ✅ Implemented | 🟣 Advanced | [→](Runtime/declarative-scheduling/access-conflict-detection.md) |
| Tick Lifecycle & Transaction Management | Restructures content already covered above by Side-Transactions and the tick loop's Parallel Tick Fence into its own directory; exists on disk but was never linked from this table until now — see the flagging note below. | ✅ Implemented | 🟣 Advanced | [→](Runtime/tick-lifecycle/README.md) |
| &nbsp;&nbsp;↳ Side-Transactions (Immediate Durability) | A transaction you open and commit from inside a tick system that becomes durable on its own, independent of whether the tick's main UoW ever flushes. | ✅ Implemented | 🟣 Advanced | [→](Runtime/tick-lifecycle/side-transactions.md) |
| &nbsp;&nbsp;↳ Parallel Tick Fence (WriteTickFence) | Spreads the post-tick WriteTickFence step across the worker pool instead of running it serially on one thread. | ✅ Implemented | 🟣 Advanced | [→](Runtime/tick-lifecycle/parallel-tick-fence.md) |

> Flagging note: the last two entries (Declarative Scheduling — Auto-DAG, Tick Lifecycle & Transaction Management) are directory-form restructurings that duplicate content already covered earlier in this table (Declarative System Scheduling; Side-Transactions + Parallel Tick Fence). They exist as real, unlinked doc pages under `Runtime/`; listed here for completeness per the source data, but they likely need consolidating with — or retiring in favor of — their originals rather than living on as permanent duplicates.

### Resources

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Resource Budget Configuration (ResourceOptions) | Startup-time configuration of fixed/growable resource limits (page cache size, max active transactions, WAL ring/segment sizing, shadow buffer, checkpoint thresholds) plus Validate() to check fixed allocations fit the total memory budget. | ✅ Implemented | 🔵 Core | [→](Resources/resource-budgets-options.md) |
| Exhaustion Policy & ResourceExhaustedException | Typed exception for resource-limit hits; ExhaustionPolicy enum documents intent but isn't a runtime dispatch switch. | 🚧 Partial | 🔵 Core | [→](Resources/exhaustion-policy-handling.md) |
| DI Registration & Wiring | Register Typhon services into IServiceCollection and have each one self-attach to the resource graph. | ✅ Implemented | 🔵 Core | [→](Resources/resources-di-wiring.md) |
| Observability Bridge (Resources to OTel/Health/Alerts) | Consumer-side mapping of resource snapshots to OpenTelemetry metrics, health-check thresholds, and alert payloads. | 🚧 Partial | 🟣 Advanced | [→](Resources/observability-bridge-resources.md) |

### Observability

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Telemetry Configuration & Gating | Hierarchical, JSON/env-var-driven static-readonly bool surface (~200 flags) that the JIT dead-code-eliminates when off, gating both Activity tracing and the typed-event Profiler with zero overhead when disabled. | ✅ Implemented | 🟢 Start Here | [→](Observability/telemetry-config-gating.md) |
| Distributed Tracing (Activity API) | Centralized ActivitySource and OTel-semantic attribute constants for correlating Typhon with a host's own OTLP trace. | 🚧 Partial | 🔵 Core | [→](Observability/distributed-tracing.md) |
| OpenTelemetry Metrics Export | Observable-pattern OTel Meter exporters that snapshot internal state and expose it as gauges/counters for Prometheus/OTLP scraping. | 🚧 Partial | 🔵 Core | [→](Observability/otel-metrics-export/README.md) |
| &nbsp;&nbsp;↳ Resource Graph Metrics Bridge | Every Resource System node exposed as a standard OTel Meter for Prometheus/OTLP scraping. | 🚧 Partial | 🟣 Advanced | [→](Observability/otel-metrics-export/resource-graph-metrics-bridge.md) |
| &nbsp;&nbsp;↳ ECS Metrics Exporter | Per-archetype EntityMap health and per-component-type transient memory as zero-cost OTel gauges. | 🚧 Partial | 🟣 Advanced | [→](Observability/otel-metrics-export/ecs-metrics-exporter.md) |
| Resource-Aware Health Checks | Framework-agnostic ITyphonHealthCheck (Healthy/Degraded/Unhealthy worst-of-composite) backed by ResourceHealthChecker. | ✅ Implemented | 🔵 Core | [→](Observability/health-checks.md) |
| Per-Domain Named Metrics Catalog | Documented target list of ~40 fixed-name OTel instruments (typhon.tx.*, typhon.wal.*, typhon.lock.*...) across 8 domains. | 📋 Planned | 🟣 Advanced | [→](Observability/per-domain-metrics-catalog.md) |
| Threshold-Based Resource Alerting | ResourceAlertGenerator raises Warning/Critical ResourceAlerts on health-state transitions, tracing root cause via a hardcoded wait-dependency graph. | ✅ Implemented | 🟣 Advanced | [→](Observability/threshold-alerting.md) |

### Errors

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| TyphonException Hierarchy & Catalog | Base TyphonException (ErrorCode + virtual IsTransient) rooted hierarchy giving callers three catch granularities, with a living catalog of every concrete exception type and a caller-owns-retry philosophy. | ✅ Implemented | 🔵 Core | [→](Errors/exception-hierarchy.md) |
| IsTransient Retry Hint | A virtual IsTransient flag on every TyphonException hinting whether a retry might succeed, without the engine ever retrying on the caller's behalf. | ✅ Implemented | 🔵 Core | [→](Errors/transience-hint.md) |
| Timeout Exceptions & Deadline Propagation | Configurable, finite deadlines (TimeoutOptions, WaitContext) replace infinite-wait lock primitives, converting hangs into typed TyphonTimeoutException subclasses plus a bulk-load checkpoint timeout. | ✅ Implemented | 🔵 Core | [→](Errors/timeout-exceptions-deadlines.md) |
| Error Code Classification | A numeric TyphonErrorCode per failure, grouped into subsystem ranges (1xxx-8xxx) for logging/metrics dashboards — the exception type, not the code, is what callers catch on. | ✅ Implemented | 🟣 Advanced | [→](Errors/error-codes.md) |
| Resource Exhaustion Handling | ExhaustionPolicy metadata (FailFast/Wait/Evict/Degrade) documents how the engine reacts when a bounded resource is full — FailFast throws a structured ResourceExhaustedException (IsTransient=true), Wait resources throw a bounded TyphonTimeoutException subclass, never an unbounded hang or InvalidOperationException. | ✅ Implemented | 🟣 Advanced | [→](Errors/resource-exhaustion-handling.md) |
| Storage & Corruption Exceptions | Typed failures for storage I/O, CRC32C page corruption (unhealable), and another-process database-file-lock detection. | ✅ Implemented | 🟣 Advanced | [→](Errors/storage-corruption-exceptions.md) |
| Durability (WAL / BulkLoad / Commit) Exceptions | Typed, fail-fast failures from the WAL writer, the commit pipeline's durability wait, and BulkLoad session lifecycle. | ✅ Implemented | 🟣 Advanced | [→](Errors/durability-exceptions.md) |
| Schema & Constraint Violation Exceptions | Engine-refuses-to-proceed failures for the data model: breaking schema mismatch, migration failure, revision downgrade, duplicate unique key. | ✅ Implemented | 🟣 Advanced | [→](Errors/schema-constraint-exceptions.md) |
| Runtime/Scheduler Declared-Access Validation | DEBUG-only InvalidAccessException when a system writes a component it never declared via Writes\<T\>()/SideWrites\<T\>(), compiled out in RELEASE. | ✅ Implemented | 🟣 Advanced | [→](Errors/runtime-access-validation.md) |

### Profiler

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| Profiler Session Lifecycle & Zero-Code Bootstrap | TyphonProfiler.Start/Stop (idempotent) manage the consumer/exporter threads and process-exit safety net; ProfilerBootstrap self-wires the whole session from typhon.telemetry.json + TyphonRuntime.Create with no host orchestration required. | ✅ Implemented | 🟢 Start Here | [→](Profiler/profiler-lifecycle-bootstrap.md) |
| Trace Export | IProfilerExporter fan-out: each attached exporter gets its own OS thread and bounded queue (drop-newest on backpressure, refcounted batches); file and live-TCP sinks cover offline post-mortem and real-time attach. | ✅ Implemented | 🔵 Core | [→](Profiler/trace-export/README.md) |
| &nbsp;&nbsp;↳ File-Based Trace Export (.typhon-trace) | Write the whole session to a versioned binary file for offline post-mortem analysis in the Workbench. | ✅ Implemented | 🔵 Core | [→](Profiler/trace-export/file-trace-export.md) |
| &nbsp;&nbsp;↳ Live TCP Streaming Export | Stream a running session over TCP so the Workbench can watch a process tick-by-tick, right now. | ✅ Implemented | 🔵 Core | [→](Profiler/trace-export/live-tcp-trace-export.md) |
| Configuration & Performance Tuning | typhon.telemetry.json / TYPHON__PROFILER__* env overrides drive independent static-readonly-bool gates per subsystem (JIT-eliminated when off); ProfilerOptions documents the consumer/drain tunables. | ✅ Implemented | 🔵 Core | [→](Profiler/profiler-configuration-tuning.md) |
| Per-Tick Gauge/Metric Snapshots | One packed record per tick exposes memory, page-cache, WAL, and transaction counters to the trace viewer. | ✅ Implemented | 🔵 Core | [→](Profiler/gauge-snapshots.md) |
| Custom Application-Defined Spans | Reserved wire-format span kind (NamedSpan, ID 246) for app-defined span names — read/replay support exists, but no producer factory exists in TyphonEvent yet. | 📋 Planned | 🟣 Advanced | [→](Profiler/custom-named-spans.md) |
| GC Event Tracing | See every .NET garbage collection and EE-suspension pause on the same timeline as your transactions. | ✅ Implemented | 🟣 Advanced | [→](Profiler/gc-event-tracing.md) |
| Unmanaged Memory Allocation Tracing | See every native (unmanaged) allocation and free on the profiler timeline, tagged by subsystem. | 🚧 Partial | 🟣 Advanced | [→](Profiler/unmanaged-allocation-tracing.md) |
| Off-CPU Thread Scheduling Capture (Windows) | EtwSchedulingPump emits one ThreadContextSwitch record per closed on-CPU slice via the NT Kernel Logger, showing when and why Typhon threads left the CPU. | ✅ Implemented | 🟣 Advanced | [→](Profiler/offcpu-thread-scheduling.md) |
| Integrated CPU Sampling (Statistical Call Tree) | In-process EventPipe SampleProfiler captures call stacks for the session, embedded as a trailer section and rendered as a dotTrace-style Call Tree. | ✅ Implemented | 🟣 Advanced | [→](Profiler/cpu-sampling-calltree.md) |
| Query Definition & Execution Export | Captures every View/EcsQuery's structural definition once per session plus per-execution args and call sites, correlated to the existing query span chain. | 🚧 Partial | 🟣 Advanced | [→](Profiler/query-definition-export.md) |
| Domain-Specific Tracing Instrumentation Expansion | Two-tier compile-time gated tracing rollout across nine engine domains (Concurrency, Storage, Memory, Data, Query, ECS, Spatial, Scheduler/Runtime, Durability, Subscriptions), zero cost when off. | 🚧 Partial | 🟣 Advanced | [→](Profiler/domain-tracing-expansion.md) |

### Hosting

| Feature | Summary | Status | Level | Link |
|---|---|---|---|---|
| DI Engine Bootstrap Chain | Add*() extension methods on IServiceCollection that register and wire the engine's top-level singletons into a working DatabaseEngine. | ✅ Implemented | 🟢 Start Here | [→](Hosting/di-bootstrap-chain/README.md) |
| &nbsp;&nbsp;↳ Singleton/Scoped/Transient Lifetime Variants | Every Add* with a lifetime choice ships as Add.../AddScoped.../AddTransient... twins sharing one factory delegate. | ✅ Implemented | 🔵 Core | [→](Hosting/di-bootstrap-chain/lifetime-variants.md) |
| Engine Options Configuration Surface | IOptions\<T\>-based configuration for engine services, set via configure delegates on each Add*() DI call. | ✅ Implemented | 🔵 Core | [→](Hosting/engine-options-configuration/README.md) |
| &nbsp;&nbsp;↳ Options Validation Hooks | AddOptions\<T\>().Validate(...) is wired on every options type but its predicate is a no-op stub today; ResourceOptions.Validate()/PagedMMFOptions.IsValid are the real, directly-callable fail-fast checks. | 🚧 Partial | 🟣 Advanced | [→](Hosting/engine-options-configuration/options-validation-stubs.md) |
| Database Seeding | TyphonOptions.Seed(revision, tx => …) registers revision-stepped, crash-safe seed steps applied automatically at engine open — a fresh database runs them all, an existing one catches up on new ones. | ✅ Implemented | 🟢 Start Here | [→](Hosting/database-seeding.md) |
| Clean-Slate Database File Deletion | EnsureFileDeleted\<TO\>(IServiceProvider) resolves IOptions\<TO\> in a fresh scope and deletes the backing database file it points at. | ✅ Implemented | 🔵 Core | [→](Hosting/ensure-file-deleted.md) |
| Profiler Launch Override Hook | AddTyphonProfiler(Func\<ProfilerLaunchConfig,ProfilerLaunchConfig\>) lets a host adjust the profiler launch config in code without giving up zero-code defaults. | ✅ Implemented | 🟣 Advanced | [→](Hosting/profiler-launch-override-hook.md) |

## Internal — Engine Internals (for Contributors)

Every Internal feature, one line each — engine machinery with no direct application-facing API. Sub-features are indented under their parent with `↳`. Categories with no Internal-tagged features (Ecs, Querying, Transactions, Subscriptions, Runtime, Observability) are omitted below.

### Foundation

| Feature | Summary | Status | Link |
|---|---|---|---|
| Reader-Writer & Resource Lifecycle Locks | CAS-only, allocation-free spin-locks every concurrent engine structure embeds for shared/exclusive or lifecycle access. | ✅ Implemented | [→](Foundation/access-control-lock-family/README.md) |
| &nbsp;&nbsp;↳ AccessControl (full-featured RW lock) | 64-bit reader-writer lock with waiter fairness, in-place promotion, and contention tracking. | ✅ Implemented | [→](Foundation/access-control-lock-family/access-control.md) |
| &nbsp;&nbsp;↳ AccessControlSmall (compact RW lock) | 32-bit reader-writer lock for thousands of embedded per-node/per-page latches with short critical sections. | ✅ Implemented | [→](Foundation/access-control-lock-family/access-control-small.md) |
| &nbsp;&nbsp;↳ ResourceAccessControl (3-mode lifecycle lock) | 32-bit Accessing/Modify/Destroy lock where structural growth doesn't block concurrent readers. | ✅ Implemented | [→](Foundation/access-control-lock-family/resource-access-control.md) |
| Epoch-Based Resource Protection | Lock-free epoch/grace-period scheme that protects in-flight page-cache pages from eviction with 2 obligations per transaction instead of per-page ref-counting. | ✅ Implemented | [→](Foundation/epoch-based-resource-protection.md) |
| High-Resolution Timers | Self-calibrating sub-millisecond periodic timer services (three-phase Sleep→Yield→Spin wait, drift-free metronome scheduling) used for the deadline watchdog, telemetry flush, and epoch advancement, exposing per-tick jitter metrics. | ✅ Implemented | [→](Foundation/high-resolution-timers/README.md) |
| &nbsp;&nbsp;↳ Dedicated Timer (HighResolutionTimerService) | A single periodic callback on its own thread, isolated from every other timer in the engine. | ✅ Implemented | [→](Foundation/high-resolution-timers/dedicated-timer.md) |
| &nbsp;&nbsp;↳ Shared Timer (HighResolutionSharedTimerService) | One thread multiplexing many periodic callbacks, each at its own rate, waking only when the soonest one is due. | ✅ Implemented | [→](Foundation/high-resolution-timers/shared-timer.md) |
| In-Memory Hash Maps | Open-addressing, pinned-memory hash set/map types with backward-shift deletion and JIT-specialized hashing that replace .NET HashSet/Dictionary/ConcurrentDictionary on hot paths. | ✅ Implemented | [→](Foundation/in-memory-hash-maps/README.md) |
| &nbsp;&nbsp;↳ Non-Concurrent HashMap\<TKey[, TValue]\> | Single-threaded open-addressing hash set/map replacing HashSet/Dictionary on a hot path, zero per-entry GC pressure. | ✅ Implemented | [→](Foundation/in-memory-hash-maps/non-concurrent-hash-map.md) |
| &nbsp;&nbsp;↳ ConcurrentHashMap\<TKey[, TValue]\> | Striped, lock-free-read hash set/map replacing ConcurrentDictionary on a shared hot path, per-stripe CAS writes. | ✅ Implemented | [→](Foundation/in-memory-hash-maps/concurrent-hash-map.md) |
| Page-Backed Linear Hash Map | O(1) exact-match key/value index, persisted in fixed-size chunks, with crash-safe rebuild instead of WAL logging. | ✅ Implemented | [→](Foundation/paged-linear-hash-map.md) |
| Memory Allocators | Pinned/unmanaged memory primitives that give every page cache, segment, and hash-map structure stable, GC-immune addresses with parent-owned leak tracking. | ✅ Implemented | [→](Foundation/memory-allocators.md) |
| Concurrent Bitmaps & Collections | Lock-free/CAS-guarded occupancy bitmaps (flat and 3-level) plus a pick/putback slot array for high-contention tracking. | ✅ Implemented | [→](Foundation/concurrent-bitmaps-collections.md) |
| Hardware-Accelerated CRC32C Checksums | SSE4.2/ARM-intrinsic CRC32C (Castagnoli polynomial) computation with software-table fallback (~1.3µs per 8KB page) — the checksum primitive backing every page-integrity check in storage and durability. | ✅ Implemented | [→](Foundation/crc32c-checksums.md) |

### Storage

| Feature | Summary | Status | Link |
|---|---|---|---|
| Epoch-Based Page Protection & Dirty-Page Tracking | Epoch-tagged eviction safety plus the ChangeSet/DirtyCounter/ActiveChunkWriters/SlotRefCount protocol that pins modified or pointer-referenced pages until checkpoint write-back. | ✅ Implemented | [→](Storage/epoch-dirty-tracking.md) |
| Page Allocation & Occupancy Tracking | A 3-level bitmap that allocates and tracks every 8 KiB page in the database file, growing the file automatically as needed. | ✅ Implemented | [→](Storage/page-allocation-occupancy.md) |
| Segment & Chunk-Based Allocation Engine | Multi-page directories and fixed-size slot allocation — the substrate every component, index, and revision chain is built from. | ✅ Implemented | [→](Storage/segment-chunk-allocation.md) |
| Pluggable Storage Backend (Persistent vs Transient) | One set of segment/index code, JIT-specialized per backend, so Transient components get heap speed for free. | ✅ Implemented | [→](Storage/pluggable-storage-backend/README.md) |
| &nbsp;&nbsp;↳ Persistent Store (MMF-backed) | The default backend — every durable component's segments run through the memory-mapped page cache at zero abstraction cost. | ✅ Implemented | [→](Storage/pluggable-storage-backend/persistent-store.md) |
| String Table Storage | UTF-8 string storage spread across linked fixed-size chunks, for strings too long to hold inline. | ✅ Implemented | [→](Storage/string-table.md) |

> Note: Transient Store (heap-backed), the other child of Pluggable Storage Backend, is Public — see the [Public](#public--what-you-can-use) section above.

### Durability

| Feature | Summary | Status | Link |
|---|---|---|---|
| A/B Protected-Page Slot-Pairing | Doublewrite-free torn-write protection for the meta page and segment-directory pages that crash recovery can't re-derive. | ✅ Implemented | [→](Durability/checkpoint-v2/protected-page-pairing.md) |
| Rebuild of Derived Structures | Indexes, EntityMap, and occupancy are never repaired after a crash — they're discarded and rebuilt wholesale from the recovered primary data. | ✅ Implemented | [→](Durability/crash-recovery/rebuild-derived-structures.md) |
| Formal Proofs & Invariant Rules | Durability correctness is gated on falsifiable artifacts — invariant rules, TLA+ specs, and a crash-simulation sweep — enforced in CI. | ✅ Implemented | [→](Durability/correctness-proofs.md) |

> Note: A/B Protected-Page Slot-Pairing and Rebuild of Derived Structures are sub-features of Checkpoint v2 and Crash Recovery respectively — both Public parents, listed in the [Public](#public--what-you-can-use) section above.

### Schema

| Feature | Summary | Status | Link |
|---|---|---|---|
| tsh Schema Shell Commands | Typhon Shell commands (schema-fields, schema-diff, schema-validate, schema-history, schema-export) exposing persisted-vs-runtime schema comparison and inspection as an interactive CLI on top of the engine schema APIs. | ✅ Implemented | [→](Schema/tsh-schema-commands.md) |
| System Schema Persistence | Self-referential storage of component/field metadata as engine-internal ECS entities (ComponentR1/FieldR1) inside the database itself, loaded/saved via a minimal single-threaded CRUD layer (no MVCC/WAL) at engine open/create. | ✅ Implemented | [→](Schema/system-schema-persistence.md) |
| Workbench Per-Session Schema Loading & ALC Reload | Loads schema DLLs into a per-session collectible ALC so Workbench sessions can rebuild/swap binaries without restarting the host, classifying engine schema exceptions into Ready/MigrationRequired/Incompatible and rebinding component IDs by schema name across reloads. | ✅ Implemented | [→](Schema/workbench-schema-loading.md) |
| Component Family Classification | Classifies a component into a semantic family (Spatial/Combat/AI/Inventory/Rendering/Networking/Input/Misc) via explicit attribute or name heuristic, for stable Workbench Data Flow grouping. | ✅ Implemented | [→](Schema/component-family-classification.md) |

### Revision

| Feature | Summary | Status | Link |
|---|---|---|---|
| Chain Walk Correctness Under Compaction | The visibility walk scans the whole chain instead of breaking on the first too-new entry, because background GC compaction can reorder entries without changing their TSNs. | ✅ Implemented | [→](Revision/mvcc-visibility-walk.md) |
| Revision Chain Storage | Per-entity, per-component circular-buffer chunk chain holding every live revision of a Versioned component, allocated on first write and grown on demand. | ✅ Implemented | [→](Revision/revision-chain-storage.md) |
| Crash-Recovery Chain Scrub & Orphan Sweep | Post-crash recovery step that collapses every Versioned chain to its single committed HEAD and frees unreachable revision-table chunks, guaranteeing pre-crash MVCC history never survives into the recovered base. | ✅ Implemented | [→](Revision/crash-recovery-chain-scrub.md) |

> Note: Chain Walk Correctness Under Compaction is a sub-feature of MVCC Snapshot Visibility, a Public parent listed in the [Public](#public--what-you-can-use) section above.

### Indexing

| Feature | Summary | Status | Link |
|---|---|---|---|
| Specialized B+Tree Key-Size Variants | Four key-width-specialized B+Tree implementations (16/32/64-bit and String64), automatically selected by an indexed field's CLR type. | ✅ Implemented | [→](Indexing/btree-key-variants.md) |
| Compound Move/MoveValue (field-update fast path) | Atomic remove+insert for indexed-field updates — one traversal, one lock on the common same-leaf case. | ✅ Implemented | [→](Indexing/compound-move-operations.md) |
| Temporal (Point-in-Time) Index Query | Reconstructs which entities held a key's value at a past TSN by replaying the index's append-only TAIL history. | 🚧 Partial | [→](Indexing/temporal-index-query.md) |
| TAIL Retention / Garbage Collection | Bounds TAIL version-history growth via boundary-sentinel-preserving pruning — built and tested, not yet auto-triggered. | 🚧 Partial | [→](Indexing/tail-garbage-collection.md) |
| Optimistic Lock Coupling (per-node concurrency) | Per-node OLC version latches give lock-free optimistic readers and leaf-only write latching for B+Tree/R-Tree index operations. | ✅ Implemented | [→](Indexing/olc-concurrency.md) |
| Index Diagnostics & Consistency Checking | Always-on per-instance contention counters plus an on-demand tsh structural walk to diagnose B+Tree contention and validate integrity. | ✅ Implemented | [→](Indexing/btree-diagnostics.md) |
| B+Tree Node Layout and Capacity Tuning | Cache-line-aware 256-byte B+Tree node layout with per-key-type capacities, tuned through a multi-phase profiling effort. | ✅ Implemented | [→](Indexing/btree-node-layout-tuning.md) |
| Batched Index Maintenance for Bulk Commits | Commit-path rework that batches secondary-index updates per commit; accessor-reuse has shipped, sorted-key application has not. | 🚧 Partial | [→](Indexing/batched-index-maintenance.md) |

### Spatial

| Feature | Summary | Status | Link |
|---|---|---|---|
| Spatial R-Tree Index | Page-backed R-Tree attached to a component field, giving sub-microsecond AABB/radius/ray queries shared across every archetype that uses it. | ✅ Implemented | [→](Spatial/spatial-rtree-index/README.md) |
| Trigger Volumes (Enter / Leave / Stay) | Region entities diffed against the spatial tree(s) each cycle to emit Enter/Leave/Stay events at a configurable per-region frequency. | ✅ Implemented | [→](Spatial/spatial-trigger-volumes.md) |
| Interest Management (Delta Spatial Queries) | Per-observer "what changed near me" delta queries via an archived dirty-bitmap ring buffer, with full-sync fallback for stale observers. | 🚧 Partial | [→](Spatial/spatial-interest-management.md) |
| Cluster Spatial Queries | Per-cell broadphase + per-entity narrowphase AABB/Radius queries for cluster-eligible archetypes. | 🚧 Partial | [→](Spatial/cluster-spatial-queries.md) |
| Cluster Dormancy (Sleep / Wake) | Clusters with no component writes for N ticks sleep and skip dispatch entirely, waking within one tick of being touched. | ✅ Implemented | [→](Spatial/cluster-dormancy.md) |

> Note: Static / Dynamic Tree Separation, the only child of Spatial R-Tree Index, is Public — see the [Public](#public--what-you-can-use) section above.

### Resources

| Feature | Summary | Status | Link |
|---|---|---|---|
| Resource Tree & Registry | Hierarchical, fail-fast tree of every managed resource (8 fixed subsystem branches under Root) with cascade disposal and path-based navigation. | ✅ Implemented | [→](Resources/resource-tree-registry.md) |
| Resource Tree Mutation Notifications | NodeMutated event fires on every Added/Removed registration so consumers (e.g. Workbench live tree view) can react without polling. | ✅ Implemented | [→](Resources/resource-tree-mutation-notifications.md) |
| Metric Reporting (IMetricSource / IMetricWriter) | Pull-based, zero-allocation interface for resources to expose 5 metric kinds — Memory, Capacity, DiskIO, Throughput, Duration — read by the graph on snapshot, not pushed on the hot path. | ✅ Implemented | [→](Resources/metric-reporting.md) |
| Owner-Aggregates Granularity Strategy | Architectural rule for what becomes a resource-tree node vs. what owning components aggregate and report on its behalf. | ✅ Implemented | [→](Resources/owner-aggregates-granularity.md) |
| Debug Properties Drill-Down (IDebugPropertiesProvider) | Ad-hoc, allocation-tolerant dictionary of diagnostic key/value pairs for per-resource drill-down that's too verbose for the structured metric writer. | ✅ Implemented | [→](Resources/debug-properties-drilldown.md) |
| Snapshot & Query API | On-demand, consistent-enough tree-wide snapshot (IResourceGraph.GetSnapshot) with query helpers — GetSubtreeMemory, FindMostUtilized, FindByType, GetSubtree, GetNode — plus auto-computed throughput rates from the previous snapshot. | ✅ Implemented | [→](Resources/snapshot-query-api/README.md) |
| &nbsp;&nbsp;↳ Root-Cause Cascade Analysis (FindRootCause) | Trace a high-utilization symptom node back through a known dependency chain to find what's actually backed up. | ✅ Implemented | [→](Resources/snapshot-query-api/root-cause-cascade-analysis.md) |

### Errors

| Feature | Summary | Status | Link |
|---|---|---|---|
| Result\<TValue,TStatus\> Hot-Path Result Type | Zero-allocation dual-generic result struct for hot-path lookups that expect a miss — no exceptions, no boxing, no branch on access; used internally by B+Tree/MVCC hot paths, not surfaced through Transaction/Query call sites. | ✅ Implemented | [→](Errors/result-type.md) |

### Profiler

| Feature | Summary | Status | Link |
|---|---|---|---|
| Typed-Event Capture Pipeline | Any-thread ~25-50ns ref-struct span/instant emission into per-thread SPSC ring buffers, drained by a dedicated timestamp-sorting consumer thread; zero allocation, JIT-eliminated when disabled. | ✅ Implemented | [→](Profiler/typed-event-capture-pipeline.md) |
| Built-in Engine Instrumentation Catalog | Automatic, no-app-code-required span/instant coverage of Scheduler, Transactions, ECS, B+Tree, Page Cache, WAL, Checkpoint, Statistics and Cluster Migration — 37+ wire-stable event kinds decoded by a shared TraceEventDecoder. | ✅ Implemented | [→](Profiler/builtin-subsystem-instrumentation.md) |
| Span Source Attribution (Go-to-Source) | Every span optionally carries a compile-time-deterministic source location for one-click editor handoff and inline preview in the Workbench. | ✅ Implemented | [→](Profiler/source-attribution.md) |
| Lock-Contention Forensics (Deep Diagnostics) | Post-mortem visibility into which threads waited on which locks, for how long, and why. | 📋 Planned | [→](Profiler/lock-contention-diagnostics.md) |

### Hosting

| Feature | Summary | Status | Link |
|---|---|---|---|
| Pluggable WAL I/O Backend (IWalFileIO seam) | AddDatabaseEngine resolves an optional internal IWalFileIO from the container so tests/benchmarks run the full WAL + checkpoint pipeline with zero disk I/O; the interface and implementations are internal, unreachable from application code. | ✅ Implemented | [→](Hosting/wal-io-injection-seam.md) |

## See also

This catalog favors practical orientation over deep internals: what a feature does, why it exists, and how to use it (Public), or just enough of how it fits together to orient a contributor (Internal). For other angles on the same engine:

- [`doc/in-depth-overview/`](../in-depth-overview/README.md) — implementation-level walkthroughs of engine internals (struct layouts, algorithms), organized to mirror `src/Typhon.Engine/`'s folder structure.
- [`doc/guide/`](../guide/README.md) — a hands-on, task-oriented tutorial for building an application on Typhon end to end, without engine internals.
