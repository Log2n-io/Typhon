---
uid: overview-resources
title: '13 — Resources'
description: 'Typhon doesn''t ship a separate metrics library, nor does it scatter ad-hoc counters across the engine. Every long-lived object — the page cache, the WAL…'
---

# 13 — Resources

**Code:** [`src/Typhon.Engine/Resources/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Resources) (+ exporters in [`src/Typhon.Engine/Observability/public/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Observability/public))

Typhon doesn't ship a separate metrics library, nor does it scatter ad-hoc counters across the engine. Every long-lived object — the page cache, the WAL ring buffer, the transaction pool, the epoch manager, the scheduler — registers as an [`IResource`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/IResource.cs) under a single hierarchical **resource graph**. The graph is the observability spine: one tree, one snapshot API, one zero-allocation metric write path. Workbench reads it, the OTel exporter reads it, the health checker reads it.

The graph is **passive**. Nodes don't push events; consumers pull snapshots when they want them (typically every 1–5 s for monitoring). Snapshot collection costs ~50 ns per node — a 100-node tree snapshots in ~5 µs. No global lock; per-node reads are atomic; cross-node values are read microseconds apart and explicitly described as "approximate."

If you've built on Typhon, you've used this without noticing. If you want to understand *where* a perf cliff is coming from, this is the chapter.

---

## 1. The graph as observability spine

Every subsystem that owns memory, capacity, throughput or duration registers a node under a fixed set of subsystem roots. Pure grouping nodes (`Storage`, `DataEngine`, …) carry no metrics of their own — they exist to organise the tree. Leaves carry the actual counters via [`IMetricSource`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/IMetricSource.cs).

This unification matters because:

- **One traversal serves every consumer** — Workbench tree view, OTel export, health checks, alert generation — no per-consumer enumeration of subsystems.
- **Add a subsystem, add a node** — instrumentation is part of the construction contract (`IResource`/`IMetricSource`), not a separate concern.
- **Lifetime is explicit** — registration happens at construction; removal happens at disposal; parent disposes children unless they opt out ([§8](#8-lifecycle)).

<a href="assets/typhon-resource-graph-overview.svg">
  <img src="assets/typhon-resource-graph-overview.svg" width="1200" alt="Resource graph overview">
</a>
<br>
<sub>The eight live subsystem subtrees under <code>Root</code> (Storage, DataEngine, Durability, Allocation, Synchronization, Timer, Runtime, Profiler) with the metric kinds each leaf writes. Grouping nodes carry no metrics; segments are aggregated by <code>ComponentTable</code> (not nodes); Runtime / Profiler children are structural/display nodes. There is no <code>Backup</code> subtree — only a reserved enum value.</sub>

---

## 2. Core abstractions

### `IResource` — the node contract

[`Resources/public/IResource.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/IResource.cs)

Every registered object implements this. It is a tree node plus identity:

```csharp
public interface IResource : IDisposable
{
    string                Id          { get; }   // stable key under Parent
    string                Name        { get; }   // human label (Workbench / diagnostics)
    int?                  Count       { get; }   // optional scalar badge (entity count, segment count, ...)
    ResourceType          Type        { get; }   // enum: PageCache, TransactionPool, WAL, ...
    IResource             Parent      { get; }
    IEnumerable<IResource> Children   { get; }
    DateTime              CreatedAt   { get; }
    IResourceRegistry     Owner       { get; }

    bool RegisterChild(IResource child);
    bool RemoveChild(IResource resource);
}
```

`Id` is the path segment used to address the node (`Storage/PageCache`). `Name` is the user-facing label — often equal to `Id`, but resources with synthetic ids (GUIDs, hex suffixes) override it. `Count` is the optional integer the Workbench tree displays as a badge — `null` for pure grouping nodes.

[`ResourceNode`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ResourceNode.cs) is the default implementation. It self-registers under its `Parent` in the constructor, holds children in a `ConcurrentDictionary<string, IResource>`, and raises `NodeMutated` events on its owning registry whenever a child is added or removed.

### `IResourceRegistry` — the per-process root

[`Resources/public/IResourceRegistry.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/IResourceRegistry.cs)

One per process. Holds the `Root` node plus a fixed set of **subsystem nodes**, every one of which is live in [`ResourceRegistry`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ResourceRegistry.cs)'s constructor:

```csharp
public interface IResourceRegistry : IDisposable
{
    IResource Root            { get; }
    IResource Storage         { get; }   // PageCache, ManagedPagedMMF, segments
    IResource DataEngine      { get; }   // DatabaseEngine, ComponentTables, TransactionPool
    IResource Durability      { get; }   // WAL ring / segments, checkpoint
    IResource Allocation      { get; }   // MemoryAllocator, bitmaps
    IResource Synchronization { get; }   // EpochManager, latch pools
    IResource Timer           { get; }   // HighResolutionSharedTimerService
    IResource TimerDedicated  { get; }   //   └── dedicated single-handler timers
    IResource Runtime         { get; }   // DAG scheduler, tick loop, worker pool
    IResource Profiler        { get; }   // Tracy-style capture consumer + exporters

    IResource GetSubsystem(ResourceSubsystem subsystem);
    IResource Register<T>(T resource, ResourceSubsystem subsystem) where T : IResource;
    IResource FindByPath(string path, string separator = "/");

    event Action<ResourceMutationEventArgs> NodeMutated;
}
```

All ten properties are populated unconditionally — there is no optional subsystem and no `Backup` subtree on the registry.

The registry is the only object that can raise `NodeMutated`; the event fires on Add/Remove from a child collection, carrying just enough identification for subscribers to invalidate caches without forcing a full graph copy:

```csharp
public enum ResourceMutationKind
{
    Added,    // new node registered under a parent
    Removed,  // node detached from its parent
    Mutated   // forward-compat slot — not raised by current code
}

public readonly struct ResourceMutationEventArgs
{
    public ResourceMutationKind Kind      { get; init; }
    public string               NodeId    { get; init; }
    public string               ParentId  { get; init; }
    public ResourceType         Type      { get; init; }
    public DateTime             Timestamp { get; init; }
}
```

[`ResourceMutationEventArgs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ResourceMutationEventArgs.cs). Subscribers must not throw and must not mutate the graph from within the handler (would re-enter and recursively raise). The registry isolates faulty handlers via per-subscriber `try`/`catch`, but that is best-effort.

### `ResourceType` — typed nodes

[`Resources/public/IResource.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/IResource.cs) defines a flat enum (`None`, `Node`, `Service`, `Engine`, `TransactionPool`, `Transaction`, `ChangeSet`, `ComponentTable`, `Segment`, `Index`, `Cache`, `File`, `Memory`, `Bitmap`, `Schema`, `Allocator`, `Synchronization`, `WAL`, `Checkpoint`, `Backup`). The enum still carries a `Backup` value, but no node of that type is registered — it's reserved for future use, the same way `ResourceMutationKind.Mutated` is reserved.

---

## 3. Metric model

The metric channel is a separate interface — only nodes with meaningful counters implement it. The grouping nodes don't; aggregates are computed by walking the subtree at snapshot time.

### `IMetricSource`

[`Resources/public/IMetricSource.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/IMetricSource.cs)

```csharp
public interface IMetricSource
{
    void ReadMetrics(IMetricWriter writer);
    void ResetPeaks();
}
```

`ReadMetrics` is called once per snapshot pass; the implementation reads live fields and writes them to the supplied writer. Target budget: <100 ns, **no allocations**, **no locks**, **no recursion into other sources** (the graph drives traversal). Reads of primitives are atomic on x64 — the contract explicitly accepts microsecond-scale cross-field skew.

`ResetPeaks` is called by `IResourceGraph.ResetAllPeaks()` to enable windowed peak measurements (per-window high-water marks after each export).

### `IMetricWriter` — exactly five kinds

[`Resources/public/IMetricWriter.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/IMetricWriter.cs)

The writer surface is deliberately closed. Five methods, five metric kinds:

| Method | Records | Notes |
|---|---|---|
| `WriteMemory(allocatedBytes, peakBytes)` | live + high-water bytes | last call wins per node |
| `WriteCapacity(current, maximum)` | bounded-slot utilisation | last call wins per node; ratio computed by the snapshot builder |
| `WriteDiskIO(readOps, writeOps, readBytes, writeBytes)` | I/O counters | last call wins per node |
| `WriteThroughput(name, count)` | named monotonic counter | **may be called multiple times** — appends |
| `WriteDuration(name, lastUs, avgUs, maxUs)` | named operation timing | **may be called multiple times** — appends |

There is no `WriteContention`. Lock-level telemetry is not a metric kind — it flows through `TyphonEvent.Emit*` typed events to per-thread ring buffers ([01-foundation §1.4](01-foundation.md), [12-observability](12-observability.md)).

Names passed to `WriteThroughput` / `WriteDuration` **must be static strings** — concatenation/interpolation defeats the zero-allocation contract.

### `NodeSnapshot` — what a snapshot row looks like

[`Resources/public/NodeSnapshot.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/NodeSnapshot.cs)

One per node per snapshot:

```csharp
public sealed class NodeSnapshot
{
    public string                       Path        { get; init; }   // "Storage/PageCache"
    public string                       Id          { get; init; }   // "PageCache"
    public ResourceType                 Type        { get; init; }
    public MemoryMetrics?               Memory      { get; init; }   // nullable
    public CapacityMetrics?             Capacity    { get; init; }
    public DiskIOMetrics?               DiskIO      { get; init; }
    public IReadOnlyList<ThroughputMetric> Throughput { get; init; } = [];
    public IReadOnlyList<DurationMetric>   Duration   { get; init; } = [];
}
```

Nullable fields = "node didn't write this kind". `CapacityMetrics.Utilization` is `Current / Maximum`, computed on read.

---

## 4. Snapshots

### `IResourceGraph` — the entry point

[`Resources/public/IResourceGraph.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/IResourceGraph.cs), implementation [`ResourceGraph.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ResourceGraph.cs).

```csharp
public interface IResourceGraph
{
    IResource Root { get; }
    ResourceSnapshot GetSnapshot();                          // full tree
    ResourceSnapshot GetSnapshot(IResource subtreeRoot);     // one subtree
    IResource FindByPath(string path);
    IEnumerable<IResource> FindByType(ResourceType type);
    void ResetAllPeaks();
}
```

A snapshot walks the tree depth-first; each `IMetricSource` node writes into a pooled internal `SnapshotMetricWriter`; the writer is drained into a `NodeSnapshot` indexed by path.

### `ResourceSnapshot` — the immutable result

[`Resources/public/ResourceSnapshot.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ResourceSnapshot.cs)

```csharp
public sealed class ResourceSnapshot
{
    public DateTime                               Timestamp { get; init; }
    public IReadOnlyDictionary<string, NodeSnapshot> Nodes  { get; init; }
    public ThroughputRates                        Rates     { get; init; }   // null on first snapshot
}
```

Snapshots are query-only. Once produced they're safe to read from any thread. The graph keeps a reference to the previous full snapshot and computes `Rates` automatically by differencing throughput counters across timestamps — **rate computation is not part of the public API**. `ComputeRates` is `private` on `ResourceGraph`; consumers just read `snapshot.Rates`.

[`ThroughputRates`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ThroughputRates.cs) is a path → metric-name → ops/sec dictionary indexed by `rates["Storage/PageCache"]["CacheHits"]`.

### Query helpers on the snapshot

The snapshot exposes pure-data queries over the frozen `Nodes` dictionary:

| Method | Returns |
|---|---|
| `GetNode(string path)` | one `NodeSnapshot` or `null` |
| `GetSubtree(string path)` | all nodes at path or below |
| `GetSubtreeMemory(string path)` | sum of `MemoryMetrics.AllocatedBytes` across the subtree — memory attribution per subsystem |
| `FindByType(ResourceType type)` | every node of that type |
| `FindMostUtilized()` | the single highest-utilisation node (capacity-bearing) |
| `FindMostUtilized(double threshold)` | all nodes with utilisation ≥ threshold, sorted descending |
| `FindRootCause(symptomPath, threshold = 0.8)` | follows hardcoded wait dependencies up the chain to the root of a contention cascade |

`FindRootCause` is the interesting one. It encodes a small static table of architectural wait edges (e.g. `DataEngine/TransactionPool` → `Durability/WALRingBuffer` → `Durability/WALSegments`, `Storage/PageCache` → `Storage/ManagedPagedMMF`) and walks from the symptom node, following the most-utilised dependency as long as the trail stays above the threshold. This is a **heuristic**, not runtime dependency tracking — the table is curated based on the engine's architecture.

<a href="assets/typhon-resource-snapshot-flow.svg">
  <img src="assets/typhon-resource-snapshot-flow.svg" width="1200" alt="Resource snapshot collection and export flow">
</a>
<br>
<sub>Snapshot collection → optional rate computation → query helpers / health checks / OTel export. The "OTel Metrics Export" box maps to <a href="https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/ResourceMetricsExporter.cs"><code>ResourceMetricsExporter</code></a> in code.</sub>

---

## 5. Configuration — `ResourceOptions`

[`Resources/public/ResourceOptions.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ResourceOptions.cs)

Lives inside `DatabaseEngineOptions` and is consumed by the components at construction. Set at startup, immutable afterwards. Real defaults — check these against the field declarations:

| Property | Default | Meaning |
|---|---:|---|
| `PageCachePages` | `256` | Pages × 8 KB → **2 MB** default page cache |
| `MaxPageCachePages` | `16384` | Cap for future dynamic resizing — **128 MB** |
| `MaxActiveTransactions` | `1000` | Hard limit on concurrent transactions; `FailFast` beyond |
| `TransactionPoolSize` | `16` | Pooled `Transaction` objects; overflow → allocate (`Degrade`) |
| `WalRingBufferSizeBytes` | `8 << 20` (**8 MB**, not 4 MB) | In-memory WAL stage |
| `WalBackPressureThreshold` | `0.8` | At this fill ratio, commits start blocking |
| `WalMaxSegmentSizeBytes` | `64L << 20` (**64 MB**) | Single segment file size |
| `WalMaxSegments` | `4` | Segments before forced checkpoint |
| `CheckpointMaxDirtyPages` | `10000` | Dirty pages before forced checkpoint |
| `CheckpointIntervalMs` | `30000` (**30 s**) | Idle checkpoint cadence |
| `PageChecksumVerification` | `OnLoad` | CRC every load vs. only during recovery |
| `ShadowBufferPages` | `512` (**4 MB**) | Reserved for CoW backup writer (forward-looking; no Backup subsystem node yet) |
| `TotalMemoryBudgetBytes` | `4L << 30` (**4 GB**) | Validated at startup |
| `PageSizeBytes` | `8192` (const) | Page size for sizing math |

`Validate()` rejects configurations where the **fixed** allocations (page cache + WAL ring + WAL segments + shadow buffer) exceed `TotalMemoryBudgetBytes`. Growable resources (active transactions, index nodes, query buffers) have runtime caps and are excluded from the upfront check. `CalculateAvailableBudgetBytes()` returns what's left for growable resources.

---

## 6. Exhaustion policies

[`Resources/public/ExhaustionPolicy.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ExhaustionPolicy.cs)

`ExhaustionPolicy` is **diagnostic metadata** on a `ResourceNode`, not a runtime dispatch decision. The actual response code is wired into each component — the enum value on the node tells you "if this resource fills up, what happens?" so the Workbench / health checker can present it.

```csharp
public enum ExhaustionPolicy
{
    None     = 0,   // structural node, no bounded resource
    FailFast = 1,   // throw ResourceExhaustedException
    Wait     = 2,   // block until space frees up (respects Deadline)
    Evict    = 3,   // remove LRU entry, retry
    Degrade  = 4    // continue with reduced performance / fallback
}
```

A component can use multiple policies in sequence (page cache: `Evict` clean pages first, then `Wait` if all are pinned). The enum on the node represents the *primary* policy. The policy is hardcoded per component — not configurable — because it's a semantic property: a cache *must* evict, a WAL ring *must* wait, a client-facing limit *must* fail fast.

[`ResourceExhaustedException`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ResourceExhaustedException.cs) is the canonical exception raised by `FailFast` paths; it carries the offending resource path, its type, and current vs. maximum so the operator gets actionable diagnostics.

---

## 7. Alerts & health

The graph is the data source; alerting and health checks live next door in the Observability folder and consume snapshots.

### `ResourceAlertGenerator`

[`Observability/public/ResourceAlertGenerator.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/ResourceAlertGenerator.cs)

Walks a snapshot, finds capacity-bearing nodes above configured thresholds, and constructs a `ResourceAlert` per crossing. Severity is `Warning` (degraded threshold) or `Critical` (unhealthy threshold). The root-cause field comes from `snapshot.FindRootCause(symptomPath, degradedThreshold)`:

```csharp
public sealed class ResourceAlert
{
    public AlertSeverity Severity              { get; init; }
    public string        Title                 { get; init; }   // "DataEngine/TransactionPool at 95% utilization"
    public string        SymptomPath           { get; init; }
    public double        SymptomUtilization    { get; init; }
    public string        RootCausePath         { get; init; }
    public double        RootCauseUtilization  { get; init; }
    public DateTime      Timestamp             { get; init; }
}
```

Root-cause attribution is the single supported chain trace — the alert carries the symptom node and the upstream root cause, nothing further downstream.

`GenerateAlerts(snapshot)` returns the cross-cutting set; `GenerateAlert(snapshot, symptomPath)` produces one for a single node (returns `null` if the node is healthy).

### `ResourceHealthChecker`

[`Observability/public/ResourceHealthChecker.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/ResourceHealthChecker.cs)

Implements [`ITyphonHealthCheck`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/ITyphonHealthCheck.cs). "Worst-of-all" pattern: the overall status is the most severe state across the tree, so a single overloaded resource is never lost in a sea of green. Critical subsystems (`Durability/WALRingBuffer`, `DataEngine/TransactionPool`) use tighter default thresholds (60 % degraded / 80 % unhealthy) than general nodes (80 / 95).

The checker reads `ResourceMetricsExporter.CurrentSnapshot` rather than calling `GetSnapshot()` itself — keeps the snapshot cadence centralised in `ResourceMetricsService`.

---

## 8. Lifecycle

Resources are constructed under an explicit parent (`new ResourceNode(id, type, parent, ...)`). `Parent.RegisterChild(this)` runs in the constructor; `parent` must not be `null` — there's no orphan container. The registry's `Dispose()` cascades through subsystems in a **deliberately ordered** sequence (see `ResourceRegistry.Dispose`): `Profiler → Runtime → DataEngine → Durability → Storage → Allocation → Synchronization → Timer → Root`. The order is load-bearing — `DataEngine`'s graceful shutdown does a final checkpoint that needs `Storage`, `Durability`, and `Synchronization` to still be alive (per design rule).

Inside each subsystem, `ResourceNode.Dispose(bool)` walks the children. A child whose lifecycle is owned **outside** the resource tree can opt out:

```csharp
public class TcpExporter : ResourceNode
{
    // Owned by TyphonProfiler — only the profiler should dispose it.
    // It stays in the tree for display, but parent teardown skips it.
    public override bool DisposeWithParent => false;
}
```

The cascade in `ResourceNode.Dispose(bool)` checks `node.DisposeWithParent` and skips nodes that returned `false`. Default is `true` — opt-out is rare and currently used only by the profiler exporters (`TcpExporter`, `FileExporter`).

---

## 9. OTel export surface

The OTel-facing classes that consume the graph:

- [`ResourceMetricsExporter`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/ResourceMetricsExporter.cs) — observable-pattern `Meter` instruments named `Typhon.Resources`; OTel callbacks read from a cached snapshot, zero overhead when no consumer listens.
- [`ResourceMetricsService`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/ResourceMetricsService.cs) — hosted service that periodically updates the cached snapshot.
- [`OTelMetricNameBuilder`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/OTelMetricNameBuilder.cs) — canonical naming for the exported instruments.
- [`EcsMetricsExporter`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Observability/public/EcsMetricsExporter.cs) — ECS-specific exporter, parallel to the resource exporter.

`ResourceHealthChecker` covers the health-check side of the same surface (see §7).

---

## 10. Tree topology — the live subsystem layout

```
Root
├── Storage/          PageCache, ManagedPagedMMF, segments
├── DataEngine/       DatabaseEngine_<id>/ComponentTable_<T>, TransactionPool, ChangeSet
├── Durability/       WAL ring buffer, WAL segment manager, Checkpoint
├── Allocation/       MemoryAllocator, ConcurrentBitmapL3All, ...
├── Synchronization/  EpochManager, latch pools
├── Timer/            HighResolutionSharedTimerService
│   └── Dedicated/    HighResolutionTimerService instances
├── Runtime/          TickDriver, worker pool, DAG scheduler
└── Profiler/         Tracy-style consumer, TcpExporter, FileExporter (DisposeWithParent = false)
```

<a href="assets/typhon-resource-tree-topology.svg">
  <img src="assets/typhon-resource-tree-topology.svg" width="1200" alt="Resource tree topology">
</a>
<br>
<sub>The live tree topology: <code>Root</code> → eight subsystem grouping nodes → metric/structural leaves, matching the registry constructor. <code>Timer</code> nests <code>Dedicated/</code>; <code>UowRegistry</code> sits under <code>Durability</code>; segments are aggregated by <code>ComponentTable</code> rather than being graph nodes. No <code>Backup</code> subtree, no <code>PrimaryKeyIndex</code>.</sub>

---

## See also

- [01-foundation](01-foundation.md) — `MemoryAllocator`, `EpochManager`, `HighResolutionTimerService` / `HighResolutionSharedTimerService` all register themselves under the corresponding subsystem nodes
- [02-storage](02-storage.md) — `PageCache` as a resource with `Capacity` + `Throughput` (`CacheHits`/`CacheMisses`), `Memory` for managed-page bytes
- [08-transactions](08-transactions.md) — `TransactionPool` (`Capacity`, `Throughput` for commits/rollbacks) and the `UowRegistry` slot pool exposed via the resource graph
- [11-durability](11-durability.md) — WAL ring buffer (`Capacity`), WAL segment manager (`DiskIO`), checkpoint (`Duration`) all surface here
- [12-observability](12-observability.md) — `TyphonEvent.Emit*` typed events, OTel exporters built on top of `ResourceMetricsExporter`, `TraceIdEnricher` for trace correlation
