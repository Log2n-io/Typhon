---
uid: guide-index
title: 'Typhon — User Guide'
description: 'A practical, read-as-you-go guide to building on Typhon. It''s task-oriented: enough to design your data, write and read it safely, query it, and run it on a…'
---

# Typhon — User Guide

A practical, read-as-you-go guide to **building on** Typhon. It's task-oriented: enough to design your data, write and read it safely, query it, and run it on a tick — without the engine internals.

> 📖 **Looking for internals?** The [in-depth overview](../in-depth-overview/README.md) is the contributor/power-user reference (struct layouts, algorithms, invariants). This guide links into it whenever you want to go deeper. Start here; go there when you need to.

> 🔍 **Looking for a specific feature?** The [feature catalog](../feature-set/README.md) is the lookup reference — every feature, one page each, with usage snippets and guarantees, organized for Ctrl-F rather than read-through. This guide teaches you the shape of Typhon end-to-end; come back to the catalog once you know what you're looking for.

---

## 🐍 What Typhon is 🐍

An **in-process, real-time ACID database** with an **ECS** (Entity-Component-System) data model. You define **components** (plain `struct`s), group them into **archetypes** (the shape of an entity), **spawn** entities, **query** them, and run **systems** over them on a **tick**. It ships as a .NET library — one `DatabaseEngine` per process, no server, no network.

**What it isn't:** not SQL, not a key-value store, not networked, no built-in replication. It exists to squeeze maximum throughput out of a single machine for simulation-style and game-server-style workloads.

> 💾 **Not RAM-bound.** Unlike an in-memory ECS (Unity DOTS, Bevy, EnTT…), Typhon's persistent data lives in a memory-mapped, **paged store on disk** — the database can be **far larger than the RAM hosting it**. Only the hot working set (a page cache you size) stays resident, the same way SQL Server or SQLite works. ECS ergonomics, database-grade storage.

## 🧠 The mental model

```
Component   a plain struct with data           (e.g. Position { X, Y })
Archetype   the fixed set of components an      (e.g. Unit = Position + Health)
            entity has — declared as a class
Entity      one instance of an archetype,       (an EntityId)
            identified by a 64-bit EntityId
Transaction how every write enters the engine   (short-lived, one per writer)
Snapshot    what every read sees — a consistent (no read locks)
            point-in-time view
System      behaviour that runs over entities   (on the tick loop)
            every tick
```

Three things to internalise early:
- **The default model: writes go through short-lived transactions, and reads see a consistent point-in-time snapshot without waiting on writers.** Hold this in your head first — it's how *Versioned* components (the default) behave.
- **One `DatabaseEngine` per process.** You build it once at startup.
- **Components choose a storage mode** — *Versioned* (full ACID, the default), *SingleVersion* (fast in-place), or *Transient* (in-memory only). The two fast modes deliberately relax the line above: their writes are immediate (no transaction needed) and their reads see live data, not a snapshot. It's a per-component decision; [ch.2](02-modeling.md) covers when to pick which. Until then, assume Versioned.

---

## 🪜 The reading ladder

Read **chapter 1** to get productive. Come back for the rest when the moment arrives — you don't need to read them in one sitting, or all of them.

| # | Chapter | Read it when you want to… |
|---|---|---|
| **1** | [Start here — your first app](01-first-app.md) | see Typhon working end-to-end and run something today |
| **2** | [Modeling your world](02-modeling.md) | design components, archetypes, indexes, storage modes, spatial |
| **3** | [Changing data: transactions & durability](03-transactions.md) | write/read safely and decide what survives a crash |
| **4** | [Querying & views](04-querying.md) | find entities, build reactive views, subscribe to changes |
| **5** | [Systems & the tick loop](05-systems.md) | run logic over your data every tick, in parallel |
| **6** | [Operating & going deeper](06-operating.md) | observe, set resource budgets, handle errors, find the deep docs |

> 📦 **Run it, don't just read it.** Every snippet in this guide is mirrored in a small runnable project — [`example/`](https://github.com/Log2n-io/Typhon/tree/main/doc/guide/example). `dotnet run --project doc/guide/example` walks the whole arc (spawn → read → transact → query → view → tick) and prints what each chapter describes.

## 🧩 The types you'll touch (whole guide)

`DatabaseEngine` · `Archetype<T>` + `Comp<T>` · `EntityId` / `EntityRef` · `Transaction` / `UnitOfWork` · `EcsQuery` / `EcsView` · the system builder. Everything else is the engine's job, not yours.
