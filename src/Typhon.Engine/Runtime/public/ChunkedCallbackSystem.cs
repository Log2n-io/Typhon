using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Chunk-parallel <see cref="CallbackSystem"/> that's invoked N times in parallel across workers, where N is the chunk count declared via
/// <see cref="SystemBuilder.ChunkedParallel"/> in <see cref="CallbackSystem.Configure"/>. Each invocation receives <see cref="TickContext.ChunkIndex"/> /
/// <see cref="TickContext.ChunkCount"/> so the implementation can compute its own data slice.
///
/// <para>Designed for non-entity-iterating chunkable work: SIMD sweeps over flat arrays, parallel reductions, image downsamples, etc. Skips all entity-prep
/// infrastructure that a <see cref="QuerySystem"/> would set up — no Accessor, no Entities, no per-chunk Transaction.</para>
///
/// <para>Typical use in <see cref="CallbackSystem.Configure"/>:
/// <code>
/// protected override void Configure(SystemBuilder b) => b
///     .Name("PheroMaxReduce")
///     .Phase(MyPhase)
///     .ReadsResource("PheromoneGrid")
///     .WritesResource("HeatFoodAccum")
///     .ChunkedParallel(chunkCount: 16);
/// </code>
/// </para>
///
/// <para>Inside <see cref="CallbackSystem.Execute"/>, compute the per-chunk slice from the TickContext:
/// <code>
/// protected override void Execute(TickContext ctx) {
///     int start = (int)((long)ctx.ChunkIndex * sourceLen / ctx.ChunkCount);
///     int end   = (int)((long)(ctx.ChunkIndex + 1) * sourceLen / ctx.ChunkCount);
///     for (int i = start; i &lt; end; i++) { /* ... */ }
/// }
/// </code>
/// </para>
/// </summary>
[PublicAPI]
public abstract class ChunkedCallbackSystem : CallbackSystem
{
    /// <summary>
    /// Scheduler hook evaluated after predecessors complete and before any worker claims a chunk. Default returns true (system runs). Override via
    /// <see cref="ChunkedCallbackSystem{TContext}"/> to inspect a typed ambient context. Untyped chunked systems use the fluent
    /// <see cref="SystemBuilder.ShouldRun(Func{bool})"/> delegate instead — both gates must pass for the system to run; the scheduler evaluates the delegate
    /// first, then this virtual.
    /// </summary>
    internal virtual bool OnShouldRun() => true;

    /// <summary>
    /// Scheduler hook invoked after predecessors complete and before any worker claims a chunk. Returns the chunk count to dispatch, or -1 to defer to
    /// <see cref="SystemDefinition.ExplicitChunkCount"/> / <see cref="SystemDefinition.RuntimeChunkCount"/>. Returning 0 skips the system (successors still
    /// fan out). Single-threaded by construction: exactly one worker decrements the last dependency to zero and runs Prepare.
    /// </summary>
    internal virtual int OnPrepare() => -1;
}

/// <summary>
/// Generic <see cref="ChunkedCallbackSystem"/> layer that exposes typed access to an ambient <typeparamref name="TContext"/> populated progressively across the
/// DAG. The context is bound at runtime via <c>TyphonRuntime.RegisterContext&lt;TContext&gt;</c> after Configure and before Start.
///
/// <para>Override <see cref="ShouldRun(TContext)"/> to gate dispatch on a typed flag, or <see cref="Prepare(TContext)"/> to build a per-tick plan and return a
/// dynamic chunk count (0 = skip, &gt;0 = dispatch with that many chunks).</para>
/// </summary>
[PublicAPI]
public abstract class ChunkedCallbackSystem<TContext> : ChunkedCallbackSystem where TContext : class
{
    private Func<TContext, bool> _shouldRunLambda;
    private Func<TContext, int> _prepareLambda;

    /// <summary>Ambient context bound by the runtime via <c>RegisterContext</c>. Null until bound.</summary>
    protected TContext Context { get; private set; }

    internal void BindContext(TContext ctx) => Context = ctx;
    internal void SetShouldRunLambda(Func<TContext, bool> p) => _shouldRunLambda = p;
    internal void SetPrepareLambda(Func<TContext, int> p) => _prepareLambda = p;

    /// <summary>Override to gate dispatch on a typed context flag. Default invokes the fluent lambda set
    /// via <see cref="SystemBuilder{TContext}.ShouldRun"/>, falling back to true.</summary>
    protected virtual bool ShouldRun(TContext ctx) => _shouldRunLambda?.Invoke(ctx) ?? true;

    /// <summary>Override to build a per-tick plan from the typed context and return the chunk count.
    /// Default invokes the fluent lambda set via <see cref="SystemBuilder{TContext}.Prepare"/>, falling back to -1.</summary>
    protected virtual int Prepare(TContext ctx) => _prepareLambda?.Invoke(ctx) ?? -1;

    internal sealed override bool OnShouldRun() => ShouldRun(Context);
    internal sealed override int OnPrepare() => Prepare(Context);

    /// <summary>The non-generic Configure is sealed; typed-system authors override
    /// <see cref="Configure(SystemBuilder{TContext})"/> instead.</summary>
    protected sealed override void Configure(SystemBuilder b) => Configure(new SystemBuilder<TContext>(b, this));

    /// <summary>Configure via the typed builder.</summary>
    protected abstract void Configure(SystemBuilder<TContext> b);
}
