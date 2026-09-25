---
uid: feature-subscriptions-projections-codecs
title: 'Projections & Codecs'
description: 'What of an archetype remote clients see — position as motion segments, quantized fields in change groups, enter-only and owner-only fields, fractions, headings — declared with the builder.'
---

# Projections & Codecs
> What of an archetype travels, and how it is quantized — network policy, declared once, never part of the storage schema.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

A component holds far more than a client needs, at far more precision: a `float` health, a `double` coordinate, AI scratch written every
tick. A projection names only what travels, quantizes it to what the client can use, and splits it so an update carries only what changed.

## ⚙️ How it works (in brief)

- **Position.** `Motion(comp, …)` for movers: sent as **motion segments** — a start point, a velocity, a start tick — that the client
  extrapolates. A new segment is sent when the extrapolation would drift past `Tolerance` (5 cm by default), on a teleport (a jump faster
  than `Teleport(maxSpeedMps)`, which is required and also sizes the velocity codec), or as a `MaxAge` heartbeat (5 s). `Position(comp)`
  for things that never move. The component must hold the archetype's `[SpatialIndex]` field; positions are quantized to 24 bits per axis
  over the spatial world's bounds.
- **Fields** travel under a `Codec` and in a **change group** (the default one when unnamed): an update record carries only the groups
  that changed.
- **`OnEnter`** fields are sent once, when an entity enters a session's view.
- **`Fraction(comp, value, max, bits)`** sends a value as a fraction of another field — an 8-bit health bar.
- **`Heading(comp, angle, bits, toleranceDeg)`** sends an angle only when it turned past the tolerance.
- **`Owner(o => o.Field(…))`** fields go only to the controlling session ([Owner state](owner-state.md)).
- **`Static<T>(…)`** declares an archetype whose state never changes: sent once on enter, no groups, no per-tick comparison.

**Codecs** (`Codec.X`): `Bool`, `U8`/`I8`/`U16`/`I16`/`U32`/`I32`, `VarUInt`/`VarInt`, `F32`, `F16`, `Quant(min, max, bits)`,
`Unorm(bits)`, `Snorm(bits)`, `Angle(bits)`, `Bits(n)`, `Enum<T>(bits)`, `Vec2/Vec3(scale, bits)`, `EntityRef`, `Str`/`Blob`/`Bytes`,
`List(of, min, max)`, `TickLo`, `Quat3`. `.Saturate()` clamps out-of-range values instead of refusing them. Quantizers take 8, 16, 24 or
32 bits. The same arithmetic runs bit-exactly in the engine and both SDKs.

## 💻 Usage

```csharp
subs.Archetype<Creature>(a => a
    .Motion(Creature.Bounds, m => m.Tolerance(0.05).Teleport(maxSpeedMps: 12))
    .OnEnter(Creature.Ai, x => x.AggroRadius, Codec.F16, name: "aggro")
    .Field(Creature.Ai, x => x.Mode, Codec.U8, name: "mode")
    .Fraction(Creature.Vitals, v => v.Health, v => v.MaxHealth,
              bits: 8, name: "hp", group: "vitals"));

subs.Static<WorldObject>(a => a
    .Position(WorldObject.Bounds)
    .OnEnter(WorldObject.Struct, s => s.Kind, Codec.U8, name: "kind"));
```

The same declarations can be written as attributes on the data ([Replication by attributes](replication-attributes.md)).

## ⚠️ Guarantees & limits

- **No 64-bit integer reaches the wire implicitly**: a `long` needs an explicit codec, and a narrowing one must `.Saturate()` (clamps are
  counted). Refused at declaration.
- **At most 8 change groups** per section — the public fields, and the owner fields — because the group mask is a byte; up to **255
  replicated archetypes**.
- **Components are read through the cluster layout** (SUB-01); a projection never reads storage any other way.
- **Replication state per entity** of an observed archetype: 64 B hot + 32 B cold for a 2D mover with a short state body, in native
  pools bounded by `StatePoolBudgetBytes`.
- The projection is **not** part of the component's schema identity: changing a codec is never a migration.

## 🧪 Tests

- [EntitiesEncodingTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/EntitiesEncodingTests.cs) — records, groups and positions on the wire
- [MotionSegmentTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/MotionSegmentTests.cs) — the segment rule: tolerance, teleport, heartbeat
- [HeadingTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/HeadingTests.cs) — a turn under the tolerance sends nothing

## 🔗 Related

- Concept: [Projection](xref:concept-projection) · [Replication catalog](xref:concept-replication-catalog)
- [Replication by attributes](replication-attributes.md) · [Wire protocol & catalog](wire-protocol.md)
