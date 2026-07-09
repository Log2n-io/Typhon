---
uid: feature-observability-strict-mode-checks
title: 'Runtime-Gated Correctness Checks (Strict Mode)'
description: 'Opt-in, JSON/env-var-driven runtime checks that turn silent API-misuse and engine-corruption into loud, catchable errors — at zero cost when off.'
---

# Runtime-Gated Correctness Checks (Strict Mode)
> Opt-in, JSON/env-var-driven runtime checks that turn silent API-misuse and engine-corruption into loud, catchable errors — at zero cost when off.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Observability](./README.md)

## 🎯 What it solves
The Release NuGet strips every `Debug.Assert` / `#if DEBUG` check, so a user who misuses the API — writing through a
read-only `EntityRef`, touching a disabled or unregistered component, sharing a transaction across threads, spawning an
uninitialized archetype — gets **no diagnostic**, just wrong results or a downstream crash far from the cause. Shipping a
separate Debug build via NuGet isn't possible (NuGet has no build-configuration axis). **Strict mode** surfaces the
valuable, *user-facing* checks at runtime instead: off by default, flipped on with one config key when you're diagnosing,
and JIT-eliminated to nothing when off — the same `static readonly bool` gate mechanism as [Telemetry
Configuration & Gating](telemetry-config-gating.md).

## ⚙️ How it works (in brief)
`CheckConfig` resolves two `static readonly bool` gates once, at static-class load, from the same merged
`typhon.telemetry.json` + environment-variable source as `TelemetryConfig`. Converted checks are written as
`CheckConfig.Require(CheckConfig.Enabled, condition, $"…")`: when the gate is off the JIT proves the field never changes
and deletes the whole call (message included); when on, a failing check throws `InvalidOperationException` (via the
engine's `ThrowHelper`). The failure message is built through an interpolated-string handler that skips all formatting —
and even the evaluation of its interpolation arguments — unless the check is actually firing, so a *passing* call in
strict mode allocates nothing.

A second gate, `DeclaredAccessActive`, is a **separate** opt-in for the one costly check
(`SystemAccessValidator.AssertWrite<T>`, two `HashSet` lookups per `Write<T>()` that verify a system only writes
components it declared) — so enabling strict mode does not tax every write unless you ask for it.

Independently of the gates, a handful of **Tier-0 always-on guards** run in every build: checks whose predicate already
executed in Release (only the reaction was stripped), promoted at zero added cost — duplicate segment-root and
wrong-thread lock release/demote now throw a `CorruptionException` (fail-fast instead of silently corrupting), and the
spatial R-Tree query DFS-stack overflow records a counter + one-shot warning so results are never silently truncated.

## 💻 Usage
```csharp
// Converted check (engine-internal) — folds to nothing when strict mode is off.
CheckConfig.Require(CheckConfig.Enabled, slot < archetype.ComponentCount,
    $"Slot {slot} out of range for archetype with {archetype.ComponentCount} components");

// Application code enables strict mode via config/env before launch (see below) — there is no runtime setter.
// Typhon.Engine self-initializes the gate via a module initializer; no host startup call is required.
```

`typhon.telemetry.json` (working directory, or next to the assembly):
```json
{
  "Typhon": {
    "Checks": {
      "Enabled": true,
      "DeclaredAccess": true
    }
  }
}
```

| Key | Effect | Default |
|---|---|---|
| `Typhon:Checks:Enabled` | Master switch — turns on the ~47 cheap user-facing misuse checks | `false` |
| `Typhon:Checks:DeclaredAccess` | Also validate declared system access (`AssertWrite<T>`, `HashSet` per write). Effective value is `Enabled AND DeclaredAccess` | `false` |

| Source | Precedence | Example |
|---|---|---|
| Environment variable (`__` hierarchy separator) | Highest | `TYPHON__CHECKS__ENABLED=true` · `TYPHON__CHECKS__DECLAREDACCESS=true` |
| `typhon.telemetry.json` in the working directory | 2nd | shape above |
| `typhon.telemetry.json` next to the assembly | 3rd | shape above |
| Built-in defaults | Lowest | both flags `false` |

## ⚠️ Guarantees & limits
- **Off by default, everywhere.** There is no build-configuration auto-detection — you enable strict mode deliberately,
  and a fresh deployment checks nothing until opted in.
- **Resolved once** per process (static constructor, forced early by a module initializer) and immutable thereafter — no
  runtime toggle; changing a flag means editing config/environment and restarting (a mutable field would defeat the JIT
  fold).
- **Zero overhead when off.** A `static readonly false` gate measures within noise of no guard at all (~0.24 ns/call,
  zero allocation); the failure message is never built and its interpolation arguments are never evaluated on the passing
  path. Checks whose *condition* itself costs something (a TLS read, a bounds-checked array load) use an inline
  `if (CheckConfig.Enabled && …)` guard so the gate short-circuits before that work.
- **What it catches (Enabled):** write-through a read-only ref, disabled / unregistered / wrong-archetype component
  access, EntityAccessor/Transaction thread-affinity violations, Versioned-slot bypass on clusters, spawn/destroy on an
  unregistered or uninitialized archetype, component payload-size / entity-id overflow, on-reopen store-corruption /
  schema-drift, and more (~47 sites). Failures throw `InvalidOperationException`.
- **DeclaredAccess is separate** because it is the only non-trivial check; `AssertWrite<T>` throws
  `InvalidAccessException` naming the undeclared component and the offending system.
- **Tier-0 guards are always on**, in every build and independent of these flags — duplicate segment-root and
  wrong-thread lock release/demote throw `CorruptionException`; the R-Tree DFS-overflow records a latch-safe counter +
  one-shot log warning. These are fail-fast corruption signals, not user-misuse checks.
- **Developer-only tripwires stay `Debug.Assert`** — internal invariants a user can't cause or fix remain Debug-only
  (obtained via a local clone + Debug build), so strict mode's surface is exactly the checks a user can act on.

## 🧪 Tests
- [CheckConfigTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Observability/CheckConfigTests.cs) — `Require`/`Record` primitives (throw on failure, no-op when off), the gate-field-shape reflection invariant, a lazy-message probe proving interpolation arguments aren't evaluated on the passing path, and a suite-config canary
- [StrictModeMisuseTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Observability/StrictModeMisuseTests.cs) — integration: write-through-readonly, destroy-null, and cross-thread transaction use all throw under strict mode
- [Tier0GuardsTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Observability/Tier0GuardsTests.cs) — wrong-thread exclusive release/demote throw `CorruptionException`; the DFS-overflow record increments its counter and never throws
- [SystemAccessValidatorTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/SystemAccessValidatorTests.cs) — `AssertWrite<T>` throws for an undeclared write under `DeclaredAccess`, passes for declared, and respects the per-thread push/pop scope

## 🔗 Related
- Sibling: [Telemetry Configuration & Gating](telemetry-config-gating.md) — the profiler/tracing gate this mirrors (same `static readonly` JIT-fold mechanism and config source)
- Source: `src/Typhon.Engine/Observability/public/CheckConfig.cs`, `src/Typhon.Engine/Spatial/internals/SpatialRTreeDiagnostics.cs`

<!-- Deep dive: claude/overview/09-observability.md §9.1 (Track 1 → Strict mode) -->
<!-- Design: claude/design/Observability/debug-checks-runtime-gating.md · ADR: claude/adr/019-runtime-telemetry-toggle.md (gate pattern) -->
