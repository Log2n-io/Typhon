using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Concurrency and growth contract for <see cref="FieldShadowBuffer"/> (#558). The buffer is appended to by every worker on the
/// hot write path and drained single-threaded at the tick boundary, so an append must be wait-free, must never lose or duplicate
/// an entry, and must survive growing past its initial capacity — the gate that admits appends
/// (<see cref="DirtyBitmap.TestAndSet"/>) grows on demand, so no capacity computed at construction is a real bound.
/// </summary>
[TestFixture]
internal sealed class FieldShadowBufferTests
{
    private const int BlockSize = 4096;   // must track FieldShadowBuffer.BlockShift

    private static KeyBytes8 Key(long v)
    {
        var k = default(KeyBytes8);
        unsafe
        {
            *(long*)&k = v;
        }

        return k;
    }

    private static long KeyValue(in KeyBytes8 k)
    {
        unsafe
        {
            fixed (KeyBytes8* p = &k)
            {
                return *(long*)p;
            }
        }
    }

    [Test]
    public void ConcurrentAppends_LoseNothingAndDuplicateNothing()
    {
        const int threads = 8;
        const int perThread = 10_000;
        var buffer = new FieldShadowBuffer();
        using var start = new ManualResetEventSlim(false);

        var tasks = new Task[threads];
        for (var t = 0; t < threads; t++)
        {
            var threadId = t;
            tasks[t] = Task.Run(() =>
            {
                start.Wait();
                for (var i = 0; i < perThread; i++)
                {
                    // Encode (thread, sequence) so every appended entry is globally unique and attributable.
                    long tag = ((long)threadId << 32) | (uint)i;
                    buffer.Append(threadId, tag, Key(tag));
                }
            });
        }

        start.Set();
        Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)), Is.True, "append workers must finish");

        Assert.That(buffer.Count, Is.EqualTo(threads * perThread), "every reservation must yield exactly one entry");

        var seen = new HashSet<long>(threads * perThread);
        for (var i = 0; i < buffer.Count; i++)
        {
            ref var e = ref buffer[i];
            Assert.That(seen.Add(e.EntityPK), Is.True, $"duplicate entry at index {i} (pk {e.EntityPK})");
            Assert.That(KeyValue(e.OldKey), Is.EqualTo(e.EntityPK), $"entry {i} is torn — key does not match its pk");
            Assert.That(e.ChunkId, Is.EqualTo((int)(e.EntityPK >> 32)), $"entry {i} is torn — chunkId does not match its pk");
        }

        Assert.That(seen, Has.Count.EqualTo(threads * perThread), "no entry may be lost");
    }

    [Test]
    public void AppendsPastInitialCapacity_Grow()
    {
        // Default capacity is 256; push well past the first block boundary so the block table itself has to grow.
        var buffer = new FieldShadowBuffer();
        var total = (BlockSize * 3) + 17;

        for (var i = 0; i < total; i++)
        {
            buffer.Append(i, i, Key(i));
        }

        Assert.That(buffer.Count, Is.EqualTo(total));
        for (var i = 0; i < total; i++)
        {
            Assert.That(buffer[i].EntityPK, Is.EqualTo(i), $"entry {i} survived growth");
        }
    }

    [Test]
    public void ConcurrentAppends_AcrossBlockBoundaries_StayIntact()
    {
        // Enough entries that growth happens WHILE other threads are appending — the case a resize-in-place would corrupt.
        const int threads = 6;
        const int perThread = 3000;
        var buffer = new FieldShadowBuffer();
        using var start = new ManualResetEventSlim(false);

        var tasks = new Task[threads];
        for (var t = 0; t < threads; t++)
        {
            var threadId = t;
            tasks[t] = Task.Run(() =>
            {
                start.Wait();
                for (var i = 0; i < perThread; i++)
                {
                    long tag = ((long)threadId << 32) | (uint)i;
                    buffer.Append(threadId, tag, Key(tag));
                }
            });
        }

        start.Set();
        Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)), Is.True);

        Assert.That(buffer.Count, Is.EqualTo(threads * perThread));
        Assert.That(buffer.Count, Is.GreaterThan(BlockSize * 4), "the test must actually cross several block boundaries");

        var seen = new HashSet<long>();
        for (var i = 0; i < buffer.Count; i++)
        {
            Assert.That(seen.Add(buffer[i].EntityPK), Is.True, $"duplicate at {i}");
        }
    }

    [Test]
    public void Reset_ClearsCountAndRetainsCapacity()
    {
        var buffer = new FieldShadowBuffer();
        for (var i = 0; i < BlockSize + 100; i++)
        {
            buffer.Append(i, i, Key(i));
        }

        buffer.Reset();
        Assert.That(buffer.Count, Is.Zero);

        // Re-filling past the old high-water mark must not fault: blocks are retained, so this allocates nothing.
        for (var i = 0; i < BlockSize + 100; i++)
        {
            buffer.Append(i, i * 2, Key(i * 2));
        }

        Assert.That(buffer.Count, Is.EqualTo(BlockSize + 100));
        Assert.That(buffer[BlockSize + 99].EntityPK, Is.EqualTo((BlockSize + 99) * 2));
    }

    [Test]
    public void Indexer_RoundTripsAcrossBlockBoundary()
    {
        var buffer = new FieldShadowBuffer();
        for (var i = 0; i < BlockSize + 8; i++)
        {
            buffer.Append(i, i, Key(i));
        }

        // Straddle the boundary explicitly — an off-by-one in the block/slot split would only show here.
        foreach (var i in new[] { 0, 1, BlockSize - 2, BlockSize - 1, BlockSize, BlockSize + 1, BlockSize + 7 })
        {
            Assert.That(buffer[i].EntityPK, Is.EqualTo(i), $"index {i}");
            Assert.That(buffer[i].ChunkId, Is.EqualTo(i), $"index {i}");
        }
    }

    [Test]
    public void EntriesAreMutableThroughTheIndexer()
    {
        // The drain path takes `ref var entry = ref buffer[e]` — the indexer must hand back a real reference, not a copy.
        var buffer = new FieldShadowBuffer();
        buffer.Append(1, 42, Key(42));

        ref var entry = ref buffer[0];
        entry.ChunkId = 99;

        Assert.That(buffer[0].ChunkId, Is.EqualTo(99));
    }
}
