---
uid: concept-client-command
title: 'Client command'
description: 'A typed intent a client sends — a move, an attack — checked on the transport thread, drained into the next tick for systems to validate and apply, and always answered.'
---

# Client command

> **In one line:** a typed **intent** a client sends — "move there", "attack that" — delivered to systems in the next
> [tick](xref:concept-tick), per session and in order, and always answered.

A command type is an `unmanaged` struct, declared once with its policy: a **rate** and burst per session, the **roles** allowed to send
it, whether it **coalesces** (only the newest per session per tick survives — a move target) or queues, and an optional stateless
**pre-check**. Its fields travel under codecs like any replicated field; a contracts assembly that references only `Typhon.Protocol` can
declare them with attributes (`[ReplicatedMessage]`, `[Quant]`, `[EntityRef]`), so .NET clients share the exact struct.

**Two gates, two jobs.** On the transport thread, before a tick sees it, a command is checked against the wire, the session's inbound
**byte budget**, its type's rate, the session's role and the pre-check. What passes is drained into typed buffers at the start of the next
tick and read by systems with `ctx.Subscriptions.Commands<T>()`. **Semantic** validation — is the target in range, is the cooldown over,
does the player own that item — is the system's, because the state it must agree with is the system's.

**Entities are named by netId**, the identity the client was sent. `TryResolve(session, netId, out entity)` answers only for entities
that session actually holds — in its view as last sent, or the one it controls — so a client cannot target what it was never shown.

**Every refused command is answered.** A refusal travels back in the session's next frame as an `ACK` with a reason — rate-limited,
forbidden by role, rejected by the pre-check or by the application (`Reject`) — while `SELF.lastSeq` reports the last command the tick
drained, so a predicting client settles every command it sent. A client that keeps sending what is refused is closed with `1008`.

## How it relates

- **[Replication session](xref:concept-replication-session)** — the session's role decides which commands it may send.
- **[System](xref:concept-system)** — commands are read in the tick, by ordinary systems.
- **[Replication event](xref:concept-replication-event)** — the other direction: the server telling clients something happened.

## In the API

- [`SubscriptionsRegistry.Command<T>(…)`](xref:Typhon.Engine.SubscriptionsRegistry) and [`CommandBuilder<T>`](xref:Typhon.Engine.CommandBuilder`1)
  — `Rate`, `Roles`, `Coalesce`, `Precheck`, `Field`, `Ignore`.
- [`SubscriptionsCommands`](xref:Typhon.Engine.SubscriptionsCommands) — `Commands<T>()`, `TryGetLatest`, `TryResolve`, `Reject`.
- [`ReplicatedMessageAttribute`](xref:Typhon.Protocol.ReplicatedMessageAttribute), [`AckReasons`](xref:Typhon.Protocol.AckReasons).

## Learn & use

- **Narrative:** [Guide ch.7 §5 — commands](xref:guide-subscriptions)
- **Feature detail:** [Commands & acknowledgements](xref:feature-subscriptions-commands)
