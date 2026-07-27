---
uid: guide-systems
title: '5 — Systems & the tick loop'
description: 'Everything so far you drove by hand: you opened a transaction, did one thing, let it go. A real simulation or game server doesn''t work that way — it runs…'
---

# 5 — Systems & the tick loop

Everything so far you drove **by hand**: you opened a transaction, did one thing, let it go. A real simulation or game server doesn't work that way — it runs the *same logic over all its data, continuously, every frame*. That's what the **runtime** gives you: a metronome that beats at a fixed rate, and a graph of **systems** that run over your entities on every beat — in parallel, with the per-tick transaction plumbing handled for you.

This is the chapter where Typhon stops being a database you poke and becomes an engine that runs your world. It's the densest in the guide, and the most important if you're building a server.

> 📌 **The runtime is recommended, but optional.** Everything in chapters 1–4 works without it — if your app is request/response, a batch job, or embeds Typhon inside an existing loop (a game engine's own frame, say), you can keep driving the engine directly through transactions and never declare a single system. The runtime is the *recommended* path when you have continuous, tick-driven logic to run in parallel; it's not a requirement for using Typhon.

Two ideas carry the whole chapter:

- **A tick is one frame of your simulation.** On each tick the runtime runs your systems once, then advances. 60 ticks a second by default.
- **A system is a unit of logic with declared data access.** You say *what it reads and writes*; the engine works out *what can run at the same time*.

> 📌 **The fixed cadence is for games — the parallelism is for everyone.** Ticks shine in game and simulation development, where a steady real-time beat (60 Hz, say) is exactly the cadence you want to stick to. But that real-time pacing is a *choice*, not a requirement. If you only need Typhon's runtime to **parallelise computation** — a dependency-aware graph of systems fanned across all cores — you can use the very same machinery without pacing to a wall clock: run the loop as fast as the work completes (a high `BaseTickRate`, where each tick is simply "one parallel pass" rather than a clock you wait on). Read the rest of this chapter for the systems-and-parallelism model; treat the fixed-cadence parts as the game-dev specialisation.

---

## 1. The model: tick → systems

Every tick, the runtime walks a fixed structure you declare once at startup:

```
Tick        one frame. A metronome fires at BaseTickRate (default 60 Hz).
 └ Track    a sequential stage. Tracks run one after another, in order.
    └ DAG   a dependency graph of systems. DAGs in a track are independent.
       └ Phase   an ordered bucket inside a DAG (Input → Simulation → …).
          └ System   your logic. Runs once per tick (unless throttled).
```

You'll spend almost all your time at the **system** level. The levels above exist so the engine knows what ordering is mandatory (phases, tracks) and what's free to parallelise (everything else). Three tracks always exist; your code lives on the **Public** track, and the engine owns the two around it (`Engine-Pre`, `Engine-Post`) for its own tick-boundary work.

### The per-tick discipline — handled for you

This is the rule that makes the whole thing safe, and you mostly *don't write it*:

- The runtime opens **one `UnitOfWork` per tick** and flushes it at tick end.
- Each `CallbackSystem` / `QuerySystem` gets its **own `Transaction`**, created on the worker thread that runs it, and **committed and disposed by the scheduler** when the system returns.
- Your system body just *uses* `ctx.Transaction` (or `ctx.Accessor`, §5). It never calls `Commit` or `Dispose`.

> 💡 **Why you must not commit your own transaction.** The scheduler owns the lifecycle so it can enforce the invariants from [ch.3](03-transactions.md) across many systems at once: one consistent snapshot per system, one durability cycle per tick, single-thread affinity (the transaction was made *on this worker* and must die there). If you committed it yourself, you'd be fighting the scheduler for ownership of the tick's atomicity. The deal is simple: you write logic, the engine writes the commit. The one escape hatch — a write that must be durable *right now*, independent of the tick — is `ctx.CreateSideTransaction(...)` ([§6](#6-building-and-running-the-runtime)).

---

## 2. Writing a system

A system is a class: derive from one of three bases, implement `Configure` (declare it) and `Execute` (run it). Three shapes, picked by *what the work looks like*:

| Base | Use it for | Gets `ctx.Entities`? | Transaction |
|---|---|---|---|
| **`CallbackSystem`** | non-entity work: draining input, timers, global state, spawning, cross-entity transfers | no | one per tick |
| **`QuerySystem`** | "do something to every entity in a set" — the workhorse | yes (a View) | one (or one per chunk, parallel) |
| **`PipelineSystem`** | bulk data-parallel work that isn't per-entity (SIMD sweeps, reductions) | no | none — separate access model |

Most simulation logic is `QuerySystem`. `CallbackSystem` is for the edges (input in, render out). `PipelineSystem` is advanced and rare — reach for it only when the per-tick transactional model is in your way.

### A CallbackSystem — spawn new characters

```csharp
internal sealed class SpawnSystem : CallbackSystem
{
    private int _next;

    protected override void Configure(SystemBuilder b) => b
        .Name("Spawn")
        .Phase(Phase.Input)
        .Writes<Transform>().Writes<Bounds>().Writes<Ham>().Writes<Faction>().Writes<Wallet>().Writes<Intent>();

    protected override void Execute(TickContext ctx)
    {
        if (ctx.TickNumber == 0 || ctx.TickNumber % 30 != 0) return;   // periodic spawn
        int i = _next++;
        float x = 100f + (i * 37 % 800), y = 100f + (i * 53 % 800);
        ctx.Transaction.Spawn<Character>(
            Character.Transform.Set(new Transform { Pos = new Point2F { X = x, Y = y }, Vel = new Point2F { X = 4f, Y = 2f } }),
            Character.Bounds.Set(new Bounds { Box = new AABB2F { MinX = x, MaxX = x, MinY = y, MaxY = y } }),
            Character.Ham.Set(new Ham { Health = 50, MaxHealth = 100, Action = 50, MaxAction = 100, Mind = 50, MaxMind = 100 }),
            Character.Faction.Set(new Faction { Value = i % 3 }),
            Character.Wallet.Set(new Wallet { Credits = 100 }),
            Character.Intent.Set(new Intent()));
        // no Commit — the scheduler commits this system's transaction
    }
}
```

### A QuerySystem — move every character

`QuerySystem` needs an **input View** — a live `EcsView` ([ch.4](04-querying.md)) that supplies the entity set. You create it once, hold it, and hand the system a factory:

```csharp
internal sealed class MoveSystem : QuerySystem
{
    private const float World = 1000f;
    private readonly EcsView<Character> _characters;
    public MoveSystem(EcsView<Character> characters) { _characters = characters; }

    protected override void Configure(SystemBuilder b) => b
        .Name("Move")
        .Phase(Phase.Simulation)
        .Input(() => _characters)
        .Parallel()                       // fan across workers (§5)
        .Writes<Transform>();             // declared access (§3)

    protected override void Execute(TickContext ctx)
    {
        foreach (EntityId id in ctx.Entities)             // the filtered set for this chunk
        {
            var e = ctx.Accessor.OpenMut(id);             // per-worker accessor (§5)
            ref var t = ref e.Write(Character.Transform);
            t.Pos = new Point2F { X = Wrap(t.Pos.X + t.Vel.X * ctx.DeltaTime),
                                  Y = Wrap(t.Pos.Y + t.Vel.Y * ctx.DeltaTime) };
        }
    }

    private static float Wrap(float v) => v < 0f ? v + World : (v > World ? v - World : v);
}
```

`ctx.Entities` is the system's input — the View's entities (or just the *changed* ones, when the system declares a change filter). `ctx.DeltaTime` is seconds since the last tick: multiply rates by it and your simulation runs at the same speed regardless of tick rate.

> 💡 **Why a class, and why declare a View up front?** The class isn't ceremony — `Configure` is where you hand the engine the metadata it needs *before the first tick*: the input set, the access set, the ordering. With that in hand the engine builds the parallel schedule once and never re-derives it. (There's also a terser lambda form — `dag.QuerySystem("name", ctx => …, input: () => view)` — fine for a trivial system, but it can't carry `Reads`/`Writes`, so you lose automatic ordering. Prefer the class form for anything real.)

---

## 3. Declaring access — the engine schedules for you

This is the part that earns the runtime its keep. In `Configure` you declare what each system touches:

```csharp
b.Reads<Faction>()         // I read Faction
 .Writes<Transform>()      // I write Transform
 .ReadsResource("Grid")    // I read a named non-component resource
 .WritesEvents(fullQueue)  // I publish to an event queue
```

From those declarations across *all* systems in a DAG, the engine **derives the execution graph** and **rejects unsafe schedules at build time** — before a single tick runs. Two systems that write the same component in the same phase, with no ordering between them, is a hard error, not a race you discover in production.

The read variants are the interesting part, because they answer *"which version of the data do I want?"*:

| Declaration | Meaning | Effect on ordering |
|---|---|---|
| `Reads<T>` | I read T, and no one writes it this phase | error if a same-phase writer exists — pick one of the two below |
| `ReadsFresh<T>` | I want **this tick's** value | ordered **after** the writer (writer → me) |
| `ReadsSnapshot<T>` | **last tick's** value is fine | ordered **before** the writer — so we can run **concurrently** |

> 💡 **Why three kinds of read?** Because "do I need the freshest value?" is a real design choice with a real cost. `ReadsFresh` is correctness when you depend on this tick's write — but it serialises you behind the writer. `ReadsSnapshot` says *"yesterday's value is good enough"* — and that one word lets the engine run your reader **alongside** the writer instead of after it, which is often the difference between a tick fitting in budget and not. One restriction: `ReadsSnapshot<T>` only applies to a **Versioned** `T` — SingleVersion and Transient have no revision history to hand out a stale-but-consistent copy of, and the engine rejects the declaration at `Build()` time if you try (rule CM-04 / `runtime-scheduling.md` AC-05).
>
> Our shard's `Transform` is SingleVersion ([ch.2](02-modeling.md)), so a system can't `ReadsSnapshot<Transform>` — SV keeps no revision history to hand out a stale-but-consistent copy of. `BoundsSyncSystem`, which must see *this tick's* positions to keep the spatial index honest, therefore declares `ReadsFresh<Transform>` and is ordered behind `Move`. `RegenSystem` sidesteps the question entirely: it never touches `Transform` — it only writes `Ham` — so it has **no declared conflict** with `MoveSystem` and runs alongside it for free.

```csharp
internal sealed class RegenSystem : QuerySystem
{
    private readonly EcsView<Character> _characters;
    public RegenSystem(EcsView<Character> characters) { _characters = characters; }

    protected override void Configure(SystemBuilder b) => b
        .Name("Regen")
        .Phase(Phase.Simulation)
        .Input(() => _characters)
        .Parallel()
        .Writes<Ham>();                   // SingleVersion write → lock-free per-worker accessor (see §5)

    protected override void Execute(TickContext ctx)
    {
        foreach (EntityId id in ctx.Entities)
        {
            var e = ctx.Accessor.OpenMut(id);
            ref var h = ref e.Write(Character.Ham);
            h.Health = Math.Min(h.MaxHealth, h.Health + 1);
            h.Action = Math.Min(h.MaxAction, h.Action + 2);   // Action recovers fastest
            h.Mind   = Math.Min(h.MaxMind,   h.Mind + 1);
        }
    }
}
```

Beyond components, you can declare **resources** (`ReadsResource`/`WritesResource` — for shared non-component state like a spatial grid handle) and **events** (`WritesEvents`/`ReadsEvents` — typed queues that create a producer→consumer edge). All of it feeds the same one-time graph derivation.

---

## 4. Ordering: phases and explicit edges

Access declarations handle *data* ordering. For *structural* ordering you have two tools:

- **Phases** — a DAG-local total order. Everything in `Input` finishes before anything in `Simulation` starts. Typhon ships `Input`, `Simulation`, `Output`, `Cleanup`; you can define your own. Use phases for coarse "all input before all simulation before all rendering" structure.
- **`After` / `Before` / `AfterAll`** — an explicit edge between two named systems in the same DAG. Use it to disambiguate two writers, or to force a specific order the access model can't infer.

You declare the phase list when you create the DAG, and the engine slots each system into its phase:

```csharp
schedule.PublicTrack
    .DeclareDag("Sim")
    .Phases(Phase.Input, Phase.Simulation)
    .Add(new SpawnSystem())                  // Phase.Input
    .Add(new MoveSystem(characters))          // Phase.Simulation
    .Add(new BoundsSyncSystem(characters))    // Phase.Simulation — after Move (keeps the spatial index coherent)
    .Add(new RegenSystem(characters))         // Phase.Simulation — parallel with Move
    .Add(new WanderSystem(characters))        // Phase.Simulation — after Move (steers next tick's velocity)
    .Add(new TradeSystem());                 // Phase.Simulation — the Versioned economy, at event cadence
```

`BoundsSyncSystem` is a cluster-native `QuerySystem` that mirrors each moved position into the spatial `Bounds` box through the `WriteSpatial` barrier ([ch.2 §5](02-modeling.md#5-spatial--querying-by-geometry)). It declares `.After("Move").ReadsFresh<Transform>().Writes<Bounds>()`, so it runs once Move has moved everything this tick.

> 💡 **Phases are a contract, not a barrier wall.** Two systems in the same phase run concurrently *unless* their access declarations conflict. Two systems in adjacent phases only serialise where they actually touch the same data — a phase-N+1 system with no conflict against a phase-N system can overlap it. So you get the readability of "input, then simulate, then render" without paying for a hard stop between every stage. Order what must be ordered; let the engine parallelise the rest.

---

## 5. Running in parallel

A `QuerySystem` with `.Parallel()` is **chunked across the worker pool**: the engine splits the entity set into chunks and runs your `Execute` on several workers at once, each handling a slice of `ctx.Entities`. You write the *same* single-threaded body — the engine fans it out.

How a parallel system touches data depends on what it **writes**:

- **Non-Versioned writes (SingleVersion / Transient)** — the fast path. Each worker gets a per-worker **`ctx.Accessor`** (an `EntityAccessor`) with warm caches and **zero per-entity locking**, riding on a single frozen snapshot (a `PointInTimeAccessor`). This is how `MoveSystem` and `RegenSystem` write `Transform` and `Ham` (both SV) across all cores with no contention.
- **Versioned writes** — declare `.WritesVersioned()`. The engine falls back to a **per-chunk `Transaction`** (via `ctx.Transaction`) because Versioned writes need the full MVCC machinery. Correct, but heavier — which is exactly why hot, overwrite-often data like position is usually SingleVersion ([ch.2](02-modeling.md)).

```csharp
b.Input(() => _characters).Parallel()
 .Writes<Transform>();                       // SV write → ctx.Accessor, lock-free

b.Input(() => _characters).Parallel().WritesVersioned()
 .Writes<Wallet>();                          // Versioned write → per-chunk ctx.Transaction
```

> 💡 **A cross-entity transfer wants a `CallbackSystem`, not a parallel one.** `ctx.Accessor` refuses a Versioned write outright, and even `.WritesVersioned()` gives each *chunk* its own transaction — fine for "update each entity independently", wrong for "move credits from A to B", where both sides must land in the **same** transaction to be atomic. That is why the sample's `TradeSystem` is a serial `CallbackSystem` driving `ctx.Transaction`, and why it fires **at event cadence** (`if (ctx.TickNumber % 10 != 0) return;`) instead of every tick. A Versioned write costs roughly 6x a SingleVersion one — so the economy beats on a slower drum while movement, HAM and AI run hot every tick. That one decision is often the difference between a tick loop that fits its budget and one that does not.

> 💡 **The zero-lock read is the whole point.** Under the hood, parallel reads share one `PointInTimeAccessor` — a single frozen TSN that every worker reads against without taking a single per-entity lock, because [snapshot isolation](03-transactions.md) guarantees the snapshot can't move under them. That's how "iterate a million entities across every core at one consistent instant" is a normal operation here, not a feat. It only works because nobody is mutating the versions those readers can see — the same property you bought with *Versioned* storage.

Two knobs worth knowing (both in `RuntimeOptions`):

- **`ParallelQueryMinChunkSize`** (default 64) — the floor on entities per chunk. Small sets still run the parallel path, just as one chunk. Stops tiny populations from spawning a chunk per worker for no gain.
- **`ChunksPerWorker`** (per-system, via `b.ChunksPerWorker(f)`) — oversubscription. Above 1.0, fast workers can steal extra chunks while a slow one finishes — smooths out an uneven workload.

---

## 6. Building and running the runtime

`TyphonRuntime.Create` takes your engine ([ch.1](01-first-app.md)), a schedule-building lambda, and options. Then `Start()` spins up the worker pool and the metronome; the tick loop runs until you `Shutdown()`.

```csharp
// engine `dbe` already built + schema registered (ch.1–2)

// One long-lived input View for the entity systems:
EcsView<Character> characters;
using (var tx = dbe.CreateQuickTransaction())
    characters = tx.Query<Character>().ToView();

using var runtime = TyphonRuntime.Create(dbe, schedule =>
{
    schedule.PublicTrack
        .DeclareDag("Sim")
        .Phases(Phase.Input, Phase.Simulation)
        .Add(new SpawnSystem())
        .Add(new MoveSystem(characters))
        .Add(new BoundsSyncSystem(characters))   // ch.2's WriteSpatial, kept coherent every tick
        .Add(new RegenSystem(characters))
        .Add(new WanderSystem(characters))
        .Add(new TradeSystem());
}, new RuntimeOptions
{
    BaseTickRate = 60,    // ticks per second
    WorkerCount  = -1,    // auto: max(1, CPUs - 4); set 1 for serial debugging
});

runtime.Start();
// … the simulation is now running on its own threads …
runtime.Shutdown();      // fires OnShutdown, stops workers cleanly
```

The runtime owns its threads — there is no "run one tick" call you drive in a loop. You `Start`, the world ticks itself, and you observe it (`runtime.CurrentTickNumber`, telemetry) or feed it (input queues, tool commands) from the outside.

**Lifecycle hooks** for the two moments that need special handling:

```csharp
runtime.OnFirstTick += ctx => { /* rebuild transient state after a crash restart */ };
runtime.OnShutdown  += ctx => { /* persist final state — ctx has an Immediate-durable tx */ };
```

- `OnFirstTick` fires once, with a real transaction — use it to repair `Transient` state that didn't survive a restart.
- `OnShutdown` fires during `Shutdown()`, with a dedicated **`Immediate`-durability** transaction so your final save is on disk before the process exits.

> 💡 **Side transactions — when a write can't wait for tick end.** The per-tick UoW commits at tick end, which is perfect for simulation state but wrong for a purchase or a trade: those must be durable the instant they happen. `ctx.CreateSideTransaction(DurabilityMode.Immediate)` gives you a transaction you own and dispose, committing independently of the tick. Use it for the rare economy-critical write; let everything else ride the tick.

---

## 7. Staying real-time under load

A fixed tick rate is a promise: 60 ticks a second means each tick has ~16 ms. When a tick starts overrunning that budget, the runtime would rather **degrade gracefully** than let latency spiral. You shape that degradation with two per-system declarations:

- **`Priority`** — `Critical` (never throttled or shed), `High`, `Normal`, `Low` (shed first).
- **`TickDivisor` / `ThrottledTickDivisor`** — run every Nth tick (normally / under load), and **`CanShed`** — may be skipped entirely under severe load.

```csharp
b.Name("Decals").Priority(SystemPriority.Low).CanShed(true).TickDivisor(2);
```

Under sustained overrun the engine escalates through a sticky chain — throttle low-priority systems, cap per-system entity budgets, slow the tick rate (down to a configurable floor), and finally fire **`OnCriticalOverload`** so *you* decide the last resort (shed work, split the world, refuse connections). The internals are the in-depth reference's job ([10-runtime](../in-depth-overview/10-runtime.md)); what you need to know to *use* it is: **set honest priorities, mark sheddable work sheddable, and the runtime keeps the critical path real-time when the machine can't keep up.**

> ⚠️ Overload response is about *surviving spikes*, not papering over a too-heavy design. If `Critical` systems alone blow the budget, no amount of shedding helps — that's a modeling/parallelism problem, not a tuning one.

---

## 🧭 What's next

You can now run logic over your world every tick, in parallel, in real time. That's the engine doing its job. The last chapter is about *operating* it:

- **[Chapter 6 — Operating & going deeper](06-operating.md):** observing a running engine (telemetry, the profiler), resource budgets, error-handling ground rules, and the map into the in-depth reference for when you outgrow this guide.

## 🧩 Key concepts & types

**Concepts:** [System](../key-concepts/system.md) · [Typhon runtime](../key-concepts/runtime.md) · [Scheduler & phases](../key-concepts/scheduler.md) · [Tick](../key-concepts/tick.md) · [Transaction](../key-concepts/transaction.md) · [Unit of Work](../key-concepts/unit-of-work.md) · [PointInTimeAccessor](../key-concepts/point-in-time-accessor.md).

**Exact calls:** `TyphonRuntime` (`Create` / `Start` / `Shutdown` / `OnFirstTick` / `OnShutdown` / `CurrentTickNumber`) · `RuntimeSchedule` (`PublicTrack.DeclareDag` / `Phases` / `Add`) · `CallbackSystem` / `QuerySystem` / `PipelineSystem` · `SystemBuilder` (`Name` / `Phase` / `Input` / `Reads` / `ReadsSnapshot` / `ReadsFresh` / `Writes` / `Parallel` / `WritesVersioned` / `After` / `Priority` / `CanShed`) · `Phase` (`Input` / `Simulation` / `Output` / `Cleanup`) · `TickContext` (`Transaction` / `Accessor` / `Entities` / `DeltaTime` / `TickNumber` / `CreateSideTransaction`) · `RuntimeOptions` (`BaseTickRate` / `WorkerCount`).
