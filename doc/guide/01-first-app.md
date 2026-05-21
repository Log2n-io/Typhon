# 1 — Start here: your first Typhon app

This chapter gets a working Typhon program in front of you. You'll declare a tiny data model (the start of a skirmish game — units with a position and health), open an engine, spawn an entity, read it back, and run a query. No internals, no tuning — just the shape of a real Typhon app.

By the end you'll recognise the five things every Typhon program does: **declare → open → write → read → query.**

---

## The whole program

Here it is end-to-end. We'll walk through it piece by piece below.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Typhon.Engine;            // DatabaseEngine, EntityId, transactions, queries
using Typhon.Schema.Definition; // [Component], [Archetype], Comp<T>

// ── 1. Declare components (plain structs) ──────────────────────────────
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

// ── 2. Declare an archetype (the shape of an entity) ───────────────────
[Archetype(1)]
public sealed partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Health>   Health   = Register<Health>();
}

// ── 3. Build the engine (once, at startup) ─────────────────────────────
var services = new ServiceCollection()
    .AddLogging()
    .AddResourceRegistry()
    .AddMemoryAllocator()
    .AddEpochManager()
    .AddHighResolutionSharedTimer()
    .AddDeadlineWatchdog()
    .AddScopedManagedPagedMemoryMappedFile(o =>
    {
        o.DatabaseName      = "skirmish";
        o.DatabaseDirectory = ".";
    })
    .AddScopedDatabaseEngine(_ => { });

using var provider = services.BuildServiceProvider();
var dbe = provider.GetRequiredService<DatabaseEngine>();

// ── 4. Register your schema (once, after building the engine) ──────────
dbe.RegisterComponentFromAccessor<Position>();
dbe.RegisterComponentFromAccessor<Health>();
Unit.Touch();                  // make the archetype known before wiring
dbe.InitializeArchetypes();    // connect archetype slots to component storage

// ── 5. Spawn an entity (a write — needs a transaction) ─────────────────
EntityId soldier;
using (var tx = dbe.CreateQuickTransaction())
{
    soldier = tx.Spawn<Unit>(
        Unit.Position.Set(new Position(10, 20)),
        Unit.Health.Set(new Health(100, 100)));
    tx.Commit();
}

// ── 6. Read it back (a read — sees a consistent snapshot) ──────────────
using (var tx = dbe.CreateQuickTransaction())
{
    var e   = tx.Open(soldier);
    var pos = e.Read(Unit.Position);
    var hp  = e.Read(Unit.Health);
    Console.WriteLine($"HP {hp.Current}/{hp.Max} at ({pos.X}, {pos.Y})");
}

// ── 7. Query (find entities matching a predicate) ──────────────────────
using (var tx = dbe.CreateQuickTransaction())
{
    var wounded = tx.Query<Unit>()
                    .Where<Health>(h => h.Current < h.Max)
                    .Execute();
    Console.WriteLine($"{wounded.Count} wounded unit(s)");
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

### 3. Build the engine once

The engine is assembled through the standard .NET service collection. The chain registers the engine's moving parts (logging, memory, paging, timers) and finally the `DatabaseEngine` itself. **`AddLogging()` is required** — the engine logs through `ILogger`, and resolution fails without it. You do this **once at startup** and hand `dbe` around — there's exactly one engine per process.

> 💡 **Why a struct-by-struct setup and not `new DatabaseEngine()`?** The engine is a composition of independently-configurable subsystems (page cache, allocator, timers). Wiring them through DI lets you tune or substitute any one of them — and lets the engine register itself as an observable resource. For a real app you'd wrap this chain in one helper and forget about it.

### 4. Register your schema

```csharp
dbe.RegisterComponentFromAccessor<Position>();
dbe.RegisterComponentFromAccessor<Health>();
Unit.Touch();                  // make the archetype known
dbe.InitializeArchetypes();    // wire archetype slots to component storage
```

Before you can spawn anything, the engine has to learn your schema. `RegisterComponentFromAccessor<T>()` registers each component type and creates its storage. `Unit.Touch()` forces the archetype's static initialisation so the engine sees it. `InitializeArchetypes()` then connects every archetype's slots to the right component storage. Do this once, after building the engine and before the first transaction.

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

## 🧩 The types you'll touch

`[Component]` / `[Archetype]` · `Archetype<T>` + `Comp<T>` · `DatabaseEngine` (`RegisterComponentFromAccessor` / `InitializeArchetypes`) · `EntityId` / `EntityRef` (`Open` / `Read`) · `Transaction` (via `CreateQuickTransaction`) · `EcsQuery` (via `tx.Query<Unit>()`).
