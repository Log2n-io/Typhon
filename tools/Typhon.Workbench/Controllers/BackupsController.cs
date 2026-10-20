using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Backups;
using Typhon.Workbench.Dtos.Backups;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// REST surface of the Backups view (#1417): a store's health, points, archive and plans, and the jobs that read or write it.
/// </summary>
/// <remarks>
/// <para><b>Path-scoped, like <see cref="IntegrityController"/>, not session-scoped.</b> A store is useful with no session at all — restoring a database that
/// will not open is the case that most justifies the view — so paths travel in the body and no <c>[RequireSession]</c> applies. The sessions matter only
/// where they hold a database: the route "Back up now" takes (WB-4) and the restore target guard (WB-12).</para>
/// <para>Reads that touch headers and footers only (status, list, archive, plans) answer on the request. Everything that reads a chain, writes a pack or
/// opens an engine is a job (<see cref="BackupJobRegistry"/>): cancellable, one per store, kept after it ends, progress over SSE.</para>
/// <para>Errors are <see cref="ProblemDetails"/> whose <c>title</c> is the category <c>typhon backup</c>'s exit code follows (<c>refused</c>,
/// <c>store_damaged</c>, <c>failed</c>, <c>plan_changed</c>, <c>busy</c>) and whose <c>detail</c> is the engine's own sentence.</para>
/// </remarks>
[ApiController]
[Route("api/backups")]
[Tags("Backups")]
[RequireBootstrapToken]
public sealed partial class BackupsController : ControllerBase
{
    private readonly IBackupOperations _ops;
    private readonly BackupJobRegistry _jobs;
    private readonly BackupPlanCache _plans;
    private readonly SessionManager _sessions;
    private readonly ILogger<BackupsController> _logger;

    public BackupsController(IBackupOperations ops, BackupJobRegistry jobs, BackupPlanCache plans, SessionManager sessions,
        ILogger<BackupsController> logger)
    {
        _ops = ops;
        _jobs = jobs;
        _plans = plans;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>The health card: the store's verdict from its packs, the bundle's engine status, and the route "Back up now" would take.</summary>
    [HttpPost("status")]
    public ActionResult<BackupStatusDto> Status([FromBody] BackupStatusRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Store))
        {
            return Problem400("A store path is required.");
        }

        var store = BackupPaths.Full(request.Store);
        var bundle = string.IsNullOrWhiteSpace(request.Bundle) ? null : BackupPaths.Full(request.Bundle);
        BackupStoreStatusDto storeStatus = null;
        string storeError = null;
        try
        {
            storeStatus = BackupDtoMapper.Map(_ops.Status(store));
        }
        catch (Exception e) when (IsStoreReadFailure(e))
        {
            storeError = Directory.Exists(store) ? e.Message : $"'{store}' is not a store yet: the first backup into it creates it.";
        }

        BackupEngineStatusDto engine = null;
        if (bundle != null)
        {
            try
            {
                var report = _ops.ReadStatus(bundle);
                if (report.Status != null)
                {
                    engine = BackupDtoMapper.Map(report.Status, "statusFile", report.EngineLive, report.Pid, report.Machine, report.StorePath,
                        report.AcceptsRequests, report.Reason);
                }
            }
            catch (Exception e) when (IsStoreReadFailure(e))
            {
                // No status to show: the card falls back to the store's own verdict.
            }
        }

        var route = BackupRouting.Decide(bundle, store, _sessions, _ops);
        return Ok(new BackupStatusDto(store, bundle, storeStatus, storeError, engine, new BackupRouteDto(route.Kind, route.Reason, route.Session?.Id),
            _jobs.Running(store)?.Id, DefaultScratch()));
    }

    /// <summary>What the store holds (<c>typhon backup list</c>): points, chains, unreadable packs, temps, drills; restorable points when asked.</summary>
    [HttpPost("list")]
    public ActionResult<BackupListingDto> List([FromBody] BackupListRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Store))
        {
            return Problem400("A store path is required.");
        }

        return Guarded(() => BackupDtoMapper.Map(_ops.List(BackupPaths.Full(request.Store), request.Restorable)));
    }

    /// <summary>The store's WAL archive: timelines, segments, gaps, time samples.</summary>
    [HttpPost("archive")]
    public ActionResult<BackupArchiveDto> Archive([FromBody] BackupStoreRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Store))
        {
            return Problem400("A store path is required.");
        }

        return Guarded(() => BackupDtoMapper.Map(_ops.ListArchive(BackupPaths.Full(request.Store))));
    }

    /// <summary>
    /// A restore's preview: every refusal before anything is written, the chain and bytes it reads, the LSN it restores to, what is lost after it, and a
    /// target one of this Workbench's sessions holds (WB-12). Its fingerprint is what the restore job runs under.
    /// </summary>
    [HttpPost("restore/plan")]
    public ActionResult<BackupRestorePlanDto> PlanRestore([FromBody] BackupRestoreRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Store))
        {
            return Problem400("A store path is required.");
        }

        if (!TryParsePoint(request.Point, archiveTargets: true, out var point, out var error))
        {
            return Problem400(error);
        }

        return Guarded(() => Plan(BackupPaths.Full(request.Store), point, request));
    }

    /// <summary>A retention's preview, mandatory before its apply: kept, compacted and removed points, the packs deleted, and the plan's fingerprint.</summary>
    [HttpPost("retention/plan")]
    public ActionResult<BackupRetentionPlanDto> PlanRetention([FromBody] BackupRetentionPlanRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Store) || request.Policy == null)
        {
            return Problem400("A store path and a policy are required.");
        }

        var policy = ToPolicy(request.Policy);
        if (!policy.Validate(out var invalid))
        {
            return Problem400(invalid);
        }

        var store = BackupPaths.Full(request.Store);
        return Guarded(() =>
        {
            var plan = _ops.PlanRetention(store, policy);
            _plans.Add(store, policy, plan);
            return BackupDtoMapper.Map(plan);
        });
    }

    /// <summary>The LSN a restore to an instant reaches, and the sample that decides it (never a commit durable after the instant).</summary>
    [HttpPost("archive/resolve-time")]
    public ActionResult<BackupTimeResolutionDto> ResolveTime([FromBody] BackupResolveTimeRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Store))
        {
            return Problem400("A store path is required.");
        }

        if (!TryParseUtc(request.TimeUtc, out var utc, out var error))
        {
            return Problem400(error);
        }

        if (!TryParseTimeline(request.TimelineId, out var timeline))
        {
            return Problem400($"Unknown timeline '{request.TimelineId}'.");
        }

        return Guarded(() =>
        {
            var r = _ops.ResolveTime(BackupPaths.Full(request.Store), utc, timeline);
            return new BackupTimeResolutionDto(r.Lsn, DateTime.SpecifyKind(r.SampleUtc, DateTimeKind.Utc), BackupDtoMapper.Id(r.TimelineId));
        });
    }

    /// <summary>The commits of a timeline between two instants, to pick an LSN from.</summary>
    [HttpPost("archive/log")]
    public ActionResult<BackupLogDto> Log([FromBody] BackupLogRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Store))
        {
            return Problem400("A store path is required.");
        }

        if (!TryParseUtc(request.FromUtc, out var from, out var error) || !TryParseUtc(request.ToUtc, out var to, out error))
        {
            return Problem400(error);
        }

        if (!TryParseTimeline(request.TimelineId, out var timeline))
        {
            return Problem400($"Unknown timeline '{request.TimelineId}'.");
        }

        var max = request.Max is > 0 and <= 10_000 ? request.Max : 500;
        return Guarded(() => BackupDtoMapper.Map(_ops.ReadLog(BackupPaths.Full(request.Store), timeline ?? Guid.Empty, from, to, max)));
    }

    private BackupRestorePlanDto Plan(string store, PointSelector point, BackupRestoreRequestDto request)
    {
        var target = string.IsNullOrWhiteSpace(request.Target) ? null : BackupPaths.Full(request.Target);
        var plan = _ops.PlanRestore(store, point, target, new BackupRestoreOptions { Replace = request.Replace, AllowFormatMismatch = request.AllowFormatMismatch });

        // WB-12: a target held by a session of this Workbench, open or paused — a paused one's watcher would reopen it mid-restore.
        string refusal = plan.Refusal?.ToString();
        var message = plan.RefusalMessage;
        Guid? heldBy = null;
        if (plan.ResolvedTarget != null && BackupRouting.SessionOn(_sessions, plan.ResolvedTarget) is { } holder)
        {
            heldBy = holder.Id;
            refusal ??= "HeldBySession";
            message ??= $"'{plan.ResolvedTarget}' is open in this Workbench. Close that session to restore over it.";
        }

        // What is lost after the restored LSN: commits up to the newest LSN the store knows on that timeline (its newest point's P, or the archive's end).
        var newest = NewestKnownLsn(store, plan.TimelineId);
        var lost = plan.CanRestore && newest > plan.PointLsn ? newest - plan.PointLsn : 0;
        return new BackupRestorePlanDto(plan.CanRestore && heldBy == null, plan.Fingerprint, plan.ResolvedTarget, plan.DisplacedBundle, [.. plan.Chain],
            plan.BytesToRead, plan.BaseBackupId, plan.CheckpointLsn, plan.PointLsn, plan.TargetLsn, BackupDtoMapper.Id(plan.TimelineId),
            plan.ArchivedSegments, plan.ArchivedBytes, plan.SampleLsn, plan.SampleUtc is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null, refusal,
            plan.Unreachable?.ToString(), message, [.. plan.NearestRestorable], heldBy, newest, lost);
    }

    private long NewestKnownLsn(string store, Guid timeline)
    {
        long newest = 0;
        try
        {
            foreach (var p in _ops.List(store, false).Points)
            {
                if (timeline == Guid.Empty || p.TimelineId == timeline)
                {
                    newest = Math.Max(newest, p.PointLsn);
                }
            }

            foreach (var t in _ops.ListArchive(store).Timelines)
            {
                if (t.TimelineId == timeline)
                {
                    newest = Math.Max(newest, t.ArchivedThroughLsn);
                }
            }
        }
        catch (Exception e) when (IsStoreReadFailure(e))
        {
            // The plan stands; only the "lost after" figure is missing.
        }

        return newest;
    }

    /// <summary>
    /// The drill scratch directory when the operator names none (WB-21): beside the Workbench's other per-user state (the fixtures root convention),
    /// never inside a store. A drill needs the space of the whole image there.
    /// </summary>
    internal static string DefaultScratch()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Typhon", "Workbench", "DrillScratch");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(xdg))
        {
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(xdg, "typhon", "workbench", "drill-scratch");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool IsStoreReadFailure(Exception e) => e is IOException or UnauthorizedAccessException or BackupException or ArgumentException;

    /// <summary>Runs a read; what it throws becomes the categorized problem the CLI would report for it.</summary>
    private ActionResult<T> Guarded<T>(Func<T> read)
    {
        try
        {
            return Ok(read());
        }
        catch (Exception e) when (e is DirectoryNotFoundException or FileNotFoundException)
        {
            return Problem(e.Message, statusCode: StatusCodes.Status404NotFound, title: "failed");
        }
        catch (ArgumentException e)
        {
            return Problem400(e.Message);
        }
        catch (Exception e) when (BackupOperations.Classify(e) != BackupFailureKind.Defect)
        {
            var category = BackupDtoMapper.Category(e);
            return Problem(e.Message, statusCode: category == "failed" ? StatusCodes.Status500InternalServerError : StatusCodes.Status409Conflict,
                title: category);
        }
    }

    private ObjectResult Problem400(string detail) => Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: "invalid_request");

    private ObjectResult Problem409(string category, string detail) => Problem(detail, statusCode: StatusCodes.Status409Conflict, title: category);

    internal static BackupRetentionPolicy ToPolicy(BackupRetentionPolicyDto p) => new()
    {
        MaxAge = TimeSpan.FromSeconds(Math.Max(0, p.MaxAgeSeconds)),
        MaxCount = p.MaxCount,
        MaxTotalBytes = p.MaxTotalBytes,
        MinKeep = p.MinKeep,
        CompactThreshold = p.CompactThreshold,
        KeepLastDrilled = p.KeepLastDrilled,
    };

    /// <summary>
    /// <c>latest</c> (also for a null reference), <c>id</c>, and — only where a restore takes them — <c>lsn</c> and <c>time</c> of an archived timeline.
    /// </summary>
    internal static bool TryParsePoint(BackupPointRefDto p, bool archiveTargets, out PointSelector point, out string error)
    {
        point = PointSelector.Latest;
        error = null;
        var kind = string.IsNullOrWhiteSpace(p?.Kind) ? "latest" : p.Kind.Trim().ToLowerInvariant();
        switch (kind)
        {
            case "latest":
                return true;
            case "id" when p.Id is > 0:
                point = PointSelector.ById(p.Id.Value);
                return true;
            case "lsn" or "time" when !archiveTargets:
                error = "Only a restore takes an LSN or a time; verify, drill and compact take a backup point.";
                return false;
            case "lsn" when p.Lsn is > 0:
                if (!TryParseTimeline(p.TimelineId, out var lt))
                {
                    error = $"Unknown timeline '{p.TimelineId}'.";
                    return false;
                }

                point = PointSelector.AtLsn(p.Lsn.Value, lt);
                return true;
            case "time":
                if (!TryParseUtc(p.TimeUtc, out var utc, out error))
                {
                    return false;
                }

                if (!TryParseTimeline(p.TimelineId, out var tt))
                {
                    error = $"Unknown timeline '{p.TimelineId}'.";
                    return false;
                }

                point = PointSelector.AtTime(utc, tt);
                return true;
            default:
                error = $"Unknown point '{p?.Kind}': expected latest, id (with an id), lsn (with an LSN) or time.";
                return false;
        }
    }

    /// <summary>An ISO 8601 instant with its offset, to UTC: an instant without one would let the server's time zone decide which commits are kept.</summary>
    internal static bool TryParseUtc(string value, out DateTime utc, out string error)
    {
        utc = default;
        var text = value?.Trim() ?? "";
        var hasOffset = text.EndsWith('Z') || text.EndsWith('z') || (text.Length > 6 && text[^6] is '+' or '-' && text[^3] == ':');
        if (!hasOffset || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            error = $"'{value}' is not an ISO 8601 instant with its offset (e.g. 2026-10-21T14:03:00Z).";
            return false;
        }

        utc = parsed.UtcDateTime;
        error = null;
        return true;
    }

    private static bool TryParseTimeline(string value, out Guid? timeline)
    {
        timeline = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Guid.TryParse(value.Trim(), out var g))
        {
            timeline = g;
            return true;
        }

        return false;
    }
}
