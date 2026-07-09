---
uid: guide-getting-started
title: Getting Started
description: A five-minute quickstart — install Typhon, declare a component, open the engine, spawn and read an entity, and run your first query.
---

# Getting Started

Typhon is an **in-process, real-time ACID database** with an **ECS** (Entity-Component-System) data model. You declare
**components** (plain `struct`s), group them into **archetypes** (the shape of an entity), **spawn** entities,
**query** them — all from a single .NET library, one engine per process, no server.

This page gets a working program in front of you in about five minutes. For the fuller walkthrough, see
[Start here — your first app](01-first-app.md).

---

## 1. Install

Add the engine package to your .NET 10 project. Typhon is currently published as a **prerelease**:

```bash
dotnet add package Typhon --prerelease
```

Optionally install the `typhon` command-line tool — an interactive shell, script runner, and the host for the
**Workbench** local database GUI (`typhon ui`):

```bash
dotnet tool install --global Typhon.Cli --prerelease
```

Prerelease packages are opt-in — the `--prerelease` flag (or checking "Include prerelease" in your IDE) is required.

---

## 2. Define a component

A component is just data — a plain, blittable `struct` with a `[Component]` attribute. The attribute's name is a
stable schema identity; the number is its revision. `StorageMode.Versioned` is the default (full ACID) and worth
spelling out. Add `[Index]` to a field to make it fast to filter on.

```csharp
using Typhon.Schema.Definition; // [Component], [Field], [Index], [Archetype], Comp<T>

[Component("Skirmish.Position", 1, StorageMode = StorageMode.Versioned)]
public struct Position
{
    public float X, Y;
    public Position(float x, float y) { X = x; Y = y; }
}

[Component("Skirmish.Health", 1, StorageMode = StorageMode.Versioned)]
public struct Health
{
    [Index] public int Current;   // indexed → fast to query on
    public int Max;
    public Health(int current, int max) { Current = current; Max = max; }
}
```

An **archetype** is the fixed shape of an entity — a `partial` class that names itself and registers its component
slots. The static `Comp<T>` handles (`Unit.Position`) are how you refer to each slot when spawning, reading, and
querying.

```csharp
[Archetype(1)]
public sealed partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Health>   Health   = Register<Health>();
}
```

---

## 3. Open the engine, spawn, and read

`DatabaseEngine.Open` is the one-line setup: it names the on-disk database (a `skirmish.typhon` directory in the
working folder), registers your components and archetype, and hands back a ready-to-use engine. Do this **once at
startup**; `using var` flushes and releases the file lock at scope end.

Writes go through a short-lived transaction; reads see a consistent point-in-time snapshot without waiting on writers.

```csharp
using Typhon.Engine;            // DatabaseEngine, EntityId, transactions, queries

using var dbe = DatabaseEngine.Open("skirmish.typhon", o => o
    .Register<Position>()
    .Register<Health>()
    .RegisterArchetype<Unit>());

// Spawn an entity (a write — needs a transaction)
EntityId soldier;
using (var tx = dbe.CreateQuickTransaction())
{
    soldier = tx.Spawn<Unit>(
        Unit.Position.Set(new Position(10, 20)),
        Unit.Health.Set(new Health(90, 100)));
    tx.Commit();
}

// Read it back (a read — sees a consistent snapshot)
using (var tx = dbe.CreateQuickTransaction())
{
    var e   = tx.Open(soldier);
    var pos = e.Read(Unit.Position);
    var hp  = e.Read(Unit.Health);
    Console.WriteLine($"HP {hp.Current}/{hp.Max} at ({pos.X}, {pos.Y})");
}
```

> 💡 **Hosting in a DI app?** The same fluent options work through
> `services.AddTyphon(o => o.DatabaseFile("skirmish.typhon").Register<Position>()…)`, which composes the engine into
> your service collection. `Open()` is the standalone equivalent that owns a private container for you.

---

## 4. Query

`Query<Unit>()` starts a query over all `Unit` entities; `Where<Health>(...)` filters by a component predicate (fast
here because `Health.Current` is indexed); `Count()` returns how many match (`Execute()` would instead hand back the
matching `EntityId`s to iterate).

```csharp
using (var tx = dbe.CreateQuickTransaction())
{
    int wounded = tx.Query<Unit>()
                    .Where<Health>(h => h.Current < h.Max)
                    .Count();
    Console.WriteLine($"{wounded} wounded unit(s)");
}
```

---

## 5. Commit and transactions

Every write enters the engine through a transaction, and nothing is visible to anyone else until `Commit()`.
`CreateQuickTransaction()` is the simplest form — it manages the durability boundary for you. This is the behaviour
of *Versioned* components (the default): transactional writes, snapshot-isolated reads, crash-safe.

```csharp
using (var tx = dbe.CreateQuickTransaction())
{
    var e = tx.OpenMut(soldier);            // mutable handle (vs. read-only tx.Open)
    e.Write(Unit.Health).Current = 100;     // heal to full — an in-place ref write
    tx.Commit();                            // durable + visible here
    // No Commit() → the change is discarded at scope end.
}
```

Hot per-frame data and throwaway scratch can opt into the faster `SingleVersion` / `Transient` storage modes instead —
those relax the transactional model on purpose. See [Changing data: transactions & durability](03-transactions.md).

---

## Next steps

- **[Start here — your first app](01-first-app.md)** — the fuller version of this quickstart, walked through
  piece by piece.
- **[Modeling your world](02-modeling.md)** — archetypes, indexes, the three storage modes, and spatial queries.
- **[Changing data: transactions & durability](03-transactions.md)** — the real transaction model and what survives
  a crash.
- **[User Guide index](README.md)** — the full reading ladder (querying, systems, operating).
- **[In-depth overview](../in-depth-overview/README.md)** — the contributor/power-user reference: struct layouts,
  algorithms, invariants.
- **Runnable example** — every snippet in the guide is mirrored in
  [`doc/guide/example`](https://github.com/Log2n-io/Typhon/tree/main/doc/guide/example);
  `dotnet run --project doc/guide/example` walks the whole arc.
