using System;
using System.IO;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Feature #616 (F3) — where captures live (design D-1) and the shape of the policy that bounds them (D-6).
/// </summary>
[TestFixture]
public sealed class TraceLocationAndPolicyTests
{
    private string _bundle;

    [SetUp]
    public void SetUp()
    {
        _bundle = Path.Combine(Path.GetTempPath(), "typhon-tracelocation", Guid.NewGuid().ToString("N") + ".typhon");
        Directory.CreateDirectory(_bundle);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_bundle))
            {
                Directory.Delete(_bundle, recursive: true);
            }
        }
        catch { /* best-effort cleanup */ }
    }

    // ── AC1 · the layout ─────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void NewCapturePath_LandsInTheBundlesProfilingsDirectory_AndCreatesIt()
    {
        var profilings = TraceLocation.ProfilingsDirectoryOf(_bundle);
        Assert.That(Directory.Exists(profilings), Is.False, "precondition: the directory does not exist yet");

        var path = TraceLocation.NewCapturePath(_bundle, new DateTime(2026, 7, 31, 17, 45, 12, 345, DateTimeKind.Utc));

        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(profilings), Is.True, "created on demand — a host should not have to pre-make it");
            Assert.That(Path.GetDirectoryName(path), Is.EqualTo(profilings));
            Assert.That(Path.GetFileName(path), Is.EqualTo("20260731-174512-345.typhon-trace"));
            // This is what makes a directory listing already the right order for a profiles list, with nothing to parse.
            Assert.That(Path.GetFileName(path), Is.GreaterThan("20260731-174512-344.typhon-trace"), "timestamp names sort chronologically as text");
        });
    }

    [Test]
    public void TheDatabaseIsTwoLevelsUpFromACapture()
    {
        var path = TraceLocation.NewCapturePath(_bundle);

        // The entire point of D-1: correlation is structural. No fingerprint, no confidence badge — just the path.
        var bundleFromCapture = Path.GetDirectoryName(Path.GetDirectoryName(path));

        Assert.That(bundleFromCapture, Is.EqualTo(_bundle));
    }

    [Test]
    public void SidecarNaming_MatchesTheCacheBuildersOwnConvention()
    {
        var capture = Path.Combine(_bundle, "x.typhon-trace");

        // Delegated rather than re-derived: a pruner that computed the name a second way would silently stop finding sidecars the day the convention moved.
        Assert.That(TraceLocation.SidecarOf(capture), Is.EqualTo(Typhon.Profiler.TraceFileCacheBuilder.GetCachePathFor(capture)));
        Assert.That(TraceLocation.CaptureOfSidecar(TraceLocation.SidecarOf(capture)), Is.EqualTo(capture), "and the inverse round-trips");
    }

    [Test]
    public void IsCapture_DistinguishesCapturesFromSidecars()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TraceLocation.IsCapture("a.typhon-trace"), Is.True);
            Assert.That(TraceLocation.IsCapture("a.typhon-trace-cache"), Is.False, "the sidecar is derived data, not a capture");
            Assert.That(TraceLocation.IsCapture("retention.json"), Is.False);
            Assert.That(TraceLocation.IsCapture(null), Is.False);
        });
    }

    // ── AC5 · the policy file ────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Policy_RoundTripsThroughTheBundle()
    {
        var profilings = TraceLocation.ProfilingsDirectoryOf(_bundle);
        var written = new RetentionPolicy { BudgetBytes = 12_345, KeepLatest = 3, Pinned = ["keep-me.typhon-trace"] };
        written.Write(profilings);

        var read = RetentionPolicy.Read(profilings, out var reason);

        Assert.Multiple(() =>
        {
            Assert.That(reason, Is.Null);
            Assert.That(read.BudgetBytes, Is.EqualTo(12_345));
            Assert.That(read.KeepLatest, Is.EqualTo(3));
            Assert.That(read.Pinned, Is.EquivalentTo(new[] { "keep-me.typhon-trace" }));
            Assert.That(read.IsPinned("KEEP-ME.TYPHON-TRACE"), Is.True, "pins match case-insensitively — these are file names on mostly case-insensitive volumes");
            Assert.That(read.IsPinned("other.typhon-trace"), Is.False);
        });
    }

    [Test]
    public void MissingPolicyFile_YieldsTheDefaults_Silently()
    {
        var read = RetentionPolicy.Read(TraceLocation.ProfilingsDirectoryOf(_bundle), out var reason);

        Assert.Multiple(() =>
        {
            Assert.That(reason, Is.Null, "a database that has never been configured is the normal case, not a problem to report");
            Assert.That(read.BudgetBytes, Is.EqualTo(RetentionPolicy.DefaultBudgetBytes));
            Assert.That(read.KeepLatest, Is.EqualTo(RetentionPolicy.DefaultKeepLatest));
            Assert.That(read.Pinned, Is.Empty);
        });
    }

    [Test]
    public void CorruptPolicyFile_YieldsTheDefaults_WithAReason_AndNeverThrows()
    {
        var profilings = TraceLocation.ProfilingsDirectoryOf(_bundle);
        Directory.CreateDirectory(profilings);
        File.WriteAllText(Path.Combine(profilings, RetentionPolicy.FileName), "{ this is not json");

        RetentionPolicy read = null;
        Assert.DoesNotThrow(() => read = RetentionPolicy.Read(profilings, out _),
            "a hand-edited retention file must not stop a profiling session from starting — the capture is the valuable thing");

        RetentionPolicy.Read(profilings, out var reason);
        Assert.That(read.BudgetBytes, Is.EqualTo(RetentionPolicy.DefaultBudgetBytes));
        Assert.That(reason, Is.Not.Null.And.Not.Empty, "…but the fallback is reported, so it is not silently mysterious");
    }

    [Test]
    public void EmptyPolicyFile_YieldsTheDefaults_WithAReason()
    {
        var profilings = TraceLocation.ProfilingsDirectoryOf(_bundle);
        Directory.CreateDirectory(profilings);
        File.WriteAllText(Path.Combine(profilings, RetentionPolicy.FileName), "   ");

        var read = RetentionPolicy.Read(profilings, out var reason);

        Assert.That(read.BudgetBytes, Is.EqualTo(RetentionPolicy.DefaultBudgetBytes));
        Assert.That(reason, Is.Not.Null);
    }

    [Test]
    public void NegativeKeepLatest_IsNormalisedRatherThanRejected()
    {
        var profilings = TraceLocation.ProfilingsDirectoryOf(_bundle);
        Directory.CreateDirectory(profilings);
        File.WriteAllText(Path.Combine(profilings, RetentionPolicy.FileName), "{\"budgetBytes\":100,\"keepLatest\":-5}");

        var read = RetentionPolicy.Read(profilings, out _);

        Assert.That(read.KeepLatest, Is.Zero, "a hand-edited negative floor means 'keep none', not 'fail the capture'");
        Assert.That(read.BudgetBytes, Is.EqualTo(100), "…and the rest of the file is still honoured");
    }
}
