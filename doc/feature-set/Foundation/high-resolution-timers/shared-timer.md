---
uid: feature-foundation-high-resolution-timers-shared-timer
title: 'Shared Timer (HighResolutionSharedTimerService)'
description: 'One thread multiplexing many periodic callbacks, each at its own rate, waking only when the soonest one is due.'
---

# Shared Timer (HighResolutionSharedTimerService)
> One thread multiplexing many periodic callbacks, each at its own rate, waking only when the soonest one is due.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [High-Resolution Timers](./README.md)

## 🎯 What it solves

A dedicated thread per periodic task doesn't scale: the deadline watchdog (200Hz), telemetry flush (10Hz), and epoch advancement (10-100Hz) are all lightweight, non-critical, and would otherwise each pay for an idle OS thread. `HighResolutionSharedTimerService` lets any number of independent periodic callbacks register on one thread, each keeping its own period, without one slow registration starving the others' accuracy under normal load.

## ⚙️ How it works (in brief)

Each `Register()` call adds a callback with its own interval to a copy-on-write registration list and lazily starts the shared thread on first use. Every cycle, the timer computes the nearest next-fire time across all active registrations — it wakes only when something is actually due, not on a fixed base tick — then fires every callback whose time has come, in sequence, on that one thread. Disposing the handle returned by `Register()` deactivates the callback; an inactive entry is lazily swept from the list. Because callbacks run sequentially, this is best-suited to fast handlers (target < 100µs) — see the contract below. The engine's own `DeadlineWatchdog` registers a 200Hz (5ms) callback on this timer.

## 💻 Usage

```csharp
// Engine-internal usage — application code does not register callbacks directly.
var shared = new HighResolutionSharedTimerService(
    parent: resourceRegistry.Timer,
    logger: loggerFactory.CreateLogger<HighResolutionSharedTimerService>());

// Telemetry flush every 100ms
var telemetryReg = shared.Register(
    "TelemetryFlush",
    intervalTicks: Stopwatch.Frequency / 10,
    callback: (scheduled, actual) => TelemetryManager.Flush());

// Epoch advancement every 10ms
var epochReg = shared.Register(
    "EpochAdvance",
    intervalTicks: Stopwatch.Frequency / 100,
    callback: (scheduled, actual) => epochManager.TryAdvance());

Console.WriteLine($"Active registrations: {shared.ActiveRegistrations}, mean error: {shared.MeanTimingErrorUs:F1}us");

telemetryReg.Dispose();  // unregisters; thread keeps running for the remaining callbacks
```

| Option | Default | Effect |
|---|---|---|
| `intervalTicks` (per `Register` call) | — (required, > 0) | This callback's own period, independent of every other registration |
| No active registrations | — | `GetNextTick()` returns "idle"; thread polls every 100ms instead of spinning |

## ⚠️ Guarantees & limits

- `internal sealed` engine type plus an `internal` `ITimerRegistration` handle — used by `DeadlineWatchdog` and engine housekeeping, not constructible/registerable from application code.
- Callback contract: target < 100µs, no blocking calls. A slow callback delays every other due callback in that cycle; invocations over 100µs are counted (`SlowInvocationCount`) per registration.
- Exceptions thrown by a callback are caught, logged, and do not stop the timer or other registrations.
- Wakes only for the nearest due registration — adding a slow 200ms task does not force a fast 5ms task's neighbors to tick faster, and vice versa.
- Registration/unregistration takes a lock and copies the registration array; intended for rare events (startup/shutdown), not a per-tick hot path.
- With the fastest registration at 5ms (the engine's deadline watchdog), the Sleep phase still engages on modern OSes, keeping the shared thread's CPU cost near zero.
- Per-registration metrics (`InvocationCount`, `LastCallbackDuration`, `MaxCallbackDuration`, `SlowInvocationCount`) are available on the `ITimerRegistration` handle; aggregate timer metrics (tick count, missed ticks, timing error) are available on the service itself and via `IMetricSource`.

## 🧪 Tests
- [HighResolutionSharedTimerServiceTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Concurrency/HighResolutionSharedTimerServiceTests.cs) — multiple independent registrations, dispose deactivates without stopping the thread, idles when nothing is due.
- [DeadlineWatchdogTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Concurrency/DeadlineWatchdogTests.cs) — the engine's own 200Hz consumer registered on this timer.

## 🔗 Related

- Parent feature: [High-Resolution Timers](./README.md)
- Sibling: [Dedicated Timer](./dedicated-timer.md)
- Sibling: [Deadline & Timeout Propagation](../deadline-timeout-propagation.md) — the engine's `DeadlineWatchdog` registers its 200Hz poll callback on this timer.

<!-- Deep dive: claude/design/Foundation/Concurrency/high-resolution-timer.md §7 -->
