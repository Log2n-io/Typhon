using Typhon.Workbench.Dtos.Backups;

namespace Typhon.Workbench.Backups;

/// <summary>Engine results → wire DTOs (#1417). Names for enums, null for the engine's "none" instants and ids; nothing derived here.</summary>
internal static class BackupDtoMapper
{
    public static DateTime? Instant(DateTime utc) => utc == DateTime.MinValue ? null : DateTime.SpecifyKind(utc, DateTimeKind.Utc);

    public static string Id(Guid id) => id == Guid.Empty ? null : id.ToString();

    public static BackupPointDto Map(BackupPointSummary p) =>
        p == null
            ? null
            : new BackupPointDto(p.BackupId, p.Kind.ToString(), Id(p.ChainId), p.ParentId, p.BaseId, p.CheckpointLsn, p.PointLsn, p.ImagePages,
                p.PageCount, p.TailSegments, p.Suspect, p.Offline, Instant(p.FreezeUtc), Instant(p.TailEndUtc), Id(p.TimelineId), p.FormatRevision,
                p.PackFile, p.PackBytes);

    public static BackupDrillSummaryDto Map(BackupDrillSummary d) =>
        d == null ? null : new BackupDrillSummaryDto(d.BackupId, Instant(d.Utc), d.Outcome.ToString(), d.Error);

    public static BackupListingDto Map(BackupStoreListing l)
    {
        var points = new List<BackupPointDto>(l.Points.Count);
        foreach (var p in l.Points)
        {
            points.Add(Map(p));
        }

        var chains = new List<BackupChainDto>(l.Chains.Count);
        foreach (var c in l.Chains)
        {
            chains.Add(new BackupChainDto(Id(c.ChainId), c.BaseId, c.HeadBackupId, c.Depth, c.HeadDepth));
        }

        var unreadable = new List<BackupUnreadablePackDto>(l.UnreadablePacks.Count);
        foreach (var u in l.UnreadablePacks)
        {
            unreadable.Add(new BackupUnreadablePackDto(u.Key, u.Value));
        }

        var drills = new List<BackupDrillSummaryDto>();
        if (l.Drills != null)
        {
            foreach (var d in l.Drills.Values)
            {
                drills.Add(Map(d));
            }

            drills.Sort(static (a, b) => a.BackupId.CompareTo(b.BackupId));
        }

        return new BackupListingDto(Id(l.StoreId), Id(l.DatabaseId), points, chains, unreadable, [.. l.TemporaryFiles],
            l.RestorablePoints == null ? null : [.. l.RestorablePoints], drills);
    }

    public static BackupArchiveGapDto Map(BackupArchiveGap g) => new(g.FromLsn, g.ToLsn, Instant(g.RecordedUtc), g.Reason.ToString(), g.Covered);

    public static BackupStoreStatusDto Map(BackupStoreStatus s)
    {
        if (s == null)
        {
            return null;
        }

        var coverage = new List<BackupArchiveCoverageDto>(s.Archive?.Count ?? 0);
        if (s.Archive != null)
        {
            foreach (var c in s.Archive)
            {
                coverage.Add(new BackupArchiveCoverageDto(Id(c.TimelineId), c.FromLsn, c.ThroughLsn, c.LastArchivedUtc is { } u ? Instant(u) : null,
                    c.UncoveredGaps.Select(Map).ToList()));
            }
        }

        return new BackupStoreStatusDto(s.Health.ToString(), s.Reason, s.Points, Map(s.Latest), Map(s.LatestRestorable), coverage);
    }

    public static BackupEngineStatusDto Map(BackupStatus s, string source, bool live, int pid, string machine, string storePath, bool acceptsRequests,
        string reasonText)
    {
        var a = s.Archive ?? BackupArchiveStatus.Off;
        return new BackupEngineStatusDto(source, live, pid, machine, storePath, acceptsRequests, s.Health.ToString(), s.Reason.ToString(),
            reasonText ?? s.ReasonText, s.State.ToString(), s.Phase.ToString(), s.ProgressPages, s.ProgressOfPages, s.ProgressBytes, Map(s.LastPoint),
            Instant(s.LastSuccessUtc), Instant(s.NextDueUtc), s.ConsecutiveFailures,
            s.LastFailure == null ? null : new BackupFailureDto(Instant(s.LastFailure.Utc), s.LastFailure.Kind, s.LastFailure.Message), s.ChainDepth,
            s.StoreBytes, s.RestorablePoints, Map(s.LastDrill), Map(s.LastPassedDrill), s.RetentionBlocked.ToString(),
            new BackupArchiveStatusDto(a.State.ToString(), Id(a.TimelineId), a.ArchivedThroughLsn, a.LagBytes, a.BacklogBytes, a.LagSeconds, a.GapsTotal,
                a.LastGapFromLsn, a.LastGapToLsn, Instant(a.LastArchivedSealUtc), a.ConsecutiveFailures, a.LastError),
            Id(s.LastRequestId), Instant(s.UpdatedUtc));
    }

    public static BackupArchiveDto Map(BackupArchiveListing l)
    {
        var timelines = new List<BackupArchiveTimelineDto>(l.Timelines.Count);
        foreach (var t in l.Timelines)
        {
            var segments = new List<BackupArchivedSegmentDto>(t.Segments.Count);
            foreach (var s in t.Segments)
            {
                segments.Add(new BackupArchivedSegmentDto(s.SegmentId, s.FirstLsn, s.LastLsn, s.SealedUtc is { } su ? Instant(su) : null,
                    Instant(s.ArchivedUtc), s.Bytes, s.Flags.ToString(), s.SessionStart));
            }

            var samples = new List<BackupTimeSampleDto>(t.Samples.Count);
            foreach (var s in t.Samples)
            {
                samples.Add(new BackupTimeSampleDto(s.Lsn, DateTime.SpecifyKind(s.Utc, DateTimeKind.Utc)));
            }

            timelines.Add(new BackupArchiveTimelineDto(Id(t.TimelineId), t.ParentTimelineId is { } parent ? Id(parent) : null, t.OriginLsn,
                t.Reason.ToString(), t.ArchivedThroughLsn, segments, t.Gaps.Select(Map).ToList(), samples,
                t.Unreadable.Select(u => new BackupUnreadablePackDto(u.Key, u.Value)).ToList()));
        }

        return new BackupArchiveDto(timelines);
    }

    public static BackupLogDto Map(BackupArchiveLog l) =>
        new(Id(l.TimelineId), l.FromLsn, l.ToLsn,
            l.Frames.Select(f => new BackupLogFrameDto(f.FirstLsn, f.LastLsn, f.Tsn, f.Kind.ToString(), f.Records,
                f.ApproxUtc is { } u ? Instant(u) : null, f.SegmentId)).ToList(), l.Truncated);

    public static BackupRetentionPlanDto Map(BackupRetentionPlan p) =>
        p == null
            ? null
            : new BackupRetentionPlanDto([.. p.Kept], [.. p.Removed], [.. p.Compactions], [.. p.PackDeletions], p.CutoffId, p.BytesBefore,
                p.BytesAfterAtMost, p.Blocked.ToString(), p.BlockedReason, p.Fingerprint);

    public static BackupCompactionResultDto Map(BackupCompactionResult r) =>
        new(Map(r.Point), r.AlreadyBase, r.ChainDepth, r.Pages, r.HolePages, r.BytesRead, r.BytesWritten, r.Duration.TotalMilliseconds);

    public static BackupRetentionResultDto Map(BackupRetentionResult r) =>
        new(Map(r.Plan), r.Compactions.Select(Map).ToList(), [.. r.Deleted], r.BytesFreed, r.Duration.TotalMilliseconds);

    public static BackupVerifyReportDto Map(BackupVerifyReport r)
    {
        static BackupVerifyFindingDto F(BackupVerifyFinding f) => new(f.Code, f.Severity.ToString(), f.File, f.PageId, f.Message);
        return new BackupVerifyReportDto(r.Level.ToString(), r.Verdict.ToString(),
            r.Packs.Select(p => new BackupPackVerificationDto(p.File, p.BackupId, p.Status.ToString(), p.Findings.Select(F).ToList())).ToList(),
            r.Points.Select(p => new BackupPointVerificationDto(p.BackupId, p.Restorable, [.. p.BrokenBy])).ToList(),
            r.StoreFindings.Select(F).ToList(), r.Duration.TotalMilliseconds, r.BytesRead, r.RemovedPoints);
    }

    public static BackupRecoveryDto Map(WalRecoveryResult r) => new(r.RecordsReplayed, r.UowsPromoted, r.UowsVoided);

    public static BackupDrillReportDto Map(BackupDrillReport r, bool needsSchema) =>
        new(Map(r.Point), r.Outcome.ToString(), r.FailedStage.ToString(), r.Error, r.RestoreTime.TotalMilliseconds, r.OpenTime.TotalMilliseconds,
            r.CheckTime.TotalMilliseconds, Map(r.Recovery), r.Verdict.ToString(), r.Findings, r.SuspectPages, r.RecordFile, needsSchema);

    public static BackupRestoreResultDto Map(BackupRestoreResult r, BackupSettleDto settle) =>
        new(Map(r.Point), r.Target, r.DisplacedBundle, r.ChainDepth, r.PagesWritten, r.ZeroPages, r.TailSegments, r.BytesRead, r.BytesWritten,
            r.Duration.TotalMilliseconds, [.. r.SuspectPages],
            r.DamagedPages.Select(d => new BackupDamagedPageDto(d.PageId, d.PackFile, d.Reason)).ToList(), Id(r.NewTimelineId), r.TargetLsn,
            r.EffectiveLsn, Id(r.TimelineId), r.ArchivedSegments, r.ForcedCrashRecovery, settle);

    /// <summary>The WB-19 category of what an operation threw, as the wire names it.</summary>
    public static string Category(Exception e) => BackupOperations.Classify(e) switch
    {
        BackupFailureKind.Refused => "refused",
        BackupFailureKind.StoreDamaged => "store_damaged",
        BackupFailureKind.PlanChanged => "plan_changed",
        _ => "failed",
    };
}
