# Overload Management
> Single-writer state machine that throttles systems and slows the tick rate so a load spike degrades instead of crashing.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](./README.md)

## 🎯 What it solves
Game servers face sudden, unpredictable load spikes — a crowd converges on one point, an explosion destroys thousands of entities, a bot swarm hits the AI pipeline. Without a built-in response, an overloaded tick loop either falls further and further behind (unbounded latency) or the process falls over. Overload Management gives the Runtime a standing policy for "what to do when a tick takes too long" so the server degrades in a controlled, reversible way instead of crashing or freezing.

## ⚙️ How it works (in brief)
Every tick, the scheduler measures the overrun ratio (actual tick duration vs. the base-rate budget) and event-queue growth, and feeds both into a single-writer state machine (`OverloadDetector`, running on the tick driver thread). Sustained overrun escalates through levels; sustained headroom de-escalates. Escalation is fast (5 consecutive overrun ticks by default), de-escalation is slow (20 consecutive under-run ticks) — this asymmetric hysteresis prevents oscillation. At `SystemThrottling` and above, low-priority systems marked shed-able stop running and normal-priority systems with a throttle divisor run less often; `Critical`/`High`-priority systems are never touched. If overrun persists, the Runtime slows the whole simulation in integer multiples of the base tick (`TickRateModulation`, up to 6x) so physics `dt` stays constant. As a last resort it fires a callback so game code can shed load itself (e.g. disconnect spectators); the Runtime never disconnects players on its own.

## 💻 Usage
```csharp
protected override void Configure(SystemBuilder b) => b
    .Name("AmbientFx")
    .Priority(SystemPriority.Low)
    .CanShed(true);                 // disabled entirely once overload hits SystemThrottling+

protected override void Configure(SystemBuilder b) => b
    .Name("AI")
    .Priority(SystemPriority.Normal)
    .ThrottledTickDivisor(3);       // runs every 3rd tick under overload instead of every tick

// Physics stays Priority.Critical (default throttle/shed knobs left untouched) — never shed or skipped.

// Tune detection thresholds and the tick-rate floor at runtime creation:
using var runtime = TyphonRuntime.Create(dbe, schedule => { /* ... */ }, new RuntimeOptions
{
    Overload = new OverloadOptions
    {
        OverrunThreshold = 1.2f,    // tick must run 20% over budget to count as "overrunning"
        EscalationTicks = 5,        // consecutive overrunning ticks before escalating
        DeescalationTicks = 20,     // consecutive headroom ticks before de-escalating
        MinTickRateHz = 10,         // floor for TickRateModulation (60Hz base -> up to 6x)
    },
});

// Last resort: game code decides who/what to shed.
runtime.OnCriticalOverload += rt =>
{
    // e.g. disconnect spectators, migrate players, split the zone
};

// Inspect current state from anywhere in game code:
var level = runtime.CurrentOverloadLevel; // OverloadLevel.Normal / SystemThrottling / ScopeReduction / TickRateModulation / PlayerShedding
```

| Option | Default | Effect |
|--------|---------|--------|
| `OverloadOptions.OverrunThreshold` | 1.2 | Ratio (actual/target) above which a tick counts as overrunning |
| `OverloadOptions.DeescalationRatio` | 0.6 | Ratio below which a tick counts toward de-escalation |
| `OverloadOptions.EscalationTicks` | 5 | Consecutive overrunning ticks required to escalate |
| `OverloadOptions.DeescalationTicks` | 20 | Consecutive headroom ticks required to de-escalate |
| `OverloadOptions.MinTickRateHz` | 10 | Hard floor for the modulated tick rate (caps the multiplier ladder) |
| `OverloadOptions.QueueGrowthTicks` | 5 | Consecutive ticks of growing event-queue depth that also counts as an escalation signal (0 disables) |
| `SystemBuilder.Priority` | `Normal` | `Critical`/`High` never throttled or shed; `Normal` throttled via divisor; `Low` shed if `CanShed` |
| `SystemBuilder.CanShed` | `false` | Whether a `Low`-priority system is disabled entirely once any overload level is active |
| `SystemBuilder.ThrottledTickDivisor` | 1 | Run-every-Nth-tick divisor applied to `Normal`-priority systems once any overload level is active |

## ⚠️ Guarantees & limits
- **Reversible and asymmetric** — every level can de-escalate; escalation reacts in ~5 ticks, de-escalation waits ~20, by design (prevents flapping under noisy load).
- **`Critical`/`High`-priority systems are never throttled or shed** by this mechanism, at any overload level — protect your core simulation systems (physics, combat) by priority, not by hoping the detector spares them.
- **System throttling/shedding is level-agnostic today** — `SystemThrottling`, `ScopeReduction`, and `TickRateModulation` all apply the same `CanShed`/`ThrottledTickDivisor` rules; there is no additional effect specific to `ScopeReduction` yet.
- **Per-system entity budgets (`EntityBudget`, `DeferralMode`) are defined but not enforced** — the types exist for the planned Level 2 scope-reduction feature; setting them today has no runtime effect.
- **Tick-rate modulation is integer-multiple only** (1/2/3/4/6x, capped by `MinTickRateHz`) — physics `dt` per step stays constant, avoiding floating-point drift; clients must be told the active multiplier separately (this feature does not push it to clients).
- **`OverrunThreshold`/`DeescalationRatio` are evaluated against the base-rate (1x) budget**, even while running under a higher multiplier — see the Telemetry feature for how `OverrunRatio` is computed.
- **Player shedding is a signal, not an action** — `TyphonRuntime.OnCriticalOverload` fires once on entering `PlayerShedding`; the Runtime takes no action on its own, all mitigation is game-defined.
- **Single-writer state machine** — `OverloadDetector.Update` runs only on the tick driver thread once per tick; no synchronization is needed and none should be added around it.

## 🧪 Tests

- [OverloadTests](../../../test/Typhon.Engine.Tests/Runtime/OverloadTests.cs) — `OverloadDetectorTests` (pure state-machine escalation/de-escalation hysteresis, tick-rate multiplier ladder) and `OverloadThrottleTests` (`ThrottledTickDivisor`, `CanShed`, telemetry recording)

## 🔗 Related
- Related feature: [Telemetry & Runtime Inspection](./telemetry-runtime-inspection.md)
- Related feature: [Declarative System Scheduling](./declarative-system-scheduling.md)
- Sibling: [Subscription Priority & Overload Throttling](../Subscriptions/priority-overload-throttling.md) — how the same overload signal reprioritizes View delivery to players.

<!-- Deep dive: claude/design/Runtime/03-overload.md -->
<!-- Deep dive: claude/overview/13-runtime.md — Overrun handling -->
<!-- Deep dive: claude/overview/13-runtime.md — Overload Management -->
