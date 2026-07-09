---
uid: feature-resources-snapshot-query-api-index
title: 'Snapshot & Query API'
description: 'Pull a consistent-enough, point-in-time snapshot of the whole resource tree, then query it for memory, capacity, and rate answers.'
---

# Snapshot & Query API
> Pull a consistent-enough, point-in-time snapshot of the whole resource tree, then query it for memory, capacity, and rate answers.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Resources](../README.md)

## 🎯 What it solves

Diagnosing "why is the engine slow" or "what's using all the memory" by reading individual component counters
means hand-correlating dozens of fields across dashboards. `IResourceGraph.GetSnapshot()` freezes every
[metric-reporting](../metric-reporting.md) node's values into one object, so an operator or health-check can ask
tree-wide questions — total memory under a subsystem, which node is closest to capacity, what's the current
op/sec rate — against a single, stable structure instead of live, moving counters.

## ⚙️ How it works (in brief)

`GetSnapshot()` walks the resource tree once, calls `ReadMetrics()` on every node that implements
`IMetricSource`, and returns an immutable `ResourceSnapshot` whose `Nodes` dictionary is keyed by full path
(e.g. `"Root/Storage/PageCache"`). `GetSnapshot(IResource subtreeRoot)` does the same walk starting partway
down the tree, for when you already know which subsystem to inspect. `ResourceSnapshot`'s query methods
(`GetSubtreeMemory`, `FindMostUtilized`, `GetNode`, `FindByType`, `GetSubtree`) run entirely against the frozen
`Nodes` dictionary — no further tree walk, safe to call from any thread. `Rates` is computed automatically by
comparing the new snapshot's `Throughput` counters against the previous one the same `IResourceGraph` instance
took; it's `null` on the very first call.

## 💻 Usage

```csharp
using Typhon.Engine;

IResourceGraph resourceGraph = new ResourceGraph(registry); // or resolve via DI (IResourceGraph)

// Full tree snapshot — call on demand, or every 1-5s for monitoring/health checks.
ResourceSnapshot snapshot = resourceGraph.GetSnapshot();

// Path-based lookup — full paths are rooted at "Root/...".
NodeSnapshot pageCache = snapshot.GetNode("Root/Storage/PageCache");
if (pageCache?.Capacity is { } capacity)
    Console.WriteLine($"PageCache: {capacity.Utilization:P0} ({capacity.Current}/{capacity.Maximum})");

// Memory attribution — sum AllocatedBytes across every node under a subtree.
long dataEngineBytes = snapshot.GetSubtreeMemory("Root/DataEngine");

// Bottleneck detection — single worst node, or every node above a threshold.
NodeSnapshot bottleneck = snapshot.FindMostUtilized();
foreach (NodeSnapshot hot in snapshot.FindMostUtilized(0.9))
    Console.WriteLine($"{hot.Path}: {hot.Capacity!.Value.Utilization:P0}");

// Targeted subtree snapshot — skips everything outside Durability.
IResource durability = resourceGraph.FindByPath("Durability");
ResourceSnapshot durabilitySnap = resourceGraph.GetSnapshot(durability);

// Throughput rates — null on the first snapshot this IResourceGraph instance ever took.
if (snapshot.Rates is { } rates)
{
    double hitsPerSec = rates.GetRate("Root/Storage/PageCache", "CacheHits");
    Console.WriteLine($"Cache hits/sec: {hitsPerSec:F0}");
}

resourceGraph.ResetAllPeaks(); // start a fresh peak-measurement window across all metric sources
```

## ⚠️ Guarantees & limits

- Per-node atomic, cross-node approximate: each `ReadMetrics()` call reads one node's fields together, but
  different nodes may be read microseconds apart — no global lock blocks live traffic during a snapshot.
- Cost is ~50 ns/node; a typical 50-100 node tree costs ~2.5-5 μs to snapshot, with no allocations beyond the
  returned snapshot's own arrays/dictionary.
- `Nodes` keys are full paths including the `"Root/"` prefix; `GetSubtreeMemory`/`GetSubtree` match on exact
  segment boundaries (`"Storage"` matches `"Storage"` and `"Storage/PageCache"`, never `"StorageX"`).
- `FindMostUtilized()`, `GetNode()`, and `FindByType()` return `null`/empty rather than throwing when nothing
  has `Capacity` metrics or nothing matches — no exceptions for "not found".
- `GetSnapshot(subtreeRoot)` computes `Rates` against the last *full-tree* snapshot, but does not itself become
  the new rate baseline — only `GetSnapshot()` (the full-tree overload) updates it.
- A `ResourceSnapshot` is a plain immutable object — hold onto one for historical comparison, or pass it across
  threads freely; nothing about it ties back to live engine state.

## 🧪 Tests

- [ResourceSnapshotTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Resources/ResourceSnapshotTests.cs) — snapshot capture, subtree scoping,
  `GetSubtreeMemory`/`FindMostUtilized`/`FindByType`/`GetSubtree`, throughput rate computation, thread-safe concurrent `GetSnapshot` calls

## 🔗 Related

- Source: [src/Typhon.Engine/Resources/public/IResourceGraph.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/IResourceGraph.cs), [src/Typhon.Engine/Resources/public/ResourceGraph.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ResourceGraph.cs), [src/Typhon.Engine/Resources/public/ResourceSnapshot.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ResourceSnapshot.cs)
- Sub-features: [Root-Cause Cascade Analysis (FindRootCause)](./root-cause-cascade-analysis.md)
- Sibling: [Metric Reporting](../metric-reporting.md) — the per-node data this API freezes into a snapshot.

<!-- Deep dive: claude/design/Resources/06-snapshot-api.md, claude/overview/08-resources.md §8.8 -->
