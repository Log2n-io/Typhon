# Parallel Entity Processing (QuerySystem.Parallel)
> Multi-core entity chunking that skips per-chunk Transaction overhead for non-Versioned writes.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](./README.md)

## 🎯 What it solves

A game server with dozens of cores but only a handful of systems can't get throughput from DAG-level
parallelism alone — most of the win has to come from splitting one system's entity set across workers.
Doing that naively (materialize the full entity list, give every chunk its own `Transaction`) burns
cycles on list-copying and per-chunk Transaction setup/commit before any game logic runs. Parallel
entity processing removes both costs for the common case (writing non-`Versioned` components), so
`b.Parallel()` scales a `QuerySystem` without changing its `Execute` loop.

## ⚙️ How it works (in brief)

The runtime picks one of four dispatch paths from two facts about the system: whether it has a
`ChangeFilter` and whether it declares `WritesVersioned()`. Non-Versioned systems get a reusable
`PointInTimeAccessor` (PTA) — attached once per tick to a fresh MVCC snapshot — and each worker gets its
own `EntityAccessor` from it, so entity access has no per-entity dictionary lookup and no Transaction
commit. Unfiltered non-Versioned systems go further: the entity set is never materialized into an array
— each chunk iterates a contiguous slice of the View's underlying hash map directly. Filtered systems
(and any system writing `Versioned` components) materialize a list instead — dirty-only when filtered,
full-set otherwise — and Versioned writers get a real per-chunk `Transaction`, since the PTA cannot
perform copy-on-write.

| Path | Selected when | Prepare cost | Chunk entity access |
|------|---------------|--------------|----------------------|
| 1 — Full, non-Versioned | no `ChangeFilter`, no `WritesVersioned()` | O(1) — no list built | `ctx.Accessor` (PTA), zero-copy hash-map slice |
| 2 — Filtered, non-Versioned | `ChangeFilter` set, no `WritesVersioned()` | O(dirty count) | `ctx.Accessor` (PTA), pooled slice |
| 3 — Full, Versioned | no `ChangeFilter`, `WritesVersioned()` | O(entity count) | `ctx.Transaction`, pooled slice |
| 4 — Filtered, Versioned | `ChangeFilter` set, `WritesVersioned()` | O(dirty count) | `ctx.Transaction`, pooled slice |

## 💻 Usage

```csharp
public class MovementSystem : QuerySystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("Movement")
        .Input(() => activeUnitsView)
        .Parallel();                       // Path 1: no ChangeFilter, no WritesVersioned

    protected override void Execute(TickContext ctx)
    {
        foreach (var id in ctx.Entities)
        {
            var entity = ctx.Accessor.OpenMut(id);     // PTA — no per-entity dictionary lookup
            ref var pos = ref entity.Write<EcsPosition>();
            pos.X += entity.Read<EcsVelocity>().X * ctx.DeltaTime;
        }
    }
}

// Path 4 (filtered + Versioned write): same Configure, plus
// .ChangeFilter(typeof(EcsBuffs)).WritesVersioned()
// — Execute then uses ctx.Transaction.OpenMut(id) instead of ctx.Accessor.
```

| Option | Default | Effect |
|--------|---------|--------|
| `b.Parallel()` | off | Enables chunking; required for any of the four paths |
| `b.ChangeFilter(...)` | none | Narrows the dispatched set to dirty/Added entities (Paths 2/4) |
| `b.WritesVersioned()` | off | Switches chunk access from `ctx.Accessor` to a per-chunk `ctx.Transaction` (Paths 3/4) |
| `b.ChunksPerWorker(factor)` | `1.0` | Oversubscribes chunk count beyond worker count, range `[1.0, 64.0]` |

## ⚠️ Guarantees & limits

- `ctx.Accessor` reads all storage modes (Versioned via MVCC chain walk, SingleVersion/Transient
  direct) and writes SingleVersion/Transient, but **throws on a Versioned write** — declare
  `WritesVersioned()` instead.
- `ctx.Accessor` cannot Spawn, Destroy, Commit, or Rollback — structural changes need an upstream
  non-parallel system.
- Path 1 vs. a per-chunk `Transaction`: ~2.2x lower per-chunk overhead (PTA ~380µs/chunk vs.
  Transaction ~850µs/chunk) — only declare `WritesVersioned()` when actually needed.
- All workers in a tick see the same frozen MVCC snapshot (one TSN per `Attach()`); the PTA is reused
  across ticks and across all non-Versioned parallel systems with zero steady-state allocation.
- Scaling is good up to one CCD's worth of cores; cross-CCD `EntityMap` access flattens the curve on
  multi-CCD hardware (measured: ~89% efficiency at 8 workers, ~42% at 16 on a 2-CCD part).
- The DAG, not the runtime, guarantees chunks across different parallel systems don't race.
- Out of scope: Spawn/Destroy inside a parallel chunk; cross-system pipelining (next system's chunk K
  starting before this system fully completes).

## 🧪 Tests

- [ParallelQueryTests](../../../test/Typhon.Engine.Tests/Runtime/ParallelQueryTests.cs) — all four dispatch paths (`ParallelQuery_NonVersioned_ChunkReceivesAccessor`, `ParallelQuery_WritesVersioned_ChunkReceivesTransaction`), chunk partitioning, chunk-throw isolation
- [ChunksPerWorkerTests](../../../test/Typhon.Engine.Tests/Runtime/ChunksPerWorkerTests.cs) — `ChunksPerWorker` oversubscription factor vs. worker-count cap and entity-count cap

## 🔗 Related

- Sibling feature: [QuerySystem](./system-types/query-system.md)

<!-- Deep dive: claude/design/Runtime/06-parallel-efficiency.md -->
<!-- Deep dive: claude/overview/13-runtime.md — QuerySystem.Parallel, PointInTimeAccessor -->
