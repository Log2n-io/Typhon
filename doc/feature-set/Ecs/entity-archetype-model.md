---
uid: feature-ecs-entity-archetype-model
title: 'Entity & Archetype Model'
description: 'Structured 64-bit entity identity, C# class-hierarchy archetypes, and typed zero-copy component handles — the schema backbone of every other ECS feature.'
---

# Entity & Archetype Model
> Structured 64-bit entity identity, C# class-hierarchy archetypes, and typed zero-copy component handles — the schema backbone of every other ECS feature.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Ecs](./README.md)

## 🎯 What it solves

A flat `EntityId` with independent per-component tables gives no guarantee that a "Factory" entity actually has the components a Factory needs, and forces one B+Tree lookup per component just to read an entity's data. The Entity & Archetype Model fixes both: an archetype declares an entity's complete component set up front (engine-enforced, not caller-maintained), and a single per-archetype lookup resolves every component slot at once. It also removes a whole class of bugs around stale references — entity identity here can never collide with a previously deleted entity occupying the same slot.

## ⚙️ How it works (in brief)

Archetypes are plain C# classes in a single-inheritance hierarchy, each declaring its components as `static readonly Comp<T>` fields; a derived archetype (e.g. `House : Archetype<House, Building>`) inherits its parent's components at stable slot indices and adds its own. Every `EntityId` packs a 52-bit monotonic `EntityKey` and a 12-bit `ArchetypeId` into one `ulong` — the `ArchetypeId` routes to the entity's archetype, the `EntityKey` is the lookup key within it. Because keys are never recycled, a stale `EntityId` simply misses on lookup; no version/generation field is needed. Opening an entity (`Open`/`OpenMut`) pays one lookup and returns an `EntityRef` that amortizes it across all subsequent `Read`/`Write` calls for that entity. `Comp<T>` is the typed handle used both to declare a component on an archetype and to address it later — slot resolution is O(1) and type-checked by the compiler.

## 💻 Usage

```csharp
// ─── Component types: plain unmanaged structs ───
[Component("Game.Placement", 1)]
public struct Placement
{
    public float X, Y, Z;
}

[Component("Game.HouseData", 1)]
public struct HouseData
{
    public int Residents;
}

// ─── Archetype hierarchy ───
[Archetype(1)]
public class Building : Archetype<Building>
{
    public static readonly Comp<Placement> Placement = Register<Placement>();
}

[Archetype(2)]
public class House : Archetype<House, Building>
{
    public static readonly Comp<HouseData> HouseInfo = Register<HouseData>();
}

// ─── Spawn, open, and access ───
using var t = dbe.CreateQuickTransaction();

var id = t.Spawn<House>(
    Building.Placement.Set(new Placement { X = 1, Y = 0, Z = 2 }),
    House.HouseInfo.Set(new HouseData { Residents = 4 }));

var house = t.Open(id);
ref readonly var placement = ref house.Read(Building.Placement); // inherited slot, zero-copy
ref readonly var info = ref house.Read(House.HouseInfo);

t.Commit();
```

| Constraint | Limit | Effect |
|---|---|---|
| Components per archetype | 16 | `[Archetype]` registration throws if exceeded |
| Registered archetypes | 4,096 (12-bit `ArchetypeId`) | IDs are explicit (`[Archetype(Id = N)]`), not auto-assigned |
| `ComponentValue` inline payload | 112 bytes | Larger components use incremental Spawn (`Spawn` + `OpenMut` + `Write`) |
| Archetype inheritance | single parent only | No diamond inheritance — enforced at registration |

## ⚠️ Guarantees & limits

- **Schema enforcement at creation**: `Spawn<T>` always allocates every component slot of the archetype (omitted ones zero-initialized and disabled) — there is no "entity missing a required component" error class.
- **O(1) entity→component routing**: one lookup per `Open`/`OpenMut` resolves all component locations for that entity; `Read`/`Write` afterward are direct, ~1-5ns for SingleVersion/Transient, ~50-100ns for Versioned (MVCC revision walk).
- **Zero-copy access**: `Read` returns `ref readonly T`, `Write` returns `ref T` — no struct copies into or out of storage.
- **Stale references always miss, safely**: `EntityKey` is monotonic and never recycled, so there's no version field and no ABA hazard — a held `EntityId` for a destroyed entity simply fails to resolve.
- **Polymorphic slot stability**: a component inherited from a parent archetype sits at the same slot index in every descendant, so handles like `Building.Placement` work uniformly across `Building`, `House`, and any other subclass.
- **`ArchetypeId` is permanent**: it's embedded in every persisted `EntityId`, so it must be assigned explicitly via the attribute and never reused for a different schema.
- **Component removal is non-structural**: an archetype's component set is fixed after spawn — "removing" a component means disabling it (O(1) bit), not migrating the entity to a different archetype.

## 🧪 Tests

- [EntityIdTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EntityIdTests.cs) — `EntityId` 64-bit packing (`EntityKey`/`ArchetypeId`), null semantics, equality, raw-value round-trip
- [ArchetypeRegistrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ArchetypeRegistrationTests.cs) — archetype registration, parent-first slot inheritance, component-type dedup across archetypes, subtree building

## 🔗 Related

- Sibling: [Entity Lifecycle & CRUD API](entity-lifecycle-crud/README.md) — `Open`/`OpenMut`/`Spawn` resolve entities through the archetype model described here
- Sibling: [Query System (EcsQuery)](query-system.md) — Tier 1 `ArchetypeMask` constraints are built directly on this model

<!-- Deep dive: claude/design/Ecs/03-entity-model.md (EntityId layout, archetype registration, resolution chain) -->
<!-- Deep dive: claude/design/Ecs/02-design-decisions.md (decisions #1-21) -->
<!-- Deep dive: claude/design/Ecs/01-motivation.md (why archetypes replace the flat CRUD model) -->
<!-- ADR: 002-ecs-data-model (claude/adr/002-ecs-data-model.md) -->
