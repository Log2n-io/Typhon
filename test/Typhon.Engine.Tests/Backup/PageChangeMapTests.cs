using System.Threading;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

[TestFixture]
public class PageChangeMapTests
{
    [Test]
    public void Set_AcrossWordAndChunkBoundaries_MarksExactlyThoseRuns()
    {
        var map = new PageChangeMap();
        var lastOfChunk = PageChangeMap.PagesPerChunk - 1;
        map.Set(62, 4);                       // straddles a word
        map.Set(lastOfChunk - 1, 3);          // straddles a chunk
        map.Set(int.MaxValue - 1, 1);         // the last directory slot

        Assert.Multiple(() =>
        {
            Assert.That(map.IsSet(61), Is.False);
            Assert.That(map.IsSet(62) && map.IsSet(63) && map.IsSet(64) && map.IsSet(65), Is.True);
            Assert.That(map.IsSet(66), Is.False);
            Assert.That(map.IsSet(lastOfChunk - 1) && map.IsSet(lastOfChunk) && map.IsSet(lastOfChunk + 1), Is.True);
            Assert.That(map.IsSet(lastOfChunk + 2), Is.False);
            Assert.That(map.IsSet(int.MaxValue - 1), Is.True);
            Assert.That(map.PopCount(int.MaxValue), Is.EqualTo(8));
            Assert.That(map.NextSetPage(0, int.MaxValue), Is.EqualTo(62));
            Assert.That(map.NextSetPage(66, int.MaxValue), Is.EqualTo(lastOfChunk - 1));
            Assert.That(map.NextSetPage(lastOfChunk + 2, int.MaxValue), Is.EqualTo(int.MaxValue - 1));
            Assert.That(map.NextSetPage(66, lastOfChunk - 1), Is.EqualTo(-1), "the end bound is exclusive");
        });
    }

    [Test]
    public void OrWith_MergesAndDense_RoundTrips()
    {
        var a = new PageChangeMap();
        var b = new PageChangeMap();
        a.Set(3, 1);
        b.Set(5, 2);
        b.Set(PageChangeMap.PagesPerChunk + 9, 1);
        a.OrWith(b);

        var dense = a.ToDense(PageChangeMap.PagesPerChunk + 10);
        var back = new PageChangeMap();
        back.OrDense(dense);

        Assert.Multiple(() =>
        {
            Assert.That(a.PopCount(int.MaxValue), Is.EqualTo(4));
            Assert.That(back.PopCount(int.MaxValue), Is.EqualTo(4));
            Assert.That(back.IsSet(3) && back.IsSet(5) && back.IsSet(6) && back.IsSet(PageChangeMap.PagesPerChunk + 9), Is.True);
            Assert.That(a.ToDense(6)[0], Is.EqualTo(1UL << 3 | 1UL << 5), "ToDense masks pages past its bound");
        });
    }

    [Test]
    public void SetAll_MarksEveryPageBelowTheBound()
    {
        var map = new PageChangeMap();
        map.SetAll(130);
        Assert.That(map.PopCount(int.MaxValue), Is.EqualTo(130));
        Assert.That(map.IsSet(129) && !map.IsSet(130), Is.True);
    }

    /// <summary>
    /// Two writers set interleaved bits of the same words, starting together on a chunk nobody has touched. A plain read-modify-write loses bits here,
    /// and a directory publish without a CAS loses the bits the first publisher set before the second overwrote its chunk; either leaves a written
    /// page out of the next incremental backup.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void ConcurrentSets_OnSharedWordsAndAFreshChunk_LoseNothing()
    {
        const int Rounds = 400;
        const int Pages = 2048;
        var maps = new PageChangeMap[Rounds];
        for (var r = 0; r < Rounds; r++)
        {
            maps[r] = new PageChangeMap();
        }

        using var start = new Barrier(2);
        void Writer(int parity)
        {
            for (var r = 0; r < Rounds; r++)
            {
                var baseline = (r % 64) * PageChangeMap.PagesPerChunk;   // a chunk this round's map has never seen
                start.SignalAndWait();
                for (var p = parity; p < Pages; p += 2)
                {
                    maps[r].Set(baseline + p, 1);
                }
            }
        }

        var t0 = new Thread(() => Writer(0));
        var t1 = new Thread(() => Writer(1));
        t0.Start();
        t1.Start();
        t0.Join();
        t1.Join();

        var lost = 0;
        for (var r = 0; r < Rounds; r++)
        {
            lost += Pages - (int)maps[r].PopCount(int.MaxValue);
        }

        Assert.That(lost, Is.Zero, "concurrent Sets lost pages");
    }
}
