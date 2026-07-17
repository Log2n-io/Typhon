---
uid: concept-component-collection
title: 'Component collections'
description: 'A per-entity variable-length list — owned values or entity-reference lists — stored outside the fixed-size component via a 4-byte buffer id and a shared per-element-type pool. The one controlled exception to fixed-size components.'
---

# Component collections

> **In one line:** a per-entity **variable-length list** on a component — the one controlled exception to Typhon's fixed-size, blittable component rule.

[Components](xref:concept-component) are fixed-size blittable structs stored column-major, so there's no room for a list of unknown length — a path's waypoints, an inventory, a parent's children. A `ComponentCollection<T>` field adds exactly that. On the struct it's just **4 bytes** (a buffer id); the elements live in a separate pool shared by every `ComponentCollection<T>` field of that element type `T` across all archetypes. `T` must be `unmanaged` — the same blittability constraint as any component field. Two flavors: **owned value data** (`ComponentCollection<Waypoint>`) and **entity-reference lists** (`ComponentCollection<EntityLink<TArch>>`, the collection-on-parent side of a 1:N [relationship](xref:concept-entity-link)).

The API is append-and-bulk-read: mutate through `Transaction.CreateComponentCollectionAccessor` (`Add`, `ElementCount`, `GetAllElements`); iterate read-only through `Transaction.GetReadOnlyCollectionEnumerator`. There is no per-element remove/replace, and no index over contents. Storage follows the component's [mode](xref:concept-storage-mode): `SingleVersion` owns its buffer in place; `Versioned` shares by reference count and clones **copy-on-write** on first mutation, so an older MVCC snapshot keeps seeing what it read. `Transient` is **rejected at startup** — its buffer would outlive the data meant to reference it.

> ⚠️ **Durability is checkpoint-bounded, not commit-bounded.** Collection *content* reaches disk at the next checkpoint, not via WAL redo — a crash between a collection write and that checkpoint recovers the buffer id but not the new elements. This is a known, separately-tracked gap; don't treat collection writes as commit-durable.

## How it relates

- **[Component](xref:concept-component)** — the field lives on a component; this is what a component can hold beyond fixed-size fields.
- **[EntityLink](xref:concept-entity-link)** — a `ComponentCollection<EntityLink<T>>` is the 1:N parent-side list.
- **[Storage mode](xref:concept-storage-mode)** — `SingleVersion` in-place, `Versioned` copy-on-write, `Transient` unsupported.
- **[Cluster storage](xref:concept-cluster-storage)** — the 4-byte field lives in the cluster slot like any other; the element pool sits beside it.

## In the API

- [`ComponentCollection<T>`](xref:Typhon.Schema.Definition.ComponentCollection`1) — the 4-byte field (`T : unmanaged`).
- [`Transaction.CreateComponentCollectionAccessor(ref field)`](xref:Typhon.Engine.Transaction.CreateComponentCollectionAccessor*) — `Add` / `ElementCount` / `GetAllElements`; [`Transaction.GetReadOnlyCollectionEnumerator(ref field)`](xref:Typhon.Engine.Transaction.GetReadOnlyCollectionEnumerator*) — allocation-free `foreach`.

## Learn & use

- **Feature detail:** [Component collections](xref:feature-ecs-component-collections)
- **Narrative:** [Guide ch.2 — modeling](xref:guide-modeling)
