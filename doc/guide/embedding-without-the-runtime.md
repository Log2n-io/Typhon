---
uid: guide-embedding
title: 'Embedding Typhon without the runtime'
description: 'Chapter 5 says the runtime is optional. It is — but one obligation comes with driving the engine yourself: you must close each tick by calling the tick fence.'
---

# Embedding Typhon without the runtime

[Chapter 5](05-systems.md) says the runtime is **recommended but optional**, and that's true: if your app is
request/response, a batch job, or embeds Typhon inside a loop you already own — a game engine's own frame, say — you
can drive the engine directly through transactions and never declare a system.

One obligation comes with that, and it is the only one:

> ### ⚠️ You must close each tick by calling `dbe.WriteTickFence(n)`
>
> Under the runtime this happens automatically at the end of every tick. Without the runtime, **nobody does it for
> you**, and skipping it does not throw — the database quietly stops maintaining itself.

## The loop

```csharp
long tick = 0;

while (running)
{
    // 1. Do your work — any number of transactions, on this thread.
    using (var tx = dbe.CreateQuickTransaction())
    {
        // spawn, read, mutate, destroy…
        tx.Commit();
    }

    // 2. Close the tick. Monotonic number, once per tick, after the tick's writes.
    dbe.WriteTickFence(++tick);
}
```

That's the whole contract. Three rules:

1. **Once per tick**, not once per transaction and not once before a query.
2. **`tickNumber` must increase** between calls.
3. **Nothing else may mutate the database while the fence runs.** On a single-threaded host that's automatic. If
   your host has its own worker threads, joining them before the fence is your responsibility — the engine does not
   check it.

## What the fence actually does

Four jobs, not one. This is why it isn't optional:

| | |
|---|---|
| **Durability** | serializes dirty `SingleVersion` components to the WAL — this *is* the crash-recovery boundary |
| **Cluster migrations** | executes the tick's pending moves for entities that crossed a spatial cell boundary |
| **Spatial AABBs** | recomputes cluster bounds and the per-cell spatial index |
| **Zone maps** | drains the per-cluster min/max summaries queries use to prune |

## What breaks if you skip it

Nothing throws. Nothing is corrupted. The failure is **silent**, which is what makes it worth a page:

- **`SingleVersion` components degrade to `Transient`-like durability** — no crash recovery. See
  [storage modes](../feature-set/Ecs/storage-modes/storage-mode-singleversion.md).
- **Cluster migrations never execute.** Entities keep their old cluster, so spatial locality decays and queries open
  progressively more clusters for the same answer.
- **Spatial AABBs and zone maps go stale.** Spatial queries answer against *old positions* — not an error, just
  wrong results.

If you are only using `Versioned` components and no spatial queries, the consequences are smaller — but the fence is
still where SV data reaches the WAL, so "I don't need it" is rarely true for long.

## What you do *not* have to call

**Checkpointing is automatic.** A background thread runs it on a timer (default 30 s), on dirty-page pressure, on
page-cache back-pressure, and on graceful shutdown. `ForceCheckpoint()` is **not** part of the tick loop — reach for
it only when you need a cycle *now*, and note that it returns immediately rather than blocking. See
[checkpointing](../feature-set/Durability/checkpoint-v2/README.md).

The one exception: `Committed` storage-mode data is durable only to the last checkpoint, so a host using it should
force a checkpoint before exiting if the shutdown isn't graceful.

## The `changeSet` parameter

`WriteTickFence(long tickNumber, ChangeSet changeSet = null)` takes an optional ChangeSet.

**Pass `null`** — the overload above — and the fence creates and commits a one-shot ChangeSet itself. That is correct
and is what a host without a per-tick unit of work should do.

The runtime passes its per-tick UoW's shared ChangeSet instead, which consolidates the fence's dirty pages with the
rest of the tick into one writeback. If your host already keeps a per-tick UoW, passing its ChangeSet gets you the
same consolidation.

## A worked example

`demo/SpaceBattle` is a full simulation that uses transactions, ECS spawn/destroy, MVCC, WAL and cluster storage —
and no runtime at all. Its tick closes like this:

```csharp
public void RunTickFence()
{
    Tick++;
    DBE.WriteTickFence(Tick);
}
```

…called once at the end of each simulated tick, after all of that tick's transactions have committed.

## When to use the runtime instead

Reach for [the runtime](05-systems.md) when you have continuous, tick-driven logic you want run **in parallel** with
dependency ordering worked out for you. It also removes this page's obligation — the fence, the per-tick unit of
work, and the transaction plumbing all become the runtime's problem.

Driving the engine yourself is the right call when you already own the loop and only need Typhon to be the store.
