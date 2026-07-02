# Observability
> Zero-overhead telemetry infrastructure for Typhon: a single hierarchical static-readonly gating surface shared by the typed-event Profiler, a `System.Diagnostics.Activity`-based tracing surface for correlating Typhon with a host's own OTLP trace, OpenTelemetry metrics export from the Resource Graph and ECS layer, and resource-derived health checks / threshold alerting for production monitoring.

> 🔬 **Recommended:** read [in-depth-overview/12-observability.md](../../in-depth-overview/12-observability.md) (Chapter 12: Observability, which also covers Profiler — the two share one chapter) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Telemetry Configuration & Gating](telemetry-config-gating.md) | Hierarchical, JSON/env-var-driven static-readonly bool surface (~200 flags) that the JIT dead-code-eliminates when off, gating both Activity tracing and the typed-event Profiler with zero overhead when disabled | ✅ Implemented | 🟢 Start Here |
| [Distributed Tracing (Activity API)](distributed-tracing.md) | Centralized `ActivitySource` and OTel-semantic attribute constants for correlating Typhon with a host's own OTLP trace — engine-internal operations aren't auto-instrumented on it yet (that's the Profiler's job) | 🚧 Partial | 🔵 Core |
| [OpenTelemetry Metrics Export](otel-metrics-export/README.md) | Observable-pattern OTel `Meter` exporters that periodically snapshot internal state and expose it as gauges/counters for Prometheus/OTLP scraping, with zero overhead when no collector is listening | 🚧 Partial | 🔵 Core |
| &nbsp;&nbsp;↳ [Resource Graph Metrics Bridge](otel-metrics-export/resource-graph-metrics-bridge.md) | Every Resource System node exposed as a standard OTel Meter for Prometheus/OTLP scraping | 🚧 Partial | 🟣 Advanced |
| &nbsp;&nbsp;↳ [ECS Metrics Exporter](otel-metrics-export/ecs-metrics-exporter.md) | Per-archetype EntityMap health and per-component-type transient memory as zero-cost OTel gauges | 🚧 Partial | 🟣 Advanced |
| [Resource-Aware Health Checks](health-checks.md) | Framework-agnostic `ITyphonHealthCheck` (Healthy/Degraded/Unhealthy worst-of-composite) backed by `ResourceHealthChecker`, reading the cached resource snapshot for cheap, frequent liveness/readiness polling | ✅ Implemented | 🔵 Core |
| [Per-Domain Named Metrics Catalog](per-domain-metrics-catalog.md) | Documented target list of ~40 fixed-name OTel instruments (`typhon.tx.*`, `typhon.wal.*`, `typhon.lock.*`...) across 8 domains, distinct from today's generic resource-path/ECS exporters | 📋 Planned | 🟣 Advanced |
| [Threshold-Based Resource Alerting](threshold-alerting.md) | `ResourceAlertGenerator` raises Warning/Critical `ResourceAlert`s on health-state transitions, using `FindRootCause` to trace symptoms back to the upstream bottleneck via a hardcoded wait-dependency graph | ✅ Implemented | 🟣 Advanced |

## Internal Features

*No internal-only features in this category — every feature here is directly usable from application code (config knobs, DI extensions, or classes constructed/called directly by host apps).*