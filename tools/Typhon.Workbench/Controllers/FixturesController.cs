using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// Sample-database endpoint. Calls the internal <see cref="FixtureDatabase.CreateOrReuse"/> to produce (or reuse)
/// a populated Workbench "sample database" under the user's local app-data folder, so a first-run user can open a
/// real-content database instantly — a "Create sample database" action in the Workbench — without hand-running the
/// NUnit generator. Shipped in Release (#433); the client detects availability via the capability probe at
/// <see cref="GetCapability"/>.
///
/// The DEBUG-only E2E-support endpoints (trace fixtures, in-process mock profiler) for the Tier-0 profiler canaries
/// live in the sibling partial <c>FixturesController.E2E.cs</c> and never ship in a Release build.
/// </summary>
[ApiController]
[Route("api/fixtures")]
[Tags("Fixtures")]
[RequireBootstrapToken]
public sealed partial class FixturesController(SessionManager sessions) : ControllerBase
{
    /// <summary>Capability probe — lets the client decide whether to render the Create-sample-database form.</summary>
    [HttpGet("capability")]
    public ActionResult<FixtureCapabilityDto> GetCapability()
        => Ok(new FixtureCapabilityDto(
            Available: true,
            OutputDirectory: DefaultOutputDirectoryRoot(),
            DefaultDatabaseName: FixtureDatabase.DefaultDatabaseName));

    /// <summary>
    /// Create (or reuse) the Workbench sample database — **async** since the Stress preset can take significant
    /// time to spawn millions of entities. Returns a <c>jobId</c> immediately (HTTP 202 semantics — body, not status,
    /// signals "in progress"); the client polls <see cref="GetJob"/> for progress + the terminal result. <see cref="CancelJob"/>
    /// signals cancellation between sub-batches. When <paramref name="req"/>.Force is <c>true</c>, closes any open
    /// session against the fixture directory first so Windows releases the memory-mapped file handle before the wipe.
    ///
    /// The reuse-without-regenerating fast path is honoured by <see cref="FixtureDatabase.CreateOrReuse"/> when the
    /// on-disk config hash matches — same preset, same tweaks ⇒ instant return.
    /// </summary>
    [HttpPost("create")]
    public ActionResult<StartFixtureJobResponseDto> Create([FromBody] CreateFixtureRequestDto req)
    {
        // Resolve + validate the database name first — bad names short-circuit with a 400 before we touch the job
        // registry, so the client sees a clear validation message instead of a generic 500 from the background task.
        var requestedName = string.IsNullOrWhiteSpace(req?.DatabaseName) ? FixtureDatabase.DefaultDatabaseName : req.DatabaseName;
        if (!FixtureDatabase.TryValidateDatabaseName(requestedName, out var dbName, out var nameError))
        {
            return BadRequest(new { detail = nameError });
        }

        // `CreateOrReuse` materialises each fixture under `{outputDir}/{databaseName}/`, so we only need to pass the
        // root here — the per-name sub-directory is composed engine-side. Honour an explicit OutputDirectory when
        // supplied (E2E specs, scripted callers); otherwise use the default root.
        var outDir = string.IsNullOrWhiteSpace(req?.OutputDirectory)
            ? DefaultOutputDirectoryRoot()
            : req.OutputDirectory;
        var force = req?.Force ?? false;
        var config = req?.Config ?? FixtureConfig.Default;

        if (force)
        {
            // CreateOrReuse composes `{outDir}/{dbName}/` for the per-database working dir — match the same leaf
            // here so we close only the session(s) backing THIS database (siblings of other DBs in `outDir` are
            // untouched). PrepareOutputDirectory will wipe `{outDir}/{dbName}/` wholesale, and Windows would
            // otherwise hold the MMF lock against the wipe if the session is still open.
            var absDbDir = Path.GetFullPath(Path.Combine(outDir, dbName));
            sessions.RemoveWhere(s => !string.IsNullOrEmpty(s.FilePath) &&
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(s.FilePath)), absDbDir,
                    StringComparison.OrdinalIgnoreCase));
        }

        FixtureJobRegistry.Prune();
        var job = FixtureJobRegistry.Create();
        job.AttachTask(Task.Run(() =>
        {
            try
            {
                job.SetRunning();
                var progress = new Progress<FixtureProgressReport>(p => job.SetProgress(p.Phase, p.Completed, p.Total));
                var useBulkLoad = req?.UseBulkLoad ?? false;
                var result = FixtureDatabase.CreateOrReuse(outDir, force, config, progress, job.Cts.Token, dbName, useBulkLoad);
                job.SetDone(result);
            }
            catch (OperationCanceledException)
            {
                job.SetCancelled();
            }
            catch (Exception ex)
            {
                job.SetError(ex.Message);
            }
        }));

        return Ok(new StartFixtureJobResponseDto(JobId: job.JobId));
    }

    /// <summary>
    /// Poll a fixture-generation job's state. The client polls this endpoint (~300 ms) until <c>State</c> reaches a
    /// terminal value (<c>done</c>, <c>error</c>, <c>cancelled</c>). 404 when the job id is unknown — the client
    /// treats this as a soft failure (stop polling, surface "job lost").
    /// </summary>
    [HttpGet("jobs/{jobId}")]
    public ActionResult<FixtureJobStateDto> GetJob(string jobId)
    {
        var job = FixtureJobRegistry.Get(jobId);
        if (job == null) return NotFound();
        return Ok(job.Snapshot());
    }

    /// <summary>
    /// Cancel a fixture-generation job. The cancellation is signalled via <see cref="CancellationToken"/>; the background
    /// Task observes it between sub-batches and unwinds cleanly. 404 when the id is unknown (already terminated /
    /// never existed); the client treats that as success since cancellation is idempotent.
    /// </summary>
    [HttpDelete("jobs/{jobId}")]
    public IActionResult CancelJob(string jobId)
    {
        if (!FixtureJobRegistry.Cancel(jobId)) return NotFound();
        return NoContent();
    }

    /// <summary>
    /// Root directory under which per-database fixture subdirectories are created. Follows the same per-user
    /// local-state convention as the bootstrap token file. On POSIX hosts uses <c>$XDG_DATA_HOME/typhon/workbench/fixtures/</c>
    /// (or <c>~/.local/share/typhon/workbench/fixtures/</c> as a fallback), on Windows uses
    /// <c>%LOCALAPPDATA%\Typhon\Workbench\Fixtures\</c>. The database name is appended at request time. Shared with the
    /// DEBUG-only E2E partial (trace fixtures live under a <c>traces</c> sibling of the per-database roots).
    /// </summary>
    private static string DefaultOutputDirectoryRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Typhon", "Workbench", "Fixtures");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(xdg))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            xdg = Path.Combine(home, ".local", "share");
        }
        return Path.Combine(xdg, "typhon", "workbench", "fixtures");
    }
}

/// <summary>
/// Client-facing advertisement of the sample-database capability + defaults.
/// <c>OutputDirectory</c> is the ROOT of the per-database subdirectories — the controller appends
/// <c>/{databaseName}/</c> at request time. <c>DefaultDatabaseName</c> seeds the form so the user sees the same name
/// the back-compat path uses, and can edit it to generate sibling fixtures with different shapes.
/// </summary>
public sealed record FixtureCapabilityDto(bool Available, string OutputDirectory, string DefaultDatabaseName);

/// <summary>
/// Request body for <see cref="FixturesController.Create"/>. <c>Config</c> is optional — omitting it (existing
/// callers, manual NUnit generator) keeps today's behaviour by defaulting to <see cref="FixtureConfig.Default"/>.
/// <c>DatabaseName</c> is optional — omitting it falls back to <see cref="FixtureDatabase.DefaultDatabaseName"/>;
/// supplying it routes the generated <c>.typhon</c> + <c>.bin</c> + WAL under a sibling subdirectory so multiple
/// fixtures can coexist.
/// </summary>
public sealed record CreateFixtureRequestDto(
    bool Force,
    string OutputDirectory,
    FixtureConfig Config = null,
    string DatabaseName = null,
    bool UseBulkLoad = false);

/// <summary>
/// Response body for the now-async <see cref="FixturesController.Create"/>. The client polls <c>jobId</c> via
/// <see cref="FixturesController.GetJob"/> for progress + terminal result.
/// </summary>
public sealed record StartFixtureJobResponseDto(string JobId);
