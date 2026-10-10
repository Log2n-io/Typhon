using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A buffer's reader unlinks the empty chunks it steps over (<see cref="VariableSizedBufferAccessor{T,TStore}.NextChunk"/>): it parks them in the
/// buffer's free list, or frees them once the list is long enough.
/// </summary>
[TestFixture]
internal sealed class VsbsEmptyChunkTests : TestBase<VsbsEmptyChunkTests>
{
    private DatabaseEngine _dbe;

    // No periodic checkpoint: the only captures are the ones a test makes.
    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
    }

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

    /// <summary>Runs <paramref name="action"/> through a change set, as a write does: its pages left owed to the checkpoint.</summary>
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

    /// <summary>The buffer's chunks from its root, in chain order, each with its element count.</summary>
    private unsafe List<(int Chunk, int Count)> Chain<T>(VariableSizedBufferSegment<T, PersistentStore> vsbs, int buffer) where T : unmanaged
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        var accessor = vsbs.Segment.CreateChunkAccessor();
        try
        {
            var chain = new List<(int, int)>();
            for (var chunk = *(int*)accessor.GetChunkAddress(buffer); chunk != 0 && chain.Count < 1_000;)
            {
                var header = (VariableSizedBufferChunkHeader*)accessor.GetChunkAddress(chunk);
                chain.Add((chunk, header->ElementCount));
                chunk = header->NextChunkId;
            }

            return chain;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>A buffer of <paramref name="count"/> elements 1, 2, …, and its data chunks, in chain order.</summary>
    private (int Buffer, List<(int Chunk, int Count)> Chunks) NewBuffer<T>(VariableSizedBufferSegment<T, PersistentStore> vsbs, int count, Func<int, T> value)
        where T : unmanaged
    {
        var items = new T[count];
        for (var i = 0; i < count; i++)
        {
            items[i] = value(i + 1);
        }

        // One element at a time: AddElements, the bulk append, does not fill a multi-chunk buffer right (#1289).
        var buffer = Write(vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            var id = vsbs.AllocateBuffer(ref a);
            foreach (var item in items)
            {
                vsbs.AddElement(id, item, ref a);
            }

            return id;
        });
        return (buffer, Chain(vsbs, buffer));
    }

    /// <summary>Deletes every element of <paramref name="chunk"/>, leaving it empty in the chain.</summary>
    private unsafe void Empty<T>(VariableSizedBufferSegment<T, PersistentStore> vsbs, int buffer, int chunk) where T : unmanaged =>
        Write(vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            var header = (VariableSizedBufferChunkHeader*)a.GetChunkAddress(chunk);
            var elements = (T*)((byte*)header + sizeof(VariableSizedBufferChunkHeader));
            var values = new T[header->ElementCount];
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

    /// <summary>Reads the whole buffer through a reader, the path that unlinks empty chunks.</summary>
    private List<T> ReadThrough<T>(VariableSizedBufferSegment<T, PersistentStore> vsbs, int buffer) where T : unmanaged
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        var read = new List<T>();
        using var reader = vsbs.GetReadOnlyAccessor(buffer);
        do
        {
            foreach (var v in reader.ReadOnlyElements)
            {
                read.Add(v);
            }
        }
        while (reader.NextChunk());

        return read;
    }

    /// <summary>
    /// A reader that meets an empty chunk of a collection of 4-byte items computed the free-list threshold from the root chunk's element capacity —
    /// 0 for those collections, whose root holds only its header — and threw a <see cref="DivideByZeroException"/> with the buffer's lock promoted and
    /// two pages latched.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void AReaderOverAnEmptyChunk_OfASmallItemCollection_ReadsTheRest()
    {
        var vsbs = _dbe.GetComponentCollectionVSBS<int>();
        Assert.That(vsbs.ElementCountRootChunk, Is.Zero, "premise: the root holds no element");
        var perChunk = vsbs.ElementCountPerChunk;
        var (buffer, chunks) = NewBuffer(vsbs, perChunk * 3, i => i);
        Assert.That(chunks, Has.Count.EqualTo(3), "premise: three data chunks");

        Empty(vsbs, buffer, chunks[1].Chunk);
        var read = ReadThrough(vsbs, buffer);
        var expected = new List<int>();
        for (var i = 1; i <= perChunk * 3; i++)
        {
            if (i <= perChunk || i > perChunk * 2)
            {
                expected.Add(i);
            }
        }

        Assert.That(read, Is.EquivalentTo(expected), "the elements of the other chunks");
        Assert.That(Chain(vsbs, buffer).ConvertAll(c => c.Chunk), Does.Not.Contain(chunks[1].Chunk), "the empty chunk was unlinked");
    }
}
