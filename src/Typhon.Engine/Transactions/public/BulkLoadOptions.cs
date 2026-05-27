using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Options for opening a <see cref="BulkLoadSession"/> via <c>DatabaseEngine.BeginBulkLoad</c>. All settings are optional — the default-constructed instance
/// is valid.
/// </summary>
/// <remarks>
/// See <c>claude/design/Durability/BulkLoad/01-api.md</c> for the full API reference and lifecycle.
/// </remarks>
[PublicAPI]
public sealed class BulkLoadOptions
{
    /// <summary>
    /// Optional reporter invoked after every <see cref="ProgressBatchSize"/> entities. Useful for UI progress bars and the Workbench's stall detector. Default:
    /// <see langword="null"/> (no callbacks).
    /// </summary>
    public Action<BulkLoadProgress> ProgressReporter { get; init; }

    /// <summary>
    /// Number of entities between progress callbacks. Smaller values increase callback overhead; larger values reduce UI responsiveness. Default:
    /// <c>10_000</c>.
    /// </summary>
    public int ProgressBatchSize { get; init; } = 10_000;

    /// <summary>
    /// Maximum time <see cref="BulkLoadSession.CompleteBulkLoad"/> will wait for the synchronous checkpoint to complete. If exceeded,
    /// <see cref="BulkLoadCheckpointTimeoutException"/> is thrown and the session remains alive (caller may retry or dispose). Default: 5 minutes.
    /// </summary>
    public TimeSpan CheckpointTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Snapshot of bulk-session progress passed to <see cref="BulkLoadOptions.ProgressReporter"/>.
/// </summary>
[PublicAPI]
public readonly struct BulkLoadProgress
{
    /// <summary>Number of entities spawned so far via <see cref="BulkLoadSession.Spawn{TArch}"/>.</summary>
    public long EntitiesSpawned { get; init; }

    /// <summary>Number of entities updated so far via <see cref="BulkLoadSession.Update{T}"/>.</summary>
    public long EntitiesUpdated { get; init; }

    /// <summary>Number of entities destroyed so far via <see cref="BulkLoadSession.Destroy"/>.</summary>
    public long EntitiesDestroyed { get; init; }

    /// <summary>Cumulative number of pages allocated by the bulk session.</summary>
    public long PagesAllocated { get; init; }

    /// <summary>Elapsed milliseconds since the session opened.</summary>
    public long ElapsedMilliseconds { get; init; }
}
