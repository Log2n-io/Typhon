---
uid: concept-entity
title: 'Entity'
description: 'An entity is one instance of an archetype, identified by a 64-bit EntityId. You open it inside a transaction to get an EntityRef for reading and writing its components.'
---

# Entity

> **In one line:** one instance of an [archetype](xref:concept-archetype), identified by a 64-bit **`EntityId`**.

Entities are created with `tx.Spawn<Unit>(…)` (which returns an `EntityId`) and removed with `Destroy(id)` — both transactional in *all* [storage modes](xref:concept-storage-mode). To read or write an entity you **open** it inside a [transaction](xref:concept-transaction): `tx.Open(id)` returns a read `EntityRef`, `tx.OpenMut(id)` a writable one. From an `EntityRef` you call `Read<T>` / `Write<T>` with a component's `Comp<T>` handle.

An `EntityId` is a compact value you can store and pass around; a typed cross-entity reference is an [`EntityLink<T>`](xref:concept-entity-link).

## How it relates

- **[Archetype](xref:concept-archetype)** — an entity's shape; you spawn *an archetype*.
- **[Component](xref:concept-component)** — what you read/write through the entity's `EntityRef`.
- **[Transaction](xref:concept-transaction)** — the scope in which an entity is opened; reads see its [snapshot](xref:concept-snapshot-isolation).
- **[EntityLink](xref:concept-entity-link)** — a typed, stored reference from one entity to another.

## In the API

- [`EntityId`](xref:Typhon.Engine.EntityId) — the 64-bit identity returned by [`Spawn`](xref:Typhon.Engine.Transaction.Spawn*).
- [`EntityRef`](xref:Typhon.Engine.EntityRef) — the handle from `Open`/`OpenMut`; [`Read<T>`](xref:Typhon.Engine.EntityRef.Read*) / [`Write<T>`](xref:Typhon.Engine.EntityRef.Write*).

## Learn & use

- **Narrative:** [Guide ch.1 — spawn, read, query](xref:guide-first-app)
- **Feature detail:** [component collections](xref:feature-ecs-component-collections)
