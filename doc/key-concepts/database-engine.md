---
uid: concept-database-engine
title: 'DatabaseEngine'
description: 'The root object — one per process. DatabaseEngine.Open names the on-disk database, registers your schema, and returns a ready-to-use engine that owns the page cache, allocator, WAL, and timers.'
---

# DatabaseEngine

> **In one line:** the **root object** — one per process — that owns everything and hands you [transactions](xref:concept-transaction), [queries](xref:concept-query), and the schema.

`DatabaseEngine.Open("skirmish.typhon", o => o.Register<Position>()…RegisterArchetype<Unit>())` is the one-line setup: it names the on-disk database, registers your [components](xref:concept-component) and [archetypes](xref:concept-archetype) (running any [schema migration](xref:concept-schema-evolution) first), and returns a ready-to-use engine. `using var` flushes dirty pages and releases the file lock at scope end. Build it **once at startup** and hand it around.

Under the hood the engine is a composition of independently-configurable subsystems — [page cache](xref:concept-page-cache), allocator, [WAL](xref:concept-wal-checkpoint), timers — tuned through `DatabaseEngineOptions` (or `services.AddTyphon(…)` in a DI app). You declare the envelope; it self-manages the rest.

## How it relates

- **[Transaction](xref:concept-transaction)** / **[Unit of Work](xref:concept-unit-of-work)** — created from the engine.
- **[Page cache](xref:concept-page-cache)** / **[WAL & checkpoint](xref:concept-wal-checkpoint)** — subsystems it owns and budgets.
- **[Schema evolution](xref:concept-schema-evolution)** — migration runs during `Open`.
- **[Runtime](xref:concept-runtime)** — built on top of an engine to run systems.

## In the API

- [`DatabaseEngine`](xref:Typhon.Engine.DatabaseEngine) — [`Open`](xref:Typhon.Engine.DatabaseEngine.Open*) / `Register<T>` / `RegisterArchetype<T>` / `CreateQuickTransaction` / [`CreateUnitOfWork`](xref:Typhon.Engine.DatabaseEngine.CreateUnitOfWork*).
- [`DatabaseEngineOptions`](xref:Typhon.Engine.DatabaseEngineOptions) — configuration ([`Resources`](xref:Typhon.Engine.DatabaseEngineOptions.Resources), [`Wal`](xref:Typhon.Engine.DatabaseEngineOptions.Wal), `Configure*`).

## Learn & use

- **Narrative:** [Guide ch.1 §3 — open the engine](xref:guide-first-app) · [ch.6 — operating](xref:guide-operating)
- **Feature detail:** [hosting](xref:feature-hosting-index) · [engine options configuration](xref:feature-hosting-engine-options-configuration-index)
