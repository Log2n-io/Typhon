using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Base class for the Engine-Subscriptions track's stages. Each stage is a <see cref="ChunkedCallbackSystem{TContext}"/> over the shared
/// <see cref="SubscriptionsContext"/>, mirroring how <c>FencePhaseExecSystemBase</c> carries the Fence DAG's phases.
/// </summary>
/// <remarks>
/// <para>
/// <b>What exists here today is the vehicle, not the replication.</b> #955 adds the track, its ordering, its gating and its dispatch on both fence paths. The
/// stage bodies — interest resolution, projection, event drain, frame assembly — are Phase 1 content and arrive with the state they operate on. Each stage
/// therefore prepares a chunk count that is honest about what it can currently partition and executes an empty body. That is deliberate: a stage that invented
/// work before its inputs exist would have to be un-invented, and the ordering this task is about is observable without it.
/// </para>
/// <para>
/// <b>Epoch scope, but not fence enrolment.</b> The dispatcher wraps no chunk body, so each chunk enters its own <see cref="EpochGuard"/> — reading cluster
/// columns is page access and PS-02 requires every page access to be inside one. It does NOT call <c>FenceWindow.EnterWorker()</c>, which
/// <c>FencePhaseExecSystemBase</c> does: that enrolment exists to make a worker's writes to FENCE-OWNED structures legal (<c>FenceExecSystem.cs:237-238</c>),
/// and these stages read engine data while writing only replication's own native blocks. <see cref="EpochGuard"/> nests, so this stays correct when
/// <c>foundation/04-public-spatial-api.md</c> makes the dispatcher enter the scope for every chunked callback.
/// </para>
/// <para>
/// <b>That resolves open item 4 of <c>foundation/03 § 6</c> only for a READ-ONLY stage, which is what these are today.</b> The stages run on pool workers
/// that never enrol, inside an open EW-01 window on both fence paths, so their <c>FenceThreadDepth</c> is zero — and <c>ExclusiveWindow.NoteMutation</c>
/// throws for exactly that combination. Every call site of it is a mutation of a fence-owned structure (a cluster B+Tree, the EntityMap, a per-cell index),
/// so the first stage body that writes one will throw at the mutation site rather than corrupt it. S2a's job is described as "marks the hit entities
/// watched": if marking ever touches an index rather than replication's own blocks, that stage must take <c>FenceWindow.EnterWorker()</c> around its chunk,
/// as <c>FencePhaseExecSystemBase</c> does. The guard failing loudly is the design working; the point is that it is a live constraint on Phase 1, not a
/// question that has been closed.
/// </para>
/// </remarks>
internal abstract class SubscriptionsExecSystemBase : ChunkedCallbackSystem<SubscriptionsContext>
{
    protected readonly DatabaseEngine Engine;

    protected SubscriptionsExecSystemBase(DatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Engine = engine;
    }

    /// <summary>
    /// The track's gate, applied to every stage. Sealed: see <see cref="SubscriptionsContext.ShouldTrackRun"/> for why this cannot live on the root alone.
    /// </summary>
    protected sealed override bool ShouldRun(SubscriptionsContext ctx)
    {
        // No null guard on the context: DagScheduler.ValidateContextBindings throws at Start for any typed system whose context was never registered, so a
        // null here is unreachable — and a guard that returns false would convert that startup error into a track which never runs and never says why.
        try
        {
            if (!ctx.ShouldTrackRun)
            {
                return false;
            }

            if (ctx.FaultGateForTest)
            {
                throw new InvalidOperationException("Injected replication-stage fault (test only).");
            }

            // Stamped here rather than in Execute, so "compute happened" stays observable for a stage that clears its gate and then prepares zero chunks —
            // which is every stage whose payload has not been built yet. Gating off leaves the stamp at zero, which is exactly what SUB-02's aborted-tick
            // case asserts.
            ctx.NoteCompute();
            return true;
        }
        catch
        {
            ctx.NoteFault();
            throw;
        }
    }

    protected sealed override int Prepare(SubscriptionsContext ctx)
    {
        try
        {
            return PrepareChunks(ctx);
        }
        catch
        {
            ctx.NoteFault();
            throw;
        }
    }

    protected sealed override void Execute(TickContext tick)
    {
        // No null guard, for the same reason as in ShouldRun above: ValidateContextBindings makes it unreachable, and returning early would turn a startup
        // error into a stage that quietly does nothing.
        var ctx = Context;

        try
        {
            using (EpochGuard.Enter(Engine.EpochManager))
            {
                ExecuteChunk(ctx, tick.ChunkIndex, tick.ChunkCount);
            }
        }
        catch
        {
            ctx.NoteFault();
            throw;
        }

        ctx.NoteChunkExecuted();
    }

    // All three gates rethrow after stamping. The stamp is what lets publication be suppressed with the compute half (SUB-02); the rethrow is what keeps
    // the scheduler's own accounting — telemetry, SkipReason.Exception, successor fan-out, the host callback — exactly as it is for every other system.
    // Swallowing here would make a replication bug invisible instead of merely non-fatal.

    /// <summary>The stage's chunk count for this tick. Wrapped by <see cref="Prepare"/> so a throw is recorded before it propagates.</summary>
    protected abstract int PrepareChunks(SubscriptionsContext ctx);

    /// <summary>The stage's per-chunk work. Empty until the stage's inputs exist — see the class remarks.</summary>
    protected abstract void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount);

    /// <summary>
    /// Chunk count for a stage partitioned over sessions. One chunk per worker, capped by the session count so a handful of sessions do not pay a wake cycle
    /// each.
    /// </summary>
    /// <remarks>
    /// A computed count rather than a static <c>ChunkedParallel(N)</c>, which is open item 3 of <c>foundation/03 § 6</c> and stays open: the choice wants the
    /// first real measurement, and this shape is the one that can be measured. It is a dispatch heuristic over the worker pool, not a policy derived from any
    /// application's session mix.
    /// </remarks>
    protected static int SessionChunks(SubscriptionsContext ctx)
    {
        var workers = ctx.WorkerCount > 0 ? ctx.WorkerCount : 1;
        return Math.Min(workers, ctx.SessionCount);
    }
}

/// <summary>
/// S2a — resolves each session's interest into hits and marks the hit entities watched. The critical path's first stage, and the DAG's root.
/// </summary>
/// <remarks>
/// Its <c>Prepare</c> also carries the track's PROLOGUE once that exists (apply moves parked by the fence, free idle blocks, grow the directory) —
/// serial work the scheduler already runs single-threaded before dispatch, which is why <c>foundation/03 § 2.5</c> puts it there instead of in a system
/// of its own.
/// </remarks>
internal sealed class SubscriptionsInterestExecSystem : SubscriptionsExecSystemBase
{
    public SubscriptionsInterestExecSystem(DatabaseEngine engine) : base(engine) { }

    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsInterest")
        .ChunkedParallel(1);

    protected override int PrepareChunks(SubscriptionsContext ctx) => SessionChunks(ctx);

    protected override void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount) { }
}

/// <summary>
/// S1 — projects and compares the watched entities' declared fields, encoding each changed record once for every session that will receive it.
/// </summary>
/// <remarks>
/// Partitioned over WATCHED BLOCKS, which is SUB-13 in the dispatch itself: an archetype far larger than what clients see costs what they see. Until blocks are
/// attached by the interest stage there are none, so this prepares zero chunks and skips cleanly — successors still fan out.
/// </remarks>
internal sealed class SubscriptionsProjectExecSystem : SubscriptionsExecSystemBase
{
    public SubscriptionsProjectExecSystem(DatabaseEngine engine) : base(engine) { }

    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsProject")
        .After("SubscriptionsInterest")
        .ChunkedParallel(1);

    protected override int PrepareChunks(SubscriptionsContext ctx) => 0;

    protected override void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount) { }
}

/// <summary>
/// S1b — drains the replicated event queues and buckets their events by cell.
/// </summary>
/// <remarks>
/// <b>A parallel branch, not a link in the chain.</b> Draining the queues depends on neither interest nor projection; only <c>Frames</c> needs both.
/// Chaining it would express reading order rather than a dependency and would add a barrier to the critical path. It declares no <c>.After()</c> for
/// exactly that reason — which is also why the track's gate lives on every stage rather than on the root (see
/// <see cref="SubscriptionsContext.ShouldTrackRun"/>).
/// <para>
/// One chunk, because EQ-04 makes queue drain single-consumer. It prepares zero until replicated queues exist, at which point this system declares itself their
/// consumer at BUILD time — registering later would pass EQ-04's check and then race it.
/// </para>
/// </remarks>
internal sealed class SubscriptionsEventsExecSystem : SubscriptionsExecSystemBase
{
    public SubscriptionsEventsExecSystem(DatabaseEngine engine) : base(engine) { }

    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsEvents")
        .ChunkedParallel(1);

    protected override int PrepareChunks(SubscriptionsContext ctx) => 0;

    protected override void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount) { }
}

/// <summary>
/// S2b — copies each session's changed records into its frame. The critical path's last stage.
/// </summary>
/// <remarks>
/// Partitioned over sessions on the same partition as <c>Interest</c>, so a session's hit list is still in the worker's cache when its frame is assembled.
/// </remarks>
internal sealed class SubscriptionsFramesExecSystem : SubscriptionsExecSystemBase
{
    public SubscriptionsFramesExecSystem(DatabaseEngine engine) : base(engine) { }

    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsFrames")
        .AfterAll("SubscriptionsProject", "SubscriptionsEvents")
        .ChunkedParallel(1);

    protected override int PrepareChunks(SubscriptionsContext ctx) => SessionChunks(ctx);

    protected override void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount) { }
}
