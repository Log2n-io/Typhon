---
uid: feature-profiler-lock-contention-diagnostics
title: 'Lock-Contention Forensics (Deep Diagnostics)'
description: 'Post-mortem visibility into which threads waited on which locks, for how long, and why.'
---

# Lock-Contention Forensics (Deep Diagnostics)
> Post-mortem visibility into which threads waited on which locks, for how long, and why.

**Status:** 📋 Planned · **Visibility:** Internal · **Category:** [Profiler](./README.md)

## 🎯 What it solves

When a stress test shows an unexplained latency spike or an occasional transaction timeout under load,
span timing alone can't tell you whether a thread was doing work or sitting blocked on a lock. Aggregate
contention counters ("47 contention events averaging 340µs") lose the per-event detail needed to actually
fix it: which lock, which two threads, what the holder was doing, and whether it's a recurring pattern.
This feature is designed to answer "why did thread 7 wait 2.3ms for thread 3, and does it keep happening?"
without attaching an external profiler.

## ⚙️ How it works (in brief)

The design captures contention only — not every lock acquisition — so the fast, uncontended path (the vast
majority of lock operations) keeps its current zero-overhead behavior. A capture point fires only when a
thread actually has to wait: it would record the waiter and holder thread IDs, the lock's identity, a call
stack at the wait point, and the resulting wait duration, then surface each event as a span so it can appear
inline in a flame graph (nested under whatever transaction triggered it) or as its own trace when contention
happens outside a transaction.

## 💻 Usage

Not usable yet — no engine emission exists. The roadmap reserves wire event-kind range 70-74
(`LockAcquire` / `LockRelease` / `LockWaitBegin` / `LockWaitEnd`) for this feature; none of these event
kinds, and no contention-capture hook on `AccessControl`, `AccessControlSmall`, or `ResourceAccessControl`,
are implemented today.

```csharp
// Illustrative only — design complete, not implemented yet. No "Typhon:Profiler:LockContention" config
// key, no TyphonEvent factory, and no contention-capture hook exist in source today.
// var runtime = TyphonRuntime.Create(dbe, schedule => { /* ... */ });
// // ...contention on AccessControl/AccessControlSmall/ResourceAccessControl would emit
// // LockWaitBegin/LockWaitEnd spans automatically, no call-site changes required...
```

| Option | Default | Effect |
|---|---|---|
| `Typhon:Profiler:LockContention:Enabled` (proposed) | `false` | Not implemented — no such config key exists today |

## ⚠️ Guarantees & limits

- **Not implemented.** No wire event kinds, capture hooks, or config surface exist in
  `src/Typhon.Engine/Foundation/Concurrency/internals/` today; this is a complete design awaiting a build slot.
- **Contention-only by design** — capturing every acquire/release was estimated at 1M+ events/sec (1.8B over
  30 minutes); capturing only actual waits cuts that by 100-10,000x while keeping exactly what's useful for
  debugging.
- **Zero overhead on the fast path** — the design only does work (stack capture, event record) once a thread
  has already determined it must wait; threads that acquire immediately pay nothing extra.
- **Holder identification is exact for exclusive waits** — `AccessControl`/`AccessControlSmall` already track
  the current exclusive holder's thread ID as part of their atomic lock state, so the design can read it with
  no extra bookkeeping. For shared-lock contention there is no single holder to name (multiple threads can
  hold a shared lock at once); the design records it as "waiting on shared readers" rather than a thread ID.
- **Would use the 16-bit thread-ID convention** already shared by `AccessControl`, `AccessControlSmall`, and
  `ResourceAccessControl` — no new thread-identity mechanism.
- **Scoped to three lock types**: `AccessControl`, `AccessControlSmall`, `ResourceAccessControl`. General
  span tracing, memory tracing, and I/O tracing are separate features, not part of this one.
- **Reserved wire range 70-74** is held for this feature and won't be reused by other event kinds in the
  meantime.

## 🔗 Related

- Sibling features: [GC Event Tracing](./gc-event-tracing.md), [Off-CPU Thread Scheduling Capture](./offcpu-thread-scheduling.md), [Configuration & Performance Tuning](./profiler-configuration-tuning.md)
- Sibling: [Reader-Writer & Resource Lifecycle Locks](../Foundation/access-control-lock-family/README.md) — the `AccessControl`/`AccessControlSmall`/`ResourceAccessControl` lock family this feature would instrument
- Source (existing, related): `src/Typhon.Engine/Foundation/Concurrency/internals/AccessControl.cs`, `AccessControlSmall.cs`, `ResourceAccessControl.cs` — no contention-capture code present yet

<!-- Deep dive: claude/design/Profiler/03-deep-diagnostics.md, claude/design/Profiler/06-profiler-feature-roadmap.md §2.3 -->
<!-- Architecture overview: claude/overview/09-observability.md, claude/overview/01-concurrency.md -->
