---
uid: concept-entity-link
title: 'EntityLink'
description: 'EntityLink<T> is a typed, blittable reference from one entity to another — storable inside a component field, unlike a bare EntityId it carries the target archetype type.'
---

# EntityLink

> **In one line:** a **typed, blittable reference** from one [entity](xref:concept-entity) to another, storable inside a [component](xref:concept-component) field.

`EntityLink<T>` is how one entity points at another *by type*: a component field of type `EntityLink<Unit>` records "this entity's target is a `Unit`". Because it is blittable, it lives directly in the memory-mapped store like any other field — no serialization, no separate reference table. It carries the target archetype type at compile time, which a bare [`EntityId`](xref:concept-entity) does not.

## How it relates

- **[Entity](xref:concept-entity)** — what a link points at; resolve it back to an `EntityRef` through a [transaction](xref:concept-transaction).
- **[Component](xref:concept-component)** — an `EntityLink<T>` is a field type you put *inside* a component.
- **[Archetype](xref:concept-archetype)** — the `T` in `EntityLink<T>` names the target's archetype.

## In the API

- [EntityLink&lt;T&gt;](xref:Typhon.Engine.EntityLink`1) — the typed link field.

## Learn & use

- **Narrative:** [Guide ch.2 — fields](xref:guide-modeling)
- **Feature detail:** [component field declaration](xref:feature-schema-component-field-declaration)
