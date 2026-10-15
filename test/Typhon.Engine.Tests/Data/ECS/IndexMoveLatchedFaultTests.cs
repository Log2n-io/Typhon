using System;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A multi-value index move brings in the pages its latched buffer step touches before it latches the leaf (IXW-01, #1237). Faulted in under the latch
/// while the cache was under back-pressure, a page of the market key's buffer held its leaf for seconds, every other mover of the key waited on it with
/// its epoch, and those epochs were what kept the page from being served: 16 threads fell from ~7,000 transfers/s to under 2,600, every storm failing.
/// </summary>
[TestFixture]
class IndexMoveLatchedFaultTests : TestBase<IndexMoveLatchedFaultTests>
{
    // One buffer over more pages than a warm accessor's 16-slot window, which pins what it holds: the moved element's chunk, early in it, is evictable.
    private const int OnTheHotKey = 50_000;
    private const int Moved = 100;

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("IXW-01")]
    public void AMoveOffAHotKey_FaultsNoPageWhileItHoldsALeafLatch()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        var ids = new EntityId[OnTheHotKey];
        for (var b = 0; b < OnTheHotKey; b += 2_000)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = b; i < b + 2_000; i++)
            {
                ids[i] = tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(0, i)), StatsArch.Tag.Set(new StatsTag(i)));
            }

            if (b == 0)
            {
                tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(1, -1)), StatsArch.Tag.Set(new StatsTag(-1)));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the pages are clean");

        var latchedMisses = 0;
        var misses = 0;
        var mover = Environment.CurrentManagedThreadId;
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(ids[Moved]).Set(StatsArch.Ranked, new StatsRanked(1, Moved));
            dbe.MMF.EvictEvictablePagesForTest();   // the index's pages: the commit's move below meets them cold
            dbe.MMF.ForceBackpressureForTest = () =>
            {
                if (Environment.CurrentManagedThreadId == mover)
                {
                    misses++;
                    if (OlcLatchHeld.Count > 0)
                    {
                        latchedMisses++;
                    }
                }

                return false;
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

        Assert.That(misses, Is.GreaterThan(0), "premise: the commit met cold pages");
        Assert.That(latchedMisses, Is.Zero, "a page faulted in under a leaf latch");

        using var reader = dbe.CreateQuickTransaction();
        Assert.That(reader.Open(ids[Moved]).TryRead<StatsRanked>(out var moved), Is.True);
        Assert.That(moved.Tier, Is.EqualTo(1), "the move landed");
    }
}
