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
}
