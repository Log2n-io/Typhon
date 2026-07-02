# Dedicated Timer (HighResolutionTimerService)
> A single periodic callback on its own thread, isolated from every other timer in the engine.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [High-Resolution Timers](./README.md)

## 🎯 What it solves

Some periodic handlers can't tolerate sharing a thread: if a slow callback elsewhere in the process delays the tick, a safety-critical check (a heartbeat, a hard real-time gate) is delayed with it. `HighResolutionTimerService` dedicates a full thread and the engine's calibrated three-phase wait loop to exactly one callback at a fixed interval, so its timing is never a function of what else is registered elsewhere.

## ⚙️ How it works (in brief)

Constructing a `HighResolutionTimerService` does not start it — the thread starts on an explicit `Start()` call. From then on, the timer maintains a metronome anchor (`nextTick += intervalTicks` after every fire, never recomputed from "now") and invokes the single callback once per tick with the scheduled and actual `Stopwatch` timestamps. If the thread falls behind by more than one interval, it skips forward rather than bursting through the backlog, and counts the skipped ticks as missed. `Dispose()` stops the thread (joined with a 2-second timeout) and removes the timer from the resource tree.

## 💻 Usage

```csharp
// Engine-internal usage — application code does not construct this directly.
var heartbeat = new HighResolutionTimerService(
    name: "HeartbeatMonitor",
    intervalTicks: Stopwatch.Frequency / 1000,  // 1ms
    callback: (scheduled, actual) => HeartbeatMonitor.CheckAlive(),
    parent: resourceRegistry.TimerDedicated,
    logger: loggerFactory.CreateLogger<HighResolutionTimerService>());

heartbeat.Start();   // thread starts here; already visible in the resource tree

// Inspect timing/invocation metrics directly or via the resource graph
Console.WriteLine($"Timing error: {heartbeat.MeanTimingErrorUs:F1}us, invocations: {heartbeat.InvocationCount}");

heartbeat.Dispose(); // stops the thread, removes from the resource tree
```

| Option | Default | Effect |
|---|---|---|
| `intervalTicks` | — (required, > 0) | Period between invocations, in `Stopwatch` ticks; `Stopwatch.Frequency / 1000` ≈ 1ms |
| `parent` | — (required) | Resource-tree parent; dedicated timers register under `registry.TimerDedicated` |

## ⚠️ Guarantees & limits

- Full thread isolation — no other timer or callback can delay this one's tick.
- `internal sealed` engine type — used by subsystems that own their own dedicated timer (e.g. a heartbeat monitor), not constructible from application code.
- Callback budget is a soft contract (target < 100µs); a slow callback delays only this timer's own subsequent ticks, never another timer's.
- Exceptions thrown by the callback are caught, logged, and do not stop the timer.
- 1ms intervals keep one logical core near-fully busy (Yield+Spin, no room for the Sleep phase); 5ms+ intervals let Sleep absorb most of the wait, dropping CPU to near zero.
- One OS thread (default 1MB stack) per instance — appropriate for a handful of isolated timers, not for dozens of lightweight periodic tasks (use the shared timer for those).
- `MeanTimingErrorUs`/`MaxTimingErrorUs`/`InvocationCount`/`MissedTicks` are available both as direct properties and through `IMetricSource` resource-graph snapshots.

## 🧪 Tests
- [HighResolutionTimerServiceTests](../../../../test/Typhon.Engine.Tests/Concurrency/HighResolutionTimerServiceTests.cs) — fire-rate accuracy, callback timestamp delivery, exception-in-callback doesn't kill the timer, per-tick/callback metrics.
- [HighResolutionTimerServiceBaseTests](../../../../test/Typhon.Engine.Tests/Concurrency/HighResolutionTimerServiceBaseTests.cs) — `Start()`/`Dispose()` thread lifecycle, calibration, invalid-interval/null-callback validation.

## 🔗 Related

- Parent feature: [High-Resolution Timers](./README.md)
- Sibling: [Shared Timer](./shared-timer.md)

<!-- Deep dive: claude/design/Foundation/Concurrency/high-resolution-timer.md §6 -->
