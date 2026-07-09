---
uid: feature-subscriptions-published-views-shared-views
title: 'Shared (World-State) Views'
description: 'One View instance, refreshed and diffed once per tick, fanned out to every subscriber.'
---

# Shared (World-State) Views
> One View instance, refreshed and diffed once per tick, fanned out to every subscriber.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](../README.md)

## 🎯 What it solves

World state — NPCs, terrain objects, the player roster — is the same for every client watching it. Computing
that delta independently per subscriber wastes CPU proportional to subscriber count for data that doesn't
vary by who's asking. Shared Views let many clients subscribe to the exact same query result: the engine
refreshes and diffs it once per tick no matter how many clients are subscribed.

## ⚙️ How it works (in brief)

`PublishView(name, view)` registers a single `ViewBase` instance as the published target. During the Output
phase, the engine refreshes that one View, computes its Added/Removed/Modified delta once, and serializes the
resulting `TickDeltaMessage` once for steady-state clients — the same byte buffer is then memcpy'd into every
subscriber's send buffer. Subscriber count only affects the fan-out step, not the delta computation or
serialization cost. The View instance must be dedicated to subscriptions — it cannot also be wired as a
system's query input (see Published/System-Input View Separation).

## 💻 Usage

```csharp
[Component("Game.Npc", 1)]
public struct Npc
{
    [Index] public int Health;
}

[Archetype(10)]
public class NpcArch : Archetype<NpcArch>
{
    public static readonly Comp<Npc> Data = Register<Npc>();
}

using var tx = dbe.CreateQuickTransaction();

// Dedicated View instance for subscriptions — do not reuse a system's input View here.
var worldNpcsView = tx.Query<NpcArch>()
    .WhereField<Npc>(n => n.Health > 0)
    .ToView();

var worldNpcs = runtime.PublishView("world_npcs", worldNpcsView, SubscriptionPriority.Normal);

// Subscribe a client to it; the runtime diffs against their previous subscription set.
runtime.SetSubscriptions(clientContext, worldNpcs);
```

| Option | Default | Effect |
|--------|---------|--------|
| `priority` | `SubscriptionPriority.Normal` | `Critical` always pushed; `Normal` throttled at overload Level 1+; `Low` throttled aggressively, paused at Level 4 |

## ⚠️ Guarantees & limits

- **One delta, one serialization, N memcpys** — cost scales with View size and change volume, not with
  subscriber count, for the steady-state (no pending events, no sync) case.
- **Must be a dedicated View** — using the same instance as a system's query input throws
  `InvalidOperationException`; the View's change-tracking ring buffer has a single consumer.
- New subscribers receive the View's current entity set via incremental sync (batched across ticks for large
  Views), not a free pass to the live delta stream — see Incremental Sync in the design doc.
- All subscribers to a shared View see identical data; there is no per-client filtering within a shared View
  (use a per-client View factory for that).

## 🧪 Tests

- [SubscriptionStressTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SubscriptionStressTests.cs) —
  `MultiClient_50Clients_SharedView_AllReceiveDeltas`: one shared View fanned out to 50 concurrent subscribers
- [ViewDeltaTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/ViewDeltaTests.cs) —
  `DeltaBuilder_SharedView_ProducesCorrectDelta`/`DeltaBuilder_SecondCall_OnlyNewEntities`: delta correctness across
  ticks for a shared `PublishedView`

## 🔗 Related

- Sibling: [Published/System-Input View Separation](../published-view-isolation.md)
- Parent feature: [Published Views](./README.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — View Types for Subscriptions, Output Phase Threading -->
