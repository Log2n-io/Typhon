using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Standard metric names for consistent taxonomy across components.
/// </summary>
/// <remarks>
/// <para>
/// Using centralized names ensures:
/// </para>
/// <list type="bullet">
/// <item><description>Consistent Grafana dashboard queries (e.g., <c>sum(typhon_cache_hits)</c>)</description></item>
/// <item><description>IntelliSense discoverability</description></item>
/// <item><description>No typos across components</description></item>
/// </list>
/// <para>
/// Use these constants with <see cref="IMetricWriter.WriteThroughput"/> and
/// <see cref="IMetricWriter.WriteDuration"/> instead of string literals.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// writer.WriteThroughput(MetricNames.CacheHits, _cacheHits);
/// writer.WriteThroughput(MetricNames.CacheMisses, _cacheMisses);
/// writer.WriteDuration(MetricNames.Flush, _lastFlushUs, _avgFlushUs, _maxFlushUs);
/// </code>
/// </example>
[PublicAPI]
public static class MetricNames
{
    // ═══════════════════════════════════════════════════════════════
    // CACHE METRICS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Number of cache hits (successful lookups).</summary>
    public const string CacheHits = "CacheHits";

    /// <summary>Number of cache misses (lookups requiring load).</summary>
    public const string CacheMisses = "CacheMisses";

    /// <summary>Number of entries evicted from cache.</summary>
    public const string Evictions = "Evictions";

    // ═══════════════════════════════════════════════════════════════
    // TRANSACTION METRICS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Number of transactions/objects created.</summary>
    public const string Created = "Created";

    /// <summary>Number of transactions successfully committed.</summary>
    public const string Committed = "Committed";

    /// <summary>Number of transactions rolled back.</summary>
    public const string RolledBack = "RolledBack";

    /// <summary>Number of concurrency conflicts detected.</summary>
    public const string Conflicts = "Conflicts";

    // ═══════════════════════════════════════════════════════════════
    // INDEX METRICS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Number of index lookups (point queries).</summary>
    public const string Lookups = "Lookups";

    /// <summary>Number of range scans performed.</summary>
    public const string RangeScans = "RangeScans";

    /// <summary>Number of entries inserted.</summary>
    public const string Inserts = "Inserts";

    /// <summary>Number of entries deleted.</summary>
    public const string Deletes = "Deletes";

    /// <summary>Number of node splits (B+Tree growth).</summary>
    public const string Splits = "Splits";

    /// <summary>Number of node merges (B+Tree compaction).</summary>
    public const string Merges = "Merges";

    // ═══════════════════════════════════════════════════════════════
    // WAL / DURABILITY METRICS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Number of WAL records written.</summary>
    public const string RecordsWritten = "RecordsWritten";

    /// <summary>Number of flush operations completed.</summary>
    public const string Flushes = "Flushes";

    /// <summary>Number of checkpoints completed.</summary>
    public const string CheckpointsCompleted = "CheckpointsCompleted";

    // ═══════════════════════════════════════════════════════════════
    // GENERAL METRICS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Number of heartbeat checks performed.</summary>
    public const string HeartbeatsChecked = "HeartbeatsChecked";

    // ═══════════════════════════════════════════════════════════════
    // DURATION OPERATION NAMES
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Duration of transaction lifetime (create to commit/rollback).</summary>
    public const string TransactionLifetime = "TransactionLifetime";

    /// <summary>Duration of checkpoint flush operations.</summary>
    public const string CheckpointFlush = "CheckpointFlush";

    /// <summary>Duration of general flush operations.</summary>
    public const string Flush = "Flush";

    /// <summary>Duration of commit operations.</summary>
    public const string Commit = "Commit";

    /// <summary>Duration of snapshot creation.</summary>
    public const string SnapshotCreation = "SnapshotCreation";
}
