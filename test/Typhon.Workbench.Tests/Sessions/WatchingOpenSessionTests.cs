using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// Live attach as a capability of a database session — the unified mode.
/// </summary>
/// <remarks>
/// <para>
/// The pairing: your application holds the database, so the session opened <b>paused</b> (#621). From there it watches
/// the application's live profiler, and when the application exits the coordinator promotes the session to a real open —
/// at which point the database and the capture the engine wrote into its own <c>profilings/</c> are both available.
/// </para>
/// <para>
/// The property these tests pin is that <b>kind is not the question</b>. An <see cref="OpenSession"/> that is watching
/// serves the profiler exactly as an <see cref="AttachSession"/> does, because #617 moved that decision onto
/// <see cref="SessionCapability"/> precisely so a capability could be acquired and released while the kind never changes.
/// </para>
/// </remarks>
[TestFixture]
public sealed class WatchingOpenSessionTests
{
    private static CancellationToken Timeout10s => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static OpenSession PausedSession(string profilerEndpoint) =>
        new(Guid.NewGuid(), @"C:\nowhere\world.typhon",
            new DatabaseHolder(4242, Environment.MachineName, DateTimeOffset.UtcNow, profilerEndpoint), []);

    [Test]
    public void APausedSessionWithNoLiveRuntime_DoesNotClaimTheProfilerCapability()
    {
        var session = PausedSession("localhost:9100");
        Assert.Multiple(() =>
        {
            Assert.That(session.IsPaused, Is.True);
            Assert.That(session.IsWatchingLive, Is.False);
            Assert.That(session.LiveRuntime, Is.Null);
            Assert.That(session.Capabilities, Does.Not.Contain(SessionCapability.Profiler),
                "advertising a profiler it is not connected to would light up panels with nothing behind them");
        });
    }

    [Test]
    public async Task WatchingAPausedSession_AcquiresTheProfilerCapability()
    {
        await using var server = new MockTcpProfilerServer { Scripted = true };
        server.Start();

        var session = PausedSession($"127.0.0.1:{server.Port}");
        var runtime = await AttachSessionRuntime.StartAsync(
            session.Id, $"127.0.0.1:{server.Port}", NullLogger.Instance, Timeout10s, CaptureMode.CherryPick);
        session.StartWatching(runtime);

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(session.IsWatchingLive, Is.True);
                Assert.That(session.Capabilities, Contains.Item(SessionCapability.Profiler),
                    "a watching database session profiles, and panels ask for the capability rather than the kind");
                Assert.That(session.Kind, Is.EqualTo(SessionKind.Open), "and it is still an Open session — the kind never changes");
            });

            // The seam every profiler handler now resolves through.
            Assert.That(session, Is.InstanceOf<ILiveProfilerHost>());
            Assert.That(((ILiveProfilerHost)session).LiveRuntime, Is.SameAs(runtime));
        }
        finally
        {
            session.Dispose();
        }
    }

    [Test]
    public async Task StopWatching_ReleasesTheCapability_AndIsIdempotent()
    {
        await using var server = new MockTcpProfilerServer { Scripted = true };
        server.Start();

        var session = PausedSession($"127.0.0.1:{server.Port}");
        session.StartWatching(await AttachSessionRuntime.StartAsync(
            session.Id, $"127.0.0.1:{server.Port}", NullLogger.Instance, Timeout10s, CaptureMode.CherryPick));

        Assert.That(session.StopWatching(), Is.True, "the first stop had something to stop");
        Assert.Multiple(() =>
        {
            Assert.That(session.IsWatchingLive, Is.False);
            Assert.That(session.Capabilities, Does.Not.Contain(SessionCapability.Profiler));
            Assert.That(session.StopWatching(), Is.False, "stopping twice is a no-op, not an error");
        });
        session.Dispose();
    }

    /// <summary>
    /// A watching database session auto-saves its recorded window INTO that database's <c>profilings/</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This assertion is inverted from what it originally said</b>, and the original was built on a defect. It read
    /// "a database session already gets a complete capture from the engine, so a second copy is redundant" — true only
    /// because a live port had been made to force a default file destination. That forcing was itself the bug: it wrote
    /// 25.84 MB beside the database while the operator had deliberately recorded a 1.04 MB window, which is the exact
    /// opposite of what on-demand capture is for.
    /// </para>
    /// <para>
    /// With the engine writing nothing for a live-only run, the recorded window is the <b>only</b> artifact. Suppressing
    /// its save would silently discard the very ticks the operator armed, and writing it to local-app-data would hide it
    /// from the Profiles list. It belongs beside the database it was recorded from.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AWatchingDatabaseSession_AutoSavesIntoTheDatabasesProfilingsDirectory()
    {
        await using var server = new MockTcpProfilerServer { Scripted = true };
        server.Start();

        var session = PausedSession($"127.0.0.1:{server.Port}");
        var runtime = await AttachSessionRuntime.StartAsync(
            session.Id, $"127.0.0.1:{server.Port}", NullLogger.Instance, Timeout10s, CaptureMode.CherryPick);

        Assert.That(runtime.AutoSaveDirectory, Is.Null, "an unattached runtime has no database, so it falls back to the captures directory");
        session.StartWatching(runtime);

        Assert.Multiple(() =>
        {
            Assert.That(runtime.SuppressAutoSave, Is.False, "the recorded window is the only artifact — nothing else would preserve it");
            Assert.That(runtime.AutoSaveDirectory, Is.EqualTo(TraceLocation.ProfilingsDirectoryOf(session.FilePath)),
                "and it must land where the Profiles list looks, not in local-app-data");
        });

        session.Dispose();
    }

    [Test]
    public void AHolderThatAdvertisesNoProfiler_IsNotWatchable()
    {
        var withEndpoint = new DatabaseHolder(1, "HOST", DateTimeOffset.UtcNow, "localhost:9100");
        var without = new DatabaseHolder(1, "HOST", DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(withEndpoint.IsWatchable, Is.True);
            Assert.That(without.IsWatchable, Is.False, "an application running without a live port offers nothing to watch");
            Assert.That(without.ProfilerEndpoint, Is.Null);
        });
    }
}
