---
uid: concept-replication-session
title: 'Replication session'
description: 'One connected client: admitted by the application, bound to a profile, placed in the world, possibly controlling an entity, and served one frame per tick until it leaves.'
---

# Replication session

> **In one line:** one connected client of a [subscription](xref:concept-subscription) — admitted by your code, bound to a
> [profile](xref:concept-replication-profile), placed in the world, and served one frame per tick until it leaves.

A session begins when a client sends `HELLO` naming a **kind** (`"player"`, `"god"`, a tool…) and a token. The engine refuses a kind the
application did not declare; for the rest it calls the application's **admission** hook, which accepts — with a **role**
(`Spectator` or `Player`), limits and any application data — or rejects with a close code. An accepted session appears to systems as a
`SessionEvent` of kind `Opened` in the next tick.

**A session receives nothing until it is bound to a profile.** The usual place is the `Opened` event: `ctx.Subscriptions.Session(id)
.Profile("player")`. Requests on a session — a profile, the entity it **controls**, an outbound byte budget, a kick — are staged and
applied at the start of the next tick, so any system on any worker may make them. A `Sphere` session also needs a centre: the application
places it every tick with `Place`, or the profile follows an entity (`Bind`) or the session's controlled entity (`AroundControlled`).

**Control is ownership.** The entity a session controls receives its **owner fields** — data only that client sees (exact health, a
wallet, a quest marker) — in the frame's `SELF` block, together with the sequence number of the last command the tick applied. That is
what a predicting client reconciles against.

**A session is cheap and never queued.** It costs a few hundred bytes of geometry plus its frames: nothing is stored per
(session, entity). A session that cannot keep up is **skipped**, not buffered — its next frame carries the union of what it missed —
then served less often, then closed (`1013`). A client that stops acknowledging is closed with `4001`; one that keeps sending refused
commands, with `1008`. Every close is announced with a `KICK` carrying the reason, and appears to systems as a `Closed` event.

## How it relates

- **[Replication profile](xref:concept-replication-profile)** — what the session sees.
- **[Client command](xref:concept-client-command)** — what the session sends; its role decides which commands it may.
- **[Projection](xref:concept-projection)** — owner fields reach only the session controlling the entity.

## In the API

- [`SubscriptionsSessions`](xref:Typhon.Engine.SubscriptionsSessions) — `Kinds(…)` and the `Admit` hook ([`Admission`](xref:Typhon.Engine.Admission),
  [`SessionRole`](xref:Typhon.Engine.SessionRole), [`SessionLimits`](xref:Typhon.Engine.SessionLimits)).
- [`SessionEvent`](xref:Typhon.Engine.SessionEvent) — `Opened` / `Closed`, read from `ctx.Subscriptions.SessionEvents`.
- [`SessionRequest`](xref:Typhon.Engine.SessionRequest) — `Profile`, `Control`, `SetBudget`, `Kick`.

## Learn & use

- **Narrative:** [Guide ch.7 §3 — sessions](xref:guide-subscriptions)
- **Feature detail:** [Sessions & admission](xref:feature-subscriptions-sessions-admission)
- **Internals:** [Technical overview 15 § 3 — what a session holds, and why it is cheap](xref:overview-subscriptions#3-the-cell-algorithm-who-receives-what)
