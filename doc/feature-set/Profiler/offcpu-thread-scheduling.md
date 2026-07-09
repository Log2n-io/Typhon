---
uid: feature-profiler-offcpu-thread-scheduling
title: 'Off-CPU Thread Scheduling Capture (Windows)'
description: 'See exactly when and why each engine thread lost the CPU, down to the kernel wait reason.'
---

# Off-CPU Thread Scheduling Capture (Windows)
> See exactly when and why each engine thread lost the CPU, down to the kernel wait reason.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Profiler](./README.md)

## 🎯 What it solves

Span timing tells you how long an operation took, but not whether the thread was actually
running for that whole span or sitting off-CPU — blocked on a lock, waiting on I/O, or simply
preempted by the OS scheduler. Without that distinction, a slow span and a starved thread look
identical on the timeline. This feature fills the gap: it records every point where a
Typhon-registered OS thread was switched out, how long it had held the CPU, and the kernel's
own reason for the switch — so the Workbench viewer can render the off-CPU gaps between
on-CPU slices instead of leaving them as unexplained dead time.

## ⚙️ How it works (in brief)

A dedicated pump opens the Windows NT Kernel Logger ETW session and subscribes to kernel
context-switch and dispatcher-ready events for the whole machine, filtering to only the
threads Typhon itself has registered. Each time a tracked thread is switched off the CPU, the
pump closes out that on-CPU slice and emits one record carrying the slice duration, the CPU it
ran on, the kernel wait reason, and how long the thread had waited on the ready queue before it
last got scheduled in. This runs on its own background thread, independent of the engine's
worker threads, and produces the raw slice data — the Workbench renders it as the off-CPU gaps
alongside the existing span timeline.

## 💻 Usage

Nothing to call — flip the config flag and the engine wires up the ETW pump for you:

```csharp
// typhon.telemetry.json next to your executable:
// {
//   "Typhon": {
//     "Telemetry": { "Enabled": true },
//     "Profiler": {
//       "Enabled": true,
//       "Runtime": { "ThreadScheduling": { "Enabled": true } }
//     }
//   }
// }

using Typhon.Engine;

var runtime = TyphonRuntime.Create(dbe, schedule =>   // ProfilerBootstrap starts EtwSchedulingPump, if enabled
{
    schedule.PublicTrack.DeclareDag("Sim").CallbackSystem("Tick", ctx => RunGameLogic(ctx));
});

// …run your workload (must run elevated — see Guarantees & limits)…

TyphonProfiler.Stop();   // optional; ordinary shutdown stops the ETW session and joins the pump thread too
```

| Option | Default | Effect |
|---|---|---|
| `Typhon:Profiler:Runtime:ThreadScheduling:Enabled` | `false` | Opens the NT Kernel Logger session and emits one `ThreadContextSwitch` record per closed on-CPU slice |

## ⚠️ Guarantees & limits

- **Windows only.** The capture is built on the NT Kernel Logger; on non-Windows hosts the pump
  skips itself and logs one diagnostic line — the rest of the profiler is unaffected.
- **Requires elevation.** The process must run as Administrator or as a member of
  `Performance Log Users`. Without it, `Start` fails gracefully (one stderr line), and the trace
  continues without scheduling data.
- **Machine-wide singleton.** The NT Kernel Logger session can only have one owner per machine.
  If another tool (PerfView, WPR, xperf) already holds it, the pump fails to start the same way
  — gracefully, with a diagnostic, never aborting the trace.
- **Scoped to Typhon's own threads.** Only OS threads Typhon has registered are recorded; pool,
  finalizer, GC-server threads, other processes, and the pump's own thread are filtered out.
- **Off by default, opt-in, and independent.** Gated by its own config key; costs nothing when
  disabled and can be enabled without turning on any other profiler sub-feature.
- **Precise, not statistical.** Every on-CPU slice for a tracked thread is captured exactly from
  kernel events — this is not sampling, so short slices are not missed the way a fixed-rate
  sampler would miss them.
- **Complements, does not replace, CPU sampling.** This gives exact off-CPU *timing* and wait
  reason with no call stack; the separate CPU-sampling feature gives a statistical call stack at
  the cost of a fixed sampling cadence. Enabling CPU sampling enables this capture by default
  where supported, so the on-CPU/off-CPU classification in the Workbench has both signals.
- **Timestamps cross-walk directly.** The kernel's QPC clock is the same clock
  `Stopwatch.GetTimestamp()` uses on Windows, so slice timestamps need no conversion to line up
  with the rest of the trace.

## 🧪 Tests

- [EtwSchedulingPumpTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/EtwSchedulingPumpTests.cs) — `IsStaleEntry` prune-decision helper (regression test for an unbounded `_threadStates` growth bug); the ETW session itself isn't unit-testable and is exercised only in practice
- [ThreadSchedulingTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/ThreadSchedulingTests.cs) — `ThreadWaitReason` wire-stability and `ThreadContextSwitchEventDto` encode/decode round-trip

## 🔗 Related

- Sibling features: [Configuration & Performance Tuning](./profiler-configuration-tuning.md), [Typed-Event Capture Pipeline](./typed-event-capture-pipeline.md), [Built-in Engine Instrumentation Catalog](./builtin-subsystem-instrumentation.md)
- Source: `src/Typhon.Engine/Profiler/internals/EtwSchedulingPump.cs`, `src/Typhon.Engine/Profiler/internals/ThreadSchedulingEvents.cs`, `src/Typhon.Profiler/ThreadWaitReason.cs`

<!-- Deep dive: claude/design/Profiler/typhon-profiler.md §6.8, claude/design/Profiler/11-cpu-sampling-integration.md §2.1, §8.7 -->
<!-- Architecture overview: claude/overview/09-observability.md -->
