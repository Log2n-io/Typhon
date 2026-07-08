#if DEBUG
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// DEBUG-only E2E-support surface for the Tier-0 profiler/trace Playwright canaries, kept out of Release builds.
/// Split from the shipped sample-database surface (<c>FixturesController.cs</c>) by #433:
/// <list type="bullet">
///   <item><c>POST /api/fixtures/trace</c> — writes a minimal valid <c>.typhon-trace</c> on disk and returns its path.</item>
///   <item><c>POST /api/fixtures/mock-profiler</c> — starts an in-process <see cref="MockTcpProfilerServer"/> and returns its port.</item>
///   <item><c>DELETE /api/fixtures/mock-profiler/{port}</c> — stops a previously-started mock server.</item>
/// </list>
/// Gated by <c>#if DEBUG</c> so this surface never ships in a Release build; the shutdown hook in
/// <c>ServiceExtensions.RegisterSessionShutdownHook</c> (also <c>#if DEBUG</c>) disposes any leftover mock servers.
/// </summary>
public sealed partial class FixturesController
{
    /// <summary>
    /// Registry of live mock profiler servers, keyed by bound port. Static because the registry's
    /// lifetime is the application's (not per-request), and DEBUG-only so it isn't a production
    /// concern. A hosted-service shutdown hook disposes every entry when the process exits.
    /// </summary>
    internal static readonly ConcurrentDictionary<int, MockTcpProfilerServer> MockServers = new();

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
}

/// <summary>Request body for <see cref="FixturesController.CreateTrace"/>.</summary>
public sealed record CreateTraceFixtureRequestDto(int? TickCount, int? InstantsPerTick, string Variant = null);

/// <summary>Response body for <see cref="FixturesController.CreateTrace"/>.</summary>
public sealed record CreateTraceFixtureResponseDto(string TraceFilePath, int TickCount);

/// <summary>Request body for <see cref="FixturesController.StartMockProfiler"/>.</summary>
public sealed record StartMockProfilerRequestDto(int? BlockIntervalMs, int? MaxBlocks);

/// <summary>Response body for <see cref="FixturesController.StartMockProfiler"/>.</summary>
public sealed record StartMockProfilerResponseDto(int Port);
#endif
