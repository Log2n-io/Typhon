using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// The engine-free facade the CLI and the Workbench share (#1444): create, list, status, verify and restore end to end on disk, and the public
/// results say what the internals found — kinds, verdicts and statuses mapped one to one.
/// </summary>
[TestFixture]
internal sealed class BackupOperationsTests
{
    private string _root;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestStorage.ForFixture(nameof(BackupOperationsTests)), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "db"));
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private string Store => Path.Combine(_root, "store");

    private BackupTestImage Database(string name, bool unclean) =>
        BackupTestImages.Build(name, unclean, entities: 7, root: Path.Combine(_root, name));

    [Test]
    [CancelAfter(60_000)]
    public void CreateListVerifyRestore_EndToEnd()
    {
        var image = Database("BkOps", unclean: true);

        var point = BackupOperations.Create(image.BundlePath, Store);
        Assert.That((point.BackupId, point.Kind, point.Offline, point.TailSegments > 0), Is.EqualTo((1u, BackupPointKind.Full, true, true)));
        Assert.That(point.PackFile, Is.EqualTo("00000001-F.tpack"));

        var listing = BackupOperations.List(Store, computeRestorable: true);
        Assert.That(listing.DatabaseId, Is.EqualTo(image.DatabaseId));
        Assert.That(listing.Points.Single(), Is.EqualTo(point));
        Assert.That(listing.Chains.Single().HeadBackupId, Is.EqualTo(1u));
        Assert.That(listing.RestorablePoints, Is.EqualTo(new uint[] { 1 }));

        var status = BackupOperations.Status(Store);
        Assert.That((status.Health, status.Points, status.Latest, status.LatestRestorable), Is.EqualTo((BackupHealth.Ok, 1, point, point)));

        var report = BackupOperations.Verify(Store, BackupVerifyLevel.Full);
        Assert.That((report.Level, report.Verdict), Is.EqualTo((BackupVerifyLevel.Full, BackupVerifyVerdict.Ok)));
        Assert.That(report.Packs.Single().Status, Is.EqualTo(BackupPackStatus.Ok));
        Assert.That(report.Points.Single(), Is.EqualTo(new BackupPointVerification(1, true, [])).Using<BackupPointVerification>(
            (a, b) => a.BackupId == b.BackupId && a.Restorable == b.Restorable && a.BrokenBy.Count == b.BrokenBy.Count));

        var restored = BackupOperations.Restore(Store, PointSelector.Latest, Path.Combine(_root, "restored"));
        Assert.That(restored.Point, Is.EqualTo(point));
        Assert.That(Path.GetFileName(restored.Target), Is.EqualTo(image.DatabaseName + ".typhon"));
        Assert.That(restored.PagesWritten, Is.EqualTo(image.PageCount));

        using var provider = BackupTestImages.BuildProvider(image.DatabaseName, Path.Combine(_root, "restored"), Path.Combine(restored.Target, "wal"));
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();
        Assert.That(dbe.TimelineId, Is.EqualTo(restored.NewTimelineId));
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<CompAArch>().Execute().Count, Is.EqualTo(7));
    }

    [Test]
    [CancelAfter(60_000)]
    public void ADamagedNewestPoint_IsAWarning_WhileAnOlderOneRestores_AndFailingWhenNoneDoes()
    {
        var image = Database("BkOpsDmg", unclean: false);
        var first = BackupOperations.Create(image.BundlePath, Store);
        var second = BackupOperations.Create(image.BundlePath, Store, new BackupCreateOptions { Full = true });
        var pack = Path.Combine(Store, "packs", second.PackFile);
        var bytes = File.ReadAllBytes(pack);
        bytes[PackFormat.HBackupId] ^= 1;
        File.WriteAllBytes(pack, bytes);

        var status = BackupOperations.Status(Store);
        Assert.That((status.Health, status.LatestRestorable.BackupId), Is.EqualTo((BackupHealth.Warning, first.BackupId)));
        Assert.That(status.Reason, Does.Contain($"#{second.BackupId}"));

        var report = BackupOperations.Verify(Store, PointSelector.ById(second.BackupId), BackupVerifyLevel.Quick);
        Assert.That(report.Verdict, Is.EqualTo(BackupVerifyVerdict.NoRestorablePoint));
        Assert.That(report.Packs.Single().Status, Is.EqualTo(BackupPackStatus.Unreadable));
        Assert.That(report.Packs.Single().Findings.Single().Severity, Is.EqualTo(BackupFindingSeverity.Damage));

        File.Delete(Path.Combine(Store, "packs", first.PackFile));
        Assert.That(BackupOperations.Status(Store).Health, Is.EqualTo(BackupHealth.Failing));
        Assert.That(BackupOperations.Verify(Store, BackupVerifyLevel.Quick).Verdict, Is.EqualTo(BackupVerifyVerdict.NoRestorablePoint));
        Assert.Throws<BackupChainBrokenException>(() => BackupOperations.Restore(Store, PointSelector.ById(first.BackupId), Path.Combine(_root, "r")));
    }

    [Test]
    [CancelAfter(60_000)]
    public void Create_IsIncrementalWhenTheLineageAllows_AndFullWhenAsked()
    {
        // A lineage starts online: an engine backs up into the store, then closes cleanly with a backup-state its meta vouches for.
        var dbDir = Path.Combine(_root, "db");
        var bundle = Path.Combine(dbDir, "BkOpsInc.typhon");
        using (var provider = BackupTestImages.BuildProvider("BkOpsInc", dbDir, Path.Combine(bundle, "wal")))
        using (var scope = provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                tx.Spawn<CompAArch>(CompAArch.A.Set(new CompA(1, 2, 3)));
                tx.Commit();
            }

            var online = dbe.BackupCoordinator.CaptureAsync(new FileSystemBackupStore(Store), BackupKind.Incremental, System.Threading.CancellationToken.None);
            Assert.That(online.Wait(TimeSpan.FromSeconds(10)), Is.True);
        }

        Assert.That(BackupOperations.Create(bundle, Store, new BackupCreateOptions { Full = true }).Kind, Is.EqualTo(BackupPointKind.Full));
        Assert.That(BackupOperations.Create(bundle, Store).Kind, Is.EqualTo(BackupPointKind.Incremental));
    }

    [Test]
    [CancelAfter(15_000)]
    public void ThePublicEnums_MirrorTheInternalOnes()
    {
        // The facade casts: a reordered member on either side would silently report another verdict.
        Assert.That(Enum.GetNames<BackupPointKind>(), Is.EqualTo(Enum.GetNames<BackupKind>()));
        Assert.That(Enum.GetValues<BackupPointKind>().Select(v => (int)v), Is.EqualTo(Enum.GetValues<BackupKind>().Select(v => (int)v)));
        Assert.That(Enum.GetNames<BackupVerifyLevel>(), Is.EqualTo(Enum.GetNames<VerifyLevel>()));
        Assert.That(Enum.GetNames<BackupVerifyVerdict>(), Is.EqualTo(Enum.GetNames<VerifyVerdict>()));
        Assert.That(Enum.GetNames<BackupFindingSeverity>(), Is.EqualTo(Enum.GetNames<VerifySeverity>()));
        Assert.That(Enum.GetNames<BackupPackStatus>(), Is.EqualTo(Enum.GetNames<PackVerifyStatus>()));
    }
}
