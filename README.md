# Typhon

[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)

### ⚠️ The engine is in active development and not usable as a library or product. ⚠️

There is no documentation, no stable API, no NuGet package, and no support.


**A microsecond-latency ACID data engine combining ECS archetype storage with tick-based parallel execution.**

Typhon is an embedded data engine for real-time workloads like game servers, simulations, and stateful dataflow.<br/> 
It pairs a microsecond-latency ACID store — MVCC snapshot isolation, configurable durability, source-generated ECS archetype accessors — with a tick-based parallel runtime that dispatches fine-grained system chunks across worker threads, each operating directly on the store.<br/>
The runtime doesn't sit on top of the database — it runs inside it.

---

## Key Features

- **Microsecond Operations** — Optimized for µs-level latency with pinned memory, SIMD, and lock-free reads
- **ACID Transactions** — Full transactional semantics with optimistic concurrency control
- **MVCC Snapshot Isolation** — Readers never block writers; each transaction sees a consistent snapshot
- **ECS Archetype System** — Entities are typed by archetype; components are blittable structs with source-generated accessors and archetype inheritance
- **Entity Clusters** — SoA batched storage co-locating up to 64 entities per cluster, with cluster-native SIMD predicate evaluation (AVX-512 / AVX2 / scalar dispatch), zone-map pruning, and k-way sorted merge
- **Query Engine** — Typed queries with `Where`, `With`/`Without` filters, polymorphic iteration, OR logic, navigation joins, and a cluster execution path that routes filtered scans through SIMD gather-compare on cluster SoA columns
- **Views & Change Tracking** — Incremental entity-set monitoring across transactions (added/removed/modified detection) with ring-buffer delta streaming
- **B+Tree Indexes** — Cache-aligned 128-byte nodes with optimistic lock coupling and specialized key-size variants
- **Spatial Indexing** — Page-backed wide R-Tree for AABB / Radius / Ray / Frustum / kNN queries, plus a per-cell cluster broadphase (`SpatialGrid`) with BMI2 Morton-encoded cell keys and multi-resolution `SimTier` dispatch for near/far/coarse simulation budgets
- **Write-Ahead Logging** — WAL with configurable durability modes (Deferred, GroupCommit, Immediate) and coalesced tick-boundary snapshots via `TickFence` / `ClusterTickFence` chunks for the runtime tick loop
- **Page Integrity** — CRC32C checksums, full-page images, and checkpoint-based recovery
- **Schema Versioning** — Component revisions with field-level migration support
- **Three Storage Modes** — Versioned (full MVCC + WAL), SingleVersion (last-writer-wins, tick-fence WAL), Transient (heap-only, no persistence)
- **Configurable Durability** — Choose per-UnitOfWork whether data is deferred, group-committed, or immediately fsynced
- **Game Server Runtime** — Tick-based micro-task `DagScheduler` with any-worker parallel dispatch, per-system change filters, typed MPSC event queues, side-transactions, cluster dormancy with staggered heartbeat wake, checkerboard Red/Black dispatch, and a 4-level overload response hierarchy (tick-rate modulation, player shedding)
- **Subscription Server + Client SDK** — TCP delta streaming of published views with MemoryPack serialization, per-client incremental sync, backpressure-driven resync, and a zero-engine-dependency `Typhon.Client` assembly for game clients
- **Observability** — Runtime telemetry, metrics, and diagnostics with zero-cost JIT-eliminated toggles
- **Deep-Trace Profiler** — Per-tick Gantt and flame-graph visualization via a `.typhon-trace` binary format, live TCP streaming to a React/Vite viewer, and optional `dotnet-trace` CPU sampling correlation for full managed-stack profiles
- **Interactive Shell (tsh)** — Database REPL for inspection and debugging
- **Workbench Dev UI** — Local ASP.NET Core + React/Vite app for data browsing, schema inspection, profiler trace viewing, System DAG and Data Flow timeline panels, Access Matrix, and internal data API for cross-panel cross-linking
- **Public/Internal API split** — Two namespaces per assembly (`Typhon.Engine` / `Typhon.Engine.Internals`), each subsystem folder split into `public/` + `internals/`, enforced at compile time by the `TYPHON008` Roslyn analyzer
- **Roslyn Analyzers** — Custom analyzers detecting undisposed engine resources at compile time + the `TYPHON008` internal-API leak detector

## Quick Start

```csharp
// Define components
[Component("Game.Position", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Position
{
    [Field] public float X;
    [Field] public float Y;
    [Field] public float Z;
}

[Component("Game.Health", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Health
{
    [Field] public int Current;
    [Field] public int Max;
}

// Define an archetype (ID is a unique 12-bit identifier embedded in EntityId)
[Archetype(1)]
partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Health>   Health   = Register<Health>();
}

// Register and initialize
var dbe = serviceProvider.GetRequiredService<DatabaseEngine>();
dbe.RegisterComponentFromAccessor<Position>();
dbe.RegisterComponentFromAccessor<Health>();
dbe.InitializeArchetypes();

// Spawn an entity
using var tx = dbe.CreateQuickTransaction();

var pos = new Position { X = 10, Y = 20, Z = 0 };
var hp  = new Health { Current = 100, Max = 100 };
var id  = tx.Spawn<Unit>(Unit.Position.Set(in pos), Unit.Health.Set(in hp));

// Read it back
ref readonly var p = ref tx.Open(id).Read(Unit.Position);
// p.X == 10, p.Y == 20

// Mutate
ref var h = ref tx.OpenMut(id).Write(Unit.Health);
h.Current = 80;

tx.Commit();
```

### Running as a Game Server (Runtime)

For tick-based workloads, the `TyphonRuntime` wraps the engine with a DAG scheduler, parallel worker pool, and tick loop:

```csharp
// Register components and archetypes as above, then:

using var runtime = TyphonRuntime.Create(dbe, schedule =>
{
    // Parallel query system — runs once per tick across worker threads
    schedule.QuerySystem("Movement", ctx =>
    {
        foreach (var id in ctx.Entities)
        {
            var e = ctx.Accessor.OpenMut(id);
            ref var pos = ref e.Write(Unit.Position);
            pos.X += e.Read(Unit.Velocity).X * ctx.DeltaTime;
        }
    }, input: () => activeUnitsView, parallel: true);

    // Sequential cleanup system — runs after Movement completes
    schedule.CallbackSystem("Cleanup", ctx => { /* ... */ }, after: "Movement");
});

runtime.Start();  // Tick loop runs at configured Hz (default 60)
```

The runtime creates one `UnitOfWork` per tick (Deferred durability), one `Transaction` per system (or per parallel chunk), and coalesces all writes into tick-fence WAL chunks at the end of each tick.

## Architecture

<a href="typhon-architecture-layers.svg">
  <img src="typhon-architecture-layers.svg" width="1165"
       alt="Typhon Architecture Layers">
</a>

## Development Status

Typhon is in **active development** targeting an alpha release. Current state:

- [x] Core transaction engine with MVCC
- [x] B+Tree indexes with optimistic lock coupling
- [x] Component-level durability options
- [x] Write-Ahead Logging with configurable durability modes
- [x] Page integrity (CRC32C, full-page images, checkpoints)
- [x] Query engine with filtering, sorting, and navigation
- [x] Views and incremental change tracking
- [x] ECS archetype system with source-generated accessors
- [x] Entity cluster storage (SoA batched, SIMD predicate evaluation, zone-map pruning, k-way sorted merge)
- [x] Dual page store abstraction (`PersistentStore` + `TransientStore`)
- [x] Schema versioning and evolution
- [x] Spatial indexing (page-backed wide R-Tree: AABB / Radius / Ray / Frustum / kNN)
- [x] Spatial grid with per-cell cluster broadphase and BMI2 Morton encoding
- [x] Multi-resolution simulation (`SimTier` dispatch, cluster dormancy, checkerboard Red/Black)
- [x] Game server runtime (`DagScheduler`, parallel system dispatch, overload management, event queues)
- [x] Subscription server and `Typhon.Client` SDK (TCP delta streaming, MemoryPack wire protocol)
- [x] Deep-trace runtime profiler (`.typhon-trace` format, live TCP streaming, React viewer)
- [x] Interactive shell (tsh)
- [x] Observability and monitoring stack
- [x] HashMap collections with key-size specialization
- [x] Workbench dev UI (data browsing, profiler viewer, System DAG view, Data Flow timeline, Access Matrix, internal data API)
- [x] Public/Internal API namespace split + `TYPHON008` analyzer
- [ ] Crash recovery testing
- [ ] Backup and restore

## Project Structure

Each engine subsystem folder splits into `public/` (consumer surface, namespace `Typhon.Engine`) and `internals/` (implementation, namespace `Typhon.Engine.Internals`) — see the [Public/Internal API classification doc](https://github.com/nockawa/typhon-claude/blob/main/research/PublicVsInternalApiClassification.md) for the methodology.

```
Typhon/
├── src/
│   ├── Typhon.Engine/              # Main database engine — 17-feature tree
│   │   ├── Foundation/             # Cross-cutting primitives
│   │   │   ├── Collections/        # HashMaps, bitmaps, lock-free arrays
│   │   │   ├── Concurrency/        # Latches, AccessControl, epoch system, deadlines, timer service
│   │   │   └── Memory/             # Memory allocator + block primitives
│   │   ├── Ecs/                    # ECS engine, archetypes, entity clusters, accessors
│   │   ├── Indexing/               # B+Tree variants (key-size specialized, OLC)
│   │   ├── Querying/               # Query engine, views, navigation joins, predicates
│   │   ├── Schema/                 # Component & archetype schema, validation, evolution
│   │   ├── Spatial/                # R-Tree, spatial grid, tier dispatch, trigger zones
│   │   ├── Storage/                # Pages, cache, segments, PagedMMF / IPageStore / PersistentStore / TransientStore
│   │   ├── Transactions/           # MVCC transactions, UnitOfWork, change capture
│   │   ├── Revision/               # Revision chains, deferred cleanup
│   │   ├── Durability/             # WAL, checkpointing, recovery, FPI capture
│   │   ├── Runtime/                # DagScheduler, tick loop, systems, overload management, queues
│   │   ├── Subscriptions/          # TCP delta streaming server, published views
│   │   ├── Profiler/               # In-engine typed-event profiler (codecs in Typhon.Profiler/)
│   │   ├── Observability/          # Telemetry config, metrics, diagnostics
│   │   ├── Resources/              # Resource graph, lifecycle, options
│   │   ├── Errors/                 # Exception hierarchy, deadline propagation
│   │   └── Hosting/                # DI extensions, service collection helpers
│   ├── Typhon.Analyzers/           # Roslyn analyzers (dispose detection + TYPHON008 internal-API leak)
│   ├── Typhon.Client/              # External client SDK (TCP subscriptions, zero engine deps)
│   ├── Typhon.Generators/          # Source generators (archetype accessors, traced-event encoders, source location)
│   ├── Typhon.Profiler/            # Trace file format, readers/writers, sidecar cache, Chrome Trace exporter
│   ├── Typhon.Protocol/            # MemoryPack wire-format types (TickDeltaMessage, etc.)
│   ├── Typhon.Schema.Definition/   # Component & archetype attributes
│   ├── Typhon.Shell/               # Interactive database shell (tsh)
│   └── Typhon.Shell.Extensibility/ # Shell extension points
├── test/
│   ├── Typhon.Engine.Tests/        # NUnit test suite (3500+ tests)
│   ├── Typhon.Client.Tests/        # Client SDK tests
│   ├── Typhon.Workbench.Tests/     # Workbench server-side tests
│   ├── Typhon.Benchmark/           # BenchmarkDotNet performance tests
│   ├── Typhon.ARPG.Schema/         # Example ARPG game schema
│   ├── Typhon.ARPG.Shell/          # Shell demo with ARPG data
│   ├── Typhon.MonitoringDemo/      # Observability demo
│   ├── Typhon.IOProfileRunner/     # Storage I/O profiling sandbox
│   ├── Typhon.Scheduler.POC/       # DagScheduler POC harness
│   ├── Typhon.SqliteBenchmark/     # Comparative SQLite benchmark
│   └── AntHill/                    # Godot-based ant-colony demo (runtime + clusters + spatial tiers)
├── tools/
│   ├── Typhon.Workbench/           # Local dev UI (ASP.NET Core 10 + React 19/Vite) — data browsing, schema, profiler, System DAG, Data Flow timeline, Access Matrix
│   ├── Typhon.Workbench.Fixtures/  # Dev-only test fixture generators consumed by the Workbench DEBUG tabs
│   └── CheckDurations/             # Helper utility for tick-duration analysis
├── claude/                         # Architecture docs, ADRs, design specs (separate nested git repo)
└── benchmark/                      # Benchmark results
```

## History

This project has had quite a journey:

- **2015** — Initial bootstrap with a different design, quickly shelved
- **2020** — COVID resurrection as a POC: "Can we build a µs-latency ACID database for persistent games?" Promising results, then shelved again
- **2025** — Third resurrection with firm intention to reach alpha stage
- **2025-2026** — Rapid progress: WAL & durability, query engine, ECS archetype system with source generators, schema evolution, observability stack, and interactive shell delivered
- **Q2 2026 alpha push** — Game-server runtime (`DagScheduler`, parallel system dispatch, overload management), entity clusters with cluster-native SIMD query execution, dual page store abstraction (`PersistentStore` + `TransientStore`), spatial indexing (page-backed R-Tree + spatial grid cluster broadphase), multi-resolution `SimTier` dispatch with cluster dormancy and checkerboard, TCP subscription server with zero-engine-dependency client SDK, and a deep-trace runtime profiler with live-streaming viewer
- **Q2 2026 Workbench buildout** — Internal Data API + System DAG view (#306, #314, #322), static schema export and Schema Inspector (#326), Data Flow Timeline + Access Matrix panels with cross-panel selection and hover linking (#327)
- **Q2 2026 API hardening** — Public/Internal namespace migration, accessibility flips, friend-list audit, and leak-tightening pass: ~430 internal types now in `Typhon.Engine.Internals` enforced by the `TYPHON008` analyzer (#329)
