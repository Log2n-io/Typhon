# Checkerboard Dispatch
> Red/Black two-phase parallel dispatch so neighbor-touching systems never race across a cell boundary.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Spatial](./README.md)

## 🎯 What it solves

Some systems read or write data in cells adjacent to the one they're processing — pheromone diffusion
blending values with its neighbors is the canonical example. Dispatching all clusters to worker threads
in one pass, as [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md) normally does, lets two
adjacent cells run on different workers at the same instant and race on the shared boundary data. Without
a way to serialize neighbor access, game code is forced to make such systems single-threaded, losing
parallelism for exactly the systems most likely to be expensive (full-grid diffusion/propagation passes).

## ⚙️ How it works (in brief)

Each cell is colored Red or Black from the parity of its grid coordinates: `(cellX + cellY) % 2`. No two
orthogonally-adjacent (face-sharing) cells share a color, so all Red clusters can run in parallel safely,
then all Black clusters can run in parallel safely, and a system that only touches face-adjacent neighbors
never sees a half-written neighbor. `checkerboard: true` turns this into two sequential dispatch phases
inside the same DAG node — your callback runs twice per tick (once per phase), with `ctx.Entities` scoped
to that phase's clusters only. Downstream systems see one node and wait for both phases; nothing else in
the DAG needs to know dispatch happened twice.

## 💻 Usage

```csharp
schedule.QuerySystem("Pheromone_Diffuse", ctx =>
{
    // Safe to read/write neighboring cells' data — no other worker is touching an adjacent cell
    // during this phase.
    foreach (var id in ctx.Entities)
    {
        ref var pher = ref ctx.Accessor.OpenMut(id).Write(Pheromone.Data);
        pher.Value = Diffuse(pher.Value, NeighborSamples(ctx, id));
    }
},  input: () => pheromoneView,
    tier: SimTier.Near,
    parallel: true,
    checkerboard: true);
```

| Option | Default | Effect |
|---|---|---|
| `checkerboard:` (`QuerySystem`) / `b.Checkerboard()` (class-based) | `false` | Split the tier-filtered cluster list into Red/Black and dispatch as two sequential parallel phases |

## ⚠️ Guarantees & limits

- Requires `parallel: true` — registering `checkerboard: true` without it throws
  `InvalidOperationException` at schedule build time, not at first tick.
- Incompatible with `ChunkedParallel` — both throw if combined.
- Composes with `tier:`/`CellAmortize` — coloring is computed over whichever cluster set the tier filter
  (and dormancy) already produced; sleeping clusters are excluded before the split, same as any other
  tier-filtered system.
- Red ∪ Black is always the full filtered cluster set with no overlap — every cluster runs exactly once
  per tick, never zero or twice.
- Protects **face adjacency only** (2-color scheme). Diagonal neighbors share the same color — a system
  that reads/writes diagonal cells, not just orthogonal ones, is not protected by this 2-phase split.
- Non-spatial archetypes (no spatial grid, or the archetype has no cell mapping) degenerate to all
  clusters in the Red phase and an empty Black phase — the callback still runs twice, but the second
  call sees no entities.
- Overhead is one extra dispatch-prepare + one extra worker barrier per tick (~10-15μs) — negligible
  unless the per-phase entity processing itself is already very cheap.

## 🧪 Tests

- [CheckerboardTests](../../../test/Typhon.Engine.Tests/Runtime/CheckerboardTests.cs) — `Checkerboard_TwoPhases_BothExecute`, `Checkerboard_RedBlack_NoOverlap`, `Checkerboard_ZeroRedClusters_BlackStillRuns`, `Checkerboard_RequiresParallel_Throws`, `Checkerboard_WithDormancy_SleepingSkipped`, `Checkerboard_NoTierFilter_StillSplits`

## 🔗 Related

- Source: [src/Typhon.Engine/Runtime/public/Dag.cs](../../../src/Typhon.Engine/Runtime/public/Dag.cs) (`QuerySystem` `checkerboard:` parameter), [SystemBuilder.cs](../../../src/Typhon.Engine/Runtime/public/SystemBuilder.cs) (`Checkerboard()`), [TyphonRuntime.cs](../../../src/Typhon.Engine/Runtime/public/TyphonRuntime.cs) (`SplitCheckerboardClusters`, two-phase prepare/re-dispatch)
- Related catalog entry: [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md) (the tier filter checkerboard composes with)
- Sibling: [Checkerboard (Red/Black) Dispatch](../Runtime/spatial-tiers-adaptive-dispatch/checkerboard-dispatch.md) — same feature cataloged from the Runtime/dispatch angle rather than the spatial-grid angle

<!-- Deep dive: claude/design/Spatial/SpatialTiers/04-tick-integration.md § Checkerboard Parallel Dispatch -->
<!-- ADR: claude/adr/046-spatial-tiers-architecture.md (Decision 6 — one DAG node, two internal phases) -->
<!-- Rules: claude/rules/spatial.md (module Checkerboard Partition, CB-01, CB-02) -->
