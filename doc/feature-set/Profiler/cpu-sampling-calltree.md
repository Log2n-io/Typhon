---
uid: feature-profiler-cpu-sampling-calltree
title: 'Integrated CPU Sampling (Statistical Call Tree)'
description: 'See exactly where CPU cycles burn, down to the method and source line, with no external profiler.'
---

# Integrated CPU Sampling (Statistical Call Tree)
> See exactly where CPU cycles burn, down to the method and source line, with no external profiler.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Profiler](./README.md)

## 🎯 What it solves
Typed spans tell you how long an instrumented operation took, but not *why* — the time inside an
uninstrumented callee, a BCL method, or a hot loop is invisible to span-based tracing. Answering "where does
`MovementSystem.Execute` actually spend its CPU?" normally means reaching for a separate tool with its own 
launch ritual, its own output file, and no way to line results up against
the transaction/B+Tree/page-cache timeline you already captured. CPU sampling closes that gap in-process: it
captures statistical call stacks for the whole session and folds them into the same trace file, correlated
against everything else your run recorded.

## ⚙️ How it works (in brief)
When enabled, the engine opens an in-process EventPipe session against its own process and lets the .NET runtime's
own ~1 kHz sampler capture managed call stacks for the
session lifetime. Nothing is parsed while it runs: the raw stream is buffered and only decoded, symbol-resolved
against portable PDBs, and embedded as a trailer section of the `.typhon-trace` file once the session stops.
The Workbench renders the result as a dotTrace-style **Call Tree** — self/total time per method, hot-path
highlight, and a "Subsystems" breakdown by engine category — scoped to any time range, span, or system you pick,
with click-to-source on every frame.

## 💻 Usage
```csharp
// typhon.telemetry.json next to your executable — CPU sampling is opt-in, alongside its profiler siblings:
// {
//   "Typhon": {
//     "Telemetry": { "Enabled": true },
//     "Profiler": {
//       "Enabled": true,
//       "Trace": "session.typhon-trace",
//       "CpuSampling": { "Enabled": true }
//     }
//   }
// }

using Typhon.Engine;

// Zero-code path — the bootstrap starts the sampler before session metadata is captured and stops/parses
// it as part of ordinary trace teardown. No sampler-specific calls are needed in application code.
var runtime = TyphonRuntime.Create(dbe, schedule =>
{
    schedule.PublicTrack.DeclareDag("Sim").CallbackSystem("Tick", ctx => RunGameLogic(ctx));
});

// ...run the workload — every managed thread is sampled for the session's lifetime...

TyphonProfiler.Stop();   // session stops, stream is parsed off-thread, samples embedded in the trace file
```

| Option | Default | Effect |
|---|---|---|
| `Typhon:Profiler:CpuSampling:Enabled` | `false` | Starts the in-process EventPipe sampler; embeds resolved stacks in the trace on stop |
| `TYPHON__PROFILER__CPUSAMPLING__ENABLED` | — | Environment override for the flag above |

## ⚠️ Guarantees & limits
- **No external tool, no special launch** — a managed NuGet-backed session opened from inside the process;
  no `dotnet-trace` global tool, no pre-CLR environment variable, no machine-installed dependency.
- **No hot-path cost** — sampling runs on the runtime's own sampler thread at a fixed ~1 kHz cadence; it never
  touches the typed-event producer path. Disabled entirely when the config flag is off.
- **File mode only (v1)** — samples are resolved and embedded at session stop; live-TCP streaming does not
  yet carry CPU samples.
- **Managed stacks only** — native (C/C++) frames are not captured; BCL and dynamically-generated frames
  resolve name-only (no source line) where no local PDB exists. Engine and application code with a portable
  PDB resolves to `file:line:method` with click-to-source.
- **Statistical, not exhaustive** — a 1 kHz sampler; short-lived or rare code paths may be under-represented.
  Wider time ranges give more samples (more accurate) but blend more distinct behavior together — the Call
  Tree panel shows the active scope's sample count and a density sparkline so you can judge that trade-off.
- **Thread-time, classified** — the sampler catches every managed thread whether or not it's actually on a
  core. Samples are classified on-CPU / voluntary-wait / involuntary-stall (GC suspension, OS preemption); the
  view you pick decides which classes are folded into the tree versus collapsed into a labelled aggregate.
- **Best-effort** — if the runtime diagnostics server is unavailable (e.g. `DOTNET_EnableDiagnostics=0`) or
  the session fails to start, the engine logs one line and continues without CPU samples; a sampling failure
  never aborts or corrupts the rest of the trace.
- **No persisted intermediate file** — the raw EventPipe stream lives in a transient temp file only while the
  session is running; it is deleted once parsed, never a trace-adjacent deliverable.

## 🧪 Tests
- [CpuSamplerSessionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/CpuSamplerSessionTests.cs) — session lifecycle idempotency, QPC anchor, graceful-degrade when EventPipe diagnostics are unavailable
- [CpuSampleParserTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/CpuSampleParserTests.cs) — `.nettrace` parse + symbol resolution driven against a real in-process capture of a CPU-burning workload
- [CpuSampleSectionRoundTripTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/CpuSampleSectionRoundTripTests.cs) — CPU-sample trailer section write/read, absent-section case, and hard rejection of pre-v11 traces

## 🔗 Related
- Sibling features: [Configuration & Performance Tuning](./profiler-configuration-tuning.md), [Profiler Session Lifecycle & Zero-Code Bootstrap](./profiler-lifecycle-bootstrap.md), [GC Event Tracing](./gc-event-tracing.md)
- Source: `src/Typhon.Engine/Profiler/internals/CpuSamplerSession.cs`, `src/Typhon.Engine/Profiler/internals/CpuSample.cs`, `src/Typhon.Engine/Profiler/internals/CpuSampleParser.cs`, `src/Typhon.Engine/Profiler/internals/CpuSampleSectionEncoder.cs`, `src/Typhon.Engine/Profiler/public/ProfilerLauncher.cs`

<!-- Deep dive: claude/design/Profiler/11-cpu-sampling-integration.md, claude/design/Profiler/06-profiler-feature-roadmap.md -->
<!-- Overview: claude/overview/09-observability.md -->
