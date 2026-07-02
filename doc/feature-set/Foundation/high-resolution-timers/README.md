# High-Resolution Timers
> Self-calibrating sub-millisecond periodic timers (Sleep→Yield→Spin) powering the deadline watchdog, telemetry flush, and epoch advancement.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Foundation](../README.md)

## 🎯 What it solves

Several engine subsystems need periodic execution well below `Thread.Sleep`'s nominal granularity — a deadline watchdog scanning for expirations every 5ms, telemetry flushing every 100ms, epoch advancement at 10ms. `Thread.Sleep(1)` actually resolves anywhere from ~0.5ms (modern Windows/Linux) to ~15.6ms (older Windows), and that gap varies by platform and OS version — a naive timer either misses its window badly on some machines or burns a full core spinning on all of them. High-resolution timers give every periodic subsystem a shared, drift-free metronome instead of each one reinventing wait logic.

## ⚙️ How it works (in brief)

A three-phase wait (`Thread.Sleep(1)` while far from the target → `Thread.Yield()` as it nears → `Thread.SpinWait` for the final ~50µs) trades CPU for precision only when precision is actually needed. The split point is calibrated once at startup by measuring this machine's actual `Thread.Sleep(1)` cost, so the same code self-tunes across Windows, Linux, and macOS without configuration. Each tick advances from the *scheduled* time, not the actual wake time, so timing error never compounds across ticks (metronome scheduling, not a chain of relative delays). Two scheduling policies share this wait loop — see the sub-features below — and both surface tick count, missed ticks, and mean/max timing-error-in-microseconds through the engine's resource graph.

## Sub-features

| Sub-feature | Thread model | Use it when... |
|---|---|---|
| [Dedicated Timer](./dedicated-timer.md) | One thread per timer | A single handler needs guaranteed isolation from every other periodic task (e.g. a heartbeat) |
| [Shared Timer](./shared-timer.md) | One thread, many callbacks | Several lightweight periodic tasks (deadline watchdog, telemetry, epoch advance) shouldn't each pay for a dedicated thread |

## ⚠️ Guarantees & limits

- Both timer flavors are `internal` engine plumbing — application code never constructs one directly; they're the infrastructure behind `DeadlineWatchdog`, telemetry flush, and epoch advancement.
- Timing error is sub-50µs on a lightly loaded system regardless of interval length — it's dominated by the Yield→Spin handoff, not by how long the wait is.
- Sub-millisecond intervals keep a logical core busy in the Yield+Spin phases (the Sleep phase has no room to engage); intervals ≥5ms on modern OSes let the Sleep phase absorb most of the wait, dropping CPU usage to near zero.
- A callback that throws is caught, logged, and skipped — one bad handler never kills the timer thread.
- Per-tick and per-callback metrics (tick count, missed ticks, mean/max timing error, invocation counts) are exposed through `IMetricSource`, so they appear in resource graph snapshots and any OTel bridge built on it — no separate instrumentation needed.

## 🧪 Tests
- [HighResolutionTimerServiceBaseTests](../../../../test/Typhon.Engine.Tests/Concurrency/HighResolutionTimerServiceBaseTests.cs) — shared wait-loop mechanics common to both flavors: thread lifecycle, calibration, dispose/double-dispose.
- [HighResolutionTimerServiceTests](../../../../test/Typhon.Engine.Tests/Concurrency/HighResolutionTimerServiceTests.cs) — dedicated timer: fire rate, callback timestamps, drift-free metronome scheduling.
- [HighResolutionSharedTimerServiceTests](../../../../test/Typhon.Engine.Tests/Concurrency/HighResolutionSharedTimerServiceTests.cs) — shared timer: multiple independent registrations, idle-when-empty, per-registration disposal.

## 🔗 Related

- Related features: [Deadline & Timeout Propagation](../deadline-timeout-propagation.md), [Resource Tree Registry](../../Resources/resource-tree-registry.md), [Metric Reporting](../../Resources/metric-reporting.md)
- Sub-features: [Dedicated Timer](./dedicated-timer.md), [Shared Timer](./shared-timer.md)

<!-- Deep dive: claude/design/Foundation/Concurrency/high-resolution-timer.md -->
<!-- Overview: claude/overview/01-concurrency.md -->
