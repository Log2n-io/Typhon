# PipelineSystem
> Reactive multi-stage gather/process/scatter system for bulk entity processing — full execution model pending Patate.

**Status:** ✅ Implemented (chunk-dispatch only) · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](../README.md)

## 🎯 What it solves

`QuerySystem.Parallel` chunks an entity-by-entity loop across workers, but it still pays per-entity overhead
(View iteration, `EntityRef` indirection) for each access. Truly high-throughput bulk processing — physics over
thousands of bodies, vectorized batch updates — wants a SoA gather → process → scatter pipeline instead of a
per-entity loop. `PipelineSystem` is the system type reserved for that shape: multi-worker chunk-parallel
execution with no `TickContext` entity machinery at all.

## ⚙️ How it works (in brief)

Today, `PipelineSystem` provides fixed-chunk-count parallel dispatch: register a chunk action and a total chunk
count, and the runtime distributes chunk indices across workers exactly like `ChunkedCallbackSystem` does — no
`Transaction`, no `Accessor`, no `ctx.Entities`. The full design (View-driven reactive skip, `ChangeFilter`,
internally-managed per-worker Transactions for the gather/scatter phases) is specified in the design doc but
**not yet wired**: the lambda registration accepts `input:`/`changeFilter:` parameters for forward
compatibility, but they are not consumed by the scheduler today, and **class-based registration is rejected
outright**. The execution model is deferred to the Patate design.

## 💻 Usage

```csharp
// The only working registration path today — lambda, fixed chunk count, no entity context.
dag.PipelineSystem("Physics", (chunkIndex, totalChunks) =>
{
    // chunkIndex / totalChunks — compute this chunk's slice of whatever data the pipeline owns.
    // Gather/Process/Scatter against entities would create their own Transaction internally (not provided).
}, totalChunks: 8, after: "Movement");
```

```csharp
// Class-based skeleton — Configure works, but registering it throws today:
public class RenderPipeline : PipelineSystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("Render")
        .Input(() => renderableView);   // accepted by the builder, not yet consumed
}

dag.Add(new RenderPipeline());   // throws NotSupportedException — pending Patate design
```

## ⚠️ Guarantees & limits

- `dag.Add(PipelineSystem)` (class-based) throws `NotSupportedException` — "execution model pending Patate
  design." Only `dag.PipelineSystem(name, chunkAction, totalChunks, ...)` (lambda) is implemented.
- The lambda's `chunkAction` receives only `(chunkIndex, totalChunks)` — no `TickContext`, no `Transaction`, no
  `Accessor`. Any entity access inside the chunk action must create and manage its own `Transaction`.
- `input:`/`changeFilter:` parameters on the lambda overload are accepted but not wired to dispatch — a
  registered `PipelineSystem` always runs (subject to `shouldRun`) with the fixed `totalChunks` count; there is
  no reactive skip yet.
- `b.Parallel()` is rejected for `PipelineSystem` — it has its own chunk-parallel model (`totalChunks`), not the
  `QuerySystem` parallel path.
- A chunk action that throws fails the whole system: remaining chunks are drained without running, successors
  are skipped (`SkipReason.DependencyFailed`); other DAG branches continue.
- Inside a `CompoundSystem`, a `PipelineSystem` child hits the same `NotSupportedException` as top-level
  registration — compounds cannot currently contain a working `PipelineSystem` member.

## 🧪 Tests

- [DagSchedulerTests](../../../../test/Typhon.Engine.Tests/Runtime/DagSchedulerTests.cs) — `PipelineSystem_AllChunksProcessed`, `PipelineSystem_MultiWorkerDistribution`, `PipelineSystem_ChunksResetEachTick` — the working lambda/fixed-chunk-count dispatch path
- [ClassBasedSystemTests](../../../../test/Typhon.Engine.Tests/Runtime/ClassBasedSystemTests.cs) — `Add_PipelineSystem_ThrowsNotSupported` — class-based registration rejection
- [TyphonRuntimeTests](../../../../test/Typhon.Engine.Tests/Runtime/TyphonRuntimeTests.cs) — `PipelineSystem_DoesNotReceiveTransaction` — no `TickContext` entity machinery

## 🔗 Related

- Parent feature: [System Types](./README.md)
- Sibling: [QuerySystem](./query-system.md) — `QuerySystem.Parallel` is the per-entity-loop alternative this bulk gather/process/scatter shape is reserved for.

<!-- Deep dive: claude/design/Runtime/02-system-scheduling.md — PipelineSystem Integration -->
<!-- Deep dive: claude/overview/13-runtime.md — PipelineSystem -->
