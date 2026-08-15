using System.IO;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.internals;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Feature #616 (F3) AC1–AC4 + AC6 — the one decision that puts a capture in its database's <c>profilings/</c>, and everything it must leave alone.
/// </summary>
/// <remarks>
/// This is the seam where D-1 actually takes effect, so it is worth testing directly rather than only through the pure-filesystem helpers: the interesting
/// behaviour is <i>when</i> the default applies, and every case where it must not.
/// </remarks>
[TestFixture]
[NonParallelizable]
class CaptureDestinationTests : TestBase<CaptureDestinationTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static TyphonRuntime CreateIdleRuntime(DatabaseEngine dbe) =>
        TyphonRuntime.Create(dbe, schedule => schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", static _ => { }),
            new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

    // ── AC1 · the default lands in the bundle ────────────────────────────────────────────────────────────────

    [Test]
    public void NoDestinationChosen_WritesIntoTheDatabasesProfilingsDirectory()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateIdleRuntime(dbe);
        var bundle = dbe.MMF.BundleDirectory;

        var resolved = ProfilerBootstrap.ApplyDefaultCaptureDestination(runtime, new ProfilerLaunchConfig());

        Assert.Multiple(() =>
        {
            Assert.That(resolved.TraceFilePath, Is.Not.Null);
            Assert.That(Path.GetDirectoryName(resolved.TraceFilePath), Is.EqualTo(TraceLocation.ProfilingsDirectoryOf(bundle)));
            Assert.That(Directory.Exists(TraceLocation.ProfilingsDirectoryOf(bundle)), Is.True, "created on demand");
            // The property the whole decision exists for: given the capture, the database is two levels up. No fingerprint, no inference.
            Assert.That(Path.GetDirectoryName(Path.GetDirectoryName(resolved.TraceFilePath)), Is.EqualTo(bundle));
        });
    }

    // ── AC2 · an explicit path is untouched ──────────────────────────────────────────────────────────────────

    [Test]
    public void ExplicitTracePath_IsHonouredUnchanged()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateIdleRuntime(dbe);
        var explicitPath = Path.Combine(Path.GetTempPath(), "somewhere-else.typhon-trace");

        var resolved = ProfilerBootstrap.ApplyDefaultCaptureDestination(runtime, new ProfilerLaunchConfig { TraceFilePath = explicitPath });

        Assert.That(resolved.TraceFilePath, Is.EqualTo(explicitPath),
            "existing configuration must not change meaning — anyone who set a path already told us where they want it");
    }

    // ── AC3 · a live port asks to WATCH, and writes nothing on its own ───────────────────────────────────────

    /// <summary>
    /// A configured live port suppresses the default file destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Live</c> and <c>Trace</c> are independent output channels, so asking for one must not silently produce the
    /// other. This assertion was briefly inverted during #805 on the reasoning that "a TCP stream leaves nothing
    /// behind, so the file is complementary". That reasoning ignored what the feature above it is for.
    /// </para>
    /// <para>
    /// On-demand tick capture exists so an operator can record a chosen window and <b>store nothing else</b> — the
    /// engine keeps emitting every tick over the wire and the Workbench discards what was not armed. A default file
    /// destination here defeats that completely and invisibly. Measured on a 2,000-entity shard while recording two
    /// 100-tick windows: <b>25.84 MB written beside the database against a 1.04 MB captured window.</b> The user's
    /// requirement was explicit — "receive the data and ignore it, but DON'T STORE it" — and a tool does not get to
    /// decide otherwise on their behalf.
    /// </para>
    /// </remarks>
    [Test]
    public void LivePort_SuppressesTheDefaultFileDestination()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateIdleRuntime(dbe);

        var resolved = ProfilerBootstrap.ApplyDefaultCaptureDestination(runtime, new ProfilerLaunchConfig { LivePort = 9100 });

        Assert.Multiple(() =>
        {
            Assert.That(resolved.LivePort, Is.EqualTo(9100), "the live port must survive untouched");
            Assert.That(resolved.TraceFilePath, Is.Null,
                "asking to watch must not write a complete capture of the whole run — that is exactly what cherry-picked capture exists to avoid");
        });
    }

    /// <summary>
    /// Wanting both channels is one setting away: an explicit path is honoured alongside a live port, so a user who
    /// wants the post-mortem file (and CPU sampling, which is file-mode only) says so and gets it.
    /// </summary>
    [Test]
    public void LiveSessionWithAnExplicitPath_KeepsThatPath()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateIdleRuntime(dbe);
        var explicitPath = Path.Combine(Path.GetTempPath(), "live-and-explicit.typhon-trace");

        var resolved = ProfilerBootstrap.ApplyDefaultCaptureDestination(
            runtime, new ProfilerLaunchConfig { LivePort = 9100, TraceFilePath = explicitPath });

        Assert.That(resolved.TraceFilePath, Is.EqualTo(explicitPath));
    }

    // ── AC4 · no engine, no invention ────────────────────────────────────────────────────────────────────────

    [Test]
    public void NoRuntime_IsANoOp()
    {
        // Standalone profiling (Typhon.IOProfileRunner, the exporter integration tests) has no database to co-locate with.
        var resolved = ProfilerBootstrap.ApplyDefaultCaptureDestination(null, new ProfilerLaunchConfig());

        Assert.That(resolved.TraceFilePath, Is.Null, "with no bundle there is nowhere structural to put a capture — better none than one invented somewhere");
    }

    // ── AC6 · retention runs at capture start ────────────────────────────────────────────────────────────────

    [Test]
    public void RetentionIsEnforcedBeforeTheNewCaptureIsCreated()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateIdleRuntime(dbe);
        var profilings = TraceLocation.ProfilingsDirectoryOf(dbe.MMF.BundleDirectory);
        Directory.CreateDirectory(profilings);

        // A pre-existing capture far over a deliberately tiny budget.
        var stale = Path.Combine(profilings, "20260101-000000-000.typhon-trace");
        File.WriteAllBytes(stale, new byte[4096]);
        new RetentionPolicy { BudgetBytes = 512, KeepLatest = 0 }.Write(profilings);

        var resolved = ProfilerBootstrap.ApplyDefaultCaptureDestination(runtime, new ProfilerLaunchConfig());

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(stale), Is.False, "pruning runs at capture start, with no Workbench involved — that is what makes it work headless");
            Assert.That(File.Exists(resolved.TraceFilePath), Is.False,
                "…and it runs BEFORE the new file exists, so the capture about to be written is never a candidate for its own eviction");
        });
    }

    [Test]
    public void AnUnreadableRetentionFile_DoesNotStopTheCapture()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateIdleRuntime(dbe);
        var profilings = TraceLocation.ProfilingsDirectoryOf(dbe.MMF.BundleDirectory);
        Directory.CreateDirectory(profilings);
        File.WriteAllText(Path.Combine(profilings, RetentionPolicy.FileName), "{{{ not json");

        var resolved = ProfilerBootstrap.ApplyDefaultCaptureDestination(runtime, new ProfilerLaunchConfig());

        Assert.That(resolved.TraceFilePath, Is.Not.Null,
            "the capture is the valuable thing; a hand-edited policy file falls back to defaults rather than costing a profiling session");
    }
}
