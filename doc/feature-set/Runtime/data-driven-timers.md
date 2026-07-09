---
uid: feature-runtime-data-driven-timers
title: 'Data-Driven Timers / Scheduled Entities'
description: 'Model respawns, expiries, and cooldowns as queryable entities — no separate timer subsystem.'
---

# Data-Driven Timers / Scheduled Entities
> Model respawns, expiries, and cooldowns as queryable entities — no separate timer subsystem.

**Status:** 📋 Planned · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](./README.md)

## 🎯 What it solves

Game servers need scheduled work — respawns, buff/cooldown expiry, auction timeouts, periodic
events — that fires by passage of time rather than by data change. A dedicated timer/scheduler
subsystem (heap of callbacks, wheel timer, etc.) duplicates machinery Typhon already has: storage,
transactions, indexes, queries. It also loses the properties those give you for free — a timer
living outside the entity model isn't transactional, isn't crash-recoverable, and isn't
queryable ("show me all pending respawns") without bespoke code.

## ⚙️ How it works (in brief)

A timer is an ordinary entity: an archetype carrying a time-of-expiry field plus whatever payload
the timer needs (player id, item id, …). A dedicated `CallbackSystem` runs every tick and queries
for entities whose expiry field has passed `now`, processes them, and destroys (one-shot) or
re-arms (repeating) the timer entity. Because expiry is driven by wall-clock or tick-count passage
— not by a write to the entity — this must be a proactive `CallbackSystem` poll, not a reactive
`changeFilter`/View trigger: nothing writes to the timer component when it silently expires, so a
change-tracking mechanism would never see the event.

## 💻 Usage

```csharp
// 1. A payload component + your own expiry component (any unmanaged struct works;
//    Typhon does not ship a built-in ScheduleAt type today).
public struct ScheduleAt
{
    public long ExpiresAtTicks;   // Stopwatch.GetTimestamp()-based
    public bool IsExpired(long nowTicks) => nowTicks >= ExpiresAtTicks;
    public static ScheduleAt After(TimeSpan delay) =>
        new() { ExpiresAtTicks = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency) };
}

public struct RespawnData
{
    public EntityId PlayerId;
}

[Archetype(300)]
public class RespawnTimer : Archetype<RespawnTimer>
{
    public static readonly Comp<ScheduleAt> Timer = Register<ScheduleAt>();
    public static readonly Comp<RespawnData> Data = Register<RespawnData>();
}

// 2. Schedule a respawn from any system, inside its Transaction:
var timer = ScheduleAt.After(TimeSpan.FromSeconds(30));
var data = new RespawnData { PlayerId = deadPlayerId };
tx.Spawn<RespawnTimer>(RespawnTimer.Timer.Set(in timer), RespawnTimer.Data.Set(in data));

// Cancel by destroying the timer entity:
tx.Destroy(respawnTimerId);

// 3. A CallbackSystem polls and fires expired timers every tick:
var dag = schedule.PublicTrack.DeclareDag("Game");
dag.CallbackSystem("ProcessRespawns", ctx =>
{
    var now = Stopwatch.GetTimestamp();
    foreach (var id in ctx.Transaction.Query<RespawnTimer>()
                 .Where<ScheduleAt>(t => t.IsExpired(now))
                 .Execute())
    {
        var entity = ctx.Transaction.Open(id);
        var data = entity.Read(RespawnTimer.Data);
        RespawnPlayer(ctx.Transaction, data.PlayerId);
        ctx.Transaction.Destroy(id);   // one-shot: remove after firing
    }
}, after: "Input");
```

## ⚠️ Guarantees & limits

- **Transactional, crash-durable, queryable, cancellable** — these come for free from being a
  normal entity: rollback undoes scheduling, recovery restores it per the archetype's storage
  mode, `tx.Query<RespawnTimer>().Execute()` lists pending timers, `tx.Destroy(id)` cancels one.
- **No built-in scheduler** — this is a documented pattern, not Typhon infrastructure. You write
  the `ScheduleAt`-style component, the `CallbackSystem` poll loop, and the re-arm/destroy logic.
- **Current query cost is a broad scan, not an index seek** — `EcsQuery<T>.Where<T>` evaluates the
  predicate per entity via `Transaction.Open` + `TryRead`; targeted (index-first) scan isn't wired
  up yet. An index-backed range query (`ExpiresAtTicks <= now`) is the eventual goal but cost
  today scales with the archetype's entity count, not the expired subset.
- **Pick storage mode per timer cost/criticality**: `SV` for high-frequency, loss-tolerant timers
  (respawns, cooldowns — a tick of loss on crash is invisible); `Versioned` for timers that must
  never silently vanish (auction expiry, trade lockouts).
- Repeating timers must be re-armed explicitly (`ExpiresAtTicks += interval`) by your processing
  code — there is no automatic repeat semantics.

## 🔗 Related

- Related feature: [Declarative System Scheduling](./declarative-system-scheduling.md) (CallbackSystem, `shouldRun`)
- Related feature: [Reactive Dispatch / Change Filters](./reactive-dispatch-change-filters.md) (why this pattern uses a poll, not a filter)
- Related feature: [Query System](../Ecs/query-system.md)
- Related feature: [Storage Mode — SingleVersion](../Ecs/storage-modes/storage-mode-singleversion.md), [Storage Mode — Versioned](../Ecs/storage-modes/storage-mode-versioned.md)

<!-- Deep dive: claude/design/Runtime/02-system-scheduling.md §Schedule Components (Timer Pattern) -->
<!-- Deep dive: claude/design/Runtime/README.md (decision R15) -->
