---
uid: feature-spatial-fat-aabb-update
title: 'Fat-AABB Incremental Update'
description: 'Margin-enlarged bounds absorb small moves for ~25ns, with no tree mutation.'
---

# Fat-AABB Incremental Update
> Margin-enlarged bounds absorb small moves for ~25ns, with no tree mutation.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Spatial](./README.md)

## 🎯 What it solves

A naive spatial index removes and reinserts an entity into the R-Tree every time it moves, even a single unit. At game-tick frequency with thousands of moving entities, that means a full tree mutation (MBR refit, possible split/merge) per entity per tick — the dominant cost of keeping a spatial index live. Fat-AABB Incremental Update absorbs the overwhelming majority of small, continuous motion (walking, patrolling, physics jitter) without touching the tree at all, so the per-tick spatial maintenance budget stays flat regardless of how many entities are merely drifting inside their own neighborhood.

## ⚙️ How it works (in brief)

Each indexed entity's true (tight) bounds are enlarged by a fixed margin into a "fat" AABB, which is what's actually stored in the R-Tree leaf. On each tick/commit, the engine checks whether the entity's new tight bounds are still contained inside its fat AABB — a handful of comparisons, no tree access. If contained, nothing else happens. If the entity has moved far enough to escape its fat AABB, the engine falls back to a full remove-then-reinsert with a freshly enlarged fat AABB around the new position. The margin is fixed per component field for the life of the schema; it does not adapt at runtime.

## 💻 Usage

```csharp
public struct Position
{
    [SpatialIndex(margin: 0.5f)]
    public float X, Y, Z;
}

[Archetype]
partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Pos = Register<Position>();
}

// No extra API to call — fast/slow path selection happens automatically
// whenever the indexed field changes, at tick fence (SV) or commit (Versioned).
using var wtx = dbe.CreateQuickTransaction();
EntityRef m = wtx.OpenMut(entityId);
ref Position p = ref m.Write(Unit.Pos);
p.X += dx; p.Y += dy; p.Z += dz;
wtx.Commit();
```

| `[SpatialIndex]` arg | Default | Effect |
|---|---|---|
| `margin` | required | Half-width added to each side of the tight AABB; larger margin = fewer escapes, wider (less precise) candidate sets |

| Entity profile | Recommended margin |
|---|---|
| Slow movers (NPCs, buildings) | 0–5 units |
| Normal movers (players, vehicles) | 5–20 units |
| Fast movers (projectiles) | 0.5–2 units |

## ⚠️ Guarantees & limits

- **Fast path ~25ns** (containment check, no tree access) when the move stays inside the margin; this is the common case — **90%+ of per-tick moves** with a reasonably chosen margin.
- **Slow path ~500–700ns** (remove + reinsert with re-enlarged fat AABB) on escape; typically **5–10% of moves**.
- **Margin is fixed at schema definition time** — set once via `[SpatialIndex(margin: ...)]`, no runtime auto-tuning. Too small inflates escape rate (more tree mutations); too large inflates false positives in range queries (every query candidate must still pass an exact-bounds post-filter).
- **Engine logs a warning** when an indexed table's escape rate exceeds 10%, naming the table — signal to raise that field's margin.
- **Transparent to query results** — queries always test against the entity's exact stored bounds, not the fat AABB; the fat AABB only affects index maintenance cost, never correctness.
- **No action needed on teleports/large jumps** — these simply always take the slow path; correctness is unaffected, only cost.

## 🧪 Tests

- [ClusterSpatialTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ClusterSpatialTests.cs) — `TickFence_SmallMove_NoEscape_FastPath` (containment, no tree mutation) and `TickFence_MovedEntity_FatAABBEscape_RTreeUpdated` (remove+reinsert on escape)
- [SpatialPerfTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialPerfTests.cs) — `Bench_ContainmentCheck_2Df32` measures the fast-path containment check cost

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/internals/SpatialMaintainer.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialMaintainer.cs)
- Source: [src/Typhon.Engine/Spatial/internals/SpatialBackPointer.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialBackPointer.cs)
- Sibling: [Spatial R-Tree Index](./spatial-rtree-index/README.md) — the tree structure fat-AABB entries are stored and refit in
- Sibling: [Static / Dynamic Tree Separation](./spatial-rtree-index/spatial-rtree-static-dynamic.md) — only `Dynamic`-mode fields receive fat-AABB maintenance; `Static` fields never do

<!-- Deep dive: claude/design/Spatial/SpatialIndex/03-tree-operations.md § Fat AABB Update Protocol, Back-Pointer Storage -->
<!-- Deep dive: claude/adr/044-spatial-rtree-architecture.md (update-strategy rationale) -->
