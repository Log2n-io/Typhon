# Profiler Launch Override Hook
> Adjust the resolved profiler launch config in code, on top of file/env, without giving up zero-code defaults.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Hosting](./README.md)

## 🎯 What it solves

The profiler self-wires from `typhon.telemetry.json` (+ `TYPHON__PROFILER__*` env vars) with zero host
code. Some hosts still need to decide the trace path or live port in code — e.g. layering `--trace`/
`--live` CLI args on top of the file config, or computing a per-run trace path (timestamped, per-match,
per-test-case) that a static JSON value can't express. This hook adds that one escape hatch without
requiring every host to hand-roll profiler bootstrap.

## ⚙️ How it works (in brief)

`AddTyphonProfiler` registers a delegate that maps the config resolved from file + environment to the
effective `ProfilerLaunchConfig`. The engine's runtime bootstrap (`TyphonRuntime.Create`) resolves and
applies it automatically when profiling starts — no other wiring is needed. If the delegate returns
`null`, the resolved config is used unchanged. Precedence is fixed: JSON file → environment →
your delegate. If you never call `AddTyphonProfiler`, nothing changes — the zero-code self-wiring path
runs exactly as before.

## 💻 Usage

```csharp
using Microsoft.Extensions.DependencyInjection;
using Typhon.Engine;

var services = new ServiceCollection();
services.AddDatabaseEngine();

// Layer CLI args on top of typhon.telemetry.json / env — CLI wins where it sets a value.
services.AddTyphonProfiler(resolved => resolved.MergedWith(ProfilerLaunchConfig.FromArgs(args)));

var provider = services.BuildServiceProvider();
var engine = provider.GetRequiredService<DatabaseEngine>();

// serviceProvider must be passed here for the override to be resolved and applied.
var runtime = TyphonRuntime.Create(engine, sched => { /* register systems */ }, serviceProvider: provider);
```

| Registration | Effect |
|---|---|
| `AddTyphonProfiler(resolved => resolved.MergedWith(ProfilerLaunchConfig.FromArgs(args)))` | CLI flags (`--trace`, `--live [port]`, `--live-wait <ms>`) override unset-only fields of the file/env config. |
| `AddTyphonProfiler(resolved => resolved with { TraceFilePath = ComputePath() })` | Fully computed trace path, ignoring whatever the file/env supplied. |

## ⚠️ Guarantees & limits

- **Opt-in, additive.** Calling `AddTyphonProfiler` never changes behavior unless the `IServiceProvider`
  is also passed to `TyphonRuntime.Create` — the override is only resolved from that container.
- **⚠️ Zero-arg call resolves to a different, unrelated overload.** `Typhon.Engine` also declares
  `TelemetryServiceExtensions.AddTyphonProfiler(IServiceCollection)` (forces early `TelemetryConfig`
  init; no delegate parameter). C# overload resolution prefers that exact-arity match over this
  hook's optional-parameter overload, so `services.AddTyphonProfiler()` with **no** argument silently
  calls the *other* method and never registers a `ProfilerLaunchOverride`. Always pass a delegate
  explicitly to reach this hook.
- **Cannot enable profiling from a closed master gate.** The delegate only runs once
  `typhon.telemetry.json`'s master `Typhon:Profiler:Enabled` is already on; it can add or change *where* output goes
  (trace file / live port), not flip profiling on for a session where it's off. If the resulting
  config still has no output channel, nothing is exported.
- **Best-effort.** Profiler startup — including your delegate — runs inside a try/catch that never
  crashes the host; a throwing delegate disables profiling for that session with a logged diagnostic,
  it does not fault application startup.
- **Runs once per process.** The hook fires the first time `TyphonRuntime.Create` self-wires the
  profiler; it is not re-invoked per runtime instance.

## 🧪 Tests

- [ProfilerLaunchConfigTests](../../../test/Typhon.Engine.Tests/Profiler/ProfilerLaunchConfigTests.cs) — `MergedWith` precedence (`MergedWith_OverrideTraceWinsWhenSet`, `_BaseRetainedWhenOverrideUnset`, `_NullOverride_ReturnsBase`) and `TypicalLayering_ConfigFirstThenArgsOverride`, which its own comment ties directly to "the `AddTyphonProfiler` hook"; no fixture calls `AddTyphonProfiler` itself — this covers the merge logic the delegate composes over

## 🔗 Related

- Source: [`TyphonBuilderExtensions.cs`](../../../src/Typhon.Engine/Hosting/public/TyphonBuilderExtensions.cs) (`AddTyphonProfiler`), [`ProfilerBootstrap.cs`](../../../src/Typhon.Engine/Profiler/internals/ProfilerBootstrap.cs) (`TryStart`), [`ProfilerLaunchConfig.cs`](../../../src/Typhon.Engine/Profiler/public/ProfilerLaunchConfig.cs), [`TyphonRuntime.cs`](../../../src/Typhon.Engine/Runtime/public/TyphonRuntime.cs) (`Create`)
- Colliding overload: [`TelemetryServiceExtensions.cs`](../../../src/Typhon.Engine/Observability/public/TelemetryServiceExtensions.cs) (`AddTyphonProfiler(IServiceCollection)`, no delegate)
- Sibling: [Profiler Session Lifecycle & Zero-Code Bootstrap](../Profiler/profiler-lifecycle-bootstrap.md) — the zero-code self-wiring this hook layers an override on top of.

<!-- Deep dive: claude/design/Profiler/README.md, claude/design/Profiler/profiler-user-manual.md -->
