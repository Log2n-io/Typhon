using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The continuous WAL archiver of one engine (#1418, impl/1418 §4.5): the seam the backup scheduler (#1416) drives. Built by
/// <see cref="DatabaseEngine.CreateWalArchiver(ArchiveOptions, string)"/>; <c>null</c> when archiving is not configured.
/// </summary>
/// <remarks>
/// Two parts. <see cref="Step"/> runs on the scheduler's tick and does only bounded work: a time sample, the switch decision, the release and gap
/// bookkeeping, the state publish. Every segment read and store write runs on the archiver's own worker thread (<c>Typhon-WalArchive</c>), so a hung
/// store never stops the tick. Nothing of the engine waits on either: the archive floor and its backlog release are the checkpoint's (BKP-10).
/// </remarks>
internal interface IWalArchiver : IDisposable
{
    /// <summary>
    /// On the scheduler tick. Bounded and no I/O: time sample, switch decision (age measured on <paramref name="monoNow"/>), release and gap
    /// bookkeeping, state publish; then it wakes the worker. Never throws.
    /// </summary>
    /// <param name="utcNow">The wall clock, for the time index.</param>
    /// <param name="monoNow">A monotonic clock in <see cref="System.Diagnostics.Stopwatch"/> ticks, for ages.</param>
    /// <param name="ct">The scheduler's stop token.</param>
    void Step(DateTime utcNow, long monoNow, CancellationToken ct);

    /// <summary>The archive's state: immutable, published with a release store, 0 B per read.</summary>
    WalArchiveState State { get; }

    /// <summary>True once per backlog release, gap, timeline fork, or archiving starting on a timeline with no point: the scheduler then takes one.</summary>
    bool ConsumeImmediatePointRequest();

    /// <summary>
    /// The close's final pass (WA-23): called after the close drains and the free quarantine's end, before the final checkpoint. Requests a switch,
    /// archives what is sealed, then stops the worker, all within <paramref name="budget"/>. Past it the worker is detached: it touches nothing of the
    /// engine again, and its native buffers are left to it rather than freed under it.
    /// </summary>
    void Stop(TimeSpan budget);
}

/// <summary>What the archive is doing.</summary>
internal enum WalArchiveCondition : byte
{
    /// <summary>Archiving is not configured.</summary>
    Off,

    /// <summary>The archiver exists and has not published its first state from the store yet.</summary>
    Starting,

    /// <summary>Every durable segment is archived, or being archived.</summary>
    Archiving,

    /// <summary>The store is failing; the archiver retries, and the floor keeps the segments until the backlog limit.</summary>
    Stalled,

    /// <summary>The backlog limit released the floor: segments are being reclaimed un-archived, and a gap follows.</summary>
    Released,

    /// <summary>The store holds another history under this timeline (a copied database): archiving stopped.</summary>
    Diverged,

    /// <summary>Refused at start (a store of another database, a store whose locking is not enforced).</summary>
    Failed,
}

/// <summary>Why the archive misses a range of LSNs.</summary>
internal enum ArchiveGapReason : byte
{
    /// <summary>No gap.</summary>
    None = 0,

    /// <summary>The backlog limit released the floor and the checkpoint reclaimed segments before they were archived.</summary>
    BacklogReleased = 1,

    /// <summary>The oldest segment present is past the archive's end: archiving was off, or the store was replaced.</summary>
    ArchivingOff = 2,

    /// <summary>A discontinuity with no known cause.</summary>
    Unknown = 3,

    /// <summary>A crash tore the end of a segment, and records recovery applies alone (fence records) were in its incomplete last frame.</summary>
    TornAtCrash = 4,
}

/// <summary>The archive's state as <see cref="IWalArchiver.State"/> publishes it (impl/1418 §4.5).</summary>
/// <param name="Condition">What the archive is doing.</param>
/// <param name="TimelineId">The WAL timeline being archived.</param>
/// <param name="ArchivedThroughLsn">Every LSN up to this one is archived (contiguously from the archive's start); 0 = nothing yet.</param>
/// <param name="LagBytes">Durable WAL bytes not archived yet: the active segment's written part and every un-archived sealed segment.</param>
/// <param name="BacklogBytes">Bytes on disk held only by the archive floor: what <see cref="ArchiveOptions.BacklogLimitBytes"/> caps.</param>
/// <param name="LagSeconds">
/// The RPO: 0 when everything durable is archived, else the seconds since the first durable LSN past <paramref name="ArchivedThroughLsn"/> was sampled.
/// </param>
/// <param name="GapsTotal">Gaps this session recorded.</param>
/// <param name="LastGapFromLsn">The first LSN of the last gap; 0 = none.</param>
/// <param name="LastGapToLsn">The last LSN of the last gap; 0 = none.</param>
/// <param name="LastGapReason">Why the last gap exists.</param>
/// <param name="LastArchivedSealUtc">When the newest archived segment was sealed; <see cref="DateTime.MinValue"/> when unknown or none.</param>
/// <param name="LastError">The last failure, or <c>null</c>.</param>
internal readonly record struct WalArchiveState(WalArchiveCondition Condition, Guid TimelineId, long ArchivedThroughLsn, long LagBytes,
    long BacklogBytes, double LagSeconds, long GapsTotal, long LastGapFromLsn, long LastGapToLsn, ArchiveGapReason LastGapReason,
    DateTime LastArchivedSealUtc, string LastError);

/// <summary>Where archiving is configured until the backup options (#1416) carry it: the options and the store the archive is written to.</summary>
internal sealed class WalArchiveConfiguration
{
    public required ArchiveOptions Options { get; init; }

    public required string StorePath { get; init; }
}
