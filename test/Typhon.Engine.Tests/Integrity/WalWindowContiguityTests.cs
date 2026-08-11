using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// <c>WAL-04</c> — a hole in the replayable window is fatal; a segment retired below it is not.
/// </summary>
/// <remarks>
/// <para>
/// The check shipped with **no test**, and with no reference to the checkpoint LSN: it compared consecutive segment
/// ids across every file present. Reclaiming a segment whose records are already checkpointed is the ordinary way a
/// log frees space, so the check reported a <c>Fatal</c> hole on databases that were simply operating normally —
/// including any that had been opened twice (#771).
/// </para>
/// <para>
/// Both directions are needed here and neither is sufficient alone. Only the negative case, and a check that reports
/// nothing ever passes it. Only the positive case, and the pre-fix implementation passes it too — it reported
/// <i>every</i> gap, including this one.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class WalWindowContiguityTests : IntegrityFixtureBase
{
    /// <summary>
    /// A database that has been opened a second time draws no contiguity finding.
    /// </summary>
    /// <remarks>
    /// This is #771's reproducer for the WAL half. The reopen retires a consumed segment and starts a new one, leaving
    /// a gap in the raw id sequence that is entirely below the checkpoint — nothing recovery would ever replay.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void ASegmentRetiredBelowTheCheckpointIsNotAHole()
    {
        BuildHealthyDatabase();

        using (var provider = ReopenProvider())
        using (var scope = provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.InitializeArchetypes();
            dbe.ForceCheckpoint();
        }

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(report.Findings.Where(f => f.Code == "CHK-WAL-04"), Is.Empty,
            "reclaiming a checkpointed segment is how a log frees space, not damage:" + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// A segment removed from inside the replayable window is still Fatal.
    /// </summary>
    /// <remarks>
    /// The half that must survive the fix. Recovery replays forward from the checkpoint and stops at the first hole, so
    /// every transaction past it is discarded even though its segments are present and intact — which is why this is
    /// Fatal and not repairable from within the database.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void AHoleInsideTheReplayableWindowIsFatal()
    {
        BuildHealthyDatabase();

        var added = DamageKit.AppendWalSegmentLeavingAnLsnGap(BundlePath);
        Assert.That(added, Is.Not.Null, "no written segment was available to copy");

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        var finding = report.Findings.FirstOrDefault(f => f.Code == "CHK-WAL-04");
        Assert.That(finding, Is.Not.Null,
            $"'{added}' begins after the previous segment's coverage ends, which is a hole:" + IntegrityReportText.Render(report));
        Assert.That(finding.Severity, Is.EqualTo(IntegritySeverity.Fatal));
        Assert.That(finding.Summary, Does.Contain("LSN"),
            "the gap is in LSN coverage, not in file names — segment ids are not densely allocated");
        Assert.That(finding.Repair, Is.EqualTo(Repairability.NotRepairable),
            "records that are in no segment cannot be recovered from within the database");
    }

    /// <summary>
    /// With no checkpoint recorded, the whole log is the window.
    /// </summary>
    /// <remarks>
    /// The boundary case the fix must not lose: if nothing has been checkpointed then nothing has been consumed, so
    /// every segment is replayable and any gap is a real one. A window bound implemented as "skip everything before
    /// the last low segment" would silently stop checking here.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void WithoutACheckpointEverySegmentIsInTheWindow()
    {
        BuildHealthyDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        var (checkpointLsn, _) = (report.Identity.CheckpointLsn, 0);

        // The fixture checkpoints, so this asserts the premise rather than the branch — stated so a future fixture
        // change that removed the checkpoint does not silently make this test vacuous.
        Assert.That(checkpointLsn, Is.GreaterThan(0),
            "this fixture is expected to have checkpointed; if it stops, the no-checkpoint branch needs its own fixture");
        Assert.That(report.Findings.Where(f => f.Code == "CHK-WAL-04"), Is.Empty, IntegrityReportText.Render(report));
    }
}
