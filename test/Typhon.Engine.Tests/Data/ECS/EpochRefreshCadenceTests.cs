using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The epoch refresh cadence adapts to the page cache's pressure (EP-03, #1230): every 128 entity opens at rest, every 8 while the cache is under
/// back-pressure and for <see cref="PagedMMF.PressureHold"/> after it. The epoch keeps every page any thread touched since the oldest pin, about pinned
/// threads × the interval × the pages an operation touches, so the shorter interval shrinks that set by the same factor.
/// </summary>
[TestFixture]
[NonParallelizable] // PagedMMF.PressureHold is a static.
class EpochRefreshCadenceTests : TestBase<EpochRefreshCadenceTests>
{
    private const int Opens = 64;   // fewer than one interval at rest, eight under pressure

    private DatabaseEngine OpenEngine(out List<EntityId> ids)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        ids = new List<EntityId>();
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < Opens; i++)
        {
            ids.Add(tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(i, i)), StatsArch.Tag.Set(new StatsTag(i))));
        }

        Assert.That(tx.Commit(), Is.True);
        return dbe;
    }

    /// <summary>How far the global epoch moved over <see cref="Opens"/> opens in one read transaction: one per refresh.</summary>
    private static long RefreshesOver(DatabaseEngine dbe, List<EntityId> ids)
    {
        using var tx = dbe.CreateQuickTransaction();
        var before = dbe.EpochManager.GlobalEpoch;
        foreach (var id in ids)
        {
            Assert.That(tx.Open(id).TryRead<StatsRanked>(out _), Is.True);
        }

        return dbe.EpochManager.GlobalEpoch - before;
    }

    [Test]
    [VerifiesRule("EP-03")]
    public void UnderPressure_ATransactionRefreshesEvery8Opens_AndEvery128OnceTheCacheRecovers()
    {
        var hold = PagedMMF.PressureHold;
        using var dbe = OpenEngine(out var ids);
        try
        {
            Assert.That(RefreshesOver(dbe, ids), Is.LessThan(2), "at rest: no refresh in fewer than 128 opens");

            dbe.MMF.NotePressureForTests();
            Assert.That(dbe.MMF.UnderPressure, Is.True, "premise: a back-pressure round sets the flag");
            Assert.That(RefreshesOver(dbe, ids), Is.GreaterThanOrEqualTo(Opens / EntityAccessor.PressuredEpochRefreshInterval),
                "under pressure: a refresh every 8 opens");

            dbe.MMF.NoteAllocatedForTests();
            Assert.That(dbe.MMF.UnderPressure, Is.True, "an allocation within the hold leaves the flag set");

            PagedMMF.PressureHold = TimeSpan.Zero;
            dbe.MMF.NotePressureForTests();
            dbe.MMF.NoteAllocatedForTests();
            Assert.That(dbe.MMF.UnderPressure, Is.False, "an allocation past the hold clears it");
            Assert.That(RefreshesOver(dbe, ids), Is.LessThan(2), "recovered: back to every 128 opens");
        }
        finally
        {
            PagedMMF.PressureHold = hold;
        }
    }

    /// <summary>
    /// An open that meets back-pressure does not wait with its transaction's pin: it unwinds to its start, moves the epoch there and runs again, and
    /// answers as if nothing happened (#1230). Waiting, one point reader's pin kept every page the other readers touched meanwhile, until 16 readers over
    /// 32 MiB had the whole cache epoch-held, nothing dirty.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("EP-02")]
    public void AnOpenMeetingBackpressure_UnwindsToItsStart_MovesItsEpoch_AndRunsAgain()
    {
        using var dbe = OpenEngine(out var ids);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the pages are clean");

        using var tx = dbe.CreateQuickTransaction();
        dbe.MMF.EvictEvictablePagesForTest();   // the open below misses
        var restarts = Interlocked.Read(ref PagedMMF.RestartedOpens);
        var epoch = dbe.EpochManager.GlobalEpoch;
        var opener = Environment.CurrentManagedThreadId;
        var rounds = 0;
        dbe.MMF.ForceBackpressureForTest = () => Environment.CurrentManagedThreadId == opener && ++rounds <= 3;
        StatsRanked read;
        bool found;
        try
        {
            found = tx.Open(ids[7]).TryRead(out read);
        }
        finally
        {
            dbe.MMF.ForceBackpressureForTest = null;
        }

        Assert.That(rounds, Is.GreaterThan(3), "premise: the open missed, and met three rounds of back-pressure");
        Assert.That(Interlocked.Read(ref PagedMMF.RestartedOpens) - restarts, Is.EqualTo(3), "each round unwound the open");
        Assert.That(dbe.EpochManager.GlobalEpoch - epoch, Is.GreaterThanOrEqualTo(3), "each restart moved the epoch");
        Assert.That(found, Is.True);
        Assert.That(read, Is.EqualTo(new StatsRanked(7, 7)), "the open's answer, after its restarts");
        Assert.That(PagedMMF.RestartableOpenDepth, Is.Zero, "the open's scope, closed");
        Assert.That(PagedMMF.OpenRestartSince, Is.Zero, "its restart budget, reset");
    }

    /// <summary>An open's restarts share one back-pressure timeout from its first round: past it the open throws the timeout, its scope closed.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void AnOpenWhoseRestartsOutlastTheTimeout_ThrowsIt_AndLeavesNoRestartScope()
    {
        using var dbe = OpenEngine(out var ids);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the pages are clean");
        var saved = TimeoutOptions.Current;
        var timeouts = saved.Clone();
        timeouts.PageCacheBackpressureTimeout = TimeSpan.FromMilliseconds(150);
        TimeoutOptions.Current = timeouts;
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            dbe.MMF.EvictEvictablePagesForTest();
            var restarts = Interlocked.Read(ref PagedMMF.RestartedOpens);
            var opener = Environment.CurrentManagedThreadId;
            dbe.MMF.ForceBackpressureForTest = () => Environment.CurrentManagedThreadId == opener;
            Exception thrown = null;
            try
            {
                tx.Open(ids[7]).TryRead<StatsRanked>(out _);
            }
            catch (Exception e)
            {
                thrown = e;
            }
            finally
            {
                dbe.MMF.ForceBackpressureForTest = null;
            }

            Assert.That(thrown, Is.InstanceOf<PageCacheBackpressureTimeoutException>());
            Assert.That(Interlocked.Read(ref PagedMMF.RestartedOpens) - restarts, Is.GreaterThan(0), "premise: the open restarted before it gave up");
            Assert.That(PagedMMF.RestartableOpenDepth, Is.Zero, "the open's scope, closed");
            Assert.That(PagedMMF.OpenRestartSince, Is.Zero, "its restart budget, reset");
        }
        finally
        {
            TimeoutOptions.Current = saved;
        }
    }

    public enum NoRestartWhere { UnderALiveIndexCursor, InACommitsPublish }

    /// <summary>
    /// Under a live index cursor, whose node only the epoch keeps, and in a commit's publish, which cannot be unwound past its WAL append (AP-03), an open
    /// waits with its pin as before: no restart.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void AnOpenUnderACursorOrInAPublish_WaitsInsteadOfRestarting([Values] NoRestartWhere where)
    {
        using var dbe = OpenEngine(out var ids);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the pages are clean");
        using var tx = dbe.CreateQuickTransaction();
        dbe.MMF.EvictEvictablePagesForTest();
        var restarts = Interlocked.Read(ref PagedMMF.RestartedOpens);
        var opener = Environment.CurrentManagedThreadId;
        var rounds = 0;
        dbe.MMF.ForceBackpressureForTest = () => Environment.CurrentManagedThreadId == opener && ++rounds <= 2;
        var cursors = EntityAccessor.LiveIndexCursors;
        var publishSince = PagedMMF.PublishSince;
        if (where == NoRestartWhere.UnderALiveIndexCursor)
        {
            EntityAccessor.LiveIndexCursors = cursors + 1;
        }
        else
        {
            PagedMMF.PublishSince = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        bool found;
        try
        {
            found = tx.Open(ids[7]).TryRead<StatsRanked>(out _);
        }
        finally
        {
            dbe.MMF.ForceBackpressureForTest = null;
            EntityAccessor.LiveIndexCursors = cursors;
            PagedMMF.PublishSince = publishSince;
        }

        Assert.That(rounds, Is.GreaterThan(2), "premise: the open missed, and met two rounds of back-pressure");
        Assert.That(found, Is.True, "the open, once the rounds passed");
        Assert.That(Interlocked.Read(ref PagedMMF.RestartedOpens) - restarts, Is.Zero, "no restart");
    }

    [Test]
    public void ABackpressureTimeout_NamesThePinsAndTheWorkingSetCause_WhenTheCacheIsMostlyEpochHeld()
    {
        var epochHeld = new PageCacheBackpressureTimeoutException(76, 8191, TimeSpan.FromSeconds(5), 32, 1700);
        Assert.That(epochHeld.Message, Does.Contain("pinned threads: 32").And.Contain("oldest pin 1700 epochs behind"));
        Assert.That(epochHeld.Message, Does.Contain("working set larger than the cache"));
        Assert.That(epochHeld.PinnedThreads, Is.EqualTo(32));
        Assert.That(epochHeld.OldestPinLag, Is.EqualTo(1700));

        var dirty = new PageCacheBackpressureTimeoutException(3231, 2918, TimeSpan.FromSeconds(5), 16, 40);
        Assert.That(dirty.Message, Does.Not.Contain("working set"), "mostly dirty: writeback debt, not the epoch");
    }
}
