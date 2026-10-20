using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// A backup job on the coordinator (plan 1416 §3.4, O3): capture → verify the new pack (DL-28) → the caller's task completes → retention (DL-6), with the
/// state and the status following. A point that fails its verify makes the next point full (DL-26, BKP-06); a job is one job until its retention ends.
/// </summary>
class BackupJobTests : BackupTestBase<BackupJobTests>
{
    private const string Marker = "BKP-06: after a failed verify";
    private string _store;
    private BackupRetentionPolicy _retention;

    public override void Setup()
    {
        _store = Path.Combine(TestStorage.ForFixture(nameof(BackupJobTests)), Guid.NewGuid().ToString("N"));
        _retention = null;
        base.Setup();
    }

    public override void TearDown()
    {
        base.TearDown();
        try
        {
            Directory.Delete(_store, true);
        }
        catch (IOException)
        {
        }
    }

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Backup = new BackupOptions { StorePath = _store, Interval = TimeSpan.Zero, Retention = _retention };
    }

    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("BKP-06")]
    public void APointFailingItsVerify_MakesTheNextOneFull() => AssertTheNextPointIsFull(retrustAfterFailure: false);

    /// <summary>The check can fail: with the lineage trusted again after the failure, the next point is an incremental over the damaged one.</summary>
    [Test]
    [CancelAfter(20_000)]
    [RuleMutant("BKP-06")]
    public void Mutant_NoRevokeAfterAFailedVerify_IsReported() =>
        RuleMutants.AssertDetects("BKP-06", Marker, () => AssertTheNextPointIsFull(retrustAfterFailure: true));

    private void AssertTheNextPointIsFull(bool retrustAfterFailure)
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, 100);
        Assert.That(Create(dbe).Kind, Is.EqualTo(BackupPointKind.Full));

        Update(dbe, ids, 2);
        dbe.BackupCoordinator.BeforePostVerify = point => Damage(point.PackPath);
        var failed = dbe.Backup.CreateAsync();
        var ex = Assert.Throws<AggregateException>(() => failed.Wait(Budget));
        Assert.That(ex.InnerException, Is.InstanceOf<BackupPackCorruptException>(), ex.ToString());
        dbe.BackupCoordinator.BeforePostVerify = null;
        Assert.That(dbe.Backup.Status.LastFailure?.Kind, Is.EqualTo(nameof(BackupPackCorruptException)));
        if (retrustAfterFailure)
        {
            dbe.BackupCoordinator.Lineage.Trusted = true;
        }

        Update(dbe, ids, 3);
        var next = Create(dbe);
        Assert.That(next.Kind, Is.EqualTo(BackupPointKind.Full), $"{Marker}, the next point must not rest on the damaged one");
    }

    /// <summary>DL-6: retention runs after a point the application asked for, on the backup thread, after the caller's task completed.</summary>
    [Test]
    [CancelAfter(20_000)]
    public void RetentionRuns_AfterAnApiPoint()
    {
        _retention = new BackupRetentionPolicy
        {
            MinKeep = 1, MaxCount = 2, MaxAge = TimeSpan.Zero, MaxTotalBytes = 0, CompactThreshold = 0, KeepLastDrilled = false,
        };
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, 100);
        using var ended = new SemaphoreSlim(0);
        dbe.BackupScheduler.JobEndedProbe = () => ended.Release();
        for (var i = 0; i < 4; i++)
        {
            Update(dbe, ids, i);
            Create(dbe);
            Assert.That(ended.Wait(Budget), Is.True, "the job (retention included) did not end");
        }

        var packs = Directory.GetFiles(Path.Combine(_store, "packs")).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.That(packs, Is.EqualTo(new[] { "00000003-C.tpack", "00000004-I.tpack" }));
        var status = dbe.Backup.Status;
        Assert.That((status.PointsTotal, status.RestorablePoints, status.RetentionBlocked), Is.EqualTo((4L, 2, BackupRetentionBlock.None)));
        Assert.That(status.LastPoint.BackupId, Is.EqualTo(4u));
        Assert.That(status.State, Is.EqualTo(BackupRunState.Idle));
    }

    /// <summary>A job is one job until its retention has ended: a second request meanwhile is BackupInProgress, though the first caller's task completed.</summary>
    [Test]
    [CancelAfter(20_000)]
    public void ASecondPointWhileRetentionRuns_IsBackupInProgress()
    {
        _retention = new BackupRetentionPolicy
        {
            MinKeep = 1, MaxCount = 5, MaxAge = TimeSpan.Zero, MaxTotalBytes = 0, CompactThreshold = 0, KeepLastDrilled = false,
        };
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Spawn(dbe, 20);
        using var inRetention = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        dbe.BackupScheduler.BeforeRetentionProbe = () =>
        {
            inRetention.Set();
            release.Wait(Budget);
        };

        try
        {
            Create(dbe);
            Assert.That(inRetention.Wait(Budget), Is.True, "premise: the retention runs after the caller's task");
            Assert.That(dbe.Backup.Status.Phase, Is.EqualTo(BackupRunPhase.Retention));
            var second = dbe.Backup.CreateAsync();
            var ex = Assert.Throws<AggregateException>(() => second.Wait(Budget));
            Assert.That((ex.InnerException as TyphonException)?.ErrorCode, Is.EqualTo(TyphonErrorCode.BackupInProgress));
        }
        finally
        {
            release.Set();
        }
    }

    private static BackupPointSummary Create(DatabaseEngine dbe)
    {
        var task = dbe.Backup.CreateAsync();
        Assert.That(task.Wait(Budget), Is.True, "the backup did not finish");
        return task.Result;
    }

    /// <summary>Flips a byte of the pack's INDEX (just before its footer): the quick verify reads it, and fails its IndexCRC.</summary>
    private static void Damage(string pack)
    {
        using var file = new FileStream(pack, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        file.Position = file.Length - 128 - 8;
        var b = file.ReadByte();
        file.Position -= 1;
        file.WriteByte((byte)(b ^ 0x5A));
    }
}
