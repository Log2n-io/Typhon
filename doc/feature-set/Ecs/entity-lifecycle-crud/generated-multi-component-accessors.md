# Generated Multi-Component Accessors
> Source-generated zero-copy Refs/MutRefs structs reading or writing every archetype component in one call.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Ecs](../README.md)

## 🎯 What it solves

Reading or writing several components on the same entity through raw `EntityRef` calls needs N+1 lines — one
`Open`/`OpenMut` plus one `Read`/`Write` per component. For archetypes with four or five components that
clutters game-loop code with repetitive boilerplate, and a generic `out T1, out T2, ...` alternative would copy
data instead of returning zero-copy refs. The `ArchetypeAccessorGenerator` emits named, zero-copy multi-component
accessors directly on the archetype class so one call resolves every declared component.

## ⚙️ How it works (in brief)

For any `[Archetype]` class declared `partial`, the source generator emits nested `Refs` / `MutRefs` ref
structs — one `ref readonly` (or `ref`) field per `Comp<T>` the archetype declares, named to match the field —
plus static `ReadAll(tx, id)` / `ReadWriteAll(tx, id)` methods. Internally these call `tx.Open`/`OpenMut` exactly
once, then `entity.Read`/`Write` by handle for every component — the same O(1)-per-component cost as hand-written
code, just without the repetition. Child archetypes get a `Refs`/`MutRefs` that includes every inherited
component first, routed through the declaring parent class's `Comp<T>` handle for correct slot resolution.

## 💻 Usage

```csharp
[Archetype(100)]
partial class Unit : Archetype<Unit>           // 'partial' required — non-partial archetypes are silently skipped
{
    public static readonly Comp<Position> Pos = Register<Position>();
    public static readonly Comp<Velocity> Vel = Register<Velocity>();
}

[Archetype(101)]
partial class Soldier : Archetype<Soldier, Unit>
{
    public static readonly Comp<Health> Health = Register<Health>();
}

// ─── Read every component in one call ───
Unit.Refs r = Unit.ReadAll(tx, id);
float x = r.Pos.X;
float dx = r.Vel.Dx;

// ─── Inherited archetype — parent components included, parent-first ───
Soldier.Refs sr = Soldier.ReadAll(tx, soldierId);
float sx = sr.Pos.X;        // from Unit
int hp = sr.Health.Current; // own

// ─── Write every component in one call ───
Unit.MutRefs m = Unit.ReadWriteAll(tx, id);
m.Pos.X = 999;
m.Vel.Dx = 42;
tx.Commit();
```

## ⚠️ Guarantees & limits

- The archetype class must be declared `partial`; if it isn't, the generator silently skips it — no
  `Refs`/`MutRefs`/`ReadAll`/`ReadWriteAll` are emitted, and no diagnostic is raised.
- `Refs`/`MutRefs` are `ref struct` — stack-only, same lifetime constraints as `EntityRef`; cannot be stored in
  a field, boxed, or escape the call site.
- Cost is one `Open`/`OpenMut` (~350ns) plus N ref assignments (~1-5ns each for `SingleVersion`/`Transient`);
  `Versioned` fields additionally pay the per-`Write` copy-on-write allocation.
- Generated field names match the `Comp<T>` declarations exactly — there is no positional `C1`/`C2` form to
  disambiguate.
- `ReadWriteAll` opens the entity read-write and exposes every field mutably at once; there is no generated
  partial-write overload — use `EntityRef.Write` directly when only a subset of components needs mutation.

## 🧪 Tests

- [EntitySpawnTests](../../../../test/Typhon.Engine.Tests/Data/ECS/EntitySpawnTests.cs) — `ReadAll`/`ReadWriteAll` zero-copy round-trip, inherited-archetype field inclusion, mutate-then-verify-persisted

## 🔗 Related

- Source: `src/Typhon.Generators/ArchetypeAccessorGenerator.cs`
- Parent feature: [Entity Lifecycle & CRUD API](./README.md)

<!-- Deep dive: claude/design/Ecs/04-crud-api.md §Generated Multi-Component Accessors -->
