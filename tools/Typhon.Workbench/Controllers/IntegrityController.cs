using Microsoft.AspNetCore.Mvc;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Storage;
using Typhon.Workbench.Middleware;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// REST surface for database integrity: scan, plan a repair, apply one.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>path-based rather than session-based</b>. Every other storage endpoint introspects the live engine of
/// an open session; these do not, and must not. A scan has to work on a database that will not open — that is the case
/// that most justifies its existence — and a repair needs exclusive access, which by definition means no session holds
/// the database. Routing these through a session would make the feature unavailable in exactly the situations it exists
/// for.
/// </para>
/// <para>
/// Scanning is read-only and therefore unrestricted. Applying a repair is guarded twice: the caller must present the
/// fingerprint of the plan it reviewed, and the engine re-scans and refuses if the database moved since. Consent to lose
/// data is a separate flag again, and the loss manifest is returned in full rather than summarised — a dialog that says
/// "47 entities will be affected, OK?" is not consent; the list is.
/// </para>
/// </remarks>
[ApiController]
[Route("api/integrity")]
[Tags("Integrity")]
[RequireBootstrapToken]
public sealed class IntegrityController : ControllerBase
{
    /// <summary>Scans a bundle and returns the report. Read-only and always safe to call.</summary>
    /// <param name="request">Which bundle to scan, and how deeply.</param>
    [HttpPost("scan")]
    public ActionResult<IntegrityReportDto> Scan([FromBody] IntegrityScanRequestDto request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest(new { error = "A bundle path is required." });
        }

        if (!TryParseDepth(request.Depth, out var depth))
        {
            return BadRequest(new { error = $"Unknown depth '{request.Depth}'. Expected spine, quick, standard or deep." });
        }

        try
        {
            using var source = new OfflineBundlePageSource(request.Path);
            var report = IntegrityScanner.Scan(source, new IntegrityOptions { Depth = depth });
            return Ok(Map(report));
        }
        catch (Exception ex) when (ex is IOException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Derives a repair plan. Read-only: describes what would happen and changes nothing.</summary>
    /// <param name="request">Which bundle to plan for.</param>
    [HttpPost("plan")]
    public ActionResult<RepairPlanDto> PlanRepair([FromBody] IntegrityScanRequestDto request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest(new { error = "A bundle path is required." });
        }

        try
        {
            using var source = new OfflineBundlePageSource(request.Path);
            var report = IntegrityScanner.Scan(source, IntegrityOptions.Deep);
            return Ok(Map(DatabaseRepair.Plan(report)));
        }
        catch (Exception ex) when (ex is IOException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Applies a repair. The only mutating endpoint in the surface.</summary>
    /// <param name="request">Which bundle, which plan fingerprint, and what consent was given.</param>
    [HttpPost("apply")]
    public ActionResult<RepairOutcomeDto> ApplyRepair([FromBody] RepairApplyRequestDto request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest(new { error = "A bundle path is required." });
        }

        if (string.IsNullOrWhiteSpace(request.Fingerprint))
        {
            return BadRequest(new
            {
                error = "A plan fingerprint is required. Applying without one would mean repairing against a diagnosis "
                    + "nobody reviewed."
            });
        }

        try
        {
            RepairPlan plan;
            using (var source = new OfflineBundlePageSource(request.Path))
            {
                if (source.LockHeld)
                {
                    return Conflict(new
                    {
                        error = "This database is open in another process. Repair requires exclusive access — close every "
                            + "session on it and retry."
                    });
                }

                plan = DatabaseRepair.Plan(IntegrityScanner.Scan(source, IntegrityOptions.Deep));
            }

            if (!string.Equals(plan.DatabaseFingerprint, request.Fingerprint, StringComparison.Ordinal))
            {
                return Conflict(new
                {
                    error = "The database has changed since the plan was reviewed, so the plan no longer describes it. "
                        + "Re-scan and review a fresh plan.",
                    expected = request.Fingerprint,
                    actual = plan.DatabaseFingerprint
                });
            }

            var outcome = DatabaseRepair.Apply(request.Path, plan, request.AllowLoss, request.BackupFirst, request.DryRun);
            return Ok(Map(outcome));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    private static bool TryParseDepth(string value, out ScanDepth depth)
    {
        depth = ScanDepth.Standard;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "spine": depth = ScanDepth.Spine; return true;
            case "quick": depth = ScanDepth.Quick; return true;
            case "standard": depth = ScanDepth.Standard; return true;
            case "deep": depth = ScanDepth.Deep; return true;
            default: return false;
        }
    }

    private static IntegrityReportDto Map(IntegrityReport report)
    {
        var findings = new List<IntegrityFindingDto>(report.Findings.Count);
        for (var i = 0; i < report.Findings.Count; i++)
        {
            var f = report.Findings[i];
            findings.Add(new IntegrityFindingDto(f.Code, f.Severity.ToString(), f.Confidence.ToString(), f.Summary, f.Detail,
                f.RuleId, f.Repair.ToString(), f.Occurrences, Map(f.Locus), Map(f.Loss)));
        }

        var id = report.Identity;
        var t = report.Totals;

        return new IntegrityReportDto(
            report.Verdict.ToString(), report.ExitCode, report.Source, report.Mode.ToString(), report.Depth.ToString(),
            report.Duration.TotalMilliseconds,
            new IntegrityIdentityDto(id.Name, id.FormatRevision, id.PageCount, id.SizeBytes, id.CheckpointLsn, id.CleanShutdown,
                id.WalSegmentCount, id.WalBytes),
            new IntegrityTotalsDto(t.PagesScanned, t.PagesAllocated, t.ChecksumFailures, t.PagesWithSectorFooters,
                t.SectorFailures, t.SegmentsWalked, t.BytesLeaked),
            findings,
            new IntegrityLimitsDto(ScanLimits.StructuralLimit, report.Limits.ChecksSkipped, report.Limits.Caveats));
    }

    private static IntegrityLocusDto Map(Locus locus)
        => new(locus.FilePageIndex, locus.SegmentRootPage, locus.Kind.ToString(), locus.ArchetypeName, locus.ComponentName, locus.ToString());

    private static IntegrityLossDto Map(LossEstimate loss)
        => new(loss.Kind.ToString(), loss.CountText, loss.Archetype, loss.Component, loss.Explanation);

    private static RepairPlanDto Map(RepairPlan plan)
    {
        var steps = new List<RepairStepDto>(plan.Steps.Count);
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var s = plan.Steps[i];
            steps.Add(new RepairStepDto(s.Order, s.Action.ToString(), s.Class.ToString(), s.Description, s.Rationale, s.Addresses));
        }

        var losses = new List<IntegrityLossDto>(plan.Loss.Entries.Count);
        for (var i = 0; i < plan.Loss.Entries.Count; i++)
        {
            losses.Add(Map(plan.Loss.Entries[i]));
        }

        return new RepairPlanDto(plan.Source, plan.DatabaseFingerprint, plan.Verdict.ToString(), plan.RequiresLossyConsent,
            steps, losses, plan.Unaddressed, plan.BlockedReason);
    }

    private static RepairOutcomeDto Map(RepairOutcome outcome)
    {
        var results = new List<RepairStepResultDto>(outcome.Results.Count);
        for (var i = 0; i < outcome.Results.Count; i++)
        {
            var r = outcome.Results[i];
            results.Add(new RepairStepResultDto(r.Step.Order, r.Step.Action.ToString(), r.Outcome.ToString(), r.Detail));
        }

        return new RepairOutcomeDto(outcome.Succeeded, outcome.BackupPath, results,
            outcome.VerificationReport == null ? null : Map(outcome.VerificationReport));
    }
}
