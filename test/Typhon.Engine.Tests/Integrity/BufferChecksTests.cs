using NUnit.Framework;
using System.Linq;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// <c>ALO-04</c> — the variable-sized buffer handle table, in both directions.
/// </summary>
/// <remarks>
/// The two directions are not two views of one thing. A dangling handle is data loss with no derived copy to rebuild
/// from; a stranded buffer loses nothing and is never reclaimed. Only the second is <b>#389</b>'s shape, and it is the
/// one no walk of a single structure can see — the buffer is well-formed, the row that used to name it is well-formed,
/// and the reference between them is gone.
/// </remarks>
[TestFixture]
internal sealed class BufferChecksTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void AHealthyDatabaseDrawsNoHandleFinding()
    {
        BuildHealthyDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(report.Findings.Where(f => f.Code == "CHK-ALO-04"), Is.Empty,
            "the handle check fired on an undamaged database:\n" + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// The check runs on the fixture schema rather than standing down.
    /// </summary>
    /// <remarks>
    /// The reverse half withdraws whenever a user component stores handles in per-entity data, which is correct but
    /// makes "no finding" ambiguous — and the fixture schema declares no collection field, so here it must actually
    /// run. Without this assertion the damage case below could pass for the wrong reason.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void TheHandleCheckRunsOnThisSchema()
    {
        BuildHealthyDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(string.Join("\n", report.Limits.ChecksSkipped), Does.Not.Contain("CHK-ALO-04"),
            IntegrityReportText.Render(report));
    }

    [Test]
    [CancelAfter(30_000)]
    public void AHandleNamingStorageThatIsNotThereIsLoss()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.BreakCollectionHandle(BundlePath, DamageKit.HandleBreak.Dangle, out var owner);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        // Redirecting a handle does two things at once, and the check is right to say both: the new target does not
        // exist, AND the buffer that used to be named is now referenced by nothing. Asserting only the first would let
        // a check that missed the second look correct.
        var findings = report.Findings.Where(f => f.Code == "CHK-ALO-04").ToArray();
        Assert.That(findings, Has.Length.EqualTo(2), IntegrityReportText.Render(report));

        var dangling = findings.Single(f => f.Severity == IntegritySeverity.DataLoss);
        Assert.That(dangling.Detail, Does.Contain(owner).Or.Contain("Buffer"),
            "the finding must identify whose handle failed to resolve");
        Assert.That(dangling.Repair, Is.EqualTo(Repairability.NotRepairable),
            "a collection is primary data — there is no derived copy to regenerate it from");
        Assert.That(dangling.Loss.Kind, Is.EqualTo(LossKind.Collection));

        var stranded = findings.Single(f => f.Severity == IntegritySeverity.Divergence);
        Assert.That(stranded.Repair, Is.EqualTo(Repairability.Lossless),
            "the abandoned buffer costs space, not data");
    }

    /// <summary>
    /// A buffer nothing references is reported as reclaimable — <b>#389</b>'s shape.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void ABufferNothingReferencesIsReportedAsReclaimable()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.BreakCollectionHandle(BundlePath, DamageKit.HandleBreak.Strand, out _);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.Single(f => f.Code == "CHK-ALO-04");
        Assert.That(finding.Severity, Is.EqualTo(IntegritySeverity.Divergence),
            "nothing is lost — the storage is simply never released");
        Assert.That(finding.Repair, Is.EqualTo(Repairability.Lossless));
        Assert.That(finding.Detail, Does.Contain("#389"),
            "the finding should name the shape it detects, so an operator can find the history behind it");
    }
}
