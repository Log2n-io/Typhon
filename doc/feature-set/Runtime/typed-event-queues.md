# Typed Event Queues
> Lock-free single-producer→single-consumer ring buffers for signalling between systems within a tick.

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
`Dag.Consumes`. Producers `Push` events during `Execute`; DAG ordering guarantees the producer
fully completes before the consumer starts, so the producer→consumer handoff needs no
synchronization. `EventQueue<T>` is single-producer, not multi-producer — a parallel system with
several chunk workers must route events through one designated producer (or a queue per worker,
merged afterward), not call `Push` directly from every chunk. A reactive system
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
        foreach (var id in ctx.Entities)
        {
            if (BossKilled(ctx.Transaction, id))
            {
                _lootQueue.Push(new LootEvent { Source = id, ItemId = 42 });
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
        Span<LootEvent> events = stackalloc LootEvent[queue.Count];
        var n = queue.Drain(events);
        for (var i = 0; i < n; i++)
        {
            SpawnLoot(ctx.Transaction, events[i]);
        }
    }
}
```

| Option | Default | Effect |
|---|---|---|
| `capacity` (`CreateEventQueue<T>`) | 1024 | Must be a power of 2; sizes the per-tick ring buffer for `T` |

## ⚠️ Guarantees & limits

- Push is allocation-free but **not thread-safe** — calling `Push` concurrently from multiple
  workers races and corrupts the queue. Only one thread may produce into a given queue per tick.
- Drain is single-consumer and relies on DAG ordering: only the declared consumer should read a
  queue, after the producer's edge has run — there are no per-slot sequence numbers to make
  unordered access safe.
- Queues are reset at the start of every tick; events never carry over, and `Push` past capacity
  throws `InvalidOperationException` (also counted in the queue's overflow telemetry) — size each
  queue for the tick's worst-case event volume.
- `T` has no `unmanaged` constraint — both structs and reference types work; reference-type slots
  are cleared after each `Drain`/`Reset` so they don't pin garbage.
- A reactive system whose only trigger is `ReadsEvents` skips completely when its queue(s) are
  empty — no `Execute`, no Transaction created.
- Intra-tick signalling only — queues are not persisted, not part of the WAL, and invisible across
  ticks, snapshots, or processes.

## 🧪 Tests

- [EventQueueTests](../../../test/Typhon.Engine.Tests/Runtime/EventQueueTests.cs) — push/drain round-trip, push-past-capacity throws, power-of-2 capacity, reference-type slot clearing on drain/reset
- [EventQueueIntegrationTests](../../../test/Typhon.Engine.Tests/Runtime/EventQueueIntegrationTests.cs) — producer→consumer handoff across a DAG edge, `ctx.ConsumedQueues` wiring, reactive skip when empty

## 🔗 Related

- Related feature: [Declarative System Scheduling](./declarative-system-scheduling.md)
- Sibling: [CallbackSystem](./system-types/callback-system.md) — a common proactive producer/consumer for these queues.

<!-- Deep dive: claude/design/Runtime/02-system-scheduling.md §Typed Event Queues -->
<!-- Deep dive: claude/design/Runtime/07-system-access-declarations.md (WritesEvents/ReadsEvents access-edge derivation) -->
