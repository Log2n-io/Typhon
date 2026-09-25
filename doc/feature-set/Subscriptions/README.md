---
uid: feature-subscriptions-index
title: 'Subscriptions'
description: 'Engine-owned replication to remote clients: declared archetype state pushed to each session around it, owner-only state, typed commands and events, over TCP or WebSocket, to .NET and TypeScript clients.'
---

# Subscriptions

> Engine-owned replication. An application declares what each archetype exposes, who sees what, and which commands clients may send;
> the engine does change detection, quantized encoding, motion segments, per-session visibility, sessions, backpressure, inbound
> hardening and the wire — in parallel on the worker pool, to native and browser clients.

> 🔬 **Recommended:** read [in-depth-overview/15-subscriptions.md](../../in-depth-overview/15-subscriptions.md) (Chapter 15) for the
> mechanism, and [Guide ch.7](../../guide/07-subscriptions.md) for an end-to-end walkthrough, before the pages below.

**Status:** 🚧 Partial — the features below are built; the list of what is not yet is at the end · **Category:** Subscriptions

## 🎯 What it solves

A game server's state has to reach thousands of clients, each seeing only the part of the world around it, at tick rate — without the
application writing change detection, encoding or networking, and without the cost growing as clients × entities. Subscriptions run
replication as an engine track after the tick fence, encode each change once, give each session only what lies around it, and never let
a slow client hold up the tick.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Push replication](push-replication.md) | `Replicate` after a write; the engine pushes spawns, destroys, moves and migrations itself, compares quantized bytes and encodes each change once | ✅ Implemented | 🔵 Core |
| [Projections & codecs](projections-codecs.md) | What of an archetype travels: position as motion segments, fields in change groups, enter-only and owner-only fields, `Fraction`, `Heading`, `Static` | ✅ Implemented | 🔵 Core |
| [Replication by attributes](replication-attributes.md) | `[Replicated]`, `[Motion]`, `[Replicate]`, `[Owner]`… on the data, compiled by a source generator; the builder overrides them | ✅ Implemented | 🔵 Core |
| [Profiles & observers](profiles-observers.md) | `World`, `Sphere` (leave band, run-time radius, distance bands), `ClientRegion` with a near budget, an `Aggregate` tier, cadence | ✅ Implemented | 🔵 Core |
| [Sessions & admission](sessions-admission.md) | Declared kinds, an admission hook with roles and limits, session events, staged requests: profile, control, budget, kick | ✅ Implemented | 🔵 Core |
| [Owner state (`SELF`)](owner-state.md) | Fields only the controlling session receives, plus the last applied command sequence | ✅ Implemented | 🔵 Core |
| [Commands & acknowledgements](commands.md) | Typed intents, rate/role/pre-check on the transport thread, `TryResolve` scoped to what the session holds, every refusal answered | ✅ Implemented | 🔵 Core |
| [Events](events.md) | One-off facts routed near a point, to the sessions that hold an entity, to an owner, to one session, or to all; best effort with a loss count | ✅ Implemented | 🟣 Advanced |
| [Backpressure & budgets](backpressure-budgets.md) | Skip never queue, catch-up from an 8-tick log, rate classes, per-session byte budgets, the inbound budget and the abuse close | ✅ Implemented | 🟣 Advanced |
| [Transports & hosting](transports-hosting.md) | The engine's TCP listener and the ASP.NET Core WebSocket adapter; the catalog endpoint | ✅ Implemented | 🔵 Core |
| [Client SDKs](client-sdks.md) | TypeScript (browser) and .NET (bots, tools) clients: stores, motion extrapolation, commands, reconnection, code generation | ✅ Implemented | 🔵 Core |
| [Wire protocol & catalog](wire-protocol.md) | `typhon.2`: the handshake, the canonical catalog, frames and blocks, close codes | ✅ Implemented | 🟣 Advanced |
| [Diagnostics](diagnostics.md) | `STATS` metrics (built-in and your own), the `DEBUG` capability, the push validator, runtime counters | ✅ Implemented | 🟣 Advanced |

## ⚠️ Not built yet

Refused at `Start` or at the call — never silently ignored:

- several entity observers in one profile, and per-session observers (`SessionRequest.Observe`);
- shared and keyed View sources (`SubscriptionsRegistry.Source`, `SessionRequest.SetSources`);
- resumable sessions (`resumeToken` is always 0) and reliable events;
- shared tile and `Static` snapshot blobs; the WebTransport link.

## 🔗 Related

- Concepts: [Subscription](xref:concept-subscription) · [Projection](xref:concept-projection) · [Replication profile](xref:concept-replication-profile) ·
  [Replication session](xref:concept-replication-session) · [Client command](xref:concept-client-command) · [Replication event](xref:concept-replication-event) ·
  [Replication catalog](xref:concept-replication-catalog)
- Related feature: [Overload Management](../Runtime/overload-management.md) — replication degrades sessions before the tick does
- Related feature: [Spatial](../Spatial/README.md) — a replicated archetype is spatially indexed; views are found through the grid
- Correctness rules: [`rules/subscriptions.md`](https://github.com/Log2n-io/Typhon/blob/main/rules/subscriptions.md) (SUB-01 … SUB-27)

<!-- Deep dive: claude/design/Subscriptions/README.md -->
<!-- Deep dive: claude/adr/067-push-replication.md -->
