---
uid: concept-projection
title: 'Projection'
description: 'What of an archetype remote clients see: its position as motion segments, its fields quantized into change groups, enter-only fields, and owner-only fields — declared by attributes or a builder.'
---

# Projection

> **In one line:** what of an [archetype](xref:concept-archetype) remote clients see — declared once, compiled at `Start`, and compared
> in quantized form every time an entity is pushed.

A projection is **network policy, not schema**: it names which component fields travel and how, without touching storage. It has four
kinds of content:

- **A position** — `Motion` for movers, sent as **motion segments** (a start point, a velocity, a start tick) that the client
  extrapolates, re-sent only when the extrapolation would drift past a tolerance, on a teleport, or as a heartbeat; `Position` for
  things that never move.
- **Fields** that travel with updates, each under a **codec** — `U8`, `F16`, `Quant(min, max, bits)`, `Unorm`, an enum's width… — and in
  a **change group**: a record carries only the groups that changed, so a health change does not resend a mode.
  `Fraction` sends one field as a fraction of another (health of max health, 8 bits); `Heading` sends an angle only when it turns past a
  tolerance.
- **Enter-only fields** (`OnEnter`) — sent once, when an entity comes into a session's view.
- **Owner fields** (`Owner`) — sent only to the session that controls the entity, in its `SELF` block.

The engine keeps, per replicated entity, the **encoded** bytes it last sent (64 B hot, 32 B cold): a push re-encodes the entity and
compares bytes, so a push that changed nothing sends nothing, and a change is encoded **once** for every session. No 64-bit integer
reaches the wire unless the projection clamps it on purpose.

**Two ways to declare the same thing.** Attributes on the data — `[Replicated]` on the archetype, `[Motion]` on its placement component,
`[Replicate]`, `[OnEnter]`, `[Owner]`, `[Fraction]`, `[Heading]` on component fields — are compiled by a source generator into builder
calls; `subs.Archetype<T>()` applies them. The builder — `subs.Archetype<T>(a => a.Motion(…).Field(…))` — replaces them entirely, for a
deployment that needs another projection or a shared component that differs per archetype. Either way the replication attributes are not
part of the component's storage identity: changing a codec is never a migration.

## How it relates

- **[Component](xref:concept-component)** / **[Archetype](xref:concept-archetype)** — a projection reads component fields through the
  archetype's cluster layout.
- **[Replication profile](xref:concept-replication-profile)** — a profile observes archetypes; their projections decide what travels.
- **[Replication catalog](xref:concept-replication-catalog)** — every projection is exported in the catalog clients decode against.

## In the API

- [`SubscriptionsRegistry.Archetype<T>(…)`](xref:Typhon.Engine.SubscriptionsRegistry) / `Static<T>(…)` and the attributed
  `Archetype<T>()`; [`ArchetypeProjectionBuilder`](xref:Typhon.Engine.ArchetypeProjectionBuilder); [`Codec`](xref:Typhon.Engine.Codec).
- Attributes: [`ReplicatedAttribute`](xref:Typhon.Protocol.ReplicatedAttribute), [`ReplicateAttribute`](xref:Typhon.Protocol.ReplicateAttribute),
  [`OwnerAttribute`](xref:Typhon.Protocol.OwnerAttribute) and their siblings in `Typhon.Protocol`.

## Learn & use

- **Narrative:** [Guide ch.7 §1 — declaring what clients see](xref:guide-subscriptions)
- **Feature detail:** [Projections & codecs](xref:feature-subscriptions-projections-codecs) ·
  [Replication by attributes](xref:feature-subscriptions-replication-attributes)
