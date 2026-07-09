---
uid: feature-profiler-profiler-lifecycle-bootstrap
title: 'Profiler Session Lifecycle & Zero-Code Bootstrap'
description: 'Turn profiling on with a config file — no startup/shutdown code, and no lost trace on crash or exit.'
---

# Profiler Session Lifecycle & Zero-Code Bootstrap
> Turn profiling on with a config file — no startup/shutdown code, and no lost trace on crash or exit.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Profiler](./README.md)

## 🎯 What it solves
A profiler needs someone to start its consumer thread, initialize exporters in the right order, and guarantee
the trace is finalized even if the host forgets to tear it down — or crashes. Getting that sequencing right
(CPU sampler before session metadata, exporters ready before the first batch, trace header patched exactly
once) is boilerplate no application developer should have to write per host. Typhon eliminates it: dropping a
config file next to the executable is enough to get a fully working trace, safely finalized on every exit path.

## ⚙️ How it works (in brief)
`ProfilerBootstrap` runs automatically at engine load and again inside `TyphonRuntime.Create`: it reads the
merged configuration, builds the exporters, starts the optional CPU sampler, composes session metadata from the
live runtime, and calls `TyphonProfiler.Start`. Teardown is wired to the engine storage's disposal so the trace
finalizes on ordinary shutdown; `AppDomain.ProcessExit`/`UnhandledException` handlers act as a backup for hosts
that skip disposal or crash. `TyphonProfiler.Start`/`Stop` are idempotent, so redundant calls from the bootstrap,
the safety net, and an explicit host call never conflict.

## 💻 Usage
```csharp
// 1. Drop next to the executable (or set TYPHON__PROFILER__* env vars) — no code required:
// typhon.telemetry.json
// {
//   "Typhon": { "Profiler": { "Enabled": true, "Trace": "session.typhon-trace" } }
// }

using Typhon.Engine;

// 2. Build the runtime as usual — profiling self-wires inside Create() because the config above is active.
var runtime = TyphonRuntime.Create(dbe, schedule =>
{
    schedule.PublicTrack.DeclareDag("Sim").CallbackSystem("Tick", ctx => RunGameLogic(ctx));
});

// ...run the workload; every built-in call site (transactions, B+Trees, page cache, WAL, checkpoints, ECS)
// emits automatically — no tracing calls needed in application code...

// 3. Ordinary shutdown finalizes the trace automatically (engine storage disposal fires it). No explicit
// Stop() call is required, but the host may end the session earlier if it wants to:
TyphonProfiler.Stop();
```

| Signal | Effect |
|---|---|
| `Typhon.Telemetry.Enabled` / `Profiler.Enabled` = `false` | Bootstrap is a no-op; zero runtime cost |
| Engine storage disposed (normal shutdown) | Primary teardown — trace finalized deterministically |
| `AppDomain.ProcessExit` / terminating unhandled exception | Backup teardown for hosts that never dispose |
| `TyphonProfiler.Stop()` called explicitly | Early, host-initiated teardown (idempotent) |

## ⚠️ Guarantees & limits
- Zero host code required for the common case — a `typhon.telemetry.json` file plus `TyphonRuntime.Create` is
  the entire integration surface.
- `TyphonProfiler.Start`/`Stop` are idempotent: a double-start is a no-op, a double-stop is a no-op — safe
  under restart-after-crash or a redundant safety-net firing.
- Bootstrap failures (port already in use, unwritable trace path) are caught and logged to stderr; the host
  keeps running without profiling rather than crashing.
- The process-exit safety net does **not** catch `TerminateProcess`/`taskkill /F`/`SIGKILL`,
  `StackOverflowException`, or access violations — the OS reaps the process before managed code runs. Only a
  clean shutdown or a caught, terminating unhandled exception finalizes the trace.
- ProcessExit handlers are budget-capped by the CLR (~2 s); a hung consumer or exporter thread can be cut
  short, in which case the trailing block of the trace may be lost.
- Session metadata (systems, archetypes, component types, tracks, resource graph) is captured once at start
  and is immutable for the lifetime of the session.
- `AttachExporter`/`DetachExporter` (advanced, low-level API) are only valid while the profiler is stopped —
  mutating exporters while running throws `InvalidOperationException`.

## 🧪 Tests
- [ProfilerSessionMetadataBuilderTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/ProfilerSessionMetadataBuilderTests.cs) — session metadata composition (systems/archetypes/component types derived from `RuntimeOptions`); per the fixture's own remarks, the full `ProfilerBootstrap.TryStart` self-wiring path gates on a `static readonly` flag and can't be unit-tested in isolation, so this covers the part that can be
- [FileExporterIntegrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/FileExporterIntegrationTests.cs) — exercises `TyphonProfiler.Start`/`Stop` end-to-end (start, emit, stop-finalizes) as part of the file-export round-trip

## 🔗 Related
- Sibling: [Configuration & Performance Tuning](./profiler-configuration-tuning.md) — the config this bootstrap reads to decide which exporters and subsystems to start
- Sibling: [Trace Export](./trace-export/README.md) — the exporters this bootstrap builds and attaches before starting the session
- Source: `src/Typhon.Engine/Profiler/public/TyphonProfiler.cs`, `src/Typhon.Engine/Profiler/internals/ProfilerBootstrap.cs`

<!-- Deep dive: claude/design/Profiler/profiler-user-manual.md §2, claude/design/Profiler/typhon-profiler.md §6 -->
<!-- Overview: claude/overview/09-observability.md -->
