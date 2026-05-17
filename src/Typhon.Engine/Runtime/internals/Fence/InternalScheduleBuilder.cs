namespace Typhon.Engine.Internals;

/// <summary>
/// Bundle returned by <see cref="InternalScheduleBuilder.AddFenceExec"/>. Holds references to the four chained internal exec systems so <c>TyphonRuntime</c>
/// can read post-dispatch state (e.g. <c>HighestLsn</c> on Finalize).
/// </summary>
internal readonly struct FenceExecBundle
{
    public readonly FencePrepExecSystem Prep;
    public readonly FenceMigrateExecSystem Migrate;
    public readonly FenceAabbRefreshExecSystem AabbRefresh;
    public readonly FenceFinalizeExecSystem Finalize;

    public FenceExecBundle(FencePrepExecSystem prep, FenceMigrateExecSystem migrate,
        FenceAabbRefreshExecSystem aabbRefresh, FenceFinalizeExecSystem finalize)
    {
        Prep = prep;
        Migrate = migrate;
        AabbRefresh = aabbRefresh;
        Finalize = finalize;
    }
}

/// <summary>
/// Registers the engine-internal fence systems on the provided <see cref="RuntimeSchedule"/>. The three systems (<see cref="FencePrepExecSystem"/>,
/// <see cref="FenceMigrateExecSystem"/>, <see cref="FenceFinalizeExecSystem"/>) are flagged <c>Internal()</c> and chained via <c>DependsOn</c> so
/// <see cref="RuntimeSchedule.Build"/> partitions them into the <c>DagScheduler</c>'s internal sub-DAG with proper Prep → Migrate → Finalize ordering.
/// Dispatched separately from the user DAG after each tick.
///
/// <para>Invoked by <c>TyphonRuntime</c> during runtime construction, after the user has populated the schedule but before
/// <see cref="RuntimeSchedule.Build"/> runs.</para>
/// </summary>
internal static class InternalScheduleBuilder
{
    public static FenceExecBundle AddFenceExec(RuntimeSchedule schedule, DatabaseEngine engine)
    {
        var prep = new FencePrepExecSystem(engine);
        var migrate = new FenceMigrateExecSystem(engine);
        var aabbRefresh = new FenceAabbRefreshExecSystem(engine);
        var finalize = new FenceFinalizeExecSystem(engine);
        schedule.Add(prep);
        schedule.Add(migrate);
        schedule.Add(aabbRefresh);
        schedule.Add(finalize);
        return new FenceExecBundle(prep, migrate, aabbRefresh, finalize);
    }
}
