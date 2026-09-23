using System;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Base class for the Engine-Subscriptions track's stages. Each stage is a <see cref="ChunkedCallbackSystem{TContext}"/> over the shared
/// <see cref="SubscriptionsContext"/>, mirroring how <c>FencePhaseExecSystemBase</c> carries the Fence DAG's phases.
/// </summary>
/// <remarks>
/// <para>
/// The stages are the blocks step and projection, the push index, the far-flush fold, the event drain and frame assembly. Each prepares a chunk count that is
/// honest about what it can partition this tick, and zero skips it cleanly.
/// </para>
/// <para>
/// <b>Epoch scope, but not fence enrolment.</b> The dispatcher wraps no chunk body, so each chunk enters its own <see cref="EpochGuard"/> — reading cluster
/// columns is page access and PS-02 requires every page access to be inside one. It does NOT call <c>FenceWindow.EnterWorker()</c>, which
/// <c>FencePhaseExecSystemBase</c> does: that enrolment exists to make a worker's writes to FENCE-OWNED structures legal (<c>FenceExecSystem.cs:237-238</c>),
/// and these stages read engine data while writing only replication's own native blocks. <see cref="EpochGuard"/> nests, so this stays correct when
/// <c>archive/Subscriptions/foundation/04-public-spatial-api.md</c> makes the dispatcher enter the scope for every chunked callback.
/// </para>
/// <para>
/// <b>No stage enrols in the fence window.</b> The stages run on pool workers that never enrol, inside an open EW-01 window on both fence paths, so their
/// <c>FenceThreadDepth</c> is zero — and <c>ExclusiveWindow.NoteMutation</c> throws for exactly that combination. Every call site of it is a mutation of a
/// fence-owned structure (a cluster B+Tree, the EntityMap, a per-cell index); the stages read engine data and write only replication's own native memory, so
/// they reach no such site. Taking an enrolment anyway would make a stage a legal writer of fence-owned structures for the length of its chunk — the licence
/// EW-01 exists to withhold. A stage body that later writes an index must take <c>FenceWindow.EnterWorker()</c> as <c>FencePhaseExecSystemBase</c> does,
/// and the guard failing loudly is what will say so.
/// </para>
/// </remarks>
internal abstract class SubscriptionsExecSystemBase : ChunkedCallbackSystem<SubscriptionsContext>
{
    protected readonly DatabaseEngine Engine;

    /// <summary>
    /// The per-tick choice between the staged shape and the collapsed one, shared by every member of the track — see <see cref="RunsInThisShape"/>.
    /// </summary>
    protected readonly SubscriptionsPipelineShape Shape;

    protected SubscriptionsExecSystemBase(DatabaseEngine engine, SubscriptionsPipelineShape shape)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(shape);
        Engine = engine;
        Shape = shape;
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

            // The shape gate comes AFTER the fault gate on purpose: the injected fault must reach whichever shape is running, or the collapsed path would
            // have no failure case to be tested against at all.
            if (!RunsInThisShape(Shape.CollapsedFor(ctx)))
            {
                return false;
            }

            // Stamped here rather than in Execute, so "compute happened" stays observable for a stage that clears its gate and then prepares zero chunks —
            // a stage with nothing to do this tick. Gating off leaves the stamp at zero, which is exactly what SUB-02's aborted-tick case asserts.
            ctx.NoteCompute();
            return true;
        }
        catch
        {
            ctx.NoteFault();
            throw;
        }
    }

    /// <summary>
    /// Whether this system belongs to the shape chosen for this tick. Staged by default; <see cref="SubscriptionsCollapsedExecSystem"/> inverts it.
    /// </summary>
    /// <remarks>
    /// This is how "exactly one shape prepares chunks per tick" is enforced, and it is enforced at the <c>ShouldRun</c> gate rather than by returning zero
    /// chunks from <c>Prepare</c>. The difference is not cosmetic: a stage's <c>Prepare</c> is where its serial half lives — the prologue, the blocks step,
    /// the session rebind — so letting the losing shape prepare and then dispatch nothing would run every one of those twice per tick, once on each shape,
    /// and the second run would see state the first had already advanced.
    /// </remarks>
    protected virtual bool RunsInThisShape(bool collapsed) => !collapsed;

    /// <summary>Timestamp ticks every subscriptions chunk spent entering an epoch, and how many did. A diagnostic; zero unless phase timing is on.</summary>
    internal static long EpochEnterTicks;

    /// <summary>Chunks that entered an epoch while the measurement was on.</summary>
    internal static long EpochEnterCount;

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

        // Timed around the epoch guard as well as the work, because holding an epoch IS part of what a stage costs the engine — the deferred page eviction
        // and view-buffer reclamation of EW-01 are bounded by how long the track holds one, and a measurement that excluded it would understate exactly the
        // thing the collapse path changes.
        var from = SubscriptionsTelemetry.Now();
        try
        {
            // Timed separately from the work: a chunk's measured span starts when a worker GRABS it, and everything between the grab and the first line of
            // the stage counts against the stage's wall without appearing in any of its phases. Entering an epoch is the only thing in that window.
            var epochFrom = FrameAssembler.PhaseTimingEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            using (EpochGuard.Enter(Engine.EpochManager))
            {
                if (epochFrom != 0L)
                {
                    Interlocked.Add(ref EpochEnterTicks, System.Diagnostics.Stopwatch.GetTimestamp() - epochFrom);
                    Interlocked.Increment(ref EpochEnterCount);
                }

                ExecuteChunk(ctx, tick.ChunkIndex, tick.ChunkCount);
            }
        }
        catch
        {
            ctx.NoteFault();
            throw;
        }
        finally
        {
            // In a finally, so a stage that threw still reports what it spent before throwing. A tick that failed is the one whose cost is most worth
            // knowing, and it is also the one a try-only timer silently drops.
            ctx.Telemetry.NoteStage(ctx.TickNumber, Stage, from, SubscriptionsTelemetry.Now());
        }

        ctx.NoteChunkExecuted();
    }

    // All three gates rethrow after stamping. The stamp is what lets publication be suppressed with the compute half (SUB-02); the rethrow is what keeps
    // the scheduler's own accounting — telemetry, SkipReason.Exception, successor fan-out, the host callback — exactly as it is for every other system.
    // Swallowing here would make a replication bug invisible instead of merely non-fatal.

    /// <summary>Which member of the track this system is, for the per-stage breakdown. Identity, not a name — see <see cref="SubscriptionsTelemetry"/>.</summary>
    protected abstract SubscriptionsStage Stage { get; }

    /// <summary>The stage's chunk count for this tick. Wrapped by <see cref="Prepare"/> so a throw is recorded before it propagates.</summary>
    protected abstract int PrepareChunks(SubscriptionsContext ctx);

    /// <summary>The stage's per-chunk work.</summary>
    protected abstract void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount);

}

/// <summary>
/// S1 — projects and compares the pushed entities' declared fields, and records one push event per entity for the frame stage to fan out. The track's root.
/// </summary>
/// <remarks>
/// <para>
/// Partitioned over the blocks the push set names: an archetype costs what was pushed, not what it holds. With nothing pushed this prepares zero chunks and
/// skips cleanly — successors still fan out.
/// </para>
/// <para>
/// <b>Its <c>Prepare</c> is also the track's blocks step</b> (<c>design/Subscriptions/02-execution.md § 3.2</c>): serial work the scheduler already runs
/// single-threaded before the dispatch, so it costs no barrier of its own. The push set is gathered and every cluster in it given a block, the fence's
/// parked migrations land, the pushed slots are marked, and last tick's released identities go back to the allocator as the leases refill.
/// </para>
/// </remarks>
internal sealed unsafe class SubscriptionsProjectExecSystem : SubscriptionsExecSystemBase
{
    public SubscriptionsProjectExecSystem(DatabaseEngine engine, SubscriptionsPipelineShape shape) : base(engine, shape) { }

    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsProject")
        .ChunkedParallel(1);

    /// <inheritdoc />
    protected override SubscriptionsStage Stage => SubscriptionsStage.Project;

    protected override int PrepareChunks(SubscriptionsContext ctx) => BlocksStep(ctx);

    protected override void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount) => Project(ctx, chunkIndex, chunkCount);

    /// <summary>The stage's serial half — the blocks step — and the chunk count it partitions the watched blocks into.</summary>
    /// <remarks>
    /// Static, and called by <see cref="SubscriptionsCollapsedExecSystem"/> as well as by the dispatch below. The body reaches nothing but
    /// <see cref="SubscriptionsContext"/>, so there is no instance state for the two shapes to disagree about — which is why the collapsed path can be a second
    /// caller rather than a second implementation.
    /// </remarks>
    internal static int BlocksStep(SubscriptionsContext ctx)
    {
        var subs = ctx.Subscriptions;
        var states = subs?.ReplicationStates;
        if (states == null || states.Length == 0)
        {
            return 0;
        }

        // The tick number is the claim stamp a block's watched list is keyed on, and zero is what a freshly rented block already reads as. A runtime's first
        // tick is 1, so this only ever declines a synthetic tick 0.
        var tick = (uint)ctx.TickNumber;
        if (tick == 0)
        {
            return 0;
        }

        var timed = FrameAssembler.PhaseTimingEnabled;
        var t0 = timed ? Stopwatch.GetTimestamp() : 0L;

        // The push set, and a block for every cluster in it — BEFORE the drain, so an entity that migrated into a cluster with no block lands in one this
        // tick. Nothing is observed when there is no push path, so there is nothing to project.
        var push = subs.Push;
        if (push == null)
        {
            return 0;
        }

        push.PrepareBlocks(tick);

        var t1 = timed ? Stopwatch.GetTimestamp() : 0L;

        // Entries the fence's migration step could not place, because their destination cluster had no block when the entity arrived in it. AFTER the blocks
        // above, which is the point: a cluster pushed this tick now has somewhere for its arrivals to go. Single-threaded here, and separated from the slices
        // that filled the lists by the fence's own barrier.
        for (var i = 0; i < states.Length; i++)
        {
            states[i]?.DrainParkedEntries();
        }

        var t2 = timed ? Stopwatch.GetTimestamp() : 0L;

        // The marks come BEFORE the leases, which are sized from the marked blocks. The index is counted by the projection's chunks unless reproducible bytes
        // are asked for: its order inside a cell follows the race. The collapsed shape counts too but never places — its frame prologue sees no index for the
        // tick and builds it serially, recounting from zero.
        for (var i = 0; i < states.Length; i++)
        {
            states[i].BeginWatchedBlocks(tick);
        }

        push.MarkPushed(Math.Max(1, ctx.WorkerCount), countInProject: !subs.Options.DeterministicProjection);

        if (timed)
        {
            var t3 = Stopwatch.GetTimestamp();
            PrologueCreateTicks += t1 - t0;
            PrologueDrainTicks += t2 - t1;
            PrologueGatherTicks += t3 - t2;
            PrologueCount++;
        }

        var blocks = WatchedBlocks(states);
        if (blocks == 0)
        {
            return 0;
        }

        var chunks = Math.Min(Math.Max(1, ctx.WorkerCount), blocks);

        for (var i = 0; i < states.Length; i++)
        {
            states[i].BeginProjectTick(chunks);
        }

        return chunks;
    }

    // The blocks step's serial cost, split by part, and the parallel half's busy time — collected only while FrameAssembler.PhaseTimingEnabled is set.
    // Written by the single thread that runs Prepare, or once per chunk with an interlocked add; read by the report after the tick.
    internal static long PrologueCreateTicks;
    internal static long PrologueDrainTicks;
    internal static long PrologueGatherTicks;
    internal static long PrologueCount;
    internal static long ProjectBusyTicks;

    /// <summary>The stage's parallel half, for one chunk of <paramref name="chunkCount"/>.</summary>
    internal static void Project(SubscriptionsContext ctx, int chunkIndex, int chunkCount)
    {
        var subs = ctx.Subscriptions;
        if (subs == null)
        {
            return;
        }

        var from = FrameAssembler.PhaseTimingEnabled ? Stopwatch.GetTimestamp() : 0L;
        ProjectAll(subs, ctx, chunkIndex, chunkCount);
        subs.Push?.CountWorker(chunkIndex);
        if (from != 0L)
        {
            Interlocked.Add(ref ProjectBusyTicks, Stopwatch.GetTimestamp() - from);
        }
    }

    private static void ProjectAll(SubscriptionsRuntime subs, SubscriptionsContext ctx, int chunkIndex, int chunkCount)
    {
        var tick = (uint)ctx.TickNumber;
        var plans = subs.Plans;
        var states = subs.ReplicationStates;
        var claim = !subs.Options.DeterministicProjection;
        for (var a = 0; a < plans.Length && a < states.Length; a++)
        {
            ProjectArchetype(plans[a], a, states[a], chunkIndex, chunkCount, tick, claim);
        }
    }

    private static void ProjectArchetype(CompiledProjectionPlan plan, int archetypeIndex, ArchetypeReplicationState state, int chunkIndex, int chunkCount,
        uint tick, bool claim)
    {
        var list = state.WatchedBlocks;
        var count = list.Count;
        var clusterState = state.ClusterState;
        if (count == 0 || chunkIndex >= count || clusterState == null)
        {
            return;
        }

        // Both stores, because a mixed archetype keeps its transient components in a second segment whose clusters share the persistent layout exactly —
        // the same pair ClusterRef resolves a column through. One accessor per chunk per archetype, not one per block.
        var persistent = clusterState.ClusterSegment;
        var transient = clusterState.TransientSegment;
        var persistentAccessor = persistent != null ? persistent.CreateChunkAccessor() : default;
        var transientAccessor = transient != null ? transient.CreateChunkAccessor() : default;
        try
        {
            if (claim)
            {
                // Claimed in batches from a shared cursor: whichever chunk is running takes the next blocks, so the stage ends when the work does, not when
                // the last worker to arrive has finished its stride. The chunk index still names the arena and lease the blocks write into.
                ref var cursor = ref state.ProjectCursor[ArchetypeReplicationState.ProjectCursorSlot];
                while (true)
                {
                    var from = Interlocked.Add(ref cursor, ProjectBatch) - ProjectBatch;
                    if (from >= count)
                    {
                        break;
                    }

                    var to = Math.Min(count, from + ProjectBatch);
                    for (var i = from; i < to; i++)
                    {
                        ProjectOne(plan, archetypeIndex, state, chunkIndex, list[i], persistent, transient, ref persistentAccessor, ref transientAccessor, tick);
                    }
                }
            }
            else
            {
                for (var i = chunkIndex; i < count; i += chunkCount)
                {
                    ProjectOne(plan, archetypeIndex, state, chunkIndex, list[i], persistent, transient, ref persistentAccessor, ref transientAccessor, tick);
                }
            }
        }
        finally
        {
            persistentAccessor.Dispose();
            transientAccessor.Dispose();
        }
    }

    // Blocks per claim: enough that the atomic is noise against a block's projection, few enough that the last claims even out the tail.
    private const int ProjectBatch = 4;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void ProjectOne(CompiledProjectionPlan plan, int archetypeIndex, ArchetypeReplicationState state, int chunkIndex, ReplicationBlockHeader* block,
        ChunkBasedSegment<PersistentStore> persistent, ChunkBasedSegment<TransientStore> transient, ref ChunkAccessor<PersistentStore> persistentAccessor,
        ref ChunkAccessor<TransientStore> transientAccessor, uint tick)
    {
        var chunkId = block->ChunkId;
        if (chunkId < 0)
        {
            // The block was released between the mark and here — a cluster that drained. Nothing describes it any more, so there is nothing to read.
            return;
        }

        var clusterBase = persistent != null ? persistentAccessor.GetChunkAddress(chunkId) : transientAccessor.GetChunkAddress(chunkId);
        var transientBase = persistent != null && transient != null ? transientAccessor.GetChunkAddress(chunkId) : null;
        ProjectionPass.ProjectBlock(plan, archetypeIndex, state, chunkIndex, block, clusterBase, transientBase, tick);
    }

    private static int WatchedBlocks(ArchetypeReplicationState[] states)
    {
        var total = 0;
        for (var i = 0; i < states.Length; i++)
        {
            total += states[i]?.WatchedBlocks.Count ?? 0;
        }

        return total;
    }
}

/// <summary>
/// S1b — drains the replicated event queues and buckets their events by cell.
/// </summary>
/// <remarks>
/// <b>A parallel branch, not a link in the chain.</b> Draining the queues does not depend on projection; only <c>Frames</c> needs both.
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
    public SubscriptionsEventsExecSystem(DatabaseEngine engine, SubscriptionsPipelineShape shape) : base(engine, shape) { }

    /// <summary>The stage's serial half. Zero until replicated queues exist; shared with <see cref="SubscriptionsCollapsedExecSystem"/>.</summary>
    internal static int PrepareDrain(SubscriptionsContext ctx) => 0;

    /// <summary>The stage's parallel half, for one chunk of <paramref name="chunkCount"/>.</summary>
    internal static void Drain(SubscriptionsContext ctx, int chunkIndex, int chunkCount) { }

    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsEvents")
        .ChunkedParallel(1);

    /// <inheritdoc />
    protected override SubscriptionsStage Stage => SubscriptionsStage.Events;

    protected override int PrepareChunks(SubscriptionsContext ctx) => PrepareDrain(ctx);

    protected override void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount) => Drain(ctx, chunkIndex, chunkCount);
}

/// <summary>
/// Places the tick's push events into the cell index, one chunk per worker list, after the projection counted them. Serial prefix in its
/// prologue; nothing to do (zero chunks) on a tick the frame prologue indexes serially.
/// </summary>
internal sealed class SubscriptionsPushIndexExecSystem : SubscriptionsExecSystemBase
{
    public SubscriptionsPushIndexExecSystem(DatabaseEngine engine, SubscriptionsPipelineShape shape) : base(engine, shape) { }

    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsPushIndex")
        .After("SubscriptionsProject")
        .ChunkedParallel(1);

    /// <inheritdoc />
    protected override SubscriptionsStage Stage => SubscriptionsStage.Project;

    protected override int PrepareChunks(SubscriptionsContext ctx) => ctx.Subscriptions?.Push?.BeginParallelIndex() ?? 0;

    protected override void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount) => ctx.Subscriptions?.Push?.PlaceWorker(chunkIndex);
}

/// <summary>
/// Distance LOD — folds the tick's far flushes: chunks of cells over the last N log slots, after the index is placed. Zero chunks when the
/// LOD is off or the index is built later, serially, by the frame prologue — which then folds serially too.
/// </summary>
internal sealed class SubscriptionsPushFarExecSystem : SubscriptionsExecSystemBase
{
    public SubscriptionsPushFarExecSystem(DatabaseEngine engine, SubscriptionsPipelineShape shape) : base(engine, shape) { }

    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsPushFar")
        .After("SubscriptionsPushIndex")
        .ChunkedParallel(1);

    /// <inheritdoc />
    protected override SubscriptionsStage Stage => SubscriptionsStage.Project;

    // No session open, nobody to flush to: a session that opens later starts with a reset and its whole view, never with an old flush.
    protected override int PrepareChunks(SubscriptionsContext ctx) => ctx.SessionCount > 0 ? ctx.Subscriptions?.Push?.BeginFarFold(ctx.WorkerCount) ?? 0 : 0;

    protected override void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount) => ctx.Subscriptions?.Push?.FoldFarChunk(chunkIndex);
}

/// <summary>
/// S2b — gathers each session's records from the push index and publishes its frame. The critical path's last stage.
/// </summary>
/// <remarks>
/// <para>
/// <b>The chunk count is the assembler's:</b> the partition is over the sessions bound to a profile that reaches something, taken from a shared cursor.
/// </para>
/// <para>
/// <b>Its <c>Prepare</c> is the stage's prologue</b> (<c>design/Subscriptions/02-execution.md § 3.5</c>): the serial half of S2b, where the skip policy
/// runs, a re-leased slot is rebound, a profile switch becomes the next frame's <c>RESET</c>, and the push index is built when its stage did not.
/// </para>
/// </remarks>
internal sealed class SubscriptionsFramesExecSystem : SubscriptionsExecSystemBase
{
    public SubscriptionsFramesExecSystem(DatabaseEngine engine, SubscriptionsPipelineShape shape) : base(engine, shape) { }

    /// <summary>The stage's serial half — the session prologue — and the chunk count it partitions the tick's sessions into.</summary>
    /// <remarks>Static and shared with <see cref="SubscriptionsCollapsedExecSystem"/>; see the note on
    /// <see cref="SubscriptionsProjectExecSystem.BlocksStep"/>.</remarks>
    internal static int Prologue(SubscriptionsContext ctx)
    {
        var frames = ctx.Subscriptions?.Frames;
        return frames == null ? 0 : frames.BeginTick(ctx.TickNumber, ctx.WorkerCount);
    }

    /// <summary>The stage's parallel half, for one chunk of <paramref name="chunkCount"/>.</summary>
    internal static void Assemble(SubscriptionsContext ctx, int chunkIndex, int chunkCount)
        => ctx.Subscriptions?.Frames?.ExecuteChunk(chunkIndex, chunkCount);

    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsFrames")
        .AfterAll("SubscriptionsProject", "SubscriptionsEvents", "SubscriptionsPushIndex", "SubscriptionsPushFar")
        .ChunkedParallel(1);

    /// <inheritdoc />
    protected override SubscriptionsStage Stage => SubscriptionsStage.Frames;

    protected override int PrepareChunks(SubscriptionsContext ctx) => Prologue(ctx);

    protected override void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount) => Assemble(ctx, chunkIndex, chunkCount);
}
