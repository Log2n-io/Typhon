namespace Typhon.Engine.Internals;

/// <summary>
/// Declares the engine's replication pipeline as a normal <see cref="Dag"/> on the schedule's Engine-Subscriptions track, mirroring
/// <see cref="FenceDagBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Serial work lives in <c>Prepare</c>, not in systems of its own.</b> Every dispatch is a worker wake/barrier cycle (≈ 0.1 ms), so the stage count is
/// a cost rather than a description of reading order. The pipeline's serial steps — the blocks step, the index offsets, the session prologue — run in
/// their stage's single-threaded <c>Prepare</c>, where they cost a function call instead of a whole barrier; <c>Events</c> depends on nothing and runs as a
/// parallel branch. The chain is <c>Project → PushIndex → PushFar → Frames</c> (<c>design/Subscriptions/02-execution.md § 2</c>).
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
/// <b>Two shapes are declared, and exactly one of them prepares chunks per tick.</b> Below a configured amount of work the staged systems are replaced
/// by <see cref="SubscriptionsCollapsedExecSystem"/>, which runs the same stage bodies inline with no barrier between them.
/// Both are members of the one DAG and both are gated on <see cref="SubscriptionsPipelineShape"/>, which decides once per tick; the losing shape's systems
/// clear no gate, prepare nothing and complete inline, which is the same cost an idle track already pays. Declaring the collapsed system conditionally — at
/// Build time, from the option — was the alternative, and it is the § 2.3 trap again in a new place: the threshold is measured per tick, not per runtime, so a
/// runtime that fell below it after Build would find the shape it needed had never been declared.
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

        // One selector for the whole track, so the five systems cannot disagree about the shape of a tick.
        var shape = new SubscriptionsPipelineShape();

        var dag = schedule.EngineSubscriptionsTrack.DeclareDag(DagName);
        dag.Add(new SubscriptionsProjectExecSystem(engine, shape));
        dag.Add(new SubscriptionsEventsExecSystem(engine, shape));
        dag.Add(new SubscriptionsPushIndexExecSystem(engine, shape));
        dag.Add(new SubscriptionsPushFarExecSystem(engine, shape));
        dag.Add(new SubscriptionsFramesExecSystem(engine, shape));

        // A root of its own, with no edge to any of the four above: it is their replacement, never their successor. Nothing orders it against them because
        // they never run on the same tick.
        dag.Add(new SubscriptionsCollapsedExecSystem(engine, shape));
    }
}
