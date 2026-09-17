namespace Typhon.Engine.Internals;

/// <summary>
/// Declares the engine's replication pipeline as a normal <see cref="Dag"/> on the schedule's Engine-Subscriptions track, mirroring
/// <see cref="FenceDagBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three dispatched stages on the critical path, not six chained ones.</b> Every dispatch is a worker wake/barrier cycle (≈ 0.1 ms), so the stage
/// count is a cost rather than a description of reading order. The pipeline's serial steps — the prologue, and creating the blocks of newly watched
/// clusters — run inside
/// the following stage's single-threaded <c>Prepare</c>, where they cost a function call instead of a whole barrier; and <c>Events</c> runs as a parallel
/// branch because it depends on neither interest nor projection. The critical path is <c>Interest → Project → Frames</c>
/// (<c>design/Subscriptions/foundation/03-subscriptions-track.md § 2.5</c>).
/// </para>
/// <para>
/// <b>Declared unconditionally, unlike the Fence DAG.</b> <see cref="FenceDagBuilder"/> is called only when <c>RuntimeOptions.EnableParallelFence</c> is set,
/// because the serial fence is a genuine alternative implementation of the same work. This track has no alternative: it is the only place replication runs. An
/// option to disable it would be a knob whose only setting worth choosing is the default — and it would reintroduce, as configuration, exactly the "silently
/// never runs" trap § 2.3 exists to close.
/// </para>
/// <para>
/// <b>What an idle track actually costs, corrected.</b> This said "one <c>ShouldRun</c> check per stage", and that was wrong:
/// <c>DagScheduler.DispatchTrackMultiThreaded</c> skipped only genuinely EMPTY tracks, so four gated-off stages still bought a generation bump and a wake of
/// every worker — ≈ 0.1 ms per tick, charged to every existing runtime for a feature none had enabled. The dispatcher now returns before the wake when a
/// track's members all completed inline, which is what a fully gated-off track does. With that, the four checks really are the cost; without it the claim was
/// a comment contradicted by the code beside it.
/// </para>
/// <para>
/// <b>No bundle returned, unlike <see cref="FenceDagBuilder"/>.</b> That one hands back its exec systems so the runtime can read post-dispatch state from them
/// (<c>Finalize.HighestLsn</c>). These stages publish nothing the runtime reads — everything shared flows through <see cref="SubscriptionsContext"/> — so
/// returning handles to them would be structure built against a need that does not exist yet.
/// </para>
/// </remarks>
internal static class SubscriptionsDagBuilder
{
    /// <summary>Name of the engine-internal Subscriptions DAG, declared on the Engine-Subscriptions track.</summary>
    private const string DagName = "Subscriptions";

    /// <summary>Name of the ingress DAG, declared on the Engine-Pre track.</summary>
    private const string IngressDagName = "SubscriptionsIngress";

    public static void DeclareSubscriptionsDag(RuntimeSchedule schedule, DatabaseEngine engine)
    {
        // Engine-Pre, and it is the whole of that track's content (RuntimeSchedule.cs:38 names it as this drain's home). It is a DAG of its own rather than a
        // stage of the one below, because the two run at opposite ends of the tick: ingress has to be BEFORE the application's track, so a command applies in
        // the tick it arrived for, and replication has to be AFTER the fence, so it computes on committed state.
        schedule.EnginePreTrack.DeclareDag(IngressDagName).Add(new SubscriptionsIngressExecSystem());

        var dag = schedule.EngineSubscriptionsTrack.DeclareDag(DagName);
        dag.Add(new SubscriptionsInterestExecSystem(engine));
        dag.Add(new SubscriptionsProjectExecSystem(engine));
        dag.Add(new SubscriptionsEventsExecSystem(engine));
        dag.Add(new SubscriptionsFramesExecSystem(engine));
    }
}
