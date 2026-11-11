using Microsoft.Extensions.Logging;

namespace Typhon.Engine.Internals;

/// <summary>The phase an index rebuild is in, for its progress line and its trace spans (#1588, design §12).</summary>
internal enum IndexRebuildPhase : byte
{
    /// <summary>The whole archetype: the span around its three phases.</summary>
    Archetype = 0,

    /// <summary>The cluster walk that copies the keys out, one span per partition.</summary>
    Stage = 1,

    /// <summary>One sorted run written to the temp file.</summary>
    Spill = 2,

    /// <summary>One tree's merge and bottom-up build.</summary>
    Build = 3,

    /// <summary>The element ids back to the cluster tails.</summary>
    WriteBack = 4,
}

/// <summary>The index rebuild's log lines (#1588, design §12): typed, level-checked before any argument is formatted.</summary>
internal static partial class IndexRebuildLog
{
    [LoggerMessage(LogLevel.Information,
        "Index rebuild of '{archetype}': {clusters} clusters, {trees} trees ({multiTrees} multi-value), a {budgetMiB} MiB budget, {workers} workers, "
        + "{partitions} stage partitions.")]
    public static partial void Start(ILogger logger, string archetype, int clusters, int trees, int multiTrees, long budgetMiB, int workers,
        int partitions);

    [LoggerMessage(LogLevel.Information,
        "Index rebuild of '{archetype}', {phase}: {done} of {total} ({percent:F1} %), {ratePerSecond:F0}/s, about {etaSeconds:F0} s left.")]
    public static partial void Progress(ILogger logger, string archetype, IndexRebuildPhase phase, long done, long total, double percent,
        double ratePerSecond, double etaSeconds);

    [LoggerMessage(LogLevel.Information, "Index rebuild of '{archetype}', {phase}: {done} of {total} ({percent:F1} %), no rate yet.")]
    public static partial void ProgressNoRate(ILogger logger, string archetype, IndexRebuildPhase phase, long done, long total, double percent);

    [LoggerMessage(LogLevel.Information,
        "Index rebuild of '{archetype}' done in {totalMs:F0} ms (stage {stageMs:F0}, build {buildMs:F0}, write-back {writeBackMs:F0}): {entries} entries, "
        + "{runs} runs, {extraMergePasses} extra merge passes, {tempMiB:F1} MiB of temp data, {nodes} nodes; data-file pages {pagesRead} read, "
        + "{pagesWritten} written.")]
    public static partial void Done(ILogger logger, string archetype, double totalMs, double stageMs, double buildMs, double writeBackMs, long entries,
        int runs, int extraMergePasses, double tempMiB, long nodes, long pagesRead, long pagesWritten);

    [LoggerMessage(LogLevel.Error,
        "Index rebuild of '{archetype}': the temp block at offset {offset} of '{path}' failed its check ({check}). The open fails; the next one rebuilds.")]
    public static partial void TempCorrupt(ILogger logger, string archetype, long offset, string path, string check);
}
