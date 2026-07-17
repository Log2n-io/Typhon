---
uid: concept-component
title: 'Component'
description: 'A component is a plain blittable struct of data — the atom of the Typhon data model. It knows nothing about the engine; a [Component] attribute makes it storable.'
---

# Component

> **In one line:** a plain **blittable struct** of data — the atom of the Typhon data model.

A component is *just data*: public, blittable value-type fields, no base class, no interface. The `[Component("stable.name", revision)]` attribute makes it storable — the name is its schema identity, the revision its version (bumped when you [evolve](xref:concept-schema-evolution) it). Each component type also picks a [storage mode](xref:concept-storage-mode), which decides its ACID guarantees and write cost.

You never touch a component through the engine directly — you refer to it by its typed handle, a `Comp<T>`, obtained when an [archetype](xref:concept-archetype) registers it (`Unit.Health`). That handle is how you set values on `Spawn`, and `Read`/`Write` on an opened [entity](xref:concept-entity).

## How it relates

- **[Archetype](xref:concept-archetype)** — a fixed set of components; the archetype hands you each component's `Comp<T>` handle.
- **[Storage mode](xref:concept-storage-mode)** — chosen per component; decides isolation, durability, and cost.
- **[Index](xref:concept-index)** / **[Spatial index](xref:concept-spatial-index)** — a component *field* can be indexed for fast lookup.
- **[Schema evolution](xref:concept-schema-evolution)** — changing a component's fields is a versioned, migrated operation.
- **[Component collections](xref:concept-component-collection)** — a component field can hold a per-entity variable-length list (the one exception to fixed-size).

## In the API

- [Comp&lt;T&gt;](xref:Typhon.Engine.Comp`1) — the typed component handle you register and reference slots with.
- `[Component("name", revision, StorageMode = …)]` — the attribute that makes a struct storable (declaration-only, not in the API reference).

## Learn & use

- **Narrative:** [Guide ch.1 — components are plain structs](xref:guide-first-app) · [ch.2 — modeling](xref:guide-modeling)
- **Feature detail:** [component field declaration](xref:feature-schema-component-field-declaration) · [storage modes](xref:feature-ecs-storage-modes-index)
