using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Backups;
using Typhon.Workbench.Dtos.Backups;

namespace Typhon.Workbench.Controllers;

/// <summary>The Backups view's jobs (#1417 §3.2, §3.5): started by a POST, followed over SSE or by snapshot, cancelled by DELETE.</summary>
public sealed partial class BackupsController
{
    /// <summary>How long a <c>request</c>-route job waits for the engine holding the database to answer (the CLI's <c>create --wait</c> default).</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Back up now (WB-4): online through this Workbench's session engine, by request to another live engine, or offline from the closed bundle — the route
    /// <c>status</c> reported, decided again here.
    /// </summary>
    [HttpPost("jobs/create")]
    public ActionResult<BackupJobDto> StartCreate([FromBody] BackupCreateRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Store))
        {
            return Problem400("A store path is required.");
        }

        var store = BackupPaths.Full(request.Store);
        var bundle = string.IsNullOrWhiteSpace(request.Bundle) ? null : BackupPaths.Full(request.Bundle);
        var route = BackupRouting.Decide(bundle, store, _sessions, () => _ops.ReadStatus(bundle));
        Action<BackupJobContext> body = route.Kind switch
        {
            BackupRoute.InProcess => CreateInProcess(route.Session.Engine, store, request.Full),
            BackupRoute.Request => CreateByRequest(bundle, request.Full),
            BackupRoute.Offline => ctx =>
            {
                var point = _ops.CreateOffline(bundle, store, new BackupCreateOptions
                {
                    Full = request.Full,
                    Cancellation = ctx.Token,
                    Progress = (done, total) => ctx.Report("capture", done, total, "pages"),
                });
                ctx.SetResult(new BackupJobResult(Create: new BackupCreateResultDto(BackupDtoMapper.Map(point), null)));
            },
            _ => null,
        };
        if (body == null)
        {
            return Problem409("refused", route.Reason);
        }

        return Start(new BackupJobSpec(BackupJobKinds.Create, store, bundle, Route: route.Kind), body);
    }

    private Action<BackupJobContext> CreateInProcess(Sessions.EngineLifecycle lifecycle, string store, bool full) => ctx =>
    {
        // The engine reports a capture's pages only through its scheduler, which an engine without BackupOptions (the Workbench's) does not run: the
        // capture is indeterminate, never a guessed percentage. A claim on the session disposes the engine mid-capture: the capture aborts, nothing is
        // published, the database is unaffected (D13).
        ctx.Report("capture", 0, 0, null);
        var point = _ops.CreateOnline(lifecycle.Engine, store, full, ctx.Token).GetAwaiter().GetResult();
        ctx.SetResult(new BackupJobResult(Create: new BackupCreateResultDto(BackupDtoMapper.Map(point), null)));
    };

    private Action<BackupJobContext> CreateByRequest(string bundle, bool full) => ctx =>
    {
        // DL-24 / C9: the engine echoes the request's id in its status once its job starts; the outcome is its point or failure count moving after that.
        var before = _ops.ReadStatus(bundle).Status;
        var id = _ops.RequestBackup(bundle, full);
        var clock = Stopwatch.StartNew();
        var echoed = false;
        while (true)
        {
            ctx.Token.ThrowIfCancellationRequested();
            var report = _ops.ReadStatus(bundle);
            var s = report.Status;
            if (!report.EngineLive || s == null)
            {
                throw new BackupJobException("failed", "The engine holding the database stopped before it answered the request.");
            }

            echoed |= s.LastRequestId == id;
            if (echoed && s.State != BackupRunState.Running)
            {
                if (s.PointsTotal > (before?.PointsTotal ?? 0))
                {
                    ctx.SetResult(new BackupJobResult(Create: new BackupCreateResultDto(BackupDtoMapper.Map(s.LastPoint), id)));
                    return;
                }

                if (s.FailuresTotal > (before?.FailuresTotal ?? 0))
                {
                    throw new BackupJobException("failed", s.LastFailure?.Message ?? "The engine's backup failed.");
                }

                // The request's job started (its id is echoed) and ended (the engine runs no job) with neither a point nor a counted failure: the engine
                // deferred it — its store held by another process, another backup running, or the engine closing (BackupScheduler's Deferred class).
                throw new BackupJobException("failed",
                    $"The engine deferred the backup request {id}: no point was taken. Its store was held by another process, another backup was "
                    + "running, or the engine was closing; try again.");
            }

            ctx.Report(echoed ? "capture" : "requested", s.ProgressPages, s.ProgressOfPages, s.ProgressOfPages > 0 ? "pages" : null);
            if (clock.Elapsed > RequestTimeout)
            {
                throw new BackupJobException("failed", $"The engine did not answer the backup request {id} within {RequestTimeout.TotalMinutes:F0} minutes.");
            }

            ctx.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
        }
    };

    /// <summary>Verify the store or one point, quick, full or deep (<c>typhon backup verify</c>'s report).</summary>
    [HttpPost("jobs/verify")]
    public ActionResult<BackupJobDto> StartVerify([FromBody] BackupVerifyRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Store))
        {
            return Problem400("A store path is required.");
        }

        if (!Enum.TryParse<BackupVerifyLevel>(request.Level ?? "quick", ignoreCase: true, out var level) || !Enum.IsDefined(level))
        {
            return Problem400($"Unknown level '{request.Level}': expected quick, full or deep.");
        }

        PointSelector? point = null;
        if (request.Point != null)
        {
            if (!TryParsePoint(request.Point, archiveTargets: false, out var p, out var error))
            {
                return Problem400(error);
            }

            point = p;
        }

        var store = BackupPaths.Full(request.Store);
        return Start(new BackupJobSpec(BackupJobKinds.Verify, store), ctx =>
        {
            var report = _ops.Verify(store, point, level, new BackupVerifyOptions
            {
                Cancellation = ctx.Token,
                Progress = (phase, done, total) => ctx.Report(phase, done, total, phase == "packs" ? "packs" : phase == "pages" ? "pages" : null),
            });
            ctx.SetResult(new BackupJobResult(Verify: BackupDtoMapper.Map(report)));
        });
    }

    /// <summary>
    /// Drill a point (WB-13) in a child process, loading the schema assemblies of the session that holds <see cref="BackupDrillRequestDto.Bundle"/>, if
    /// any. At most one drill at a time in the whole Workbench: each restores a whole image (the registry's admission).
    /// </summary>
    [HttpPost("jobs/drill")]
    public ActionResult<BackupJobDto> StartDrill([FromBody] BackupDrillRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Store))
        {
            return Problem400("A store path is required.");
        }

        if (!TryParsePoint(request.Point, archiveTargets: false, out var point, out var error))
        {
            return Problem400(error);
        }

        var store = BackupPaths.Full(request.Store);
        var scratch = BackupPaths.Full(string.IsNullOrWhiteSpace(request.Scratch) ? DefaultScratch() : request.Scratch);
        var schema = SchemaOf(request.Bundle);
        var pointArg = point.IsLatest ? "latest" : point.BackupId.ToString();
        return Start(new BackupJobSpec(BackupJobKinds.Drill, store, request.Bundle), ctx =>
        {
            var report = _ops.Drill(store, pointArg, scratch, schema,
                (stage, done, total) => ctx.Report(stage, done, total, stage == nameof(BackupDrillStage.Restore) ? "pages" : null), ctx.Token);
            ctx.SetResult(new BackupJobResult(Drill: report));
        });
    }

    /// <summary>Compact a point into a base: reads about its chain's bytes, writes one full pack; deletes nothing.</summary>
    [HttpPost("jobs/compact")]
    public ActionResult<BackupJobDto> StartCompact([FromBody] BackupCompactRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Store))
        {
            return Problem400("A store path is required.");
        }

        if (!TryParsePoint(request.Point, archiveTargets: false, out var point, out var error))
        {
            return Problem400(error);
        }

        var store = BackupPaths.Full(request.Store);
        return Start(new BackupJobSpec(BackupJobKinds.Compact, store), ctx =>
        {
            var result = _ops.Compact(store, point, new BackupLifecycleOptions
            {
                Cancellation = ctx.Token,
                Progress = (done, total) => ctx.Report("compact", done, total, "bytes"),
            });
            ctx.SetResult(new BackupJobResult(Compact: BackupDtoMapper.Map(result)));
        });
    }

    /// <summary>
    /// Apply the retention plan the operator reviewed (WB-8): the plan and policy the preview served, by fingerprint. The engine refuses
    /// (<c>plan_changed</c>, nothing deleted) when the store would now delete a pack that plan did not list.
    /// </summary>
    [HttpPost("jobs/retention")]
    public ActionResult<BackupJobDto> StartRetention([FromBody] BackupRetentionApplyRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Store) || string.IsNullOrWhiteSpace(request.ApprovedFingerprint))
        {
            return Problem400("A store path and the reviewed plan's fingerprint are required: a retention is never applied without its preview.");
        }

        var store = BackupPaths.Full(request.Store);
        if (!_plans.TryFind(store, request.ApprovedFingerprint, out var policy, out var approved))
        {
            return Problem409("plan_changed",
                "This Workbench no longer knows the reviewed plan (it restarted, or the plan is over an hour old): preview the retention again.");
        }

        return Start(new BackupJobSpec(BackupJobKinds.Retention, store), ctx =>
        {
            var result = _ops.ApplyRetention(store, policy, approved, new BackupLifecycleOptions
            {
                Cancellation = ctx.Token,
                Progress = (done, total) => ctx.Report("compact", done, total, "bytes"),
            });
            ctx.SetResult(new BackupJobResult(Retention: BackupDtoMapper.Map(result)));
        });
    }

    /// <summary>
    /// Restore under the reviewed plan's fingerprint (WB-7), then settle the restored bundle (WB-10) in a child process, with the schema of the session that
    /// holds <see cref="BackupRestoreRequestDto.Bundle"/>, if any. Refused before anything is written when the plan refuses or a session of this Workbench
    /// holds or is opening the target (WB-12, decided under the registry's lock); while it runs no session may open the target.
    /// </summary>
    [HttpPost("jobs/restore")]
    public ActionResult<BackupJobDto> StartRestore([FromBody] BackupRestoreRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Store) || string.IsNullOrWhiteSpace(request.Target))
        {
            return Problem400("A store path and a target are required.");
        }

        if (string.IsNullOrWhiteSpace(request.PlanFingerprint))
        {
            return Problem400("The reviewed plan's fingerprint is required: a restore never runs against a preview nobody saw.");
        }

        if (!TryParsePoint(request.Point, archiveTargets: true, out var point, out var error))
        {
            return Problem400(error);
        }

        var store = BackupPaths.Full(request.Store);
        BackupRestorePlanDto plan;
        try
        {
            // The binding needs the fingerprint and the refusals, not what is lost after the point (a listing and an archive read).
            plan = Plan(store, point, request, withLost: false);
        }
        catch (Exception e) when (BackupOperations.Classify(e) != BackupFailureKind.Defect || e is ArgumentException)
        {
            return Problem409(BackupDtoMapper.Category(e), e.Message);
        }

        if (!plan.CanRestore)
        {
            return Problem409(plan.Unreachable != null ? "store_damaged" : "refused", plan.RefusalMessage ?? "The restore is refused.");
        }

        if (!string.Equals(plan.Fingerprint, request.PlanFingerprint, StringComparison.Ordinal))
        {
            return Problem409("plan_changed", "What this restore would do has changed since the preview: review the new plan.");
        }

        var schema = SchemaOf(request.Bundle);
        return Start(new BackupJobSpec(BackupJobKinds.Restore, store, request.Bundle, plan.ResolvedTarget), ctx =>
        {
            // A restore cancelled before it began never starts (the stager itself observes the token only between its write batches).
            ctx.Token.ThrowIfCancellationRequested();
            BackupSettleDto settle = null;
            var result = _ops.Restore(store, point, BackupPaths.Full(request.Target), new BackupRestoreOptions
            {
                Replace = request.Replace,
                AllowDamaged = request.AllowDamaged,
                AllowFormatMismatch = request.AllowFormatMismatch,
                ApprovedPlan = request.PlanFingerprint,
                Cancellation = ctx.Token,
                Progress = (done, total) => ctx.Report("restore", done, total, "pages"),
                OpenAndClose = bundle => Settle(ctx, bundle, schema, s => settle = s),
            });
            settle ??= new BackupSettleDto(false, false, null, 0, result.SettleError, false, schema.Length > 0);

            // Honoured before the publish only: a cancel that arrives after the swap leaves a published restore, which the result says.
            var note = ctx.Token.IsCancellationRequested ? "Published: the cancel came after the swap." : null;
            ctx.SetResult(new BackupJobResult(Restore: BackupDtoMapper.Map(result, settle)), note);
        });
    }

    /// <summary>
    /// The restore's settle, in a child process; <paramref name="record"/> receives what it did, whether it stands or not. A settle whose open had to
    /// replay and did not is a failed one (WB-10's guard): the bundle holds the image at <c>C</c>. A failure is rethrown, so the restore reports it as its
    /// settle error (04 §2).
    /// </summary>
    private void Settle(BackupJobContext ctx, string bundle, string[] schema, Action<BackupSettleDto> record)
    {
        ctx.Report("settle", 0, 0, null);
        var clock = Stopwatch.StartNew();
        BackupSettleReport r;
        try
        {
            r = _ops.Settle(bundle, schema);
        }
        catch (Exception e)
        {
            record(new BackupSettleDto(true, false, null, clock.Elapsed.TotalMilliseconds, e.Message, BackupSettle.NeedsSchema(e), schema.Length > 0));
            throw;
        }

        var guard = BackupSettle.GuardError(r);
        record(new BackupSettleDto(true, r.CrashRecoveryRan, r.Recovery, r.DurationMs, guard, false, schema.Length > 0));
        if (guard != null)
        {
            throw new BackupJobException("failed", guard);
        }
    }

    /// <summary>The store's jobs, newest first, as summaries (no result: GET the job for it): the running one and the finished ones still kept.</summary>
    [HttpGet("jobs")]
    public ActionResult<List<BackupJobDto>> ListJobs([FromQuery] string store)
    {
        if (string.IsNullOrWhiteSpace(store))
        {
            return Problem400("A store path is required.");
        }

        var jobs = _jobs.List(store);
        var list = new List<BackupJobDto>(jobs.Count);
        foreach (var j in jobs)
        {
            list.Add(j.Summary());
        }

        return Ok(list);
    }

    /// <summary>A job's snapshot, with its result; 404 once it is no longer kept (or the Workbench restarted).</summary>
    [HttpGet("jobs/{jobId:guid}")]
    public ActionResult<BackupJobDto> GetJob(Guid jobId) =>
        _jobs.Get(jobId) is { } job ? Ok(job.Snapshot()) : NotFound();

    /// <summary>Cancels a job (idempotent). A capture publishes nothing; a restore is cancelled before its publish only.</summary>
    [HttpDelete("jobs/{jobId:guid}")]
    public IActionResult CancelJob(Guid jobId) => _jobs.Cancel(jobId) ? NoContent() : NotFound();

    private ActionResult<BackupJobDto> Start(BackupJobSpec spec, Action<BackupJobContext> body)
    {
        var start = _jobs.TryStart(spec, body);
        return start.Admission switch
        {
            BackupJobAdmission.Started => Ok(start.Job.Snapshot()),
            BackupJobAdmission.StoreBusy => Busy(start.Job, spec.Store, $"A {start.Job.Kind} job is already running on this store: one at a time."),
            BackupJobAdmission.DrillBusy => Busy(start.Job, spec.Store,
                $"A drill is already running in this Workbench (on '{start.Job.Store}'); one at a time, each restores a whole image."),
            BackupJobAdmission.TargetBusy => Problem409("refused",
                $"'{spec.Target}' is the target of another running restore (backup job {start.Job.Id}); one restore into a bundle at a time."),
            BackupJobAdmission.TargetOpening => Problem409("refused",
                $"'{spec.Target}' is being opened in this Workbench. Close that session to restore over it."),
            _ => TargetHeld(spec.Target, start.HolderSessionId),
        };
    }

    private ObjectResult TargetHeld(string target, Guid? session)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "refused",
            Detail = $"'{target}' is open in this Workbench. Close that session to restore over it.",
        };
        problem.Extensions["heldBySessionId"] = session;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status409Conflict };
    }

    /// <summary>A start refused because a job is in the way: <c>sameStore</c> tells the client whether that job is the one to attach to.</summary>
    private static ObjectResult Busy(BackupJob running, string store, string detail)
    {
        var problem = new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "busy", Detail = detail };
        problem.Extensions["runningJobId"] = running.Id;
        problem.Extensions["sameStore"] = BackupPaths.Same(running.Store, store);
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status409Conflict };
    }

    /// <summary>The schema assemblies the session on <paramref name="bundle"/> loaded (to replay a WAL tail of its archetypes); none without one.</summary>
    private string[] SchemaOf(string bundle) => BackupRouting.SessionOn(_sessions, bundle)?.SchemaDllPaths ?? [];
}
