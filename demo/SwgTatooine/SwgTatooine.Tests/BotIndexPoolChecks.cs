using System;
using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Bots;

namespace SwgTatooine.Tests;

/// <summary>
/// The fleet's account-index allocator, which decides whether two live bots can end up sharing one account.
/// </summary>
/// <remarks>
/// <b>These exist because both of its invariants broke in one change, and neither was visible from outside the swarm.</b> An
/// account name is derived from an index, so an index handed out while it is still in use means two sessions logging into one
/// account and the server kicking whichever arrives second; an index taken back and then lost is capacity the dial can never
/// reach again. The allocator was three private fields and a lock until it got both wrong on the same day.
/// </remarks>
[TestFixture]
internal sealed class BotIndexPoolChecks
{
    /// <summary>A fresh pool hands out runs from the bottom and stops at its ceiling.</summary>
    [Test]
    public void AFreshPoolHandsOutRunsAndStopsAtTheCeiling()
    {
        var pool = new BotIndexPool(500);

        var first = pool.Allocate(250, out var got1);
        var second = pool.Allocate(250, out var got2);
        var third = pool.Allocate(250, out var got3);

        Assert.Multiple(() =>
        {
            Assert.That((first, got1), Is.EqualTo((0, 250)));
            Assert.That((second, got2), Is.EqualTo((250, 250)));
            Assert.That(got3, Is.Zero, "a pool at its ceiling must report zero rather than hand out an index twice");
            Assert.That(third, Is.EqualTo(500), "the exhausted answer still names the high-water mark");
            Assert.That(pool.Available, Is.Zero);
        });
    }

    /// <summary>
    /// <b>The first of the two defects: releasing a PERMUTATION must not free an index that is still in use.</b>
    /// </summary>
    /// <remarks>
    /// A shrink retires the fleet's tail SLOTS, and since each handshake publishes itself the moment it completes, the indices in
    /// those slots are in completion order rather than index order. The old allocator was handed <c>(min(index), count)</c> as one
    /// block, so it freed every index the retired set merely spanned — including ones still playing. The next growth then opened an
    /// account that was already live and the server kicked the arriving session. Here the retired set is deliberately scattered.
    /// </remarks>
    [Test]
    public void ReleasingAScatteredSetFreesExactlyThoseIndices()
    {
        var pool = new BotIndexPool(500);
        pool.Allocate(500, out var all);
        Assert.That(all, Is.EqualTo(500));

        // A scattered hundred, the shape a shrink actually produces: every third index from 200, so min = 200 and max = 497 while
        // the 200 indices in between are still in use. Released as one block, [200, 300) would be freed and 198 of those are live.
        var retired = new List<int>();
        for (var i = 0; retired.Count < 100; i++)
        {
            retired.Add(200 + (i * 3));
        }

        foreach (var index in retired)
        {
            pool.Release(index, 1);
        }

        Assert.That(pool.FreeCount, Is.EqualTo(100), "a release must account for every index given back and for no others");

        // Draw the pool dry and prove the set that comes back is exactly the set that went in — no live index, nothing invented.
        var handed = new List<int>();
        while (true)
        {
            var from = pool.Allocate(100, out var count);
            if (count <= 0)
            {
                break;
            }

            for (var k = 0; k < count; k++)
            {
                handed.Add(from + k);
            }
        }

        retired.Sort();
        handed.Sort();
        Assert.That(handed, Is.EqualTo(retired), "the pool handed back a different set of indices than it was given");
    }

    /// <summary>
    /// <b>The second defect: nothing may be lost.</b> The old allocator remembered ONE run and kept whichever was lowest.
    /// </summary>
    /// <remarks>
    /// That is how a chunk with <i>n</i> failed handshakes leaked <i>n-1</i> indices — each failure releases one index on its own —
    /// and a leak is permanent: the dial loses that much reach for the rest of the run, and with the default capacity equal to the
    /// start count the fleet can then never reach its own target.
    /// </remarks>
    [Test]
    public void SingleIndexReleasesNeverLeak()
    {
        var pool = new BotIndexPool(64);
        pool.Allocate(64, out _);

        // Five scattered failures, released one at a time, lowest LAST so that "keep the lowest" cannot hide the loss.
        foreach (var index in new[] { 40, 31, 22, 13, 4 })
        {
            pool.Release(index, 1);
        }

        Assert.That(pool.FreeCount, Is.EqualTo(5), "four of five single-index releases were dropped");
        Assert.That(pool.Available, Is.EqualTo(5));
    }

    /// <summary>Indices released one at a time come back as ONE run, which is what keeps a re-grow's chunks full size.</summary>
    /// <remarks>
    /// Correctness would survive a fragmented list; the chunk size would not. A re-grow after a 250-bot shrink should allocate 250
    /// at once rather than 250 runs of one, because the chunk is what the whole pipeline is paced by.
    /// </remarks>
    [Test]
    public void AdjacentReleasesCoalesceIntoOneRun()
    {
        var pool = new BotIndexPool(1000);
        pool.Allocate(1000, out _);

        // Shuffled on purpose: coalescing must not depend on the order they come back in.
        var order = new List<int>();
        for (var i = 500; i < 750; i++)
        {
            order.Add(i);
        }

        var rng = new Random(20261016);
        for (var i = order.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        foreach (var index in order)
        {
            pool.Release(index, 1);
        }

        Assert.Multiple(() =>
        {
            Assert.That(pool.FreeRuns, Is.EqualTo(1), "250 adjacent indices came back as more than one run, so the next chunk is short");
            Assert.That(pool.FreeCount, Is.EqualTo(250));
        });

        var from = pool.Allocate(250, out var got);
        Assert.Multiple(() =>
        {
            Assert.That(got, Is.EqualTo(250), "a re-grow must be able to take the whole run in one chunk");
            Assert.That(from, Is.EqualTo(500));
        });
    }

    /// <summary>Freeing the tail walks the high-water mark back, even when the tail is freed last.</summary>
    /// <remarks>
    /// Without absorbing after an insert, releasing 500..749 and then 250..499 leaves a run and a mark that never moves — the
    /// capacity is all still there but the pool reports it as fragmented free space rather than untouched ceiling.
    /// </remarks>
    [Test]
    public void FreeingTheTailOutOfOrderStillRecoversTheHighWaterMark()
    {
        var pool = new BotIndexPool(1000);
        pool.Allocate(750, out _);

        pool.Release(500, 250);
        pool.Release(250, 250);

        Assert.Multiple(() =>
        {
            Assert.That(pool.HighWater, Is.EqualTo(250), "the mark did not follow the freed tail down");
            Assert.That(pool.FreeRuns, Is.Zero, "the freed tail should be ceiling again, not a remembered run");
            Assert.That(pool.Available, Is.EqualTo(750));
        });
    }

    /// <summary>The longest run is allocated first, so one stray index cannot shrink the next chunk to one bot.</summary>
    [Test]
    public void TheLongestRunIsTakenFirst()
    {
        var pool = new BotIndexPool(1000);
        pool.Allocate(1000, out _);

        pool.Release(10, 1);        // a lone failure, low
        pool.Release(400, 200);     // a shrink's worth, higher

        var from = pool.Allocate(250, out var got);

        Assert.Multiple(() =>
        {
            Assert.That(got, Is.EqualTo(200), "taking the lowest run would have handed back a chunk of one");
            Assert.That(from, Is.EqualTo(400));
        });
    }

    /// <summary>A shrink and re-grow of the whole population leaves the pool exactly as it started.</summary>
    /// <remarks>
    /// The round trip is the property the dial depends on: 300 up, 800 up, 250 down, 600 up again must not drift the pool, or the
    /// fleet slowly loses the ability to reach its own ceiling.
    /// </remarks>
    [Test]
    public void AShrinkAndRegrowRoundTripLeavesNoDrift()
    {
        var pool = new BotIndexPool(1200);
        var live = new List<int>();

        void Grow(int to)
        {
            while (live.Count < to)
            {
                var from = pool.Allocate(Math.Min(250, to - live.Count), out var got);
                Assert.That(got, Is.GreaterThan(0), "the pool refused to grow while it still had capacity");
                for (var k = 0; k < got; k++)
                {
                    live.Add(from + k);
                }
            }
        }

        void Shrink(int to)
        {
            // From the TAIL of the live list, which is slot order, not index order.
            while (live.Count > to)
            {
                var index = live[^1];
                live.RemoveAt(live.Count - 1);
                pool.Release(index, 1);
            }
        }

        Grow(300);
        Grow(800);
        Shrink(250);
        Grow(600);

        Assert.Multiple(() =>
        {
            Assert.That(live, Is.Unique, "two live bots share an account index, which the server refuses as AccountAlreadyPlaying");
            Assert.That(live, Has.Count.EqualTo(600));
            Assert.That(pool.Available, Is.EqualTo(600), "the pool lost capacity across a shrink and re-grow");
        });

        Shrink(0);
        Assert.Multiple(() =>
        {
            Assert.That(pool.Available, Is.EqualTo(1200), "a fully drained fleet must give the whole pool back");
            Assert.That(pool.HighWater, Is.Zero, "and the mark should be back at the bottom, not a list of runs");
        });
    }
}
