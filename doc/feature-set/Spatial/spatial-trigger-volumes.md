---
uid: feature-spatial-spatial-trigger-volumes
title: 'Trigger Volumes (Enter / Leave / Stay)'
description: 'Per-region occupant diffing against the per-cell cluster index emits Enter/Leave/Stay events at a configurable per-region frequency.'
---

# Trigger Volumes (Enter / Leave / Stay)
> Per-region occupant diffing against the per-cell cluster index emits Enter/Leave/Stay events at a configurable per-region frequency.

**Status:** ✅ Implemented · **Visibility:** Public · **Category:** [Spatial](./README.md)

## 🎯 What it solves

Games need transition events — the moment an entity enters or leaves a region — not just a point-in-time occupancy snapshot: capture zones, stealth detection, environmental hazards, loading boundaries all key off the edge, not the level. Polling every region against every nearby entity each tick to derive that edge by hand is wasteful and easy to get wrong (duplicate or missed events at the boundary). Trigger Volumes turn a region into a maintained query that does the diffing for the caller and only at the cadence the caller asks for.

## ⚙️ How it works (in brief)

A trigger region is a lightweight config slot — bounds, category mask, evaluation frequency — not an ECS entity or component. Each evaluation runs one AABB query through the per-cell cluster index for the region's box and category mask, collecting the matching entities of every cluster archetype that shares the spatial component, and compares that set against the set captured at the region's previous evaluation. An entity in the current set but not the previous one is an **Enter**; one in the previous set but not the current is a **Leave**; one in both is a **Stay**, counted but not materialized.

Occupancy is tracked **by entity id**, in a `HashSet<long>`, and that is a correctness requirement rather than a convenience: cluster storage has its own chunk-id namespace, so a bitmap indexed by the component table's would collide two different entities onto one bit and report neither transition. The two sets are double-buffered and swapped after each diff, so a steady-state evaluation allocates nothing once both have grown to their working size.

Evaluation is frequency-gated per region — a region is skipped unless `currentTick - lastEvaluatedTick >= EvaluationFrequency` — so cheap ambient zones and expensive per-tick damage fields can share the same system at different cadences. A region that has never been evaluated always evaluates on its first call, whatever its frequency. A 2D region is widened to infinite Z rather than being given the plane's own coordinates, so a 2D archetype and a 3D one both pass the Z overlap test on a query that did not ask about Z.

## 💻 Usage

```csharp
// The facade is a thin handle over engine-owned state — cheap to obtain, nothing to dispose.
SpatialTriggerVolumes triggers = dbe.SpatialTriggers<Position>();

double[] zoneBounds = { -10, -10, -10, 50, 50, 50 };   // minX,minY,minZ,maxX,maxY,maxZ (four doubles for a 2D component)
var zone = triggers.CreateRegion(zoneBounds, categoryMask: (uint)Faction.Player, evaluationFrequency: 5);

// once per tick, per region:
SpatialTriggerResult r = triggers.EvaluateRegion(zone, currentTick);
if (r.WasEvaluated)
{
    foreach (long entityId in r.Entered) { /* fire OnEnter */ }
    foreach (long entityId in r.Left)    { /* fire OnLeave */ }
    // r.StayCount — occupants unchanged since the previous evaluation
}

triggers.UpdateRegionBounds(zone, newBounds);       // occupant set is kept, so the next evaluation reports the difference
triggers.UpdateRegionCategoryMask(zone, newMask);
triggers.DestroyRegion(zone);
```

| `CreateRegion` arg | Default | Effect |
|---|---|---|
| `bounds` | required | `[minX, minY, maxX, maxY]` for a 2D component, `[minX, minY, minZ, maxX, maxY, maxZ]` for a 3D one |
| `categoryMask` | `0` | `0` = no filtering; non-zero admits a cluster on any bit overlap, the same semantic the cluster broadphase applies |
| `evaluationFrequency` | `1` | Minimum ticks between real evaluations; `0` is coerced to `1` |

## ⚠️ Guarantees & limits

- **Reachable from application code** — `dbe.SpatialTriggers<T>()` returns a public `SpatialTriggerVolumes` facade, and throws `InvalidOperationException` naming the component if it carries no `[SpatialIndex]` field. Application code reaches the system through that facade and not through an internal factory. A `default`-constructed facade is rejected by every member; check `IsValid` if you did not obtain it from the extension method.
- **Event completeness** — every outside→inside transition between two evaluations produces exactly one Enter, every inside→outside produces exactly one Leave; an entity inside at both evaluations produces neither, and is counted in `StayCount` only.
- **Frequency contract** — `EvaluationFrequency = N` guarantees at most one evaluation every N ticks. A skipped call returns a result whose `WasEvaluated` is `false`, in which case `Entered`, `Left` and `StayCount` are not meaningful. A freshly created region always evaluates on its first call regardless of N.
- **Result spans are transient, and shared across regions** — `Entered` and `Left` are `ReadOnlySpan<long>` over the system's result buffers, valid only until the next `EvaluateRegion` call on that system, whichever region it names. Copy entity ids out before yielding control if they need to outlive the call.
- **Both cell halves are always evaluated** — a region cannot be scoped to static or dynamic clusters, and no argument narrows the walk to one half. One query visits both halves of each cell it opens.
- **Destroyed handles are rejected** — `DestroyRegion` bumps the slot's generation; any further use of the old handle, including a double-destroy, throws `ArgumentException`. The free-list link lives in its own field rather than over the generation counter, so recycling a slot never walks that counter backwards and a destroyed handle can never validate against the slot that replaced it.
- **Do not hold the facade across a schema migration** — the underlying state is created on first use and lives as long as the component's `ComponentTable`, which is the engine's lifetime in ordinary use but not across a migration that reconstructs the table.
- **Cost tracks occupants and cluster count** — each evaluation is one cell walk plus a set diff, so it scales with how many clusters the region's cells hold and how many entities they yield, not with world size. Per-tick total cost is bounded by staggering `EvaluationFrequency` across regions rather than evaluating all of them every tick. *No per-region measurement is on record.*

## 🧪 Tests

- [SpatialTriggerTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialTriggerTests.cs) — `Enter_SpawnInsideRegion`, `Leave_DestroyEntity` and `StayInside_NoEnterLeaveEvents` for event completeness; `EvalFrequency_SkipsTicks` for the frequency contract; `CreateRegion_HandleReuse_GenerationPreventsStaleAccess` for the generation check; `CategoryMask_FiltersNonMatchingEntities`, `UpdateBounds_NewEntitiesEnterLeave`, `MultipleRegions_IndependentTracking` and `LargeScale_MultipleRegions_EventCorrectness`; `StaticEntity_EntersOnceThenStays` and `DestroyingAStaticEntity_ReportsItAsLeaving` cover a static-mode archetype

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/public/SpatialObservers.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialObservers.cs) (the public `SpatialTriggerVolumes` facade and the `SpatialTriggers<T>()` extension)
- Source: [src/Typhon.Engine/Spatial/internals/SpatialTriggerSystem.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialTriggerSystem.cs) (region storage, the occupant diff, frequency gating)
- Source: [src/Typhon.Engine/Spatial/public/SpatialRegionHandle.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialRegionHandle.cs), [src/Typhon.Engine/Spatial/public/SpatialTriggerResult.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialTriggerResult.cs)
- Related catalog entry: [Spatial Category Filtering](./spatial-category-filtering.md), [Cluster Spatial Queries](./cluster-spatial-queries.md) (the walk each evaluation runs)
- Related catalog entry: [Interest Management (Delta Spatial Queries)](./spatial-interest-management.md) — the sibling system on the same index, reached the same way

<!-- Deep dive: claude/design/Spatial/SpatialIndex/08-game-features.md (Feature F3 — Trigger Volumes: algorithm, frequency budget) -->
<!-- Rules: rules/spatial.md (Module: Trigger Volumes — TV-01 event completeness and entity-id occupancy, TV-02 frequency contract; IM-04 for the public entry point) -->
