---
uid: guide-first-app
title: '1 — Start here: your first Typhon app'
description: 'This chapter gets a working Typhon program in front of you. You''ll declare a tiny data model (the start of a harvesting sim — harvester drones with a position and…'
---

# 1 — Start here: your first Typhon app

This chapter gets a working Typhon program in front of you. You'll declare a tiny data model (the start of a harvesting sim — harvester drones with a position and a cargo hold), open an engine, spawn an entity, read it back, and run a query. No internals, no tuning — just the shape of a real Typhon app.

By the end you'll recognise the five things every Typhon program does: **declare → open → write → read → query.**

---

## The whole program

Here it is end-to-end. We'll walk through it piece by piece below.

```csharp
using System.Numerics;          // Vector2, for the spatial-grid config
using Typhon.Engine;            // DatabaseEngine, EntityId, Point2F, AABB2F, transactions, queries
using Typhon.Schema.Definition; // [Component], [Archetype], Comp<T>
using SwgGuide;                 // the component + archetype types declared at the bottom

// ── 3. Open the engine (once, at startup) ──────────────────────────────
// One call: names the on-disk database (a "swg-guide.typhon" directory in the
// working folder), registers your components, configures the spatial grid the
// [SpatialIndex] on Footprint needs (your archetype self-registers at assembly
// load), and returns a ready-to-use engine. `using var` flushes and releases
// the file lock at scope end.
using var dbe = DatabaseEngine.Open("swg-guide.typhon", o => o
    .Register<Position>()
    .Register<Footprint>()
    .Register<Cargo>()
    .Register<Drift>()
    .Register<Extractor>()
    .ConfigureSpatialGrid(new SpatialGridConfig(Vector2.Zero, new Vector2(1000f, 1000f), cellSize: 50f)));

// ── 4. Spawn an entity (a write — needs a transaction) ─────────────────
EntityId drone;
using (var tx = dbe.CreateQuickTransaction())
{
    drone = tx.Spawn<Harvester>(
        Harvester.Position.Set(new Position { P = new Point2F { X = 10f, Y = 20f } }),
        Harvester.Footprint.Set(new Footprint { Box = new AABB2F { MinX = 10f, MaxX = 10f, MinY = 20f, MaxY = 20f } }),
        Harvester.Cargo.Set(new Cargo { Amount = 250, Capacity = 1000 }),
        Harvester.Drift.Set(new Drift { Dx = 0f, Dy = 0f }),
        Harvester.Extractor.Set(new Extractor { ResourceKind = 1, Rate = 5 }));
    tx.Commit();
}

// ── 5. Read it back (a read — sees a consistent snapshot) ──────────────
using (var tx = dbe.CreateQuickTransaction())
{
    var e     = tx.Open(drone);
    var pos   = e.Read(Harvester.Position);
    var cargo = e.Read(Harvester.Cargo);
    Console.WriteLine($"cargo {cargo.Amount}/{cargo.Capacity} at ({pos.P.X}, {pos.P.Y})");
}

// ── 6. Query (find entities matching a predicate) ──────────────────────
using (var tx = dbe.CreateQuickTransaction())
{
    var filling = tx.Query<Harvester>()
                    .Where<Cargo>(c => c.Amount < c.Capacity)
                    .Execute();
    Console.WriteLine($"{filling.Count} drone(s) still filling");
}

// ── 1. Declare components + archetype ─────────────
// A named namespace keeps a growing project tidy (and is what you'd use in a real app —
// see doc/guide/example). The types could equally sit in the file's global
// namespace; the generator supports both. Top-level statements can't sit in a namespace,
// so the types go in a `namespace { }` block after them.
namespace SwgGuide
{
    [Component("Swg.Position", 1, StorageMode = StorageMode.SingleVersion)]
    public struct Position
    {
        public Point2F P;
    }

    [Component("Swg.Footprint", 1, StorageMode = StorageMode.SingleVersion)]
    public struct Footprint
    {
        [SpatialIndex(2f)] public AABB2F Box;
    }

    [Component("Swg.Cargo", 1, StorageMode = StorageMode.Versioned)]
    public struct Cargo
    {
        public int Amount, Capacity;
    }

    [Component("Swg.Drift", 1, StorageMode = StorageMode.Transient)]
    public struct Drift
    {
        public float Dx, Dy;
    }

    [Component("Swg.Extractor", 1, StorageMode = StorageMode.Versioned)]
    public struct Extractor
    {
        [Index(AllowMultiple = true)] public int ResourceKind;
        public int Rate;
    }

    // ── 2. Declare an archetype (the shape of an entity) ───────────────
    [Archetype]
    public sealed partial class Harvester : Archetype<Harvester>
    {
        public static readonly Comp<Position>  Position  = Register<Position>();
        public static readonly Comp<Footprint> Footprint = Register<Footprint>();
        public static readonly Comp<Cargo>     Cargo     = Register<Cargo>();
        public static readonly Comp<Drift>     Drift     = Register<Drift>();
        public static readonly Comp<Extractor> Extractor = Register<Extractor>();
    }
}
```

> ✅ This program compiles and runs against the current engine (verified). It prints `cargo 250/1000 at (10, 20)` and `1 drone(s) still filling`.

---

## Walking through it

### 1. Components are plain structs

A component is just data. The `[Component("name", revision)]` attribute makes it storable; the name is a stable identity for the schema, the revision is its version (used when you evolve the struct later — see ch.2). Fields are public, blittable value types.

We also write `StorageMode = StorageMode.Versioned` explicitly on `Cargo` and `Extractor`. It's the **default**, so you could omit it — but every component makes this choice, and spelling it out is worth the habit. *Versioned* means full ACID: snapshot-isolated reads, transactional writes, crash-safe. It's the right call for state like a cargo hold's accumulated yield. Hot per-tick data (`Position`) and throwaway scratch (`Drift`) opt into the faster `SingleVersion` / `Transient` modes instead — that's [ch.2](02-modeling.md), which also explains the `[Index]` / `[SpatialIndex]` attributes on a couple of the fields above.

There's no base class, no interface — a component knows nothing about the engine.

### 2. An archetype is the shape of an entity

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

- `[Archetype]` marks it an archetype. Its identity is the CLR type name `Harvester` (or `[Archetype(Name="...")]`); the engine auto-assigns a per-process catalog id and a persisted per-DB routing id — you never pick a number.
- `Archetype<Harvester>` (the class names itself) gives it a compile-time identity.
- Each `Register<T>()` declares a component slot; the static `Comp<T>` handle (`Harvester.Position`) is how you refer to that slot when spawning, reading, and querying.
- **`partial` matters:** Typhon's source generator ships *inside* the `Typhon` package, so it's already active — it's what emits the module-init barrier that self-registers your archetype (above). On a `partial` archetype it *also* generates typed bulk accessors (`Harvester.ReadAll` / `ReadWriteAll`); we don't use those until [ch.2](02-modeling.md), but keeping the class `partial` now costs nothing and lets the generator add them without a later change.

### 3. Open the engine

`DatabaseEngine.Open` is the one-line setup. It names the on-disk database (the path's stem becomes the database name — here a `swg-guide.typhon` directory in the working folder), registers your schema, and hands back a **ready-to-use** engine. `Register<T>()` registers each component type and creates its storage; the archetype needs no registration call — it self-registers at assembly load via a generated module-init barrier, and its slots wire to that storage once its components are registered — so you can `Spawn` immediately, with no separate init call. Do this **once at startup** and hand `dbe` around — there's exactly one engine per process. `using var` disposes it (flushing dirty pages, releasing the file lock) at the end of scope.

> 💡 **Hosting in a DI app?** The same fluent options work through `services.AddTyphon(o => o.DatabaseFile("swg-guide.typhon").Register<Position>()…)`, which composes the engine into your service collection and registers it as an observable resource; `Open()` is the standalone equivalent that owns a private container for you. Under the hood the engine is a composition of independently-configurable subsystems (page cache, allocator, timers) — the `Configure*` methods on the options (`ConfigureStorage`, `ConfigureEngine`, …) let you tune any of them when you need to. (Using `AddTyphon` directly, you don't even need to call `AddLogging()` first — it registers a no-op logging backend for you, and defers to your own if you configured one.)

> ⚠️ **The database is persistent — data survives across runs.** `Open("swg-guide.typhon")` **creates the directory on first run and reopens it (with all its data) on every run after.** A program that unconditionally `Spawn`s on startup therefore *adds another set of entities every time you run it*. For initial (and evolving) data, use **`o.Seed(revision, tx => { … })`** — you register revision-tagged seed steps, and on every open the engine applies the ones this database hasn't run yet, in order, each in its own durable transaction. A fresh database runs them all; an existing one catches up on whatever is new. It's crash-safe (a step whose transaction never commits re-runs on the next open):
>
> ```csharp
> using var dbe = DatabaseEngine.Open("swg-guide.typhon", o => o
>     .Register<Position>().Register<Footprint>().Register<Cargo>().Register<Drift>().Register<Extractor>()
>     .ConfigureSpatialGrid(new SpatialGridConfig(Vector2.Zero, new Vector2(1000f, 1000f), cellSize: 50f))
>     .Seed(1, tx => tx.Spawn<Harvester>(
>         Harvester.Position.Set(new Position { P = new Point2F { X = 10f, Y = 20f } }),
>         Harvester.Footprint.Set(new Footprint { Box = new AABB2F { MinX = 10f, MaxX = 10f, MinY = 20f, MaxY = 20f } }),
>         Harvester.Cargo.Set(new Cargo { Amount = 0, Capacity = 1000 }),
>         Harvester.Drift.Set(new Drift { Dx = 0f, Dy = 0f }),
>         Harvester.Extractor.Set(new Extractor { ResourceKind = 1, Rate = 5 })))
>     .Seed(2, tx => { /* extra data you introduced in revision 2 — existing databases pick this up on next open */ }));
> ```
>
> For lower-level control there's also `dbe.IsNewlyCreated` (true only on the run that created the bundle). For a throwaway demo you can instead delete the directory first: `if (Directory.Exists(dir)) Directory.Delete(dir, true);`.

### 5. Writes go through a transaction

```csharp
using (var tx = dbe.CreateQuickTransaction())
{
    drone = tx.Spawn<Harvester>(
        Harvester.Position.Set(new Position { P = new Point2F { X = 10f, Y = 20f } }),
        Harvester.Footprint.Set(new Footprint { Box = new AABB2F { MinX = 10f, MaxX = 10f, MinY = 20f, MaxY = 20f } }),
        Harvester.Cargo.Set(new Cargo { Amount = 250, Capacity = 1000 }),
        Harvester.Drift.Set(new Drift { Dx = 0f, Dy = 0f }),
        Harvester.Extractor.Set(new Extractor { ResourceKind = 1, Rate = 5 }));
    tx.Commit();
}
```

`CreateQuickTransaction()` is the simplest way to get a transaction (it manages the durability boundary for you — ch.3 covers the explicit form). `Spawn<Harvester>` creates an entity, taking initial component values via `Comp<T>.Set(...)`, and returns its `EntityId`. Nothing is visible to anyone else until `Commit()`.

### 6. Reads see a consistent snapshot

```csharp
var e     = tx.Open(drone);
var pos   = e.Read(Harvester.Position);
var cargo = e.Read(Harvester.Cargo);
```

`tx.Open(id)` resolves the entity; `Read(Harvester.Cargo)` returns that component. Every read happens against a stable point-in-time snapshot, so a concurrent writer never gives you a half-updated view and the read doesn't wait on writers. (In a project with the source generator wired, `Harvester.ReadAll(tx, id)` hands you all components at once — [ch.2](02-modeling.md).)

### 7. Queries find entities

```csharp
var filling = tx.Query<Harvester>()
                .Where<Cargo>(c => c.Amount < c.Capacity)
                .Execute();
```

`Query<Harvester>()` starts a query over all `Harvester` entities; `Where<Cargo>(...)` filters by a component predicate; `Execute()` returns the matching `EntityId`s. This is the tip of the query API — filtering, indexes, reactive views, and statistics-driven planning all live in [ch.4](04-querying.md).

---

## 🔁 What just happened

| Step | Concept | Where it goes deeper |
|---|---|---|
| 1–2 | Components & archetypes — your data model | ch.2 Modeling |
| 3 | One engine per process, built at startup | ch.6 Operating |
| 4 | Register components; archetypes self-register at load | ch.2 Modeling |
| 5 | Writes are transactional | ch.3 Transactions |
| 6 | Reads are snapshot-consistent | ch.3 Transactions |
| 7 | Querying | ch.4 Querying |

You now have the full data loop: **declare → register → write → read → query.** That's a complete (if tiny) Typhon application.

## 🧭 What's next

This program creates and reads data once. A real simulation runs **systems** over its entities **every tick** — that's where Typhon earns its keep, and it's [ch.5](05-systems.md). Before that:

- **[Chapter 2 — Modeling your world](02-modeling.md):** archetypes in depth, indexes for fast lookups, the three **storage modes** (which decide what's ACID, what's fast-and-loose, and what's memory-only), and spatial queries.
- **[Chapter 3 — Changing data](03-transactions.md):** the real transaction model, durability modes, rollback, and exactly what each storage mode guarantees.

## 🧩 Key concepts & types

**Concepts:** [Component](../key-concepts/component.md) · [Archetype](../key-concepts/archetype.md) · [Entity](../key-concepts/entity.md) · [DatabaseEngine](../key-concepts/database-engine.md) · [Transaction](../key-concepts/transaction.md) · [Query](../key-concepts/query.md).

**Exact calls:** `[Component]` / `[Archetype]` · `Archetype<T>` + `Comp<T>` · `DatabaseEngine.Open` (`Register<T>`) · `EntityId` / `EntityRef` (`Open` / `Read`) · `Transaction` (via `CreateQuickTransaction`) · `EcsQuery` (via `tx.Query<Harvester>()`).
