---
uid: feature-runtime-telemetry-runtime-inspection
title: 'Telemetry & Runtime Inspection'
description: 'Always-on, zero-allocation per-tick telemetry your game code can read directly — no exporter required.'
---

# Telemetry & Runtime Inspection
> Always-on, zero-allocation per-tick telemetry your game code can read directly — no exporter required.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](./README.md)

## 🎯 What it solves
Game servers need to know *now*, from inside the process, whether the tick loop is healthy — how long the last tick took, which systems are eating the budget, why a system didn't run. Waiting on an external metrics pipeline is too slow for an admin command or an in-game debug overlay, and too heavyweight if all you want is "did tick N overrun?". This feature gives every tick's vitals to game code as plain structs, with no allocation and no opt-in required.

## ⚙️ How it works (in brief)
Every tick, the scheduler writes one `TickTelemetry` record plus one `SystemTelemetry` record per system into a pre-allocated circular buffer (`TickTelemetryRing`, default 1024 ticks ≈ 17s at 60Hz). The ring is single-writer (tick driver thread) and safe for concurrent reads — game code, an admin command, or a periodic logger can walk it from any thread. Only the most recent `Capacity` ticks are retained; reading a tick number outside that window throws. A second surface, `IRuntimeInspector`, is designed as a push-based hook (`OnTickStart`/`OnSystemComplete`/`OnTickEnd`) for future remote tooling (REST API, web explorer) — it is not implemented; `RuntimeOptions` has no `Inspector` property today.

## 💻 Usage
```csharp
var ring = runtime.Telemetry;

// Inspect the most recent tick
ref readonly var tick = ref ring.GetTick(ring.NewestTick);
if (tick.OverrunRatio > 1.2f)
{
    logger.LogWarning("Tick {N} overran: {Actual}ms / {Target}ms ({Level}, x{Mul})",
        tick.TickNumber, tick.ActualDurationMs, tick.TargetDurationMs,
        tick.CurrentLevel, tick.TickMultiplier);
}

// Per-system breakdown for that same tick
foreach (ref readonly var sys in ring.GetSystemMetrics(tick.TickNumber))
{
    if (sys.WasSkipped)
    {
        Console.WriteLine($"system {sys.SystemIndex} skipped: {sys.SkipReason}");
    }
}

// Walk recent history (oldest available -> newest)
for (var n = ring.OldestAvailableTick; n <= ring.NewestTick; n++)
{
    ref readonly var t = ref ring.GetTick(n);
    // aggregate, plot, export...
}
```

| Option | Default | Effect |
|--------|---------|--------|
| `RuntimeOptions.TelemetryRingCapacity` | 1024 | Ring size (must be a power of 2); trades retention window for memory (~`capacity × (32B + systemCount × 48B)`). |

## ⚠️ Guarantees & limits
- **Always on, zero allocation** — `Record` copies into pre-allocated slots; there is no opt-in flag and no per-tick GC pressure.
- **Bounded retention** — only the last `TelemetryRingCapacity` ticks are kept; `GetTick`/`GetSystemMetrics` throw `ArgumentOutOfRangeException` once a tick scrolls out of the window. Read promptly if correlating with an external event (e.g. a player report).
- **Single writer** — only the tick driver thread calls `Record`; readers get a consistent past-tick snapshot but must not assume the *current* in-flight tick's slot is stable.
- **`TickTelemetry.OverrunRatio` is base-rate, not throttle-adjusted** — it is always `actual / target-at-1x`, even while `TickMultiplier > 1`. Use it to ask "are we over the engine's nominal budget", not "are we over our currently throttled budget".
- **`SystemTelemetry.StragglerGapUs` is a placeholder today** — always `0` pending deeper Pipeline integration; don't rely on it for parallel-imbalance analysis yet.
- **No remote/out-of-process inspection** — `IRuntimeInspector` is designed but unimplemented; there is no REST endpoint or web explorer hook today. All access is in-process (game code, admin commands compiled into the server).
- **Reading is not free at scale** — `GetSystemMetrics` returns a span over a per-tick array sized to the full system count (engine-internal systems included); iterate only what you need on a hot path.

## 🧪 Tests

- [TickTelemetryRingTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/TickTelemetryRingTests.cs) — record/read round-trip, ring-wrap eviction of the oldest tick, out-of-window `GetTick` throws

## 🔗 Related
- Related feature: [Subscription Telemetry & Tracing](../Subscriptions/subscription-telemetry.md)

<!-- Deep dive: claude/design/Runtime/03-overload.md — Tick Telemetry & Runtime Inspection -->
<!-- Deep dive: claude/overview/13-runtime.md — Telemetry -->
<!-- Deep dive: claude/overview/13-runtime.md — Per-tick diagnostics -->
