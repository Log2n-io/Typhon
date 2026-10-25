using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Which points a store keeps (05 §3, ADR-030's dual limit). Every value is load-bearing, so every one is stated: there is no default, and 0 means
/// "no limit" for the limits, never a sentinel.
/// </summary>
/// <remarks>
/// <para>
/// The restorable points are walked newest first. A point is kept while it is among the newest <see cref="MinKeep"/>, or while it is within
/// <see cref="MaxAge"/> AND among the newest <see cref="MaxCount"/> AND the packs every kept point needs stay within <see cref="MaxTotalBytes"/>;
/// the walk stops at the first point that is not kept. <see cref="KeepLastDrilled"/> moves the cutoff back to the newest point whose drill passed.
/// </para>
/// <para>
/// <see cref="MinKeep"/> and <see cref="KeepLastDrilled"/> beat every limit: a limit they prevent is reported (<see cref="BackupRetentionBlock"/>),
/// never met by deleting. <see cref="MaxTotalBytes"/> bounds the store in steady state; a compaction writes a whole image before the packs it replaces
/// are deleted.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class BackupRetentionPolicy
{
    /// <summary>Keep points frozen no longer ago than this. <see cref="TimeSpan.Zero"/> = no age limit.</summary>
    public required TimeSpan MaxAge { get; init; }

    /// <summary>Keep at most this many restorable points. 0 = no count limit; else at least <see cref="MinKeep"/>.</summary>
    public required int MaxCount { get; init; }

    /// <summary>Keep the points whose packs fit in this many bytes. 0 = no size limit.</summary>
    public required long MaxTotalBytes { get; init; }

    /// <summary>Never keep fewer restorable points than this, whatever the limits. At least 1.</summary>
    public required int MinKeep { get; init; }

    /// <summary>
    /// Compact a chain's head when a restore of it would read more packs than this. 0 = never; else at least 2 (a depth of 1 is already a base).
    /// </summary>
    public required int CompactThreshold { get; init; }

    /// <summary>Never delete the newest point whose drill passed, nor any point after it.</summary>
    public required bool KeepLastDrilled { get; init; }

    /// <summary>Checks the ranges, and that at least one of <see cref="MaxAge"/>, <see cref="MaxCount"/>, <see cref="MaxTotalBytes"/> limits something.</summary>
    /// <param name="error">What is wrong, or <c>null</c>.</param>
    public bool Validate(out string error)
    {
        error = MaxAge < TimeSpan.Zero ? $"MaxAge {MaxAge} is negative."
            : MaxCount < 0 ? $"MaxCount {MaxCount} is negative."
            : MaxTotalBytes < 0 ? $"MaxTotalBytes {MaxTotalBytes} is negative."
            : MinKeep < 1 ? $"MinKeep {MinKeep}: at least one point is always kept."
            : CompactThreshold is < 0 or 1 ? $"CompactThreshold {CompactThreshold}: 0 (never) or at least 2."
            : MaxAge == TimeSpan.Zero && MaxCount == 0 && MaxTotalBytes == 0
                ? "No limit is set: give at least one of MaxAge, MaxCount and MaxTotalBytes (a policy that keeps everything is no policy)."
            : MaxCount != 0 && MaxCount < MinKeep ? $"MaxCount {MaxCount} is below MinKeep {MinKeep}: it could never be met."
            : null;
        return error == null;
    }
}
