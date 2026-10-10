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

    /// <summary>The cache slot holding <paramref name="chunk"/>.</summary>
    private unsafe int SlotOf<T>(VariableSizedBufferSegment<T, PersistentStore> vsbs, int chunk) where T : unmanaged
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        var accessor = vsbs.Segment.CreateChunkAccessor();
        try
        {
            return _dbe.MMF.MemPageIndexOf(accessor.GetChunkAddress(chunk));
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

    /// <summary>
    /// #1280: the reader's unlinks go through an accessor with no change set, so they leave their pages clean, unless something else makes them owed —
    /// as a chunk freed on the same page does. Here the predecessor's link is on one page and the freed chunks on the next: the free reaches the disk,
    /// the link does not, and the chain read back links freed chunks — chunks the segment can hand to another buffer.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    public void AReadersUnlinks_ReachTheDisk()
    {
        var vsbs = _dbe.GetComponentCollectionVSBS<long>();
        var segment = vsbs.Segment;
        var perChunk = vsbs.ElementCountPerChunk;
        var (buffer, chunks) = NewBuffer(vsbs, vsbs.ElementCountRootChunk + perChunk * 200, i => (long)i);
        Assert.That(chunks, Has.Count.EqualTo(200), "premise: two hundred data chunks");
        var boundary = chunks.FindIndex(c => PageOf(segment, c.Chunk) != PageOf(segment, chunks[0].Chunk));
        Assert.That(boundary, Is.InRange(90, 180), "premise: the chunks cross a page boundary, past round one");

        // Round one fills the buffer's free list past its threshold, so the next unlinks free their chunks instead of parking them.
        for (var i = 1; i <= 80; i++)
        {
            Empty(vsbs, buffer, chunks[i].Chunk);
        }

        ReadThrough(vsbs, buffer);
        Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: round one is on disk");

        // Round two: the chunks just past the boundary. Their predecessor, on the page before, gets the only write of the unlink on that page.
        for (var i = boundary; i < boundary + 10; i++)
        {
            Empty(vsbs, buffer, chunks[i].Chunk);
        }

        Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the emptied chunks are on disk");
        ReadThrough(vsbs, buffer);
        for (var i = boundary; i < boundary + 10; i++)
        {
            Assert.That(segment.IsChunkAllocated(chunks[i].Chunk), Is.False, $"premise: the reader freed chunk {chunks[i].Chunk}");
        }

        Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
        var predecessorSlot = SlotOf(vsbs, chunks[boundary - 1].Chunk);
        Assert.That(_dbe.MMF.EvictEvictablePagesForTest(), Is.GreaterThan(0), "premise: pages were evicted");
        Assert.That(_dbe.MMF.GetPageState(predecessorSlot), Is.EqualTo(PagedMMF.PageState.Free), "premise: the predecessor's page left the cache");
        var chain = Chain(vsbs, buffer).ConvertAll(c => c.Chunk);
        Assert.That(chain, Has.None.Matches<int>(c => !segment.IsChunkAllocated(c)), "the chain read back links a freed chunk");
    }

    /// <summary>
    /// A reader's own read is no write: it holds nothing that keeps the checkpoint off a page. The reader used to fetch every chunk it stepped onto as
    /// dirty — through an accessor with no change set, a mark that registered nothing but the page's active-writer count — so a page owed by someone
    /// else could not be written while the reader was open, and a forced checkpoint never completed (#1277, #1280).
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    public void AnOpenReader_DoesNotHoldOffTheCheckpoint()
    {
        var vsbs = _dbe.GetComponentCollectionVSBS<int>();
        var (buffer, chunks) = NewBuffer(vsbs, vsbs.ElementCountPerChunk * 2, i => i);
        var (other, otherChunks) = NewBuffer(vsbs, 1, i => i);
        Assert.That(PageOf(vsbs.Segment, otherChunks[0].Chunk), Is.EqualTo(PageOf(vsbs.Segment, chunks[1].Chunk)), "premise: the two buffers share a page");
        Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the buffers are on disk");

        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        using var reader = vsbs.GetReadOnlyAccessor(buffer);
        Assert.That(reader.NextChunk(), Is.True, "premise: the reader stepped onto the second data chunk");

        // Another buffer's write leaves that page owed.
        Write(vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            vsbs.AddElement(other, 1_000, ref a);
            return 0;
        });
        Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(5)), Is.True, "the checkpoint, while the reader is open");
    }

    /// <summary>The page of <paramref name="chunk"/> in its segment.</summary>
    private static int PageOf(ChunkBasedSegment<PersistentStore> segment, int chunk) =>
        chunk < segment.ChunkCountRootPage ? 0 : 1 + (chunk - segment.ChunkCountRootPage) / segment.ChunkCountPerPage;
}
