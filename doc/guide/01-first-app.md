---
uid: guide-first-app
title: '1 — Start here: your first Typhon app'
description: 'This chapter gets a working Typhon program in front of you. You''ll declare a tiny data model (the start of a skirmish game — units with a position and…'
---

# 1 — Start here: your first Typhon app

This chapter gets a working Typhon program in front of you. You'll declare a tiny data model (the start of a skirmish game — units with a position and health), open an engine, spawn an entity, read it back, and run a query. No internals, no tuning — just the shape of a real Typhon app.

By the end you'll recognise the five things every Typhon program does: **declare → open → write → read → query.**

---

## The whole program

Here it is end-to-end. We'll walk through it piece by piece below.

```csharp
using Typhon.Engine;            // DatabaseEngine, EntityId, transactions, queries
using Typhon.Schema.Definition; // [Component], [Archetype], Comp<T>
using Skirmish;                 // the component + archetype types declared at the bottom

// ── 3. Open the engine (once, at startup) ──────────────────────────────
// One call: names the on-disk database (a "skirmish.typhon" directory in the
// working folder), registers your components + archetype, and returns a
// ready-to-use engine. `using var` flushes and releases the file lock at scope end.
using var dbe = DatabaseEngine.Open("skirmish.typhon", o => o
    .Register<Position>()
    .Register<Health>()
    .RegisterArchetype<Unit>());

// ── 4. Spawn an entity (a write — needs a transaction) ─────────────────
EntityId soldier;
using (var tx = dbe.CreateQuickTransaction())
{
    soldier = tx.Spawn<Unit>(
        Unit.Position.Set(new Position(10, 20)),
        Unit.Health.Set(new Health(100, 100)));
    tx.Commit();
}

// ── 5. Read it back (a read — sees a consistent snapshot) ──────────────
using (var tx = dbe.CreateQuickTransaction())
{
    var e   = tx.Open(soldier);
    var pos = e.Read(Unit.Position);
    var hp  = e.Read(Unit.Health);
    Console.WriteLine($"HP {hp.Current}/{hp.Max} at ({pos.X}, {pos.Y})");
}

// ── 6. Query (find entities matching a predicate) ──────────────────────
using (var tx = dbe.CreateQuickTransaction())
{
    var wounded = tx.Query<Unit>()
                    .Where<Health>(h => h.Current < h.Max)
                    .Execute();
    Console.WriteLine($"{wounded.Count} wounded unit(s)");
}

// ── 1. Declare components + archetype ─────────────
// A named namespace keeps a growing project tidy (and is what you'd use in a real app —
// see doc/guide/example/Model.cs). The types could equally sit in the file's global
// namespace; the generator supports both. Top-level statements can't sit in a namespace,
// so the types go in a `namespace { }` block after them.
namespace Skirmish
{
    [Component("Skirmish.Position", 1, StorageMode = StorageMode.Versioned)]
    public struct Position
    {
        public float X, Y;
        public Position(float x, float y) { X = x; Y = y; }
    }

    [Component("Skirmish.Health", 1, StorageMode = StorageMode.Versioned)]
    public struct Health
    {
        public int Current, Max;
        public Health(int current, int max) { Current = current; Max = max; }
    }

    // ── 2. Declare an archetype (the shape of an entity) ───────────────
    [Archetype(1)]
    public sealed partial class Unit : Archetype<Unit>
    {
        public static readonly Comp<Position> Position = Register<Position>();
        public static readonly Comp<Health>   Health   = Register<Health>();
    }
}
```

> ✅ This program compiles and runs against the current engine (verified). It prints `HP 100/100 at (10, 20)` and `0 wounded unit(s)`.

---

## Walking through it

### 1. Components are plain structs

A component is just data. The `[Component("name", revision)]` attribute makes it storable; the name is a stable identity for the schema, the revision is its version (used when you evolve the struct later — see ch.2). Fields are public, blittable value types.

We also write `StorageMode = StorageMode.Versioned` explicitly. It's the **default**, so you could omit it — but every component makes this choice, and spelling it out is worth the habit. *Versioned* means full ACID: snapshot-isolated reads, transactional writes, crash-safe. It's the right call for gameplay state like health. Hot per-frame data and throwaway scratch can opt into the faster `SingleVersion` / `Transient` modes instead — that's [ch.2](02-modeling.md).

There's no base class, no interface — a component knows nothing about the engine.

### 2. An archetype is the shape of an entity

```csharp
[Archetype(1)]
public sealed partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Health>   Health   = Register<Health>();
}
```

- `[Archetype(1)]` gives it a stable numeric id.
- `Archetype<Unit>` (the class names itself) gives it a compile-time identity.
- Each `Register<T>()` declares a component slot; the static `Comp<T>` handle (`Unit.Position`) is how you refer to that slot when spawning, reading, and querying.
- **`partial` matters:** marking the archetype `partial` lets Typhon's source generator add typed bulk accessors (`Unit.ReadAll` / `ReadWriteAll`). We don't use them in this chapter — they need the generator wired into your project, a [ch.2](02-modeling.md) topic — but adding `partial` now costs nothing and saves a change later.

### 3. Open the engine

`DatabaseEngine.Open` is the one-line setup. It names the on-disk database (the path's stem becomes the database name — here a `skirmish.typhon` directory in the working folder), registers your schema, and hands back a **ready-to-use** engine. `Register<T>()` registers each component type and creates its storage; `RegisterArchetype<Unit>()` makes the archetype's shape known and wires its slots to that storage — so you can `Spawn` immediately, with no separate init call. Do this **once at startup** and hand `dbe` around — there's exactly one engine per process. `using var` disposes it (flushing dirty pages, releasing the file lock) at the end of scope.

> 💡 **Hosting in a DI app?** The same fluent options work through `services.AddTyphon(o => o.DatabaseFile("skirmish.typhon").Register<Position>()…)`, which composes the engine into your service collection and registers it as an observable resource; `Open()` is the standalone equivalent that owns a private container for you. Under the hood the engine is a composition of independently-configurable subsystems (page cache, allocator, timers) — the `Configure*` methods on the options (`ConfigureStorage`, `ConfigureEngine`, …) let you tune any of them when you need to. (Using `AddTyphon` directly, you don't even need to call `AddLogging()` first — it registers a no-op logging backend for you, and defers to your own if you configured one.)

> ⚠️ **The database is persistent — data survives across runs.** `Open("skirmish.typhon")` **creates the directory on first run and reopens it (with all its data) on every run after.** A program that unconditionally `Spawn`s on startup therefore *adds another set of entities every time you run it*. For initial (and evolving) data, use **`o.Seed(revision, tx => { … })`** — you register revision-tagged seed steps, and on every open the engine applies the ones this database hasn't run yet, in order, each in its own durable transaction. A fresh database runs them all; an existing one catches up on whatever is new. It's crash-safe (a step whose transaction never commits re-runs on the next open):
>
> ```csharp
> using var dbe = DatabaseEngine.Open("skirmish.typhon", o => o
>     .Register<Position>().Register<Health>().RegisterArchetype<Unit>()
>     .Seed(1, tx => tx.Spawn<Unit>(Unit.Position.Set(new Position(10, 20)), Unit.Health.Set(new Health(100, 100))))
>     .Seed(2, tx => { /* extra data you introduced in revision 2 — existing databases pick this up on next open */ }));
> ```
>
> For lower-level control there's also `dbe.IsNewlyCreated` (true only on the run that created the bundle). For a throwaway demo you can instead delete the directory first: `if (Directory.Exists(dir)) Directory.Delete(dir, true);`.

### 5. Writes go through a transaction

```csharp
using (var tx = dbe.CreateQuickTransaction())
{
    soldier = tx.Spawn<Unit>(
        Unit.Position.Set(new Position(10, 20)),
        Unit.Health.Set(new Health(100, 100)));
    tx.Commit();
}
```

`CreateQuickTransaction()` is the simplest way to get a transaction (it manages the durability boundary for you — ch.3 covers the explicit form). `Spawn<Unit>` creates an entity, taking initial component values via `Comp<T>.Set(...)`, and returns its `EntityId`. Nothing is visible to anyone else until `Commit()`.

### 6. Reads see a consistent snapshot

```csharp
var e   = tx.Open(soldier);
var pos = e.Read(Unit.Position);
var hp  = e.Read(Unit.Health);
```

`tx.Open(id)` resolves the entity; `Read(Unit.Health)` returns that component. Every read happens against a stable point-in-time snapshot, so a concurrent writer never gives you a half-updated view and the read doesn't wait on writers. (In a project with the source generator wired, `Unit.ReadAll(tx, id)` hands you all components at once — [ch.2](02-modeling.md).)

### 7. Queries find entities

```csharp
var wounded = tx.Query<Unit>()
                .Where<Health>(h => h.Current < h.Max)
                .Execute();
```

`Query<Unit>()` starts a query over all `Unit` entities; `Where<Health>(...)` filters by a component predicate; `Execute()` returns the matching `EntityId`s. This is the tip of the query API — filtering, indexes, reactive views, and statistics-driven planning all live in [ch.4](04-querying.md).

---

## 🔁 What just happened

| Step | Concept | Where it goes deeper |
|---|---|---|
| 1–2 | Components & archetypes — your data model | ch.2 Modeling |
| 3 | One engine per process, built at startup | ch.6 Operating |
| 4 | Register components + archetypes before use | ch.2 Modeling |
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

**Exact calls:** `[Component]` / `[Archetype]` · `Archetype<T>` + `Comp<T>` · `DatabaseEngine.Open` (`Register<T>` / `RegisterArchetype<T>`) · `EntityId` / `EntityRef` (`Open` / `Read`) · `Transaction` (via `CreateQuickTransaction`) · `EcsQuery` (via `tx.Query<Unit>()`).
