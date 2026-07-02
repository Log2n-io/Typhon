# Resource-Aware Health Checks
> A framework-agnostic Healthy/Degraded/Unhealthy verdict, derived from live resource utilization, for cheap liveness/readiness polling.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Observability](./README.md)

## 🎯 What it solves
Container orchestrators and load balancers need a single, cheap-to-call answer to "is this instance OK?" —
checked every few seconds, sometimes sub-second. Deriving that answer by hand means picking thresholds for page
cache fill, WAL ring fill, transaction pool load, etc., and reducing them all to one verdict in every host
application. `ITyphonHealthCheck` does that reduction once, against the engine's own resource utilization data,
so a Kubernetes liveness probe or a `/health` endpoint gets a ready-made composite status instead of reimplementing
threshold logic per deployment.

## ⚙️ How it works (in brief)
`ResourceHealthChecker` implements `ITyphonHealthCheck` (no ASP.NET Core dependency) and reads the
`ResourceMetricsExporter`'s cached `ResourceSnapshot` — it never walks the resource graph itself. `CheckHealth()`
is the cheap path: it looks only at the single most-utilized resource and compares it to that resource's
threshold, suitable for frequent liveness polling. `GetDetailedResult()` walks every resource with a capacity
metric and returns the **worst-of-all** status plus the lists of degraded/unhealthy paths, for dashboards and
`/health/details` endpoints. Thresholds default to 80%/95% (degraded/unhealthy) and tighten to 60%/80% for
resources marked architecturally critical (`Durability/WALRingBuffer`, `DataEngine/TransactionPool`); any path
can be overridden.

## 💻 Usage
```csharp
using Typhon.Engine;

services.AddTyphonObservabilityBridge(options =>
{
    // Tighten or relax thresholds per resource path (leading "Root/" is optional).
    options.Thresholds["Storage/PageCache"] = new HealthThresholds(0.70, 0.90);
});

var healthCheck = serviceProvider.GetRequiredService<ITyphonHealthCheck>();

// Cheap path — e.g. a Kubernetes liveness probe.
HealthStatus status = healthCheck.CheckHealth();
if (status == HealthStatus.Unhealthy)
{
    return Results.StatusCode(503);
}

// Detailed path — e.g. a dashboard or readiness endpoint.
HealthCheckResult detail = healthCheck.GetDetailedResult();
Console.WriteLine(detail.Description); // "Storage/PageCache at 92% utilization (Degraded)"
```

| Option | Default | Effect |
|---|---|---|
| `ObservabilityBridgeOptions.Thresholds["<path>"]` | unset (falls back to built-in) | `HealthThresholds(degraded, unhealthy)` override for one resource path |
| `HealthThresholds.Default` | 0.80 / 0.95 | Applied to any resource without an explicit or critical-default entry |
| `HealthThresholds.Critical` | 0.60 / 0.80 | Built-in default for `Durability/WALRingBuffer` and `DataEngine/TransactionPool` |

## ⚠️ Guarantees & limits
- **Worst-of-all composite** — overall status is the single most severe status across all resources carrying a
  Capacity metric; one critical resource is never averaged away by many healthy ones.
- Only resources that report a `Capacity` metric participate; pure Memory/DiskIO/Throughput sources and grouping
  nodes are excluded from health computation.
- `CheckHealth()` is sized for frequent polling — it inspects only the most-utilized node
  (`ResourceSnapshot.FindMostUtilized()`); `GetDetailedResult()` does a full node walk and suits less frequent
  dashboard calls.
- Reads the exporter's cached snapshot, not live state — status reflects the last `SnapshotInterval` refresh
  (default 5s; see the Resource Metrics Export feature for the snapshot lifecycle).
- `ITyphonHealthCheck` has no ASP.NET Core dependency; bridging to
  `Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck` is a thin adapter the host writes (`HealthStatus`
  maps 1:1, `Description`/`Data` pass straight through).
- Health checks classify, not explain — for root-cause attribution behind a Degraded/Unhealthy verdict, pair this
  with the threshold-alerting side of the same bridge.

## 🧪 Tests
- [ObservabilityBridgeTests](../../../test/Typhon.Engine.Tests/Observability/Bridge/ObservabilityBridgeTests.cs) — `ResourceHealthChecker.CheckHealth`/`GetDetailedResult` threshold crossings (Healthy/Degraded/Unhealthy), custom per-path thresholds, and worst-of-all composite status

## 🔗 Related
- Sibling: [Threshold-Based Resource Alerting](./threshold-alerting.md) — same resource snapshot; adds root-cause attribution and push notifications on top of this Healthy/Degraded/Unhealthy classification
- Sibling: [Resource Budget Configuration (ResourceOptions)](../Resources/resource-budgets-options.md) — the startup-configured limits these health thresholds are measured against
- Source: `ITyphonHealthCheck.cs`, `ResourceHealthChecker.cs`, `ObservabilityBridgeOptions.cs` (`src/Typhon.Engine/Observability/public/`)

<!-- Deep dive: claude/overview/09-observability.md §9.5 Health Checks -->
<!-- Design: claude/design/Resources/08-observability-bridge.md §3 Graph → Health Checks Mapping -->
