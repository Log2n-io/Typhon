using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The whole replication pipeline as ONE dispatched system: prologue → interest → blocks → project → events → frames, inline, with no barrier between them.
/// D4 of <c>design/Subscriptions/09-phase1-build-plan.md § 2</c>, and the answer to open item 5 of <c>foundation/03 § 6</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it buys.</b> The staged shape's critical path is three sequential dispatches — <c>Interest → Project → Frames</c> — and a dispatch is a worker
/// wake/barrier cycle, ≈ 0.1 ms measured (<c>foundation/03 § 4</c>). That is ≈ 0.3 ms of pure scheduling before any replication work happens, which is the
/// entirety of AC-1's budget. Below some amount of work the barriers cost more than the parallelism they buy back, and this shape pays one dispatch instead
/// of three. Where that crossing point is, is a MEASUREMENT (Q-M1); this file builds the mechanism, and
/// <see cref="SubscriptionsOptions.CollapseBelowWorkUnits"/> defaults to 0 so nothing selects it until somebody measures.
/// </para>
/// <para>
/// <b>It executes the same chunk counts, serially — it does not re-partition.</b> Each stage's own <c>Prepare</c> decides the chunk count exactly as it does
/// in the staged shape, and this system then runs chunks <c>0 … n-1</c> in order on one thread. That is not an accident of implementation, it is what makes
/// the two shapes comparable: a record's arena is chosen by its chunk index, a netId lease is held per chunk index, and the frame assembler walks the arenas
/// in index order — so identical chunk counts give byte-identical frames whichever shape produced them, and <c>CollapsePathTests</c> asserts exactly that.
/// Collapsing the partition to one chunk as well would have been the obvious reading of "inline", and it would have changed the output.
/// </para>
/// <para>
/// <b>The stage bodies are the staged shape's, called rather than copied.</b> Every body is an <c>internal static</c> on its own stage class reaching nothing
/// but <see cref="SubscriptionsContext"/>, so this system is a second CALLER and never a second implementation. Duplicating them would have produced two
/// pipelines drifting apart under one differential test, which catches only what it covers.
/// </para>
/// <para>
/// <b>It is still a dispatched system, and one chunk of it.</b> Running the work in <c>Prepare</c> instead would put it on the TickDriver thread and cost
/// zero dispatches — genuinely cheaper — but it would also move replication onto the thread that is about to block on the fsync, and it would make the
/// track's chunk accounting report zero for a tick that did all of the work. D4 asks for the dispatch count to be REPORTED, not hidden, so the collapsed
/// shape reports one.
/// </para>
/// </remarks>
internal sealed class SubscriptionsCollapsedExecSystem : SubscriptionsExecSystemBase
{
    public SubscriptionsCollapsedExecSystem(DatabaseEngine engine, SubscriptionsPipelineShape shape) : base(engine, shape) { }

    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsCollapsed")
        .ChunkedParallel(1);

    /// <summary>The inverse of every staged member: this system is the one that runs when the tick collapsed.</summary>
    protected override bool RunsInThisShape(bool collapsed) => collapsed;

    /// <summary>
    /// One chunk, always. The stages' own chunk counts are computed inside <see cref="ExecuteChunk"/>, because each depends on the stage before it having
    /// already RUN — the blocks step reads the lists the interest pass produced, not the ones its <c>Prepare</c> produced.
    /// </summary>
    protected override int PrepareChunks(SubscriptionsContext ctx) => 1;

    protected override void ExecuteChunk(SubscriptionsContext ctx, int chunkIndex, int chunkCount)
    {
        // The base has already entered the epoch scope PS-02 requires, and stamps the chunk on the way out. What is left is the pipeline's order, which is
        // the one thing this shape may not get wrong: Events is a parallel BRANCH in the staged DAG and has no ordering constraint against Interest or
        // Project, but Frames needs both, so running it here between Project and Frames satisfies the DAG's edges rather than merely reading well.
        var chunks = SubscriptionsInterestExecSystem.Prologue(ctx);
        for (var k = 0; k < chunks; k++)
        {
            SubscriptionsInterestExecSystem.Resolve(ctx, k, chunks);
        }

        chunks = SubscriptionsProjectExecSystem.BlocksStep(ctx);
        for (var k = 0; k < chunks; k++)
        {
            SubscriptionsProjectExecSystem.Project(ctx, k, chunks);
        }

        chunks = SubscriptionsEventsExecSystem.PrepareDrain(ctx);
        for (var k = 0; k < chunks; k++)
        {
            SubscriptionsEventsExecSystem.Drain(ctx, k, chunks);
        }

        chunks = SubscriptionsFramesExecSystem.Prologue(ctx);
        for (var k = 0; k < chunks; k++)
        {
            SubscriptionsFramesExecSystem.Assemble(ctx, k, chunks);
        }
    }
}

/// <summary>
/// The per-tick choice between the staged pipeline and the collapsed one, decided once and read by every member of the track.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the decision is memoized rather than recomputed per stage.</b> Five systems ask, and three of them ask from a worker thread. A stage that answered
/// differently from its siblings would run a half-staged, half-collapsed tick — the interest pass executed twice, or the frame assembler never prepared —
/// so the answer has to be one answer. It is keyed on the tick number because the context is reset per tick and carries no room for this (the collapse path
/// adds one field to <see cref="SubscriptionsOptions"/> and none to <see cref="SubscriptionsContext"/>).
/// </para>
/// <para>
/// <b>Who writes it, and why the ordering is nevertheless explicit.</b> Every DAG root's <c>ShouldRun</c> is evaluated on the dispatching thread inside
/// <c>DagScheduler.MarkTrackRootsReady</c>, before a single worker is woken, so in practice the decision is taken by the TickDriver and every worker-side
/// read is of a value published before the wake. The wake is a barrier and would carry it. It is still written with a <see cref="Volatile"/> release and read
/// with acquires, because "the scheduler happens to mark roots before waking" is a property of another file that nothing here would notice losing, and an
/// acquire load costs nothing on x64 and one instruction on arm64.
/// </para>
/// <para>
/// <b>The work estimate is the PREVIOUS tick's watched-block count.</b> This tick's is not knowable before the interest pass has run, which is itself one of
/// the stages being shaped; asking for it would be circular. A one-tick-stale estimate is the right accuracy for a dispatch-shape choice: the quantity moves
/// with a camera, not with a frame, and the cost of being wrong for one tick is one shape's overhead, never a wrong result.
/// </para>
/// </remarks>
internal sealed class SubscriptionsPipelineShape
{
    /// <summary>The tick <see cref="_collapsed"/> was decided for. Released after it, so an acquiring reader that sees this tick sees the decision.</summary>
    private long _decidedTick = long.MinValue;

    /// <summary>1 when this tick collapsed. An <c>int</c> rather than a <c>bool</c>, so the read and the write are the ordinary volatile ones.</summary>
    private int _collapsed;

    /// <summary>The shape for <paramref name="ctx"/>'s tick, deciding it if this is the first stage to ask.</summary>
    public bool CollapsedFor(SubscriptionsContext ctx)
    {
        var tick = ctx.TickNumber;
        if (Volatile.Read(ref _decidedTick) == tick)
        {
            return Volatile.Read(ref _collapsed) != 0;
        }

        var collapsed = Decide(ctx);
        Volatile.Write(ref _collapsed, collapsed ? 1 : 0);
        Volatile.Write(ref _decidedTick, tick);
        return collapsed;
    }

    /// <summary>
    /// <c>sessions × watchedBlocks</c> against <see cref="SubscriptionsOptions.CollapseBelowWorkUnits"/>, with zero blocks counted as one.
    /// </summary>
    /// <remarks>
    /// Counting a blockless tick as one unit rather than zero is deliberate. A session with nothing watched still costs a frame prologue and a frame, so
    /// zero would read as "no work" for a thousand sessions that have just connected — the one case where the barriers are worth paying for. With the floor,
    /// the estimate for that tick is the session count, which is the honest lower bound on what the tick will do.
    /// </remarks>
    private static bool Decide(SubscriptionsContext ctx)
    {
        var subs = ctx.Subscriptions;
        if (subs == null)
        {
            return false;
        }

        var threshold = subs.Options.CollapseBelowWorkUnits;
        if (threshold <= 0)
        {
            return false;
        }

        var sessions = ctx.SessionCount;
        if (sessions <= 0)
        {
            // Unreachable through the gate, which already refuses a tick with no session. Kept because this method is the one that must never divide the
            // track against itself, and an estimate of zero work is exactly the input that would make it.
            return false;
        }

        long blocks = 0;
        var states = subs.ReplicationStates;
        for (var i = 0; i < states.Length; i++)
        {
            blocks += states[i].WatchedBlocks.Count;
        }

        return sessions * Math.Max(1L, blocks) < threshold;
    }
}
