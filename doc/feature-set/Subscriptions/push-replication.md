---
uid: feature-subscriptions-push-replication
title: 'Push Replication'
description: 'The application says what changed with Replicate; the engine pushes spawns, destroys, moves and migrations itself, compares quantized bytes, and encodes each change once for every session.'
---

# Push Replication
> The application says what changed; the engine decides what to send, encodes it once, and fans it out to the sessions around it.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

Finding what changed is the dominant cost of replication. Scanning every entity every tick costs as much as the world is large; tracking
what each client last saw costs clients × entities. Push replication costs what **changed**: the entities a tick touched, nothing else —
an archetype of ten million quiet entities costs nothing.

## ⚙️ How it works (in brief)

- **The push set.** After a write a client should see, a system calls `ctx.Subscriptions.Replicate(in cluster, slot)` (or a slot mask, or
  `Replicate(in entityRef)` for an entity opened by id). It is one atomic OR into the per-cluster structure word the tick fence already
  keeps. The engine adds its own pushes: spawns, destroys, `WriteSpatial` moves (and a mutable span over the spatial column), migrations,
  movers the client is still extrapolating, and — on the first tick — every live entity.
- **Projection.** After the fence, each pushed entity's declared fields are read through the archetype's cluster layout, quantized, encoded
  into their change groups and **compared byte for byte** with what was last encoded. The comparison *is* the encode: a push that changed
  nothing visible costs an encode and no bytes on the wire.
- **Encode once.** Enter records, group bodies and motion segments are encoded once per entity per tick into replication-owned native
  storage; a session's frame copies them. Two sessions in the same state receive identical bytes.
- **Fan-out.** Changes are bucketed by replication cell; each session's frame is gathered from the cells around it ([Profiles & observers](profiles-observers.md)).

## 💻 Usage

```csharp
// A system that walks clusters:
if (ham[slot].Health != newHealth)
{
    ham[slot].Health = newHealth;
    ctx.Subscriptions.Replicate(in cluster, slot);
}

// An entity reached by id — a command's target:
var target = ctx.Transaction.OpenMut(id);
target.Write(Character.Ham).Health -= 10;
ctx.Subscriptions.Replicate(in target);
```

| Setting | Default | Effect |
|---|---|---|
| `ProfileBuilder.Detection(PushDetection.Automatic)` | `Explicit` | The engine projects every live entity of the profile's archetypes every tick — nothing to forget, an encode per entity per tick. Experimental; refused unless `SubscriptionsOptions.AllowAutomaticPushDetection` is set |
| `TYPHON_PUSH_VALIDATE=N` (environment) | off | Projects N whole clusters per explicit archetype per tick, counts changes nobody pushed — and sends them |

## ⚠️ Guarantees & limits

- **A write you do not push is not sent.** The client keeps the old value until the entity changes again or is pushed. The engine pushes
  structure and spatial changes; content writes are yours. Use the validator in development.
- **Per-tick work follows the push set, never the archetype** (SUB-13); an archetype no profile observes costs nothing.
- **Nothing is published for an aborted or fence-failed tick**; frames are released only after the tick's durability flush (SUB-02).
- **Zero steady-state managed allocation** in the replication path (SUB-07).
- Measured on the SWG demo (269 k entities, 1 000 player sessions, 50 Hz, Ryzen 7950X): the whole replication track ≈ 1.5–1.6 ms per tick.

## 🧪 Tests

- [PushOracleTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushOracleTests.cs) — decoded clients against the server under churn, skips and both detection modes, with the forgotten-push mutant
- [FrameAssemblerTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/FrameAssemblerTests.cs) — the projection addresses exactly the pushed slots, by cluster and by `EntityRef`

## 🔗 Related

- [Projections & codecs](projections-codecs.md) — what is compared and encoded
- Internals: [the cell algorithm, and why it scales](../../in-depth-overview/15-subscriptions.md#3-the-cell-algorithm-who-receives-what)
- Concept: [Subscription](xref:concept-subscription) · [Tick fence](xref:concept-tick-fence)
