using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine;

/// <summary>Options of <see cref="BackupOperations.Compact"/> and <c>BackupOperations.ApplyRetention</c>.</summary>
[PublicAPI]
public sealed class BackupLifecycleOptions
{
    /// <summary>Read-window memory, split across the packs of the chain being compacted.</summary>
    public long ReadBudgetBytes { get; init; } = 64L << 20;

    /// <summary>
    /// Stops a compaction (observed every 4 MiB read; nothing is published) or a retention (between deletes; what was deleted stays deleted, every
    /// point still listed resolves).
    /// </summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>Called during a compaction with the bytes read so far and the bytes its chain holds.</summary>
    public Action<long, long> Progress { get; init; }

    /// <summary>Where the store logs.</summary>
    public ILogger Logger { get; init; }
}

/// <summary>What a compaction did.</summary>
/// <param name="Point">The point, as its compacted pack now describes it (or its existing base when it already was one).</param>
/// <param name="AlreadyBase">The point was already a base (a full point, or compacted before): nothing was written.</param>
/// <param name="ChainDepth">The packs its chain read.</param>
/// <param name="Pages">The image's pages, every one held by the compacted pack.</param>
/// <param name="HolePages">Pages no pack of the chain held, written as zero entries.</param>
/// <param name="BytesRead">Bytes read from the chain's packs (the pre-publish verify excluded).</param>
/// <param name="BytesWritten">The compacted pack's size.</param>
/// <param name="Duration">Wall time, the pre-publish verify included.</param>
[PublicAPI]
public sealed record BackupCompactionResult(BackupPointSummary Point, bool AlreadyBase, int ChainDepth, int Pages, int HolePages, long BytesRead,
    long BytesWritten, TimeSpan Duration);

/// <summary>Why a retention could not meet its policy (05 §3 R3): it kept more than the limits allow, and says which limit it could not meet.</summary>
[PublicAPI]
public enum BackupRetentionBlock
{
    /// <summary>Every limit is met.</summary>
    None = 0,

    /// <summary><see cref="BackupRetentionPolicy.MinKeep"/> kept points a size, count or age limit would have removed.</summary>
    MinKeep,

    /// <summary><see cref="BackupRetentionPolicy.KeepLastDrilled"/> kept the newest drill-passed point (and every point after it) past a limit.</summary>
    KeepLastDrilled,

    /// <summary>A chain holds a point at or after the cutoff that does not resolve: nothing of that chain is deleted until it is repaired or gone.</summary>
    DamagedChain,

    /// <summary>The volume has less free space than a compaction needs (it writes a whole image beside the packs it reads): nothing was written.</summary>
    InsufficientSpace,
}

/// <summary>What a retention would do, or did: the kept points, the compactions it runs first, then the packs it deletes.</summary>
/// <param name="Kept">The points kept, ascending.</param>
/// <param name="Removed">The points whose packs are deleted (restorable or not), ascending.</param>
/// <param name="Compactions">The points compacted before any delete, ascending.</param>
/// <param name="PackDeletions">The pack files deleted, in delete order (descending id: a crash part-way leaves every listed point restorable).</param>
/// <param name="CutoffId">The oldest kept point (<c>K</c>); 0 when nothing is kept.</param>
/// <param name="BytesBefore">The store's pack bytes now.</param>
/// <param name="BytesAfterAtMost">
/// An upper bound of the pack bytes once applied: the packs the kept points resolve through now (a compacted base holds each page of its chain once,
/// so it is no larger than the packs it replaces plus its index).
/// </param>
/// <param name="Blocked">The limit it could not meet, or <see cref="BackupRetentionBlock.None"/>.</param>
/// <param name="BlockedReason">Why, in one sentence; <c>null</c> when not blocked.</param>
/// <param name="Fingerprint">
/// A CRC of the deletions and compactions: <c>BackupOperations.ApplyRetention</c> refuses a plan that would now delete a pack the approved
/// one did not list.
/// </param>
[PublicAPI]
public sealed record BackupRetentionPlan(IReadOnlyList<uint> Kept, IReadOnlyList<uint> Removed, IReadOnlyList<uint> Compactions,
    IReadOnlyList<string> PackDeletions, uint CutoffId, long BytesBefore, long BytesAfterAtMost, BackupRetentionBlock Blocked, string BlockedReason,
    string Fingerprint);

/// <summary>What a retention did.</summary>
/// <param name="Plan">The plan it applied (re-planned after its compactions).</param>
/// <param name="Compactions">The compactions it ran, failed ones excluded (their chains keep every pack this run).</param>
/// <param name="Deleted">The pack files deleted, in order.</param>
/// <param name="BytesFreed">Their bytes, minus the compacted packs written.</param>
/// <param name="Duration">Wall time.</param>
[PublicAPI]
public sealed record BackupRetentionResult(BackupRetentionPlan Plan, IReadOnlyList<BackupCompactionResult> Compactions, IReadOnlyList<string> Deleted,
    long BytesFreed, TimeSpan Duration);
