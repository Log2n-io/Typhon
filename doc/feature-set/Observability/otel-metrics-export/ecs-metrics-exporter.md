# ECS Metrics Exporter
> Per-archetype EntityMap health and per-component-type transient memory, as zero-cost OTel gauges.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Observability](../README.md)

## 🎯 What it solves

Archetype growth (entity counts, hash-table load factor, bucket splits) and Transient-mode component memory
pressure are exactly the signals an operator wants on a dashboard when diagnosing skewed entity distributions or
runaway transient allocation — but today they're only visible by stepping through `DatabaseEngine` internals in
a debugger or via storage introspection APIs. The ECS Metrics Exporter surfaces them as standard OTel
instruments without adding a single counter to the ECS hot path.

## ⚙️ How it works (in brief)

`EcsMetricsExporter` creates a `Meter` named `"Typhon.ECS"` and registers five observable instruments. Each
callback walks the engine's existing archetype-state array or component-table list directly — no cache, no
background timer, no new bookkeeping field — and reads fields that are already maintained for other purposes
(`EntityMap.EntryCount`, `EntityMap.LoadFactor`, `TransientComponentSegment.PageCount`). Because every field read
is already either plain or naturally atomic on x64, there is no `Interlocked` overhead added anywhere; the cost
exists only when an OTel collector actually scrapes.

## 💻 Usage

```csharp
using Microsoft.Extensions.DependencyInjection;
using Typhon.Engine;

var dbe = serviceProvider.GetRequiredService<DatabaseEngine>();
using var ecsExporter = new EcsMetricsExporter(dbe);

// Point an OTel MeterProvider at the exporter's meter:
services.AddOpenTelemetry().WithMetrics(builder => builder
    .AddMeter(EcsMetricsExporter.MeterName)            // "Typhon.ECS"
    .AddPrometheusExporter());                          // or AddOtlpExporter(), AddConsoleExporter(), ...
```

| Metric | Type | Tag | Description |
|---|---|---|---|
| `typhon.ecs.entity_count` | Gauge | `typhon.ecs.archetype` | Live entity count per archetype |
| `typhon.ecs.entitymap.load_factor` | Gauge | `typhon.ecs.archetype` | EntityMap hash-table load factor (0.0–1.0) per archetype |
| `typhon.ecs.entitymap.splits_total` | Counter | `typhon.ecs.archetype` | Cumulative EntityMap bucket splits per archetype |
| `typhon.ecs.transient.allocated_bytes` | Gauge | `typhon.ecs.component_type` | Transient heap memory allocated per `StorageMode.Transient` component type |
| `typhon.ecs.transient.utilization` | Gauge | `typhon.ecs.component_type` | Transient chunk utilization (allocated/capacity) per component type |

## ⚠️ Guarantees & limits

- No DI registration extension exists yet — unlike the Resource Graph bridge's `AddTyphonObservabilityBridge`,
  you construct `EcsMetricsExporter` directly and own its lifetime.
- Entity-count and EntityMap gauges only appear for archetypes that have an initialized `EntityMap`; archetypes
  with no entities spawned yet are absent from the output, not zero.
- Transient gauges only cover component tables with `StorageMode.Transient` and an initialized
  `TransientComponentSegment` — Versioned, SingleVersion, and Committed tables don't report through this
  exporter (they're covered by the Resource Graph bridge instead, where instrumented).
- All reads are zero-overhead by design: no new counters, no `Interlocked`, no allocation on the observable
  callback path beyond the `IEnumerable` iterator itself.
- `EcsMetricsExporter` implements `IDisposable`; disposing it disposes the `Meter` and detaches its instruments.
- No dedicated automated test coverage yet (unlike `ResourceMetricsExporter`, which has fixture coverage in
  `ObservabilityBridgeTests`) — verify wiring manually against a `MeterListener` until that gap closes.

## 🔗 Related

- Sibling: [Resource Graph Metrics Bridge](./resource-graph-metrics-bridge.md) — the other OTel metrics exporter; covers Resource Graph nodes instead of ECS/archetype state
- Related feature: [Entity Archetype Model](../../Ecs/entity-archetype-model.md), [Storage Modes](../../Ecs/storage-modes/README.md)
- Parent feature: [OpenTelemetry Metrics Export](./README.md)

<!-- Deep dive: claude/design/Observability/README.md, claude/overview/09-observability.md §9.2 -->
