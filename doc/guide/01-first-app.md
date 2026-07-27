---
uid: guide-first-app
title: '1 — Start here: your first Typhon app'
description: 'This chapter gets a working Typhon program in front of you. You''ll declare a tiny data model (the start of a world shard — characters with a position, health…'
---

# 1 — Start here: your first Typhon app

This chapter gets a working Typhon program in front of you. You'll declare a tiny data model (the start of a **world shard** — characters that roam a planet, carrying health pools, a faction, and a purse of credits), open an engine, spawn an entity, read it back, and run a query. No internals, no tuning — just the shape of a real Typhon app.

By the end you'll recognise the five things every Typhon program does: **declare → open → write → read → query.**

> 📦 **This is the scaffold's data model.** The components and the `Character` archetype below are the same ones `typhon new <name>` emits (see [getting started](getting-started.md)) and that the runnable [`example/`](https://github.com/Log2n-io/Typhon/tree/main/doc/guide/example) project runs. You can type it out, or generate it and read along.
>
> Two differences from the generated project, both cosmetic: this chapter shows everything as **one file** so it can be read top to bottom, whereas `typhon new` splits it into `Character.cs` (the model), `Systems.cs` (the tick loop) and `Program.cs`. And the namespace here is `ShardGuide`; the generated files use `Typhon.Samples.Swg.Shard` for the model and `SwgGuide` for the systems, because both are embedded verbatim from this repository.

---

## The whole program

Here it is end-to-end. We'll walk through it piece by piece below.

```csharp
using System;                   // Console
using System.Numerics;          // Vector2, for the spatial-grid config
using Typhon.Engine;            // DatabaseEngine, EntityId, Point2F, AABB2F, transactions, queries
using Typhon.Schema.Definition; // [Component], [Archetype], Comp<T>
using ShardGuide;               // the component + archetype types declared at the bottom

// ── 3. Open the engine (once, at startup) ──────────────────────────────
// One call: names the on-disk database (a "world-shard.typhon" directory in the
// working folder), registers your components, configures the spatial grid the
// [SpatialIndex] on Bounds needs (your archetype self-registers at assembly
// load), and returns a ready-to-use engine. `using var` flushes and releases
// the file lock at scope end.
using var dbe = DatabaseEngine.Open("world-shard.typhon", o => o
    .Register<Transform>()
    .Register<Bounds>()
    .Register<Ham>()
    .Register<Faction>()
    .Register<Wallet>()
    .Register<Intent>()
    .ConfigureSpatialGrid(new SpatialGridConfig(Vector2.Zero, new Vector2(1000f, 1000f), cellSize: 50f)));

// ── 4. Spawn an entity (a write — needs a transaction) ─────────────────
EntityId scout;
using (var tx = dbe.CreateQuickTransaction())
{
    scout = tx.Spawn<Character>(
        Character.Transform.Set(new Transform { Pos = new Point2F { X = 10f, Y = 20f } }),
        Character.Bounds.Set(new Bounds { Box = new AABB2F { MinX = 10f, MaxX = 10f, MinY = 20f, MaxY = 20f } }),
        Character.Ham.Set(new Ham { Health = 800, MaxHealth = 1000, Action = 700, MaxAction = 1000, Mind = 600, MaxMind = 1000 }),
        Character.Faction.Set(new Faction { Value = Factions.Rebel }),
        Character.Wallet.Set(new Wallet { Credits = 250 }),
        Character.Intent.Set(new Intent()));
    tx.Commit();
}

// ── 5. Read it back (a read — sees a consistent snapshot) ──────────────
using (var tx = dbe.CreateQuickTransaction())
{
    var e   = tx.Open(scout);
    var pos = e.Read(Character.Transform);
    var ham = e.Read(Character.Ham);
    var w   = e.Read(Character.Wallet);
    Console.WriteLine($"{w.Credits} credits, HAM {ham.Health}/{ham.Action}/{ham.Mind} at ({pos.Pos.X}, {pos.Pos.Y})");
}

// ── 6. Query (find entities matching a predicate) ──────────────────────
using (var tx = dbe.CreateQuickTransaction())
{
    var wounded = tx.Query<Character>()
                    .Where<Ham>(h => h.Health < h.MaxHealth)
                    .Execute();
    Console.WriteLine($"{wounded.Count} character(s) hurt");
}

// ── 1. Declare components + archetype ─────────────
// A named namespace keeps a growing project tidy (and is what you'd use in a real app —
// see doc/guide/example). The types could equally sit in the file's global
// namespace; the generator supports both. Top-level statements can't sit in a namespace,
// so the types go in a `namespace { }` block after them.
namespace ShardGuide
{
    // The galaxy's standing factions — plain ints, named for readability.
    public static class Factions
    {
        public const int Neutral = 0, Rebel = 1, Imperial = 2, Hutt = 3;
    }

    [Component("Shard.Transform", 1, StorageMode = StorageMode.SingleVersion)]
    public struct Transform
    {
        public Point2F Pos;
        public Point2F Vel;
    }

    [Component("Shard.Bounds", 1, StorageMode = StorageMode.SingleVersion)]
    public struct Bounds
    {
        [SpatialIndex(2f, Mode = SpatialMode.Dynamic)] public AABB2F Box;
    }

    // HAM — three parallel pools (Health / Action / Mind), drained by exertion, regenerated over time.
    [Component("Shard.Ham", 1, StorageMode = StorageMode.SingleVersion)]
    public struct Ham
    {
        public int Health, Action, Mind;
        public int MaxHealth, MaxAction, MaxMind;
    }

    [Component("Shard.Faction", 1, StorageMode = StorageMode.SingleVersion)]
    public struct Faction
    {
        [Index(AllowMultiple = true)] public int Value;
    }

    [Component("Shard.Wallet", 1, StorageMode = StorageMode.Versioned)]
    public struct Wallet
    {
        public long Credits;
    }

    [Component("Shard.Intent", 1, StorageMode = StorageMode.Transient)]
    public struct Intent
    {
        public Point2F Target;
    }

    // ── 2. Declare an archetype (the shape of an entity) ───────────────
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
}
```

> ✅ This program compiles and runs against the current engine (verified). It prints `250 credits, HAM 800/700/600 at (10, 20)` and `1 character(s) hurt`.

---

## Walking through it

### 1. Components are plain structs

A component is just data. The `[Component("name", revision)]` attribute makes it storable; the name is a stable identity for the schema, the revision is its version (used when you evolve the struct later — see ch.2). Fields are public, blittable value types.

Notice that each component **spells out its storage mode**, and they aren't all the same. That's the single most consequential choice in Typhon and it's made *per component*, so it's worth the habit of writing it even when you pick the default:

- `Wallet` is **Versioned** (the default) — full ACID: snapshot-isolated reads, transactional writes, crash-safe. Right for money.
- `Transform`, `Bounds`, `Ham`, `Faction` are **SingleVersion** — hot state written every tick, still durable, but no MVCC and no rollback.
- `Intent` is **Transient** — per-tick AI scratch that shouldn't survive a restart at all.

The rule in one line: **pay for MVCC where "did this commit?" matters, and nowhere else.** [Ch.2](02-modeling.md) makes the case properly, and also explains the `[Index]` / `[SpatialIndex]` attributes on a couple of the fields above.

There's no base class, no interface — a component knows nothing about the engine.

### 2. An archetype is the shape of an entity

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

- `[Archetype]` marks it an archetype. Its identity is the CLR type name `Character` (or `[Archetype(Name="...")]`); the engine auto-assigns a per-process catalog id and a persisted per-DB routing id — you never pick a number.
- `Archetype<Character>` (the class names itself) gives it a compile-time identity.
- Each `Register<T>()` declares a component slot; the static `Comp<T>` handle (`Character.Transform`) is how you refer to that slot when spawning, reading, and querying.
- **`partial` matters:** Typhon's source generator ships *inside* the `Typhon` package, so it's already active — it's what emits the module-init barrier that self-registers your archetype (above). On a `partial` archetype it *also* generates typed bulk accessors (`Character.ReadAll` / `ReadWriteAll`); we don't use those until [ch.2](02-modeling.md), but keeping the class `partial` now costs nothing and lets the generator add them without a later change.

Note that one archetype freely **mixes storage modes** — `Character` has all three. The mode lives on each component *type*, not on the archetype.

### 3. Open the engine

`DatabaseEngine.Open` is the one-line setup. It names the on-disk database (the path's stem becomes the database name — here a `world-shard.typhon` directory in the working folder), registers your schema, and hands back a **ready-to-use** engine. `Register<T>()` registers each component type and creates its storage; the archetype needs no registration call — it self-registers at assembly load via a generated module-init barrier, and its slots wire to that storage once its components are registered — so you can `Spawn` immediately, with no separate init call. Do this **once at startup** and hand `dbe` around — there's exactly one engine per process. `using var` disposes it (flushing dirty pages, releasing the file lock) at the end of scope.

> 💡 **Hosting in a DI app?** The same fluent options work through `services.AddTyphon(o => o.DatabaseFile("world-shard.typhon").Register<Transform>()…)`, which composes the engine into your service collection and registers it as an observable resource; `Open()` is the standalone equivalent that owns a private container for you. Under the hood the engine is a composition of independently-configurable subsystems (page cache, allocator, timers) — the `Configure*` methods on the options (`ConfigureStorage`, `ConfigureEngine`, …) let you tune any of them when you need to. (Using `AddTyphon` directly, you don't even need to call `AddLogging()` first — it registers a no-op logging backend for you, and defers to your own if you configured one.)

> ⚠️ **The database is persistent — data survives across runs.** `Open("world-shard.typhon")` **creates the directory on first run and reopens it (with all its data) on every run after.** A program that unconditionally `Spawn`s on startup therefore *adds another set of entities every time you run it*. For initial (and evolving) data, use **`o.Seed(revision, tx => { … })`** — you register revision-tagged seed steps, and on every open the engine applies the ones this database hasn't run yet, in order, each in its own durable transaction. A fresh database runs them all; an existing one catches up on whatever is new. It's crash-safe (a step whose transaction never commits re-runs on the next open):
>
> ```csharp
> using var dbe = DatabaseEngine.Open("world-shard.typhon", o => o
>     .Register<Transform>().Register<Bounds>().Register<Ham>()
>     .Register<Faction>().Register<Wallet>().Register<Intent>()
>     .ConfigureSpatialGrid(new SpatialGridConfig(Vector2.Zero, new Vector2(1000f, 1000f), cellSize: 50f))
>     .Seed(1, tx => tx.Spawn<Character>(
>         Character.Transform.Set(new Transform { Pos = new Point2F { X = 10f, Y = 20f } }),
>         Character.Bounds.Set(new Bounds { Box = new AABB2F { MinX = 10f, MaxX = 10f, MinY = 20f, MaxY = 20f } }),
>         Character.Ham.Set(new Ham { Health = 1000, MaxHealth = 1000, Action = 1000, MaxAction = 1000, Mind = 1000, MaxMind = 1000 }),
>         Character.Faction.Set(new Faction { Value = Factions.Neutral }),
>         Character.Wallet.Set(new Wallet { Credits = 0 }),
>         Character.Intent.Set(new Intent())))
>     .Seed(2, tx => { /* extra data you introduced in revision 2 — existing databases pick this up on next open */ }));
> ```
>
> For lower-level control there's also `dbe.IsNewlyCreated` (true only on the run that created the bundle). For a throwaway demo you can instead delete the directory first: `if (Directory.Exists(dir)) Directory.Delete(dir, true);`.

### 5. Writes go through a transaction

```csharp
using (var tx = dbe.CreateQuickTransaction())
{
    scout = tx.Spawn<Character>(
        Character.Transform.Set(new Transform { Pos = new Point2F { X = 10f, Y = 20f } }),
        // … the other five components …
        Character.Wallet.Set(new Wallet { Credits = 250 }));
    tx.Commit();
}
```

`CreateQuickTransaction()` is the simplest way to get a transaction (it manages the durability boundary for you — ch.3 covers the explicit form). `Spawn<Character>` creates an entity, taking initial component values via `Comp<T>.Set(...)`, and returns its `EntityId`. Nothing is visible to anyone else until `Commit()`.

### 6. Reads see a consistent snapshot

```csharp
var e   = tx.Open(scout);
var pos = e.Read(Character.Transform);
var w   = e.Read(Character.Wallet);
```

`tx.Open(id)` resolves the entity; `Read(Character.Wallet)` returns that component. Every read happens against a stable point-in-time snapshot, so a concurrent writer never gives you a half-updated view and the read doesn't wait on writers. (In a project with the source generator wired, `Character.ReadAll(tx, id)` hands you all components at once — [ch.2](02-modeling.md).)

### 7. Queries find entities

```csharp
var wounded = tx.Query<Character>()
                .Where<Ham>(h => h.Health < h.MaxHealth)
                .Execute();
```

`Query<Character>()` starts a query over all `Character` entities; `Where<Ham>(...)` filters by a component predicate; `Execute()` returns the matching `EntityId`s. This is the tip of the query API — filtering, indexes, reactive views, and statistics-driven planning all live in [ch.4](04-querying.md).

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

- **[Chapter 2 — Modeling your world](02-modeling.md):** archetypes in depth, indexes for fast lookups, the three **storage modes** (which decide what's ACID, what's fast-and-loose, and what's memory-only), relationships between entities, and spatial queries.
- **[Chapter 3 — Changing data](03-transactions.md):** the real transaction model, durability modes, rollback, and exactly what each storage mode guarantees.

## 🧩 Key concepts & types

**Concepts:** [Component](../key-concepts/component.md) · [Archetype](../key-concepts/archetype.md) · [Entity](../key-concepts/entity.md) · [DatabaseEngine](../key-concepts/database-engine.md) · [Transaction](../key-concepts/transaction.md) · [Query](../key-concepts/query.md).

**Exact calls:** `[Component]` / `[Archetype]` · `Archetype<T>` + `Comp<T>` · `DatabaseEngine.Open` (`Register<T>`) · `EntityId` / `EntityRef` (`Open` / `Read`) · `Transaction` (via `CreateQuickTransaction`) · `EcsQuery` (via `tx.Query<Character>()`).
