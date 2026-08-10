using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// The <c>CLU</c> family and the entity-key half of <c>ALO</c>, each against damage that produces exactly it.
/// </summary>
/// <remarks>
/// These are the checks that read a cluster's engine-defined prefix — the occupancy word and the packed entity-key
/// array — at an offset computable only from the archetype's component count. <c>09 §1</c> recorded that value as being
/// nowhere in the file; it is in <c>ArchetypeR1</c>, and these checks are what that correction bought.
/// </remarks>
[TestFixture]
internal sealed class ClusterChecksTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void AHealthyDatabaseDrawsNoClusterFinding()
    {
        BuildHealthyDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(report.Findings.Where(f => f.Code.StartsWith("CHK-CLU-", System.StringComparison.Ordinal)
                                            || f.Code.StartsWith("CHK-ALO-", System.StringComparison.Ordinal)), Is.Empty,
            "the cluster family fired on an undamaged database:\n" + IntegrityReportText.Render(report));

        Assert.That(report.Limits.ChecksSkipped.Any(s => s.Contains("CHK-CLU", System.StringComparison.Ordinal)), Is.False,
            "the family was skipped, so its clean result is not evidence:\n" + IntegrityReportText.Render(report));
    }

    [Test]
    [CancelAfter(30_000)]
    public void AnOccupiedSlotWithNoEntityKeyIsReportedAsLoss()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.BreakClusterSlot(BundlePath, DamageKit.ClusterBreak.ClearLiveKey);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);
        DamageKit.AssertDetectedExactly(DamageKit.Scan(BundlePath, ScanDepth.Deep), damage);
    }

    [Test]
    [CancelAfter(30_000)]
    public void TwoLiveSlotsClaimingOneEntityIsFatal()
    {
        // The #697 shape. Both slots are individually well-formed, which is exactly why nothing but a cross-structure
        // check sees it: every lookup resolves to whichever the index happens to name, and a write through one is
        // invisible through the other.
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.BreakClusterSlot(BundlePath, DamageKit.ClusterBreak.DuplicateKey);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.Single(f => f.Code == "CHK-CLU-02");
        Assert.That(finding.Severity, Is.EqualTo(IntegritySeverity.Fatal));
        Assert.That(finding.RuleId, Is.EqualTo("RB-06"));
    }

    [Test]
    [CancelAfter(30_000)]
    public void AKeyPastTheWatermarkIsReportedFromBothEnds()
    {
        // CLU-05 sees it per entity, ALO-02 per archetype, and both are true at once. Reporting only one would leave an
        // operator to work out on their own whether a single stray key is an allocator fault.
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.BreakClusterSlot(BundlePath, DamageKit.ClusterBreak.KeyAboveWatermark);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        Assert.That(report.Findings.Single(f => f.Code == "CHK-ALO-02").Severity, Is.EqualTo(IntegritySeverity.Fatal),
            "an allocator that will re-issue a live identifier is not a divergence");
    }

    [Test]
    [CancelAfter(30_000)]
    public void TheWatermarkChecksAreSkippedOnACrashPathFile()
    {
        // RB-05 is explicit that the persisted watermarks are refreshed only on clean shutdown, so on a crash-path file
        // they are stale BY DESIGN and recovery has not yet recomputed them. Comparing against them there reports a
        // fatal allocator fault on a database behaving exactly as documented — which is what the G0 crash fixtures
        // caught when these checks first ran unguarded.
        BuildHealthyDatabase();

        using (var provider = ReopenProvider())
        {
            using var scope = provider.CreateScope();
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();
            dbe.SimulateHardCrash();
        }

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(report.Findings.Where(f => f.Code is "CHK-ALO-01" or "CHK-ALO-02" or "CHK-CHN-05" or "CHK-CLU-05"),
            Is.Empty,
            "a watermark check fired on a crash-path file, where the watermark is stale by design:\n"
            + IntegrityReportText.Render(report));

        Assert.That(string.Join("\n", report.Limits.ChecksSkipped), Does.Contain("CHK-ALO-02"),
            "a check that cannot run must be declared, not silently absent:\n" + IntegrityReportText.Render(report));
    }
}
