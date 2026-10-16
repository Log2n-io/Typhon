using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A B+Tree write a commit's publish makes does not wait out back-pressure with its pin while it has changed nothing (#1382, EP-02): it unwinds to its
/// entry, which moves the thread's epoch, waits for a page and runs it again. At 32 MiB the publishes' pins were the oldest of the epoch-held cache.
/// </summary>
[TestFixture]
class PublishIndexRestartTests : TestBase<PublishIndexRestartTests>
{
    private const int Spawned = 2_000;
    private const int Moved = 100;

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("EP-02")]
    [VerifiesRule("AP-03")]
    public void APublishIndexMove_MeetingBackpressureBeforeItsFirstLatch_RestartsWithItsEpochMoved()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        var ids = new EntityId[Spawned];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Spawned; i++)
            {
                ids[i] = tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(0, i)), StatsArch.Tag.Set(new StatsTag(i)));
            }

            tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(1, -1)), StatsArch.Tag.Set(new StatsTag(-1)));   // the key moved to, cold too
            Assert.That(tx.Commit(), Is.True);
        }

        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the pages are clean");
        Assert.That((EntityAccessor.LiveIndexCursors, PagedMMF.PublishSince, PagedMMF.IndexRestartArmed), Is.EqualTo((0, 0L, false)),
            "premise: this worker thread carries no live index cursor, publish or armed write from an earlier test");
        var restarts = Interlocked.Read(ref PagedMMF.RestartedIndexWrites);
        var mover = Environment.CurrentManagedThreadId;
        var armedRounds = 0;
        var armedOutsidePublish = 0;
        var misses = 0;
        long firstPin = 0, lastPin = 0;
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(ids[Moved]).Set(StatsArch.Ranked, new StatsRanked(1, Moved));

            // The index's pages, cold: the publish's move meets them before it latches anything. This thread's warm accessors still hold, slot-referenced,
            // the pages the spawns' index writes touched last, which no eviction takes. How many depends on the epoch changes during that commit: when they
            // were all the move reads, it met no miss, in about one run in three.
            ChunkBasedSegment<PersistentStore>.ReleaseWarmAccessorsForTests();
            dbe.MMF.EvictEvictablePagesForTest();
            dbe.MMF.ForceBackpressureForTest = () =>
            {
                if (Environment.CurrentManagedThreadId != mover)
                {
                    return false;
                }

                misses++;
                if (!PagedMMF.IndexRestartArmed)
                {
                    return false;
                }

                if (PagedMMF.PublishSince == 0)
                {
                    armedOutsidePublish++;
                }

                var pin = dbe.EpochManager.CurrentThreadPinnedEpoch;
                if (armedRounds == 0)
                {
                    firstPin = pin;
                }

                lastPin = pin;
                return ++armedRounds <= 2;   // two rounds of back-pressure, then the cache serves
            };
            try
            {
                Assert.That(tx.Commit(), Is.True);
            }
            finally
            {
                dbe.MMF.ForceBackpressureForTest = null;
            }
        }

        Assert.That(armedRounds, Is.GreaterThan(2),
            $"premise: the move met back-pressure twice before its first latch, then ran ({misses} misses on this thread)");
        Assert.That(Interlocked.Read(ref PagedMMF.RestartedIndexWrites) - restarts, Is.GreaterThanOrEqualTo(2), "each round restarted the move");
        Assert.That(lastPin, Is.GreaterThan(firstPin), "the restart moved the epoch");
        Assert.That(armedOutsidePublish, Is.Zero, "a write is restartable only in a publish");
        Assert.That(PagedMMF.IndexRestartArmed, Is.False, "nothing left armed after the commit");

        using var reader = dbe.CreateQuickTransaction();
        Assert.That(reader.Open(ids[Moved]).TryRead<StatsRanked>(out var moved), Is.True);
        Assert.That(moved.Tier, Is.EqualTo(1), "the move landed");
        Assert.That(reader.Query<StatsArch>().WhereField<StatsRanked>(r => r.Tier == 1).Count(), Is.EqualTo(2),
            "the index holds the moved entity under its new key");
        Assert.That(reader.Query<StatsArch>().WhereField<StatsRanked>(r => r.Tier == 0).Count(), Is.EqualTo(Spawned - 1), "and the old key no more");
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("EP-02")]
    public void APublishUnderPressure_MovesItsEpochBetweenEntries_EveryPressuredInterval([Values] bool underPressure)
    {
        const int entries = 4 * EntityAccessor.PressuredEpochRefreshInterval;
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        var ids = new EntityId[entries];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < entries; i++)
            {
                ids[i] = tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(0, i)), StatsArch.Tag.Set(new StatsTag(i)));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        var publisher = Environment.CurrentManagedThreadId;
        var pins = new System.Collections.Generic.List<long>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < entries; i++)
            {
                tx.OpenMut(ids[i]).Set(StatsArch.Ranked, new StatsRanked(1, i));   // every entry moves its index entry: the probe sees each one
            }

            dbe.PublishAfterIndexesProbe = () =>
            {
                if (Environment.CurrentManagedThreadId == publisher)
                {
                    pins.Add(dbe.EpochManager.CurrentThreadPinnedEpoch);
                }
            };
            if (underPressure)
            {
                dbe.MMF.NotePressureForTests();   // under pressure for PressureHold (100 ms): the publish below takes a few
            }

            try
            {
                Assert.That(tx.Commit(), Is.True);
            }
            finally
            {
                dbe.PublishAfterIndexesProbe = null;
            }
        }

        Assert.That(pins, Has.Count.EqualTo(entries), "premise: the probe saw every entry");
        var distinct = 1;
        for (var i = 1; i < pins.Count; i++)
        {
            if (pins[i] != pins[i - 1])
            {
                distinct++;
            }
        }

        Assert.That(distinct, Is.EqualTo(underPressure ? 4 : 1),
            underPressure ? "one move of the pin every PressuredEpochRefreshInterval entries" : "no pressure, no move: the publish keeps its pin");
    }

    public enum Change
    {
        Latch,
        Reservation,
        Node,
    }

    [Test]
    [VerifiesRule("EP-02")]
    public unsafe void AWriteThatLatchesReservesOrAllocates_IsNoLongerRestartable([Values] Change change)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var segment = dbe.MMF.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var tree = new IntSingleBTree<PersistentStore>(segment);
        var accessor = segment.CreateChunkAccessor();
        var publishSince = PagedMMF.PublishSince;
        var began = false;
        try
        {
            Assert.That(PagedMMF.BeginRestartableIndexWrite(), Is.False, "outside a publish, a write is not restartable");
            PagedMMF.PublishSince = Stopwatch.GetTimestamp();
            began = PagedMMF.BeginRestartableIndexWrite();
            Assert.That(began && PagedMMF.IndexRestartArmed, Is.True, "a publish's write starts restartable");
            Assert.That(PagedMMF.BeginRestartableIndexWrite(), Is.False, "a nested write is not a restart boundary of its own");
            switch (change)
            {
                case Change.Latch:
                    var version = 0;
                    var latch = new OlcLatch(ref version);
                    Assert.That(latch.TryWriteLock(), Is.True);
                    latch.WriteUnlock();
                    break;
                case Change.Reservation:
                    ChunkReservation<PersistentStore>.Current.Begin(segment);
                    ChunkReservation<PersistentStore>.Current.End();
                    break;
                case Change.Node:
                    tree.AllocNode(NodeStates.IsLeaf, ref accessor);
                    break;
            }

            Assert.That(PagedMMF.IndexRestartArmed, Is.False, $"a write that took a {change} has changed something: it must not unwind");
            PagedMMF.EndRestartableIndexWrite(began);
            began = PagedMMF.BeginRestartableIndexWrite();
            Assert.That(began, Is.True, "the next write starts restartable again");
        }
        finally
        {
            PagedMMF.EndRestartableIndexWrite(began);   // the thread's flags, whatever failed: the next test on it starts clean
            PagedMMF.PublishSince = publishSince;
            accessor.Dispose();
        }
    }
}
