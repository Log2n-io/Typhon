---
uid: feature-observability-otel-metrics-export-resource-graph-metrics-bridge
title: 'Resource Graph Metrics Bridge'
description: 'Every Resource System node, exposed as a standard System.Diagnostics.Metrics.Meter for any OTel exporter to pick up.'
---

# Resource Graph Metrics Bridge
> Every Resource System node, exposed as a standard `System.Diagnostics.Metrics.Meter` for any OTel exporter to pick up.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Observability](../README.md)

## 🎯 What it solves

Typhon's Resource System already tracks memory, capacity, disk I/O, throughput, and duration for every
significant engine resource (page cache, WAL ring, transaction pool, indexes, ...), but that data lives behind
an engine-internal snapshot API, not a metrics endpoint. Hand-writing OTel instruments for 50–300 resource nodes,
and keeping them in sync as resources are added or removed, is busywork every host application would otherwise
repeat.

## ⚙️ How it works (in brief)

`ResourceMetricsExporter` creates one `Meter` named `"Typhon.Resources"` and registers one *observable*
instrument per metric kind (Memory, Capacity, DiskIO, Throughput, Duration) — not one instrument per resource.
Each observable callback reads the most recently captured `ResourceSnapshot` and yields one `Measurement<T>` per
node that reports that kind, tagged with a `resource_path` attribute (and `metric_name` for the named
Throughput/Duration metrics). `ResourceMetricsService` is a self-contained `Timer` that calls `UpdateSnapshot()`
on the configured interval — that periodic snapshot is the only standing cost; the observable callbacks
themselves only run when something actually scrapes. Metric names are built by `OTelMetricNameBuilder`, which
converts a resource path and metric kind into a dotted, snake_cased OTel name (e.g.
`typhon.resource.memory.allocated_bytes`).

## 💻 Usage

```csharp
using Microsoft.Extensions.DependencyInjection;
using Typhon.Engine;

services.AddSingleton<IResourceGraph>(sp => resourceGraph);
services.AddTyphonObservabilityBridge(options =>
{
    options.SnapshotInterval = TimeSpan.FromSeconds(5);
    options.MetricNamePrefix = "typhon.resource";     // default
    options.ExportDurationMetrics = false;             // opt out of a metric kind
});

// Start the background snapshot loop (registration alone does not start it).
var bridgeService = serviceProvider.GetRequiredService<ResourceMetricsService>();
bridgeService.Start();

// Point an OTel MeterProvider at the bridge's meter (ASP.NET Core / OpenTelemetry.Extensions.Hosting):
services.AddOpenTelemetry().WithMetrics(builder => builder
    .AddMeter(ResourceMetricsExporter.MeterName)       // "Typhon.Resources"
    .AddPrometheusExporter());                          // or AddOtlpExporter(), AddConsoleExporter(), ...
```

| Metric kind | OTel name suffix | Example |
|---|---|---|
| Memory | `memory.allocated_bytes`, `memory.peak_bytes` | `typhon.resource.memory.allocated_bytes` |
| Capacity | `capacity.current`, `capacity.maximum`, `capacity.utilization` | `typhon.resource.capacity.utilization` |
| DiskIO | `disk_io.read_ops`, `disk_io.write_ops`, `disk_io.read_bytes`, `disk_io.write_bytes` | `typhon.resource.disk_io.write_bytes` |
| Throughput | `throughput.count` (tagged `metric_name`) | `typhon.resource.throughput.count{metric_name="CacheHits"}` |
| Duration | `duration.last_us`, `duration.avg_us`, `duration.max_us` (tagged `metric_name`) | `typhon.resource.duration.avg_us{metric_name="Checkpoint"}` |

| Option | Default | Effect |
|---|---|---|
| `MetricNamePrefix` | `"typhon.resource"` | Namespace prefix for every emitted metric name |
| `ExportMemoryMetrics` / `ExportCapacityMetrics` / `ExportDiskIOMetrics` / `ExportThroughputMetrics` / `ExportDurationMetrics` | `true` | Per-kind opt-out — skips registering that instrument entirely |
| `SnapshotInterval` | 5 s | How often the underlying snapshot (read by every callback) refreshes |

## ⚠️ Guarantees & limits

- One instrument per metric *kind*, not per resource — cardinality comes from the `resource_path` (and
  `metric_name`) attribute, not from instrument count, so adding resources never requires new registration.
- Observable callbacks read a cached snapshot; they never touch the resource graph directly, so an idle OTel
  pipeline (no exporter attached) costs nothing beyond the snapshot interval itself.
- Values are only as fresh as the last `UpdateSnapshot()` — driven by `ResourceMetricsService.SnapshotInterval`
  (default 5 s), not by the OTel scrape interval.
- A resource only appears in a given instrument's output if its `IMetricSource.ReadMetrics` actually wrote that
  metric kind for the current snapshot — nodes that don't report a kind are simply absent, not zero.
- `ResourceMetricsExporter` implements `IDisposable`; disposing it disposes the `Meter` and detaches every
  instrument — do this once, at host shutdown.
- Prometheus auto-converts dots to underscores, so the same dotted OTel names work unmodified for both
  OTLP/Grafana and Prometheus scraping.

## 🧪 Tests
- [ObservabilityBridgeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Observability/Bridge/ObservabilityBridgeTests.cs) — `OTelMetricNameBuilder` name/path normalization and snake_casing, `ResourceMetricsExporter` snapshot lifecycle, per-kind metric opt-out, and `MeterListener`-observed instrument registration

## 🔗 Related

- Sibling: [ECS Metrics Exporter](./ecs-metrics-exporter.md) — the other OTel metrics exporter; covers ECS/archetype state instead of Resource Graph nodes
- Sibling: [Observability Bridge (Resources to OTel/Health/Alerts)](../../Resources/observability-bridge-resources.md) — the same bridge, documented from the Resources category's side
- Related features: [Resource-Aware Health Checks](../health-checks.md), [Threshold-Based Resource Alerting](../threshold-alerting.md) — read the same `ResourceMetricsExporter` snapshot
- Related feature: [Metric Reporting (IMetricSource / IMetricWriter)](../../Resources/metric-reporting.md) — how resources produce the data this exports
- Parent feature: [OpenTelemetry Metrics Export](./README.md)

<!-- Deep dive: claude/design/Resources/08-observability-bridge.md §2, claude/design/Observability/implementing-metric-sources.md, claude/adr/032-resource-system-architecture.md -->
