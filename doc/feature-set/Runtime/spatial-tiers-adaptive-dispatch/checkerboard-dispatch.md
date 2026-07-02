# Checkerboard (Red/Black) Dispatch
> Two-phase Red/Black parallel dispatch so neighbor-touching systems never race across a cell boundary.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](../README.md)

## 🎯 What it solves

Parallel dispatch normally hands different clusters to different worker threads with no ordering between
them. That's fine when a system only touches the entity it's iterating, but systems that read or write
neighboring cells too — pheromone diffusion, heat propagation — would race if two face-adjacent cells
ran on different workers at the same instant. Without engine support, the only safe fallback is running
such a system single-threaded, which throws away parallelism for exactly the systems most likely to be
expensive (full-grid passes).

## ⚙️ How it works (in brief)

Each cell is colored Red or Black from the parity of its grid coordinates, `(cellX + cellY) % 2`. No two
orthogonally-adjacent cells share a color, so the engine can dispatch all Red clusters in parallel, wait
for them to finish, then dispatch all Black clusters in parallel — a system that only touches
face-adjacent neighbors never observes a half-written neighbor. This is implemented as a single DAG node
with two internal phases, not two DAG nodes: after the Red phase's chunks all complete, the scheduler's
cleanup hook signals "re-dispatch," the same node runs again scoped to Black, and only then do successors
fire. From the DAG's perspective — and from every other system's perspective — checkerboard dispatch is
invisible structure inside one node.

## 💻 Usage

```csharp
var game = schedule.PublicTrack.DeclareDag("Game");

game.QuerySystem("Pheromone_Diffuse", ctx =>
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

// Equivalent class-based form (SystemBuilder):
// protected override void Configure(SystemBuilder b) => b.Name("Pheromone_Diffuse").Tier(SimTier.Near).Parallel().Checkerboard();
```

| Option | Default | Effect |
|---|---|---|
| `checkerboard:` (`QuerySystem`) / `b.Checkerboard()` (`SystemBuilder`) | `false` | Split the filtered cluster list into Red/Black and dispatch as two sequential parallel phases within one DAG node |

## ⚠️ Guarantees & limits

- Requires `parallel: true` — `Build()` rejects `checkerboard: true` without it, at schedule build time,
  not at first tick.
- Incompatible with `ChunkedParallel` — combining the two throws at build time.
- Composes with `tier:` / `CellAmortize` and dormancy — the split runs over whichever cluster set those
  filters already produced; sleeping clusters are excluded before coloring, same as for any other
  tier-filtered system.
- Red ∪ Black is always the full filtered cluster set with no overlap — every cluster is processed
  exactly once per tick, never zero times or twice.
- Protects **face adjacency only** — diagonal neighbors share the same color, so a system reading/writing
  diagonal cells (not just orthogonal ones) is not protected by the two-phase split.
- Non-spatial archetypes (no grid, or no cell mapping) degenerate to all clusters in Red and an empty
  Black phase — the callback still runs twice, but the second call sees no entities.
- One extra dispatch-prepare and worker barrier per tick versus a single-phase parallel system —
  negligible unless the per-phase entity work is already very cheap.

## 🧪 Tests

- [CheckerboardTests](../../../../test/Typhon.Engine.Tests/Runtime/CheckerboardTests.cs) — `Checkerboard_TwoPhases_BothExecute`, `Checkerboard_RedBlack_NoOverlap`, `Checkerboard_RequiresParallel_Throws`, `Checkerboard_WithDormancy_SleepingSkipped`

## 🔗 Related

- Source: [src/Typhon.Engine/Runtime/public/TyphonRuntime.cs](../../../../src/Typhon.Engine/Runtime/public/TyphonRuntime.cs) (`SplitCheckerboardClusters`, `OnParallelQueryPrepare`/`OnParallelQueryCleanup` two-phase protocol)
- Source: [src/Typhon.Engine/Runtime/public/SystemBuilder.cs](../../../../src/Typhon.Engine/Runtime/public/SystemBuilder.cs) (`Checkerboard()`), [Dag.cs](../../../../src/Typhon.Engine/Runtime/public/Dag.cs) (`checkerboard:` parameter)
- Parent feature: [Spatial Tiers & Adaptive Dispatch](./README.md)
- Same mechanism, cell/grid-configuration angle: [Spatial category — Checkerboard Dispatch](../../Spatial/checkerboard-dispatch.md)

<!-- Deep dive: claude/overview/13-runtime.md §Checkerboard Dispatch -->
<!-- ADR: claude/adr/046-spatial-tiers-architecture.md (Decision 6 — one DAG node, two internal phases) -->
<!-- Rules: claude/rules/spatial.md (module Checkerboard Partition, CB-01, CB-02) -->
