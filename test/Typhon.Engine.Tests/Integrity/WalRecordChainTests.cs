using NUnit.Framework;
using System.Linq;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// <c>WAL-02</c> — the chunk CRC chain, and the distinction between a torn tail and corruption.
/// </summary>
/// <remarks>
/// <para>
/// The whole check turns on one judgement: an unverifiable chunk at the <i>end</i> of an append-only log is the
/// ordinary shape of a crash mid-append, and recovery already handles it by stopping at the last chunk that verifies.
/// Reporting that would put a finding on every crash-path database in existence — which is the failure mode this
/// feature exists to replace, arrived at from the opposite direction.
/// </para>
/// <para>
/// So the two tests that matter are the pair: a break with valid chunks after it must be reported, and a break at the
/// tail must not be. Either one alone is satisfiable by a check that is simply wrong in the other direction.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class WalRecordChainTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void AHealthyLogDrawsNoFindingAndTheChainIsWalked()
    {
        BuildHealthyDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(string.Join(" | ", report.Limits.ChecksSkipped), Does.Not.Contain("CHK-WAL-02"),
            "the record chain must actually be walked:" + IntegrityReportText.Render(report));
        Assert.That(report.Findings.Where(f => f.Code == "CHK-WAL-02"), Is.Empty,
            "the chain check fired on an undamaged log:" + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// The caveat about unparsed WAL contents is gone, because they are parsed now.
    /// </summary>
    /// <remarks>
    /// A limits block that keeps disclaiming a coverage gap after the gap closes is worse than one that never
    /// mentioned it: an operator reading it stops trusting the rest.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void TheReportNoLongerDisclaimsUnparsedWalContents()
    {
        BuildHealthyDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(string.Join(" | ", report.Limits.Caveats), Does.Not.Contain("WAL contents were not parsed"),
            IntegrityReportText.Render(report));
    }

    /// <summary>
    /// A break with valid chunks after it is reported — that cannot be a partial append.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AFrameBrokenBeforeTheTailIsReported()
    {
        BuildMultiFrameWalDatabase();
        DamageKit.Baseline(BundlePath);

        var damage = DamageKit.CorruptWalChunkBeforeTheTail(BundlePath, out var segment);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.Single(f => f.Code == "CHK-WAL-02");
        Assert.That(finding.Summary, Does.Contain(segment));
        Assert.That(finding.Detail, Does.Contain("not a torn tail"),
            "the finding must say why this is corruption rather than the ordinary end of an append-only log");
        Assert.That(finding.RuleId, Is.EqualTo("WP-05"));
    }

    /// <summary>
    /// A crash-path log whose last chunk is partial draws a caveat, never a finding.
    /// </summary>
    /// <remarks>
    /// Truncating the log mid-chunk is exactly what a power loss during append produces. This is the negative case the
    /// check's usefulness depends on: without it, the pair above is satisfied by a check that reports every torn tail.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void ATornTailIsACaveatAndNotAFinding()
    {
        BuildMultiFrameWalDatabase();
        DamageKit.Baseline(BundlePath);

        DamageKit.TruncateWalMidFrame(BundlePath);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(report.Findings.Where(f => f.Code == "CHK-WAL-02"), Is.Empty,
            "a partial final chunk is what a crash mid-append leaves; reporting it would fire on every crash-path "
            + "database:" + IntegrityReportText.Render(report));
    }
}
