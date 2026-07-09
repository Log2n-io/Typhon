---
uid: feature-profiler-index
title: 'Profiler'
description: 'Typhon''s engine-embedded, zero-allocation typed-event profiler: any-thread ~25-50ns span/instant capture into per-thread ring buffers, drained by a…'
---

# Profiler
> Typhon's engine-embedded, zero-allocation typed-event profiler: any-thread ~25-50ns span/instant capture into per-thread ring buffers, drained by a dedicated consumer thread and exported to a versioned `.typhon-trace` file and/or a live TCP feed for the Workbench trace viewer. Built-in instrumentation covers transactions, the B+Tree, page cache, WAL, checkpoint, and ECS automatically; opt-in extensions on the same pipeline add GC events, native memory tracking, per-tick gauges, CPU sampling, off-CPU scheduling, source attribution, query definition export, and domain-specific forensic tracing, each independently gated and zero-cost when off.

> 🔬 **Recommended:** Profiler doesn't have its own in-depth-overview chapter — read [in-depth-overview/12-observability.md](../../in-depth-overview/12-observability.md) (Chapter 12: Observability, merged since the typed-event pipeline the two share is one cohesive story) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Profiler Session Lifecycle & Zero-Code Bootstrap](profiler-lifecycle-bootstrap.md) | `TyphonProfiler.Start`/`Stop` (idempotent) manage the consumer/exporter threads and process-exit safety net; `ProfilerBootstrap` self-wires the whole session from `typhon.telemetry.json` + `TyphonRuntime.Create` with no host orchestration required | ✅ Implemented | 🟢 Start Here |
| [Trace Export](trace-export/README.md) | `IProfilerExporter` fan-out: each attached exporter gets its own OS thread and bounded queue (drop-newest on backpressure, refcounted batches); file and live-TCP sinks cover offline post-mortem and real-time attach | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [File-Based Trace Export (.typhon-trace)](trace-export/file-trace-export.md) | Write the whole session to a versioned binary file for offline post-mortem analysis in the Workbench | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Live TCP Streaming Export](trace-export/live-tcp-trace-export.md) | Stream a running session over TCP so the Workbench can watch a process tick-by-tick, right now | ✅ Implemented | 🔵 Core |
| [Configuration & Performance Tuning](profiler-configuration-tuning.md) | `typhon.telemetry.json` / `TYPHON__PROFILER__*` env overrides drive independent static-readonly-bool gates per subsystem (JIT-eliminated when off); `ProfilerOptions` documents the consumer/drain tunables (cadence, per-exporter queue depth, merge-buffer size) | ✅ Implemented | 🔵 Core |
| [Per-Tick Gauge/Metric Snapshots](gauge-snapshots.md) | One packed record per tick exposes memory, page-cache, WAL, and transaction counters to the trace viewer | ✅ Implemented | 🔵 Core |
| [Custom Application-Defined Spans](custom-named-spans.md) | Reserved wire-format span kind (`NamedSpan`, ID 246) for app-defined span names — read/replay support exists, but no producer factory exists in `TyphonEvent` yet | 📋 Planned | 🟣 Advanced |
| [GC Event Tracing](gc-event-tracing.md) | See every .NET garbage collection and EE-suspension pause on the same timeline as your transactions | ✅ Implemented | 🟣 Advanced |
| [Unmanaged Memory Allocation Tracing](unmanaged-allocation-tracing.md) | See every native (unmanaged) allocation and free on the profiler timeline, tagged by subsystem | 🚧 Partial | 🟣 Advanced |
| [Off-CPU Thread Scheduling Capture (Windows)](offcpu-thread-scheduling.md) | `EtwSchedulingPump` emits one `ThreadContextSwitch` record per closed on-CPU slice via the NT Kernel Logger, showing when and why Typhon threads left the CPU | ✅ Implemented | 🟣 Advanced |
| [Integrated CPU Sampling (Statistical Call Tree)](cpu-sampling-calltree.md) | In-process EventPipe `SampleProfiler` captures call stacks for the session, embedded as a trailer section and rendered as a dotTrace-style Call Tree | ✅ Implemented | 🟣 Advanced |
| [Query Definition & Execution Export](query-definition-export.md) | Captures every View/EcsQuery's structural definition once per session plus per-execution args and call sites, correlated to the existing query span chain | 🚧 Partial | 🟣 Advanced |
| [Domain-Specific Tracing Instrumentation Expansion](domain-tracing-expansion.md) | Two-tier compile-time gated tracing rollout across nine engine domains (Concurrency, Storage, Memory, Data, Query, ECS, Spatial, Scheduler/Runtime, Durability, Subscriptions), zero cost when off | 🚧 Partial | 🟣 Advanced |

## Internal Features

| Feature | Summary | Status |
|---|---|---|
| [Typed-Event Capture Pipeline](typed-event-capture-pipeline.md) | Any-thread ~25-50ns ref-struct span/instant emission into per-thread SPSC ring buffers, drained by a dedicated timestamp-sorting consumer thread; zero allocation, JIT-eliminated when disabled | ✅ Implemented |
| [Built-in Engine Instrumentation Catalog](builtin-subsystem-instrumentation.md) | Automatic, no-app-code-required span/instant coverage of Scheduler, Transactions, ECS, B+Tree, Page Cache (incl. async-completion pairing), WAL, Checkpoint, Statistics and Cluster Migration — 37+ wire-stable event kinds decoded by a shared `TraceEventDecoder` | ✅ Implemented |
| [Span Source Attribution (Go-to-Source)](source-attribution.md) | Every span optionally carries a compile-time-deterministic source location for one-click editor handoff and inline preview in the Workbench | ✅ Implemented |
| [Lock-Contention Forensics (Deep Diagnostics)](lock-contention-diagnostics.md) | Post-mortem visibility into which threads waited on which locks, for how long, and why | 📋 Planned |