---
uid: concept-query
title: 'Query'
description: 'A query asks a question of your data — by component shape, field value, or geometry — and runs against the transaction''s snapshot. Nothing executes until a terminal call; the engine picks a targeted, spatial, or broad scan.'
---

# Query

> **In one line:** a question you ask of your data — by component shape, field value, or geometry — evaluated against a [transaction](xref:concept-transaction)'s [snapshot](xref:concept-snapshot-isolation).

A query starts from `tx.Query<Unit>()`, which returns a builder you refine with chainable filters: `With`/`Without`/`Enabled` (component shape), `Where` (any predicate, broad scan) vs `WhereField` (an [indexed](xref:concept-index) field, targeted scan), and spatial predicates ([`WhereNearby`/`WhereInAABB`/`WhereRay`](xref:concept-spatial-index)). Nothing runs until a **terminal**: `Execute` (materialise ids), `Count`, `Any`, or `foreach`.

The engine picks the scan for you — *targeted* (index-driven, cheap as the archetype grows), *spatial*, or *broad* (linear) — guided by lightweight statistics it maintains. Keep a query result current over time by turning it into a [view](xref:concept-view).

## How it relates

- **[Archetype](xref:concept-archetype)** — a query is typed on one: `tx.Query<Unit>()`.
- **[Index](xref:concept-index)** — a `WhereField` on an indexed field drives the fast path.
- **[Spatial index](xref:concept-spatial-index)** — geometric predicates run off the spatial structure.
- **[View](xref:concept-view)** — a query kept current, with deltas.
- **[Snapshot isolation](xref:concept-snapshot-isolation)** — what a one-shot query sees.

## In the API

- [EcsQuery&lt;T&gt;](xref:Typhon.Engine.EcsQuery`1) — the query builder returned by [`tx.Query<T>()`](xref:Typhon.Engine.Transaction.Query*).

## Learn & use

- **Narrative:** [Guide ch.4 — querying & views](xref:guide-querying)
- **Feature detail:** [lookup & range scan](xref:feature-indexing-lookup-and-range-scan) · [spatial query API](xref:feature-spatial-spatial-query-api)
