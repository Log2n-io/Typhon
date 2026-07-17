---
uid: feature-observability-otel-metrics-export-index
title: 'OpenTelemetry Metrics Export'
description: 'Observable-pattern OTel Meter exporters that snapshot internal state and expose it as gauges/counters for Prometheus/OTLP scraping.'
---

# OpenTelemetry Metrics Export
> Observable-pattern OTel `Meter` exporters that snapshot internal state and expose it as gauges/counters for Prometheus/OTLP scraping.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Observability](../README.md)

## 🎯 What it solves

Operators want Typhon's internal numbers — page cache utilization, WAL ring fill, per-archetype entity counts,
transient memory pressure — in the dashboards and scrapers they already run (Prometheus, Grafana, SigNoz,
`dotnet-counters`). Hand-instrumenting every internal counter as an OTel instrument, and keeping that
instrumentation in sync as resources and archetypes come and go, is repetitive work every host application would
otherwise duplicate. Typhon exposes two purpose-built `Meter`s instead, each translating a different slice of
engine state into standard `System.Diagnostics.Metrics` instruments.

## ⚙️ How it works (in brief)

Both exporters use the same **observable** pattern: rather than pushing measurements on every state change, they
register `CreateObservableGauge`/`CreateObservableCounter` callbacks that are only invoked when an OTel listener
(a `MeterListener`, a Prometheus scrape, an OTLP export tick) actually asks for a value. The Resource Graph bridge
reads a periodically-refreshed `ResourceSnapshot` cache; the ECS exporter reads live engine fields directly (no
cache, no extra counters) since those reads are already cheap. Either way, an idle process with no collector
attached pays nothing beyond — for the Resource Graph bridge — the background snapshot timer.

## Sub-features

| Sub-feature | Exports | Use it when... |
|---|---|---|
| [Resource Graph Metrics Bridge](./resource-graph-metrics-bridge.md) | Memory/Capacity/DiskIO/Throughput/Duration for every `IResourceGraph` node | Wiring Prometheus/OTLP/Grafana dashboards for engine-wide resource pressure (page cache, WAL ring, transaction pool, ...) |
| [ECS Metrics Exporter](./ecs-metrics-exporter.md) | Per-archetype entity/EntityMap gauges, per-component transient memory gauges | Watching archetype growth, EntityMap hashing health, or Transient-mode component memory in a dashboard |

## ⚠️ Guarantees & limits

- Zero overhead when nothing is listening: observable callbacks only run on a scrape/collection pass, never on
  a timer of their own.
- The two exporters are independent — different `Meter` names (`Typhon.Resources` vs `Typhon.ECS`), different
  registration paths, no shared state. You can wire up either, both, or neither.
- **Partial coverage** — only resource-graph nodes and ECS archetype/transient state are exported this way today.
  The broader metrics catalog in the [per-domain named-metrics catalog](../per-domain-metrics-catalog.md)
  (transaction counts, lock contention, B+Tree mutations, WAL/checkpoint counters) is captured by the
  [Typed-Event Profiler](../README.md) and `TickTelemetryRing`, not yet bridged into OTel instruments.
- `EcsMetricsExporter` has no DI registration extension yet (unlike the Resource Graph bridge's
  `AddTyphonObservabilityBridge`) — construct it directly against a `DatabaseEngine` and register its `Meter`
  with your `MeterProvider`.
- Cardinality is bounded by resource-node count / archetype count / component-type count, not by entity count or
  operation volume — these are gauges over current state, not per-event counters.

## 🧪 Tests
- [ObservabilityBridgeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Observability/Bridge/ObservabilityBridgeTests.cs) — exercises the shared observable-callback pattern (snapshot-backed gauges, per-kind opt-out, `MeterListener` instrument registration) via the Resource Graph bridge; the ECS exporter sub-feature has no dedicated coverage yet

## 🔗 Related

- Sub-features: [Resource Graph Metrics Bridge](./resource-graph-metrics-bridge.md), [ECS Metrics Exporter](./ecs-metrics-exporter.md)
- Related features: [Resource-Aware Health Checks](../health-checks.md), [Threshold-Based Resource Alerting](../threshold-alerting.md) — read the same `ResourceMetricsExporter` snapshot as this bridge

<!-- Deep dive: claude/design/Resources/08-observability-bridge.md, claude/overview/09-observability.md §9.2 -->
