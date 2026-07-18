---
uid: guide-modeling
title: '2 — Modeling your world'
description: 'Chapter 1 showed the data loop. This chapter is about design: how to shape your data so the engine works with you. Four decisions live here — what your…'
---

# 2 — Modeling your world

Chapter 1 showed the data loop. This chapter is about **design**: how to shape your data so the engine works *with* you. Four decisions live here — what your components and archetypes are, which **storage mode** each component uses, which fields you **index**, and whether you need **spatial** queries. Get these right and the rest of Typhon falls into place; get them wrong and you'll fight the engine.

We'll grow the chapter-1 `Unit` into something a real skirmish would use.

---

## 1. The shape: components, archetypes, entities

The three nouns again, now with the *why*:

- A **component** is a plain `struct` of data — `Position`, `Health`, `Team`. No behaviour, no engine references.
- An **archetype** is a *fixed set* of components — the shape `Unit = Health + Position + …`. You declare it as a class.
- An **entity** is one instance of an archetype, addressed by an `EntityId`.

💡 **Why a fixed shape per entity?** Because Typhon stores components **archetype-major**: every `Unit`'s `Position` sits contiguously in memory, separate from every `Building`'s. Iterating "all units' positions" is then a linear walk over packed memory — cache-friendly, branch-free, fast. That contiguity is the whole performance bet of ECS, and it's only possible because the shape is fixed at spawn. The cost: an entity can't grow a new component type after it's spawned (you model that with a different archetype, or an *enabled/disabled* component flag).

### Declaring an archetype

```csharp
[Archetype(1)]
public sealed partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Health>   Health   = Register<Health>();
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Bounds>   Bounds   = Register<Bounds>();
    public static readonly Comp<Velocity> Velocity = Register<Velocity>();
    public static readonly Comp<Team>     Team     = Register<Team>();
}
```

Each `Register<T>()` adds a component slot and returns a `Comp<T>` handle (`Unit.Health`) you use everywhere — spawn, read, query. The `[Archetype(N)]` id is stable; keep it stable across versions.

**Archetype inheritance** lets one shape extend another:

```csharp
[Archetype(2)]
public sealed partial class Hero : Archetype<Hero, Unit>   // Hero = Unit's components + its own
{
    public static readonly Comp<Inventory> Inventory = Register<Inventory>();
}
```

A `Hero` *is-a* `Unit` for typed references: `EntityLink<Unit>` accepts a `Hero`. Use `EntityLink<T>` to point one entity at another — a typed, self-documenting reference. One caveat: `T` is a contract, not an enforced guarantee — the implicit conversion from `EntityId` accepts *any* entity, with no compile-time or runtime check that it's actually a `T` (or descendant).

```csharp
[Component("Skirmish.Target", 1)]
public struct Target { public EntityLink<Unit> Enemy; }   // stores another entity, typed
```

### Reading every component at once — generated accessors

In ch.1 you read one component at a time with `e.Read(Unit.Health)`. For the common "give me everything" case, Typhon's source generator emits typed bulk accessors on any `partial` archetype:

```csharp
var u = Unit.ReadAll(tx, id);          // read-only view of all of Unit's components
int hp = u.Health.Current;

var m = Unit.ReadWriteAll(tx, id);     // mutable view
m.Health.Current -= 10;
```

**This needs one bit of project setup:** the generator must be referenced as an *analyzer* in your `.csproj` (alongside the engine reference):

```xml
<ProjectReference Include="path/to/Typhon.Generators.csproj"
                  ReferenceOutputAssembly="false" OutputItemType="Analyzer" />
```

Without it, the archetype still works — you just read with `e.Read(...)` instead of `ReadAll`. (That's why ch.1 used `e.Read`: no setup beyond the engine reference.)

---

## 2. Storage modes — the decision that matters most

Every component picks a **storage mode**, set on its `[Component]` attribute. This is the single most consequential modeling choice in Typhon, because it decides what ACID guarantees that component's *data* gets — and what it costs.

| | **Versioned** (default) | **SingleVersion** | **Transient** |
|---|---|---|---|
| Reads | snapshot-isolated (consistent point-in-time) | live (last write wins) | live |
| Writes | transactional — staged, committed | in-place, immediate | in-place, immediate |
| `Rollback` reverts it? | yes | no | no |
| Survives a crash? | yes (WAL + checkpoint) | to the last tick (tick-fence WAL) | no (memory only) |
| Cost | highest | low | lowest |

💡 **Why three modes instead of "everything is ACID"?** Because full MVCC isn't free — every Versioned write allocates a new revision and every read may walk a version chain. That's the right price for an account balance or an inventory, where "did this commit?" matters. It's the *wrong* price for a position you overwrite 60 times a second and never need to roll back. Typhon lets you pay per component instead of all-or-nothing.

The rule of thumb:

- **Versioned** — state where correctness matters: health, inventory, score, anything you'd be upset to lose or see half-updated.
- **SingleVersion** — hot fields, last-writer-wins, but you still want them to survive a restart: position, cached AI cost. Persisted at the tick boundary (you can lose at most the last tick on a crash).
- **Transient** — pure runtime scratch that should *not* survive a restart: per-frame velocity, targeting temporaries.

Applied to `Unit`:

```csharp
[Component("Skirmish.Health", 1, StorageMode = StorageMode.Versioned)]      // ACID gameplay state
public struct Health { public int Current, Max; }

[Component("Skirmish.Position", 1, StorageMode = StorageMode.SingleVersion)] // hot, durable, no isolation
public struct Position { public Point2F P; }

[Component("Skirmish.Bounds", 1, StorageMode = StorageMode.SingleVersion)]   // spatial index lives here ([§4](#4-spatial--querying-by-geometry))
public struct Bounds { [SpatialIndex(2f)] public AABB2F Box; }

[Component("Skirmish.Velocity", 1, StorageMode = StorageMode.Transient)]     // per-tick scratch
public struct Velocity { public float Dx, Dy; }

[Component("Skirmish.Team", 1, StorageMode = StorageMode.Versioned)]
public struct Team { [Index(AllowMultiple = true)] public int Id; }         // many units per team
```

> ⚠️ **The catch worth knowing now:** a transaction only protects *Versioned* data. An SV/Transient write is visible to everyone the instant it happens and can't be rolled back. Entity creation and destruction are transactional in **all** modes — it's component *data* writes that differ. Chapter 3 spells out exactly what each mode gives up.

A single archetype freely mixes modes — `Unit` above has all three — because the mode lives on each component *type*, not on the archetype. (`Bounds` is the spatial mirror of `Position`; [§4](#4-spatial--querying-by-geometry) explains why spatial indexing wants a separate box.)

---

## 3. Schema: fields, indexes, evolution

### Fields

Component fields are blittable value types: the numeric primitives, `bool`, fixed-width strings (`String64`), spatial types (`Point2F`/`Point3F`, AABBs), and `EntityLink<T>`. That "blittable" constraint is what lets Typhon store and memory-map components without serialization.

> **Two sizing rules that catch newcomers:**
>
> 1. **Only `public` fields count toward a component's size.** Typhon derives the stored layout from the struct's **public** fields (not `sizeof(T)`), so a `private` field is invisible to storage — adding `private int _pad` does **not** change anything.
> 2. **A component must be at least 8 bytes.** Chunk storage has an 8-byte minimum stride. A `Versioned` component with a single 4-byte field (one `int`/`float`) trips `Invalid component/chunk stride: 4 bytes …` at open time. Fix it by adding a **public** field so the struct reaches 8 bytes (e.g. a second `public int`). `SingleVersion`/`Transient` components clear 8 bytes automatically via their internal per-entity key, so this only bites tiny `Versioned` components.

### Indexes — fast lookup by field value

A plain field can only be found by scanning. Mark it `[Index]` and Typhon maintains a sorted index so you can look it up directly:

```csharp
public struct Team   { [Index(AllowMultiple = true)] public int Id; }   // many units share a team
public struct Serial { [Index] public int Number; }                     // unique — duplicates throw
```

- `[Index(AllowMultiple = true)]` allows many entities to share a value — use it for "all units on team 3". This is what `Unit.Team` uses.
- `[Index]` is a **unique** index — inserting a duplicate key throws `UniqueConstraintViolationException`. Use it for identities (a slot, a serial number).

You don't query the index directly — you filter on the field in a normal query (ch.4), and a filter that *targets an indexed field* is served from the index instead of scanning the archetype.

### Evolution — changing a component later

Schemas live *in* the database, so reopening with a changed struct is a real operation, not undefined behaviour. The model is deliberately simple from your side:

1. Change the struct (add a field, widen `int`→`long`, …).
2. Bump the `[Component]` revision (`("Skirmish.Health", 1)` → `2`).
3. Reopen. The engine compares persisted vs runtime schema and migrates the stored data **before** your code runs.

For changes the engine can't infer (a field that needs computing from old data) you supply a migration function. The point for *modeling*: you're free to evolve components; you don't hand-write storage migrations for the common cases. The mechanics are in [04-schema](../in-depth-overview/04-schema.md) of the in-depth reference.

---

## 4. Spatial — querying by geometry

When entities live in space and you ask "what's near here?", a field scan is the wrong tool. A spatial index answers geometric queries — but it indexes an **axis-aligned box** (`AABB2F`), not a point. So a point entity carries a small `Bounds` component whose box collapses onto its position, marked `[SpatialIndex]` (this is the `Bounds` we added in §2):

```csharp
public struct Bounds { [SpatialIndex(2f)] public AABB2F Box; }   // 2f = movement margin
```

Configure the grid as part of the one-line setup — add `ConfigureSpatialGrid` to the `Open` / `AddTyphon` options and it's applied automatically before the archetypes are wired:

```csharp
using var dbe = DatabaseEngine.Open("game.typhon", o => o
    .Register<Position>().Register<Bounds>().RegisterArchetype<Unit>()
    .ConfigureSpatialGrid(new SpatialGridConfig(
        worldMin: Vector2.Zero, worldMax: new Vector2(1000f, 1000f), cellSize: 50f)));
```

Then query by geometry — spatial queries are materialised with `Execute()`:

```csharp
var nearby = tx.Query<Unit>()
               .WhereNearby<Bounds>(centerX, centerY, 0f, 15f)   // x, y, z, radius
               .Execute();
```

> ⚠️ **A convention the analyzer flags, not a runtime-enforced rule.** A `[SpatialIndex]` field should be mutated through the `WriteSpatial` **barrier**, not a plain assignment — `ClusterRef.GetSpan<T>`/`Get<T>` calls that touch a spatial-indexed component get a build-time `TYPHON009` **warning** (not an error, and it doesn't guard `EntityRef.Write` at all — nothing stops a plain write from compiling or running, it just silently skips the spatial-index refresh). To get the warning, reference `Typhon.Analyzers.csproj` as an analyzer too — the same `OutputItemType="Analyzer"` pattern as the generator reference earlier in this chapter — without it the plain write compiles silently and the index goes stale. So a system that moves entities mirrors each point into its box:
>
> ```csharp
> cluster.WriteSpatial(Unit.Bounds, slot, new Bounds { Box = new AABB2F { MinX = x, MaxX = x, MinY = y, MaxY = y } });
> ```

The index is maintained at the **tick fence**: inside the runtime ([ch.5](05-systems.md)) it refreshes every tick automatically; from a bare transaction you run `dbe.WriteTickFence(n)` once after spawning before a spatial query.

Three spatial predicates cover the common needs:

- `WhereNearby<T>(x, y, z, radius)` — everything within a radius (our "enemies near me").
- `WhereInAABB<T>(minX,…, maxX,…)` — everything inside a box (selection rectangle, region trigger).
- `WhereRay<T>(origin…, dir…, maxDist)` — first hits along a ray (line of sight, projectiles).

That's the user-facing surface. *How* it stays fast as thousands of units move every tick (the broad-phase grid + per-component R-tree, margins, rebuild avoidance) is engine internals — see [07-spatial](../in-depth-overview/07-spatial.md) if you're curious; you don't need it to use spatial queries.

---

## 5. Two things the engine quietly does for you

You'll notice this chapter never mentioned memory, files, or B-trees. That's the point — two whole subsystems work on your behalf and ask nothing of you:

- **Storage.** Components live in a memory-mapped, paged store with a cache and crash-safe persistence. You never allocate a page, size a buffer, or write a save file — declaring a component is the entire interaction. Because that store is **disk-backed and paged**, the database can far exceed available RAM: only the hot pages are resident, everything else lives on disk and is paged in on demand — entity count and data size scale with *disk*, not memory. (Every in-memory ECS must fit the whole world in RAM; the one exception in Typhon is *Transient* components, which are RAM-only scratch by design.) Tuning knobs exist for when you scale up; [ch.6](06-operating.md).
- **Indexing.** `[Index]` builds and maintains a B+Tree behind the scenes; spatial indexes maintain their own structure, refreshed at the tick fence. You declare the index; a query that targets that field (or geometry) is served from it. You never touch the tree.

This is the dividing line of the whole guide: you make *modeling decisions*; the engine handles *mechanism*.

---

## 🧭 What's next

You can now design a data model: archetypes, the storage mode per component, indexes, and spatial fields. Next is putting data in and getting it out safely:

- **[Chapter 3 — Changing data](03-transactions.md):** the transaction model in full, durability modes, rollback, and precisely what each storage mode guarantees under a crash.
- **[Chapter 4 — Querying & views](04-querying.md):** the query API in depth, plus reactive views that stay up to date as data changes.

## 🧩 Key concepts & types

**Concepts:** [Component](../key-concepts/component.md) · [Archetype](../key-concepts/archetype.md) · [Storage mode](../key-concepts/storage-mode.md) · [Index](../key-concepts/secondary-index.md) · [Spatial index](../key-concepts/spatial-index.md) · [Schema evolution](../key-concepts/schema-evolution.md) · [EntityLink](../key-concepts/entity-link.md).

**Exact calls:** `[Component(StorageMode = …)]` · `[Index]` / `[Index(AllowMultiple = true)]` · `[SpatialIndex]` on an `AABB2F` field · `Point2F` / `Point3F` · `EntityLink<T>` · `Archetype<TSelf, TParent>` (inheritance) · generated `ReadAll` / `ReadWriteAll` · `ConfigureSpatialGrid` (in the `Open`/`AddTyphon` options) · `dbe.WriteTickFence` · `tx.Query<T>().WhereNearby/WhereInAABB/WhereRay` · `cluster.WriteSpatial`.
