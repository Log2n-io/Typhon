using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Linq;
using Typhon.Engine.Tests.Backup;

namespace Typhon.Engine.Tests;

/// <summary>
/// WA-23: the close's final pass archives what the session wrote (a switch, then every sealed segment) after the drains and before the final checkpoint,
/// within its budget; past the budget the worker is detached and never touches the engine again; a close treated as a crash makes no pass.
/// </summary>
[NonParallelizable]
class ArchiveCloseTests : ArchiveTestBase<ArchiveCloseTests>
{
    [Test]
    [CancelAfter(20_000)]
    public void AClose_ArchivesTheSessionsLastWrites_IncludingTheActiveSegment()
    {
        Guid timeline;
        long durable;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            timeline = dbe.TimelineId;
            NewArchiver(dbe, start: true);
            SpawnAndSeal(dbe, 20, 0);
            Spawn(dbe, 20, 100);   // left in the active segment
            durable = dbe.WalManager.DurableLsn;
        }

        var archived = WalArchiveStore.Load(StoreFileSystem.Instance, StorePath, timeline, trustIndex: false);
        Assert.That(archived.ArchivedThroughLsn, Is.GreaterThanOrEqualTo(durable), "the final pass sealed and archived the active segment");
        Assert.That(archived.Unreadable, Is.Empty);
        Assert.That(WalArchiveStore.Load(StoreFileSystem.Instance, StorePath, timeline).FromIndex, Is.True, "the close wrote the index cache");
    }

    [Test]
    [CancelAfter(20_000)]
    public void AHungStore_StopReturnsWithinItsBudget_DetachesTheWorker_AndTheCloseCompletes()
    {
        var fs = new SwitchableStoreFileSystem();
        WalArchiver archiver;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            archiver = NewArchiver(dbe, fs, start: true);
            archiver.Step(Utc(0), Mono(0), default);
            fs.Block();
            SpawnAndSeal(dbe, 20, 0);
            archiver.Step(Utc(1), Mono(1), default);
            Assert.That(fs.WriteBlocked.Wait(Budget), Is.True, "premise: the worker is inside a store write");

            var clock = Stopwatch.StartNew();
            archiver.Stop(TimeSpan.FromMilliseconds(200));
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(1_200)), "Stop overran its budget");
            Assert.That(archiver.Detached, Is.True);
        }

        // The engine is closed; the store answers at last: the worker finishes its write and ends without touching the engine.
        fs.Release();
        Assert.That(archiver.JoinWorker(Budget), Is.True);
    }

    [Test]
    [CancelAfter(20_000)]
    public void ACloseTreatedAsACrash_MakesNoFinalPass()
    {
        Guid timeline;
        long durable;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            timeline = dbe.TimelineId;
            NewArchiver(dbe, start: true);
            Spawn(dbe, 20, 0);
            durable = dbe.WalManager.DurableLsn;
            dbe.SimulateHardCrash();
        }

        var archived = WalArchiveStore.Load(StoreFileSystem.Instance, StorePath, timeline, trustIndex: false);
        Assert.That(archived.ArchivedThroughLsn, Is.LessThan(durable), "a crash archives nothing at the end: the active segment stays as it was");
        Assert.That(archived.Segments.Count, Is.Zero);
    }
}
