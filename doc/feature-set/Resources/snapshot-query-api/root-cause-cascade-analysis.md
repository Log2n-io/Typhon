---
uid: feature-resources-snapshot-query-api-root-cause-cascade-analysis
title: 'Root-Cause Cascade Analysis (FindRootCause)'
description: 'Trace a high-utilization symptom node back through a known dependency chain to find what''s actually backed up.'
---

# Root-Cause Cascade Analysis (FindRootCause)
> Trace a high-utilization symptom node back through a known dependency chain to find what's actually backed up.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Resources](../README.md)

## 🎯 What it solves

A node at 95% capacity might be the actual bottleneck, or it might just be stuck waiting on something else
further down the chain — e.g. `TransactionPool` stalls because the WAL ring buffer can't drain fast enough, not
because the pool itself is undersized. Without traversal, an operator has to know Typhon's internal wait
relationships by heart and check each candidate by hand. `FindRootCause` automates that one hop at a time.

## ⚙️ How it works (in brief)

`ResourceSnapshot.FindRootCause(symptomPath, highUtilizationThreshold = 0.8)` checks whether the symptom node's
`Capacity.Utilization` exceeds the threshold; if so, it looks up the node in a small, hardcoded
*architectural-knowledge* table of wait dependencies and follows the edge to whichever dependency is both above
the same threshold and most utilized. It repeats from there until a node has no further high-utilization
dependency, and returns that node — the chain's end is reported as the root cause. The table reflects how
Typhon's subsystems interact by design, not anything observed at runtime.

## 💻 Usage

```csharp
ResourceSnapshot snapshot = resourceGraph.GetSnapshot();

NodeSnapshot symptom = snapshot.GetNode("Root/DataEngine/TransactionPool");
if (symptom?.Capacity is { Utilization: > 0.8 })
{
    // Accepts the path with or without the "Root/" prefix.
    NodeSnapshot rootCause = snapshot.FindRootCause("DataEngine/TransactionPool");
    Console.WriteLine($"Root cause: {rootCause.Path} ({rootCause.Capacity?.Utilization:P0})");
    // → "Root cause: Root/Durability/WALRingBuffer (93%)"
}

// Stricter threshold — only chase dependencies that are themselves above 90% utilization.
NodeSnapshot strict = snapshot.FindRootCause("DataEngine/TransactionPool", highUtilizationThreshold: 0.9);
```

## ⚠️ Guarantees & limits

- The dependency table is fixed architectural knowledge baked into `ResourceSnapshot`, not derived from runtime
  call graphs or actual wait events. It currently has 4 edges: `DataEngine/TransactionPool` →
  `Durability/WALRingBuffer`; `Durability/WALRingBuffer` → `Durability/WALSegments`; `Storage/PageCache` →
  `Storage/ManagedPagedMMF`; `Backup/ShadowBuffer` → `Backup/SnapshotStore`.
- Coverage is partial by construction: a node with no entry on the left side of the table returns itself
  immediately, even if it's genuinely blocked on something architecturally — this is a curated heuristic, not a
  complete dependency graph.
- Two of the four edges (`Backup/*`) reference paths that don't exist in the current resource tree; a lookup
  through them simply finds nothing and the chain stops there — no exception.
- A dependency is only followed when **both** the current node and the candidate are above
  `highUtilizationThreshold` — a saturated node whose blocker is calm (e.g. 60% with an 0.8 threshold) is
  reported as the root cause itself; the chain doesn't continue past a dependency that isn't itself under
  pressure.
- Traversal is cycle-safe (visited-set guard) though the shipped table is acyclic; cost is O(chain length) —
  in practice 1-2 hops — with no extra `ReadMetrics()` calls, since it reads only the already-frozen snapshot.
- Scoped to `Capacity.Utilization` only. Lock/latch contention isn't part of this surface — see
  `IContentionTarget` for wait-time telemetry, which aggregates separately per owning component.

## 🧪 Tests

- [ResourceOptionsTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Resources/ResourceOptionsTests.cs) — `FindRootCause` region: chain
  tracing through multiple hops, custom/default threshold behavior, missing-node and `Root/`-prefix handling, most-utilized-dependency
  selection

## 🔗 Related

- Source: [src/Typhon.Engine/Resources/public/ResourceSnapshot.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ResourceSnapshot.cs)
- Parent feature: [Snapshot & Query API](./README.md)
- Sibling: [Threshold-Based Resource Alerting](../../Observability/threshold-alerting.md) — `FindRootCause` is the shared root-cause tracing machinery behind both (cross-category).

<!-- Deep dive: claude/design/Resources/06-snapshot-api.md §5 FindRootCause, claude/design/Resources/07-budgets-exhaustion.md -->
