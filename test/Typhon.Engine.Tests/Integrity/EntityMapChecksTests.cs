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

    [Test]
    [CancelAfter(30_000)]
    public void TheEntryComparisonDeclaresItselfUnrun()
    {
        // MAP-01/02 are withheld: the bucket walk recovers only part of a healthy map's entries, so comparing against
        // the cluster would report live entities as unreachable. A check that cannot run must say so.
        BuildHealthyDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(string.Join("\n", report.Limits.ChecksSkipped), Does.Contain("CHK-MAP-01"),
            IntegrityReportText.Render(report));
    }
}
