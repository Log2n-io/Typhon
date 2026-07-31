using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Feature #616 (F3) — captures live in <c>{name}.typhon/profilings/</c> and the writer enforces the database's retention policy there (design D-1 + D-6).
/// </summary>
/// <remarks>
/// Pruning is pure filesystem work over a temp directory, so these run without an engine and are fully deterministic — file ages are set explicitly rather
/// than waited for. The one thing they cannot fake is a *session* holding a capture open, so <see cref="LockedCapture_IsSkippedNotFatal"/> holds a real
/// <see cref="FileStream"/> instead: that is the actual mechanism by which "never evict a capture open in a session" is enforced.
/// </remarks>
[TestFixture]
public sealed class TraceRetentionTests
{
    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "typhon-retention", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch { /* best-effort cleanup */ }
    }

    // ── AC6 · budget enforcement, oldest first ───────────────────────────────────────────────────────────────

    [Test]
    public void PrunesOldestFirst_UntilCapturesFitTheBudget()
    {
        Capture("a", bytes: 100, ageMinutes: 50);   // oldest
        Capture("b", bytes: 100, ageMinutes: 40);
        Capture("c", bytes: 100, ageMinutes: 30);
        Capture("d", bytes: 100, ageMinutes: 20);   // newest

        var report = Prune(budget: 250, keepLatest: 0);

        Assert.Multiple(() =>
        {
            Assert.That(Names(), Is.EquivalentTo(new[] { "c" + Ext, "d" + Ext }), "the two oldest go, in age order");
            Assert.That(report.Evicted, Is.EqualTo(2));
            Assert.That(report.CaptureBytes, Is.EqualTo(200));
            Assert.That(report.CaptureBytes, Is.LessThanOrEqualTo(report.BudgetBytes));
        });
    }

    [Test]
    public void UnderBudget_DeletesNothing()
    {
        Capture("a", bytes: 10, ageMinutes: 50);
        Capture("b", bytes: 10, ageMinutes: 10);

        var report = Prune(budget: 1000, keepLatest: 0);

        Assert.That(Names(), Has.Length.EqualTo(2));
        Assert.That(report.Evicted, Is.Zero);
    }

    [Test]
    public void NonPositiveBudget_DisablesEviction()
    {
        Capture("a", bytes: 10_000, ageMinutes: 50);

        var report = Prune(budget: 0, keepLatest: 0);

        Assert.That(Names(), Has.Length.EqualTo(1), "a budget of 0 means 'no budget', not 'delete everything'");
        Assert.That(report.Evicted, Is.Zero);
    }

    // ── AC7 · pinned counted, never evicted ──────────────────────────────────────────────────────────────────

    [Test]
    public void PinnedCaptures_AreCountedInTheBudget_ButNeverEvicted()
    {
        Capture("pinned", bytes: 400, ageMinutes: 90);   // oldest AND biggest — the first thing eviction would reach for
        Capture("a", bytes: 100, ageMinutes: 50);
        Capture("b", bytes: 100, ageMinutes: 10);

        var report = Prune(budget: 450, keepLatest: 0, pinned: ["pinned" + Ext]);

        Assert.Multiple(() =>
        {
            Assert.That(Names(), Does.Contain("pinned" + Ext), "a pin outranks age and size");
            Assert.That(report.PinnedBytes, Is.EqualTo(400),
                "pinned bytes are reported, not hidden — exempting them from the total would make the budget read '18 of 20 GB' while the disk fills");
            Assert.That(report.CaptureBytes, Is.GreaterThanOrEqualTo(report.PinnedBytes));
            Assert.That(Names(), Does.Not.Contain("a" + Ext), "unpinned captures are still evicted to make what room they can");
        });
    }

    // ── AC8 · pinned alone over budget ───────────────────────────────────────────────────────────────────────

    [Test]
    public void PinnedAloneOverBudget_IsReported()
    {
        Capture("big", bytes: 1000, ageMinutes: 10);

        var report = Prune(budget: 500, keepLatest: 0, pinned: ["big" + Ext]);

        Assert.That(report.PinnedExceedBudget, Is.True,
            "pins that exceed the budget mean the policy can no longer be honoured — a state worth surfacing rather than silently tolerating");
        Assert.That(Names(), Does.Contain("big" + Ext));
    }

    // ── AC9 · sidecars ───────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Sidecars_AreAccountedSeparately_AndNeverCompeteForBudget()
    {
        Capture("a", bytes: 100, ageMinutes: 10);
        Sidecar("a", bytes: 5000);   // a derived cache far larger than the whole budget

        var report = Prune(budget: 200, keepLatest: 0);

        Assert.Multiple(() =>
        {
            Assert.That(Names(), Does.Contain("a" + Ext), "a regenerable sidecar must never cost a real capture its place");
            Assert.That(report.CaptureBytes, Is.EqualTo(100), "the budget counts captures only");
            Assert.That(report.SidecarBytes, Is.EqualTo(5000), "sidecars are reported on their own axis");
        });
    }

    [Test]
    public void OrphanedSidecars_AreReclaimed()
    {
        Sidecar("gone", bytes: 500);   // its capture no longer exists

        var report = Prune(budget: 1_000_000, keepLatest: 0);

        Assert.That(File.Exists(SidecarPath("gone")), Is.False, "a sidecar whose capture is gone is pure waste");
        Assert.That(report.SidecarBytes, Is.Zero);
    }

    [Test]
    public void EvictingACapture_AlsoEvictsItsSidecar()
    {
        Capture("old", bytes: 100, ageMinutes: 90);
        Sidecar("old", bytes: 50);
        Capture("new", bytes: 100, ageMinutes: 10);

        Prune(budget: 150, keepLatest: 0);

        Assert.That(File.Exists(CapturePath("old")), Is.False);
        Assert.That(File.Exists(SidecarPath("old")), Is.False, "leaving the sidecar behind would just make it an orphan for the next pass to find");
    }

    // ── AC10 · a capture in use ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public void LockedCapture_IsSkippedNotFatal()
    {
        Capture("locked", bytes: 100, ageMinutes: 90);   // oldest — eviction reaches it first
        Capture("evictable", bytes: 100, ageMinutes: 60);
        Capture("newest", bytes: 100, ageMinutes: 10);

        // A capture open in a Workbench session looks exactly like this to the writer. There is no registry to consult and nothing to keep in sync — the
        // delete simply fails, which is the whole mechanism behind "never evict a capture currently open".
        // 300 bytes against a 250-byte budget: skipping the locked capture still leaves exactly one eviction needed, so "carried on past the skip" and
        // "stopped as soon as it fit" are both visible in the same numbers.
        using (var held = new FileStream(CapturePath("locked"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var report = Prune(budget: 250, keepLatest: 0);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(CapturePath("locked")), Is.True, "the held capture survives");
                Assert.That(report.Skipped, Is.EqualTo(1));
                Assert.That(File.Exists(CapturePath("evictable")), Is.False, "…and pruning carries on past it rather than aborting");
                Assert.That(File.Exists(CapturePath("newest")), Is.True, "…and stops once the budget is met");
                Assert.That(report.Evicted, Is.EqualTo(1));
            });
            held.Close();
        }
    }

    // ── AC11 · keep-latest floor ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public void KeepLatest_IsAFloor_EvenWhenThatExceedsTheBudget()
    {
        Capture("a", bytes: 100, ageMinutes: 50);
        Capture("b", bytes: 100, ageMinutes: 40);
        Capture("c", bytes: 100, ageMinutes: 30);

        var report = Prune(budget: 50, keepLatest: 2);

        Assert.Multiple(() =>
        {
            Assert.That(Names(), Is.EquivalentTo(new[] { "b" + Ext, "c" + Ext }), "the two newest survive a budget none of them fit");
            Assert.That(report.CaptureBytes, Is.GreaterThan(report.BudgetBytes), "the report says so rather than pretending the budget was met");
        });
    }

    // ── robustness ───────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void MissingDirectory_IsANoOp()
    {
        var absent = Path.Combine(_dir, "does-not-exist");
        Assert.DoesNotThrow(() => TraceRetention.Prune(absent, RetentionPolicy.Default, null));
    }

    [Test]
    public void PolicyFileItself_IsNeverTreatedAsACapture()
    {
        new RetentionPolicy { BudgetBytes = 1, KeepLatest = 0 }.Write(_dir);
        Capture("a", bytes: 10, ageMinutes: 10);

        Prune(budget: 1, keepLatest: 0);

        Assert.That(File.Exists(Path.Combine(_dir, RetentionPolicy.FileName)), Is.True,
            "the policy lives in the directory it governs; pruning must not evict its own configuration");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    private const string Ext = ".typhon-trace";

    private string CapturePath(string stem) => Path.Combine(_dir, stem + Ext);

    private string SidecarPath(string stem) => TraceLocation.SidecarOf(CapturePath(stem));

    /// <summary>Writes a capture of an exact size and age. Ages are stamped, never waited for, so the fixture stays sub-millisecond and deterministic.</summary>
    private void Capture(string stem, int bytes, int ageMinutes)
    {
        var path = CapturePath(stem);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-ageMinutes));
    }

    private void Sidecar(string stem, int bytes) => File.WriteAllBytes(SidecarPath(stem), new byte[bytes]);

    private RetentionReport Prune(long budget, int keepLatest, string[] pinned = null) =>
        TraceRetention.Prune(_dir, new RetentionPolicy { BudgetBytes = budget, KeepLatest = keepLatest, Pinned = pinned ?? [] }, null);

    private string[] Names() =>
        [.. Directory.EnumerateFiles(_dir, "*" + Ext).Where(TraceLocation.IsCapture).Select(Path.GetFileName)];
}
