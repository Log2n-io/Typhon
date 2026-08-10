using NUnit.Framework;
using System;
using System.Linq;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// The <c>MAP</c> family: the directory walk survives a damaged pointer and reports it, and stays quiet when clean.
/// </summary>
/// <remarks>
/// <c>MAP-04</c> is the check the catalogue calls "a hard requirement on the traversal code, not merely a finding" —
/// so the load-bearing assertion in the damage case is that the scan <b>completes</b>, not merely that it reports.
/// </remarks>
[TestFixture]
internal sealed class EntityMapChecksTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void AHealthyDatabaseDrawsNoEntityMapFinding()
    {
        BuildHealthyDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(report.Findings.Where(f => f.Code.StartsWith("CHK-MAP-", StringComparison.Ordinal)), Is.Empty,
            "the entity-map family fired on an undamaged database:\n" + IntegrityReportText.Render(report));
    }

    [Test]
    [CancelAfter(30_000)]
    public void ADirectorySlotPointingOutsideTheSegmentIsReportedAndNotFollowed()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.RedirectEntityMapDirectorySlot(BundlePath);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.Single(f => f.Code == "CHK-MAP-04");
        Assert.That(finding.RuleId, Is.EqualTo("RB-01"));
        Assert.That(finding.Repair, Is.EqualTo(Repairability.Lossless),
            "an EntityMap is derived from the cluster, so rebuilding it costs nothing");
        Assert.That(finding.Detail, Does.Contain("NOT followed"),
            "the finding must state that the damaged pointer was not dereferenced — that is MAP-04's actual requirement");
    }

    /// <summary>
    /// The walk recovers <b>every</b> entry of a healthy map — the property MAP-02 is worthless without.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion whose absence cost two rounds of debugging. An earlier walk returned a strict subset of a
    /// healthy map and there was no test that could tell: <c>MAP-01</c> passes trivially on a subset (everything found
    /// is real), <c>MAP-03</c> passes (fewer entries cannot collide), <c>MAP-04</c> passes (no pointer was bad). Only
    /// counting against a known population sees it, and the count has to come from outside the walk.
    /// </para>
    /// <para>
    /// The cause was not the bucket layout it was blamed on. The cursor handed back a span over one reused page buffer,
    /// and the nested bucket reads rewrote the directory the outer loop was still iterating — so 27 of 256 buckets were
    /// visited and four keys of sixty-four came back.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void EveryLiveEntityIsFoundInTheMap()
    {
        const int Entities = 64;
        BuildHealthyDatabase(Entities);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(report.Limits.ChecksSkipped.Any(s => s.Contains("CHK-MAP-01", StringComparison.Ordinal)), Is.False,
            "MAP-01/02 must actually run:\n" + IntegrityReportText.Render(report));

        // MAP-02 fires when a live cluster entity has no map entry. On a healthy database of known population, its
        // silence IS the statement that all 64 were found — a walk that lost even one would report it.
        Assert.That(report.Findings.Where(f => f.Code is "CHK-MAP-01" or "CHK-MAP-02"), Is.Empty,
            $"all {Entities} entities must be reachable through the map:\n" + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// An entity missing from the cluster is reported as an orphaned map entry.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AnEntryForAnEntityTheClusterLostIsReported()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        // Zeroing a live slot's key removes that identity from the cluster while leaving the map's entry for it — the
        // exact shape MAP-01 exists to name.
        var damage = DamageKit.BreakClusterSlot(BundlePath, DamageKit.ClusterBreak.ClearLiveKey);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.First(f => f.Code == "CHK-MAP-01");
        Assert.That(finding.Repair, Is.EqualTo(Repairability.Lossless),
            "the map is derived from the cluster, so rebuilding it loses nothing");
    }

    /// <summary>
    /// A live entity absent from the map is reported — the reverse direction, which forward-only checking cannot see.
    /// </summary>
    /// <remarks>
    /// Both directions are required and shipping only <c>MAP-01</c> is the classic mistake
    /// (<c>03 §7</c>): a map missing half its entries satisfies forward-only checking completely, and that is precisely
    /// what a rebuild over pre-apply state produces — <c>RB-02</c>'s failure mode.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void ALiveEntityAbsentFromTheMapIsReported()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        // Raising a live key past the watermark gives the cluster an identity the map has never heard of, and strands
        // the map's entry for the old one: the disagreement appears in both directions at once.
        var damage = DamageKit.BreakClusterSlot(BundlePath, DamageKit.ClusterBreak.KeyAboveWatermark);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.First(f => f.Code == "CHK-MAP-02");
        Assert.That(finding.RuleId, Is.EqualTo("RB-02"));
        Assert.That(finding.Detail, Does.Contain("unfindable"),
            "the finding must say what the operator loses: the entity is present but not reachable by id");
    }
}
