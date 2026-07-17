---
uid: concept-index
title: 'Index (secondary)'
description: 'Marking a component field [Index] makes Typhon maintain a sorted B+Tree so it can be looked up directly instead of scanned. Unique or multi-value; maintained for you.'
---

# Index (secondary)

> **In one line:** mark a [component](xref:concept-component) field `[Index]` and Typhon maintains a sorted **B+Tree** so it can be looked up directly instead of scanned.

A plain field can only be found by scanning the archetype. `[Index]` builds and maintains an index behind the scenes; a [query](xref:concept-query) that *targets that field* (`WhereField<Team>(t => t.Id == 3)`) is served from the index rather than a broad scan, and stays cheap as the archetype grows. `[Index]` is **unique** (a duplicate key throws `UniqueConstraintViolationException`); `[Index(AllowMultiple = true)]` lets many entities share a value.

You never touch the tree — you declare the index and filter on the field. An [`IndexRef`](xref:Typhon.Engine.IndexRef) is the handle when you resolve one directly.

## How it relates

- **[Component](xref:concept-component)** — an index is declared on a component *field*.
- **[Query](xref:concept-query)** — a `WhereField` filter on an indexed field drives a targeted (fast) scan.
- **[View](xref:concept-view)** — an indexed `WhereField` predicate backs an *incremental* live view.
- **[Storage mode](xref:concept-storage-mode)** — index freshness/maintenance timing follows the component's mode.
- **[Spatial index](xref:concept-spatial-index)** — the geometric sibling, for "what's near here?".

## In the API

- [`IndexRef`](xref:Typhon.Engine.IndexRef) — the index-resolution handle.
- `[Index]` / `[Index(AllowMultiple = true)]` — the field attributes (declaration-only, not in the API reference).

## Learn & use

- **Narrative:** [Guide ch.2 §3 — indexes](xref:guide-modeling)
- **Feature detail:** [indexing](xref:feature-indexing-index) · [lookup & range scan](xref:feature-indexing-lookup-and-range-scan) · [secondary-index storage modes](xref:feature-indexing-secondary-index-storage-modes-index)
