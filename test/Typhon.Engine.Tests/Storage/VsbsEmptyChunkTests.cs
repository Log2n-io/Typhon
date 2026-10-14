using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
    /// #1343: a writer's compaction unlinks the chunks its removals emptied — never the append cursor — and stops after as many as it was told; the next
    /// appends fill the cursor, then reuse a parked chunk.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void Compaction_UnlinksTheEmptiedChunks_ButNotTheCursor_AndStopsAtItsCount()
    {
        var vsbs = _dbe.GetComponentCollectionVSBS<int>();
        var perChunk = vsbs.ElementCountPerChunk;
        var (buffer, chunks) = NewBuffer(vsbs, perChunk * 5, i => i);
        Assert.That(chunks, Has.Count.EqualTo(5), "premise: five data chunks, the last one the append cursor");

        // Emptied by removals: chunks 0, 1 and 3, and the cursor (4). Only the first three are reported as given back.
        var reported = new List<int>();
        foreach (var c in new[] { 0, 1, 3, 4 })
        {
            Write(vsbs, (ref ChunkAccessor<PersistentStore> a) =>
            {
                for (var v = c * perChunk + 1; v <= (c + 1) * perChunk; v++)
                {
                    vsbs.DeleteElement(buffer, chunks[c].Chunk, v, ref a, out var emptied);
                    if (emptied)
                    {
                        reported.Add(c);
                    }
                }

                return 0;
            });
        }

        Assert.That(reported, Is.EqualTo(new[] { 0, 1, 3 }), "every emptied chunk but the cursor is reported, once");

        var first = Write(vsbs, (ref ChunkAccessor<PersistentStore> a) => vsbs.CompactEmptyChunks(buffer, 2, ref a));
        Assert.That(first, Is.EqualTo(2), "stops after the count it was given");
        Assert.That(Chain(vsbs, buffer).ConvertAll(c => c.Chunk), Is.EqualTo(new[] { chunks[2].Chunk, chunks[3].Chunk, chunks[4].Chunk }),
            "the first two emptied chunks unlinked, the third left for later");

        var second = Write(vsbs, (ref ChunkAccessor<PersistentStore> a) => vsbs.CompactEmptyChunks(buffer, int.MaxValue, ref a));
        Assert.That(second, Is.EqualTo(1));
        Assert.That(Chain(vsbs, buffer).ConvertAll(c => c.Chunk), Is.EqualTo(new[] { chunks[2].Chunk, chunks[4].Chunk }),
            "the cursor stays linked, empty");

        // Appends fill the cursor, then take a parked chunk: nothing is allocated, and every element is read back.
        var allocated = vsbs.Segment.AllocatedChunkCount;
        Write(vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            for (var v = 0; v < perChunk + 1; v++)
            {
                vsbs.AddElement(buffer, 10_000 + v, ref a);
            }

            return 0;
        });

        Assert.That(vsbs.Segment.AllocatedChunkCount, Is.EqualTo(allocated), "the overflow took a parked chunk");
        var expected = new List<int>();
        for (var v = 2 * perChunk + 1; v <= 3 * perChunk; v++)
        {
            expected.Add(v);
        }

        for (var v = 0; v < perChunk + 1; v++)
        {
            expected.Add(10_000 + v);
        }

        Assert.That(ReadThrough(vsbs, buffer), Is.EquivalentTo(expected));
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
            Assert.That(segment.IsChunkLive(chunks[i].Chunk), Is.False, $"premise: the reader freed chunk {chunks[i].Chunk}");
        }

        Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");

        // The freed chunks were held (CK-17, #1300): released here, not by the worker racing the eviction below, and their release on disk too.
        _dbe.MMF.DrainReleases();
        Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the release's checkpoint");
        var predecessorSlot = SlotOf(vsbs, chunks[boundary - 1].Chunk);
        Assert.That(_dbe.MMF.EvictEvictablePagesForTest(), Is.GreaterThan(0), "premise: pages were evicted");
        Assert.That(_dbe.MMF.GetPageState(predecessorSlot), Is.EqualTo(PagedMMF.PageState.Free), "premise: the predecessor's page left the cache");
        var chain = Chain(vsbs, buffer).ConvertAll(c => c.Chunk);
        Assert.That(chain, Has.None.Matches<int>(c => !segment.IsChunkLive(c)), "the chain read back links a freed chunk");
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

    /// <summary>
    /// The unlinks that park empty chunks in the buffer's free list — the root's free-list head and count, each parked chunk's link — reach the disk:
    /// read back after an eviction, the free list holds what the root counts.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    public unsafe void AReadersParkedChunks_ReachTheDisk()
    {
        // The root alone at the end of its page, its chunks on the next: the root's free-list writes are the only ones on its page.
        var vsbs = _dbe.GetComponentCollectionVSBS<long>();
        var segment = vsbs.Segment;
        int buffer;
        while (true)
        {
            buffer = Write(vsbs, (ref ChunkAccessor<PersistentStore> a) => vsbs.AllocateBuffer(ref a));
            if (PageOf(segment, buffer) != PageOf(segment, buffer + 1))
            {
                break;
            }
        }

        Write(vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            for (var i = 1; i <= vsbs.ElementCountRootChunk + vsbs.ElementCountPerChunk * 8; i++)
            {
                vsbs.AddElement(buffer, i, ref a);
            }

            return 0;
        });
        var chunks = Chain(vsbs, buffer);
        Assert.That(PageOf(segment, chunks[0].Chunk), Is.Not.EqualTo(PageOf(segment, buffer)), "premise: the chunks are on another page than the root");
        for (var i = 1; i <= 3; i++)
        {
            Empty(vsbs, buffer, chunks[i].Chunk);
        }

        // The deletes on disk, so the pages the reader writes are owed by its own marks only.
        Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the emptied chunks are on disk");
        ReadThrough(vsbs, buffer);
        Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
        var rootSlot = SlotOf(vsbs, buffer);
        Assert.That(_dbe.MMF.EvictEvictablePagesForTest(), Is.GreaterThan(0), "premise: pages were evicted");
        Assert.That(_dbe.MMF.GetPageState(rootSlot), Is.EqualTo(PagedMMF.PageState.Free), "premise: the root's page left the cache");

        var (total, walked) = Write(vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            ref var root = ref a.GetChunk<VariableSizedBufferRootHeader>(buffer);
            int t = root.TotalFreeChunk, w = 0;
            for (var chunk = root.FirstFreeChunkId; chunk != 0 && w < 100; chunk = *(int*)a.GetChunkAddress(chunk))
            {
                w++;
            }

            return (t, w);
        });
        Assert.That(total, Is.EqualTo(3), "the root's free-list count, read back");
        Assert.That(walked, Is.EqualTo(3), "the free list, read back");
    }

    /// <summary>
    /// A buffer of <paramref name="chunks"/> full data chunks of elements 1, 2, …, each on a page of its own: before each chunk, fillers take the rest of
    /// the page, and are freed once the buffer is built.
    /// </summary>
    private (int Buffer, List<(int Chunk, int Count)> Chunks) NewSpreadBuffer(VariableSizedBufferSegment<int, PersistentStore> vsbs, int chunks)
    {
        var segment = vsbs.Segment;
        var fillers = new List<int>();
        var buffer = Write(vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            var id = vsbs.AllocateBuffer(ref a);
            var value = 1;
            for (var c = 0; c < chunks; c++)
            {
                for (var f = 0; f < segment.ChunkCountPerPage - 1; f++)
                {
                    fillers.Add(segment.AllocateChunk(false, a.ChangeSet));
                }

                for (var i = 0; i < vsbs.ElementCountPerChunk; i++)
                {
                    vsbs.AddElement(id, value++, ref a);
                }
            }

            return id;
        });
        foreach (var filler in fillers)
        {
            segment.FreeChunk(filler);
        }

        var chain = Chain(vsbs, buffer);
        Assert.That(chain, Has.Count.EqualTo(chunks), "premise: the data chunks");
        var pages = new HashSet<int>();
        foreach (var (chunk, _) in chain)
        {
            pages.Add(PageOf(segment, chunk));
        }

        Assert.That(pages, Has.Count.EqualTo(chunks), "premise: a page per data chunk");
        Assert.That(pages, Does.Not.Contain(PageOf(segment, buffer)), "premise: no data chunk on the root's page");
        return (buffer, chain);
    }

    /// <summary>The elements of every chunk of <paramref name="chunks"/> but those of <paramref name="emptied"/>, in chain order.</summary>
    private static List<int> ElementsBut(List<(int Chunk, int Count)> chunks, int perChunk, Predicate<int> emptied)
    {
        var expected = new List<int>();
        for (var c = 0; c < chunks.Count; c++)
        {
            if (!emptied(c))
            {
                for (var i = 1; i <= perChunk; i++)
                {
                    expected.Add(c * perChunk + i);
                }
            }
        }

        return expected;
    }

    /// <summary>
    /// #1290: two readers of one buffer, the first at an empty chunk. Unlinking it needs the buffer's lock exclusive, and a promotion waits for the
    /// other shared holders to leave — here a reader held by the same thread, elsewhere one waiting on its own promotion: it waited out the 10 s lock
    /// timeout. The unlink is housekeeping, left to a reader alone on the buffer; and the empty chunk left linked is stepped over, where the enumerator
    /// ended at it.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void TwoReadersAtAnEmptyChunk_NeitherWaitsNorStopsThere()
    {
        var vsbs = _dbe.GetComponentCollectionVSBS<int>();
        var perChunk = vsbs.ElementCountPerChunk;
        var (buffer, chunks) = NewBuffer(vsbs, perChunk * 3, i => i);
        Assert.That(chunks, Has.Count.EqualTo(3), "premise: three data chunks");
        Empty(vsbs, buffer, chunks[1].Chunk);

        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        using var other = vsbs.GetReadOnlyAccessor(buffer);
        var read = new List<int>();
        var watch = Stopwatch.StartNew();
        foreach (var v in vsbs.EnumerateBuffer(buffer))
        {
            read.Add(v);
        }

        watch.Stop();
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), "the read, beside another reader");
        Assert.That(read, Is.EqualTo(ElementsBut(chunks, perChunk, c => c == 1)), "the elements after the empty chunk");
    }

    /// <summary>
    /// #1290: a reader unlinking an empty chunk whose page another thread holds latched. It retried the same chunk with no bound, holding the buffer's
    /// lock exclusive and its root's and predecessor's pages latched: two readers each needing a page the other holds spun for good. It now leaves the
    /// chunk linked, and reads on.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public unsafe void AReaderAtAnEmptyChunkWhosePageIsLatched_ReadsOn()
    {
        var vsbs = _dbe.GetComponentCollectionVSBS<int>();
        var perChunk = vsbs.ElementCountPerChunk;
        var (buffer, chunks) = NewSpreadBuffer(vsbs, 3);
        Empty(vsbs, buffer, chunks[1].Chunk);

        using var latched = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var g = EpochGuard.Enter(_dbe.EpochManager);
            var a = vsbs.Segment.CreateChunkAccessor();
            try
            {
                a.GetChunkAddress(chunks[1].Chunk);
                Assert.That(a.TryLatchExclusive(chunks[1].Chunk), Is.True, "premise: the empty chunk's page, latched");
                latched.Set();
                release.Wait();
                a.UnlatchExclusive(chunks[1].Chunk);
            }
            finally
            {
                a.Dispose();
            }
        });
        Assert.That(latched.Wait(TimeSpan.FromSeconds(10)), Is.True, "premise: the holder latched the page");

        var reader = Task.Run(() => ReadThrough(vsbs, buffer));
        var finished = reader.Wait(TimeSpan.FromSeconds(2));
        release.Set();
        holder.GetAwaiter().GetResult();
        Assert.That(finished, Is.True, "the read, while the empty chunk's page is latched");
        Assert.That(reader.GetAwaiter().GetResult(), Is.EqualTo(ElementsBut(chunks, perChunk, c => c == 1)), "the elements after the empty chunk");
    }

    /// <summary>
    /// #1290: a run of empty chunks on more pages than the reader's accessor keeps. Fetching them pushed the root's and the predecessor's pages out of
    /// its window, and their unlatch, which looked the page up there, found nothing and returned: both pages stayed latched for good.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AnUnlinkOverMorePagesThanTheReaderKeeps_LeavesNoPageLatched()
    {
        var vsbs = _dbe.GetComponentCollectionVSBS<int>();
        var perChunk = vsbs.ElementCountPerChunk;
        var (buffer, chunks) = NewSpreadBuffer(vsbs, 72);
        for (var c = 1; c <= 70; c++)
        {
            Empty(vsbs, buffer, chunks[c].Chunk);
        }

        var rootSlot = SlotOf(vsbs, buffer);
        var predecessorSlot = SlotOf(vsbs, chunks[0].Chunk);
        Assert.That(ReadThrough(vsbs, buffer), Is.EqualTo(ElementsBut(chunks, perChunk, c => c is >= 1 and <= 70)), "the read");
        Assert.That(Chain(vsbs, buffer), Has.Count.EqualTo(2), "premise: the empty chunks were unlinked");
        Assert.That(_dbe.MMF.GetPageState(rootSlot), Is.Not.EqualTo(PagedMMF.PageState.Exclusive), "the root's page");
        Assert.That(_dbe.MMF.GetPageState(predecessorSlot), Is.Not.EqualTo(PagedMMF.PageState.Exclusive), "the predecessor's page");
    }

    /// <summary>
    /// #1290: a reader that unlinked a chunk, then read on over more pages than its accessor keeps. The unlatch took the root's page off the epoch,
    /// and the window it left took the last hold: the page left the cache, and the reader's release of the buffer's lock, through the address it had
    /// kept, wrote into a page no longer the root's — the lock stayed held.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AReaderThatUnlinkedAndReadOn_ReleasesTheRootItHolds()
    {
        var vsbs = _dbe.GetComponentCollectionVSBS<int>();
        var perChunk = vsbs.ElementCountPerChunk;
        var (buffer, chunks) = NewSpreadBuffer(vsbs, 70);
        Empty(vsbs, buffer, chunks[1].Chunk);
        var rootSlot = SlotOf(vsbs, buffer);

        var read = new List<int>();
        var rootLeftTheCache = false;
        using (EpochGuard.Enter(_dbe.EpochManager))
        {
            var reader = vsbs.GetReadOnlyAccessor(buffer);
            try
            {
                do
                {
                    foreach (var v in reader.ReadOnlyElements)
                    {
                        read.Add(v);
                    }
                }
                while (reader.NextChunk());

                // The unlink's root write on disk, so its page is clean: evictable as soon as nothing holds it.
                Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
                _dbe.MMF.EvictEvictablePagesForTest();
                rootLeftTheCache = _dbe.MMF.GetPageState(rootSlot) == PagedMMF.PageState.Free;
            }
            finally
            {
                reader.Dispose();
            }
        }

        Assert.That(read, Is.EqualTo(ElementsBut(chunks, perChunk, c => c == 1)), "the read");
        Assert.That(rootLeftTheCache, Is.False, "the root's page, while its reader holds it");
        var holds = Write(vsbs, (ref ChunkAccessor<PersistentStore> a) => a.GetChunk<VariableSizedBufferRootHeader>(buffer).Lock.SharedUsedCounter);
        Assert.That(holds, Is.Zero, "the buffer's shared holds, once its reader is gone");
    }

    /// <summary>The page of <paramref name="chunk"/> in its segment.</summary>
    private static int PageOf(ChunkBasedSegment<PersistentStore> segment, int chunk) =>
        chunk < segment.ChunkCountRootPage ? 0 : 1 + (chunk - segment.ChunkCountRootPage) / segment.ChunkCountPerPage;
}
