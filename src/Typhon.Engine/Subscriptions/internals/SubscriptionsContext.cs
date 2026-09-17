using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Tick-scoped state shared by the Engine-Subscriptions track's stages, mirroring <see cref="FenceContext"/>'s role for the Fence DAG. One instance lives on
/// <c>TyphonRuntime</c> and is bound onto every stage via <see cref="DagScheduler.RegisterContext{TContext}"/> after Build and before Start.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owned by the runtime, not by <see cref="DatabaseEngine"/> — unlike <see cref="FenceContext"/>.</b> The fence context lives on the engine because engine
/// code (<c>DatabaseEngine.WriteTickFence</c>) populates and reads it. Nothing in <c>DatabaseEngine</c> touches replication: the track is declared by the
/// runtime, dispatched by the runtime, and gated on runtime state. Putting it on the engine would create a dependency that only points one way on paper.
/// </para>
/// <para>
/// <b>The abort / fence-failure gate reads the scheduler LIVE, and that is load-bearing.</b> It would be cheaper to snapshot both flags into fields during
/// <see cref="Reset"/>, and it would be wrong: <see cref="DagScheduler.DispatchDeferredTracks"/> walks Engine-Post (the Fence DAG) and Engine-Subscriptions in
/// ONE loop, so a fence phase that fails while Engine-Post is running latches <see cref="DagScheduler.IsFenceFailed"/> strictly after any reset this tick
/// performed. A snapshot taken before the loop cannot see it, and the track would compute for a tick whose fence did not complete — precisely what SUB-02
/// forbids. The live read costs two volatile loads per system per tick.
/// </para>
/// </remarks>
internal sealed class SubscriptionsContext
{
    private DagScheduler _scheduler;
    private SubscriptionsRuntime _subscriptions;

    // Padded, and not merely out of habit: this is incremented per chunk per worker, while the ordering-journal fields below are read by the driver. Left as a
    // bare int it shares their cache line, so every worker's increment bounces a line the driver is reading — the one shape the ≥64 B rule exists to stop.
    private CacheLinePaddedInt _chunksExecuted;

    /// <summary>The tick this pass belongs to.</summary>
    public long TickNumber;

    /// <summary>Worker pool width, used by the stages to size their chunk counts.</summary>
    public int WorkerCount;

    /// <summary>
    /// Sessions currently connected. Zero means the track costs exactly its <c>ShouldRun</c> check — "no session, no work"
    /// (<c>design/Subscriptions/01-model.md § 1</c>).
    /// </summary>
    /// <remarks>
    /// Written by the ingress half of the pipeline once sessions exist (#956 / #957). Until then it stays zero in every production path, which is why the track
    /// dispatches nothing on a stock runtime, and tests set it directly to exercise the vehicle.
    /// </remarks>
    public int SessionCount;

    /// <summary>
    /// Test-only fault injection: when set, every stage's gate throws. Exists because the invariant it verifies — a replication bug must not stop the
    /// database — has no other reachable path while the stage bodies are empty, and it is too important to leave unverified until they are not.
    /// </summary>
    internal bool FaultGateForTest;

    /// <summary>Binds the scheduler whose abort / fence-failure latches gate this track. Called once, during runtime construction.</summary>
    internal void AttachScheduler(DagScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        _scheduler = scheduler;
    }

    /// <summary>
    /// Everything replication owns for the life of the runtime: the compiled plan, the catalog, the session table and the pools.
    /// <see langword="null"/> until <c>Start</c> has built it.
    /// </summary>
    /// <remarks>
    /// <b>The one field this file gains, and the reason it gains only one</b> (09-phase1-build-plan § 4). This context is tick-scoped and shared by every
    /// stage of the track; everything a stage needs to reach at tick time hangs off <see cref="SubscriptionsRuntime"/>, which every later slice extends
    /// instead of extending this class. Read through a volatile load because it is published from the thread that calls <c>Start</c> and read by workers.
    /// </remarks>
    public SubscriptionsRuntime Subscriptions => Volatile.Read(ref _subscriptions);

    /// <summary>Publishes the replication state to the stages. Called once, from <see cref="TyphonRuntime.Start"/>, before the workers exist.</summary>
    internal void AttachSubscriptions(SubscriptionsRuntime subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        Volatile.Write(ref _subscriptions, subscriptions);
    }

    /// <summary>
    /// The whole track's gate: nothing is computed for an aborted or fence-failed tick, and nothing is computed when no session is connected.
    /// </summary>
    /// <remarks>
    /// Evaluated by EVERY stage rather than only the DAG's root. The design (<c>foundation/03 § 2.4</c>) gates the root alone, reasoning that "the DAG is a
    /// chain, so one gate suppresses all six stages" — but § 2.5 replaced that chain: <c>Events</c> is a parallel BRANCH with no predecessor, so it becomes
    /// ready on its own and a root-only gate would let it dispatch on an aborted tick. Gating in the shared base is both the fix and the cheaper thing to
    /// reason about, since no future re-shaping of the DAG can silently un-gate a stage.
    /// </remarks>
    public bool ShouldTrackRun
    {
        get
        {
            if (_scheduler == null)
            {
                // Deliberately a throw, not `return false`. AttachScheduler runs unconditionally during runtime construction, so a null here can only be a
                // construction-order bug — and answering "false" would turn that bug into a track that is silently dead for the life of the process, which is
                // the exact failure mode this whole track exists to avoid being (§ 2.3's trap, one layer up).
                throw new InvalidOperationException(
                    "SubscriptionsContext has no scheduler bound. AttachScheduler must run before the first tick.");
            }

            return SessionCount > 0 && !_scheduler.IsTickAborted && !_scheduler.IsFenceFailed;
        }
    }

    /// <summary>Chunks that actually executed this tick, across every stage. AC-6 asserts this is zero when no session is connected.</summary>
    public int ChunksExecuted => Volatile.Read(ref _chunksExecuted.Value);

    /// <summary>Counts one executed chunk. Called from worker threads, so it is interlocked.</summary>
    internal void NoteChunkExecuted() => Interlocked.Increment(ref _chunksExecuted.Value);

    // ═══════════════════════════════════════════════════════════════
    // Tick-phase ordering journal — the instrument SUB-02 is asserted through
    // ═══════════════════════════════════════════════════════════════
    //
    // SUB-02 is an ORDERING rule, and the obvious way to check an ordering is to time the phases and compare. That test would be a flake generator: the phases
    // are sub-millisecond, the flush blocks on an fsync of unpredictable length, and the compute half runs on worker threads whose scheduling the test does not
    // control. These four stamps record the ORDER the phases were reached in, not when — a monotone sequence number claimed by whichever phase gets there
    // first. A zero means the phase did not happen at all this tick, which is what makes "nothing was computed or published for an aborted tick" directly
    // observable rather than inferred from an absence of side effects.

    private int _faulted;
    private int _phaseSeq;
    private int _fenceSeq;
    private int _computeSeq;
    private int _flushSeq;
    private int _publishSeq;

    /// <summary>
    /// Order in which the tick fence's serial work completed and the deferred dispatch was about to begin; zero when the fence threw before reaching it.
    /// </summary>
    /// <remarks>
    /// It marks the serial fence prep rather than the whole fence, and the distinction is real on the parallel path: there, the Fence DAG runs inside the very
    /// dispatch call that also runs this track, so no statement exists between the two to stamp from. What orders them is the track barrier (PH-01) —
    /// Engine-Post completes before Engine-Subscriptions begins — which is a structural guarantee asserted separately, by the track-order test, rather
    /// than by this counter.
    /// </remarks>
    public int FenceSeq => Volatile.Read(ref _fenceSeq);

    /// <summary>Order in which the track's first stage passed its gate; zero if the track was gated off or never dispatched.</summary>
    public int ComputeSeq => Volatile.Read(ref _computeSeq);

    /// <summary>Order in which the UoW flush completed; zero if it did not.</summary>
    public int FlushSeq => Volatile.Read(ref _flushSeq);

    /// <summary>Order in which publication ran; zero when it was suppressed.</summary>
    public int PublishSeq => Volatile.Read(ref _publishSeq);

    /// <summary>True when a stage threw this tick. Publication is suppressed for such a tick — see the remarks on <see cref="NoteFault"/>.</summary>
    public bool Faulted => Volatile.Read(ref _faulted) != 0;

    /// <summary>
    /// Records that a stage threw this tick, so publication can be suppressed with the compute half.
    /// </summary>
    /// <remarks>
    /// Needed because making replication's failure NON-terminal (<see cref="Track.FailureIsTerminal"/>) removed the very signal publication used to key
    /// on. A stage throw no longer latches <c>IsFenceFailed</c> and never latched <c>IsTickAborted</c>, so the publish gate
    /// <c>!tickAborted &amp;&amp; !fenceFailed</c> stays true for a tick whose compute blew up. SUB-02 requires compute and publish to be
    /// skippable together and ONLY together, and without this flag the isolation fix would have quietly broken exactly that: frames half-produced by a
    /// faulted tick, published as if the tick had succeeded, moving every receiving session's baseline past records it was never sent.
    /// </remarks>
    internal void NoteFault() => Volatile.Write(ref _faulted, 1);

    internal void NoteFence() => StampOnce(ref _fenceSeq);

    internal void NoteCompute() => StampOnce(ref _computeSeq);

    internal void NoteFlush() => StampOnce(ref _flushSeq);

    internal void NotePublish() => StampOnce(ref _publishSeq);

    /// <summary>
    /// Claims the next sequence number for a phase that has not yet been stamped this tick.
    /// </summary>
    /// <remarks>
    /// Interlocked because the compute stamp is claimed from whichever stage clears its gate first, and <c>Interest</c> and <c>Events</c> are independent DAG
    /// roots that become ready together. A racing loser burns a sequence number without storing it, which leaves gaps — harmless, because every assertion on
    /// these is a comparison and never an equality against a literal.
    /// </remarks>
    private void StampOnce(ref int slot)
    {
        if (Volatile.Read(ref slot) != 0)
        {
            return;
        }

        var seq = Interlocked.Increment(ref _phaseSeq);
        Interlocked.CompareExchange(ref slot, seq, 0);
    }

    /// <summary>Resets the per-tick state. Called on the TickDriver thread immediately before the track is dispatched.</summary>
    public void Reset(long tickNumber, int workerCount)
    {
        TickNumber = tickNumber;
        WorkerCount = workerCount;
        Volatile.Write(ref _chunksExecuted.Value, 0);
        Volatile.Write(ref _faulted, 0);
        Volatile.Write(ref _phaseSeq, 0);
        Volatile.Write(ref _fenceSeq, 0);
        Volatile.Write(ref _computeSeq, 0);
        Volatile.Write(ref _flushSeq, 0);
        Volatile.Write(ref _publishSeq, 0);
    }
}
