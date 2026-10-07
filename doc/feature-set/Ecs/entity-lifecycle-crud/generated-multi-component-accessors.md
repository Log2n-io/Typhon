---
uid: feature-ecs-entity-lifecycle-crud-generated-multi-component-accessors
title: 'Generated Multi-Component Accessors'
description: 'A source-generated Values struct and ReadAll / WriteAll methods that read or set every archetype component in one call.'
---

# Generated Multi-Component Accessors
> A source-generated `Values` struct and `ReadAll` / `WriteAll` methods that read or set every archetype component in one call.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Ecs](../README.md)

## 🎯 What it solves

Reading or writing several components on the same entity through raw `EntityRef` calls needs N+1 lines — one
`Open`/`OpenMut` plus one `Read`/`Set` per component. For archetypes with four or five components that clutters
game-loop code with repetitive boilerplate. The `ArchetypeAccessorGenerator` emits named multi-component accessors
directly on the archetype class so one call reads, or sets, every declared component.

## ⚙️ How it works (in brief)

For any `[Archetype]` class declared `partial`, the source generator emits a nested `Values` struct — one field per
`Comp<T>` the archetype declares, named to match it — plus static `ReadAll(tx, id)` and `WriteAll(tx, id, in values)`
methods. `ReadAll` calls `tx.Open` once, then `entity.Read` by handle for every component, and returns the copies.
`WriteAll` calls `tx.OpenMut` once, then `entity.Set` by handle for every component. Child archetypes get a `Values`
that includes every inherited component first, routed through the declaring parent class's `Comp<T>` handle for
correct slot resolution.

## 💻 Usage

```csharp
[Archetype]
partial class Unit : Archetype<Unit>           // 'partial' required — non-partial archetypes are silently skipped
{
    public static readonly Comp<Position> Pos = Register<Position>();
    public static readonly Comp<Velocity> Vel = Register<Velocity>();
}

[Archetype]
partial class Soldier : Archetype<Soldier, Unit>
{
    public static readonly Comp<Health> Health = Register<Health>();
}

// ─── Read every component in one call ───
Unit.Values v = Unit.ReadAll(tx, id);
float x = v.Pos.X;
float dx = v.Vel.Dx;

// ─── Inherited archetype — parent components included, parent-first ───
Soldier.Values s = Soldier.ReadAll(tx, soldierId);
float sx = s.Pos.X;        // from Unit
int hp = s.Health.Current; // own

// ─── Change some fields, then set every component in one call ───
var u = Unit.ReadAll(tx, id);
u.Pos.X = 999;
u.Vel.Dx = 42;
Unit.WriteAll(tx, id, u);
tx.Commit();
```

## ⚠️ Guarantees & limits

- The archetype class must be declared `partial`; if it isn't, the generator silently skips it — no
  `Values`/`ReadAll`/`WriteAll` are emitted, and no diagnostic is raised.
- `Values` holds copies, like `EntityRef.Read`: it is an ordinary struct, valid after the transaction, and changing it
  changes nothing until `WriteAll` (rule EP-03).
- Cost is one `Open`/`OpenMut` (~90–100 ns warm, see [the CRUD page](README.md)) plus one copy per component;
  `Versioned` fields additionally pay the per-`Set` copy-on-write allocation.
- Generated field names match the `Comp<T>` declarations exactly — there is no positional `C1`/`C2` form to
  disambiguate.
- `WriteAll` sets every component, which marks every one dirty and copies-on-write every `Versioned` one; when only
  a subset changes, `Set` those through `EntityRefMut` directly.

## 🧪 Tests

- [EntitySpawnTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EntitySpawnTests.cs) — `ReadAll`/`WriteAll` round-trip, inherited-archetype field inclusion, change-then-verify-persisted

## 🔗 Related

- Source: `src/Typhon.Generators/ArchetypeAccessorGenerator.cs`
- Parent feature: [Entity Lifecycle & CRUD API](./README.md)

<!-- Deep dive: claude/design/Ecs/04-crud-api.md §Generated Multi-Component Accessors -->
