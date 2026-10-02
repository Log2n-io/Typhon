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

**Codecs** (`Codec.X`): `Bool`, `U8`/`I8`/`U16`/`I16`/`U32`/`I32`, `U64`/`I64`, `VarUInt`/`VarInt`, `VarUInt64`/`VarInt64`, `F32`,
`F64`, `F16`, `Quant(min, max, bits)`, `Unorm(bits)`, `Snorm(bits)`, `Angle(bits)`, `Bits(n)`, `Enum<T>(bits)`, `Vec2/Vec3(scale, bits)`,
`EntityRef`, `Str`, `Coll(maxCount)`, `TickLo`, `Quat3`. **`Codec.Exact`** is the stored type's exact codec — `u64` for a
`ulong`, `f64` for a `double`, `f32 × 3` for a `Point3F`, `str{63}` for a `String64`, `entityRef` for an `EntityId` or an `EntityLink<T>`.
`.Count(n)` repeats a scalar codec for a point, a quaternion, a box or a sphere,
whose count the type also gives. `.Saturate()` clamps out-of-range values instead of refusing them. Quantizers take 8, 16, 24 or 32 bits. The
same arithmetic runs bit-exactly in the engine and the .NET, TypeScript and C SDKs.

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

- **Exact by default, lossy only when declared**: a 64-bit integer travels whole (`u64`, `i64`, their varints), a `double` as `f64`, a point or a
  box as its components; a narrowing of any width must `.Saturate()` (clamps are counted), and a pairing that loses data for nothing — a `float`
  in an integer codec, a `float` in `f64` — is refused at declaration.
- **Text on a field** (`String64`, as `Codec.Exact` or `Codec.Str(n)` with n ≤ 63) is cut at its cap on a character boundary, never inside one,
  and every cut is counted. A group holding text is stored out of line, so it costs the entity's hot entry 8 bytes whatever its length. A
  `String1024` cannot be in a replicated archetype — cluster storage needs eight entities to a page.
- **A reference** (`EntityId` or `EntityLink<T>`, as `entityRef`) carries its target's netId, or 0 when the target is null, gone, or not
  replicated. A target given its identity this tick is resolved the next one. When the target goes, every referrer sends 0 the following tick,
  with no push from the application, long before the netId can be reissued. A reference in an `OnEnter` field or on a `Static` archetype is
  refused, since it could never be corrected. So is an `EntityLink<T>` whose `T` no profile observes.
- **A collection** (`ComponentCollection<T>`, as `Codec.Coll(maxCount)`) travels whole whenever it changes. Each element carries `T`'s public
  fields, exactly, and may hold text and references. `maxCount` is required. A longer list sends its first `maxCount` elements and its real
  count, so the client sees the cut, and the cut is counted. An element holding `bool` or `char` is refused, since its layout would not be the
  buffer's. A collection is legal in a public group, the owner section and `OnEnter`, never in an event or a command.
- **At most 8 change groups** per section — the public fields, and the owner fields — because the group mask is a byte; up to **255
  replicated archetypes**.
- **Components are read through the cluster layout** (SUB-01). The one exception is a collection's elements, which live outside the cluster:
  they are read from the component's buffer, in the replication track only.
- **Replication state per entity** of an observed archetype: 64 B hot + 32 B cold for a 2D mover with a short state body, in native
  pools bounded by `StatePoolBudgetBytes`.
- The projection is **not** part of the component's schema identity: changing a codec is never a migration.

## 🧪 Tests

- [EntitiesEncodingTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/EntitiesEncodingTests.cs) — records, groups and positions on the wire
- [MotionSegmentTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/MotionSegmentTests.cs) — the segment rule: tolerance, teleport, heartbeat
- [HeadingTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/HeadingTests.cs) — a turn under the tolerance sends nothing
- [CollectionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/CollectionTests.cs) — collections whole on change, truncation counted, references per element
- [WideGroupTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/WideGroupTests.cs) — text in every section, cut at a character, carried across a migration

## 🔗 Related

- Concept: [Projection](xref:concept-projection) · [Replication catalog](xref:concept-replication-catalog)
- [Replication by attributes](replication-attributes.md) · [Wire protocol & catalog](wire-protocol.md)
