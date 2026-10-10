using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1289: <see cref="VariableSizedBufferSegment{T,TStore}.AddElements"/>, the bulk append, and <see cref="VariableSizedBufferSegment{T,TStore}.CloneBuffer"/>,
/// its caller — the copy-on-write clone of a component collection.
/// </summary>
[TestFixture]
internal sealed class VsbsBulkAppendTests : TestBase<VsbsBulkAppendTests>
{
    private DatabaseEngine _dbe;

    [SetUp]
    public void SetUpEngine()
    {
        _dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.InitializeArchetypes();
    }

    [TearDown]
    public void TearDownEngine()
    {
        _dbe?.Dispose();
        _dbe = null;
    }

    private delegate TR WithAccessor<out TR>(ref ChunkAccessor<PersistentStore> accessor);

    private TR Write<T, TR>(VariableSizedBufferSegment<T, PersistentStore> vsbs, WithAccessor<TR> action) where T : unmanaged
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        var changeSet = _dbe.MMF.CreateChangeSet();
        var accessor = vsbs.Segment.CreateChunkAccessor(changeSet);
        try
        {
            return action(ref accessor);
        }
        finally
        {
            accessor.Dispose();
            changeSet.ReleaseDirtyMarks();
        }
    }

    private T[] ReadBack<T>(VariableSizedBufferSegment<T, PersistentStore> vsbs, int buffer) where T : unmanaged => Write(vsbs,
        (ref ChunkAccessor<PersistentStore> a) =>
        {
            var dest = new T[vsbs.GetElementCount(buffer, ref a)];
            vsbs.ReadAllElements(buffer, dest, ref a);
            return dest;
        });

    /// <summary>The buffer's data chunks from its root, in chain order.</summary>
    private unsafe List<int> Chain<T>(VariableSizedBufferSegment<T, PersistentStore> vsbs, int buffer) where T : unmanaged => Write(vsbs,
        (ref ChunkAccessor<PersistentStore> a) =>
        {
            var chain = new List<int>();
            for (var chunk = *(int*)a.GetChunkAddress(buffer); chunk != 0 && chain.Count < 1_000; chunk = *(int*)a.GetChunkAddress(chunk))
            {
                chain.Add(chunk);
            }

            return chain;
        });

    private unsafe int ElementCount<T>(VariableSizedBufferSegment<T, PersistentStore> vsbs, int chunk) where T : unmanaged => Write(vsbs,
        (ref ChunkAccessor<PersistentStore> a) => ((VariableSizedBufferChunkHeader*)a.GetChunkAddress(chunk))->ElementCount);

    /// <summary>A bulk append that fills several chunks puts every element in, in order.</summary>
    [Test]
    [CancelAfter(10_000)]
    public void ABulkAppendAcrossChunks_KeepsEveryElementInOrder()
    {
        var vsbs = _dbe.GetComponentCollectionVSBS<int>();
        var items = Enumerable.Range(1, vsbs.ElementCountPerChunk * 3 + 2).ToArray();
        var buffer = Write(vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            var id = vsbs.AllocateBuffer(ref a);
            vsbs.AddElements(id, items, ref a);
            return id;
        });

        Assert.That(ReadBack(vsbs, buffer), Is.EqualTo(items));
    }

    /// <summary>
    /// A clone copies its source chunk by chunk. A source chunk only partly filled — a delete took one of its elements — shifts the rest across the
    /// destination's chunks: each later copy straddles two of them, which the bulk append got wrong.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void ACloneOfABufferWithAPartlyFilledChunk_HasTheSameElements()
    {
        var vsbs = _dbe.GetComponentCollectionVSBS<long>();
        var count = vsbs.ElementCountRootChunk + vsbs.ElementCountPerChunk * 3;
        var source = Write(vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            var id = vsbs.AllocateBuffer(ref a);
            for (var i = 1; i <= count; i++)
            {
                vsbs.AddElement(id, i, ref a);
            }

            return id;
        });
        var firstDataChunk = Chain(vsbs, source)[0];
        Write(vsbs, (ref ChunkAccessor<PersistentStore> a) => vsbs.DeleteElement(source, firstDataChunk, vsbs.ElementCountRootChunk + 1, ref a));
        Assert.That(ElementCount(vsbs, firstDataChunk), Is.EqualTo(vsbs.ElementCountPerChunk - 1), "premise: a partly filled data chunk");

        // The clone copies in chain order: the same elements, in the same order.
        var clone = Write(vsbs, (ref ChunkAccessor<PersistentStore> a) => vsbs.CloneBuffer(source, ref a));
        Assert.That(ReadBack(vsbs, clone), Is.EqualTo(ReadBack(vsbs, source)));
    }

    /// <summary>
    /// A bulk append that needs a new chunk takes the head of the buffer's free list, and the list keeps the rest. It used to read the head's successor
    /// after zeroing the chunk, so the list ended there: the chunks behind it were orphaned while the root still counted them.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public unsafe void ABulkAppendThatTakesAFreeChunk_KeepsTheRestOfTheFreeList()
    {
        var vsbs = _dbe.GetComponentCollectionVSBS<long>();
        var perChunk = vsbs.ElementCountPerChunk;
        var count = vsbs.ElementCountRootChunk + perChunk * 6;
        var buffer = Write(vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            var id = vsbs.AllocateBuffer(ref a);
            for (var i = 1; i <= count; i++)
            {
                vsbs.AddElement(id, i, ref a);
            }

            return id;
        });

        // Three empty chunks away from the append cursor, parked by a reader in the buffer's free list.
        var chain = Chain(vsbs, buffer);
        foreach (var chunk in chain.Skip(1).Take(3))
        {
            Write(vsbs, (ref ChunkAccessor<PersistentStore> a) =>
            {
                var header = (VariableSizedBufferChunkHeader*)a.GetChunkAddress(chunk);
                var elements = (long*)((byte*)header + sizeof(VariableSizedBufferChunkHeader));
                var values = new long[header->ElementCount];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = elements[i];
                }

                foreach (var v in values)
                {
                    vsbs.DeleteElement(buffer, chunk, v, ref a);
                }

                return 0;
            });
        }

        using (EpochGuard.Enter(_dbe.EpochManager))
        {
            using var reader = vsbs.GetReadOnlyAccessor(buffer);
            while (reader.NextChunk())
            {
            }
        }

        (int First, int Total, int Walked) FreeList() => Write(vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            ref var root = ref a.GetChunk<VariableSizedBufferRootHeader>(buffer);
            int first = root.FirstFreeChunkId, total = root.TotalFreeChunk, walked = 0;
            for (var chunk = first; chunk != 0 && walked < 100; chunk = *(int*)a.GetChunkAddress(chunk))
            {
                walked++;
            }

            return (first, total, walked);
        });
        Assert.That(FreeList().Walked, Is.EqualTo(3), "premise: three chunks in the free list");

        // The cursor chunk is full: the append needs one chunk, and takes the free list's head.
        Write(vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            vsbs.AddElements(buffer, Enumerable.Range(1, perChunk).Select(i => 1000L + i).ToArray(), ref a);
            return 0;
        });
        var after = FreeList();
        Assert.That(after.Total, Is.EqualTo(2), "premise: one free chunk was taken");
        Assert.That(after.Walked, Is.EqualTo(after.Total), "the free list reachable from the root, against the root's count");
    }
}
