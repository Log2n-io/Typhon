---
uid: feature-subscriptions-sessions-admission
title: 'Sessions & Admission'
description: 'Declared session kinds, an admission hook that accepts with a role and limits or rejects with a code, Opened/Closed events in the tick, and staged requests: profile, control, budget, kick.'
---

# Sessions & Admission
> Who may connect, as what, and how the application steers each session from its systems.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

A connection is not yet a player: it has to be authenticated, given a role, bound to what it may see and, for a player, attached to the
entity it controls — and all of that has to happen without the network thread touching the simulation.

## ⚙️ How it works (in brief)

- **`HELLO`** names a **kind** and carries a token and an optional payload. An undeclared kind is refused by the engine (4003). For the
  others, the application's `Admit` hook — on the connection's thread — returns `Admission.Accept(role, limits, appData)` or
  `Admission.Reject(code, reason)` (codes 4100–4999). No hook: every declared kind is accepted as a `Spectator`.
- **Roles**: `Spectator` (watches) and `Player` (controls an entity, sends player commands).
- **Session events** — `Opened`, `Closed` (with a `SessionCloseReason` and close code) — are delivered in the next tick's Engine-Pre, so a
  system reacts with an ordinary transaction. A session that opens and closes in one tick produces both, in order.
- **Requests** through `ctx.Subscriptions.Session(id)`: `.Profile(name)` (a session with none receives nothing), `.Control(entity)`,
  `.SetBudget(bytesPerSecond)`, `.Kick(code, reason)`. They are staged per worker and applied by the next tick's prologue, so any system
  may make them. `Place(session, point)` and `SetRadius(session, r)` apply now.
- **Closing** is announced with a `KICK` carrying the code and reason, sent by the session's send pump after whatever it was writing.

## 💻 Usage

```csharp
subs.Sessions.Kinds("player", "god");
subs.Sessions.Admit = static (in AdmissionRequest r) => r.Kind == "player"
    ? Admission.Accept(SessionRole.Player, new SessionLimits { BytesPerSecond = 64 * 1024 })
    : Admission.Accept(SessionRole.Spectator, SessionLimits.God);

// In a system:
foreach (ref readonly var e in ctx.Subscriptions.SessionEvents)
{
    if (e.Kind == SessionEventKind.Opened && e.SessionKind == "player")
    {
        var avatar = SpawnAvatar(ctx.Transaction);
        ctx.Subscriptions.Session(e.Session).Profile("player").Control(avatar);
    }
}
```

| Setting | Default | Effect |
|---|---|---|
| `SubscriptionsOptions.MaxSessions` | 8 192 | Session table size (hard max 65 535); a full table refuses with 1013 |
| `SessionLimits` (`BytesPerSecond`, `MaxObservers`, `FrameBytes`, `ClientMessageBytes`, `AllowDebug`) | operator defaults | Per-session limits, resolved against the operator's values — never above them |
| `HELLO` deadline | 5 s | A connection that sends no `HELLO` is closed with 4002 before a session exists |

## ⚠️ Guarantees & limits

- **One writer per session row** — the tick; transport threads touch only rings, leases and counters (SUB-05).
- **A session the tick closes is told why before its link closes** (SUB-14).
- **A session costs a few hundred bytes of geometry plus its frames and a 4 KiB inbound ring**, whatever it holds.
- Neither transport authenticates: the token reaches `Admit` unexamined. **Resume is not built** (`resumeToken` is 0): a reconnect is a new
  session.

## 🧪 Tests

- [HandshakeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/HandshakeTests.cs) — admission, the deadline, caps, close codes
- [SessionChurnLeakTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SessionChurnLeakTests.cs) — 10 000 connect/disconnect cycles leak nothing

## 🔗 Related

- Concept: [Replication session](xref:concept-replication-session)
- [Owner state](owner-state.md) · [Backpressure & budgets](backpressure-budgets.md)
