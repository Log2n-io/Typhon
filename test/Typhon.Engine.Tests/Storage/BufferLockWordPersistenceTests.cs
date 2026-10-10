using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A variable-sized buffer's lock (<see cref="VariableSizedBufferRootHeader.Lock"/>, an <see cref="AccessControl"/>) lives in its root chunk, in a data
/// page — the shape of #1253's revision-chain lock word. A reader holds it shared for as long as it reads the buffer (a component collection, an
/// AllowMultiple index's entry set), a writer exclusively. A page written while it was held carried the held lock to disk, and once the clean page was
/// evicted and read back the lock was held by no one: every later write of the buffer timed out on it. A hold now takes a capture ticket, and has the
/// page rewritten at its release when a capture overlapped it (PS-20); a crash open frees the collection buffers' locks as their segments load.
/// </summary>
[TestFixture]
class BufferLockWordPersistenceTests : TestBase<BufferLockWordPersistenceTests>
{
    // The crash case reopens and must find the WAL it crashed with, or the reopen is not a crash recovery.
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    // No periodic checkpoint: the only captures are the ones a test makes. The log goes under the bundle, which the teardown deletes.
    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
        o.Wal.WalDirectory = null;
    }

    /// <summary>The buffer's lock word: who holds it exclusively, and how many share it.</summary>
    private static (int Holder, int Shared) LockWord(DatabaseEngine dbe, VariableSizedBufferSegment<int, PersistentStore> vsbs, int buffer)
    {
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var accessor = vsbs.Segment.CreateChunkAccessor();
            try
            {
                ref var header = ref accessor.GetChunk<VariableSizedBufferRootHeader>(buffer);
                return (header.Lock.LockedByThreadId, header.Lock.SharedUsedCounter);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            dbe.EpochManager.ExitScope(depth);
        }
    }

    /// <summary>The cache slot holding <paramref name="chunk"/>.</summary>
    private static unsafe int SlotOf(DatabaseEngine dbe, VariableSizedBufferSegment<int, PersistentStore> vsbs, int chunk)
    {
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var accessor = vsbs.Segment.CreateChunkAccessor();
            try
            {
                return dbe.MMF.MemPageIndexOf(accessor.GetChunkAddress(chunk));
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            dbe.EpochManager.ExitScope(depth);
        }
    }

    /// <summary>The page of <paramref name="chunk"/> in its segment.</summary>
    private static int PageOf(ChunkBasedSegment<PersistentStore> segment, int chunk) =>
        chunk < segment.ChunkCountRootPage ? 0 : 1 + (chunk - segment.ChunkCountRootPage) / segment.ChunkCountPerPage;

    private delegate T WriteFunc<out T>(ref ChunkAccessor<PersistentStore> accessor);

    /// <summary>Runs <paramref name="write"/> through a change set, as a write does: its pages left owed to the checkpoint.</summary>
    private static T Write<T>(DatabaseEngine dbe, VariableSizedBufferSegment<int, PersistentStore> vsbs, WriteFunc<T> write)
    {
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var changeSet = dbe.MMF.CreateChangeSet();
            var accessor = vsbs.Segment.CreateChunkAccessor(changeSet);
            try
            {
                return write(ref accessor);
            }
            finally
            {
                accessor.Dispose();
                changeSet.ReleaseDirtyMarks();
            }
        }
        finally
        {
            dbe.EpochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// A buffer whose root is the last chunk of its page, holding one element, and a neighbour root on the same page. The collection stride leaves a root
    /// no room for elements, so they land on the next page. Until #1280 a reader marked the chunk it stepped onto as being written, which kept the
    /// checkpoint off that page; the root's page, apart, was the one captured.
    /// </summary>
    private static (int Buffer, int Neighbour) BufferAtAPageEnd(DatabaseEngine dbe, VariableSizedBufferSegment<int, PersistentStore> vsbs)
    {
        var segment = vsbs.Segment;
        int neighbour = 0, buffer;
        while (true)
        {
            buffer = Write(dbe, vsbs, (ref ChunkAccessor<PersistentStore> acc) => vsbs.AllocateBuffer(ref acc));
            if (PageOf(segment, buffer) != PageOf(segment, buffer + 1))
            {
                break;
            }

            neighbour = buffer;
        }

        Assert.That(neighbour, Is.Not.Zero, "premise: a neighbour root on the buffer's page");
        Assert.That(PageOf(segment, neighbour), Is.EqualTo(PageOf(segment, buffer)), "premise: the neighbour shares the buffer's page");
        var data = Write(dbe, vsbs, (ref ChunkAccessor<PersistentStore> acc) =>
        {
            vsbs.AddElement(buffer, 1, ref acc);
            return acc.GetChunk<VariableSizedBufferRootHeader>(buffer).FirstStoredChunkId;
        });
        Assert.That(PageOf(segment, data), Is.Not.EqualTo(PageOf(segment, buffer)), "premise: the buffer's elements are on another page");
        return (buffer, neighbour);
    }

    /// <summary>
    /// A reader of the buffer — the read path's own lock site — holds its lock while a neighbour's write leaves the page owed and a checkpoint writes it.
    /// </summary>
    private static long ReadDuringACheckpoint(DatabaseEngine dbe, VariableSizedBufferSegment<int, PersistentStore> vsbs, int buffer, int neighbour)
    {
        var before = dbe.MMF.LocksHeldThroughCapture;
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            using var reader = new VariableSizedBufferAccessor<int, PersistentStore>(vsbs, buffer);
            Assert.That(LockWord(dbe, vsbs, buffer), Is.EqualTo((0, 1)), "premise: the reader holds the buffer's lock");
            Write(dbe, vsbs, (ref ChunkAccessor<PersistentStore> acc) =>
            {
                acc.GetChunk<VariableSizedBufferRootHeader>(neighbour, true);
                return 0;
            });
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
        }
        finally
        {
            dbe.EpochManager.ExitScope(depth);
        }

        return dbe.MMF.LocksHeldThroughCapture - before;
    }

    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public void ABufferLockHeldWhileItsPageIsWritten_IsFreeWhenThePageIsReadBack()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.InitializeArchetypes();
        var vsbs = dbe.GetComponentCollectionVSBS<int>();
        var (buffer, neighbour) = BufferAtAPageEnd(dbe, vsbs);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the pages are written, so clean");

        Assert.That(ReadDuringACheckpoint(dbe, vsbs, buffer, neighbour), Is.EqualTo(1), "the release found the capture that copied its hold");
        Assert.That(LockWord(dbe, vsbs, buffer), Is.EqualTo((0, 0)), "premise: the lock is free in memory");

        // The release left the page owed: it stays cached until the next checkpoint writes it with the lock free.
        var slot = SlotOf(dbe, vsbs, buffer);
        dbe.MMF.EvictEvictablePagesForTest();
        Assert.That(dbe.MMF.GetPageState(slot), Is.Not.EqualTo(PagedMMF.PageState.Free), "the page holding the only free copy of the lock was evicted");
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
        Assert.That(dbe.MMF.EvictEvictablePagesForTest(), Is.GreaterThan(0), "premise: pages were evicted");
        Assert.That(dbe.MMF.GetPageState(slot), Is.EqualTo(PagedMMF.PageState.Free), "premise: the buffer's page left the cache");
        Assert.That(LockWord(dbe, vsbs, buffer), Is.EqualTo((0, 0)), "the buffer's lock, read back from disk");
    }

    /// <summary>
    /// A crash leaves no time for the rewrite: the hold the checkpoint copied is what the data file keeps. The crash open frees the collection buffers'
    /// locks as their segments load, and the buffer takes a write again. A clean close first leaves the buffer in a database the crashed session
    /// reopened, as a long-lived one would be.
    /// </summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(30_000)]
    public void ABufferLockCopiedBeforeACrash_IsFreeAfterRecovery()
    {
        int buffer, neighbour;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.InitializeArchetypes();
            (buffer, neighbour) = BufferAtAPageEnd(dbe, dbe.GetComponentCollectionVSBS<int>());
            dbe.Dispose();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.InitializeArchetypes();
            var vsbs = dbe.GetComponentCollectionVSBS<int>();
            Assert.That(ReadDuringACheckpoint(dbe, vsbs, buffer, neighbour), Is.EqualTo(1), "premise: the capture copied the hold");
            dbe.SimulateHardCrash();
        }

        using var scope3 = ServiceProvider.CreateScope();
        using var reopened = scope3.ServiceProvider.GetRequiredService<DatabaseEngine>();
        reopened.InitializeArchetypes();
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        var reopenedVsbs = reopened.GetComponentCollectionVSBS<int>();
        Assert.That(LockWord(reopened, reopenedVsbs, buffer), Is.EqualTo((0, 0)), "the buffer's lock after recovery");
        Assert.That(reopened.CollectionBufferLocksResetAtOpen, Is.EqualTo(1), "premise: the data file held the buffer's lock");
        Assert.That(Write(reopened, reopenedVsbs, (ref ChunkAccessor<PersistentStore> acc) => reopenedVsbs.AddElement(buffer, 2, ref acc)), Is.Not.Zero,
            "a write of the buffer");
    }

    /// <summary>
    /// A write takes the buffer's lock exclusively (<see cref="VariableSizedBufferSegmentBase{TStore}.LockBuffer"/>), a raw read shared: both holds are
    /// ticketed, so a capture in flight across them has the page rewritten.
    /// </summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public void ABuffersWriteAndRawRead_EndTheirHolds()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.InitializeArchetypes();
        var vsbs = dbe.GetComponentCollectionVSBS<int>();
        var (buffer, _) = BufferAtAPageEnd(dbe, vsbs);
        var slot = SlotOf(dbe, vsbs, buffer);

        var before = dbe.MMF.LocksHeldThroughCapture;
        dbe.MMF.BeginCapture(slot);
        Write(dbe, vsbs, (ref ChunkAccessor<PersistentStore> acc) => vsbs.AddElement(buffer, 2, ref acc));
        dbe.MMF.EndCapture(slot);
        Assert.That(dbe.MMF.LocksHeldThroughCapture - before, Is.EqualTo(1), "the write's hold");

        before = dbe.MMF.LocksHeldThroughCapture;
        dbe.MMF.BeginCapture(slot);
        var copied = Write(dbe, vsbs, (ref ChunkAccessor<PersistentStore> acc) => vsbs.ReadAllElementsRaw(buffer, new byte[64], ref acc));
        dbe.MMF.EndCapture(slot);
        Assert.That(copied, Is.EqualTo(2), "premise: the read copied both elements");
        Assert.That(dbe.MMF.LocksHeldThroughCapture - before, Is.EqualTo(1), "the raw read's hold");
    }
}
