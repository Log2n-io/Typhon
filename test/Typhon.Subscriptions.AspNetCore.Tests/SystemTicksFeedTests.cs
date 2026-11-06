using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine;

namespace Typhon.Subscriptions.AspNetCore.Tests;

/// <summary>
/// The per-system tick feed (<see cref="SystemTicksFeed"/>, <c>GET /typhon/systems/ticks</c>): its binary layout read back field by field, the gap a reader
/// away for longer than the ring sees, <c>after</c> clamped at both ends, its size at forty systems, and the endpoint over a real runtime.
/// </summary>
[TestFixture]
[NonParallelizable] // the endpoint case owns a database directory and a runtime
public class SystemTicksFeedTests
{
    private string _databaseDir;

    [SetUp]
    public void SetUp()
    {
        _databaseDir = Path.Combine(Path.GetTempPath(), $"TyphonSystemTicks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_databaseDir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_databaseDir, recursive: true);
        }
        catch (IOException)
        {
            // A handle the host has not released yet.
        }
    }

    /// <summary>One answer, parsed as a reader parses it.</summary>
    private sealed record Feed(ushort Version, ushort Flags, long FirstTick, int Workers, string[] Names, List<(long Tick, float WallMs, Sys[] Systems)> Ticks);

    private readonly record struct Sys(float Cpu, float Wall, byte EffX10, byte Touched);

    private static Feed Parse(ReadOnlySpan<byte> b, bool names)
    {
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(b), Is.EqualTo(SystemTicksFeed.Magic), "magic");
        Assert.That(Encoding.ASCII.GetString(b[..4]), Is.EqualTo("TSYT"), "the magic's bytes spell TSYT");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(b[4..]);
        var systems = BinaryPrimitives.ReadUInt16LittleEndian(b[6..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(b[8..]);
        var first = BinaryPrimitives.ReadInt64LittleEndian(b[12..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(b[20..]);
        var workers = BinaryPrimitives.ReadInt32LittleEndian(b[24..]);
        var at = SystemTicksFeed.HeaderBytes;
        var read = new string[names ? systems : 0];
        for (var i = 0; i < read.Length; i++)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
            read[i] = Encoding.UTF8.GetString(b.Slice(at + 2, length));
            at += 2 + length;
        }

        var ticks = new List<(long, float, Sys[])>();
        for (var t = 0; t < count; t++)
        {
            var number = BinaryPrimitives.ReadInt64LittleEndian(b[at..]);
            var wall = BinaryPrimitives.ReadSingleLittleEndian(b[(at + 8)..]);
            at += SystemTicksFeed.TickBytes;
            var row = new Sys[systems];
            for (var s = 0; s < systems; s++)
            {
                row[s] = new Sys(
                    BinaryPrimitives.ReadSingleLittleEndian(b[at..]),
                    BinaryPrimitives.ReadSingleLittleEndian(b[(at + 4)..]),
                    b[at + 8],
                    b[at + 9]);
                at += SystemTicksFeed.SystemBytes;
            }

            ticks.Add((number, wall, row));
        }

        Assert.That(at, Is.EqualTo(b.Length), "the body is exactly what its header says, no more, no less");
        return new Feed(version, flags, first, workers, read, ticks);
    }

    /// <summary>A ring of three systems — parallel, serial, skipped — over <paramref name="ticks"/> ticks, each tick's wall time its number + 0.5 ms.</summary>
    private static TickTelemetryRing Ring(int capacity, int ticks)
    {
        var ring = new TickTelemetryRing(capacity, 3);
        for (var t = 0; t < ticks; t++)
        {
            var tick = new TickTelemetry { TickNumber = t, ActualDurationMs = t + 0.5f };
            ReadOnlySpan<SystemTelemetry> systems =
            [
                new SystemTelemetry { SystemIndex = 0, DurationUs = 100f + t, WorkUs = 300f + (3 * t), WorkersTouched = 3 },
                new SystemTelemetry { SystemIndex = 1, DurationUs = 50f, WorkUs = 0f, WorkersTouched = 1 },
                new SystemTelemetry { SystemIndex = 2, DurationUs = 9f, SkipReason = SkipReason.EmptyInput },
            ];
            ring.Record(in tick, systems);
        }

        return ring;
    }

    private static readonly string[] Names = ["Move", "Économie", "Skipped"];

    private static Feed Read(TickTelemetryRing ring, long after, bool names, string[] systemNames = null)
    {
        systemNames ??= Names;
        var buffer = new byte[SystemTicksFeed.MaxBytes(systemNames, SystemTicksFeed.TicksAfter(ring, after), names)];
        var length = SystemTicksFeed.Write(buffer, ring, systemNames, 16, after, names);
        return Parse(buffer.AsSpan(0, length), names);
    }

    /// <summary>
    /// Every field read back: the header, the names (UTF-8, one of them accented), and per tick its number, wall time and each system's CPU — the chunks'
    /// work for the parallel one, the span for the serial one — its wall, its effective cores × 10 and the workers it touched; the skipped system zeros.
    /// </summary>
    [Test]
    public void TheLayout_RoundTrips_FieldForField()
    {
        var feed = Read(Ring(8, 5), -1, names: true);
        Assert.Multiple(() =>
        {
            Assert.That((feed.Version, feed.Flags, feed.FirstTick, feed.Workers), Is.EqualTo((SystemTicksFeed.Version, (ushort)0, 0L, 16)));
            Assert.That(feed.Names, Is.EqualTo(Names));
            Assert.That(feed.Ticks.ConvertAll(t => t.Tick), Is.EqualTo(new long[] { 0, 1, 2, 3, 4 }));
            var (_, wall, systems) = feed.Ticks[2];
            Assert.That(wall, Is.EqualTo(2.5f));
            Assert.That(systems[0], Is.EqualTo(new Sys(306f, 102f, 30, 3)), "parallel: CPU is the chunks' work, cores ≈ 306 / 102 = 3.0");
            Assert.That(systems[1], Is.EqualTo(new Sys(50f, 50f, 10, 1)), "serial: CPU is its span, one core");
            Assert.That(systems[2], Is.EqualTo(default(Sys)), "skipped: zeros");
        });
    }

    /// <summary>
    /// <c>after</c> clamped: at the newest tick, nothing, and the next tick named as the first; past the oldest the ring still holds, the gap flag and the
    /// oldest held — a reader away for longer than the ring must not interpolate; exactly at the oldest less one, no gap.
    /// </summary>
    [Test]
    public void After_IsClamped_AndAnEvictedRangeSetsTheGap()
    {
        var full = Read(Ring(8, 5), 4, names: false);
        var evicted = Ring(8, 20);
        var gap = Read(evicted, 3, names: false);
        var edge = Read(evicted, 11, names: false);
        var fresh = Read(evicted, -1, names: false);
        Assert.Multiple(() =>
        {
            Assert.That((full.Ticks.Count, full.FirstTick, full.Flags), Is.EqualTo((0, 5L, (ushort)0)), "at the newest: nothing new");
            Assert.That(evicted.OldestAvailableTick, Is.EqualTo(12), "the premise: ticks 0-11 evicted");
            Assert.That((gap.Flags, gap.FirstTick, gap.Ticks.Count), Is.EqualTo((SystemTicksFeed.FlagGap, 12L, 8)), "ticks 4-11 lost: the gap, from 12");
            Assert.That((edge.Flags, edge.FirstTick, edge.Ticks.Count), Is.EqualTo(((ushort)0, 12L, 8)), "nothing lost after 11");
            Assert.That((fresh.Flags, fresh.FirstTick), Is.EqualTo(((ushort)0, 12L)), "no `after`: the oldest held, and no gap claimed");
            Assert.That(Read(new TickTelemetryRing(8, 3), -1, names: false).Ticks, Is.Empty, "an empty ring: an empty answer, not a throw");
        });
    }

    /// <summary>A reader far behind gets <see cref="SystemTicksFeed.MaxTicks"/> of the oldest, and asks again.</summary>
    [Test]
    public void OneAnswer_CarriesAtMostMaxTicks()
    {
        var feed = Read(Ring(2048, 1500), -1, names: false);
        Assert.That((feed.Ticks.Count, feed.Ticks[^1].Tick), Is.EqualTo((SystemTicksFeed.MaxTicks, (long)SystemTicksFeed.MaxTicks - 1)));
    }

    /// <summary>AC-1: forty systems at 10 Hz polled every 2 s — twenty ticks — is at most 10 KB.</summary>
    [Test]
    public void ATwoSecondPoll_OfFortySystems_IsUnderTenKilobytes()
    {
        var names = new string[40];
        Array.Fill(names, "SomeSystemName");
        Assert.That(SystemTicksFeed.MaxBytes(names, 20, withNames: false), Is.LessThanOrEqualTo(10 * 1024));
    }

    /// <summary>
    /// The endpoint over a real runtime, stopped so its ring is frozen: the octet-stream body, one system per scheduler system with its name, every tick
    /// recorded, the scheduler's worker count; <c>after</c> at the newest answers nothing.
    /// </summary>
    [Test]
    public async Task TheEndpoint_AnswersTheRuntimesRing()
    {
        using var engine = BuildEngine(out var provider, out var scope);
        using (provider)
        using (scope)
        {
            using var runtime = StoppedRuntime(engine);
            using var host = await StartAsync(runtime).ConfigureAwait(false);
            var client = host.GetTestClient();
            var response = await client.GetAsync("/typhon/systems/ticks?names=1").ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var feed = Parse(body, names: true);
            var ring = runtime.Telemetry;
            var expected = new string[runtime.Scheduler.AllSystemCount];
            for (var i = 0; i < expected.Length; i++)
            {
                expected[i] = runtime.Scheduler.Systems[i]?.Name ?? string.Empty;
            }

            var newest = await client.GetByteArrayAsync($"/typhon/systems/ticks?after={ring.NewestTick}").ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
                Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
                Assert.That(feed.Names, Is.EqualTo(expected), "one system per scheduler system (AllSystemCount), by index");
                Assert.That(feed.Ticks, Has.Count.EqualTo((int)ring.TotalTicksRecorded));
                Assert.That(feed.Workers, Is.EqualTo(runtime.Scheduler.WorkerCount));
                Assert.That(Parse(newest, names: false).Ticks, Is.Empty, "after the newest: nothing");
            });
        }
    }

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
                o.DatabaseName = "system-ticks";
                o.DatabaseDirectory = _databaseDir;
                o.DatabaseCacheSize = 64UL * 1024 * 1024;
            })
            .AddScopedDatabaseEngine();

        provider = services.BuildServiceProvider();
        scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        engine.InitializeArchetypes();
        return engine;
    }

    private static TyphonRuntime StoppedRuntime(DatabaseEngine engine)
    {
        var runtime = TyphonRuntime.Create(engine, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Probe").CallbackSystem("Probe", _ => { });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        var ring = runtime.Telemetry;
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
                    app.UseEndpoints(endpoints => endpoints.MapTyphonSystemTicks());
                }))
            .StartAsync()
            .ConfigureAwait(false);
}
