using NUnit.Framework;
using System.Linq;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// The <c>CHN</c> family: each check against damage that produces exactly it, and nothing against a healthy database.
/// </summary>
/// <remarks>
/// These are the first checks that read <i>inside</i> chunks. They were blocked not by the schema — every field they
/// touch is engine-defined — but by the arithmetic needed to find the next chunk, which format revision 7 supplied.
/// </remarks>
[TestFixture]
internal sealed class ChainChecksTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void AHealthyDatabaseDrawsNoChainFinding()
    {
        // The negative case first, and it is not a formality. A chain walker with an off-by-one in its chunk arithmetic
        // reports plausible findings about every database it meets, and only a clean run over healthy data catches that.
        BuildHealthyDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(report.Findings.Where(f => f.Code.StartsWith("CHK-CHN-", System.StringComparison.Ordinal)), Is.Empty,
            "the chain family fired on an undamaged database:\n" + IntegrityReportText.Render(report));
        Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Sound), IntegrityReportText.Render(report));

        // And it must actually have run, or the assertion above is satisfied by nothing happening.
        Assert.That(report.Limits.ChecksSkipped.Any(s => s.Contains("CHK-CHN", System.StringComparison.Ordinal)), Is.False,
            "the chain family was skipped, so its clean result is not evidence:\n" + IntegrityReportText.Render(report));
    }

    [Test]
    [CancelAfter(30_000)]
    public void ADanglingChainPointerIsReportedAsDataLoss()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.BreakRevisionChain(BundlePath, DamageKit.ChainBreak.OutOfRange);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.Single(f => f.Code == "CHK-CHN-03");
        Assert.That(finding.Severity, Is.EqualTo(IntegritySeverity.DataLoss),
            "history beyond an unreachable chunk cannot be regenerated from anything, so this is loss, not divergence");
        Assert.That(finding.Locus.FilePageIndex, Is.GreaterThan(0), "the finding must name the page it was found on");
    }

    [Test]
    [CancelAfter(30_000)]
    public void ACircularChainIsReportedAndDoesNotHangTheScan()
    {
        // The scan completing at all IS the assertion here. A chain is a linked list read out of a damaged file, and a
        // walker without a bound does not report a cycle — it hangs, on precisely the database somebody was trying to
        // diagnose. CancelAfter turns that into a failure rather than a stuck run.
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.BreakRevisionChain(BundlePath, DamageKit.ChainBreak.Cycle);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.Single(f => f.Code == "CHK-CHN-04");
        Assert.That(finding.Severity, Is.EqualTo(IntegritySeverity.Fatal));
        Assert.That(finding.Detail, Does.Contain("does not return").IgnoreCase.Or.Contain("already visited"),
            "the finding must say what a reader following this chain would do");
    }

    [Test]
    [CancelAfter(30_000)]
    public void TheChainFamilyIsSkippedAndSaysSoBelowDeepDepth()
    {
        // A check that is not run must be declared, not silently absent. A Standard-depth report that simply carried no
        // CHN findings would read identically to a clean Deep one, which is the exact confusion Limits exists to prevent.
        BuildHealthyDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Standard);

        Assert.That(report.Limits.ChecksSkipped.Any(s => s.Contains("CHK-CHN", System.StringComparison.Ordinal)), Is.True,
            "a Standard scan must declare the chain family unrun:\n" + IntegrityReportText.Render(report));
    }
}
