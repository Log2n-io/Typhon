# Metric Reporting (IMetricSource / IMetricWriter)
> Zero-cost-until-asked metric collection for every significant engine resource.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Resources](./README.md)

## 🎯 What it solves
Diagnosing capacity problems, sizing budgets, and wiring OpenTelemetry export all need numeric
visibility into engine internals — memory used, slots free, I/O volume, op rates, latencies. A
push/event-based instrumentation model would tax every hot-path operation even when nobody is
watching. Applications need a way to ask "what's the current state of this resource?" without
paying for it between asks.

## ⚙️ How it works (in brief)
Resources with measurable state implement `IMetricSource` alongside their place in the resource
tree. They maintain plain fields (counters, gauges) as a side effect of normal operation — no
locks, no `Interlocked` for single-writer counters. When a snapshot is requested, the resource
graph walks the tree and calls `ReadMetrics(writer)` once per node; the node writes whichever of
the five metric kinds (Memory, Capacity, DiskIO, Throughput, Duration) are relevant to it. Reads
are approximate (no synchronization) and per-node atomic only. Fine-grained primitives — latches,
chunks, segments — are not graph nodes; their owning component aggregates and reports on their
behalf (Owner Aggregates pattern), keeping the tree at ~50-300 nodes regardless of entity count.

## 💻 Usage
```csharp
using Typhon.Engine;

// Implementing IMetricSource on a custom resource node
public class MyCache : ResourceNode, IMetricSource
{
    private long _hits;
    private long _misses;
    private long _usedSlots;
    private readonly long _totalSlots;
    private long _peakBytes;
    private long _currentBytes;

    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteMemory(_currentBytes, _peakBytes);
        writer.WriteCapacity(_usedSlots, _totalSlots);
        writer.WriteThroughput(MetricNames.CacheHits, _hits);
        writer.WriteThroughput(MetricNames.CacheMisses, _misses);
    }

    public void ResetPeaks() => _peakBytes = _currentBytes;
}

// Reading a snapshot (e.g. periodic monitoring or a health check)
ResourceSnapshot snapshot = resourceGraph.GetSnapshot();
NodeSnapshot pageCache = snapshot.GetNode("Root/Storage/PageCache"); // Nodes[] keys include the "Root/" prefix
if (pageCache?.Capacity is { } capacity)
{
    Console.WriteLine($"PageCache utilization: {capacity.Utilization:P1}");
}
foreach (var t in pageCache.Throughput)
{
    Console.WriteLine($"{t.Name}: {t.Count}");
}

resourceGraph.ResetAllPeaks(); // start a new peak-measurement window
```

| Metric kind | Reports | Typical source |
|---|---|---|
| Memory | `AllocatedBytes`, `PeakBytes` | Allocators, caches, buffers |
| Capacity | `Current`, `Maximum` (`Utilization` derived) | Pools, segments, bitmaps |
| DiskIO | `ReadOps`/`WriteOps`/`ReadBytes`/`WriteBytes` | PageCache, WAL, checkpoint |
| Throughput | named `Count` | Hits/misses, commits, inserts |
| Duration | named `LastUs`/`AvgUs`/`MaxUs` | Flush, checkpoint, commit |

## ⚠️ Guarantees & limits
- Zero hot-path cost: fields are updated with plain writes (or `Interlocked` only where
  multi-writer); the read side only runs when a snapshot is taken.
- `ReadMetrics` targets < 100 ns per node and must not allocate, lock, or call other sources —
  recursion is handled by the graph during tree walk.
- Snapshot consistency is per-node atomic, cross-node approximate (nodes are read microseconds
  apart); a full snapshot over 50-100 nodes costs roughly 2.5-5 μs.
- `WriteThroughput`/`WriteDuration` names must be static strings (use `MetricNames` constants) —
  this is a documented convention, not runtime-enforced.
- High-water marks (`PeakBytes`, `MaxUs`) use plain check-and-write; an occasional lost update
  under a race is accepted as a diagnostic-grade trade-off.
- Not every resource implements `IMetricSource` — pure grouping nodes ("Root", "Storage") report
  nothing of their own; subtree totals are computed by the snapshot query API.
- Latches, chunks, segments, and other sub-500-instance primitives never become graph nodes — look
  for their numbers aggregated on the owning `ComponentTable`/cache/allocator instead.

## 🧪 Tests
- [MetricSourceTests](../../../test/Typhon.Engine.Tests/Resources/MetricSourceTests.cs) — `IMetricWriter`/`IMetricSource` contract,
  partial-metrics reporting, `GetMetricSources` tree walk (self-inclusion, skipping non-sources, from-root traversal)
- [MetricTypesTests](../../../test/Typhon.Engine.Tests/Resources/MetricTypesTests.cs) — the 5 metric-kind value types
  (Memory/Capacity/DiskIO/Throughput/Duration), `CapacityMetrics.Utilization` edge cases, `MetricNames` constants

## 🔗 Related
- Sibling: [Debug Properties Drill-Down](./debug-properties-drilldown.md) — the allocation-tolerant counterpart for ad-hoc diagnostic detail.
- Sibling: [Snapshot & Query API](./snapshot-query-api/README.md) — freezes these metrics into a point-in-time snapshot for querying.

<!-- Deep dive: claude/design/Resources/03-metric-source.md, claude/design/Resources/04-metric-kinds.md, claude/design/Resources/05-granularity-strategy.md -->
<!-- Decision record: claude/adr/032-resource-system-architecture.md (see partial-supersession note — IMetricWriter ships with 5 methods; contention flows through IContentionTarget instead) -->
