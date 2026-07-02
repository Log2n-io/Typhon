# Observability Bridge (Resources to OTel/Health/Alerts)
> Turns resource-graph snapshots into OpenTelemetry metrics, a health status, and threshold alerts.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Resources](./README.md)

## 🎯 What it solves
Monitoring stacks (Prometheus/Grafana, Kubernetes probes, PagerDuty/Slack) don't want to talk to
the resource graph directly — they want standard metrics, a health enum, and alert payloads with a
root cause already attached. The Observability Bridge is that adapter layer: it reads
`ResourceSnapshot`s on a timer and republishes them in the three shapes external tooling expects,
so applications don't hand-roll polling loops around `IResourceGraph`.

## ⚙️ How it works (in brief)
The bridge is a pure **consumer** of the resource graph — it owns no counters and defines no tree
structure, it only reads what components already report. A `ResourceMetricsExporter` snapshots the
graph on an interval and exposes every metric kind (Memory, Capacity, DiskIO, Throughput, Duration)
as `System.Diagnostics.Metrics` observable instruments tagged with `resource_path`, so any OTel
exporter you attach (Prometheus, OTLP, dotnet-counters) picks them up for free. A
`ResourceHealthChecker` derives a `Healthy`/`Degraded`/`Unhealthy` status from `Capacity.Utilization`
against per-path thresholds, using a worst-of-all-nodes rule. A `ResourceAlertGenerator` turns a
threshold breach into a `ResourceAlert` with a root-cause path found via `FindRootCause`. A
`ResourceMetricsService` wires the three together on one timer and raises events on status
transitions.

## 💻 Usage
```csharp
using Typhon.Engine;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddSingleton<IResourceGraph>(existingResourceGraph);

services.AddTyphonObservabilityBridge(options =>
{
    options.SnapshotInterval = TimeSpan.FromSeconds(1);
    options.Thresholds["Durability/WALRingBuffer"] = new HealthThresholds(0.60, 0.80);
});

var provider = services.BuildServiceProvider();

// OTel export: attach any exporter to ResourceMetricsExporter.MeterName ("Typhon.Resources").
var exporter = provider.GetRequiredService<ResourceMetricsExporter>();

// Health: cheap poll for a liveness probe, or a detailed result for a dashboard.
ITyphonHealthCheck health = provider.GetRequiredService<ITyphonHealthCheck>();
HealthStatus status = health.CheckHealth();
HealthCheckResult detail = health.GetDetailedResult();
Console.WriteLine($"{detail.Status}: {detail.Description}");

// Alerts: subscribe before starting the timer-driven service.
var metricsService = provider.GetRequiredService<ResourceMetricsService>();
metricsService.AlertRaised += (_, alert) =>
    Console.WriteLine($"[{alert.Severity}] {alert.Title} -> root cause {alert.RootCausePath}");
metricsService.HealthStatusChanged += (_, e) =>
    Console.WriteLine($"{e.PreviousStatus} -> {e.NewStatus}");
metricsService.Start();
```

| Option | Default | Effect |
|---|---|---|
| `SnapshotInterval` | 5s | Cadence for both metrics refresh and health/alert evaluation (one timer, not two) |
| `MetricNamePrefix` | `"typhon.resource"` | Prefix for every OTel instrument name |
| `ExportMemoryMetrics` / `ExportCapacityMetrics` / `ExportDiskIOMetrics` / `ExportThroughputMetrics` / `ExportDurationMetrics` | `true` | Toggle a metric kind's instruments off entirely |
| `Thresholds["<path>"]` | `HealthThresholds.Default` (80%/95%) | Per-node degraded/unhealthy utilization cutoffs; `WALRingBuffer` and `TransactionPool` default to `Critical` (60%/80%) |

## ⚠️ Guarantees & limits
- Read-only consumer: the bridge maintains no counters, defines no tree structure, and does not
  decide exhaustion policy — it only reads `ResourceSnapshot`s the graph already produces.
- Metric names are generic per kind (e.g. `typhon.resource.capacity.utilization`) with the node
  path carried as a `resource_path` tag, not baked into the metric name — keeps OTel/Prometheus
  cardinality management on the consumer side.
- Alerts fire only on status **transitions to a worse state** (`AlertRaised`); recovery
  (`Unhealthy → Degraded → Healthy`) raises `HealthStatusChanged` but never a new alert, to avoid
  flapping noise.
- `ResourceAlertGenerator` traces a single root-cause node via `FindRootCause`; it does not (yet)
  attach cascading-effect/contention-hotspot lists to the alert, despite that being part of the
  original design intent — treat `ResourceAlert` as symptom + one root cause, not a full blast
  radius.
- `ITyphonHealthCheck` is framework-independent by design — there is no built-in ASP.NET Core
  `IHealthCheck` or Kubernetes probe endpoint; write a thin adapter that calls `CheckHealth()` /
  `GetDetailedResult()` from your own health-check registration.
- No built-in Prometheus/OTLP exporter or AlertManager/webhook sender ships in Typhon — the bridge
  stops at the `Meter` and the `AlertRaised`/`HealthStatusChanged` events; wiring those to an actual
  sink is application code.
- `ResourceMetricsService` swallows exceptions from its timer callback to avoid killing the timer
  thread — a persistently throwing health check degrades silently rather than crashing.

## 🧪 Tests
- [ObservabilityBridgeTests](../../../test/Typhon.Engine.Tests/Observability/Bridge/ObservabilityBridgeTests.cs) — OTel metric-name
  building, `ResourceMetricsExporter` instrument registration/gating, `ResourceHealthChecker` threshold tiers, `ResourceAlertGenerator`
  root-cause attribution and transition-only alerting, `ResourceMetricsService` start/stop/force-update events

## 🔗 Related
- Sibling: [Resource Graph Metrics Bridge](../Observability/otel-metrics-export/resource-graph-metrics-bridge.md) — the Observability-side view of this same OTel/health/alert exporter (cross-category).
- Sibling: [Metric Reporting](./metric-reporting.md) — the per-node data this bridge reads on each snapshot.

<!-- Deep dive: claude/design/Resources/08-observability-bridge.md -->
