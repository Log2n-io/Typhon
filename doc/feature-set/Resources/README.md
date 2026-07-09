---
uid: feature-resources-index
title: 'Resources'
description: 'A runtime resource graph that tracks every significant engine resource (storage, transactions, durability, allocation, synchronization, timers, runtime,…'
---

# Resources
> A runtime resource graph that tracks every significant engine resource (storage, transactions, durability, allocation, synchronization, timers, runtime, profiler) in a fixed 8-branch hierarchical tree, lets components report metrics/budgets/debug detail through a handful of pull-based interfaces, and exposes point-in-time snapshots with query and root-cause helpers for diagnostics, monitoring, and exhaustion handling.

> 🔬 **Recommended:** read [in-depth-overview/13-resources.md](../../in-depth-overview/13-resources.md) (Chapter 13: Resources) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Resource Budget Configuration (ResourceOptions)](resource-budgets-options.md) | Startup-time configuration of fixed/growable resource limits (page cache size, max active transactions, WAL ring/segment sizing, shadow buffer, checkpoint thresholds) plus `Validate()` to check fixed allocations fit the total memory budget | ✅ Implemented | 🔵 Core |
| [Exhaustion Policy & ResourceExhaustedException](exhaustion-policy-handling.md) | Typed exception for resource-limit hits; `ExhaustionPolicy` enum documents intent but isn't a runtime dispatch switch | 🚧 Partial | 🔵 Core |
| [DI Registration & Wiring](resources-di-wiring.md) | Register Typhon services into `IServiceCollection` and have each one self-attach to the resource graph | ✅ Implemented | 🔵 Core |
| [Observability Bridge (Resources to OTel/Health/Alerts)](observability-bridge-resources.md) | Consumer-side mapping of resource snapshots to OpenTelemetry metrics, health-check thresholds, and alert payloads | 🚧 Partial | 🟣 Advanced |

## Internal Features

> Engine machinery for contributors — the resource graph's own plumbing (tree structure, metric/debug reporting interfaces, and the raw snapshot/query surface) that the public features above are built on. Application code doesn't call these directly; Typhon's own tooling (Workbench, tsh) does.

| Feature | Summary | Status |
|---|---|---|
| [Resource Tree & Registry](resource-tree-registry.md) | Hierarchical, fail-fast tree of every managed resource (8 fixed subsystem branches under Root) with cascade disposal and path-based navigation | ✅ Implemented |
| [Resource Tree Mutation Notifications](resource-tree-mutation-notifications.md) | `NodeMutated` event fires on every Added/Removed registration so consumers (e.g. Workbench live tree view) can react without polling | ✅ Implemented |
| [Metric Reporting (IMetricSource / IMetricWriter)](metric-reporting.md) | Pull-based, zero-allocation interface for resources to expose 5 metric kinds — Memory, Capacity, DiskIO, Throughput, Duration — read by the graph on snapshot, not pushed on the hot path | ✅ Implemented |
| [Owner-Aggregates Granularity Strategy](owner-aggregates-granularity.md) | Architectural rule for what becomes a resource-tree node vs. what owning components aggregate and report on its behalf | ✅ Implemented |
| [Debug Properties Drill-Down (IDebugPropertiesProvider)](debug-properties-drilldown.md) | Ad-hoc, allocation-tolerant dictionary of diagnostic key/value pairs for per-resource drill-down that's too verbose for the structured metric writer | ✅ Implemented |
| [Snapshot & Query API](snapshot-query-api/README.md) | On-demand, consistent-enough tree-wide snapshot (`IResourceGraph.GetSnapshot`) with query helpers — `GetSubtreeMemory`, `FindMostUtilized`, `FindByType`, `GetSubtree`, `GetNode` — plus auto-computed throughput rates from the previous snapshot | ✅ Implemented |
| &nbsp;&nbsp;↳ [Root-Cause Cascade Analysis (FindRootCause)](snapshot-query-api/root-cause-cascade-analysis.md) | Trace a high-utilization symptom node back through a known dependency chain to find what's actually backed up | ✅ Implemented |