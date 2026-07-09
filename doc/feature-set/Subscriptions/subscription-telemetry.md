---
uid: feature-subscriptions-subscription-telemetry
title: 'Subscription Telemetry & Tracing'
description: 'Per-tick counters for Output-phase cost and delta volume, plus a tracing span around the whole phase.'
---

# Subscription Telemetry & Tracing
> Per-tick counters for Output-phase cost and delta volume, plus a tracing span around the whole phase.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves
Subscription delta push runs inside the tick budget — if it gets slow or a client population starts overflowing send buffers, that needs to show up in telemetry without the application instrumenting it manually. Operators need three things: how long the Output phase took, how many entity deltas were actually pushed, and how often clients fell behind and had to resync. This feature exposes those numbers as part of Typhon's existing tick telemetry, and adds a trace span so the Output phase is visible alongside the rest of the tick in a profiler timeline.

## ⚙️ How it works (in brief)
Each tick, `TyphonRuntime` reads three counters off the internal `SubscriptionOutputPhase` (wall-clock duration, deltas pushed, overflow count) and writes them into that tick's `TickTelemetry` record in the `TickTelemetryRing`. Separately, the whole Output phase is wrapped in a `RuntimeSubscriptionOutputExecute` trace span (gated by `TelemetryConfig`, zero-cost when disabled) so it appears as a span in the runtime's tick trace. A deeper per-subscriber subtree — six more span kinds for per-subscriber dispatch, delta build, serialization, sync transitions, and cleanup — has its wire format and config-resolver leaves already shipped, but nothing in `SubscriptionOutputPhase` emits them yet, so today they produce no events.

## 💻 Usage
```csharp
// Per-tick counters — always populated, no configuration needed
var ring = runtime.Telemetry;
ref readonly var tick = ref ring.GetTick(ring.NewestTick);

Console.WriteLine($"output={tick.OutputPhaseMs}ms " +
                   $"pushed={tick.SubscriptionDeltasPushed} " +
                   $"overflows={tick.SubscriptionOverflowCount}");
```

```jsonc
// typhon.telemetry.json — opt into the Output-phase trace span
{
  "Typhon": {
    "Profiler": {
      "Enabled": true,
      "Runtime": {
        "Enabled": true,
        "Subscription": { "Output": { "Execute": { "Enabled": true } } }
      }
    }
  }
}
```

| Telemetry field | Source | Always available? |
|---|---|---|
| `TickTelemetry.OutputPhaseMs` | `TickTelemetryRing` (per tick) | Yes |
| `TickTelemetry.SubscriptionDeltasPushed` | `TickTelemetryRing` (per tick) | Yes |
| `TickTelemetry.SubscriptionOverflowCount` | `TickTelemetryRing` (per tick) | Yes |
| `RuntimeSubscriptionOutputExecute` span | Trace event (kind 164), gated by `Typhon:Profiler:Runtime:Subscription:Output:Execute:Enabled` | Opt-in |
| Per-subscriber dispatch spans (`Subscriber`, `Delta:Build`/`Serialize`/`DirtyBitmapSupplement`, `Transition:BeginSync`, `Output:Cleanup`) | Wire format + config leaves shipped | No producer wired — never emitted today |

## ⚠️ Guarantees & limits
- **Per-tick counters are unconditional** — `OutputPhaseMs`, `SubscriptionDeltasPushed`, and `SubscriptionOverflowCount` are recorded every tick regardless of profiler configuration; no opt-in needed to see them.
- **The Output-phase span exists but is off by default** — `RuntimeSubscriptionOutputExecute` requires explicit opt-in via `TelemetryConfig`/`typhon.telemetry.json`, same zero-cost-when-disabled model as the rest of the tracer.
- **The Output-phase span's stats fields are placeholders today** — the span's begin parameters (tick number, overload level) are live, but the per-tick stat fields it carries (client count, views refreshed, deltas pushed, overflow count) are not yet populated from `SubscriptionOutputPhase`.
- **No per-subscriber or per-View trace detail yet** — you cannot currently see which subscriber, View, or client a given delta/serialize cost belongs to; only the aggregate per-tick numbers above are observable. The six deeper span kinds exist in the wire protocol and config tree but have no producer wired into `SubscriptionOutputPhase`/`DeltaBuilder`, so enabling them today changes nothing.
- **`SubscriptionDeltasPushed` counts entities, not bytes** — it sums `Added + Modified + Removed` across all Views pushed this tick; it is not a measure of wire payload size.
- **Ring buffer retention is bounded** — `TickTelemetryRing` only retains the most recent `Capacity` ticks (default 1024, ~17s at 60Hz); read promptly if correlating with an external event.

## 🧪 Tests

- [TraceEventEncodeEquivalenceTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/TraceEventEncodeEquivalenceTests.cs)
  — `RuntimeSubscriptionOutputExecuteEvent_StructEncode_MatchesCodec`: binary encode of the Output-phase span,
  including its still-placeholder stat fields (client count, views refreshed, deltas pushed, overflow count)

## 🔗 Related
- Related feature: [Backpressure & Resync Recovery](./backpressure-resync.md), [Priority & Overload Throttling](./priority-overload-throttling.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Subscription Cost & Overload Integration -->
<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Profiling / Tracing Hooks -->
<!-- Design: claude/design/Profiler/07-tracing-instrumentation/09-subscription-dispatch.md -->
