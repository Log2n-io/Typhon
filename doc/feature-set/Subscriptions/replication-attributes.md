---
uid: feature-subscriptions-replication-attributes
title: 'Replication by Attributes'
description: 'Declare an archetype''s projection and a message''s codecs on the data — [Replicated], [Motion], [Replicate], [Owner], [ReplicatedMessage]… — compiled by a source generator into builder calls, no reflection.'
---

# Replication by Attributes
> The projection on the data it describes; a source generator turns it into the builder calls you would have written.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

For the common case — one projection per archetype — the builder repeats what the component already says: its fields, their types.
Attributes put the declaration next to the data, keep one source of truth, and still compile to the builder, so validation, the catalog and
the wire are exactly the same.

## ⚙️ How it works (in brief)

- **Archetypes.** A `[Replicated]` archetype (`partial`) gets a generated `IReplicatedArchetype` implementation whose `DeclareReplication`
  is one builder call per attribute. `subs.Archetype<T>()` applies it — constrained to `IReplicatedArchetype`, so an archetype without the
  attribute does not compile there. `subs.Archetype<T>(a => …)` **replaces** the attributes entirely: the per-deployment escape.
- **Messages.** A `[ReplicatedMessage]` struct (`partial`) gets an engine-free `IReplicatedMessage` descriptor of its attributed fields.
  `subs.Command<T>(…)` and `subs.Event<T>(…)` read it; a field declared in the builder call wins over its attribute, and an unattributed
  field takes its type's default. The contracts assembly needs only `Typhon.Protocol` and the generator.
- **Codecs** are emitted through `Codec.Declared<TField>(kind, …)`: one mapping from a declared kind to a codec, validated by the same
  factories, with the same defaults by type as an undeclared message field.
- **No reflection, no module initializer**: the generator implements interfaces on the partial types. AOT-safe.

| Attribute | On | Builder equivalent |
|---|---|---|
| `[Replicated(Static)]` | archetype class | `subs.Archetype<T>(…)` / `Static<T>(…)` — opt in |
| `[Motion(ToleranceM, TeleportMps, MaxAgeS)]`, `[Position]` | a `Comp<T>` field (a parent archetype's reaches its children) | `.Motion(…)`, `.Position(…)` |
| `[Replicate(kind?, Group, Name)]` | component field | `.Field(…)` |
| `[OnEnter(kind?, Name)]` | component field | `.OnEnter(…)` |
| `[Owner(kind?, Group, Name)]` | component field | `.Owner(o => o.Field(…))` |
| `[Fraction(nameof(max), Bits, Name, Group)]` | numeric component field | `.Fraction(…)` |
| `[Heading(Bits, ToleranceDeg, Name)]` | angle field | `.Heading(…)` |
| `[ReplicatedMessage]` | command / event struct | the field half of `Command<T>` / `Event<T>` |
| `[Codec(kind)]`, `[Quant(min, max, bits)]`, `[EntityRef]` | public message field | `.Field(m => m.X, Codec.X(…))` |

Every codec-bearing attribute also takes `Bits`, `Min`, `Max`, `Scale`, `MaxBytes`, `Name` and `Saturate`.

## 💻 Usage

```csharp
[Archetype(1, "Creatures"), Replicated]
public partial class Creature : Archetype<Creature>
{
    [Motion(ToleranceM = 0.05, TeleportMps = 12)]
    public static readonly Comp<CreaturePlacement> Bounds = Register<CreaturePlacement>();
    public static readonly Comp<CreatureVitals> Vitals = Register<CreatureVitals>();
}

public struct CreatureVitals
{
    [Fraction(nameof(MaxHealth), Name = "hp", Group = "vitals")]
    [Owner(CodecKind.Varu, Name = "health")]
    public int Health;
    public int MaxHealth;
}

[ReplicatedMessage]
public partial struct MoveTo
{
    [Quant(-8192, 8192, 24)] public double X;
    [Quant(-8192, 8192, 24)] public double Z;
}

subs.Archetype<Creature>();
subs.Command<MoveTo>(c => c.Coalesce(CommandCoalesce.LatestPerSession).Rate(10, burst: 20));
```

## ⚠️ Guarantees & limits

- **Compile-time diagnostics**: TPH1101 not partial · TPH1102 two positions · TPH1104 a type with no default codec (`long`, `double`, a
  struct) · TPH1105 an invalid `[Fraction]` · TPH1106 a misplaced attribute (a message codec on a component field, a component attribute on
  a message, a codec on a non-public message field, `[Motion]` off a `Comp<T>`) · TPH1107 a numeric codec on a non-number · TPH1108 a field
  both `[Replicate]` and `[Owner]` (the "private" value would go to everyone) · TPH1109 an unsupported shape (generic, nested in a
  non-partial type, `[Replicated]` without the engine, an undefined `CodecKind`). Widths and ranges are validated by the codec factories at
  declaration.
- **Attributes cannot vary per archetype**: a component shared by two replicated archetypes projects the same way in both. When they should
  differ, declare one of them with the builder.
- **Not expressible as attributes** (use the builder): `Computed`, `VelocityFrom`, `IgnoreTickDilation`, a `List` codec; a command's rate,
  roles and coalescing; an event's routing.
- **Attributes are not schema**: the storage schema ignores them, so a codec change is never a migration.
- The SWG demo declares its five archetypes by attributes; its catalog hash is identical to the builder-declared one.

## 🧪 Tests

- [ReplicationAttributeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/ReplicationAttributeTests.cs) — attributed archetypes, commands and events compile to the builder's catalog byte for byte; override; precedence; schema unchanged
- [ReplicationGeneratorTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Generators.Tests/ReplicationGeneratorTests.cs) — every attribute's emission, compiled against the builder's signatures, and every diagnostic

## 🔗 Related

- [Projections & codecs](projections-codecs.md) · [Commands & acknowledgements](commands.md) · [Events](events.md)
- Concept: [Projection](xref:concept-projection)
