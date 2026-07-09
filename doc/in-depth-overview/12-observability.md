---
uid: overview-observability
title: '12 — Observability'
description: 'Typhon''s observability story is not "scatter Activity spans everywhere and let an OTel exporter sort it out." That model puts an unconditional method call…'
---

# 12 — Observability

**Code:** [`src/Typhon.Engine/Observability/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Observability) + [`src/Typhon.Engine/Profiler/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Profiler) (merged into one doc — engine-side observability + the in-process profiler share a single zero-overhead philosophy and a single producer pipeline)

Typhon's observability story is *not* "scatter `Activity` spans everywhere and let an OTel exporter sort it out." That model puts an unconditional method call on every hot path and bets the runtime can elide it cheaply enough. Typhon takes the other side of that bet. The hot paths emit **typed events** through a producer pipeline that the JIT can dead-code-eliminate at Tier-1 compilation — when the relevant gate is off, the call site folds to nothing, and the engine pays zero CPU for telemetry it isn't using.

Two things make this possible: (1) every gate is a `public static readonly bool` initialized once at class-load from configuration, and (2) every typed event's `Begin*Event` factory has the gate check as its first instruction. That's all it takes — the JIT does the rest.

The catch: turning telemetry on/off requires a process restart (gates are baked at JIT time). That's the deal Typhon makes on purpose. You decide *at deployment time* what you want to observe; the running process pays nothing for the rest. Hosts that need dynamic toggling layer their own runtime-gated logging on top — the engine doesn't pretend to.

<a href="assets/typhon-observability-overview.svg">
  <img src="assets/typhon-observability-overview.svg" width="1200" alt="Observability overview">
</a>
<br>
<sub>The single observability surface: typed events flow through the <code>TelemetryConfig</code> JIT gate → <code>TyphonEvent</code> → per-thread ring → consumer → File/TCP exporters → Workbench. Resource-graph <strong>metrics</strong> (via <code>ResourceMetricsExporter</code>) and <strong>logs</strong> (Serilog) are separate paths that bypass the gate and the ring.</sub>

---

## 1. Overview

The observability surface is a single pipeline:

| Layer | What it is |
|---|---|
| **Gate flags** | ~200 `public static readonly bool XxxActive` in [`TelemetryConfig`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/TelemetryConfig.cs). JIT folds disabled branches to no-ops. |
| **Typed events** | Engine call sites call `TyphonEvent.Begin*Event(...)` / `TyphonEvent.Emit*(...)`. Each event is a `ref struct` carrying its payload; `Dispose` publishes the record. |
| **Wire encoding** | Records land in a per-thread SPSC ring buffer (1 MB SOA). A consumer thread drains, compresses, and hands off to exporters. |
| **Exporters** | File (`.typhon-trace`), TCP (live stream), and OTel meters for resource-graph metrics. |
| **Workbench viewer** | TypeScript SPA reading either a trace file (via the Workbench server) or a live TCP stream (proxied through SSE). |

A note on `Activity`: `TyphonActivitySource` exists in the public surface, but the engine never *creates* an `Activity` itself — `TyphonActivitySource.Instance.StartActivity` has zero production call sites. The typed-event pipeline *captures* host-created `Activity` context: when an enclosing `Activity` is set on the calling thread, the engine copies its `TraceId`/`SpanId` into the wire record so logs and traces correlate. Hosts can create their own activities; Typhon will record the correlation.

---

## 2. Gate flags — `TelemetryConfig`

[`Observability/public/TelemetryConfig.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/TelemetryConfig.cs)

A static class holding ~200 `public static readonly bool XxxActive` flags. Each one resolves at class-load time from `typhon.telemetry.json` + environment variables, then never changes. The `static readonly` qualifier is load-bearing — it's what lets the JIT treat `if (TelemetryConfig.XxxActive) { ... }` as dead code when `XxxActive` is false at Tier-1 compilation.

### Naming convention — subsystem-prefixed

Every flag follows `<Subsystem><Detail>Active`. There are ~30 subsystem prefixes; here are representative examples:

| Subsystem family | Example flags |
|---|---|
| Concurrency | `ConcurrencyAccessControlActive`, `ConcurrencyAccessControlSharedAcquireActive`, `ConcurrencyResourceAccessControlActive`, `ConcurrencyEpochScopeEnterActive`, `ConcurrencyAdaptiveWaiterYieldOrSleepActive`, `ConcurrencyOlcLatchWriteUnlockActive` |
| Spatial | `SpatialQueryAabbActive`, `SpatialRTreeNodeSplitActive`, `SpatialClusterMigrationDetectActive`, `SpatialTriggerEvalActive` |
| Scheduler / Runtime | `SchedulerSystemCompletionActive`, `SchedulerWorkerBetweenTickActive`, `SchedulerOverloadLevelChangeActive`, `RuntimePhaseUoWFlushActive`, `RuntimeTransactionLifecycleActive`, `RuntimeWriteTickFenceTableActive`, `RuntimeThreadSchedulingActive` |
| Storage / Memory | `StoragePageCacheActive`, `StoragePageCacheDirtyWalkActive`, `StorageSegmentGrowActive`, `MemoryAlignmentWasteActive` |
| Data plane | `DataTransactionActive`, `DataTransactionPrepareActive`, `DataMvccChainWalkActive`, `DataIndexBTreeActive`, `DataIndexBTreeSearchActive`, `DataIndexBTreeNodeCowActive` |
| Query / ECS | `QueryParseActive`, `QueryExecuteIterateActive`, `EcsQueryConstructActive`, `EcsViewIncrementalDrainActive`, `EcsViewDeltaBufferOverflowActive` |
| Durability | `DurabilityWalOsWriteActive`, `DurabilityWalGroupCommitActive`, `DurabilityCheckpointWriteBatchActive`, `DurabilityRecoveryFpiActive`, `DurabilityUowStateActive` |

The full prefix is mandatory at the call site — there is no `AccessControlActive` (legacy docs claimed there was). It's always `ConcurrencyAccessControlActive`. Same for `StoragePageCacheActive` (not `PagedMMFActive`), `DataIndexBTreeActive` (not `BTreeActive`), `DataTransactionActive` (not `TransactionActive`).

### Parent-implies-children resolution

[`TelemetryConfigResolver.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/TelemetryConfigResolver.cs) walks a tree of `Node`s and resolves each leaf as `master && parent && self`. Flipping `Typhon:Profiler:Concurrency:Enabled = true` turns on every Concurrency sub-leaf at once unless a leaf is explicitly set to `false`. The 30-odd subsystem trees are defined inline in the `TelemetryConfig` static constructor — read it directly if you need the exact shape; the trees match the directories under [`Profiler/internals/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Profiler/internals).

### Initialization

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
public static void EnsureInitialized() => _ = Enabled;
```

Touching any static field triggers the static constructor. `EnsureInitialized()` is the public entry point — it forces the static-ctor *before* hot paths get JIT'd, so the gates are baked into the IL at Tier-1 compilation.

The engine calls it automatically via `[ModuleInitializer]` in [`ProfilerBootstrap.Initialize()`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Profiler/internals/ProfilerBootstrap.cs):

```csharp
[ModuleInitializer]
internal static void Initialize()
{
    TelemetryConfig.EnsureInitialized();
    if (TelemetryConfig.ProfilerActive && !SpilloverRingPool.IsInitialized)
    {
        var options = new ProfilerOptions();
        SpilloverRingPool.Initialize(options.SpilloverBufferCount, options.SpilloverBufferSizeBytes);
    }
}
```

CA2255 (module-initializer-in-library) is suppressed deliberately: it's the only way to get the JIT gate baked with **zero host-side code** — which is the design goal, profiler integration that needs no host boilerplate (startup collapses to a single `TyphonProfiler.Start(runtime)` call). Hosts that *don't* use `Typhon.Engine` (tests, tooling) don't trigger the initializer either, so nothing accidental happens.

### `GetConfigurationSummary()`

```csharp
public static string GetConfigurationSummary() => $"""
  Typhon Profiler Configuration:
    Config File: {LoadedConfigurationFile ?? "(none)"}
    Master Enabled: {Enabled}
    Profiler: Active={ProfilerActive}
      GcTracing={...}, MemoryAllocations={...}, Gauges={...}, CpuSampling={...}
    Scheduler: Active={SchedulerActive}
      Enabled={...}, TransitionLatency={...}, WorkerUtilization={...}, ...
""";
```

Log it once at startup so you have a clear record of what was on for the run.

### Configuration precedence

`typhon.telemetry.json` (current dir → assembly dir) → environment variables (`TYPHON__PROFILER__ENABLED=true`, etc., using `__` as section separator).

```json
{
  "Typhon": {
    "Profiler": {
      "Enabled": true,
      "Concurrency": { "Enabled": true, "AccessControl": { "Contention": { "Enabled": true } } },
      "Storage":     { "PageCache": { "Enabled": true } }
    }
  }
}
```

---

## 3. The typed-event pipeline

### Producer call sites

Every instrumented subsystem calls one of two patterns:

```csharp
// Span — Begin returns a ref struct, Dispose publishes
using (TyphonEvent.BeginTransactionCommitEvent(tsn, componentCount: n)) { /* work */ }

// Instant — single Emit call
TyphonEvent.EmitConcurrencyAccessControlContention(threadId);
```

Both factories start with the same prologue:

```csharp
if (!TelemetryConfig.ProfilerActive) return default;     // JIT-folded when disabled
if (SuppressedKinds[(int)kind]) return default;          // per-kind deny-list (see below)
var idx = ThreadSlotRegistry.GetOrAssignSlot();          // claim TLS slot
// ... capture timestamp, span id, parent linkage, Activity.Current ...
```

The `ProfilerActive` check is the JIT-fold seam. When false, the entire prologue body — including the suppressed-kinds array load, the slot registry call, and the timestamp capture — collapses to `return default`. Zero CPU at the call site.

[`Profiler/internals/TyphonEvent.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Profiler/internals/TyphonEvent.cs) owns the prologue (`BeginPrologue`) and the publishing path (`Publish<T>` with a `where T : struct, ITraceEventEncoder, allows ref struct` constraint that lets the JIT inline the full encode for each concrete event type).

### `TraceEventKind` — ~217 kinds across Phases 2-8

[`src/Typhon.Profiler/TraceEventKind.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Profiler/TraceEventKind.cs) — wire-stable byte enum, public so external decoders (Workbench client, offline tools) can read trace files.

Sparse by design — gaps left for related categories to grow contiguously. Approximate inventory:

| Range | Phase | Examples |
|---|---|---|
| 0–9 | Instants — base | `TickStart`, `TickEnd`, `PhaseStart`/`End`, `SystemReady`, `SystemSkipped`, `Instant`, `GcStart`, `GcEnd`, `MemoryAllocEvent` |
| 10 | Span — base | `SchedulerChunk` |
| 20–23 | Transaction | `TransactionCommit`, `TransactionRollback`, `TransactionCommitComponent`, `TransactionPersist` |
| 30–35 | ECS | `EcsSpawn`, `EcsDestroy`, `EcsQueryExecute`/`Count`/`Any`, `EcsViewRefresh` |
| 40–43 | B+Tree | `BTreeInsert`, `BTreeDelete`, `BTreeNodeSplit`, `BTreeNodeMerge` |
| 50–59 | Page cache | `PageCacheFetch`, `PageCacheDiskRead`/`Write`/`Flush`, async `Completed` peers, `PageEvicted`, `PageCacheBackpressure` |
| 60, 61-63 | Cluster | `ClusterMigration` (60), `WriteTickFenceCluster`/`-Shadow`/`-Spatial` (61-63) |
| 75–77 | Misc | `GcSuspension`, `PerTickSnapshot`, `ThreadInfo` |
| 80–89 | WAL + Checkpoint + Statistics | `WalFlush` (soft-deprecated), `WalSegmentRotate`, `WalWait`, `CheckpointCycle`/`Collect`/`Write`/`Fsync`/`Transition`/`Recycle`, `StatisticsRebuild` |
| 90–116 | **Concurrency** | AccessControl / AccessControlSmall / ResourceAccessControl / Epoch / AdaptiveWaiter / OlcLatch instants |
| 117–145 | **Spatial** | Query (AABB/Radius/Ray/Frustum/KNN/Count), RTree structural, Grid, Cell:Index, ClusterMigration, TierIndex, Maintain, Trigger |
| 146–164 | **Scheduler / Runtime** | System lifecycle, Worker idle/wake/between-tick, Dependency, Overload trio, Graph build/rebuild, UoW create/flush, Transaction lifecycle, Subscription output |
| 165–172 | **Storage / Memory** | DirtyWalk, Segment Create/Grow/Load, ChunkSegmentGrow, FileHandle, OccupancyMapGrow, AlignmentWaste |
| 173–186 | **Data plane** | Transaction Init/Prepare/Validate/Conflict/Cleanup, MVCC ChainWalk / VersionCleanup, B+Tree Search/RangeScan/Rebalance/BulkInsert/Root/NodeCow |
| 187–213 | **Query / ECS:Query / ECS:View** | Parse, DNF, Plan, Estimate, IndexScan, Iterate, Filter, Pagination, plus ECS depth |
| 214–234 | **Durability** | WAL split (QueueDrain/OsWrite/Signal), GroupCommit, Queue, Buffer, Frame, Backpressure; Checkpoint depth; Recovery; UoW state/deadline |
| 235–240 | **Subscription dispatch** | Subscriber, DeltaBuild, DeltaSerialize, TransitionBeginSync, Cleanup, DirtyBitmapSupplement |
| 241–245 | Scheduler follow-ups (#289, #311, #327) | MetronomeWait (span), OverloadDetector (instant), RuntimePhaseSpan, QueueTickEnd, SchedulerSystemArchetype |
| 246 | Fallback | `NamedSpan` — user-defined span with inline UTF-8 name (was 200 until 2026-05-10, reassigned to avoid collision with `EcsQueryMaskAnd`; wire format bumped from v7 to v8) |
| 247–248 | Query Definition Export (#342) | `QueryDefinitionDescribe`, `QueryArgs` — variable-length payloads |
| 249–253 | Spatial/Fence detail | `SpatialClusterMigrationDetectScan`, `SpatialClusterAabbRefresh`, `WriteTickFenceTable`/`-Shadow`/`-Spatial` |
| 254 | OS thread scheduling | `ThreadContextSwitch` (Windows ETW kernel logger, Admin-only) |

Total declared kinds: **~217**. Earlier docs claimed 37 / 38 — that was the count at Phase 1, before the Phase 2-8 explosion.

### Per-kind suppression deny-list

[`TyphonEvent.cs:68-99`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Profiler/internals/TyphonEvent.cs) — a `bool[256]` indexed by `TraceEventKind`. When entry is true, `BeginPrologue` short-circuits even if the gate is on. Reserved for truly extreme-frequency kinds (≥10⁵/sec on realistic workloads — `PageCacheFetch`, `DataMvccChainWalk`, `DataIndexBTreeSearch`, `EcsViewProcessEntry`, `DurabilityWalFrame`, …). Diagnostic-grade kinds (per-tick, per-UoW) are gated solely by their JSON category. Operators flip via `TyphonEvent.UnsuppressKind(kind)` for ad-hoc deep-diving.

### Per-thread ring buffer — 1 MB SOA

[`ThreadSlotRegistry.cs:54`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Profiler/internals/ThreadSlotRegistry.cs)

```csharp
private const int DefaultBufferCapacity = 1 * 1024 * 1024;  // 1 MB per slot
```

Earlier docs said 128 KB. The size has been raised twice (128 KB → 1 MB → 4 MB constant in design notes); the current shipped value is 1 MB.

- **256 slots maximum** — `MaxSlots = 256`, an `EpochThreadRegistry`-shape array of cache-line-padded structs.
- **Per-slot SPSC ring** — variable-size records; producer is the owning thread, consumer is the profiler drain thread.
- **Lazy allocation** — each slot's 1 MB backing array is allocated on first claim, reused across re-claims of the same slot. Idle slots cost ~30 B (object header + fields).
- **CAS claim, finalizer release** — slots transition Free → Active via `Interlocked.CompareExchange`; a `[ThreadStatic] SlotReleaser : CriticalFinalizerObject` runs on thread death and marks the slot `Retiring`.
- **Drop-newest on overflow** — when the ring fills, new records are dropped (and accounted for in a counter the consumer surfaces). The 1 MB budget gives ≥100 K records of breathing room — sized to absorb realistic spawn bursts and parallel-query result flushes.

### Spillover ring pool

[`SpilloverRingPool.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Profiler/internals/SpilloverRingPool.cs) — chained backup buffers that absorb the gap between *gate-open* and *consumer-running*. Allocated eagerly in `ProfilerBootstrap.Initialize` so events emitted during host startup (pre-`TyphonProfiler.Start`) don't drop.

---

## 4. Source location instrumentation

[`Profiler/internals/TraceSpanHeader.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Profiler/internals/TraceSpanHeader.cs), [`src/Typhon.Generators/SourceLocationGenerator.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Generators/SourceLocationGenerator.cs)

Every span carries a `SourceLocationId` — a compile-time-assigned `ushort` that maps to (file, line, method, kind). A C# 14 interceptor mechanism bakes the literal id into the IL at each call site:

1. `SourceLocationGenerator` (Roslyn source generator) discovers every `TyphonEvent.BeginXxx(...)` call site in `Typhon.Engine`, sorts them by `(filePath, line, column)` for determinism, assigns ids 1..N.
2. For each site, it emits an `[InterceptsLocation]` wrapper that forwards to the matching `BeginXxxWithSiteId(...)` overload — the wrapper passes the literal id constant.
3. It also emits a `SourceLocations` table mapping id → (fileId, line, methodId, kind), serialised into the trace file's `SourceLocationManifest` section.

The generator's scope is **`Typhon.Engine` only** — other consumers (tests, tools) call the un-intercepted `Begin*` factories and pass siteId = 0 ("unknown source").

The trace file's `FileTable` section dedupes source file paths into `u16` file ids; the `SourceLocationManifest` lists `(siteId, fileId, line, kind, methodId)` quads. The Workbench reads both at session-open and renders span hierarchies with clickable source attribution.

Wire-level: when the event has `SpanFlagsHasSourceLocation` set (bit 1 of the `SpanFlags` byte), 2 trailing bytes after the trace context carry the `SourceLocationId`. See §5.

---

## 5. Wire protocol

[`src/Typhon.Profiler/TraceRecordHeader.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Profiler/TraceRecordHeader.cs) — record layout. [`LiveStreamProtocol.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Profiler/LiveStreamProtocol.cs) — TCP envelope.

### Record layout

Every record starts with the **common header** (12 B, little-endian):

| Offset | Field | Notes |
|---|---|---|
| 0..1 | `u16 Size` | Total record size including these 2 bytes. `0` = empty slot, `0xFFFF` = wrap sentinel. |
| 2 | `u8 Kind` | `TraceEventKind` discriminant. |
| 3 | `u8 ThreadSlot` | Producer slot index (0..255). |
| 4..11 | `i64 StartTimestamp` | `Stopwatch.GetTimestamp()` at Begin (span) or Emit (instant). |

For span records (kind ≥ 10 with carve-outs per `TraceEventKindExtensions.IsSpan` — see [`TraceEventKind.cs:1062`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Profiler/TraceEventKind.cs)), a **span header extension** (25 B) follows:

| Offset | Field | Notes |
|---|---|---|
| 12..19 | `i64 DurationTicks` | End-timestamp delta. `0` allowed for open/crashed spans. |
| 20..27 | `u64 SpanId` | Unique per span (0 disallowed for real spans). |
| 28..35 | `u64 ParentSpanId` | Enclosing Typhon span on this thread, or 0 for top-level. |
| 36 | `u8 SpanFlags` | Bit 0 = has trace context (16 B follow). Bit 1 = has SourceLocationId (2 B follow). |

When `SpanFlagsHasTraceContext` (0x01) is set, the next **16 B** carry `TraceIdHi`/`TraceIdLo` (W3C trace context copied from `Activity.Current` at span open).

When `SpanFlagsHasSourceLocation` (0x02) is set, the next **2 B** carry the `SourceLocationId`.

### `LiveStreamProtocol` — TCP envelope

```csharp
// Frame: [u8 type][u32 length][payload]
public enum LiveFrameType : byte
{
    Init                    = 1,    // file header + system/archetype/component tables
    Block                   = 2,    // one LZ4-compressed record block per consumer drain
    Shutdown                = 3,    // end of session
    FileTable               = 4,    // interned source file paths (#302)
    SourceLocationManifest  = 5,    // id → (fileId, line, kind, method) (#302)
}
```

Init + FileTable + SourceLocationManifest are sent once during the handshake. After that the stream carries a sequence of Block frames (LZ4-compressed bundles of typed records), terminated by Shutdown.

### `Chunker` version — v16

[`TraceFileCache.cs:553`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Profiler/TraceFileCache.cs):

```csharp
public const ushort CurrentChunkerVersion = 16;
```

The chunker version stamps the Workbench-side cache. Bumped on any change that affects how records fold into the on-disk cache (new sections, decode-semantic changes). Earlier docs cited v8 — the version has advanced through schema changes for component definitions (v13), system-archetype touches (v15), and the NamedSpan kind reassignment (v16).

The trace **file** format also has its own version — [`TraceFileHeader.cs:149`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Profiler/TraceFileHeader.cs):

```csharp
public const ushort CurrentVersion = 11;  // Track→DAG partitioning (#354)
```

v11 adds the Tracks + DAGs tables to support the Track→DAG hierarchy [10-runtime](10-runtime.md). The Workbench reader hard-rejects v10-and-older — re-record against a v11-aware build.

---

## 6. ETW off-CPU pump

[`Profiler/internals/EtwSchedulingPump.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Profiler/internals/EtwSchedulingPump.cs)

Windows-only, opt-in. Gated on `RuntimeThreadSchedulingActive`. Opens the singleton NT Kernel Logger ETW session with the `ContextSwitch` + `Dispatcher` keywords, runs `TraceEventSession.Source.Process()` on a dedicated thread, and emits one `ThreadContextSwitch` record (kind 254) per ON-CPU slice closed for a Typhon-registered OS thread.

The pump tracks per-OS-TID state (`StartTick`, `ReadyTick`, `ProcessorNumber`), emits on each `CSwitchOldThread`, and reattributes the record to the correct Workbench lane via a `TargetSlotIdx` payload byte (the pump itself produces the record on its own slot — the wire format separates "producer" from "subject").

**Privileges:** requires Administrator or `Performance Log Users` membership.
**Singleton:** PerfView / WPR / xperf will collide on the NT Kernel Logger.
**Graceful degradation:** `Start` catches `UnauthorizedAccessException` and writes one diagnostic line to `Console.Error`. The engine continues without scheduling data.
**Clock:** ETW `TimeStampQPC` = `QueryPerformanceCounter` = `Stopwatch.GetTimestamp()` on Windows. No conversion — slice timestamps cross-walk directly into the trace's time space.

Output: off-CPU gaps visible in the Workbench timeline overlaid on the affected thread's lane, with wait reason annotated (kernel `WaitReason`, thread state, processor number, ready-queue latency).

---

## 7. Profiler engine pipeline

[`Profiler/internals/ProfilerBootstrap.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Profiler/internals/ProfilerBootstrap.cs), [`ProfilerConsumerThread.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Profiler/internals/ProfilerConsumerThread.cs), [`Profiler/public/ProfilerLauncher.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Profiler/public/ProfilerLauncher.cs)

<a href="assets/typhon-profiler-architecture.svg">
  <img src="assets/typhon-profiler-architecture.svg" width="1200" alt="Profiler architecture">
</a>
<br>
<sub>End-to-end profiler topology: engine-side capture (instrumented call sites + auto-capture → <code>TyphonEvent</code> → per-slot rings → consumer → exporters → <code>.typhon-trace</code> v11), the <code>Typhon.Workbench</code> ASP.NET Core server (live TCP ingest, cache builder writing the v16 sidecar, REST + SSE endpoints), and the Preact browser viewer (file + live pipelines, worker pool, OPFS chunk store).</sub>

<a href="assets/typhon-profiler-engine-pipeline.svg">
  <img src="assets/typhon-profiler-engine-pipeline.svg" width="1200" alt="Profiler engine pipeline">
</a>
<br>
<sub>The engine-side capture hot path: producers (instrumented call sites, ~217 kinds, plus GC / gauge / CPU-sampling sub-pipelines) → JIT gate → per-thread 1 MB SPSC ring → <code>ProfilerConsumerThread</code> (drain · sort · LZ4) → exporter fan-out (File / TCP).</sub>

### Bootstrap & start

`[ModuleInitializer]` in `ProfilerBootstrap.Initialize` runs at assembly load, before any hot path JITs. It:
1. Forces `TelemetryConfig.EnsureInitialized()` so the gates resolve before any IL bakes them.
2. Eagerly allocates `SpilloverRingPool` when `ProfilerActive` is true — early host emissions (engine bridge construction, bulk spawn) extend the chain instead of dropping.

`ProfilerBootstrap.TryStart(runtime, sp)` runs at the end of `TyphonRuntime.Create`. When `ProfilerActive` is on AND a launch config declares an output channel:
1. Resolves `ProfilerLaunchConfig` (file + env + optional host DI override via `AddTyphonProfiler` + `ProfilerLaunchOverride`).
2. Builds exporter list — `FileExporter` for a `.typhon-trace` path, `TcpExporter` for a live port. **CPU sampler starts before metadata is built** so its QPC anchor lands in the trace header (ordering matters).
3. Builds `ProfilerSessionMetadata` (system definitions, archetype catalog, component types, runtime config, resource graph snapshot — all the static-structure tables the Workbench needs to label spans meaningfully).
4. Calls `TyphonProfiler.Start(parent, metadata, processExitTeardown: FinishStop)`.
5. Subscribes `runtime.Engine.MMF.DisposingEvent` to `FinishStop` so the trace finalizes deterministically when the engine's storage tears down (after the engine's own shutdown teardown, so engine-shutdown events make it into the trace).

### Drain

`ProfilerConsumerThread` drains all 256 slot rings round-robin, batches records via `TraceRecordBatchPool`, compresses with LZ4, and hands the compressed blocks to each attached `IProfilerExporter`. The consumer is one dedicated thread (`Typhon.Profiler-Consumer`).

### `TraceFileCache`

[`TraceFileCache.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Profiler/TraceFileCache.cs), [`IncrementalCacheBuilder.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Profiler/IncrementalCacheBuilder.cs)

The Workbench reads trace files through a **sidecar cache** (`.typhon-trace-cache`) that holds folded sections for fast pan/zoom:
- Per-thread record chunks (LZ4 verbatim).
- Cross-thread roll-ups (span trees, queue rollups, system-archetype touches).
- Static-structure tables (schema, archetypes, indexes, resource graph) lifted out of the trace file.

The cache's `ChunkerVersion` is independent of the trace-file version. Mismatched chunker versions trigger a rebuild — see `TraceFileCacheReader.cs:316` for the reject path.

### CPU sampling integration

When `ProfilerCpuSamplingActive` is on, the bootstrap starts an in-process `CpuSamplerSession` via .NET's EventPipe. Samples land in a temp `.nettrace` file; at session stop, [`CpuSampleParser`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Profiler/internals/CpuSampleParser.cs) transcodes and resolves symbols off-thread (the parse can take seconds — running it during shutdown overlap is the optimisation). Results land in the trace file's `CpuSampleSection` (v10+) where the Workbench overlays them on the timeline.

---

## 8. Workbench viewer flow

<a href="assets/typhon-profiler-viewer-flow.svg">
  <img src="assets/typhon-profiler-viewer-flow.svg" width="1200" alt="Profiler viewer flow">
</a>
<br>
<sub>Workbench viewer flow: file mode (chunk-based lazy load via the v16 sidecar cache, decode ~217 kinds) and live mode (SSE) feed a shared model rendered to canvas; OPFS persists chunk bytes client-side.</sub>

[`tools/Typhon.Workbench/Controllers/ProfilerController.cs`](https://github.com/Log2n-io/Typhon/blob/main/tools/Typhon.Workbench/Controllers/ProfilerController.cs), [`Sessions/TraceSessionRuntime.cs`](https://github.com/Log2n-io/Typhon/blob/main/tools/Typhon.Workbench/Sessions/TraceSessionRuntime.cs), [`ClientApp/src/libs/profiler/`](https://github.com/Log2n-io/Typhon/tree/main/tools/Typhon.Workbench/ClientApp/src/libs/profiler)

The viewer is split between an ASP.NET Core server (`tools/Typhon.Workbench/`) and a TypeScript SPA (`ClientApp/`).

> **Note:** the standalone `Typhon.Profiler.Server` project that earlier docs (and the architecture SVG) referenced has been **retired** — its functionality is merged into `tools/Typhon.Workbench/`. There's only one server now.

### Server side
- **Trace sessions:** the controller opens a `.typhon-trace` + sidecar `-cache` pair via `TraceFileCache` / `TraceFileCacheReader`. Pan/zoom queries hit the cache, not the raw trace.
- **Attach sessions:** the controller connects to a live `TcpExporter` and proxies the binary block stream to the browser via Server-Sent Events. The browser-side decoder treats SSE blocks identically to file-loaded blocks.
- **Workers:** the cache index (per-section worker pool) supports parallel section fetches.

### Client side (`ClientApp/src/libs/profiler/`)
- **Phase 4 cache v16** — the client mirrors the server's `TraceFileCache` schema. Mismatched versions trigger a rebuild.
- **OPFS chunk store** — [`cache/opfsChunkStore.ts`](https://github.com/Log2n-io/Typhon/blob/main/tools/Typhon.Workbench/ClientApp/src/libs/profiler/cache/opfsChunkStore.ts) persists LZ4 chunk bytes verbatim in the browser's Origin Private File System. Calls `navigator.storage.persist()` to keep the browser from evicting under pressure. OPFS failures are caught and treated as "best-effort optimisation" — the server-backed fetch path always works.
- **SSE live mode** — the client's chunk decoder ([`decode/chunkDecoder.ts`](https://github.com/Log2n-io/Typhon/blob/main/tools/Typhon.Workbench/ClientApp/src/libs/profiler/decode/chunkDecoder.ts)) consumes the same wire format whether the source is OPFS, server cache, or live SSE.
- **Worker pool** — record decoding and SOA layout happen in Web Workers so the main thread stays responsive during pan/zoom.

---

## 9. OTel integration

[`Observability/public/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Observability/public)

Separate from the typed-event pipeline — this is **resource-graph metrics** flowing to OTel meters, not span/trace export.

<a href="assets/typhon-telemetry-sinks.svg">
  <img src="assets/typhon-telemetry-sinks.svg" width="1040" alt="Telemetry sinks">
</a>

| Type | Purpose |
|---|---|
| [`ObservabilityBridgeExtensions`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/ObservabilityBridgeExtensions.cs) | `AddTyphonObservabilityBridge(...)` DI extension. Wires the snapshot loop, exporter, alert generator, health checker. |
| [`ObservabilityBridgeOptions`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/ObservabilityBridgeOptions.cs) | Snapshot interval, per-path health thresholds. |
| [`ResourceMetricsExporter`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/ResourceMetricsExporter.cs) | Reads `IResourceGraph` snapshots and exposes them through `Meter` "Typhon.Resources". Observable-instrument pattern: OTel callbacks read a cached snapshot — zero overhead when no consumer is listening. |
| [`ResourceMetricsService`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/ResourceMetricsService.cs) | `IHostedService` that periodically calls `exporter.UpdateSnapshot()`. |
| [`OTelMetricNameBuilder`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/OTelMetricNameBuilder.cs) | Maps resource-graph paths to OTel-compliant metric names. |
| [`EcsMetricsExporter`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/EcsMetricsExporter.cs) | Separate exporter for ECS-specific gauges (per-archetype entity counts, etc.). |
| [`ResourceHealthChecker`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/ResourceHealthChecker.cs) | Implements `ITyphonHealthCheck` against `ResourceMetricsExporter`'s snapshot + threshold config. |
| [`ResourceAlertGenerator`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/ResourceAlertGenerator.cs) | Promotes snapshot anomalies into structured alerts (Severity / SymptomPath / RootCausePath / Timestamp). |
| [`TraceIdEnricher`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/TraceIdEnricher.cs) | Serilog enricher that copies `Activity.Current.TraceId/SpanId` into log events for log↔trace correlation. Zero allocation when no `Activity` is active. |

The resource-graph snapshot is built in [13-resources](13-resources.md). This section is just the path from `IResourceGraph` → OTel meters → Prometheus/OTLP backend.

---

## See also

- [01-foundation](01-foundation.md) — `AccessControl.Telemetry.cs` (mid-migration partial still under `#if TELEMETRY`); the real `TyphonEvent.Emit*` pattern used by every other primitive.
- [02-storage](02-storage.md) — page-cache and segment events (kinds 50-59, 165-171).
- [03-indexing](03-indexing.md) — B+Tree mutation events (40-43, 180-186).
- [06-ecs](06-ecs.md) — `EcsSpawn`/`EcsDestroy`/`EcsQuery*`/`EcsView*` event families.
- [08-transactions](08-transactions.md) — Transaction commit/rollback events; UoW state transitions; the `Activity.Current` capture point.
- [10-runtime](10-runtime.md) — Scheduler/worker/overload events; the Track→DAG hierarchy (v11 trace format).
- [11-durability](11-durability.md) — WAL/Checkpoint/Recovery events; fail-fast (per ADR) surfaces here.
- [13-resources](13-resources.md) — `IResourceGraph` is the source for OTel metrics flowing through `ResourceMetricsExporter`.

<a href="assets/typhon-profiler-wire-protocol.svg">
  <img src="assets/typhon-profiler-wire-protocol.svg" width="1200" alt="Profiler wire protocol">
</a>
<br>
<sub>The wire format: 12 B common header, optional 25 B span extension (when the kind is a span), optional 16 B trace context (SpanFlags bit 0) and 2 B SourceLocationId (bit 1), then the per-kind typed payload (~217 kinds); framed by the LiveStreamProtocol envelope and indexed by the v16 chunker sidecar.</sub>

<a href="assets/typhon-telemetry-tracks.svg">
  <img src="assets/typhon-telemetry-tracks.svg" width="1200" alt="Zero-overhead telemetry gating">
</a>
<br>
<sub>Why disabled telemetry costs nothing: every hot-path call site guards on a <code>public static readonly bool</code> in <code>TelemetryConfig</code>; at Tier-1 the JIT dead-code-eliminates the branch when the flag is false (0 CPU) or emits a <code>TyphonEvent</code> when true. The panel ranks why <code>static readonly</code> is the cheapest gating mechanism. The four call sites shown are representative — there are ~30 subsystem gate families with fully-qualified prefixes.</sub>
