---
uid: feature-runtime-typed-event-queues
title: 'Typed Event Queues'
description: 'Lock-free multi-producer→single-consumer buffers for signalling between systems within a tick.'
---

# Typed Event Queues
> Lock-free multi-producer→single-consumer buffers for signalling between systems within a tick.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](./README.md)

## 🎯 What it solves

Game systems need to react to what another system did this tick — drop loot after a kill, resolve
damage after combat — without polling shared state or scanning every entity every tick. Typhon's
DAG is static (no dynamic system insertion), so conditional cascades need a channel that lets a
downstream system stay statically wired into the schedule yet do nothing on a quiet tick. Typed
event queues give producer and consumer systems a structured data channel whose emptiness doubles
as the consumer's skip signal.

## ⚙️ How it works (in brief)

A queue is created once at schedule-build time (`Dag.CreateEventQueue<T>`) and wired to its
producer/consumer systems either declaratively (`SystemBuilder.WritesEvents` / `.ReadsEvents`,
which also derives the DAG ordering edge) or via the lambda-shorthand `Dag.Produces` /
`Dag.Consumes`. Producers push events during `Execute` through `ctx.Writer(queue)`; DAG ordering guarantees every
producer fully completes before the consumer starts, so the producer→consumer handoff needs no
synchronization. The queue owns **one segment per worker**, so a `.Parallel()` system may push from
every chunk worker concurrently — each writes its own segment, and no atomics are involved. A reactive system
(QuerySystem/PipelineSystem) that declares `ReadsEvents` auto-skips when every consumed queue is
empty and it has no other dirty-entity trigger — its `Execute` never runs, no Transaction is
created. Every queue is cleared automatically at the start of each tick.

## 💻 Usage

```csharp
public struct LootEvent
{
    public EntityId Source;
    public int ItemId;
}

// using System.Buffers;
var dag = schedule.PublicTrack.DeclareDag("Game");
var lootQueue = dag.CreateEventQueue<LootEvent>("LootEvents", capacity: 256);

public class CombatSystem : QuerySystem
{
    private readonly EventQueue<LootEvent> _lootQueue;
    public CombatSystem(EventQueue<LootEvent> lootQueue) => _lootQueue = lootQueue;

    protected override void Configure(SystemBuilder b) => b
        .Name("Combat").Input(() => combatView)
        .WritesEvents(_lootQueue);

    protected override void Execute(TickContext ctx)
    {
        // Resolve this worker's segment ONCE, then push through it — that is what keeps a
        // multi-producer queue at single-producer cost.
        var loot = ctx.Writer(_lootQueue);
        foreach (var id in ctx.Entities)
        {
            if (BossKilled(ctx.Transaction, id))
            {
                loot.Push(new LootEvent { Source = id, ItemId = 42 });
            }
        }
    }
}

public class LootDropSystem : QuerySystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("LootDrop").Input(() => combatView)
        .ReadsEvents(_lootQueue)
        .After("Combat");

    // Skipped entirely on ticks where Combat produced no LootEvent.
    protected override void Execute(TickContext ctx)
    {
        var queue = (EventQueue<LootEvent>)ctx.ConsumedQueues[0];
        // Heap-rent rather than stackalloc: under skew Count can reach capacity per worker slot.
        var events = ArrayPool<LootEvent>.Shared.Rent(queue.Count);
        var n = queue.Drain(events);
        for (var i = 0; i < n; i++)
        {
            SpawnLoot(ctx.Transaction, events[i]);
        }

        ArrayPool<LootEvent>.Shared.Return(events);
    }
}
```

| Option | Default | Effect |
|---|---|---|
| `capacity` (`CreateEventQueue<T>`) | 1024 | Power of 2. Expected events per tick — split across worker segments, and the per-segment growth ceiling. Reported unchanged as `Capacity`; live allocation is `AllocatedCapacity` |
| `allowGrowth` (`new EventQueue<T>`) | `true` | When false a full segment drops instead of doubling — a fixed bound with loud telemetry |

## ⚠️ Guarantees & limits

- Push is allocation-free in steady state and **safe from every worker at once** — obtain a writer
  with `ctx.Writer(queue)` and it binds the calling worker's own segment. Never share a writer
  between threads; it is a `ref struct`, so the compiler already prevents capturing or storing one.
- Producing from a **lifecycle hook** (`OnFirstTick`, `OnShutdown`) throws: those contexts carry
  `TickContext.NonWorkerId` and own no segment.
- Drain is single-consumer and relies on DAG ordering. Both halves are enforced at build time: a
  queue may have **at most one** consumer, and a `.Parallel()` system may **produce** but not
  **consume**. Any number of systems — and any number of chunk workers — may produce.
- `Drain` **throws** if the destination span is shorter than `Count`; size it from `Count`.
  Truncating silently would be event loss that no counter reports.
- **Events are not ordered across workers.** Each worker's events arrive in push order, workers in
  slot order; which worker ran which chunk is not reproducible, so never depend on inter-event order.
- Queues are reset at the start of every tick and events never carry over. A full segment **doubles**
  up to `capacity`; past that a push is **dropped and counted** in the queue's overflow telemetry, and
  `Push` returns `false`. It never throws — a queue sizing mistake must not be able to abort a tick.
  Grown buffers survive `Reset`, so a workload stops allocating after a few ticks.
- `T` has no `unmanaged` constraint — both structs and reference types work; reference-type slots
  are cleared after each `Drain`/`Reset` so they don't pin garbage.
- A reactive system whose only trigger is `ReadsEvents` skips completely when its queue(s) are
  empty — no `Execute`, no Transaction created.
- Intra-tick signalling only — queues are not persisted, not part of the WAL, and invisible across
  ticks, snapshots, or processes.

## 📊 Performance

Guarded by `EventQueueBenchmarks` (`test/Typhon.Benchmark`, categories `Runtime` + `Regression`). Pushing through
`ctx.Writer(queue)` costs ~0.22 ns/push more than the single-producer buffer it replaced (0.78 ns vs 0.56 ns,
allocation-free) — and that buys pushing from every chunk worker at once. Advancing one shared tail with
`Interlocked.Increment` instead measures 10x the writer *single-threaded*, and far worse under contention.

## 🧪 Tests

- [EventQueueTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/EventQueueTests.cs) — push/drain round-trip, growth past the initial allocation, drop-and-count at the ceiling, power-of-2 capacity, reference-type slot clearing on drain/reset
- [EventQueueConcurrencyTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/EventQueueConcurrencyTests.cs) — a `.Parallel()` producer delivers every event exactly once (exact multiset equality), telemetry folds every worker's pushes, lifecycle hooks cannot produce
- [EventQueueIntegrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/EventQueueIntegrationTests.cs) — producer→consumer handoff across a DAG edge, `ctx.ConsumedQueues` wiring, reactive skip when empty

## 🔗 Related

- Related feature: [Declarative System Scheduling](./declarative-scheduling/README.md)
- Sibling: [CallbackSystem](./system-types/callback-system.md) — a common proactive producer/consumer for these queues.

<!-- Deep dive: claude/design/Runtime/02-system-scheduling.md §Typed Event Queues -->
<!-- Deep dive: claude/design/Runtime/07-system-access-declarations.md (WritesEvents/ReadsEvents access-edge derivation) -->
