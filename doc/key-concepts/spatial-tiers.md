---
uid: concept-spatial-tiers
title: 'Spatial tiers & adaptive dispatch'
description: 'Per-cluster simulation tiers let systems run near entities every tick and far ones at reduced, amortized, or dormant rates — routed by grid cell, at cluster granularity, so cost scales with active clusters not entity count.'
---

# Spatial tiers & adaptive dispatch

> **In one line:** run systems at **full rate near, reduced rate far** — per spatial cell, at [cluster](xref:concept-cluster-storage) granularity, so the cost of tiering scales with *active clusters*, never entity count.

A large world can't simulate every entity at full frequency, and a per-entity distance check every tick is itself O(N). Instead, game code assigns one of four `SimTier` flags (`Tier0`..`Tier3`) to each [spatial](xref:concept-spatial-index) grid cell once per tick (via `TickContext.SpatialGrid`), and the engine builds — at tick start, skipped when nothing changed — a per-archetype index of active clusters grouped by tier. Three composable mechanisms read it: a system's **`Tier(...)` filter** restricts it to matching clusters (with `CellAmortize(N)` to process only 1/N of them per tick); **`Checkerboard()`** splits a tier-filtered set into two conflict-free Red/Black phases for systems that touch neighboring cells; and **cluster dormancy** puts clusters untouched for N ticks to sleep, dropping them from every dispatch path at zero cost until a write wakes them.

All three compose, engine-managed — a system can be tier-filtered, amortized, and checkerboard-dispatched at once, with dormancy filtering underneath. The trade is **one tick of staleness**: a tier change, a migration, or a wake takes effect at the *next* tick's dispatch.

## How it relates

- **[Cluster storage](xref:concept-cluster-storage)** — tiers operate on cluster lists, not entities; this is why cost is O(active clusters).
- **[Spatial index](xref:concept-spatial-index)** — the grid whose cells carry the `SimTier` assignment.
- **[TickContext](xref:concept-tick-context)** — where you assign tiers (`SpatialGrid`) and read `TierBudgetMetrics`.
- **[System](xref:concept-system)** — the `Tier` / `CellAmortize` / `Checkerboard` filters are declared on it.

## In the API

- [`SimTier`](xref:Typhon.Engine.SimTier) (`Tier0`..`Tier3`) — the per-cell tier flag, assigned via [`TickContext.SpatialGrid`](xref:Typhon.Engine.TickContext.SpatialGrid).
- [`SystemBuilder.Tier(...)`](xref:Typhon.Engine.SystemBuilder.Tier*) / [`.CellAmortize(n)`](xref:Typhon.Engine.SystemBuilder.CellAmortize*) / [`.Checkerboard()`](xref:Typhon.Engine.SystemBuilder.Checkerboard*) — the dispatch scoping.
- [`TickContext.TierBudgetMetrics`](xref:Typhon.Engine.TickContext.TierBudgetMetrics) — per-tier wall-clock + entity counts from the previous tick, for adaptive tier boundaries.

## Learn & use

- **Feature detail:** [Spatial tiers & adaptive dispatch](xref:feature-runtime-spatial-tiers-adaptive-dispatch-index) — [tier-filtered & amortized](xref:feature-runtime-spatial-tiers-adaptive-dispatch-tier-filtered-amortized-dispatch) · [cluster dormancy](xref:feature-runtime-spatial-tiers-adaptive-dispatch-cluster-dormancy) · [checkerboard dispatch](xref:feature-runtime-spatial-tiers-adaptive-dispatch-checkerboard-dispatch)
- **Narrative:** [Guide ch.5 — systems](xref:guide-systems)
