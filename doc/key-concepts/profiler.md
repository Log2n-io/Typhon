---
uid: concept-profiler
title: 'Profiler'
description: 'The engine-embedded typed-event profiler — any-thread ~25-50 ns capture into per-thread ring buffers, exported to a .typhon-trace file or streamed live to the Workbench. Gated by the same telemetry flags; zero-cost when off.'
---

# Profiler

> **In one line:** the engine's own **typed-event profiler** — ~25–50 ns capture from any thread, drained off-thread, viewed in the Workbench. Gated by the same [telemetry flags](xref:concept-observability); zero-cost when off.

A producer emits a typed span or instant into a **per-thread ring buffer** — zero-allocation, ~25–50 ns, safe from any thread — and a dedicated consumer thread drains them. Nothing is serialized on your hot path. The engine already instruments itself: transactions, the B+Tree, page cache, WAL, checkpoint, and ECS have built-in coverage, so a trace is useful before you add a single span of your own.

Output goes through an [`IProfilerExporter`](xref:Typhon.Engine.IProfilerExporter) fan-out — a versioned **`.typhon-trace` file** for offline post-mortem, and/or a **live TCP feed** so the Workbench can watch a running process tick by tick. On the same pipeline, opt-in extensions add GC events, native-memory tracking, per-tick gauges, CPU sampling, off-CPU thread scheduling, and source attribution (go-to-source on any span) — each independently gated and JIT-eliminated when off.

> 💡 **This is not OpenTelemetry.** The profiler is a separate, higher-throughput, non-OTel pipeline aimed at the Workbench; [distributed tracing](xref:concept-observability) is the OTel-facing surface for correlating Typhon with your host's own trace. The two share one gating surface, not one pipeline.

## How it relates

- **[Observability & telemetry](xref:concept-observability)** — the same `TelemetryConfig` gates drive both; that's the metrics/OTel half, this is the deep-dive half.
- **[Tick](xref:concept-tick)** — per-tick gauges and spans are how you see where a tick's budget actually went.
- **[Resources & budgets](xref:concept-resources)** — utilization tells you *that* you're under pressure; a trace tells you *where* the time went.

## In the API

- `TyphonProfiler.Start` / `Stop` — session lifecycle (idempotent); `ProfilerBootstrap` self-wires a session from `typhon.telemetry.json`.
- [`ProfilerOptions`](xref:Typhon.Engine.ProfilerOptions) — consumer/drain tunables (cadence, per-exporter queue depth, buffer sizes).
- [`IProfilerExporter`](xref:Typhon.Engine.IProfilerExporter) — the export contract; file and live-TCP sinks ship.

## Learn & use

- **Feature detail:** [Profiler](xref:feature-profiler-index) — [session lifecycle & bootstrap](xref:feature-profiler-profiler-lifecycle-bootstrap) · [trace export](xref:feature-profiler-trace-export-index) · [typed-event capture](xref:feature-profiler-typed-event-capture-pipeline) · [built-in instrumentation](xref:feature-profiler-builtin-subsystem-instrumentation) · [config & tuning](xref:feature-profiler-profiler-configuration-tuning)
- **Reference:** [Telemetry flags](xref:feature-observability-telemetry-flags-reference) — every gate, the profiler's included.
