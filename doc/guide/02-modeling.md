---
uid: guide-modeling
title: '2 — Modeling your world'
description: 'Chapter 1 showed the data loop. This chapter is about design: how to shape your data so the engine works with you. Four decisions live here — what your…'
---

# 2 — Modeling your world

Chapter 1 showed the data loop. This chapter is about **design**: how to shape your data so the engine works *with* you. Four decisions live here — what your components and archetypes are, which **storage mode** each component uses, which fields you **index**, and whether you need **spatial** queries. Get these right and the rest of Typhon falls into place; get them wrong and you'll fight the engine.

We'll grow the chapter-1 `Harvester` into something a real harvesting sim would use.

---

## 1. The shape: components, archetypes, entities

The three nouns again, now with the *why*:

- A **component** is a plain `struct` of data — `Position`, `Cargo`, `Extractor`. No behaviour, no engine references.
- An **archetype** is a *fixed set* of components — the shape `Harvester = Cargo + Position + …`. You declare it as a class.
- An **entity** is one instance of an archetype, addressed by an `EntityId`.

💡 **Why a fixed shape per entity?** Because Typhon stores components **archetype-major**: every `Harvester`'s `Position` sits contiguously in memory, separate from every other archetype's. Iterating "all drones' positions" is then a linear walk over packed memory — cache-friendly, branch-free, fast. That contiguity is the whole performance bet of ECS, and it's only possible because the shape is fixed at spawn. The cost: an entity can't grow a new component type after it's spawned (you model that with a different archetype, or an *enabled/disabled* component flag).

### Declaring an archetype

```csharp
[Archetype]
public sealed partial class Harvester : Archetype<Harvester>
{
    public static readonly Comp<Position>  Position  = Register<Position>();
    public static readonly Comp<Footprint> Footprint = Register<Footprint>();
    public static readonly Comp<Cargo>     Cargo     = Register<Cargo>();
    public static readonly Comp<Drift>     Drift     = Register<Drift>();
    public static readonly Comp<Extractor> Extractor = Register<Extractor>();
}
```

Each `Register<T>()` adds a component slot and returns a `Comp<T>` handle (`Harvester.Cargo`) you use everywhere — spawn, read, query. An archetype's identity is its CLR type name (or `[Archetype(Name="...")]`); the engine auto-assigns a per-process catalog id and a persisted per-DB routing id, so there is no numeric id for you to pick or keep stable.

**Archetype inheritance** lets one shape extend another:

```csharp
[Archetype]
public sealed partial class Refinery : Archetype<Refinery, Harvester>   // Refinery = Harvester's components + its own
{
    public static readonly Comp<Smelter> Smelter = Register<Smelter>();
}
```

A `Refinery` *is-a* `Harvester` for typed references: `EntityLink<Harvester>` accepts a `Refinery`. Use `EntityLink<T>` to point one entity at another — a typed, self-documenting reference. One caveat: `T` is a contract, not an enforced guarantee — the implicit conversion from `EntityId` accepts *any* entity, with no compile-time or runtime check that it's actually a `T` (or descendant).

```csharp
[Component("Swg.Assignment", 1)]
public struct Assignment { public EntityLink<Harvester> Hauler; }   // stores another entity, typed
```

### Reading every component at once — generated accessors

In ch.1 you read one component at a time with `e.Read(Harvester.Cargo)`. For the common "give me everything" case, Typhon's source generator emits typed bulk accessors on any `partial` archetype:

```csharp
var h = Harvester.ReadAll(tx, id);          // read-only view of all of Harvester's components
int cargo = h.Cargo.Amount;

var m = Harvester.ReadWriteAll(tx, id);     // mutable view
m.Cargo.Amount -= 10;
```

**Where the generator comes from:** it ships *inside* the `Typhon` package, so if you installed Typhon with `dotnet add package Typhon` it's already active — and it's not optional, because the same generator emits the module-init barrier that registers your archetypes. You wire it by hand only when you reference the engine by *project* instead of by package, as an analyzer:

```xml
<ProjectReference Include="path/to/Typhon.Generators.Consumer.csproj"
                  ReferenceOutputAssembly="false" OutputItemType="Analyzer" />
```

`ReadAll` / `ReadWriteAll` are generated for every `partial` archetype; ch.1 just used `e.Read` because it hadn't introduced them yet.

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

💡 **Why three modes instead of "everything is ACID"?** Because full MVCC isn't free — every Versioned write allocates a new revision and every read may walk a version chain. That's the right price for a cargo hold or an inventory, where "did this commit?" matters. It's the *wrong* price for a position you overwrite 60 times a second and never need to roll back. Typhon lets you pay per component instead of all-or-nothing.

The rule of thumb:

- **Versioned** — state where correctness matters: cargo, inventory, score, anything you'd be upset to lose or see half-updated.
- **SingleVersion** — hot fields, last-writer-wins, but you still want them to survive a restart: position, cached AI cost. Persisted at the tick boundary (you can lose at most the last tick on a crash).
- **Transient** — pure runtime scratch that should *not* survive a restart: per-tick drift, targeting temporaries.

Applied to `Harvester`:

```csharp
[Component("Swg.Cargo", 1, StorageMode = StorageMode.Versioned)]         // ACID accumulated yield
public struct Cargo { public int Amount, Capacity; }

[Component("Swg.Position", 1, StorageMode = StorageMode.SingleVersion)]   // hot, durable, no isolation
public struct Position { public Point2F P; }

[Component("Swg.Footprint", 1, StorageMode = StorageMode.SingleVersion)]  // spatial index lives here ([§4](#4-spatial--querying-by-geometry))
public struct Footprint { [SpatialIndex(2f)] public AABB2F Box; }

[Component("Swg.Drift", 1, StorageMode = StorageMode.Transient)]          // per-tick movement scratch
public struct Drift { public float Dx, Dy; }

[Component("Swg.Extractor", 1, StorageMode = StorageMode.Versioned)]
public struct Extractor { [Index(AllowMultiple = true)] public int ResourceKind; public int Rate; }  // many drones per kind
```

> ⚠️ **The catch worth knowing now:** a transaction only protects *Versioned* data. An SV/Transient write is visible to everyone the instant it happens and can't be rolled back. Entity creation and destruction are transactional in **all** modes — it's component *data* writes that differ. Chapter 3 spells out exactly what each mode gives up.

A single archetype freely mixes modes — `Harvester` above has all three — because the mode lives on each component *type*, not on the archetype. (`Footprint` is the spatial mirror of `Position`; [§4](#4-spatial--querying-by-geometry) explains why spatial indexing wants a separate box.)

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
public struct Extractor { [Index(AllowMultiple = true)] public int ResourceKind; }   // many drones share a kind
public struct Serial    { [Index] public int Number; }                               // unique — duplicates throw
```

- `[Index(AllowMultiple = true)]` allows many entities to share a value — use it for "all drones extracting kind 3". This is what `Harvester.Extractor` uses.
- `[Index]` is a **unique** index — inserting a duplicate key throws `UniqueConstraintViolationException`. Use it for identities (a slot, a serial number).

You don't query the index directly — you filter on the field in a normal query (ch.4), and a filter that *targets an indexed field* is served from the index instead of scanning the archetype.

### Evolution — changing a component later

Schemas live *in* the database, so reopening with a changed struct is a real operation, not undefined behaviour. The model is deliberately simple from your side:

1. Change the struct (add a field, widen `int`→`long`, …).
2. Bump the `[Component]` revision (`("Swg.Cargo", 1)` → `2`).
3. Reopen. The engine compares persisted vs runtime schema and migrates the stored data **before** your code runs.

For changes the engine can't infer (a field that needs computing from old data) you supply a migration function. The point for *modeling*: you're free to evolve components; you don't hand-write storage migrations for the common cases. The mechanics are in [04-schema](../in-depth-overview/04-schema.md) of the in-depth reference.

---

## 4. Spatial — querying by geometry

When entities live in space and you ask "what's near here?", a field scan is the wrong tool. A spatial index answers geometric queries — but it indexes an **axis-aligned box** (`AABB2F`), not a point. So a point entity carries a small `Footprint` component whose box collapses onto its position, marked `[SpatialIndex]` (this is the `Footprint` we added in §2):

```csharp
public struct Footprint { [SpatialIndex(2f)] public AABB2F Box; }   // 2f = movement margin
```

Configure the grid as part of the one-line setup — add `ConfigureSpatialGrid` to the `Open` / `AddTyphon` options and it's applied automatically before the archetypes are wired:

```csharp
using var dbe = DatabaseEngine.Open("field.typhon", o => o
    .Register<Position>().Register<Footprint>()
    .ConfigureSpatialGrid(new SpatialGridConfig(
        worldMin: Vector2.Zero, worldMax: new Vector2(1000f, 1000f), cellSize: 50f)));
```

Then query by geometry — spatial queries are materialised with `Execute()`:

```csharp
var nearby = tx.Query<Harvester>()
               .WhereNearby<Footprint>(centerX, centerY, 0f, 15f)   // x, y, z, radius
               .Execute();
```

> ⚠️ **A convention the analyzer flags, not a runtime-enforced rule.** A `[SpatialIndex]` field should be mutated through the `WriteSpatial` **barrier**, not a plain assignment — `ClusterRef.GetSpan<T>`/`Get<T>` calls that touch a spatial-indexed component get a build-time `TYPHON009` **warning** (not an error, and it doesn't guard `EntityRef.Write` at all — nothing stops a plain write from compiling or running, it just silently skips the spatial-index refresh). To get the warning, reference `Typhon.Analyzers.csproj` as an analyzer too — the same `OutputItemType="Analyzer"` pattern as the generator reference earlier in this chapter — without it the plain write compiles silently and the index goes stale. So a system that moves entities mirrors each point into its box:
>
> ```csharp
> cluster.WriteSpatial(Harvester.Footprint, slot, new Footprint { Box = new AABB2F { MinX = x, MaxX = x, MinY = y, MaxY = y } });
> ```

The index is maintained at the **tick fence**: inside the runtime ([ch.5](05-systems.md)) it refreshes every tick automatically; from a bare transaction you run `dbe.WriteTickFence(n)` once after spawning before a spatial query.

Three spatial predicates cover the common needs:

- `WhereNearby<T>(x, y, z, radius)` — everything within a radius (our "drones near a point").
- `WhereInAABB<T>(minX,…, maxX,…)` — everything inside a box (selection rectangle, region trigger).
- `WhereRay<T>(origin…, dir…, maxDist)` — first hits along a ray (line of sight, scans).

That's the user-facing surface. *How* it stays fast as thousands of drones move every tick (the broad-phase grid + per-component R-tree, margins, rebuild avoidance) is engine internals — see [07-spatial](../in-depth-overview/07-spatial.md) if you're curious; you don't need it to use spatial queries.

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
