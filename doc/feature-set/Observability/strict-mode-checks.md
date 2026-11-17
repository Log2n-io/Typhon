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

Two more gates are independent of `Enabled` and default **on in a Debug build of your application** (its entry assembly
compiled without optimization), off otherwise: `DeclaredAccessActive` validates every read, write, spawn, destroy and event of a
system against its declaration (rule DV-01, a bitset test per access), and `PoisonDisposedTransactions` drops a disposed
transaction instead of pooling it, so a use after its `Dispose` keeps throwing instead of reaching the next caller's transaction
(rule TX-01, one allocation per transaction).

Independently of the gates, **always-on guards** run in every build: checks whose failure is silent corruption and whose cost is
a load and a compare per call. Duplicate segment-root and wrong-thread lock release/demote throw a `CorruptionException`; the
spatial R-Tree query DFS-stack overflow records a counter + one-shot warning. The cluster fast path refuses writable memory
(`GetSpan` / `Get`) over a `Versioned` component, a component with a `String1024` field or the realm key, and a `WriteSpatial` of
anything but the non-Versioned spatial component; a `ClusterRef` used after its enumerator moved on throws; `EntityRefMut.Set` refuses a
non-finite position. A `Transaction` is committed, rolled back and disposed only on the thread that created it, refuses every call
after its `Dispose`, and a second transaction of a Deferred unit of work is refused on another thread while the first lives (#1652,
#1653: all of these were strict-only or missing).

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
      "DeclaredAccess": true,
      "PoisonDisposedTransactions": true
    }
  }
}
```

| Key | Effect | Default |
|---|---|---|
| `Typhon:Checks:Enabled` | Master switch — turns on the ~47 cheap user-facing misuse checks | `false` |
| `Typhon:Checks:DeclaredAccess` | Validate a system's accesses against its declaration (DV-01, a bitset test per access). Independent of `Enabled` | on in a Debug build of the application, else `false` |
| `Typhon:Checks:PoisonDisposedTransactions` | Never pool a disposed transaction, so a use after `Dispose` keeps throwing (TX-01) | on in a Debug build of the application, else `false` |

| Source | Precedence | Example |
|---|---|---|
| Environment variable (`__` hierarchy separator) | Highest | `TYPHON__CHECKS__ENABLED=true` · `TYPHON__CHECKS__DECLAREDACCESS=true` · `TYPHON__CHECKS__POISONDISPOSEDTRANSACTIONS=true` |
| `typhon.telemetry.json` in the working directory | 2nd | shape above |
| `typhon.telemetry.json` next to the assembly | 3rd | shape above |
| Built-in defaults | Lowest | `Enabled` `false`; the other two follow the application's build |

## ⚠️ Guarantees & limits
- **`Enabled` is off by default, everywhere** — you enable strict mode deliberately. `DeclaredAccess` and
  `PoisonDisposedTransactions` read the application's build instead, and an explicit key overrides either.
- **Resolved once** per process (static constructor, forced early by a module initializer) and immutable thereafter — no
  runtime toggle; changing a flag means editing config/environment and restarting (a mutable field would defeat the JIT
  fold).
- **Zero overhead when off.** A `static readonly false` gate measures within noise of no guard at all (~0.24 ns/call,
  zero allocation); the failure message is never built and its interpolation arguments are never evaluated on the passing
  path. Checks whose *condition* itself costs something (a TLS read, a bounds-checked array load) use an inline
  `if (CheckConfig.Enabled && …)` guard so the gate short-circuits before that work.
- **What it catches (Enabled):** write-through a read-only ref, disabled / unregistered / wrong-archetype component
  access, the per-operation EntityAccessor thread-affinity check, spawn/destroy on an unregistered or uninitialized
  archetype, component payload-size / entity-id overflow, on-reopen store-corruption / schema-drift, and more. Failures throw
  `InvalidOperationException`.
- **DeclaredAccess is separate** from `Enabled`; an undeclared access throws `InvalidAccessException` naming the component and
  the offending system.
- **Always-on guards** hold in every build and independent of these flags: the corruption signals above, and the misuse checks
  of #1652 / #1653 whose failure would be silent (listed under *How it works*).
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
