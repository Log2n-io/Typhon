---
uid: concept-observability
title: 'Observability & telemetry'
description: 'Gated telemetry with JIT-eliminated off-path cost, distributed tracing over the Activity API, OpenTelemetry metrics export, and resource-aware health checks — how you see inside a live engine.'
---

# Observability & telemetry

> **In one line:** see inside a live engine — **gated** telemetry (zero cost when off), distributed **tracing**, OpenTelemetry **metrics**, and **health checks** — without paying for what you don't turn on.

Typhon's instrumentation is *gated*: ~200 hierarchical flags (JSON / env-driven) guard every emission point, and when a flag is off the guard is JIT-eliminated to nothing — you pay only for the signal you enable. On top of that sit four consumer-facing surfaces: **distributed tracing** via the .NET `Activity` API with OpenTelemetry-semantic attributes; **metrics export** projecting the engine's resource graph and per-archetype ECS health as standard OTel instruments; framework-agnostic **health checks** (`ITyphonHealthCheck` → Healthy / Degraded / Unhealthy); and **threshold alerting** that raises Warning / Critical as resource metrics cross bounds.

This page doesn't reproduce the individual flags. The **[telemetry flags reference](xref:feature-observability-telemetry-flags-reference)** lists every gate — all ~224, each with its config key, default, and effect — generated from source and shipped with a copy-paste `typhon.telemetry.json` template; the **[gating page](xref:feature-observability-telemetry-config-gating)** explains the *model* (how flags resolve, parent-implies-children, JIT elimination). For metrics, the **[per-domain named-metrics catalog](xref:feature-observability-per-domain-metrics-catalog)** groups the ~40 **planned** fixed-name OTel instruments by domain — a design target, not yet wired to an exporter.

## How it relates

- **[DatabaseEngine](xref:concept-database-engine)** — what you observe; telemetry is configured on it.
- **[Errors & failures](xref:concept-errors)** — health checks and alerts turn failure signals into operational state.
- **[Tick](xref:concept-tick)** — per-tick telemetry (durations, overrun) is a primary signal source.
- **[Profiler](xref:concept-profiler)** — the other consumer of the same gates: a higher-throughput, non-OTel pipeline for deep-dive tracing.
- **[Resources & budgets](xref:concept-resources)** — the utilization tree these metrics and health checks project.

## In the API

- [`TelemetryConfig`](xref:Typhon.Engine.TelemetryConfig) — the gating configuration (the ~200 flags); [`GetConfigurationSummary()`](xref:Typhon.Engine.TelemetryConfig.GetConfigurationSummary) dumps the resolved set.
- [`ITyphonHealthCheck`](xref:Typhon.Engine.ITyphonHealthCheck) / [`HealthStatus`](xref:Typhon.Engine.HealthStatus) — the health-check contract.
- OTel export is wired through the resource-graph metrics bridge and the ECS metrics exporter (consumer-side, standard `Meter` instruments).

## Learn & use

- **Feature detail:** [Observability](xref:feature-observability-index) — [telemetry gating](xref:feature-observability-telemetry-config-gating) · [all flags (generated reference)](xref:feature-observability-telemetry-flags-reference) · [distributed tracing](xref:feature-observability-distributed-tracing) · [OTel metrics export](xref:feature-observability-otel-metrics-export-index) · [named-metrics catalog](xref:feature-observability-per-domain-metrics-catalog) · [health checks](xref:feature-observability-health-checks) · [threshold alerting](xref:feature-observability-threshold-alerting)
- **Narrative:** [Guide ch.6 — operating](xref:guide-operating)
