---
uid: feature-subscriptions-backpressure
title: 'Backpressure & Budgets'
description: 'Skip, never queue: a slow session''s next frame carries the union of what it missed; rate classes, per-session byte budgets and detail levels degrade before closing; inbound budgets and the abuse rule bound what clients send.'
---

# Backpressure & Budgets
> A slow or hostile client costs itself, never the tick or the other sessions.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

Clients are slow, lossy, far away or malicious. A server that queues frames for a slow client grows without bound; one that waits for it
stalls everybody; one that accepts whatever a client sends can be driven off its tick.

## ⚙️ How it works (in brief)

- **Skip, never queue.** A session has two frame slots (one in flight, one ready). With both busy it is **skipped**: nothing is gathered
  for it and nothing about it moves. Its next frame carries the union of everything it missed, replayed from an **8-tick push log**; past
  it, a `RESET` re-delivers its view.
- **Rate classes.** A session that keeps being skipped is served every 2nd, then every 4th tick, then closed with **1013** at
  `CloseStalledAfter`. A profile can also be served every 2 or 4 ticks by declaration (`Every`).
- **Acknowledgement-based lag.** A send completing is not proof of delivery; clients report their last applied tick in `PING` (4 Hz), and
  a session too far behind is skipped. A client that stops pinging is closed with **4001**.
- **Per-session byte budget** (`Session(id).SetBudget(bytesPerSecond)` or `SessionLimits.BytesPerSecond`): the engine raises the session's
  detail level — longer periods for far bands, a smaller enter budget — and, at the last level, shrinks its radius; it never drops state.
- **Overload.** When the runtime is overloaded, every Sphere session is served a level lower and every profile half as often.
- **Inbound.** Each session's inbound byte budget (`IngressBytesPerSecond`, required with commands) refuses over-budget `COMMANDS` whole;
  refused commands past `AbuseRefusalsPerWindow` for `AbuseWindows` adjacent windows close the session with **1008**.
- **Pools.** Frames and per-entity state live in native pools under `FramePoolBudgetBytes` and `StatePoolBudgetBytes`; an exhausted frame
  pool skips sessions (and they catch up), never allocates.

| Option | Default | Effect |
|---|---|---|
| `CloseStalledAfter` | 3 s | Skip run after which a session is closed with 1013 (degraded first) |
| `LagSkipRttAllowanceMs` | 250 | RTT allowance of the acknowledgement-based lag skip |
| `FrameBytes` | 256 KiB | Largest frame a session may be sent |
| `FramePoolBudgetBytes` / `StatePoolBudgetBytes` | 256 MiB / 256 MiB | Native pool ceilings |
| `IngressPoolBudgetBytes` | 64 MiB | Inbound rings, all sessions |

## ⚠️ Guarantees & limits

- **A slower session loses rate, never state**; records are absolute and skips are unions (SUB-03).
- **An idle tick is not a skip** (SUB-15).
- **An exhausted state pool** leaves a pushed cluster unprojected for that tick — a policy for it is not built yet.
- **Connection churn is not rate-limited by the engine.** Measured on the SWG demo, ~1 700 connect/close cycles a second from one host raise
  the tick's p99 severalfold; put connection-rate limiting in front of a public endpoint.

## 🧪 Tests

- [SendPumpTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SendPumpTests.cs) — skip, degrade, close
- [PushLodLevelTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushLodLevelTests.cs) — detail levels under a budget
- [IngressHardeningTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/IngressHardeningTests.cs) — inbound budget and abuse

## 🔗 Related

- Related feature: [Overload Management](../Runtime/overload-management.md)
- [Commands & acknowledgements](commands.md)
