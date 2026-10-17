using System;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A revision chain's indices are 16-bit (#1382). An entity written faster than the oldest snapshot lets its chain compact grew past short.MaxValue
/// entries: the count wrapped negative, every reader misread the chain, and the compaction's stackalloc of a negative length overflowed the stack — the
/// market with 4 desks under 16 writers crashed the process. A full chain now refuses the append before writing anything, and takes writes again once the
/// snapshot that kept it full ends.
/// </summary>
[TestFixture]
[NonParallelizable] // ComponentRevisionManager.MaxChainRevisionIndex and TimeoutOptions.Current are process-wide
class RevisionChainFullTests : TestBase<RevisionChainFullTests>
{
    [Test]
    [CancelAfter(15_000)]
    public void AFullChain_RefusesTheAppend_AndTakesWritesAgainOnceTheSnapshotEnds()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(1, 0)), StatsArch.Tag.Set(new StatsTag(1)));
            Assert.That(tx.Commit(), Is.True);
        }

        var max = ComponentRevisionManager.MaxChainRevisionIndex;
        var saved = TimeoutOptions.Current;
        var timeouts = saved.Clone();
        timeouts.RevisionChainLockTimeout = TimeSpan.FromMilliseconds(100);   // the refused first write waits this long for a compaction
        TimeoutOptions.Current = timeouts;
        ComponentRevisionManager.MaxChainRevisionIndex = 4 * ComponentRevisionManager.CompRevCountInNext;
        try
        {
            var refusals = ComponentRevisionManager.ChainFullRefusals;
            var lastCommitted = 0;
            Exception refused = null;
            var reader = dbe.CreateReadOnlyTransaction();   // the oldest snapshot: every revision after it is kept
            try
            {
                for (var i = 1; i <= 200 && refused == null; i++)
                {
                    try
                    {
                        using var tx = dbe.CreateQuickTransaction();
                        tx.OpenMut(id).Set(StatsArch.Ranked, new StatsRanked(1, i));
                        Assert.That(tx.Commit(), Is.True);
                        lastCommitted = i;
                    }
                    catch (ResourceExhaustedException e)
                    {
                        refused = e;
                    }
                }
            }
            finally
            {
                reader.Dispose();
            }

            Assert.That(refused, Is.Not.Null, "the chain filled while the snapshot kept every revision");
            Assert.That(ComponentRevisionManager.ChainFullRefusals - refusals, Is.GreaterThan(0));
            using (var check = dbe.CreateReadOnlyTransaction())
            {
                Assert.That(check.Open(id).TryRead<StatsRanked>(out var value), Is.True, "the full chain still reads");
                Assert.That(value.Score, Is.EqualTo(lastCommitted), "the refused write left no trace");
            }

            // The snapshot ended: the next transactions' cleanups compact the chain, and the writes go through again.
            var wrote = false;
            for (var i = 0; i < 50 && !wrote; i++)
            {
                try
                {
                    using var tx = dbe.CreateQuickTransaction();
                    tx.OpenMut(id).Set(StatsArch.Ranked, new StatsRanked(1, 1_000 + i));
                    wrote = tx.Commit();
                }
                catch (ResourceExhaustedException)
                {
                }
            }

            Assert.That(wrote, Is.True, "writes again once the snapshot that kept the chain full ended");
        }
        finally
        {
            ComponentRevisionManager.MaxChainRevisionIndex = max;
            TimeoutOptions.Current = saved;
        }
    }
}
