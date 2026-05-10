// unset

using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Configuration for the background statistics rebuild subsystem.
/// Controls when and how HyperLogLog, MostCommonValues, and Histogram statistics
/// are rebuilt for indexed fields.
/// </summary>
[PublicAPI]
public class StatisticsOptions
{
    /// <summary>Number of index mutations before a rebuild is triggered for a ComponentTable.</summary>
    public int MutationThreshold { get; set; } = 1000;

    /// <summary>Worker thread poll interval in milliseconds.</summary>
    public int PollIntervalMs { get; set; } = 5000;

    /// <summary>Skip tables with fewer entities than this threshold.</summary>
    public int MinEntitiesForRebuild { get; set; } = 100;

    /// <summary>
    /// Minimum number of entities to trigger page-granularity sampling (below this, full scan is used).
    /// When entity count exceeds this threshold, the scan reads only a subset of pages to bound scan time.
    /// </summary>
    public int SamplingMinEntities { get; set; } = 10000;

    /// <summary>Set to false to disable the background statistics worker thread entirely.</summary>
    public bool Enabled { get; set; } = true;
}
