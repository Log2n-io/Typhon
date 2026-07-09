---
uid: feature-spatial-spatial-trigger-volumes
title: 'Trigger Volumes (Enter / Leave / Stay)'
description: 'Per-region occupant diffing against the R-Tree(s) emits Enter/Leave/Stay events at a configurable per-region frequency.'
---

# Trigger Volumes (Enter / Leave / Stay)
> Per-region occupant diffing against the R-Tree(s) emits Enter/Leave/Stay events at a configurable per-region frequency.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Spatial](./README.md)

## 🎯 What it solves

Games need transition events — the moment an entity enters or leaves a region — not just a point-in-time occupancy snapshot: capture zones, stealth detection, environmental hazards, loading boundaries all key off the edge, not the level. Polling every region against every nearby entity each tick to derive that edge by hand is wasteful and easy to get wrong (duplicate or missed events at the boundary). Trigger Volumes turn a region into a maintained query that does the diffing for the caller and only at the cadence the caller asks for.

## ⚙️ How it works (in brief)

A trigger region is a lightweight config slot (bounds, category mask, evaluation frequency, target-tree mode) — not an ECS entity or component. Each evaluation queries the spatial tree(s) for the region's AABB and category mask, builds a bitmap of current occupants, and XORs it against the bitmap captured at the region's previous evaluation: set-but-not-previously-set bits are Enter, previously-set-but-not-set bits are Leave, the rest are Stay (count only, not materialized). Evaluation is frequency-gated per region — a region is skipped unless `currentTick - lastEvaluatedTick >= EvaluationFrequency` — so cheap ambient zones and expensive per-tick damage fields can share the same system at different cadences. `TargetTreeMode` controls which tree(s) are queried: `DynamicOnly` (default) never touches the static tree; `Both`/`StaticOnly` query the static tree once and cache the result, only re-querying it when the static tree mutates or the region's bounds/category change. Spatial-grid cluster archetypes registered on the same index are diffed too, tracked by EntityId rather than by R-Tree chunk id.

## 💻 Usage

Trigger regions are reached through the per-table spatial index state, which is engine-internal today (see Guarantees below) — this is the actual API, shown as the test suite exercises it:

```csharp
var table = dbe.GetComponentTable<Position>();
var triggers = table.SpatialIndex.GetOrCreateTriggerSystem(table);

double[] zoneBounds = { -10, -10, -10, 50, 50, 50 };   // minX,minY,minZ,maxX,maxY,maxZ
var zone = triggers.CreateRegion(zoneBounds, categoryMask: (uint)Faction.Player,
    evaluationFrequency: 5, targetTree: TargetTreeMode.DynamicOnly);

// once per tick, per region:
SpatialTriggerResult r = triggers.EvaluateRegion(zone, currentTick);
if (r.WasEvaluated)
{
    foreach (long entityId in r.Entered) { /* fire OnEnter */ }
    foreach (long entityId in r.Left)    { /* fire OnLeave */ }
    // r.StayCount — occupants unchanged since the previous evaluation
}

triggers.UpdateRegionBounds(zone, newBounds);       // invalidates static-tree cache
triggers.UpdateRegionCategoryMask(zone, newMask);   // invalidates static-tree cache
triggers.DestroyRegion(zone);
```

| `TargetTreeMode` | Default | Effect |
|---|---|---|
| `DynamicOnly` | yes | Queries the dynamic tree only; static tree never touched |
| `Both` | — | Dynamic tree every evaluation + cached static-tree result, merged |
| `StaticOnly` | — | Static tree only, fully cached after the first evaluation |

## ⚠️ Guarantees & limits

- **Engine-internal only today** — `SpatialTriggerSystem`, `ComponentTable.SpatialIndex`, and `GetOrCreateTriggerSystem` are all `internal`; there is no public `DatabaseEngine`/ECS entry point yet, so application code outside the engine assembly cannot create or evaluate trigger regions. `SpatialRegionHandle`, `SpatialTriggerResult`, and `TargetTreeMode` are public types, ready for a future public wrapper.
- **Event completeness (rule TV-01)** — every outside→inside transition between two evaluations produces exactly one Enter, every inside→outside produces exactly one Leave; an entity inside at both evaluations produces neither (it's counted in `StayCount` only).
- **Frequency contract (rule TV-02)** — `EvaluationFrequency = N` guarantees at most one evaluation every N ticks. A skipped evaluation returns `SpatialTriggerResult.Skipped` (`WasEvaluated == false`; `Entered`/`Left`/`StayCount` are not meaningful). A freshly created region always evaluates on its first call regardless of N.
- **Result spans are transient** — `Entered`/`Left` are `ReadOnlySpan<long>` over pooled buffers, valid only until the next `EvaluateRegion` call on that system; copy entity ids out before yielding control if they need to outlive the call.
- **Static-tree caching, not re-querying** — `Both`/`StaticOnly` regions re-run the static query only when the static tree's mutation version changes or the region's bounds/category mask are updated; steady-state evaluations reuse the cached bitmap.
- **Cluster archetypes are included automatically** — if the table's spatial index has spatial-grid cluster archetypes registered, their entities are queried and diffed in the same `EvaluateRegion` call, tracked by EntityId in a separate set from the R-Tree bitmap.
- **Destroyed handles are rejected** — `DestroyRegion` bumps the slot's generation; any further use of the old handle (including a double-destroy) throws `ArgumentException`.
- **Designed for ~800ns per region** at ~50 occupants (AABB query + bitmap diff); cost tracks occupant count and tree depth, and per-tick total cost is bounded by staggering `EvaluationFrequency` across regions rather than evaluating all regions every tick.

## 🧪 Tests

- [SpatialTriggerTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialTriggerTests.cs) — region lifecycle + generation-checked handle reuse, `Enter`/`Leave`/`StayInside` event completeness, `EvalFrequency_SkipsTicks`

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/internals/SpatialTriggerSystem.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialTriggerSystem.cs)
- Source: [src/Typhon.Engine/Spatial/public/SpatialRegionHandle.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialRegionHandle.cs) (`TargetTreeMode`)
- Source: [src/Typhon.Engine/Spatial/public/SpatialTriggerResult.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialTriggerResult.cs)
- Related catalog entry: [Spatial Category Filtering](./spatial-category-filtering.md), [Spatial R-Tree Index](./spatial-rtree-index/README.md)

<!-- Deep dive: claude/design/Spatial/SpatialIndex/08-game-features.md (Feature F3 — Trigger Volumes: algorithm, static-cache strategy, frequency budget) -->
<!-- Rules: claude/rules/spatial.md (Module: Trigger Volumes — TV-01 event completeness, TV-02 frequency contract) -->
