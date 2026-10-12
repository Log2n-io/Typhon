using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A variable-sized buffer is reference-counted (<see cref="VariableSizedBufferRootHeader.RefCounter"/>): a copy-on-write shares it, and each referent
/// gives its reference back. Only the last one may free it.
/// </summary>
[TestFixture]
internal sealed class VsbsReferenceCountTests : TestBase<VsbsReferenceCountTests>
{
    private DatabaseEngine _dbe;
    private VariableSizedBufferSegment<int, PersistentStore> _vsbs;

    // No periodic checkpoint: the only captures are the ones a test makes.
    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
    }

    [SetUp]
    public void SetUpSegment()
    {
        _dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.InitializeArchetypes();
        _vsbs = _dbe.GetComponentCollectionVSBS<int>();
    }

    [TearDown]
    public void TearDownSegment()
    {
        _dbe?.Dispose();
        _dbe = null;
    }

    private delegate T WithAccessor<out T>(ref ChunkAccessor<PersistentStore> accessor);

    private T Run<T>(WithAccessor<T> action)
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        var changeSet = _dbe.MMF.CreateChangeSet();
        var accessor = _vsbs.Segment.CreateChunkAccessor(changeSet);
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

    private unsafe int SlotOf(int chunk) => Run((ref ChunkAccessor<PersistentStore> a) => _dbe.MMF.MemPageIndexOf(a.GetChunkAddress(chunk)));

    private int NewBuffer(params int[] values) => Run((ref ChunkAccessor<PersistentStore> a) =>
    {
        var buffer = _vsbs.AllocateBuffer(ref a);
        _vsbs.AddElements(buffer, values, ref a);
        return buffer;
    });

    private int[] ReadBack(int buffer) => Run((ref ChunkAccessor<PersistentStore> a) =>
    {
        var dest = new int[_vsbs.GetElementCount(buffer, ref a)];
        _vsbs.ReadAllElements(buffer, dest, ref a);
        return dest;
    });

    /// <summary>
    /// #1279: a delete gives back one reference. It used to free the root whatever the count, so a buffer still shared lost its root under the other
    /// referent — and kept its element chunks, which only a count of 0 frees.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void ADeleteOfASharedBuffer_KeepsItForTheOtherReference()
    {
        var buffer = NewBuffer(1, 2, 3);
        Assert.That(Run((ref ChunkAccessor<PersistentStore> a) => _vsbs.BufferAddRef(buffer, ref a)), Is.EqualTo(2), "premise: the buffer is shared");

        Run((ref ChunkAccessor<PersistentStore> a) =>
        {
            _vsbs.DeleteBuffer(buffer, ref a);
            return 0;
        });
        Assert.That(_vsbs.Segment.IsChunkAllocated(buffer), Is.True, "the root, still referenced, was freed");
        Assert.That(Run((ref ChunkAccessor<PersistentStore> a) => _vsbs.GetRefCounter(buffer, ref a)), Is.EqualTo(1));
        Assert.That(ReadBack(buffer), Is.EqualTo(new[] { 1, 2, 3 }), "the other reference's content");

        Run((ref ChunkAccessor<PersistentStore> a) =>
        {
            _vsbs.DeleteBuffer(buffer, ref a);
            return 0;
        });
        Assert.That(_vsbs.Segment.IsChunkLive(buffer), Is.False, "the last reference's delete frees the root");
    }

    /// <summary>
    /// #1280: a release through an accessor with no change set — the deferred cleanup's — decremented the count in a page it never made owed. Evicted
    /// clean and read back, the count was one too high, and the buffer could never be freed.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void AReleaseWithoutAChangeSet_ReachesTheDisk()
    {
        var buffer = NewBuffer(1, 2, 3);
        Run((ref ChunkAccessor<PersistentStore> a) => _vsbs.BufferAddRef(buffer, ref a));
        Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the count of 2 is on disk");

        using (EpochGuard.Enter(_dbe.EpochManager))
        {
            var accessor = _vsbs.Segment.CreateChunkAccessor();
            try
            {
                Assert.That(_vsbs.BufferRelease(buffer, ref accessor), Is.EqualTo(1), "premise: one reference left");
            }
            finally
            {
                accessor.Dispose();
            }
        }

        Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
        var slot = SlotOf(buffer);
        Assert.That(_dbe.MMF.EvictEvictablePagesForTest(), Is.GreaterThan(0), "premise: pages were evicted");
        Assert.That(_dbe.MMF.GetPageState(slot), Is.EqualTo(PagedMMF.PageState.Free), "premise: the root's page left the cache");
        Assert.That(Run((ref ChunkAccessor<PersistentStore> a) => _vsbs.GetRefCounter(buffer, ref a)), Is.EqualTo(1), "the count, read back from disk");
    }
}
