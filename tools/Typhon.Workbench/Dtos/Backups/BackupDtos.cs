namespace Typhon.Workbench.Dtos.Backups;

// The Backups view's wire contract (#1417). Engine enums travel as their names (ToString), never as numbers: the client maps the names it knows and
// renders an unknown one as itself, so an engine that grows a value never shows a wrong word. DateTime.MinValue ("none" in the engine) travels as null.

// ── Requests ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Which point: <c>latest</c>, <c>id</c> (with <see cref="Id"/>), or for a restore <c>lsn</c> / <c>time</c> of an archived timeline.</summary>
/// <param name="Kind">latest | id | lsn | time.</param>
/// <param name="Id">The backup id, for <c>id</c>.</param>
/// <param name="Lsn">The LSN, for <c>lsn</c>.</param>
/// <param name="TimeUtc">An ISO 8601 instant with its offset, for <c>time</c>.</param>
/// <param name="TimelineId">The timeline of an <c>lsn</c> or <c>time</c> target; null for the newest.</param>
public sealed record BackupPointRefDto(string Kind, uint? Id, long? Lsn, string TimeUtc, string TimelineId);

/// <summary>A store, and optionally the database bundle it backs up (the route, the engine's status).</summary>
public sealed record BackupStatusRequestDto(string Store, string Bundle);

/// <summary>A store's listing; <see cref="Restorable"/> also runs a quick verify to mark the restorable points.</summary>
public sealed record BackupListRequestDto(string Store, bool Restorable);

/// <summary>A store.</summary>
public sealed record BackupStoreRequestDto(string Store);

/// <summary>A restore, previewed or run. <see cref="Bundle"/> names the database whose open session's schema settles the restored bundle, if any.</summary>
/// <param name="Store">The store.</param>
/// <param name="Point">Which point, LSN or time.</param>
/// <param name="Target">The <c>{name}.typhon</c> bundle path, or the directory to put it in.</param>
/// <param name="Replace">Move an existing target aside instead of refusing.</param>
/// <param name="AllowDamaged">Forensics: write pages that fail their check.</param>
/// <param name="AllowFormatMismatch">Restore an image of another on-disk format revision.</param>
/// <param name="Bundle">The database the panel shows (its session's schema settles the restore); null for none.</param>
/// <param name="PlanFingerprint">The reviewed plan's fingerprint; required to run.</param>
public sealed record BackupRestoreRequestDto(string Store, BackupPointRefDto Point, string Target, bool Replace, bool AllowDamaged,
    bool AllowFormatMismatch, string Bundle, string PlanFingerprint);

/// <summary>A retention policy: every field is the operator's (none is defaulted by the Workbench); 0 means "no limit" for the three limits.</summary>
public sealed record BackupRetentionPolicyDto(long MaxAgeSeconds, int MaxCount, long MaxTotalBytes, int MinKeep, int CompactThreshold,
    bool KeepLastDrilled);

/// <summary>A retention preview.</summary>
public sealed record BackupRetentionPlanRequestDto(string Store, BackupRetentionPolicyDto Policy);

/// <summary>A retention apply: the fingerprint of the plan the operator reviewed (the server applies that plan's own policy).</summary>
public sealed record BackupRetentionApplyRequestDto(string Store, string ApprovedFingerprint);

/// <summary>Back up now.</summary>
public sealed record BackupCreateRequestDto(string Store, string Bundle, bool Full);

/// <summary>Verify the store (<see cref="Point"/> null) or one point, at <c>quick</c>, <c>full</c> or <c>deep</c>.</summary>
public sealed record BackupVerifyRequestDto(string Store, BackupPointRefDto Point, string Level);

/// <summary>
/// Drill a point into <see cref="Scratch"/> (blank: the Workbench's default); <see cref="Bundle"/> names the database whose open session's schema the
/// drill loads, if any.
/// </summary>
public sealed record BackupDrillRequestDto(string Store, BackupPointRefDto Point, string Scratch, string Bundle);

/// <summary>Compact a point into a base.</summary>
public sealed record BackupCompactRequestDto(string Store, BackupPointRefDto Point);

/// <summary>The LSN a restore to <see cref="TimeUtc"/> reaches.</summary>
public sealed record BackupResolveTimeRequestDto(string Store, string TimeUtc, string TimelineId);

/// <summary>The commits of a timeline between two instants.</summary>
public sealed record BackupLogRequestDto(string Store, string TimelineId, string FromUtc, string ToUtc, int Max);

// ── Store facts ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One point, as its pack describes it (<c>BackupPointSummary</c>).</summary>
public sealed record BackupPointDto(uint BackupId, string Kind, string ChainId, uint ParentId, uint BaseId, long CheckpointLsn, long PointLsn,
    int ImagePages, int PageCount, int TailSegments, bool Suspect, bool Offline, DateTime? FreezeUtc, DateTime? TailEndUtc, string TimelineId,
    int FormatRevision, string PackFile, long PackBytes);

/// <summary>A chain's head.</summary>
public sealed record BackupChainDto(string ChainId, uint BaseId, uint HeadBackupId, int Depth, int HeadDepth);

/// <summary>A point's newest drill (a drill that could not run is not one).</summary>
public sealed record BackupDrillSummaryDto(uint BackupId, DateTime? Utc, string Outcome, string Error);

/// <summary>A pack whose header or footer cannot be read.</summary>
public sealed record BackupUnreadablePackDto(string File, string Reason);

/// <summary>What a store holds.</summary>
/// <param name="RestorablePoints">The restorable points by a quick verify; null when not asked for.</param>
public sealed record BackupListingDto(string StoreId, string DatabaseId, List<BackupPointDto> Points, List<BackupChainDto> Chains,
    List<BackupUnreadablePackDto> UnreadablePacks, List<string> TemporaryFiles, List<uint> RestorablePoints, List<BackupDrillSummaryDto> Drills);

/// <summary>A WAL archive gap.</summary>
public sealed record BackupArchiveGapDto(long FromLsn, long ToLsn, DateTime? RecordedUtc, string Reason, bool Covered);

/// <summary>What a timeline's archive makes restorable.</summary>
public sealed record BackupArchiveCoverageDto(string TimelineId, long FromLsn, long ThroughLsn, DateTime? LastArchivedUtc,
    List<BackupArchiveGapDto> UncoveredGaps);

/// <summary>A store's verdict from its packs (<c>typhon backup status --store</c>).</summary>
public sealed record BackupStoreStatusDto(string Health, string Reason, int Points, BackupPointDto Latest, BackupPointDto LatestRestorable,
    List<BackupArchiveCoverageDto> Archive);

/// <summary>A capture's failure.</summary>
public sealed record BackupFailureDto(DateTime? Utc, string Kind, string Message);

/// <summary>The engine's WAL archiver.</summary>
public sealed record BackupArchiveStatusDto(string State, string TimelineId, long ArchivedThroughLsn, long LagBytes, long BacklogBytes,
    double LagSeconds, long GapsTotal, long LastGapFromLsn, long LastGapToLsn, DateTime? LastArchivedSealUtc, int ConsecutiveFailures,
    string LastError);

/// <summary>
/// An engine's backup status: the open session's own engine (<c>session</c>), or the bundle's <c>backup.status.json</c> as
/// <c>BackupOperations.ReadStatus</c> re-evaluates it (<c>statusFile</c>).
/// </summary>
public sealed record BackupEngineStatusDto(string Source, bool EngineLive, int Pid, string Machine, string StorePath, bool AcceptsRequests,
    string Health, string HealthReason, string ReasonText, string State, string Phase, long ProgressPages, long ProgressOfPages, long ProgressBytes,
    BackupPointDto LastPoint, DateTime? LastSuccessUtc, DateTime? NextDueUtc, int ConsecutiveFailures, BackupFailureDto LastFailure, int ChainDepth,
    long StoreBytes, int RestorablePoints, BackupDrillSummaryDto LastDrill, BackupDrillSummaryDto LastPassedDrill, string RetentionBlocked,
    BackupArchiveStatusDto Archive, string LastRequestId, DateTime? UpdatedUtc);

/// <summary>
/// How "Back up now" reaches this bundle: <c>inProcess</c> (this Workbench's session holds it), <c>request</c> (another live engine does, and accepts
/// requests), <c>offline</c> (nobody does), or <c>unavailable</c> with the reason.
/// </summary>
public sealed record BackupRouteDto(string Kind, string Reason, Guid? SessionId);

/// <summary>The health card's facts.</summary>
/// <param name="Store">The store, as given.</param>
/// <param name="Bundle">The bundle, as given (null in store-only use).</param>
/// <param name="StoreStatus">The store's own verdict; null when it is not a store (yet).</param>
/// <param name="StoreError">Why the store could not be read; null when it was.</param>
/// <param name="Engine">The engine's status; null when none is known.</param>
/// <param name="Route">How "Back up now" would run.</param>
/// <param name="RunningJobId">The store's running job, if any.</param>
/// <param name="DefaultScratch">The drill scratch directory used when none is given (WB-21).</param>
public sealed record BackupStatusDto(string Store, string Bundle, BackupStoreStatusDto StoreStatus, string StoreError, BackupEngineStatusDto Engine,
    BackupRouteDto Route, Guid? RunningJobId, string DefaultScratch);

/// <summary>One archived WAL segment.</summary>
public sealed record BackupArchivedSegmentDto(long SegmentId, long FirstLsn, long LastLsn, DateTime? SealedUtc, DateTime? ArchivedUtc, long Bytes,
    string Flags, bool SessionStart);

/// <summary>A time sample: this LSN was durable at this instant.</summary>
public sealed record BackupTimeSampleDto(long Lsn, DateTime Utc);

/// <summary>One timeline of the archive.</summary>
public sealed record BackupArchiveTimelineDto(string TimelineId, string ParentTimelineId, long OriginLsn, string Reason, long ArchivedThroughLsn,
    List<BackupArchivedSegmentDto> Segments, List<BackupArchiveGapDto> Gaps, List<BackupTimeSampleDto> Samples, List<BackupUnreadablePackDto> Unreadable);

/// <summary>The store's WAL archive.</summary>
public sealed record BackupArchiveDto(List<BackupArchiveTimelineDto> Timelines);

/// <summary>What a time resolves to.</summary>
public sealed record BackupTimeResolutionDto(long Lsn, DateTime SampleUtc, string TimelineId);

/// <summary>One WAL frame of the archive.</summary>
public sealed record BackupLogFrameDto(long FirstLsn, long LastLsn, long Tsn, string Kind, int Records, DateTime? ApproxUtc, long SegmentId);

/// <summary>A range of the archive: its frames, newest last.</summary>
public sealed record BackupLogDto(string TimelineId, long FromLsn, long ToLsn, List<BackupLogFrameDto> Frames, bool Truncated);

// ── Plans ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A restore's preview (<c>BackupOperations.PlanRestore</c>) plus what the Workbench adds: a target one of its sessions holds (WB-12), and what is lost
/// after the restored LSN.
/// </summary>
/// <param name="CanRestore">No refusal: the restore may run with <see cref="Fingerprint"/>.</param>
/// <param name="Refusal">The refusal's reason (the engine's <c>RestoreRefusalReason</c>, or <c>HeldBySession</c>); null when none.</param>
/// <param name="Unreachable">An LSN or time target's <c>RestoreUnreachableReason</c>; null when reachable.</param>
/// <param name="RefusalMessage">The refusal in words.</param>
/// <param name="HeldBySessionId">The Workbench session that holds the target (WB-12), to offer its close.</param>
/// <param name="NewestKnownLsn">
/// The newest LSN the store knows on the restored timeline (its newest point's <c>P</c>, or the archive's end); null when the store could not be read
/// for it (what is lost is then unknown, never "nothing").
/// </param>
/// <param name="LostLsns">Of those, how many LSNs lie after the restored one: what the restored database will not hold; null when unknown.</param>
public sealed record BackupRestorePlanDto(bool CanRestore, string Fingerprint, string ResolvedTarget, string DisplacedBundle, List<uint> Chain,
    long BytesToRead, uint BaseBackupId, long CheckpointLsn, long PointLsn, long TargetLsn, string TimelineId, int ArchivedSegments, long ArchivedBytes,
    long SampleLsn, DateTime? SampleUtc, string Refusal, string Unreachable, string RefusalMessage, List<long> NearestRestorable,
    Guid? HeldBySessionId, long? NewestKnownLsn, long? LostLsns);

/// <summary>A retention's preview; the apply runs only with its <see cref="Fingerprint"/>.</summary>
public sealed record BackupRetentionPlanDto(List<uint> Kept, List<uint> Removed, List<uint> Compactions, List<string> PackDeletions, uint CutoffId,
    long BytesBefore, long BytesAfterAtMost, string Blocked, string BlockedReason, string Fingerprint);

// ── Jobs ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A job's failure: the category the CLI's exit code follows (<c>refused</c>, <c>store_damaged</c>, <c>failed</c>, <c>plan_changed</c>).</summary>
public sealed record BackupJobErrorDto(string Category, string Message);

/// <summary>Back up now's receipt.</summary>
public sealed record BackupCreateResultDto(BackupPointDto Point, Guid? RequestId);

/// <summary>One verify finding.</summary>
public sealed record BackupVerifyFindingDto(string Code, string Severity, string File, long PageId, string Message);

/// <summary>One pack, verified.</summary>
public sealed record BackupPackVerificationDto(string File, uint BackupId, string Status, List<BackupVerifyFindingDto> Findings);

/// <summary>One point, verified.</summary>
public sealed record BackupPointVerificationDto(uint BackupId, bool Restorable, List<uint> BrokenBy);

/// <summary>A verify's report (<c>typhon backup verify</c>'s facts).</summary>
public sealed record BackupVerifyReportDto(string Level, string Verdict, List<BackupPackVerificationDto> Packs, List<BackupPointVerificationDto> Points,
    List<BackupVerifyFindingDto> StoreFindings, double DurationMs, long BytesRead, int RemovedPoints);

/// <summary>What a recovery replayed.</summary>
public sealed record BackupRecoveryDto(long RecordsReplayed, int UowsPromoted, int UowsVoided);

/// <summary>A drill's report.</summary>
public sealed record BackupDrillReportDto(BackupPointDto Point, string Outcome, string FailedStage, string Error, double RestoreMs, double OpenMs,
    double CheckMs, BackupRecoveryDto Recovery, string Verdict, int Findings, int SuspectPages, string RecordFile, bool NeedsSchema);

/// <summary>A compaction's result.</summary>
public sealed record BackupCompactionResultDto(BackupPointDto Point, bool AlreadyBase, int ChainDepth, int Pages, int HolePages, long BytesRead,
    long BytesWritten, double DurationMs);

/// <summary>A retention's result.</summary>
public sealed record BackupRetentionResultDto(BackupRetentionPlanDto Plan, List<BackupCompactionResultDto> Compactions, List<string> Deleted,
    long BytesFreed, double DurationMs);

/// <summary>What the settle after a restore did: the open (recovery) and close that make the restored bundle ready.</summary>
/// <param name="Ran">A settle was attempted.</param>
/// <param name="CrashRecoveryRan">Its open took the crash path (WAL replayed).</param>
/// <param name="Recovery">What it replayed.</param>
/// <param name="DurationMs">Open to close.</param>
/// <param name="Error">Why it failed, or null.</param>
/// <param name="NeedsSchema">It failed only because this process does not register the application's archetypes (7012): not damage.</param>
/// <param name="WithSchema">It loaded the open session's schema assemblies.</param>
public sealed record BackupSettleDto(bool Ran, bool CrashRecoveryRan, BackupRecoveryDto Recovery, double DurationMs, string Error, bool NeedsSchema,
    bool WithSchema);

/// <summary>A restore's result.</summary>
public sealed record BackupRestoreResultDto(BackupPointDto Point, string Target, string DisplacedBundle, int ChainDepth, int PagesWritten, int ZeroPages,
    int TailSegments, long BytesRead, long BytesWritten, double DurationMs, List<uint> SuspectPages, List<BackupDamagedPageDto> DamagedPages,
    string NewTimelineId, long TargetLsn, long EffectiveLsn, string TimelineId, int ArchivedSegments, bool ForcedCrashRecovery, BackupSettleDto Settle);

/// <summary>A page a forensic restore wrote despite its check.</summary>
public sealed record BackupDamagedPageDto(long PageId, string PackFile, string Reason);

/// <summary>
/// A backup job: queued → running(phase, completed, total) → done(result) | error(category, message) | cancelled. <see cref="Total"/> 0 means
/// indeterminate: no percentage is shown. One result slot per kind, filled in a job's snapshot (<c>GET jobs/{id}</c>, the stream) and null in the store's
/// job list (<c>GET jobs</c>: summaries).
/// </summary>
public sealed record BackupJobDto(Guid JobId, string Kind, string Store, string Bundle, string State, string Phase, long Completed, long Total,
    string Unit, DateTime StartedUtc, DateTime? EndedUtc, string Route, string Note, BackupJobErrorDto Error, BackupCreateResultDto Create,
    BackupVerifyReportDto Verify, BackupDrillReportDto Drill, BackupCompactionResultDto Compact, BackupRetentionResultDto Retention,
    BackupRestoreResultDto Restore);

/// <summary>A start refused because the store already runs a job: attach to it.</summary>
public sealed record BackupBusyDto(string Error, Guid RunningJobId);
