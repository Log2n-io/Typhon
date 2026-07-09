---
uid: feature-profiler-profiler-configuration-tuning
title: 'Configuration & Performance Tuning'
description: 'Dial each profiler subsystem on or off, and tune the drain pipeline, without touching a build.'
---

# Configuration & Performance Tuning
> Dial each profiler subsystem on or off, and tune the drain pipeline, without touching a build.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Profiler](./README.md)

## 🎯 What it solves
Profiling is only "zero when disabled" if disabling it truly costs nothing, and only safe to enable in
production if you can turn on just the subsystem you need — not the whole tracer. Typhon exposes both knobs
outside the binary: a config file (or environment variables) toggles each opt-in subsystem independently, and
a settings object governs how the drain pipeline buffers events, trading latency against CPU.

## ⚙️ How it works (in brief)
`typhon.telemetry.json` (or `TYPHON__PROFILER__*` env vars) is read once, at class load, into
`TelemetryConfig` — a set of `static readonly bool` gates, one per subsystem, each combined with the master
`Profiler.Enabled` switch (e.g. `ProfilerGcTracingActive = ProfilerActive && ProfilerGcTracingEnabled`). Because
the fields are `static readonly`, the JIT proves a `false` gate can never flip and deletes the guarded code
outright. `ProfilerOptions` is a separate settings object governing the consumer thread's drain cadence and
buffering; today it ships with engine-tuned defaults (see limits).

## 💻 Usage
```csharp
// typhon.telemetry.json — dropped next to the executable, or set the TYPHON__PROFILER__* equivalents:
// {
//   "Typhon": {
//     "Profiler": {
//       "Enabled": true,
//       "GcTracing":         { "Enabled": true },
//       "MemoryAllocations": { "Enabled": false },
//       "CpuSampling":       { "Enabled": false },
//       "Gauges":            { "Enabled": true }
//     }
//   }
// }

using Typhon.Engine;

// Force the static constructor to run (and the JIT gate to resolve) before your hot paths compile,
// then log exactly what's active — useful at startup for a diagnostic banner.
TelemetryConfig.EnsureInitialized();
Console.WriteLine(TelemetryConfig.GetConfigurationSummary());

// Individual gates are public fields — branch on them for app-side diagnostics if you need to:
if (TelemetryConfig.ProfilerGcTracingActive)
{
    Console.WriteLine("GC tracing is capturing to the trace.");
}
```

```bash
# Override a single sub-flag for one run, no file edit:
TYPHON__PROFILER__GAUGES__ENABLED=false dotnet run
```

| Config key | Default | Enables |
|---|---|---|
| `Typhon:Profiler:Enabled` | `false` | Master gate — every flag below is forced off when this is `false` |
| `Typhon:Profiler:GcTracing:Enabled` | `false` | GC ingestion thread; `GcStart`/`GcEnd`/`GcSuspension` records |
| `Typhon:Profiler:MemoryAllocations:Enabled` | `false` | Per-alloc/free `MemoryAllocEvent` for `PinnedMemoryBlock` traffic |
| `Typhon:Profiler:CpuSampling:Enabled` | `false` | In-process CPU stack sampling, embedded in the trace file |
| `Typhon:Profiler:Gauges:Enabled` | `false` | Per-tick `PerTickSnapshot` (memory, page cache, WAL, tx/UoW) |

`ProfilerOptions` fields (engine-internal defaults today — see limits):

| Field | Default | Controls |
|---|---|---|
| `ConsumerCadence` | 1 ms | Drain-thread wake interval — lower = lower live-viewer latency, higher CPU |
| `PerExporterChannelDepth` | 4 | Batches buffered per exporter before drop-newest kicks in |
| `MergeBufferBytes` | 512 KB | Per-pass merge scratch capacity on the drain thread |
| `SpilloverBufferCount` / `SpilloverBufferSizeBytes` | 8 × 16 MiB | Overflow rings a producer chains onto when its primary ring fills |

## ⚠️ Guarantees & limits
- Disabled subsystems cost exactly 0 ns — Tier 1 JIT dead-code-eliminates the guarded block (ADR-019),
  proven by benchmark, not just claimed. Every sub-flag is independent: `GcTracing` costs nothing on
  `MemoryAllocations` or `Gauges`.
- Gates resolve once, at class load — editing the JSON/env after the process starts has no effect until restart.
- Effective precedence, highest to lowest: environment variables → `typhon.telemetry.json` → built-in
  (all-`false`) defaults. Keys live under the `Typhon:Profiler:*` namespace.
- Measured overhead with GcTracing + MemoryAllocations + Gauges all on: under 1% scheduler-thread cost on
  tested workloads.
- `ProfilerOptions` tuning is not yet exposed on the standard zero-code path (`AddTyphonProfiler` +
  `TyphonRuntime.Create`): the internal bootstrap always constructs `new ProfilerOptions()`. Setting a custom
  instance requires calling `TyphonProfiler.Start` directly, which is `internal` — reachable only by hosts with
  no `TyphonRuntime` of their own (today, Typhon's own tooling). No public override hook exists yet.
- `TyphonProfiler.TotalDroppedEvents > 0` after a run means the producer outran the consumer — check
  `ConsumerCadence` and spillover sizing first.

## 🧪 Tests
- [TelemetryConfigGateShapeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Observability/TelemetryConfigGateShapeTests.cs) — enforces every `*Active` gate field is `public static readonly bool`, the structural precondition the JIT dead-code-elimination claim depends on
- [TelemetryConfigCpuSamplingTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/TelemetryConfigCpuSamplingTests.cs) — one gate worked end-to-end: reads from `typhon.telemetry.json`, composes `Active = ProfilerActive && Enabled`, surfaces in `GetConfigurationSummary`

## 🔗 Related
- Sibling: [Profiler Session Lifecycle & Zero-Code Bootstrap](./profiler-lifecycle-bootstrap.md) — reads this config to decide which exporters and subsystems to start
- Sibling: [Domain-Specific Tracing Instrumentation Expansion](./domain-tracing-expansion.md) — the Tier-2 per-domain gating scheme layered on top of this mechanism
- Source: `src/Typhon.Engine/Observability/public/TelemetryConfig.cs`, `src/Typhon.Engine/Profiler/public/ProfilerOptions.cs`, `src/Typhon.Engine/Profiler/internals/ProfilerBootstrap.cs`

<!-- Deep dive: claude/design/Profiler/profiler-user-manual.md §3, claude/design/Profiler/typhon-profiler.md §9 -->
<!-- ADR: claude/adr/019-runtime-telemetry-toggle.md -->
