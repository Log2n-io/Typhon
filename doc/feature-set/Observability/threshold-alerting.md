---
uid: feature-observability-threshold-alerting
title: 'Threshold-Based Resource Alerting'
description: 'Warning/Critical alerts on resource health transitions, with automatic root-cause tracing to the upstream bottleneck.'
---

# Threshold-Based Resource Alerting
> Warning/Critical alerts on resource health transitions, with automatic root-cause tracing to the upstream bottleneck.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Observability](./README.md)

## 🎯 What it solves
A health check tells an operator *that* something is wrong; it doesn't say *why*, and polling it manually to catch
transitions doesn't scale. Production monitoring needs push-style notifications — fire a Warning/Critical event the
moment a resource crosses a threshold — and, more importantly, needs to point at the actual bottleneck rather than
whichever downstream resource happens to be visibly full. `ResourceAlertGenerator` plus `ResourceMetricsService`
give you both: threshold-crossing alerts, and a root cause pointing at the upstream resource that's really
constraining the system.

## ⚙️ How it works (in brief)
`ResourceAlertGenerator.GenerateAlert` checks one resource's utilization against its `HealthThresholds` and returns
`null` if it's healthy, a `Warning` alert if it's past the degraded threshold, or `Critical` if past the unhealthy
threshold. It then calls `ResourceSnapshot.FindRootCause`, which walks a small hardcoded wait-dependency graph
(e.g. `TransactionPool` waits on `WALRingBuffer`, which waits on `WALSegments`) and follows it through any
upstream node that is *also* above the high-utilization bar, returning the resource at the end of that chain. This
is architectural knowledge, not runtime wait tracking — it identifies the likely cause, not a measured one.
`ResourceMetricsService` drives this on a timer: it takes a snapshot, computes overall health, and — only when the
worst-of-all status **escalates** (Healthy→Degraded, Healthy→Unhealthy, Degraded→Unhealthy) — sweeps every
degraded/unhealthy resource and raises one `AlertRaised` event per resource. Recovery transitions never raise
alerts, which keeps a flapping resource from generating an alert storm.

## 💻 Usage
```csharp
using Typhon.Engine;

services.AddTyphonObservabilityBridge(options =>
{
    options.Thresholds["Storage/PageCache"] = new HealthThresholds(0.70, 0.90);
});

var alertService = serviceProvider.GetRequiredService<ResourceMetricsService>();
alertService.AlertRaised += (_, alert) =>
{
    Console.WriteLine($"[{alert.Severity}] {alert.Title}");
    Console.WriteLine($"  Root cause: {alert.RootCausePath} ({alert.RootCauseUtilization:P0})");
    // Ship to PagerDuty/Slack/AlertManager here.
};
alertService.Start(); // snapshots + health checks every SnapshotInterval (default 5s)

// One-shot check outside the service, e.g. from a diagnostics endpoint:
var generator = serviceProvider.GetRequiredService<ResourceAlertGenerator>();
ResourceAlert alert = generator.GenerateAlert(snapshot, "Root/DataEngine/TransactionPool");
```

| Option | Default | Effect |
|---|---|---|
| `ObservabilityBridgeOptions.Thresholds["<path>"]` | unset (falls back to `HealthThresholds.Default`) | Per-path degraded/unhealthy bar used by both the alert generator and health checker |
| `ResourceSnapshot.FindRootCause(path, threshold)` | `threshold = 0.8` | How high an upstream node's utilization must be to be accepted as the next link in the causal chain |
| `ObservabilityBridgeOptions.SnapshotInterval` | 5s | How often `ResourceMetricsService` re-checks health and can raise a new alert wave |

## ⚠️ Guarantees & limits
- **Alerts fire only on escalating state transitions** — never on recovery, never repeatedly for a resource stuck
  at the same status. This is deliberate anti-flood behavior, not a bug if you expected steady-state re-alerting.
- A single escalation raises one `AlertRaised` event **per currently degraded/unhealthy resource**, not just the
  one that crossed the line — a pre-existing Warning can re-fire alongside a new Critical in the same sweep.
- Root cause tracing (`FindRootCause`) only follows edges in a small hardcoded dependency table
  (`TransactionPool → WALRingBuffer → WALSegments`, `PageCache → ManagedPagedMMF`, `ShadowBuffer → SnapshotStore`);
  resources outside that table return themselves as their own root cause.
- `ResourceAlertGenerator`'s per-path thresholds come **only** from `ObservabilityBridgeOptions.Thresholds`, falling
  back to `HealthThresholds.Default` (80%/95%) — unlike `ResourceHealthChecker`, it does **not** apply the built-in
  60%/80% critical defaults for `WALRingBuffer`/`TransactionPool`. Set those explicitly in `Thresholds` if you want
  alert severity to match the health check's stricter bar.
- `ResourceAlert` carries only symptom/root-cause path, utilization, severity, and timestamp — no message text or
  cascading-effects list; format the notification payload from those fields at the integration boundary.
- Reads the same cached `ResourceSnapshot` as metrics export and health checks — alert freshness is bounded by
  `SnapshotInterval`, not real time.

## 🧪 Tests
- [ObservabilityBridgeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Observability/Bridge/ObservabilityBridgeTests.cs) — `ResourceAlertGenerator` Warning/Critical threshold crossings plus `FindRootCause` attribution across the wait-dependency graph, and `ResourceMetricsService` escalation-only alert firing (no re-alert on recovery)

## 🔗 Related
- Related feature: [Resource-Aware Health Checks](./health-checks.md)
- Sibling: [Root-Cause Cascade Analysis (FindRootCause)](../Resources/snapshot-query-api/root-cause-cascade-analysis.md) — the dependency-walk machinery `GenerateAlert` calls to attribute a symptom to its upstream bottleneck
- Sibling: [Resource Budget Configuration (ResourceOptions)](../Resources/resource-budgets-options.md) — the startup-configured limits these thresholds are percentages of
- Source: `ResourceAlertGenerator.cs`, `ResourceMetricsService.cs`, `ResourceSnapshot.cs` (`FindRootCause`) in `src/Typhon.Engine/Observability/public/` and `src/Typhon.Engine/Resources/public/`

<!-- Deep dive: claude/design/Resources/08-observability-bridge.md §4 Graph → Alerts Mapping -->
<!-- ADR: claude/adr/032-resource-system-architecture.md -->
