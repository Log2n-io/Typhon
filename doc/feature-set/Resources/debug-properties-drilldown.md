---
uid: feature-resources-debug-properties-drilldown
title: 'Debug Properties Drill-Down (IDebugPropertiesProvider)'
description: 'An ad-hoc, allocation-tolerant key/value dictionary for per-resource diagnostic drill-down.'
---

# Debug Properties Drill-Down (IDebugPropertiesProvider)
> An ad-hoc, allocation-tolerant key/value dictionary for per-resource diagnostic drill-down.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Resources](./README.md)

## 🎯 What it solves

`IMetricSource` is deliberately narrow — five fixed kinds (Memory, Capacity, DiskIO, Throughput,
Duration), zero-allocation, read on every snapshot. That shape is right for dashboards and alerts,
but wrong for "why is *this specific* `ComponentTable` behaving oddly?" questions: per-segment
chunk counts, page-cache state histograms, schema names, transaction-chain TSNs — detail that's
too verbose, too shaped-per-type, or too rarely needed to justify a dedicated metric field.
`IDebugPropertiesProvider` gives any resource a second, looser channel for exactly that detail,
without growing the structured metric surface or the resource tree itself.

## ⚙️ How it works (in brief)

Any `IResource` may additionally implement `IDebugPropertiesProvider` with one method,
`GetDebugProperties()`, returning `IReadOnlyDictionary<string, object>`. Unlike `ReadMetrics`,
this call is allowed to allocate — it's invoked on demand for diagnostics, not on every snapshot
tick. Keys follow a `Container.Field` dot-convention (e.g. `ComponentSegment.AllocatedChunks`,
`Contention.MaxWaitUs`); values are primitives or types with meaningful `ToString()`. It is also
the documented surface for the "Owner Aggregates" pattern: fine-grained primitives that don't get
their own graph node (segments, latches, banks) report their per-instance breakdown here, while
their owner reports only the aggregate via `IMetricSource`.

## 💻 Usage

```csharp
var ct = dbe.GetComponentTable<Player>();

// Direct call on a typed reference
foreach (var (key, value) in ct.GetDebugProperties())
{
    Console.WriteLine($"{key} = {value}");
}
// StorageMode = Versioned
// ComponentSegment.AllocatedChunks = 4096
// ComponentSegment.Capacity = 8192
// CompRevTableSegment.AllocatedChunks = 4096
// DefaultIndexSegment.AllocatedChunks = 256
// ...

// Or drill into any node reached via the resource tree, without holding a typed reference
var node = resourceGraph.FindByPath("Storage/ManagedPagedMMF");
if (node is IDebugPropertiesProvider provider)
{
    var props = provider.GetDebugProperties();
    // PageCache.FreeCount, PageCache.DirtyCount, ClockSweep.MinCounter, Segments.Count, ...
}
```

## ⚠️ Guarantees & limits

- **Optional interface** — most `IResource` types don't implement it; check with
  `is IDebugPropertiesProvider` before calling on an arbitrary node, or call directly on a known
  type.
- **May allocate** — a new `Dictionary<string, object>` per call, including boxing for value
  types. Not for hot paths or per-tick polling; call on demand for debugging/diagnostics.
- **No cross-key snapshot consistency** — unlike `IMetricSource.ReadMetrics` (one coherent call
  per node during a tree walk), `GetDebugProperties()` reads live state directly; individual
  entries may be momentarily inconsistent with each other under concurrent mutation.
- **Lock-free by convention, not enforced** — implementations are expected to avoid taking locks
  so a debug read can't itself become a contention source, but the interface doesn't require it.
- **No tree-wide snapshot helper ships today** — there is no built-in walker that collects
  `GetDebugProperties()` across every node; call it per node yourself (directly, or after an
  `is IDebugPropertiesProvider` check while walking `IResource.Children`).
- Currently implemented on `ComponentTable`, `ManagedPagedMMF`, `DatabaseEngine`,
  `TransactionChain`, `MemoryAllocator`, `ConcurrentBitmapL3All`, `PinnedMemoryBlock`,
  `MemoryBlockArray`, `MemoryBlockBase`, and `StagingBufferPool`.

## 🧪 Tests
- [OwnerAggregatesTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Resources/OwnerAggregatesTests.cs) — `ComponentTable.GetDebugProperties()`
  per-segment breakdown (`ComponentSegment.*`, `CompRevTableSegment.*`, `DefaultIndexSegment.*`, `String64IndexSegment.*` when present),
  values match live segment state after entity creation

## 🔗 Related

- Source: [`IDebugPropertiesProvider.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/IDebugPropertiesProvider.cs)
- Related: [Metric Reporting](./metric-reporting.md) — the structured, zero-allocation counterpart this complements.

<!-- Deep dive: claude/design/Resources/09-resource-observability-implementations.md, claude/design/Resources/05-granularity-strategy.md -->
