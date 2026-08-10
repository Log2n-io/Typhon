using NUnit.Framework;
using System;
using System.Linq;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// <c>CHN-06</c> and <c>CLU-04</c> — the two checks that need more than one structure to have been walked.
/// </summary>
/// <remarks>
/// Both exist to catch a database where every individual structure is well-formed. That is the class of damage the
/// whole feature is for, and the class a per-structure walk cannot reach by construction: a stranded revision chain is
/// a perfectly good chain, and a component count that two records disagree on is two perfectly good records.
/// </remarks>
[TestFixture]
internal sealed class CrossStructureChecksTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void AHealthyDatabaseDrawsNeitherFinding()
    {
        BuildHealthyDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(report.Findings.Where(f => f.Code is "CHK-CHN-06" or "CHK-CLU-04"), Is.Empty,
            "the cross-structure checks fired on an undamaged database:\n" + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// Both checks actually run, rather than quietly declaring themselves unavailable.
    /// </summary>
    /// <remarks>
    /// A check that skips is indistinguishable in a report from a check that passes unless something asserts on the
    /// skip list — and both of these depend on a chain of derivations (component names, storage modes, record size) any
    /// link of which would make them stand down.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void BothChecksRun()
    {
        BuildHealthyDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        var skipped = string.Join("\n", report.Limits.ChecksSkipped);

        Assert.That(skipped, Does.Not.Contain("CHK-CHN-06"), IntegrityReportText.Render(report));
        Assert.That(skipped, Does.Not.Contain("CHK-CLU-04"), IntegrityReportText.Render(report));
    }

    /// <summary>
    /// A revision chain nothing references is reported, and reported as reclaimable rather than as loss.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AChainNothingReferencesIsReported()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.OrphanRevisionChain(BundlePath, out var stranded);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.Single(f => f.Code == "CHK-CHN-06");
        Assert.That(finding.Detail, Does.Contain(stranded.ToString()),
            "the finding must name the chain that was stranded, not merely that one was");
        Assert.That(finding.Repair, Is.EqualTo(Repairability.Lossless),
            "the entity's data is intact — it is the reference that is gone, and rebuilding the map restores it");
    }

    /// <summary>
    /// The stranded chain is invisible to every other family, which is the whole reason CHN-06 exists.
    /// </summary>
    /// <remarks>
    /// Asserted explicitly rather than left implied by <c>AssertDetectedExactly</c>: the value of this check is
    /// precisely that no single-structure walk can see the damage, and stating that as its own assertion means a future
    /// change that made <c>CHN-03</c> or <c>MAP-02</c> notice would be visible rather than silently redundant.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void TheStrandedChainIsInvisibleToTheChainAndMapFamilies()
    {
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);
        DamageKit.OrphanRevisionChain(BundlePath, out _);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(report.Findings.Where(f =>
                f.Code.StartsWith("CHK-MAP-", StringComparison.Ordinal)
                || (f.Code.StartsWith("CHK-CHN-", StringComparison.Ordinal) && f.Code != "CHK-CHN-06")),
            Is.Empty,
            "a stranded chain leaves both structures individually well-formed; if another family reports it, this "
            + "fixture is no longer isolating what it claims:\n" + IntegrityReportText.Render(report));
    }
}
