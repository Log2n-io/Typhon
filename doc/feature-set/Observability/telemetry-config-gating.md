---
uid: feature-observability-telemetry-config-gating
title: 'Telemetry Configuration & Gating'
description: 'One hierarchical static-readonly bool surface that gates both tracing and the typed-event profiler at zero cost when off.'
---

# Telemetry Configuration & Gating
> One hierarchical static-readonly bool surface that gates both tracing and the typed-event profiler at zero cost when off.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Observability](./README.md)

## 🎯 What it solves
Typhon instruments roughly 200 call-site families across concurrency, storage, indexing, query, ECS, and durability.
Application developers need to turn this on selectively for diagnosis — a single subsystem, a single sub-operation —
without shipping a different binary, without a config system per instrumentation track, and without paying for the
"is anyone watching" check when nothing is. `TelemetryConfig` is the one place that answers "is X being observed
right now" for both distributed tracing spans and the typed-event Profiler.

## ⚙️ How it works (in brief)
`TelemetryConfig` is resolved once, at static-class load, from `typhon.telemetry.json` plus environment variables,
into ~240 `static readonly bool` gate fields (mostly `*Active`). Flags are organized as a tree (e.g. `Concurrency` → `AccessControl`
→ `Contention`) with parent-implies-children semantics: a disabled parent silences every descendant regardless of
its own setting, and a leaf with no explicit key inherits its parent's effective state. Because each field is
`static readonly`, the JIT can prove it never changes after class init and deletes a disabled `if (TelemetryConfig.XxxActive)`
block entirely at Tier 1. The flag tree is declared once in a catalog (`telemetry-flags.jsonc`) from which a source
generator emits the gate fields, so there is one on/off surface, not one per instrumentation track.

## 💻 Usage
```csharp
// Hot-path producer code — the gate is the only cost when the flag is off.
if (TelemetryConfig.DataMvccChainWalkActive)
{
    RecordChainWalkDepth(depth);
}

// Optional host startup — Typhon.Engine self-initializes via a module initializer,
// but a DI host can force resolution explicitly before building the service provider.
services.AddTyphonProfiler();

// Log what was actually resolved at startup.
_logger.LogInformation(TelemetryConfig.GetConfigurationSummary());
```

`typhon.telemetry.json` (working directory, or next to the assembly):
```json
{
  "Typhon": {
    "Profiler": {
      "Enabled": true,
      "Concurrency": {
        "Enabled": true,
        "AccessControl": { "Contention": { "Enabled": true } }
      }
    }
  }
}
```

| Source | Precedence | Example |
|---|---|---|
| Environment variable (`__` hierarchy separator) | Highest | `TYPHON__PROFILER__CONCURRENCY__ENABLED=true` |
| `typhon.telemetry.json` in the working directory | 2nd | shape above |
| `typhon.telemetry.json` next to the assembly | 3rd | shape above |
| Built-in defaults | Lowest | every flag `false`, except the four default-on Scheduler gauges |

## ⚠️ Guarantees & limits
- Resolved once per process (static constructor, forced early by a module initializer) and immutable thereafter —
  no runtime toggle; changing a flag means editing config/environment and restarting.
- Flags default to `false`, with one deliberate exception: four raw-leaf Scheduler gauges (`TransitionLatency`,
  `WorkerUtilization`, `StragglerGap`, `ArchetypeTouches`) default to `true` and are read without parent gating — see the
  [flags reference](./telemetry-flags-reference.md). The profiler master itself defaults off.
- Parent-implies-children: disabling a subtree's root disables every descendant even if individually set `true`.
- Benchmark-verified zero overhead: a `static readonly false` guard measures identical to no guard at all
  (~0.22 ns); `true` costs ~0.22 ns more; a mutable `static` field or interface dispatch costs 200–500%+ more for
  the equivalent check.
- The engine's consumer of these gates is the typed-event Profiler. The [distributed tracing](./distributed-tracing.md)
  `ActivitySource` is not called by the engine today, so no engine `Activity` span is gated here yet.

## 🧪 Tests
- [TelemetryConfigResolverTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Observability/TelemetryConfigResolverTests.cs) — parent-implies-children resolution: parent-off cascades to children despite explicit `true`, explicit leaf override wins, implicit leaf inherits parent
- [TelemetryConfigGateShapeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Observability/TelemetryConfigGateShapeTests.cs) — enforces every `*Active` field is `public static readonly bool` (the structural invariant the JIT dead-code-elimination guarantee depends on)
- [TelemetryConfigCpuSamplingTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/TelemetryConfigCpuSamplingTests.cs) — end-to-end resolution of a real flag from `typhon.telemetry.json`, composed `XxxActive` derivation, and `GetConfigurationSummary()` diagnostics

## 🔗 Related
- Sibling: [Distributed Tracing (Activity API)](./distributed-tracing.md) — the Activity-based surface; not gated here today since the engine starts no spans on it
- Sibling: [Profiler](../Profiler/README.md) — the typed-event pipeline that is this gating surface's other consumer
- Source: `src/Typhon.Engine/Observability/public/TelemetryConfig.cs`, `TelemetryConfigResolver.cs`, `TelemetryServiceExtensions.cs`

<!-- Deep dive: claude/overview/09-observability.md §9.1 -->
<!-- ADR: claude/adr/019-runtime-telemetry-toggle.md -->
