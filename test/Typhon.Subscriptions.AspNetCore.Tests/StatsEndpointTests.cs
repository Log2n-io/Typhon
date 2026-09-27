using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine;

namespace Typhon.Subscriptions.AspNetCore.Tests;

/// <summary>
/// #ENG-07 — what <c>MapTyphonStats</c> actually writes, against a real engine and a real runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this fixture exists separately from <see cref="WebSocketEndpointTests"/>.</b> That one owns the HTTP half and deliberately has no
/// <c>TyphonRuntime</c>, so all it can say about this endpoint is that the route exists and answers 503 before the runtime does. The BODY was therefore
/// untested: a mis-keyed object, a number written under the wrong name or an unbalanced writer would have shipped, and the first reader to notice would have
/// been an operator reading a dashboard.
/// </para>
/// <para>
/// <b>The runtime is shut down before the request, which is what makes exact comparison possible.</b> While a runtime ticks, two reads of a percentile are
/// two different windows at two different moments — an earlier attempt to compare the wire block against the reader measured 32.5 ms against 0.206 ms for
/// exactly that reason. A stopped runtime has a frozen ring, so the body and a direct <c>ReadStats()</c> call must agree to the digit, and the assertion
/// becomes about the projection rather than about timing.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable] // owns a database directory and a runtime; runs alone like every other engine-bearing fixture
public class StatsEndpointTests
{
    private string _databaseDir;

    [SetUp]
    public void SetUp()
    {
        _databaseDir = Path.Combine(Path.GetTempPath(), $"TyphonStatsEndpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_databaseDir);
    }

    [TearDown]
    public void TearDown()
    {
        // The database is a real file here (the in-memory WAL backend is internal to the engine), so it has to be swept or every run leaves one behind.
        try
        {
            Directory.Delete(_databaseDir, recursive: true);
        }
        catch (IOException)
        {
            // A handle the host has not released yet. A stale temp directory is not worth failing a green test over.
        }
    }

    [Test]
    public async Task TheBodyIsTheSnapshot_FieldForField()
    {
        using var engine = BuildEngine(out var provider, out var scope);
        using (provider)
        using (scope)
        {
            using var runtime = StoppedRuntimeOverThreeTicks(engine);

            using var host = await StartAsync(runtime).ConfigureAwait(false);
            var response = await host.GetTestClient().GetAsync("/typhon/stats.json").ConfigureAwait(false);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True, "the endpoint reports what the engine is doing now; the 1 s cache belongs at the edge");

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            // Parsing at all is the first assertion: Utf8JsonWriter validates structure, but only a reader proves the document a caller receives is well
            // formed and complete rather than truncated at a flush.
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // The ring is frozen, so the projection must be exact. A tolerance here would hide the failure it exists to catch: a field written under
            // another field's name.
            var expected = runtime.ReadStats();

            Assert.Multiple(() =>
            {
                Assert.That(root.GetProperty("tick").GetInt64(), Is.EqualTo(expected.Tick));
                Assert.That(root.GetProperty("ticksInWindow").GetInt32(), Is.EqualTo(expected.TicksInWindow));

                var tick = root.GetProperty("tickMs");
                Assert.That(tick.GetProperty("target").GetDouble(), Is.EqualTo(expected.TargetTickMs));
                Assert.That(tick.GetProperty("p50").GetDouble(), Is.EqualTo(expected.TickP50Ms));
                Assert.That(tick.GetProperty("p99").GetDouble(), Is.EqualTo(expected.TickP99Ms));
                Assert.That(tick.GetProperty("overruns").GetInt32(), Is.EqualTo(expected.Overruns));

                // In its own object beside the tick, because the pair is the reading an operator needs: 12 ms of tick with 9 of it here is a disk problem.
                Assert.That(root.GetProperty("durabilityMs").GetProperty("waitP99").GetDouble(), Is.EqualTo(expected.DurabilityWaitP99Ms));

                var replication = root.GetProperty("replication");
                Assert.That(replication.GetProperty("archetypes").GetInt32(), Is.EqualTo(expected.ReplicatedArchetypes));
                Assert.That(replication.GetProperty("sessions").GetInt32(), Is.EqualTo(expected.Sessions));
                Assert.That(replication.GetProperty("outBytesTotal").GetInt64(), Is.EqualTo(expected.NetOutBytesTotal));
                Assert.That(replication.GetProperty("trackP99Ms").GetDouble(), Is.EqualTo(expected.ReplicationTrackP99Ms));

                Assert.That(replication.TryGetProperty("running", out _), Is.False,
                    "a `running` flag would be true on every started engine, replicating or not — the count replaced it deliberately");
            });
        }
    }

    [Test]
    public async Task EverySystemAndArchetypeIsNamedInTheArrays()
    {
        using var engine = BuildEngine(out var provider, out var scope);
        using (provider)
        using (scope)
        {
            using var runtime = StoppedRuntimeOverThreeTicks(engine);

            using var host = await StartAsync(runtime).ConfigureAwait(false);
            var json = await host.GetTestClient().GetStringAsync("/typhon/stats.json").ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            var expected = runtime.ReadStats();
            var systems = document.RootElement.GetProperty("systems");
            var archetypes = document.RootElement.GetProperty("archetypes");

            Assert.Multiple(() =>
            {
                Assert.That(systems.GetArrayLength(), Is.EqualTo(expected.Systems.Length), "one entry per scheduled system");
                Assert.That(archetypes.GetArrayLength(), Is.EqualTo(expected.Archetypes.Length));

                // Named, not indexed. A system index is global and means nothing to a reader of a dashboard, which is the whole reason the array carries
                // names rather than positions.
                var found = false;
                foreach (var system in systems.EnumerateArray())
                {
                    Assert.That(system.TryGetProperty("name", out _), Is.True, "every entry carries a name");
                    Assert.That(system.TryGetProperty("meanUs", out _), Is.True, "and its mean, in microseconds");
                    found |= system.GetProperty("name").GetString() == "Probe";
                }

                Assert.That(found, Is.True, "the test's own system must appear by name");

                foreach (var archetype in archetypes.EnumerateArray())
                {
                    Assert.That(archetype.GetProperty("name").GetString(), Is.Not.Null.And.Not.Empty);
                    Assert.That(archetype.GetProperty("entities").GetInt64(), Is.GreaterThanOrEqualTo(0));
                }
            });
        }
    }

    [Test]
    public async Task ARuntimeThatNeverTickedStillAnswers_WithZerosRatherThanAnError()
    {
        // The state a host is scraped in during startup. An error or an empty body here would make "still starting" indistinguishable from "broken", which
        // is the distinction a health dashboard exists to draw.
        using var engine = BuildEngine(out var provider, out var scope);
        using (provider)
        using (scope)
        {
            using var runtime = TyphonRuntime.Create(engine, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Probe").CallbackSystem("Probe", static _ => { });
            }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

            using var host = await StartAsync(runtime).ConfigureAwait(false);
            var response = await host.GetTestClient().GetAsync("/typhon/stats.json").ConfigureAwait(false);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "not started is not an error");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = document.RootElement;

            Assert.Multiple(() =>
            {
                Assert.That(root.GetProperty("tick").GetInt64(), Is.EqualTo(-1), "no tick has been recorded");
                Assert.That(root.GetProperty("ticksInWindow").GetInt32(), Is.Zero);
                Assert.That(root.GetProperty("tickMs").GetProperty("p99").GetDouble(), Is.Zero);
                Assert.That(root.GetProperty("tickMs").GetProperty("target").GetDouble(), Is.EqualTo(1.0).Within(1e-9),
                    "the configured target is known before the first tick, and is what makes the zeros readable");
                Assert.That(root.GetProperty("durabilityMs").GetProperty("waitP99").GetDouble(), Is.Zero);
            });
        }
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A real engine over a temp database directory — the endpoint's subject is a running engine, so there is nothing to fake here.</summary>
    private DatabaseEngine BuildEngine(out ServiceProvider provider, out IServiceScope scope)
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "stats-endpoint";
                o.DatabaseDirectory = _databaseDir;
                // 64 MiB, which is the engine's own recommended minimum: `TestMode` (the switch that suppresses the small-cache advisory) is internal to
                // the engine and visible only to its own test project, so the honest way to keep this fixture's output clean is to ask for enough cache.
                o.DatabaseCacheSize = 64UL * 1024 * 1024;
            })
            .AddScopedDatabaseEngine();

        provider = services.BuildServiceProvider();
        scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        engine.InitializeArchetypes();
        return engine;
    }

    /// <summary>
    /// A runtime that ran at least three ticks and was then shut down, so its telemetry ring is frozen and two reads of it agree to the digit.
    /// </summary>
    private static TyphonRuntime StoppedRuntimeOverThreeTicks(DatabaseEngine engine)
    {
        var ticks = 0;
        var runtime = TyphonRuntime.Create(engine, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Probe").CallbackSystem("Probe", _ => Interlocked.Increment(ref ticks));
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        var ring = runtime.Telemetry;
        // Waiting on the RECORDED count, not on the system's counter: the counter moves part-way through a tick, and shutting down there would cut the
        // tick's telemetry before it was written — the ring would hold fewer ticks than the wait implied.
        SpinWait.SpinUntil(() => ring.TotalTicksRecorded >= 3, TimeSpan.FromSeconds(5));
        runtime.Shutdown();
        Assert.That(ring.TotalTicksRecorded, Is.GreaterThanOrEqualTo(3), "the runtime ran and recorded before it stopped");
        return runtime;
    }

    private static async Task<IHost> StartAsync(TyphonRuntime runtime) =>
        await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(runtime);
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapTyphonStats("/typhon/stats.json"));
                }))
            .StartAsync()
            .ConfigureAwait(false);
}
