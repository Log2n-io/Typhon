using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>The facade's WAL-archive side (#1418): plan a restore, list the archive, resolve a time, read the log.</summary>
public static partial class BackupOperations
{
    /// <summary>
    /// What <see cref="Restore"/> would do with these arguments, without doing any of it: read-only, no lock taken, nothing written. Every refusal the
    /// restore would raise is reported, never thrown: a target that exists, a live one, a misnamed one, an image of another format revision, a foreign
    /// staging, an LSN or a time that cannot be reached. Pass its <see cref="BackupRestorePlan.Fingerprint"/> as
    /// <see cref="BackupRestoreOptions.ApprovedPlan"/> to make the restore refuse if what it would restore has changed since.
    /// </summary>
    /// <param name="store">The store directory.</param>
    /// <param name="point">A point, or an LSN or a time of an archived timeline.</param>
    /// <param name="to">The bundle path or its parent directory; null to plan the source only.</param>
    /// <param name="options">Replace and format mismatch, as the restore would take them.</param>
    public static BackupRestorePlan PlanRestore(string store, PointSelector point, string to = null, BackupRestoreOptions options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(store);
        options ??= new BackupRestoreOptions();
        var preview = RestoreStager.Preview(store, point, to, new RestoreOptions
        {
            Replace = options.Replace,
            AllowFormatMismatch = options.AllowFormatMismatch,
            Logger = options.Logger,
        });
        if (preview.Unreachable is { } u)
        {
            return new BackupRestorePlan(null, null, null, [], 0, 0, 0, 0, point.Lsn, u.TimelineId, 0, 0, 0, null, null, u.Reason, u.Message,
                u.NearestRestorable);
        }

        var chain = new List<uint>(preview.Chain.Depth);
        foreach (var p in preview.Chain.Packs)
        {
            chain.Add(p.BackupId);
        }

        var plan = preview.ArchivePlan;
        var sample = plan?.SampleUsed;
        return new BackupRestorePlan(preview.Fingerprint, preview.ResolvedTarget, preview.DisplacedBundle, chain, preview.BytesToRead,
            preview.Chain.Point.BackupId, preview.Chain.Point.CheckpointLsn, plan?.EffectiveLsn ?? preview.Chain.Point.PointLsn, plan?.TargetLsn ?? 0,
            plan?.TimelineId ?? preview.Chain.Point.TimelineId, plan?.ArchivedSegments ?? 0, plan?.ArchivedBytes ?? 0, sample?.Lsn ?? 0,
            sample is { } s ? new DateTime(s.UtcTicks, DateTimeKind.Utc) : null, preview.Refusal?.Reason, null, preview.Refusal?.Message, []);
    }

    /// <summary>
    /// The store's WAL archive: every timeline (parents before children) with its segments, its gaps (whether a later point covers each), and a time
    /// sample per segment. Read-only, from each timeline's index cache where it still describes its files.
    /// </summary>
    /// <param name="store">The store directory.</param>
    public static BackupArchiveListing ListArchive(string store)
    {
        ArgumentException.ThrowIfNullOrEmpty(store);
        var fs = StoreFileSystem.Instance;
        return WalArchiveReport.List(fs, StoreView.Open(store, fs, trustCatalog: false));
    }

    /// <summary>
    /// The LSN a restore to <paramref name="utc"/> restores to: the highest LSN sampled durable at or before it on <paramref name="timeline"/>'s ancestry
    /// (null: the store's newest timeline). Never a commit durable after the instant.
    /// </summary>
    /// <param name="store">The store directory.</param>
    /// <param name="utc">The instant; its <see cref="DateTime.Kind"/> must be <see cref="DateTimeKind.Utc"/>.</param>
    /// <param name="timeline">The timeline; null for the newest.</param>
    /// <exception cref="RestoreTargetUnreachableException">No sample lies at or before the instant (<see cref="RestoreUnreachableReason.BeforeCoverage"/>).</exception>
    public static BackupTimeResolution ResolveTime(string store, DateTime utc, Guid? timeline = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(store);
        if (utc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The instant must be UTC (DateTimeKind.Utc).", nameof(utc));
        }

        var fs = StoreFileSystem.Instance;
        var view = StoreView.Open(store, fs, trustCatalog: false);
        var timelines = ArchiveRestorePlanner.LoadTimelines(fs, view.Paths.Root);
        var (lsn, sample, on) = ArchiveRestorePlanner.ResolveTime(fs, view.Paths.Root, timelines, timeline ?? ArchiveRestorePlanner.NewestTimeline(view, timelines),
            utc);
        return new BackupTimeResolution(lsn, new DateTime(sample.UtcTicks, DateTimeKind.Utc), on);
    }

    /// <summary>
    /// A range of a timeline's archive by time: the frames durable after <paramref name="fromUtc"/> and at or before <paramref name="toUtc"/>, as the
    /// time samples place them (<see cref="ResolveTime"/>'s rule at each end).
    /// </summary>
    /// <param name="store">The store directory.</param>
    /// <param name="timeline">The timeline; <see cref="Guid.Empty"/> for the newest.</param>
    /// <param name="fromUtc">The range's start (UTC).</param>
    /// <param name="toUtc">The range's end (UTC).</param>
    /// <param name="commits">Also list every frame of the range.</param>
    /// <param name="max">The most frames listed.</param>
    public static BackupArchiveLog ReadLog(string store, Guid timeline, DateTime fromUtc, DateTime toUtc, bool commits, int max)
    {
        ArgumentException.ThrowIfNullOrEmpty(store);
        if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The range's instants must be UTC (DateTimeKind.Utc).");
        }

        var fs = StoreFileSystem.Instance;
        var view = StoreView.Open(store, fs, trustCatalog: false);
        var timelines = ArchiveRestorePlanner.LoadTimelines(fs, view.Paths.Root);
        var t = timeline != Guid.Empty ? timeline : ArchiveRestorePlanner.NewestTimeline(view, timelines);
        var from = SampledAtOrBefore(fs, view, timelines, t, fromUtc) + 1;
        var to = SampledAtOrBefore(fs, view, timelines, t, toUtc);
        return WalArchiveReport.Log(fs, view, t, from, Math.Max(to, from - 1), commits, max);
    }

    /// <summary>A range of a timeline's archive by LSN, <c>[fromLsn, toLsn]</c>; with <paramref name="commits"/>, every frame ending inside it.</summary>
    /// <param name="store">The store directory.</param>
    /// <param name="timeline">The timeline; <see cref="Guid.Empty"/> for the newest.</param>
    /// <param name="fromLsn">The range's first LSN.</param>
    /// <param name="toLsn">The range's last LSN.</param>
    /// <param name="commits">Also list every frame of the range.</param>
    /// <param name="max">The most frames listed.</param>
    public static BackupArchiveLog ReadLog(string store, Guid timeline, long fromLsn, long toLsn, bool commits, int max)
    {
        ArgumentException.ThrowIfNullOrEmpty(store);
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        var fs = StoreFileSystem.Instance;
        var view = StoreView.Open(store, fs, trustCatalog: false);
        var t = timeline != Guid.Empty ? timeline : ArchiveRestorePlanner.NewestTimeline(view, ArchiveRestorePlanner.LoadTimelines(fs, view.Paths.Root));
        return WalArchiveReport.Log(fs, view, t, fromLsn, toLsn, commits, max);
    }

    private static long SampledAtOrBefore(IStoreFileSystem fs, StoreView view, Dictionary<Guid, ArchivedTimeline> timelines, Guid t, DateTime utc)
    {
        try
        {
            return ArchiveRestorePlanner.ResolveTime(fs, view.Paths.Root, timelines, t, utc).Lsn;
        }
        catch (RestoreTargetUnreachableException e) when (e.Reason == RestoreUnreachableReason.BeforeCoverage)
        {
            return 0;
        }
    }
}
