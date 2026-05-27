#if DEBUG
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// DEBUG-only dev-fixture endpoint. Calls the internal <see cref="FixtureDatabase.CreateOrReuse"/>
/// to produce (or reuse) a populated Workbench test database under the user's local app data
/// folder, so the "Dev Fixture" Connect tab can instantly open a real-content DB without the user
/// having to run the NUnit generator manually.
///
/// Also hosts two E2E-support endpoints for the Tier-0 profiler canaries:
/// <list type="bullet">
///   <item><c>POST /api/fixtures/trace</c> — writes a minimal valid <c>.typhon-trace</c> on disk and returns its path.</item>
///   <item><c>POST /api/fixtures/mock-profiler</c> — starts an in-process <see cref="MockTcpProfilerServer"/> and returns its port.</item>
///   <item><c>DELETE /api/fixtures/mock-profiler/{port}</c> — stops a previously-started mock server.</item>
/// </list>
/// Gated by <c>#if DEBUG</c> so this surface never ships in a Release build of the Workbench. The
/// client detects availability via the capability probe at <see cref="GetCapability"/>.
/// </summary>
[ApiController]
[Route("api/fixtures")]
[Tags("Fixtures")]
[RequireBootstrapToken]
public sealed class FixturesController(SessionManager sessions) : ControllerBase
{
    /// <summary>
    /// Registry of live mock profiler servers, keyed by bound port. Static because the registry's
    /// lifetime is the application's (not per-request), and DEBUG-only so it isn't a production
    /// concern. A hosted-service shutdown hook disposes every entry when the process exits.
    /// </summary>
    internal static readonly ConcurrentDictionary<int, MockTcpProfilerServer> MockServers = new();

    /// <summary>Capability probe — lets the client decide whether to render the Dev Fixture tab.</summary>
    [HttpGet("capability")]
    public ActionResult<FixtureCapabilityDto> GetCapability()
        => Ok(new FixtureCapabilityDto(
            Available: true,
            OutputDirectory: DefaultOutputDirectoryRoot(),
            DefaultDatabaseName: FixtureDatabase.DefaultDatabaseName));

    /// <summary>
    /// Create (or reuse) the Workbench dev fixture database — **async** since the Stress preset can take significant
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
    /// Write a minimal valid <c>.typhon-trace</c> to the fixtures directory and return its path. The
    /// Tier-0 Playwright canary for the Open-Trace flow calls this, pastes the returned path into
    /// the dialog, and asserts the Profiler panel mounts cleanly.
    /// </summary>
    [HttpPost("trace")]
    public ActionResult<CreateTraceFixtureResponseDto> CreateTrace([FromBody] CreateTraceFixtureRequestDto req)
    {
        // Traces go under a fixed sibling subdir of the per-DB roots, keyed by trace variant. Predates the editable
        // database-name change — the trace path doesn't participate in the dev-fixture name.
        var outDir = Path.Combine(DefaultOutputDirectoryRoot(), "traces");
        var tickCount = (req?.TickCount).GetValueOrDefault(3);
        var instantsPerTick = (req?.InstantsPerTick).GetValueOrDefault(5);

        // Variants:
        //   "with-access-declarations" — v6 wire path: 2 systems + 3 components + 3 phases. Drives the static-topology
        //                                Playwright cases (no bars; archetype tables empty).
        //   "with-archetype-touches"   — extends the above with 2 archetypes + per-tick SchedulerSystemArchetype events,
        //                                so the Data Flow timeline can render bars (#327 Phase D bar-click + hover canary).
        //   "with-context-switches"    — per-tick ThreadContextSwitch (kind 254) records, so the off-CPU overlay has
        //                                data to render (off-CPU Playwright canary).
        //   "with-cpu-samples"         — a #351 CpuSampleSection trailer (frame symbols + interned stacks + samples),
        //                                so the Call Tree panel has data to fold (Phase-4 Playwright canary).
        //   "with-queries"             — #376 Stage-3 4A: two View query definitions + executions (QueryPlan spans +
        //                                phase children) + QuerySourceStringTable, so the Query Analyzer catalog ranks
        //                                by TotalWallNs and the Executions/Plan tabs have data.
        //   default                    — minimal trace (no systems, no archetypes); for the open-trace flow canary.
        // tickCount/instantsPerTick are ignored for the typed variants; their builders hardcode the layout.
        string path;
        if (string.Equals(req?.Variant, "with-archetype-touches", StringComparison.OrdinalIgnoreCase))
        {
            path = TraceFixtureBuilder.BuildTraceWithArchetypeTouches(outDir);
        }
        else if (string.Equals(req?.Variant, "with-context-switches", StringComparison.OrdinalIgnoreCase))
        {
            path = TraceFixtureBuilder.BuildTraceWithContextSwitches(outDir);
        }
        else if (string.Equals(req?.Variant, "with-cpu-samples", StringComparison.OrdinalIgnoreCase))
        {
            path = TraceFixtureBuilder.BuildTraceWithCpuSamples(outDir);
        }
        else if (string.Equals(req?.Variant, "with-access-declarations", StringComparison.OrdinalIgnoreCase))
        {
            path = TraceFixtureBuilder.BuildTraceWithAccessDeclarations(outDir);
        }
        else if (string.Equals(req?.Variant, "with-track-hierarchy", StringComparison.OrdinalIgnoreCase))
        {
            // #354 W5 — 3 ordered tracks (Engine-Pre / Public / Engine-Post), a user DAG + a Fence DAG.
            // Drives the System-DAG Track→DAG grouping Playwright canary.
            path = TraceFixtureBuilder.BuildTraceWithTrackHierarchy(outDir);
        }
        else if (string.Equals(req?.Variant, "with-queries", StringComparison.OrdinalIgnoreCase))
        {
            // #376 Stage-3 4A — query definitions + executions + phases; drives the Query Analyzer (4B–4D).
            path = TraceFixtureBuilder.BuildTraceWithQueries(outDir);
        }
        else if (string.Equals(req?.Variant, "with-anomalies", StringComparison.OrdinalIgnoreCase))
        {
            // #377 Stage-4 Phase 3 — deterministic tick-duration outliers + GC-pause spikes at known
            // tick numbers; drives the Engine Live Health anomaly log + the J3 anomaly-jump E2E.
            path = TraceFixtureBuilder.BuildTraceWithAnomalies(outDir);
        }
        else
        {
            path = TraceFixtureBuilder.BuildMinimalTrace(outDir, tickCount, instantsPerTick);
        }
        return Ok(new CreateTraceFixtureResponseDto(TraceFilePath: path, TickCount: tickCount));
    }

    /// <summary>
    /// Start an in-process <see cref="MockTcpProfilerServer"/> bound to an ephemeral loopback port
    /// and return the port so the Playwright attach canary can point the UI at it. Tracks the
    /// server in <see cref="MockServers"/>; the paired DELETE stops it, and the application
    /// shutdown hook disposes any left over.
    /// </summary>
    [HttpPost("mock-profiler")]
    public ActionResult<StartMockProfilerResponseDto> StartMockProfiler([FromBody] StartMockProfilerRequestDto req)
    {
        var server = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds((req?.BlockIntervalMs).GetValueOrDefault(50)),
            MaxBlocks = (req?.MaxBlocks).GetValueOrDefault(200),
        };
        server.Start();
        MockServers[server.Port] = server;
        return Ok(new StartMockProfilerResponseDto(Port: server.Port));
    }

    /// <summary>
    /// Stop a previously-started mock profiler. Idempotent — a missing port returns 404 but is not a
    /// hard test failure (a client can call it after the server self-terminated on MaxBlocks).
    /// </summary>
    [HttpDelete("mock-profiler/{port:int}")]
    public async Task<IActionResult> StopMockProfiler(int port)
    {
        if (!MockServers.TryRemove(port, out var server))
        {
            return NotFound();
        }
        await server.DisposeAsync();
        return NoContent();
    }

    /// <summary>
    /// Root directory under which per-database fixture subdirectories are created. Follows the same per-user
    /// local-state convention as the bootstrap token file. On POSIX hosts uses <c>$XDG_DATA_HOME/typhon/workbench/fixtures/</c>
    /// (or <c>~/.local/share/typhon/workbench/fixtures/</c> as a fallback), on Windows uses
    /// <c>%LOCALAPPDATA%\Typhon\Workbench\Fixtures\</c>. The database name is appended at request time.
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
/// Client-facing advertisement of the dev-fixture capability + defaults.
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

/// <summary>Request body for <see cref="FixturesController.CreateTrace"/>.</summary>
public sealed record CreateTraceFixtureRequestDto(int? TickCount, int? InstantsPerTick, string Variant = null);

/// <summary>Response body for <see cref="FixturesController.CreateTrace"/>.</summary>
public sealed record CreateTraceFixtureResponseDto(string TraceFilePath, int TickCount);

/// <summary>Request body for <see cref="FixturesController.StartMockProfiler"/>.</summary>
public sealed record StartMockProfilerRequestDto(int? BlockIntervalMs, int? MaxBlocks);

/// <summary>Response body for <see cref="FixturesController.StartMockProfiler"/>.</summary>
public sealed record StartMockProfilerResponseDto(int Port);
#endif
