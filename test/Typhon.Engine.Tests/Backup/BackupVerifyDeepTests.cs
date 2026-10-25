using System.IO;
using System.Linq;
using NUnit.Framework;
using Typhon.Engine.Tests.Integrity;

namespace Typhon.Engine.Tests;

/// <summary>
/// Deep verify (DL-27, plan 1416 O8): full, plus the integrity scan of the point's image read through its chain. An image the scan cannot traverse is not
/// restorable (BKP-VER-19); damage the image holds is the live database's, kept faithfully: a warning (BKP-VER-20). The damage is made in the closed
/// database with the integrity damage kit, then backed up offline: every pack byte is right, only the image is wrong, which full verify cannot see.
/// </summary>
internal sealed class BackupVerifyDeepTests : IntegrityFixtureBase
{
    private string Store => Path.Combine(DbDir, "..", "store");

    [Test]
    [CancelAfter(30_000)]
    public void AGoodPoint_IsOk()
    {
        BuildHealthyDatabase();
        var point = BackupOperations.Create(BundlePath, Store);

        var report = BackupOperations.Verify(Store, PointSelector.ById(point.BackupId), BackupVerifyLevel.Deep);

        Assert.That(report.Level, Is.EqualTo(BackupVerifyLevel.Deep));
        Assert.That(report.Verdict, Is.EqualTo(BackupVerifyVerdict.Ok), string.Join("\n", report.StoreFindings.Select(f => f.Message)));
    }

    /// <summary>The mutant "deep skipping the scan" is the full verify itself: it calls this point fine.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void AnImageTheScanCannotTraverse_IsNotRestorable()
    {
        BuildHealthyDatabase(entityCount: 3000, perTransaction: 3000);
        DamageKit.LoopForwardChain(BundlePath, out _);
        var point = BackupOperations.Create(BundlePath, Store);
        Assert.That(BackupOperations.Verify(Store, BackupVerifyLevel.Full).Verdict, Is.EqualTo(BackupVerifyVerdict.Ok), "premise: every pack byte is right");

        var report = BackupOperations.Verify(Store, BackupVerifyLevel.Deep);

        var finding = report.StoreFindings.SingleOrDefault(f => f.Code == "BKP-VER-19");
        Assert.That(finding, Is.Not.Null, string.Join("\n", report.StoreFindings.Select(f => f.Code + " " + f.Message)));
        Assert.That(finding.Severity, Is.EqualTo(BackupFindingSeverity.Damage));
        Assert.That(finding.Message, Does.Contain($"#{point.BackupId}").And.Contain("cannot be traversed"));
        Assert.That(report.Points.Single().Restorable, Is.False);
        Assert.That(report.Verdict, Is.EqualTo(BackupVerifyVerdict.NoRestorablePoint));
    }

    [Test]
    [CancelAfter(30_000)]
    public void AnImageCarryingTheLiveDatabasesDamage_IsAWarning()
    {
        BuildIndexedDatabase();
        DamageKit.BreakIndexEntry(BundlePath, DamageKit.IndexEntryBreak.KeyOrder, out _);
        BackupOperations.Create(BundlePath, Store);

        var report = BackupOperations.Verify(Store, PointSelector.Latest, BackupVerifyLevel.Deep);

        var finding = report.StoreFindings.Single(f => f.Code == "BKP-VER-20");
        Assert.That(finding.Severity, Is.EqualTo(BackupFindingSeverity.Advisory));
        Assert.That(finding.Message, Does.Contain("damage the live database had").And.Contain("Divergent"));
        Assert.That(report.Points.Single().Restorable, Is.True);
        Assert.That(report.Verdict, Is.EqualTo(BackupVerifyVerdict.Warning));
    }
}
