---
uid: feature-resources-owner-aggregates-granularity
title: 'Owner-Aggregates Granularity Strategy'
description: 'The rule for what becomes a resource-tree node and what gets rolled up into its owner.'
---

# Owner-Aggregates Granularity Strategy
> The rule for what becomes a resource-tree node and what gets rolled up into its owner.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Resources](./README.md)

## 🎯 What it solves
Typhon has thousands of latches, hundreds of thousands of chunks, and millions of entities. A
resource tree with one node per instance would cost hundreds of MB and tens of milliseconds per
snapshot, and burying the few decisions an operator can act on under a sea of leaf nodes defeats
the point of having a tree at all. Application code that walks the resource graph (dashboards,
health checks, OTel export) needs that tree to stay small and meaningful regardless of how much
data the engine is holding.

## ⚙️ How it works (in brief)
This is an architectural rule, not an API to call. A type only gets a graph node if it has a
distinct lifecycle, operators can act on its metrics, its count is bounded (under ~500), and
tracking it aids debugging — structs/ref structs are never nodes. Everything that fails the test
(latches, chunks, segments, revision chains, entities) is **aggregated into its owning node**: the
owner reports the rolled-up numbers via `IMetricSource`, and exposes a per-child breakdown on
demand via `IDebugPropertiesProvider`. Latch contention specifically flows up through the
`IContentionTarget` callback. The target shape is 3 levels deep (System → Subsystem → Component /
Per-type Instance); a 4th level is added only when a sub-resource has its own distinct metrics or
resource pool worth isolating.

## 💻 Usage
You don't call this pattern — you apply it when adding a new internal type. `ComponentTable<T>` is
the reference example: it owns several segments but exposes only itself as a node.

```csharp
public class ComponentTable : ResourceNode, IMetricSource, IDebugPropertiesProvider
{
    // ComponentSegment, CompRevTableSegment, DefaultIndexSegment, String64IndexSegment, ...
    // are NOT ResourceNodes — no node per segment, no node per chunk inside them.

    public void ReadMetrics(IMetricWriter writer)
    {
        long allocated = (ComponentSegment?.AllocatedChunkCount ?? 0) + (CompRevTableSegment?.AllocatedChunkCount ?? 0);
        long capacity = (ComponentSegment?.ChunkCapacity ?? 0) + (CompRevTableSegment?.ChunkCapacity ?? 0);
        writer.WriteCapacity(allocated, capacity); // one Capacity metric for the whole table
    }
}

// Consumers see one node, with drill-down available on request:
var ct = dbe.GetComponentTable<Player>();
Console.WriteLine(ct.Children.Count); // 0 — segments are not children

var snapshot = resourceGraph.GetSnapshot();
var node = snapshot.GetNode("Root/DataEngine/ComponentTable_Player");
Console.WriteLine(node.Capacity.Value.Utilization); // aggregated across all segments

foreach (var (k, v) in ct.GetDebugProperties())     // per-segment breakdown, on demand
    Console.WriteLine($"{k} = {v}");
```

| Decision input | Node? | Example |
|---|---|---|
| Distinct lifecycle, bounded count (<500), actionable metrics | Yes | `ComponentTable<T>`, `PageCache`, `PrimaryKeyIndex` |
| struct / ref struct | Never | `AtomicChange`, lock guards |
| Fine-grained, unbounded, or pure data | No — aggregate into owner | latches, chunks, segments, revision chains, entities |

## ⚠️ Guarantees & limits
- Not enforced by the compiler or runtime — it's a design rule applied when a new `IResource` is
  added; review against `claude/design/Resources/05-granularity-strategy.md` §2 before adding a node.
- Aggregated primitives are invisible to `IResource.Children` / tree walks; their numbers only
  surface through the owner's `IMetricSource.ReadMetrics` (rolled-up) and `IDebugPropertiesProvider`
  (per-child breakdown, allocates, call on demand — not every tick).
- Keeps the tree at roughly 50-400 nodes regardless of entity/chunk/latch count (a full snapshot
  stays in the few-μs range, see metric-reporting.md); a 4th tree level is justified only when a
  sub-resource has metrics or a resource pool genuinely distinct from its parent's — not for symmetry.
- `ComponentTable_SegmentsAreNotChildren` and related tests assert this invariant for the ECS
  storage path; a regression there (a segment gaining a node) is a design violation, not a feature.

## 🧪 Tests
- [OwnerAggregatesTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Resources/OwnerAggregatesTests.cs) — `ComponentTable` has zero graph
  children while its `IMetricSource.ReadMetrics` aggregates segment capacity/allocation totals

## 🔗 Related
- Companion features: [`metric-reporting.md`](./metric-reporting.md) (the rolled-up surface), [`debug-properties-drilldown.md`](./debug-properties-drilldown.md) (the per-child drill-down)
- Tests: [`test/Typhon.Engine.Tests/Resources/OwnerAggregatesTests.cs`](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Resources/OwnerAggregatesTests.cs)

<!-- Deep dive: claude/design/Resources/05-granularity-strategy.md -->
<!-- Decision record: claude/adr/032-resource-system-architecture.md (Decision 3 — Owner Aggregates Pattern) -->
