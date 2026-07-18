---
uid: feature-hosting-database-seeding
title: 'Database Seeding'
description: 'Revision-stepped, crash-safe data seeding applied automatically at engine open.'
---

# Database Seeding
> Revision-stepped, crash-safe seed steps applied automatically at engine open — a fresh database runs them all; an existing one catches up on whatever is new.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Hosting](./README.md)

## 🎯 What it solves

A Typhon database is persistent: `DatabaseEngine.Open("game.typhon")` **creates** the bundle on the first run and **reopens** it (with all its data) on every run after. A program that unconditionally spawns its initial data on startup therefore *duplicates* that data every run. Hand-rolling a "seed only if empty" guard is easy to get wrong — it doesn't survive a crash mid-seed, and "empty" is not the same as "not yet seeded." `Seed` makes one-time (and evolving) data a first-class, crash-safe part of engine setup.

## ⚙️ How it works (in brief)

`TyphonOptions.Seed(int revision, Action<Transaction> step)` registers a seed step tagged with a monotonic **revision**. At engine open — through both `DatabaseEngine.Open` and the DI `AddTyphon` path, right after archetypes are wired — the engine applies every registered step whose revision is greater than the database's **committed** seed revision, in ascending order, each inside its own durable transaction, then records the new committed revision in the database's bootstrap key/value store. A fresh database (committed 0) runs every step; an existing one runs only the steps it has not applied yet — so shipping a new `Seed(N, …)` brings every instance up to date on its next open.

For lower-level control, `DatabaseEngine.IsNewlyCreated` reports whether *this* open created the bundle (vs reopened an existing one).

## 💻 Usage

```csharp
using var dbe = DatabaseEngine.Open("game.typhon", o => o
    .Register<Position>().Register<Health>().RegisterArchetype<Unit>()
    .Seed(1, tx => tx.Spawn<Unit>(Unit.Position.Set(new Position(10, 20)), Unit.Health.Set(new Health(100, 100))))
    .Seed(2, tx => { /* data introduced in revision 2 — existing databases pick this up on next open */ }));
```

## ⚠️ Guarantees & limits

- **A step whose transaction never commits re-runs.** Do a step's work in its supplied transaction; if it throws (or the process dies) before that transaction commits, nothing is durable and the step re-runs on the next open. The engine commits each step for you.
- **Ascending revision order**, each in its own durable (`Immediate`) transaction. Revisions must be `>= 1` and unique across `Seed` calls — a duplicate revision throws at configuration time.
- **Forward-only.** No down-migrations, and an already-committed revision is never re-run.
- **Durability bound (setup-time).** The committed revision is stored in the bootstrap key/value store, written *after* each step commits — a separate meta-page fsync, not part of the step's WAL commit. So a crash in the narrow window between a step's commit and that write re-runs **that one step** on the next open — at worst a duplicate of that step's data, never data loss. This is an accepted bound for setup-time seeding; a fully-atomic marker would require an engine-owned ECS entity, which the engine intentionally does not introduce.
- **Baseline caveat.** Steps gate on the committed revision, which starts at 0 — so retrofitting `Seed(1, …)` onto a database that was hand-populated *before* seeding was added will run step 1 (the classic migration "baseline" situation). Gate on `IsNewlyCreated` if you need to skip that.

## 🧪 Tests

- [SeedTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Hosting/SeedTests.cs) — apply-all-on-create / skip-on-reopen, apply-only-new-steps-on-reopen (incremental catch-up), re-run-a-step-that-never-committed (crash-safety), duplicate-revision-throws.

## 🔗 Related

- Source: [`TyphonOptions.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Hosting/public/TyphonOptions.cs) (`Seed`), [`TyphonBuilderExtensions.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Hosting/public/TyphonBuilderExtensions.cs) (apply-pending-steps gate), [`DatabaseEngine.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/DatabaseEngine.cs) (`IsNewlyCreated`)
- Guide: [Start here: your first Typhon app](../../guide/01-first-app.md) — introduces `Seed` in context.
- Sibling: [Bulk Load](../Durability/bulk-load.md) — the high-throughput path for seeding *millions* of entities in one session.
