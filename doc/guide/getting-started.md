---
uid: guide-getting-started
title: Getting Started
description: A five-minute quickstart — scaffold a runnable Typhon app with `typhon new`, run it, and open its profiler trace in the Workbench; or build one by hand.
---

# Getting Started

Typhon is an **in-process, real-time ACID database** with an **ECS** (Entity-Component-System) data model. You declare
**components** (plain `struct`s), group them into **archetypes** (the shape of an entity), **spawn** entities,
**query** them — all from a single .NET library, one engine per process, no server.

There are two ways to get going, both about five minutes: **scaffold** a working project in one command (fastest), or
**build one by hand** to see every piece. Start with the scaffold; read the by-hand walkthrough when you want to
understand what it generated.

---

## The fastest start: `typhon new`

Install the `typhon` command-line tool — a .NET global tool that also hosts the **Workbench**, a local database GUI:

```bash
dotnet tool install --global Typhon.Cli --prerelease
```

Scaffold a project. It emits a runnable starter that models a small world of roaming **harvester drones** (the SWG Light
sample) with a tick loop and profiling already wired:

```bash
typhon new MyApp
cd MyApp
```

Run it. The first run restores the `Typhon` package from NuGet, spawns drones, ticks the runtime, and — because
`typhon.telemetry.json` turns the profiler on — writes a profiler trace to `./captures/`:

```bash
dotnet run
```
```
== ch.1 — spawn, read, query ==
probe drone: cargo 250/1000 at (10, 20)
...
after run: 8 drones (deployed 1), probe cargo 1000

OK — ran end to end; profiler trace written: ./captures/guide.typhon-trace (244,884 bytes)
```

Open that trace in the Workbench:

```bash
typhon ui --open-latest
```

That's the whole loop — **scaffold → run → profile → inspect**. The generated project is small:

| File | What it is |
|------|------------|
| `Harvester.cs` | The data model — the `Harvester` archetype and its components (one per storage mode + spatial + index). |
| `Systems.cs` | The tick-loop systems: spawn drones, roam, keep the spatial index coherent, accumulate cargo. |
| `Program.cs` | Opens the engine, walks the API (spawn / read / transact / query / view), then runs the runtime. |
| `typhon.telemetry.json` | Turns on config-driven profiling (the engine self-wires it — no code needed). |

Edit the components and systems to model your own world. The chapters below explain every piece.

### Turn profiling on and off

Profiling is **config-driven** — a `typhon.telemetry.json` beside your app, which the engine reads at startup with no
code from you. The CLI authors that file so you never hand-edit nested JSON:

```bash
typhon telemetry trace captures/app.typhon-trace   # set the profiler trace output file
typhon telemetry enable CpuSampling                # add a capture channel
typhon telemetry edit                              # full-screen interactive flag editor
typhon telemetry trace --clear                     # stop writing a trace
```

---

## Or build it by hand

Prefer to see every piece wired yourself? Here's the same engine, hand-built. Add the package to a .NET 10 project:

```bash
dotnet add package Typhon --prerelease
```

Prerelease packages are opt-in — the `--prerelease` flag (or checking "Include prerelease" in your IDE) is required.

### 1. Define a component

A component is just data — a plain, blittable `struct` with a `[Component]` attribute. The attribute's name is a
stable schema identity; the number is its revision. `StorageMode.Versioned` is the default (full ACID) and worth
spelling out. Add `[Index]` to a field to make it fast to filter on.

```csharp
using Typhon.Schema.Definition; // [Component], [Field], [Index], [Archetype], Comp<T>

namespace Swg;   // component + archetype types must live in a namespace, not the global one

[Component("Swg.Position", 1, StorageMode = StorageMode.Versioned)]
public struct Position
{
    public float X, Y;
    public Position(float x, float y) { X = x; Y = y; }
}

[Component("Swg.Cargo", 1, StorageMode = StorageMode.Versioned)]
public struct Cargo
{
    [Index] public int Amount;    // indexed → fast to query on
    public int Capacity;
    public Cargo(int amount, int capacity) { Amount = amount; Capacity = capacity; }
}
```

An **archetype** is the fixed shape of an entity — a `partial` class that names itself and registers its component
slots. The static `Comp<T>` handles (`Harvester.Position`) are how you refer to each slot when spawning, reading, and
querying.

```csharp
[Archetype]
public sealed partial class Harvester : Archetype<Harvester>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Cargo>    Cargo    = Register<Cargo>();
}
```

### 2. Open the engine, spawn, and read

`DatabaseEngine.Open` is the one-line setup: it names the on-disk database (a `swg.typhon` directory in the
working folder), registers your components (your archetype self-registers at assembly load), and hands back a
ready-to-use engine. Do this **once at startup**; `using var` flushes and releases the file lock at scope end.

Writes go through a short-lived transaction; reads see a consistent point-in-time snapshot without waiting on writers.

```csharp
using Typhon.Engine;            // DatabaseEngine, EntityId, transactions, queries

using var dbe = DatabaseEngine.Open("swg.typhon", o => o
    .Register<Position>()
    .Register<Cargo>());

// Spawn an entity (a write — needs a transaction)
EntityId drone;
using (var tx = dbe.CreateQuickTransaction())
{
    drone = tx.Spawn<Harvester>(
        Harvester.Position.Set(new Position(10, 20)),
        Harvester.Cargo.Set(new Cargo(250, 1000)));
    tx.Commit();
}

// Read it back (a read — sees a consistent snapshot)
using (var tx = dbe.CreateQuickTransaction())
{
    var e     = tx.Open(drone);
    var pos   = e.Read(Harvester.Position);
    var cargo = e.Read(Harvester.Cargo);
    Console.WriteLine($"cargo {cargo.Amount}/{cargo.Capacity} at ({pos.X}, {pos.Y})");
}
```

> 💡 **Hosting in a DI app?** The same fluent options work through
> `services.AddTyphon(o => o.DatabaseFile("swg.typhon").Register<Position>()…)`, which composes the engine into
> your service collection. `Open()` is the standalone equivalent that owns a private container for you.

### 3. Query

`Query<Harvester>()` starts a query over all `Harvester` entities; `Where<Cargo>(...)` filters by a component predicate
— `c.Amount < c.Capacity` compares two fields, so this is a broad scan; for index-backed filtering use `WhereField`
(see [ch.4](04-querying.md)); `Count()` returns how many match (`Execute()` would instead hand back the matching
`EntityId`s to iterate).

```csharp
using (var tx = dbe.CreateQuickTransaction())
{
    int filling = tx.Query<Harvester>()
                    .Where<Cargo>(c => c.Amount < c.Capacity)
                    .Count();
    Console.WriteLine($"{filling} drone(s) still filling");
}
```

### 4. Commit and transactions

Every write enters the engine through a transaction, and nothing is visible to anyone else until `Commit()`.
`CreateQuickTransaction()` is the simplest form — it manages the durability boundary for you. This is the behaviour
of *Versioned* components (the default): transactional writes, snapshot-isolated reads, crash-safe.

```csharp
using (var tx = dbe.CreateQuickTransaction())
{
    var e = tx.OpenMut(drone);              // mutable handle (vs. read-only tx.Open)
    e.Write(Harvester.Cargo).Amount = 1000; // fill to capacity — an in-place ref write
    tx.Commit();                            // durable + visible here
    // No Commit() → the change is discarded at scope end.
}
```

Hot per-frame data and throwaway scratch can opt into the faster `SingleVersion` / `Transient` storage modes instead —
those relax the transactional model on purpose. See [Changing data: transactions & durability](03-transactions.md).

---

## Next steps

- **[Start here — your first app](01-first-app.md)** — the fuller walkthrough, piece by piece (it *is* the scaffold's `Program.cs`).
- **[Modeling your world](02-modeling.md)** — archetypes, indexes, the three storage modes, and spatial queries.
- **[Changing data: transactions & durability](03-transactions.md)** — the real transaction model and what survives
  a crash.
- **[Systems & the tick loop](05-systems.md)** — the runtime that ticks your world in parallel (the scaffold's `Systems.cs`).
- **[User Guide index](README.md)** — the full reading ladder.
- **[In-depth overview](../in-depth-overview/README.md)** — the contributor/power-user reference: struct layouts,
  algorithms, invariants.
- **Runnable example** — the scaffold's exact sources live in
  [`doc/guide/example`](https://github.com/Log2n-io/Typhon/tree/main/doc/guide/example);
  `dotnet run --project doc/guide/example` walks the whole arc.
