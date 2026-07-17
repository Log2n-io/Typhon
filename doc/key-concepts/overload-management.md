---
uid: concept-overload-management
title: 'Overload management'
description: 'A single-writer state machine that measures tick overrun and, under sustained load, throttles systems, slows the tick rate, and finally signals game code to shed load — degrading instead of crashing.'
---

# Overload management

> **In one line:** when a tick runs over budget for too long, the runtime **degrades in controlled, reversible steps** — throttle systems, slow the tick, then signal *you* to shed load — instead of falling behind unboundedly or crashing.

Every tick, the scheduler measures the overrun ratio (actual vs. budgeted tick time) and event-queue growth, feeding a single-writer state machine on the tick-driver thread. Sustained overrun **escalates** through levels; sustained headroom **de-escalates**. The hysteresis is deliberately asymmetric — escalate after ~5 overrun ticks, de-escalate only after ~20 — so load noise doesn't make it flap. The levels: `Normal` → `SystemThrottling` → `ScopeReduction` → `TickRateModulation` → `PlayerShedding`.

What each does: at `SystemThrottling`+, `Low`-priority [systems](xref:concept-system) marked `CanShed` stop running and `Normal` systems with a `ThrottledTickDivisor` run less often — `Critical` / `High` are **never** touched (protect core sim by priority). `TickRateModulation` slows the whole simulation in integer multiples (up to 6×) while keeping physics `dt` constant. `PlayerShedding` is a **signal, not an action**: the runtime fires `OnCriticalOverload` and *your* code decides who to drop — it never disconnects players itself.

> ⚠️ **Status: partial.** `ScopeReduction` currently applies the same rules as `SystemThrottling` (no distinct effect yet), and per-system `EntityBudget` / `DeferralMode` are defined but not enforced. Throttling, tick-rate modulation, and the shed callback are live.

## How it relates

- **[System](xref:concept-system)** — throttle/shed decisions key off a system's declared `Priority` / `CanShed` / `ThrottledTickDivisor`.
- **[Typhon runtime](xref:concept-runtime)** — owns the detector and fires `OnCriticalOverload`.
- **[Tick](xref:concept-tick)** — the unit measured; tick-rate modulation stretches it.
- **[Resources & budgets](xref:concept-resources)** — the *other* pressure signal: this detector watches tick overrun, not resource utilization.

## In the API

- [`OverloadOptions`](xref:Typhon.Engine.OverloadOptions) — detection thresholds ([`OverrunThreshold`](xref:Typhon.Engine.OverloadOptions.OverrunThreshold), [`EscalationTicks`](xref:Typhon.Engine.OverloadOptions.EscalationTicks), [`DeescalationTicks`](xref:Typhon.Engine.OverloadOptions.DeescalationTicks), [`MinTickRateHz`](xref:Typhon.Engine.OverloadOptions.MinTickRateHz)), set via [`RuntimeOptions`](xref:Typhon.Engine.RuntimeOptions).
- [`SystemBuilder.Priority`](xref:Typhon.Engine.SystemBuilder.Priority*) / [`.CanShed(bool)`](xref:Typhon.Engine.SystemBuilder.CanShed*) / [`.ThrottledTickDivisor(n)`](xref:Typhon.Engine.SystemBuilder.ThrottledTickDivisor*) — the per-system knobs.
- `TyphonRuntime.OnCriticalOverload` (event) and [`TyphonRuntime.CurrentOverloadLevel`](xref:Typhon.Engine.TyphonRuntime.CurrentOverloadLevel) ([`OverloadLevel`](xref:Typhon.Engine.OverloadLevel)).

## Learn & use

- **Feature detail:** [Overload management](xref:feature-runtime-overload-management)
- **Narrative:** [Guide ch.5 — systems](xref:guide-systems) · [ch.6 — operating](xref:guide-operating)
