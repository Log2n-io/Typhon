---
uid: concept-view
title: 'View'
description: 'A view is a query result you keep and refresh, and that reports what changed each time — Added / Removed / Modified. Incremental when backed by an indexed predicate, pull otherwise. The basis for reactive systems and subscriptions.'
---

# View

> **In one line:** a [query](xref:concept-query) result you **keep and refresh**, that reports what changed each time.

Where a one-shot query is a snapshot answer, a view (`tx.Query<Unit>()…ToView()`) is a result set you hold: `Refresh(tx)` brings it up to date, `GetDelta()` returns the `Added` / `Removed` / `Modified` entity keys, and `ClearDelta()` resets for the next cycle. That delta is exactly what a reactive [system](xref:concept-system) or a UI needs.

A view built on an [indexed](xref:concept-index) `WhereField` predicate updates **incrementally** — the engine moves only the entities that crossed the boundary. A view on a free `Where` is a **pull** view, recomputed on `Refresh`. Same delta either way; the difference is cost. A view is also the input a [`QuerySystem`](xref:concept-system) runs over, and what a [subscription](xref:concept-subscription) publishes to clients.

## How it relates

- **[Query](xref:concept-query)** — a view is a query made durable-over-time.
- **[Index](xref:concept-index)** — an indexed predicate makes the view incremental.
- **[System](xref:concept-system)** — a `QuerySystem` takes a view as its input set.
- **[Subscription](xref:concept-subscription)** — publishes a view's deltas to remote clients.

## In the API

- [EcsView&lt;T&gt;](xref:Typhon.Engine.EcsView`1) — [`Contains`](xref:Typhon.Engine.EcsView`1.Contains*) / [`Refresh`](xref:Typhon.Engine.EcsView`1.Refresh*) / `GetDelta` / `ClearDelta`, and `foreach`.

## Learn & use

- **Narrative:** [Guide ch.4 §3 — live views](xref:guide-querying)
- **Feature detail:** [reactive dispatch & change filters](xref:feature-runtime-reactive-dispatch-change-filters) · [published views](xref:feature-subscriptions-published-views-index)
