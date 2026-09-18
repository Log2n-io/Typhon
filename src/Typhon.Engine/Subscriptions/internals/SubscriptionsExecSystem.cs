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
/// <b>Open item 4 of <c>foundation/03 § 6</c> is now answered for S2a as well, and it was answered by a test.</b> The stages run on pool workers that never
/// enrol, inside an open EW-01 window on both fence paths, so their <c>FenceThreadDepth</c> is zero — and <c>ExclusiveWindow.NoteMutation</c> throws for
/// exactly that combination. Every call site of it is a mutation of a fence-owned structure (a cluster B+Tree, the EntityMap, a per-cell index). S2a's job is
/// described as "marks the hit entities watched", and what marking turned out to touch is the replication block's own header word and nothing else, so it
/// reaches no such site: <c>InterestEpochScopeTests</c> runs the stage at eight workers with <c>EnableParallelFence</c> on and reads zero violations off an
/// engine whose fence DID mutate a guarded structure in the same window. No enrolment is taken, and taking one would have made the stage a legal writer of
/// fence-owned structures for the length of its chunk — the licence EW-01 exists to withhold. A stage body that later writes an index must take
/// <c>FenceWindow.EnterWorker()</c> as <c>FencePhaseExecSystemBase</c> does, and the guard failing loudly is what will say so.
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

            // The shape gate comes AFTER the fault gate on purpose: the injected fault is the only reachable "a stage threw" path while the bodies are what
            // they are, and it must reach whichever shape is running, or the collapsed path would have no failure case to be tested against at all.
            if (!RunsInThisShape(Shape.CollapsedFor(ctx)))
            {
                return false;
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
/// <para>
/// The work itself is <see cref="InterestPass"/>, which hangs off <see cref="SubscriptionsRuntime"/>; this class is the stage that dispatches it. Its
/// <c>Prepare</c> carries the track's PROLOGUE — serial work the scheduler already runs single-threaded before dispatch, which is why
/// <c>foundation/03 § 2.5</c> puts it there instead of in a system of its own that would cost a whole barrier. Today the prologue clears the watched masks
/// this pass set last tick and partitions the tick's sessions; freeing idle blocks and splicing the parked moves join it in P1-11.
/// </para>
/// <para>
/// <b>The chunk count is the pass's, not <see cref="SubscriptionsExecSystemBase.SessionChunks"/>.</b> The two agree today, and they stop agreeing the moment
/// a session is open without a profile bound: such a session has no interest to resolve, and dispatching a chunk for it would be a wake cycle spent on
/// nothing.
/// </para>
/// <para>
/// <b>It takes the epoch scope from its base and no fence enrolment</b> — open item 4 of <c>foundation/03 § 6</c>, answered with a test rather than an
/// argument. <see cref="InterestPass"/> reads cluster occupancy words and writes replication's own block headers and arenas; it reaches no
/// <see cref="ExclusiveWindow.NoteMutation"/> call site, and <c>InterestEpochScopeTests</c> asserts zero violations at eight workers with the parallel fence
/// on, against a tick that did mutate a guarded structure inside the same window. Enrolling anyway would have been the cheap answer and the wrong one: it
/// would have made this stage a legal writer of fence-owned structures for the length of its chunk, which is exactly the licence EW-01 exists to withhold.
/// </para>
/// </remarks>
internal sealed class SubscriptionsInterestExecSystem : SubscriptionsExecSystemBase
{
    public SubscriptionsInterestExecSystem(DatabaseEngine engine, SubscriptionsPipelineShape shape) : base(engine, shape) { }

    /// <summary>The stage's serial half — the prologue — and the chunk count it partitions the tick's sessions into.</summary>
    /// <remarks>
    /// Static, and called by <see cref="SubscriptionsCollapsedExecSystem"/> as well as by the dispatch below. The body reaches nothing but
    /// <see cref="SubscriptionsContext"/>, so there is no instance state for the two shapes to disagree about — which is the whole reason the collapsed path
    /// can be a second caller rather than a second implementation.
    /// </remarks>
    internal static int Prologue(SubscriptionsContext ctx)
    {
        var interest = ctx.Subscriptions?.Interest;
        return interest == null ? 0 : interest.BeginTick(ctx.TickNumber, ctx.WorkerCount);
    }

    /// <summary>The stage's parallel half, for one chunk of <paramref name="chunkCount"/>.</summary>
    internal static void Resolve(SubscriptionsContext ctx, int chunkIndex, int chunkCount)
        => ctx.Subscriptions?.Interest?.ExecuteChunk(chunkIndex, chunkCount);

    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsInterest")
        .ChunkedParallel(1);

    protected override int PrepareChunks(SubscriptionsContext ctx) => Prologue(ctx);

    protected override void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount) => Resolve(ctx, chunkIndex, chunkCount);
}

/// <summary>
/// S1 — projects and compares the watched entities' declared fields, encoding each changed record once for every session that will receive it.
/// </summary>
/// <remarks>
/// <para>
/// Partitioned over WATCHED BLOCKS, which is SUB-13 in the dispatch itself: an archetype far larger than what clients see costs what they see. With no
/// session looking at anything there is no block, so this prepares zero chunks and skips cleanly — successors still fan out.
/// </para>
/// <para>
/// <b>Its <c>Prepare</c> is also the track's blocks step</b> (<c>foundation/03 § 2.5</c>): serial work the scheduler already runs single-threaded before the
/// dispatch, so it costs no barrier of its own. Three things happen there — last tick's released identities go back to the allocator and the leases refill,
/// the record arenas rewind, and the blocks the interest stage marked are gathered into one indexable partition per archetype.
/// </para>
/// <para>
/// <b>The gather resolves each block's archetype with a directory probe, and that is a seam worth naming.</b> The interest stage lists the blocks it claimed
/// per worker, flat across archetypes, so the archetype is recovered here by asking each directory whether it names that chunk id. It is one probe for a
/// single-archetype runtime and averages half the archetype count otherwise — cheap for Phase 1's scales, and removable outright the moment the interest
/// stage's watched-block entry carries the archetype index it already knows.
/// </para>
/// </remarks>
internal sealed unsafe class SubscriptionsProjectExecSystem : SubscriptionsExecSystemBase
{
    public SubscriptionsProjectExecSystem(DatabaseEngine engine, SubscriptionsPipelineShape shape) : base(engine, shape) { }

    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsProject")
        .After("SubscriptionsInterest")
        .ChunkedParallel(1);

    protected override int PrepareChunks(SubscriptionsContext ctx) => BlocksStep(ctx);

    protected override void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount) => Project(ctx, chunkIndex, chunkCount);

    /// <summary>The stage's serial half — the blocks step — and the chunk count it partitions the watched blocks into.</summary>
    /// <remarks>
    /// Static and shared with <see cref="SubscriptionsCollapsedExecSystem"/>; see the note on <see cref="SubscriptionsInterestExecSystem.Prologue"/>.
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

        var interest = subs.Interest;

        // Block creation comes BEFORE the "nothing is watched" exit, and the order is the whole of why the pipeline runs at all. A cluster becomes watchable
        // by having a block, and the interest stage lists the clusters it hit that had none; skipping the stage because no block is watched yet would mean the
        // first block is never created, so nothing is ever watched — a runtime that projects nothing, forever, with no error anywhere. The list is produced by
        // the interest stage and does not depend on the watched count, so there is nothing to gain by deferring it.
        if (interest != null)
        {
            CreateNewBlocks(interest, states);
        }

        var blocks = interest?.WatchedBlockCount ?? WatchedBlocks(states);
        if (blocks == 0)
        {
            return 0;
        }

        var chunks = Math.Min(Math.Max(1, ctx.WorkerCount), blocks);

        // The gather comes FIRST, because the identity leases are sized from the watched slots it produces. Refilling before the partition exists would size
        // the very first tick's leases from nothing and defer most of an initial fill by a tick for no reason.
        if (interest != null)
        {
            Gather(interest, states, tick);
        }

        for (var i = 0; i < states.Length; i++)
        {
            states[i].BeginProjectTick(chunks);
        }

        return chunks;
    }

    /// <summary>The stage's parallel half, for one chunk of <paramref name="chunkCount"/>.</summary>
    internal static void Project(SubscriptionsContext ctx, int chunkIndex, int chunkCount)
    {
        var subs = ctx.Subscriptions;
        if (subs == null)
        {
            return;
        }

        var tick = (uint)ctx.TickNumber;
        var plans = subs.Plans;
        var states = subs.ReplicationStates;
        for (var a = 0; a < plans.Length && a < states.Length; a++)
        {
            ProjectArchetype(plans[a], a, states[a], chunkIndex, chunkCount, tick);
        }
    }

    private static void ProjectArchetype(CompiledProjectionPlan plan, int archetypeIndex, ArchetypeReplicationState state, int chunkIndex, int chunkCount,
        uint tick)
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
            for (var i = chunkIndex; i < count; i += chunkCount)
            {
                var block = list[i];
                var chunkId = block->ChunkId;
                if (chunkId < 0)
                {
                    // The block was released between the mark and here — a cluster that drained. Nothing describes it any more, so there is nothing to read.
                    continue;
                }

                var clusterBase = persistent != null ? persistentAccessor.GetChunkAddress(chunkId) : transientAccessor.GetChunkAddress(chunkId);
                var transientBase = persistent != null && transient != null ? transientAccessor.GetChunkAddress(chunkId) : null;
                ProjectionPass.ProjectBlock(plan, archetypeIndex, state, chunkIndex, block, clusterBase, transientBase, tick);
            }
        }
        finally
        {
            persistentAccessor.Dispose();
            transientAccessor.Dispose();
        }
    }

    private static int WatchedBlocks(ArchetypeReplicationState[] states)
    {
        var total = 0;
        for (var i = 0; i < states.Length; i++)
        {
            total += states[i].WatchedBlocks.Count;
        }

        return total;
    }

    /// <summary>
    /// The second half of the blocks step: gathers the interest stage's per-worker watched-block lists into one indexable partition per archetype.
    /// </summary>
    /// <remarks>
    /// <see cref="CreateNewBlocks"/> runs before this, at the top of <c>PrepareChunks</c>, and the separation matters: the partition is SIZED from the
    /// directory — at most one listing per block that exists — so a cluster that gained its block after the sizing would have nowhere to be listed. A block
    /// created this tick carries no watched bit and is therefore not listed until the interest stage marks it on the next one, which is the only place a
    /// watched bit is ever set.
    /// </remarks>
    private static void Gather(InterestPass interest, ArchetypeReplicationState[] states, uint tick)
    {
        for (var i = 0; i < states.Length; i++)
        {
            states[i].BeginWatchedBlocks(tick);
        }

        for (var w = 0; w < interest.ArenaCount; w++)
        {
            var watched = interest.Arena(w).WatchedBlocks;
            for (var i = 0; i < watched.Count; i++)
            {
                var block = (ReplicationBlockHeader*)watched[i];
                var chunkId = block->ChunkId;
                if (chunkId < 0)
                {
                    continue;
                }

                for (var a = 0; a < states.Length; a++)
                {
                    if (states[a].Directory.TryGetBlock(chunkId, out var found) && found == block)
                    {
                        states[a].WatchedBlocks.Add(block);
                        break;
                    }
                }
            }
        }

    }

    /// <summary>
    /// Rents and registers a block for every cluster the interest stage hit and found none for. It sets <b>no</b> watched bit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A fresh block leaves here with an empty mask, and that is the contract rather than an omission.</b> The interest stage owns the mask end to end: it
    /// is the only writer of a watched bit, it claims a block by being the worker whose <c>Interlocked.Or</c> saw a previous value of zero, and it clears the
    /// mask of the blocks it claimed at the next tick's prologue. A mask written here belongs to nobody — no worker ever claimed the block, so it never
    /// reaches an interest arena's watched list, never has its mask cleared, and stays watched for the life of the block. The cost of not writing it is that
    /// a newly watched cluster is projected from the following tick, which is one tick of latency on an entity nobody has ever been sent.
    /// </para>
    /// <para>
    /// The pool already hands back a zeroed header, so this is a matter of not undoing that.
    /// </para>
    /// </remarks>
    private static void CreateNewBlocks(InterestPass interest, ArchetypeReplicationState[] states)
    {
        for (var w = 0; w < interest.ArenaCount; w++)
        {
            var created = interest.Arena(w).NewBlocks;
            for (var i = 0; i < created.Count; i++)
            {
                var archetype = HitArena.NewBlockArchetype(created[i]);
                var chunkId = HitArena.NewBlockChunkId(created[i]);
                if ((uint)archetype >= (uint)states.Length || chunkId < 0)
                {
                    continue;
                }

                // Already created by another worker's entry for the same cluster, or the pool's budget binds. Neither is an error: the cluster gets its block
                // on a tick that has room for it, and a refusal is counted by the pool.
                var state = states[archetype];
                if (!state.Directory.TryGetBlock(chunkId, out _))
                {
                    state.TryAttachBlock(chunkId, out _);
                }
            }
        }
    }
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
    public SubscriptionsEventsExecSystem(DatabaseEngine engine, SubscriptionsPipelineShape shape) : base(engine, shape) { }

    /// <summary>The stage's serial half. Zero until replicated queues exist; shared with <see cref="SubscriptionsCollapsedExecSystem"/>.</summary>
    internal static int PrepareDrain(SubscriptionsContext ctx) => 0;

    /// <summary>The stage's parallel half, for one chunk of <paramref name="chunkCount"/>.</summary>
    internal static void Drain(SubscriptionsContext ctx, int chunkIndex, int chunkCount) { }

    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsEvents")
        .ChunkedParallel(1);

    protected override int PrepareChunks(SubscriptionsContext ctx) => PrepareDrain(ctx);

    protected override void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount) => Drain(ctx, chunkIndex, chunkCount);
}

/// <summary>
/// S2b — copies each session's changed records into its frame. The critical path's last stage.
/// </summary>
/// <remarks>
/// <para>
/// Partitioned over sessions on the same partition as <c>Interest</c>, so a session's hit list is still in the worker's cache when its frame is assembled.
/// </para>
/// <para>
/// <b>The chunk count is the assembler's, not <see cref="SubscriptionsExecSystemBase.SessionChunks"/>.</b> For the same reason the interest stage gives:
/// the partition is over the sessions that HAVE interest to resolve, which is what <see cref="InterestPass.TickSessionCount"/> counts and what
/// <see cref="InterestPass.HitsOf"/> is indexed by. Partitioning over the table's open rows instead would index the hit lists with the wrong numbers.
/// </para>
/// <para>
/// <b>Its <c>Prepare</c> is the stage's prologue</b> (<c>foundation/03 § 2.5</c>): the serial half of S2b, where a session's known-set is created, a
/// re-leased slot is rebound and a profile switch becomes the next frame's <c>RESET</c>. Doing it here rather than on a worker keeps the resource graph off
/// the parallel path — a known-set registers under its parent, and two workers registering at once would race a structure with no reason to be concurrent.
/// </para>
/// </remarks>
internal sealed class SubscriptionsFramesExecSystem : SubscriptionsExecSystemBase
{
    public SubscriptionsFramesExecSystem(DatabaseEngine engine, SubscriptionsPipelineShape shape) : base(engine, shape) { }

    /// <summary>The stage's serial half — the session prologue — and the chunk count it partitions the tick's sessions into.</summary>
    /// <remarks>
    /// Static and shared with <see cref="SubscriptionsCollapsedExecSystem"/>; see the note on <see cref="SubscriptionsInterestExecSystem.Prologue"/>.
    /// </remarks>
    internal static int Prologue(SubscriptionsContext ctx)
    {
        var subs = ctx.Subscriptions;
        var frames = subs?.Frames;
        return frames == null ? 0 : frames.BeginTick(subs.Interest, ctx.TickNumber, ctx.WorkerCount);
    }

    /// <summary>The stage's parallel half, for one chunk of <paramref name="chunkCount"/>.</summary>
    internal static void Assemble(SubscriptionsContext ctx, int chunkIndex, int chunkCount)
        => ctx.Subscriptions?.Frames?.ExecuteChunk(chunkIndex, chunkCount);

    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsFrames")
        .AfterAll("SubscriptionsProject", "SubscriptionsEvents")
        .ChunkedParallel(1);

    protected override int PrepareChunks(SubscriptionsContext ctx) => Prologue(ctx);

    protected override void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount) => Assemble(ctx, chunkIndex, chunkCount);
}
