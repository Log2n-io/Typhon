using NUnit.Framework;
using System.Linq;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// <c>IDX-01</c> and <c>IDX-06</c> — index ownership, and the part of tree shape readable without the key width.
/// </summary>
/// <remarks>
/// <para>
/// The default fixture schema declares no indexed field, so every check here would skip on it — and a skipped check
/// reads in a report exactly like one that passed. These tests therefore build the indexed schema explicitly, and the
/// first assertion of the healthy case is that the check <b>ran</b>.
/// </para>
/// <para>
/// What is checked is bounded by what the shared node prefix can answer. Key order, high-key bounds and
/// entry-to-field agreement need the key array, whose offset and width depend on which of four node layouts a tree
/// uses; those declare themselves unrun rather than guess.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class IndexChecksTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void AHealthyIndexedDatabaseDrawsNoFindingAndTheCheckRuns()
    {
        BuildIndexedDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(string.Join("\n", report.Limits.ChecksSkipped), Does.Not.Contain("CHK-IDX-06"),
            "the structural index check must actually run on a schema that has indexes:\n" + IntegrityReportText.Render(report));

        Assert.That(report.Findings.Where(f => f.Code is "CHK-IDX-01" or "CHK-IDX-06"), Is.Empty,
            "the index family fired on an undamaged database:\n" + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// The two index segments of different node strides are both reached.
    /// </summary>
    /// <remarks>
    /// A walk that silently handled only one stride would satisfy the healthy case above. This fixture's archetype
    /// indexes a <c>String64</c> and an <c>int</c>, so it owns two index segments with different node layouts (#658),
    /// and the scan must account for both.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void BothIndexSegmentsAreAccountedFor()
    {
        BuildIndexedDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        // Unreachable-page findings are how an index segment that was never visited would surface. Their absence over a
        // schema with two index segments is the statement that both were walked.
        Assert.That(report.Findings.Where(f => f.Code.StartsWith("CHK-SEG-", System.StringComparison.Ordinal)), Is.Empty,
            IntegrityReportText.Render(report));

        Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Sound), IntegrityReportText.Render(report));
    }

    /// <summary>
    /// A sibling link into a freed chunk is reported and not followed.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void ASiblingLinkThatResolvesToNothingIsReported()
    {
        BuildIndexedDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.BreakIndexSiblingLink(BundlePath, DamageKit.IndexBreak.Dangle, out var bogus);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.First(f => f.Code == "CHK-IDX-06");
        Assert.That(finding.Detail, Does.Contain(bogus.ToString()));
        Assert.That(finding.Detail, Does.Contain("NOT followed"),
            "a link into a freed chunk must be reported as refused, not merely as noticed");
        Assert.That(finding.Repair, Is.EqualTo(Repairability.Lossless),
            "an index is derived from cluster data, so rebuilding it costs nothing");
    }

    /// <summary>
    /// The content checks run, and the key comparison is the field type's own.
    /// </summary>
    /// <remarks>
    /// A key-order check that compared raw bytes rather than the key type's order would pass on this fixture's
    /// ascending <c>int</c> keys and be wrong for every signed-negative, unsigned or floating-point index. Asserting
    /// that <c>IDX-05</c> ran and stayed silent over both an <c>int</c> tree and a <c>String64</c> one is what makes
    /// the silence mean something.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void TheContentChecksRunOverBothKeyTypes()
    {
        BuildIndexedDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        var skipped = string.Join("\n", report.Limits.ChecksSkipped);

        Assert.That(skipped, Does.Not.Contain("CHK-IDX-02"), IntegrityReportText.Render(report));
        Assert.That(skipped, Does.Not.Contain("CHK-IDX-05"), IntegrityReportText.Render(report));

        Assert.That(report.Findings.Where(f => f.Code is "CHK-IDX-02" or "CHK-IDX-05"), Is.Empty,
            "the content checks fired on an undamaged database:\n" + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// A leaf whose keys stop ascending is reported, with the tree still structurally perfect.
    /// </summary>
    /// <remarks>
    /// Every link resolves and every chain terminates, so <c>IDX-06</c> sees nothing — which is the point. Structure
    /// and contents fail independently, and an index whose shape is flawless can still answer every query wrongly.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void ALeafWhoseKeysStopAscendingIsReported()
    {
        BuildIndexedDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.BreakIndexEntry(BundlePath, DamageKit.IndexEntryBreak.KeyOrder, out var field);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        // Copying one key over another breaks the order AND duplicates a key in a unique index, so IDX-05 reports
        // twice. Both are true, and the order one is what this test is about.
        var order = report.Findings.First(f => f.Code == "CHK-IDX-05" && f.Summary.Contains("out of order"));
        Assert.That(order.Summary, Does.Contain(field));
        Assert.That(order.Detail, Does.Contain("binary search"),
            "the finding must say why order matters — a lookup stops at the first key that compares wrongly");

        Assert.That(report.Findings.Any(f => f.Code == "CHK-IDX-05" && f.Summary.Contains("twice")), Is.True,
            "the duplicated key must be reported too:\n" + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// An entry pointing at a free cluster slot is reported.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AnEntryNamingAFreeSlotIsReported()
    {
        BuildIndexedDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.BreakIndexEntry(BundlePath, DamageKit.IndexEntryBreak.DanglingValue, out _);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.First(f => f.Code == "CHK-IDX-02");
        Assert.That(finding.RuleId, Is.EqualTo("IX-02"));
        Assert.That(finding.Detail, Does.Contain("access violation"),
            "RB-04 records that decoding a ClusterLocation against a slot that is not live is a crash, not a wrong row");
    }

    /// <summary>
    /// A sibling chain that points at itself is Fatal, and the scan still terminates.
    /// </summary>
    /// <remarks>
    /// The load-bearing assertion is that the scan <i>completes</i>. A cyclic node chain is the shape that turns a
    /// range scan into a hang rather than an error, and a scanner without its own cycle guard would hang here too —
    /// on exactly the database somebody was trying to diagnose.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void ACyclicSiblingChainIsFatalAndTheScanStillTerminates()
    {
        BuildIndexedDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.BreakIndexSiblingLink(BundlePath, DamageKit.IndexBreak.Cycle, out _);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.First(f => f.Code == "CHK-IDX-06");
        Assert.That(finding.Severity, Is.EqualTo(IntegritySeverity.Fatal));
        Assert.That(finding.Detail, Does.Contain("hangs"),
            "the finding must say what the damage costs a reader, not merely that a cycle exists");
    }
}
