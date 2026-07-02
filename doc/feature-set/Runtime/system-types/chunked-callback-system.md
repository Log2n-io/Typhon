# ChunkedCallbackSystem — Chunk-Parallel Non-Entity Work
> Fan a CallbackSystem's body out across N workers for SIMD sweeps, reductions, and other non-entity chunkable work.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](../README.md)

## 🎯 What it solves

Some per-tick work is naturally parallel but isn't entity iteration at all — sweeping a flat pheromone grid,
downsampling an image buffer, reducing an array into a heat accumulator. Routing that through `QuerySystem.Parallel`
would mean paying for entity-prep machinery (View input, `Accessor`, per-chunk `Transaction`) the work never
uses. `ChunkedCallbackSystem` gives `CallbackSystem` the same N-way worker fan-out without any of that overhead —
just a chunk index and count.

## ⚙️ How it works (in brief)

Derive from `ChunkedCallbackSystem` and call `b.ChunkedParallel(chunkCount)` in `Configure`; `Execute` is then
invoked `chunkCount` times in parallel across workers, each call reading `ctx.ChunkIndex`/`ctx.ChunkCount` to
compute its own slice of whatever data structure the system owns. There's no `Accessor`, no `Entities`, no
per-chunk `Transaction` — `TickContext` carries only tick metadata plus the chunk coordinates.
`ChunkedCallbackSystem<TContext>` adds a typed ambient context (bound once via `scheduler.RegisterContext(ctx)`
after `Build()`) with two extra hooks evaluated before any worker claims a chunk: `ShouldRun(TContext)` to gate
the whole system on typed state, and `Prepare(TContext)` to build a per-tick plan and return a dynamic chunk
count (`0` skips, `>0` dispatches that many chunks, `-1` defers to the static `ChunkedParallel(N)` count).

## 💻 Usage

```csharp
public sealed class PheroMaxReduce : ChunkedCallbackSystem
{
    private readonly float[] _pheromoneGrid;
    private float[] _heatAccum;

    protected override void Configure(SystemBuilder b) => b
        .Name("PheroMaxReduce")
        .ReadsResource("PheromoneGrid")
        .WritesResource("HeatFoodAccum")
        .ChunkedParallel(chunkCount: 16);

    protected override void Execute(TickContext ctx)
    {
        int len = _pheromoneGrid.Length;
        int start = (int)((long)ctx.ChunkIndex * len / ctx.ChunkCount);
        int end = (int)((long)(ctx.ChunkIndex + 1) * len / ctx.ChunkCount);
        for (int i = start; i < end; i++)
        {
            _heatAccum[i] = MathF.Max(_heatAccum[i], _pheromoneGrid[i]);
        }
    }
}

dag.Add(new PheroMaxReduce());
```

Typed variant — a per-tick plan gates the chunk count dynamically:

```csharp
public sealed class WaveSimulationStep : ChunkedCallbackSystem<SimContext>
{
    protected override void Configure(SystemBuilder<SimContext> b) => b
        .Name("WaveStep")
        .ChunkedParallel(chunkCount: 8);   // static fallback if Prepare returns -1

    // Evaluated before any worker claims a chunk; false skips the system entirely.
    protected override bool ShouldRun(SimContext ctx) => ctx.WaveCount > 0;

    // Returns the dynamic chunk count for this tick: 0 = skip, >0 = dispatch, -1 = use the static count.
    protected override int Prepare(SimContext ctx) => Math.Min(ctx.WaveCount, 32);

    protected override void Execute(TickContext ctx)
    {
        // Context property (from the base class) exposes the bound typed context.
        var slice = Context.GetSlice(ctx.ChunkIndex, ctx.ChunkCount);
        // ... process slice ...
    }
}

var scheduler = dag.Build(parentResource);
scheduler.RegisterContext(new SimContext(...));   // must run after Build(), before Start()
scheduler.Start();
```

| Option | Default | Effect |
|--------|---------|--------|
| `b.ChunkedParallel(chunkCount)` | n/a (required) | Static chunk count; `Execute` runs this many times in parallel |
| `Prepare(TContext)` returns `>0` | — | Overrides the static count for this tick only |
| `Prepare(TContext)` returns `0` | — | Skips the system this tick (successors still dispatch) |
| `Prepare(TContext)` returns `-1` | default | No opinion — falls back to the static `ChunkedParallel(N)` count |

## ⚠️ Guarantees & limits

- `ChunkedParallel` is mutually exclusive with every entity-context builder method — `Input`, `ChangeFilter`,
  `WritesVersioned`, `Tier`, `CellAmortize`, `Checkerboard`, `ChunksPerWorker` — `Build()` rejects the
  combination.
- Valid only on `CallbackSystem` (and its `ChunkedCallbackSystem`/`ChunkedCallbackSystem<TContext>`
  subclasses) — not on `QuerySystem` or `PipelineSystem`.
- A typed system (`ChunkedCallbackSystem<TContext>`) must be bound via `scheduler.RegisterContext<TContext>(ctx)`
  after `Build()` and before `Start()` — `Start()` throws if a typed system was registered without a matching
  `RegisterContext` call.
- `ShouldRun`/`Prepare` run once per system per tick, single-threaded, before any worker claims a chunk — they
  are not re-entered per chunk and must not block.
- Each `Execute` invocation runs concurrently with the others for the same system — slicing must be
  partition-correct (no two chunk indices touching overlapping data); the runtime does not check for overlap.
- No `Transaction`, no `Accessor`, no `ctx.Entities` — this type is for plain memory/array work, not entity
  reads/writes. Use `QuerySystem.Parallel` when the work is per-entity.

## 🧪 Tests

- [TypedContextSystemTests](../../../../test/Typhon.Engine.Tests/Runtime/TypedContextSystemTests.cs) — `ChunkedCallbackSystem<TContext>` binding via `RegisterContext`, typed `ShouldRun`/`Prepare` (skip/dispatch/-1 fallback), unbound-system `Start()` throw, untyped variant still works

## 🔗 Related

- Parent feature: [System Types](./README.md)
- Sibling: [CallbackSystem](./callback-system.md) — the proactive base type this specializes for chunk-parallel non-entity work.

<!-- Deep dive: claude/design/Runtime/02-system-scheduling.md — ChunkedCallbackSystem -->
<!-- Deep dive: claude/overview/13-runtime.md — ChunkedCallbackSystem, Parallel Tick Fence -->
