namespace Typhon.Engine.Internals;

/// <summary>
/// Bundle returned by <see cref="FenceDagBuilder.DeclareFenceDag"/>. Holds references to the four chained fence-phase exec systems so <c>TyphonRuntime</c> can
/// read post-dispatch state (e.g. <c>HighestLsn</c> on Finalize).
/// </summary>
internal readonly struct FenceExecBundle
{
    public readonly FencePrepExecSystem Prep;
    public readonly FenceMigrateExecSystem Migrate;
    public readonly FenceAabbRefreshExecSystem AabbRefresh;
    public readonly FenceFinalizeExecSystem Finalize;

    public FenceExecBundle(FencePrepExecSystem prep, FenceMigrateExecSystem migrate, FenceAabbRefreshExecSystem aabbRefresh, FenceFinalizeExecSystem finalize)
    {
        Prep = prep;
        Migrate = migrate;
        AabbRefresh = aabbRefresh;
        Finalize = finalize;
    }
}

/// <summary>
/// Declares the engine's parallel Fence as a normal <see cref="Dag"/> on the schedule's Engine-Post track. The four chained systems
/// (<see cref="FencePrepExecSystem"/> → <see cref="FenceMigrateExecSystem"/> → <see cref="FenceAabbRefreshExecSystem"/> → <see cref="FenceFinalizeExecSystem"/>)
/// are ordered by <c>.After()</c> edges within a single implicit phase. The Fence DAG is declared and executed exactly like any app DAG — its Engine-Post track
/// is dispatched by the runtime after the serial <c>WriteTickFence</c> prep.
///
/// <para>Invoked by <c>TyphonRuntime</c> during runtime construction, after the app has populated the schedule but before <see cref="RuntimeSchedule.Build"/>.</para>
/// </summary>
internal static class FenceDagBuilder
{
    /// <summary>Name of the engine-internal Fence DAG, declared on the Engine-Post track.</summary>
    private const string DagName = "Fence";

    public static FenceExecBundle DeclareFenceDag(RuntimeSchedule schedule, DatabaseEngine engine)
    {
        var prep = new FencePrepExecSystem(engine);
        var migrate = new FenceMigrateExecSystem(engine);
        var aabbRefresh = new FenceAabbRefreshExecSystem(engine);
        var finalize = new FenceFinalizeExecSystem(engine);

        var dag = schedule.EnginePostTrack.DeclareDag(DagName);
        dag.Add(prep);
        dag.Add(migrate);
        dag.Add(aabbRefresh);
        dag.Add(finalize);

        return new FenceExecBundle(prep, migrate, aabbRefresh, finalize);
    }
}
