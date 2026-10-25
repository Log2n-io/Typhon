using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// AC-O2 and the bundle files (plan 1416 §4.6, DL-23/24): a request written by the one writer other processes use is served and its id echoed; an
/// engine that refuses requests refuses the writer too; the status file follows the engine, and a stopped engine's status degrades by itself.
/// </summary>
class BackupRequestTests : SchedulerTestBase<BackupRequestTests>
{
    [Test]
    [CancelAfter(30_000)]
    public void ARequest_IsServed_AndItsIdEchoedInTheStatusAndTheFile()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        Spawn(dbe, 20);
        TickIdle(dbe, "the first tick reads the store and writes the status file");
        var bundle = dbe.MMF.BundleDirectory;
        var before = BackupOperations.ReadStatus(bundle);
        Assert.That((before.EngineLive, before.AcceptsRequests, before.StorePath), Is.EqualTo((true, true, Store)));

        var id = BackupOperations.RequestBackup(bundle, BackupRequestKind.Full, "test");
        var point = Tick(dbe);

        Assert.That(point?.Kind, Is.EqualTo(BackupKind.Full));
        Assert.That(dbe.Backup.Status.LastRequestId, Is.EqualTo(id));
        var after = BackupOperations.ReadStatus(bundle);
        Assert.That(after.Status.LastRequestId, Is.EqualTo(id), "the file echoes it too");
        Assert.That(after.Status.LastPoint.BackupId, Is.EqualTo(point.Value.BackupId));
        Assert.That(after.Store.LatestRestorable.BackupId, Is.EqualTo(point.Value.BackupId), "and the store's own view comes with it");
    }

    [Test]
    [CancelAfter(30_000)]
    public void AnEngineRefusingRequests_RefusesTheWriter()
    {
        AcceptRequests = false;
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenOnClock(scope);
        TickIdle(dbe, "");

        var ex = Assert.Throws<TyphonException>(() => BackupOperations.RequestBackup(dbe.MMF.BundleDirectory, BackupRequestKind.Auto, "test"));
        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.BackupNotConfigured));
        Assert.That(ex.Message, Does.Contain("refuses backup requests"));
        Assert.That(File.Exists(Path.Combine(dbe.MMF.BundleDirectory, "backup.request")), Is.False);
    }

    /// <summary>DL-23: the file carries what its verdict was judged on; read after the engine stopped, the time rules are judged again, at read time.</summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-15")]
    public void AStoppedEnginesStatus_DegradesByItself()
    {
        string bundle;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = OpenOnClock(scope);
            Spawn(dbe, 20);
            Clock.Advance(TimeSpan.FromSeconds(31));
            Assert.That(Tick(dbe), Is.Not.Null);
            bundle = dbe.MMF.BundleDirectory;
            Assert.That(BackupOperations.ReadStatus(bundle).Health, Is.EqualTo(BackupHealth.Ok));
        }

        var now = BackupOperations.ReadStatus(bundle);
        Assert.That((now.EngineLive, now.Status.State, now.Health), Is.EqualTo((false, BackupRunState.Stopped, BackupHealth.Ok)));
        Assert.That(now.Reason, Does.StartWith("engine not running"));
        var later = BackupOperations.ReadStatus(bundle, DateTime.UtcNow + Interval * 3);
        Assert.That((later.Health, later.Status.Reason), Is.EqualTo((BackupHealth.Degraded, BackupHealthReason.Stale)),
            "a stopped engine's file does not say Ok forever");

        Assert.That(BackupOperations.ReadStatus(Store).Store.Health, Is.EqualTo(BackupHealth.Ok), "a store is answered from its packs");
        Assert.That(BackupOperations.ReadStatus(Path.Combine(Path.GetDirectoryName(Store), "nothing")).Health, Is.EqualTo(BackupHealth.Off));
    }
}
