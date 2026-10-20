using Microsoft.Extensions.Logging;

namespace Typhon.Engine.Internals;

/// <summary>The replication track's log lines. Raised from the serial blocks step only, and only at the edges of a condition, never per tick.</summary>
internal static partial class SubscriptionsLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Replication state pool for {Archetype} is exhausted: {Clusters} pushed clusters have no block, so their entities are not replicated until "
                  + "one frees. The budget is {BudgetBytes} B for this archetype ({Blocks} blocks of {BlockBytes} B, one per cluster): raise "
                  + "SubscriptionsOptions.StatePoolBudgetBytes to at least clusters x block size")]
    public static partial void StatePoolExhausted(ILogger logger, string archetype, int clusters, long budgetBytes, int blocks, int blockBytes);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Replication state pool for {Archetype} recovered: every pushed cluster has a block again, after {Ticks} ticks and {Deferrals} deferred "
                  + "cluster-ticks")]
    public static partial void StatePoolRecovered(ILogger logger, string archetype, long ticks, long deferrals);

    // At most once a second, and only in a second the engine itself closed someone (FrameAssembler.ReportClosures).
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Replication closed {EngineClosed} sessions in the last {WindowMs} ms: {Silent} silent (4001, no PING for {SilenceMs} ms), {Lagging} "
                  + "lagging (1013), {Protocol} protocol errors, {Abusive} abusive, {Internal} internal errors; beside {LinkLost} links lost and {Left} "
                  + "clients leaving. Frames skipped in the window: {InFlightFull} in flight full, {LagSkips} lagging, {PoolRefused} pool refused, "
                  + "{Oversize} oversize, {Gated} gated, {OwedReset} owed reset. Longest tick interval {LongestTickMs:F1} ms against a "
                  + "{PeriodMs:F1} ms period; {Open} sessions open")]
    public static partial void SessionsClosedByEngine(ILogger logger, long engineClosed, long windowMs, long silent, long silenceMs, long lagging,
        long protocol, long abusive, long @internal, long linkLost, long left, long inFlightFull, long lagSkips, long poolRefused, long oversize, long gated,
        long owedReset, double longestTickMs, double periodMs, int open);
}
