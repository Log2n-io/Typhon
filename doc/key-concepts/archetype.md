---
uid: concept-archetype
title: 'Archetype'
description: 'An archetype is the fixed shape of an entity — the set of component types it has, declared once as a sealed partial class deriving from Archetype<TSelf>.'
---

# Archetype

> **In one line:** the fixed **shape of an entity** — the set of [component](xref:concept-component) types it has.

An archetype is declared once as a `sealed partial class Unit : Archetype<Unit>` with an `[Archetype(id)]` attribute for a stable numeric identity. Each `Register<T>()` declares a component slot and yields a static `Comp<T>` handle (`Unit.Position`) — the compile-time key you use to spawn, read, and query that component.

Marking the class **`partial`** lets Typhon's source generator add typed bulk accessors (`Unit.ReadAll` / `ReadWriteAll`). Archetypes can also inherit (`Archetype<TSelf, TParent>`) to share a common component prefix. An [entity](xref:concept-entity) is one instance of an archetype.

## How it relates

- **[Component](xref:concept-component)** — an archetype is a fixed set of them; it owns each one's `Comp<T>` handle.
- **[Entity](xref:concept-entity)** — an instance of an archetype, created with `Spawn<TArch>`.
- **[Query](xref:concept-query)** — queries are typed on an archetype: `tx.Query<Unit>()`.
- **[Schema evolution](xref:concept-schema-evolution)** — archetype membership and component versions are part of the persisted schema.
- **[Cluster storage](xref:concept-cluster-storage)** — the archetype's component mix silently decides its physical layout (clustered SoA vs legacy per-entity) — a ~50× bulk-iteration difference.

## In the API

- [Archetype&lt;T&gt;](xref:Typhon.Engine.Archetype`1) — the base class an archetype derives from; [`Register<T>()`](xref:Typhon.Engine.Archetype`1.Register*) declares slots.
- [Comp&lt;T&gt;](xref:Typhon.Engine.Comp`1) — the per-slot handle the archetype exposes.
- `[Archetype(id)]` — the identity attribute (declaration-only, not in the API reference).

## Learn & use

- **Narrative:** [Guide ch.1 — an archetype is the shape of an entity](xref:guide-first-app) · [ch.2 — modeling](xref:guide-modeling)
- **Feature detail:** [component collections](xref:feature-ecs-component-collections)
