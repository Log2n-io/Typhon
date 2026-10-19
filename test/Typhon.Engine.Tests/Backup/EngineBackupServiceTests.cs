using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>engine.Backup</c> (#1414, 07 §1): an application backs up its running database into a real store — Auto lets admission choose, Full forces a
/// new chain, a second call while one runs is refused, and a cancelled one publishes nothing and forgets no page.
/// </summary>
class EngineBackupServiceTests : BackupTestBase<EngineBackupServiceTests>
{
    private string _store;

    public override void Setup()
    {
        base.Setup();
        _store = Path.Combine(TestStorage.ForFixture(nameof(EngineBackupServiceTests)), Guid.NewGuid().ToString("N"));
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

    [Test]
    [CancelAfter(15_000)]
    public void Auto_StartsAChainWithAFullPoint_ThenContinuesItIncrementally()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, 200);

        var first = Create(dbe);
        Assert.That(first.Kind, Is.EqualTo(BackupPointKind.Full));
        Assert.That(first.Offline, Is.False);

        Update(dbe, ids, 7);
        var second = Create(dbe);
        Assert.That(second.Kind, Is.EqualTo(BackupPointKind.Incremental));
        Assert.That(second.ParentId, Is.EqualTo(first.BackupId));
        Assert.That(second.ChainId, Is.EqualTo(first.ChainId));
        Assert.That(second.PointLsn, Is.GreaterThan(first.PointLsn));

        Assert.That(BackupOperations.Verify(_store, BackupVerifyLevel.Full).Verdict, Is.EqualTo(BackupVerifyVerdict.Ok));
        Assert.That(BackupOperations.List(_store).Points.Select(p => p.BackupId), Is.EqualTo(new[] { first.BackupId, second.BackupId }),
            "what CreateAsync returned is what the store holds");
    }

    [Test]
    [CancelAfter(15_000)]
    public void Full_StartsANewChain_WhereAutoWouldContinue()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Spawn(dbe, 50);
        var first = Create(dbe);

        var full = Create(dbe, BackupRequestKind.Full);
        Assert.That(full.Kind, Is.EqualTo(BackupPointKind.Full));
        Assert.That(full.ChainId, Is.Not.EqualTo(first.ChainId));
        Assert.That(full.ParentId, Is.EqualTo(0u));
    }

    [Test]
    [CancelAfter(15_000)]
    public void ASecondCallWhileOneRuns_IsRefusedWithBackupInProgress()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Spawn(dbe, 50);

        using var frozen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        dbe.BackupCoordinator.Probe = (phase, _) =>
        {
            if (phase == CapturePhase.AfterFreeze)
            {
                frozen.Set();
                release.Wait(Budget);
            }
        };

        var running = dbe.Backup.CreateAsync(_store);
        try
        {
            Assert.That(frozen.Wait(Budget), Is.True, "premise: the first backup is running");
            var second = Assert.ThrowsAsync<TyphonException>(() => dbe.Backup.CreateAsync(_store));
            Assert.That(second.ErrorCode, Is.EqualTo(TyphonErrorCode.BackupInProgress));
        }
        finally
        {
            release.Set();
        }

        Assert.That(running.Wait(Budget), Is.True);
        Assert.That(running.Result.BackupId, Is.EqualTo(1u), "the running backup is not disturbed by the refused one");
    }

    [Test]
    [CancelAfter(15_000)]
    public void Cancelled_PublishesNothing_AndTheNextPointStillContinuesTheChain()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, 200);
        var first = Create(dbe);
        Update(dbe, ids, 3);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);

        using var cts = new CancellationTokenSource();
        var imageSet = new List<int>();
        var coordinator = dbe.BackupCoordinator;
        coordinator.Probe = (phase, _) =>
        {
            if (phase == CapturePhase.AfterFreeze)
            {
                var capture = coordinator.LastCapture;
                for (var page = 0; page < capture.ImagePages; page++)
                {
                    if (capture.IsInImage(page))
                    {
                        imageSet.Add(page);
                    }
                }

                cts.Cancel();
            }
        };

        var cancelled = Assert.ThrowsAsync<BackupAbortedException>(() => dbe.Backup.CreateAsync(_store, ct: cts.Token));
        Assert.That(cancelled.Reason, Is.EqualTo(BackupAbortReason.Cancelled));
        Assert.That(cancelled.ErrorCode, Is.EqualTo(TyphonErrorCode.BackupAborted));
        Assert.That(cancelled, Is.InstanceOf<BackupException>(), "an application catches every backup failure as a BackupException");
        Assert.That(cancelled.IsTransient, Is.False, "a cancellation is not worth retrying on its own");
        coordinator.Probe = null;

        var listing = BackupOperations.List(_store);
        Assert.That(listing.Points.Select(p => p.BackupId), Is.EqualTo(new[] { first.BackupId }), "a cancelled backup publishes nothing");
        Assert.That(listing.TemporaryFiles, Is.Empty, "nor leaves its temporary pack behind");
        Assert.That(imageSet, Is.Not.Empty, "premise: the cancelled capture froze a page set");
        Assert.That(imageSet.Where(p => !dbe.MMF.ChangeMap.IsSet(p)), Is.Empty, "the cancelled capture's page set is back in the live map (BKP-07)");

        var next = Create(dbe);
        Assert.That(next.Kind, Is.EqualTo(BackupPointKind.Incremental), "a cancellation leaves the lineage trusted");
        Assert.That(next.ParentId, Is.EqualTo(first.BackupId));
    }

    [Test]
    public void BadArguments_AreRejectedBeforeAnyCapture()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Assert.ThrowsAsync<ArgumentException>(() => dbe.Backup.CreateAsync(""));
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => dbe.Backup.CreateAsync(_store, (BackupRequestKind)7));
        Assert.That(Directory.Exists(_store), Is.False, "nothing was started");
    }

    private BackupPointSummary Create(DatabaseEngine dbe, BackupRequestKind kind = BackupRequestKind.Auto)
    {
        var task = dbe.Backup.CreateAsync(_store, kind);
        Assert.That(task.Wait(Budget), Is.True, "the backup did not finish");
        return task.Result;
    }
}
