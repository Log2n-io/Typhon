---
uid: guide-modeling
title: '2 — Modeling your world'
description: 'Chapter 1 showed the data loop. This chapter is about design: how to shape your data so the engine works with you — components, storage modes, indexes…'
---

# 2 — Modeling your world

Chapter 1 showed the data loop. This chapter is about **design**: how to shape your data so the engine works *with* you. Five decisions live here — what your components and archetypes are, which **storage mode** each component uses, which fields you **index**, how entities **relate** to each other, and whether you need **spatial** queries. Get these right and the rest of Typhon falls into place; get them wrong and you'll fight the engine.

We start from chapter 1's `Character` and then grow into a full shard — players, guilds, the structures they own, the resources they harvest and the items they craft.

> 📦 **Both models are real code you can read.** The one-archetype seed (`Character`) and the nine-archetype shard shown later both live in [`samples/Typhon.Samples.Swg`](https://github.com/Log2n-io/Typhon/tree/main/samples/Typhon.Samples.Swg) — `Light/` and `Full/` respectively. Every snippet below is taken from types that compile there.

---

## 1. The shape: components, archetypes, entities

The three nouns again, now with the *why*:

- A **component** is a plain `struct` of data — `Transform`, `Wallet`, `Ham`. No behaviour, no engine references.
- An **archetype** is a *fixed set* of components — the shape `Character = Transform + Bounds + Ham + …`. You declare it as a class.
- An **entity** is one instance of an archetype, addressed by an `EntityId`.

💡 **Why a fixed shape per entity?** Because Typhon stores components **archetype-major**: every `Character`'s `Transform` sits contiguously in memory, separate from every other archetype's. Iterating "all characters' positions" is then a linear walk over packed memory — cache-friendly, branch-free, fast. That contiguity is the whole performance bet of ECS, and it's only possible because the shape is fixed at spawn. The cost: an entity can't grow a new component type after it's spawned (you model that with a different archetype, or an *enabled/disabled* component flag).

### Declaring an archetype

```csharp
[Archetype]
public sealed partial class Character : Archetype<Character>
{
    public static readonly Comp<Transform> Transform = Register<Transform>();
    public static readonly Comp<Bounds>    Bounds    = Register<Bounds>();
    public static readonly Comp<Ham>       Ham       = Register<Ham>();
    public static readonly Comp<Faction>   Faction   = Register<Faction>();
    public static readonly Comp<Wallet>    Wallet    = Register<Wallet>();
    public static readonly Comp<Intent>    Intent    = Register<Intent>();
}
```

Each `Register<T>()` adds a component slot and returns a `Comp<T>` handle (`Character.Wallet`) you use everywhere — spawn, read, query. An archetype's identity is its CLR type name; the engine auto-assigns a per-process catalog id and a persisted per-DB routing id, so there is no numeric id for you to pick or keep stable. Two optional arguments are worth knowing: `[Archetype(1, "Resource Deposit")]` gives a **schema revision** and a **display alias** (what tools like the Workbench label it) — useful once your shard has more archetypes than you can eyeball.

### Reading every component at once — generated accessors

In ch.1 you read one component at a time with `e.Read(Character.Wallet)`. For the common "give me everything" case, Typhon's source generator emits typed bulk accessors on any `partial` archetype:

```csharp
var c = Character.ReadAll(tx, id);           // read-only view of all of Character's components
long credits = c.Wallet.Credits;

var m = Character.ReadWriteAll(tx, id);      // mutable view
m.Wallet.Credits -= 10;
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
| `Rollback` reverts it? | yes | no by default — **yes** under `Commit` discipline ([ch.3](03-transactions.md#5-what-each-storage-mode-guarantees-here)) | no |
| Survives a crash? | yes (WAL + checkpoint) | to the last tick (tick-fence WAL) | no (memory only) |
| Cost | highest (~250 ns/write) | low (~40 ns/write) | lowest |

💡 **Why three modes instead of "everything is ACID"?** Because full MVCC isn't free — every Versioned write allocates a new revision and every read may walk a version chain. That's the right price for a wallet or an inventory, where "did this commit?" matters. It's the *wrong* price for a position you overwrite 60 times a second. Typhon lets you pay per component instead of all-or-nothing.

The rule of thumb:

- **Versioned** — state where correctness matters: credits, inventory, ownership, anything you'd be upset to lose or see half-updated.
- **SingleVersion** — hot fields, last-writer-wins, but you still want them to survive a restart: position, health pools, a cached AI cost. Persisted at the tick boundary (you can lose at most the last tick on a crash).
- **Transient** — pure runtime scratch that should *not* survive a restart: an AI wander target, connection state.

> 🎯 **The one heuristic to carry away: don't put per-tick state in `Versioned`.** It's the mistake that costs the most and shows up the least in a small test. A counter that climbs every tick through MVCC is paying ~6× the write cost for isolation nobody reads and history nobody queries. In the sample shard a harvester's `Hopper.Amount` — the resource level that climbs each tick as it extracts — is deliberately **SingleVersion** for exactly this reason, while the player's `Wallet` next to it stays **Versioned** because a credit transfer must be atomic and must not be lost. Same schema, opposite answers, decided by *access pattern*, not by importance.

Applied to `Character`:

```csharp
[Component("Shard.Wallet", 1)]                                             // Versioned is the default
public struct Wallet { public long Credits; }

[Component("Shard.Transform", 1, StorageMode = StorageMode.SingleVersion)]  // hot, durable, no isolation
public struct Transform { public Point2F Pos; public Point2F Vel; }

[Component("Shard.Bounds", 1, StorageMode = StorageMode.SingleVersion)]     // spatial index lives here (§5)
public struct Bounds { [SpatialIndex(2f, Mode = SpatialMode.Dynamic)] public AABB2F Box; }

[Component("Shard.Intent", 1, StorageMode = StorageMode.Transient)]         // per-tick AI scratch
public struct Intent { public Point2F Target; }
```

> ⚠️ **The catch worth knowing now:** a transaction only protects *Versioned* data. An SV/Transient write is visible to everyone the instant it happens and can't be rolled back. Entity creation and destruction are transactional in **all** modes — it's component *data* writes that differ. Chapter 3 spells out exactly what each mode gives up.

A single archetype freely mixes modes — `Character` has all three — because the mode lives on each component *type*, not on the archetype.

---

## 3. Schema: fields, indexes, evolution

### Fields

Component fields are blittable value types: the numeric primitives, `bool`, fixed-width strings (`String64`), spatial types (`Point2F`/`Point3F`, AABBs), and `EntityLink<T>`. That "blittable" constraint is what lets Typhon store and memory-map components without serialization.

> **Two sizing rules that catch newcomers:**
>
> 1. **Only `public` fields count toward a component's size.** Typhon derives the stored layout from the struct's **public** fields (not `sizeof(T)`), so a `private` field is invisible to storage — adding `private int _pad` does **not** change anything.
> 2. **A component must be at least 8 bytes.** Chunk storage has an 8-byte minimum stride. A `Versioned` component with a single 4-byte field (one `int`/`float`) trips `Invalid component/chunk stride: 4 bytes …` at open time. Fix it by adding a **public** field so the struct reaches 8 bytes — or, as `Wallet` does, by picking a type that's already 8 (`long Credits`). `SingleVersion`/`Transient` components clear 8 bytes automatically via their internal per-entity key, so this only bites tiny `Versioned` components.

### Indexes — fast lookup by field value

A plain field can only be found by scanning. Mark it `[Index]` and Typhon maintains a sorted index so you can look it up directly:

```csharp
public struct Faction { [Index(AllowMultiple = true)] public int Value; }   // many characters share a faction
public struct Player  { [Index] public long AccountId;                      // unique — duplicates throw
                        [Index(AllowMultiple = true)] public int Level; }   // many players per level
```

- `[Index(AllowMultiple = true)]` allows many entities to share a value — use it for "every Imperial on the planet".
- `[Index]` is a **unique** index — inserting a duplicate key throws `UniqueConstraintViolationException`. Use it for identities (an account id, a guild name).

You don't query the index directly — you filter on the field in a normal query (ch.4), and a filter that *targets an indexed field* is served from the index instead of scanning the archetype.

💡 **Index what's stable; scan what churns.** An index isn't free: every write that changes an indexed field has to move the entry from the old key to the new one. That's a great trade for a `Faction` (set once, read constantly) and a bad one for a wallet balance that changes every few ticks — which is why `Wallet.Credits` in the sample is deliberately **not** indexed, and wealth questions are answered by a scan. Index the fields you *filter by*, not the fields you *change*.

> ⚠️ **One placement constraint worth knowing early.** `[Index]` on a `String64` field requires that **component** to be `StorageMode.Versioned`. It is a rule about the component, not about the archetype it sits in — you can mix a Versioned component carrying an indexed `String64` into an archetype full of SingleVersion ones. The reason is the shadow buffer: in-place storage modes capture the *old* index key in 8 bytes so the commit can move the entry off it, and a 64-byte key does not fit. Declaring one anyway fails at registration with a message naming the field. In the sample, `Guild.Name` and `Recipe.Name` are `[Index] String64` on Versioned components, while `Player` uses a `long AccountId` as its unique key and leaves `Name` unindexed.

### Evolution — changing a component later

Schemas live *in* the database, so reopening with a changed struct is a real operation, not undefined behaviour. The model is deliberately simple from your side:

1. Change the struct (add a field, widen `int`→`long`, …).
2. Bump the `[Component]` revision (`("Shard.Wallet", 1)` → `2`).
3. Reopen. The engine compares persisted vs runtime schema and migrates the stored data **before** your code runs.

A **storage mode change counts as a schema change** and requires the same revision bump — the mode is fixed for a given (name, revision) pair. That's exactly what happened to the sample's `Hopper` when it moved from Versioned to SingleVersion: `[Component("Swg.Hopper", 2, StorageMode = StorageMode.SingleVersion)]`.

For changes the engine can't infer (a field that needs computing from old data) you supply a migration function. The point for *modeling*: you're free to evolve components; you don't hand-write storage migrations for the common cases. The mechanics are in [04-schema](../in-depth-overview/04-schema.md) of the in-depth reference.

---

## 4. Relationships — when one archetype isn't enough

A seed with one archetype takes you a surprisingly long way, but a real world has *kinds* of things that relate to each other. Typhon gives you three tools, in increasing order of how much you should think before reaching for them.

### Archetype inheritance — a shape that extends another

```csharp
[Archetype(1, "Structure")]                                    // the base — never spawned directly
public class StructureArch : Archetype<StructureArch>
{
    public static readonly Comp<Structure>      Structure = Register<Structure>();
    public static readonly Comp<StructureOwner> Owner     = Register<StructureOwner>();
}

[Archetype(1, "Harvester")]                                    // Harvester = Structure's components + its own
public class HarvesterArch : Archetype<HarvesterArch, StructureArch>
{
    public static readonly Comp<Hopper>            Hopper      = Register<Hopper>();
    public static readonly Comp<HarvesterTarget>   Target      = Register<HarvesterTarget>();
    public static readonly Comp<MaintenanceState>  Maintenance = Register<MaintenanceState>();
    public static readonly Comp<StructurePosition> Position    = Register<StructurePosition>();
}

[Archetype(1, "Factory")]
public class FactoryArch : Archetype<FactoryArch, StructureArch>
{
    public static readonly Comp<FactoryConfig>     Config   = Register<FactoryConfig>();
    public static readonly Comp<PowerSupply>       Power    = Register<PowerSupply>();
    public static readonly Comp<StructurePosition> Position = Register<StructurePosition>();
}
```

`Archetype<TSelf, TParent>` says "I am my parent's components, plus mine". The payoff is **polymorphic querying**: `tx.Query<StructureArch>()` matches every harvester *and* every factory (the whole subtree), while `tx.QueryExact<HarvesterArch>()` matches only the leaf. That's how you write "charge maintenance on every structure this player owns" once instead of once per structure kind.

💡 **Inheritance here is about the query subtree, not code reuse.** There are no virtual methods and nothing to override — a base archetype is a *set of components a family of shapes shares*, and its real job is to give you one name to query the family by. Model a base archetype when you'll genuinely ask questions of the family; don't build a hierarchy just because the nouns feel related.

### `EntityLink<T>` — a typed reference to another entity

```csharp
[Component("Swg.StructureOwner", 1)]
public struct StructureOwner
{
    [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]
    public EntityLink<PlayerArch> Owner;      // this structure belongs to that player
}
```

`EntityLink<T>` stores another entity's id, typed for readability. Three things to know:

- **`OnParentDelete = CascadeAction.Delete`** makes it a real foreign key: destroy the player and their structures go with them, in the same transaction. Without it the link is just a stored id and you clean up yourself.
- **The link needs `[Index]`** to support cascade and reverse lookups ("all structures owned by *this* player").
- **`T` is a contract, not a guarantee.** The implicit conversion from `EntityId` accepts *any* entity — there's no compile-time or runtime check that the target really is a `T`. Inheritance works as you'd expect (`EntityLink<StructureArch>` happily holds a harvester), but a wrong assignment won't be caught for you.

Links can be self-referential — the sample's resource taxonomy is a tree built from `EntityLink<ResourceTypeArch> Parent` on `ResourceType`.

> ⚠️ **Don't chase links in a hot loop.** A foreign key is an *indirection*: resolving one is a random lookup that defeats the contiguous-memory bet §1 is built on. In the sample, every link models a genuine ownership/membership/taxonomy edge that gets walked at event cadence (a player logs in, a structure is placed) — **none** is dereferenced per-entity per-tick. If you find yourself following a link inside a per-tick system over thousands of entities, that's usually a signal to denormalise the value you need into the entity itself.

### `ComponentCollection<T>` — a variable number of child rows

A component is fixed-size, so "a recipe has between one and eight ingredient slots" doesn't fit in a plain field. `ComponentCollection<T>` holds a variable-length list of blittable elements inside a component:

```csharp
public struct RecipeSlot { public int SlotIndex, ClassReq, MinUnits; }   // a plain struct, NOT a component

[Component("Swg.Recipe", 1)]
public struct Recipe
{
    [Field] [Index] public String64 Name;
    [Field] public ComponentCollection<RecipeSlot> Slots;                // 1..8 ingredient slots
}
```

Use it for genuinely-owned child rows with no identity of their own (recipe slots, item affixes). Elements are opaque payloads — they **can't be indexed**, and they can't be foreign keys, which is why `RecipeSlot.ClassReq` above is a plain resource-type id rather than an `EntityLink`. If the children need to be queried or referenced independently, they want to be entities with a link back, not collection elements.

> 📌 **`[Field]` in these snippets.** The Full sample marks every field `[Field]` because its tooling (the shell's schema loader) requires it; the engine itself reads all public fields either way. Both styles work — ch.1's seed omits it.

### Grouping: `[ComponentFamily]`

```csharp
[Component("Swg.Guild", 1)]
[ComponentFamily("Social")]
public struct Guild { … }
```

Purely organisational — it tags a component into a named family (`Social`, `Industry`, `Item`, `World` in the sample) so tools can group them. It has no effect on storage or queries; it makes a 20-component schema navigable in the Workbench.

---

## 5. Spatial — querying by geometry

When entities live in space and you ask "what's near here?", a field scan is the wrong tool. A spatial index answers geometric queries — but it indexes an **axis-aligned box** (`AABB2F`), not a point. So a point entity carries a small `Bounds` component whose box collapses onto its position, marked `[SpatialIndex]`:

```csharp
public struct Bounds { [SpatialIndex(2f, Mode = SpatialMode.Dynamic)] public AABB2F Box; }   // 2f = movement margin
```

Two attribute arguments shape how it's maintained:

- **`Mode`** — `Dynamic` for things that move (characters, players, structures that can be redeeded), `Static` for things that never do. A `Static` entry skips the per-tick fence work entirely; the sample marks resource deposits `Static` and players `Dynamic`.
- **`Category`** — a bitmask tag so broad-phase queries can filter by *kind* before testing geometry ("structures near this point", ignoring the thousands of characters). The sample defines `SwgCategory.Player / Deposit / Structure` and tags each position component accordingly.

Configure the grid as part of the one-line setup — add `ConfigureSpatialGrid` to the `Open` / `AddTyphon` options and it's applied automatically before the archetypes are wired:

```csharp
using var dbe = DatabaseEngine.Open("world-shard.typhon", o => o
    .Register<Transform>().Register<Bounds>()
    .ConfigureSpatialGrid(new SpatialGridConfig(
        worldMin: Vector2.Zero, worldMax: new Vector2(1000f, 1000f), cellSize: 50f)));
```

Then query by geometry — spatial queries are materialised with `Execute()`:

```csharp
var nearby = tx.Query<Character>()
               .WhereNearby<Bounds>(centerX, centerY, 0f, 15f)   // x, y, z, radius
               .Execute();
```

> ⚠️ **A convention the analyzer flags, not a runtime-enforced rule.** A `[SpatialIndex]` field should be mutated through the `WriteSpatial` **barrier**, not a plain assignment — `ClusterRef.GetSpan<T>`/`Get<T>` calls that touch a spatial-indexed component get a build-time `TYPHON009` **warning** (not an error, and it doesn't guard `EntityRef.Write` at all — nothing stops a plain write from compiling or running, it just silently skips the spatial-index refresh). To get the warning, reference `Typhon.Analyzers.csproj` as an analyzer too — the same `OutputItemType="Analyzer"` pattern as the generator reference earlier in this chapter — without it the plain write compiles silently and the index goes stale. So a system that moves entities mirrors each point into its box:
>
> ```csharp
> cluster.WriteSpatial(Character.Bounds, slot, new Bounds { Box = new AABB2F { MinX = x, MaxX = x, MinY = y, MaxY = y } });
> ```

The index is maintained at the **tick fence**: inside the runtime ([ch.5](05-systems.md)) it refreshes every tick automatically; from a bare transaction you run `dbe.WriteTickFence(n)` once after spawning before a spatial query.

Three spatial predicates cover the common needs:

- `WhereNearby<T>(x, y, z, radius)` — everything within a radius (area of interest, aggro range).
- `WhereInAABB<T>(minX,…, maxX,…)` — everything inside a box (selection rectangle, region trigger).
- `WhereRay<T>(origin…, dir…, maxDist)` — first hits along a ray (line of sight, scans).

That's the user-facing surface. *How* it stays fast as thousands of characters move every tick (the broad-phase grid + per-component R-tree, margins, rebuild avoidance) is engine internals — see [07-spatial](../in-depth-overview/07-spatial.md) if you're curious; you don't need it to use spatial queries.

---

## 6. Two things the engine quietly does for you

You'll notice this chapter never mentioned memory, files, or B-trees. That's the point — two whole subsystems work on your behalf and ask nothing of you:

- **Storage.** Components live in a memory-mapped, paged store with a cache and crash-safe persistence. You never allocate a page, size a buffer, or write a save file — declaring a component is the entire interaction. Because that store is **disk-backed and paged**, the database can far exceed available RAM: only the hot pages are resident, everything else lives on disk and is paged in on demand — entity count and data size scale with *disk*, not memory. (Every in-memory ECS must fit the whole world in RAM; the one exception in Typhon is *Transient* components, which are RAM-only scratch by design.) Tuning knobs exist for when you scale up; [ch.6](06-operating.md).
- **Indexing.** `[Index]` builds and maintains a B+Tree behind the scenes; spatial indexes maintain their own structure, refreshed at the tick fence. You declare the index; a query that targets that field (or geometry) is served from it. You never touch the tree.

This is the dividing line of the whole guide: you make *modeling decisions*; the engine handles *mechanism*.

---

## 🧭 What's next

You can now design a data model: archetypes and their hierarchy, the storage mode per component, indexes, relationships, and spatial fields. Next is putting data in and getting it out safely:

- **[Chapter 3 — Changing data](03-transactions.md):** the transaction model in full, durability modes, rollback, and precisely what each storage mode guarantees under a crash.
- **[Chapter 4 — Querying & views](04-querying.md):** the query API in depth, plus reactive views that stay up to date as data changes.

## 🧩 Key concepts & types

**Concepts:** [Component](../key-concepts/component.md) · [Archetype](../key-concepts/archetype.md) · [Storage mode](../key-concepts/storage-mode.md) · [Index](../key-concepts/secondary-index.md) · [Spatial index](../key-concepts/spatial-index.md) · [Schema evolution](../key-concepts/schema-evolution.md) · [EntityLink](../key-concepts/entity-link.md).

**Exact calls:** `[Component(StorageMode = …)]` · `[Index]` / `[Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]` · `[SpatialIndex(margin, Mode = …, Category = …)]` on an `AABB2F` field · `[ComponentFamily]` · `Point2F` / `Point3F` · `EntityLink<T>` · `ComponentCollection<T>` · `Archetype<TSelf, TParent>` (inheritance) · generated `ReadAll` / `ReadWriteAll` · `ConfigureSpatialGrid` (in the `Open`/`AddTyphon` options) · `dbe.WriteTickFence` · `tx.Query<T>().WhereNearby/WhereInAABB/WhereRay` · `cluster.WriteSpatial`.
