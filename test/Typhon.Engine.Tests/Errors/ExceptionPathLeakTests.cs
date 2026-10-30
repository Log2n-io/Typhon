using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// A lock timeout on an exception path leaves nothing behind: no pin on the page, and no hold on the lock.
/// </summary>
/// <remarks>
/// Each test states what a leak would actually do, on the one page and the one lock involved: an exclusive acquire of the lock fails once its holder has gone,
/// and the buffer accessor, the one path owning an accessor of its own, leaves the page's <c>SlotRefCount</c> up. A whole-cache comparison of page states can see
/// neither, and races the 100 ms test checkpoint (#410).
/// </remarks>
[TestFixture]
[NonParallelizable] // Mutates static TimeoutOptions.Current — would race with any parallel DatabaseEngine ctor.
class ExceptionPathLeakTests : TestBase<ExceptionPathLeakTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine _dbe;
    private EntityId _entityId;
    private TimeoutOptions _savedTimeouts;

    [SetUp]
    public override void Setup()
    {
        base.Setup();

        _dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(_dbe);
        _dbe.InitializeArchetypes();

        // Create and commit an entity so revision chains are populated
        var comp = new CompA(42);
        using var t = _dbe.CreateQuickTransaction();
        _entityId = t.Spawn<CompAArch>(CompAArch.A.Set(in comp));
        t.Commit();

        // Override timeouts AFTER DatabaseEngine creation (its ctor sets TimeoutOptions.Current)
        _savedTimeouts = TimeoutOptions.Current;
        TimeoutOptions.Current = new TimeoutOptions
        {
            RevisionChainLockTimeout = TimeSpan.FromMilliseconds(50),
            SegmentAllocationLockTimeout = TimeSpan.FromMilliseconds(50),
            DefaultLockTimeout = _savedTimeouts.DefaultLockTimeout,
            PageCacheLockTimeout = _savedTimeouts.PageCacheLockTimeout,
            BTreeLockTimeout = _savedTimeouts.BTreeLockTimeout,
            TransactionChainLockTimeout = _savedTimeouts.TransactionChainLockTimeout,
        };
    }

    [TearDown]
    public override void TearDown()
    {
        _dbe?.Dispose();
        TimeoutOptions.Current = _savedTimeouts;
        base.TearDown();
    }

    #region Test 1: RevisionEnumerator constructor

    [Test]
    [CancelAfter(5000)]
    public void RevisionEnumerator_Constructor_WhenLockTimeout_DoesNotLeakChunkHandle()
    {
        var ct = _dbe.GetComponentTable<CompA>();
        var segment = ct.CompRevTableSegment;
        var firstChunkId = LookupRevisionChunkId(ct, _entityId);

        var acquired = new ManualResetEventSlim(false);
        var canRelease = new ManualResetEventSlim(false);

        // Background thread holds the revision chain lock exclusively
        var holder = Task.Run(() =>
        {
            var depth = _dbe.EpochManager.EnterScope();
            try
            {
                var holderAccessor = segment.CreateChunkAccessor();
                ref var header = ref holderAccessor.GetChunk<CompRevStorageHeader>(firstChunkId, false);
                header.EnterControlLockForTest();
                acquired.Set();
                canRelease.Wait();
                header.ExitControlLockForTest();
                holderAccessor.Dispose();
            }
            finally
            {
                _dbe.EpochManager.ExitScope(depth);
            }
        });

        acquired.Wait();
        var slot = SlotOf(segment, firstChunkId);
        var pins = _dbe.MMF.SlotRefCountOf(slot);

        // Main thread: attempt to create RevisionEnumerator — should throw LockTimeoutException
        {
            var depth = _dbe.EpochManager.EnterScope();
            try
            {
                var accessor = segment.CreateChunkAccessor();
                try
                {
                    var enumerator = new RevisionEnumerator(ref accessor, firstChunkId, true, true,
                        WaitContext.FromTimeout(TimeoutOptions.Current.RevisionChainLockTimeout));
                    enumerator.Dispose();
                    Assert.Fail("Expected LockTimeoutException was not thrown");
                }
                catch (LockTimeoutException)
                {
                    // Expected — now verify no resources leaked
                }
                accessor.Dispose();
            }
            finally
            {
                _dbe.EpochManager.ExitScope(depth);
            }
        }

        AssertNoPinLeaked(slot, pins, "RevisionEnumerator");

        canRelease.Set();
        holder.Wait();
        AssertChainLockIsFree(segment, firstChunkId);
    }

    #endregion

    #region Test 2: GetRevisionElement chain-walk path

    [Test]
    [CancelAfter(5000)]
    public void GetRevisionElement_WhenLockTimeout_DoesNotLeakChunkHandles()
    {
        var ct = _dbe.GetComponentTable<CompA>();
        var segment = ct.CompRevTableSegment;
        var firstChunkId = LookupRevisionChunkId(ct, _entityId);

        var acquired = new ManualResetEventSlim(false);
        var canRelease = new ManualResetEventSlim(false);

        // Background thread holds the revision chain lock exclusively
        var holder = Task.Run(() =>
        {
            var depth = _dbe.EpochManager.EnterScope();
            try
            {
                var holderAccessor = segment.CreateChunkAccessor();
                ref var header = ref holderAccessor.GetChunk<CompRevStorageHeader>(firstChunkId, false);
                header.EnterControlLockForTest();
                acquired.Set();
                canRelease.Wait();
                header.ExitControlLockForTest();
                holderAccessor.Dispose();
            }
            finally
            {
                _dbe.EpochManager.ExitScope(depth);
            }
        });

        acquired.Wait();

        var slot = SlotOf(segment, firstChunkId);
        var pins = _dbe.MMF.SlotRefCountOf(slot);

        // Request a revision index >= CompRevCountInRoot to trigger the chain-walk path
        var revisionIndex = (short)ComponentRevisionManager.CompRevCountInRoot;

        {
            var depth = _dbe.EpochManager.EnterScope();
            try
            {
                var accessor = segment.CreateChunkAccessor();
                try
                {
                    ComponentRevisionManager.GetRevisionElement(ref accessor, firstChunkId, revisionIndex);
                    Assert.Fail("Expected LockTimeoutException was not thrown");
                }
                catch (LockTimeoutException)
                {
                    // Expected — now verify no resources leaked
                }
                accessor.Dispose();
            }
            finally
            {
                _dbe.EpochManager.ExitScope(depth);
            }
        }

        AssertNoPinLeaked(slot, pins, "GetRevisionElement");

        canRelease.Set();
        holder.Wait();
        AssertChainLockIsFree(segment, firstChunkId);
    }

    #endregion

    #region Test 3: VariableSizedBufferAccessor constructor

    [Test]
    [CancelAfter(5000)]
    public void VariableSizedBufferAccessor_Constructor_WhenLockTimeout_DoesNotLeakResources()
    {
        // Create a VariableSizedBufferSegment and allocate a buffer to get a valid rootChunkId
        var ct = _dbe.GetComponentTable<CompA>();
        var segment = ct.CompRevTableSegment;
        var vsbs = new VariableSizedBufferSegment<int, PersistentStore>(segment);

        int rootChunkId;
        {
            var depth = _dbe.EpochManager.EnterScope();
            try
            {
                var setupAccessor = segment.CreateChunkAccessor();
                rootChunkId = vsbs.AllocateBuffer(ref setupAccessor);
                setupAccessor.Dispose();
            }
            finally
            {
                _dbe.EpochManager.ExitScope(depth);
            }
        }

        var acquired = new ManualResetEventSlim(false);
        var canRelease = new ManualResetEventSlim(false);

        // Background thread holds the buffer's AccessControl lock exclusively
        var holder = Task.Run(() =>
        {
            var depth = _dbe.EpochManager.EnterScope();
            try
            {
                var holderAccessor = segment.CreateChunkAccessor();
                ref var header = ref holderAccessor.GetChunk<VariableSizedBufferRootHeader>(rootChunkId, false);
                header.EnterBufferLockForTest();
                acquired.Set();
                canRelease.Wait();
                header.ExitBufferLockForTest();
                holderAccessor.Dispose();
            }
            finally
            {
                _dbe.EpochManager.ExitScope(depth);
            }
        });

        acquired.Wait();

        // Read with the holder live: only a pin the failed GetReadOnlyAccessor leaves behind may show.
        var slot = SlotOf(segment, rootChunkId);
        var pins = _dbe.MMF.SlotRefCountOf(slot);

        // Attempt to create a read-only accessor — should throw due to lock contention.
        // Must be inside an epoch scope because GetReadOnlyAccessor creates an ChunkAccessor<PersistentStore> internally.
        var mainDepth = _dbe.EpochManager.EnterScope();
        try
        {
            var bufferAccessor = vsbs.GetReadOnlyAccessor(rootChunkId);
            bufferAccessor.Dispose();
            Assert.Fail("Expected LockTimeoutException was not thrown");
        }
        catch (LockTimeoutException)
        {
            // Expected — now verify no resources leaked
        }
        finally
        {
            _dbe.EpochManager.ExitScope(mainDepth);
        }

        AssertNoPinLeaked(slot, pins, "VariableSizedBufferAccessor");

        canRelease.Set();
        holder.Wait();
        AssertBufferLockIsFree(segment, rootChunkId);
    }

    #endregion

    #region Helpers

    /// <summary>The cache slot of the page holding <paramref name="chunkId"/>. The holder's accessor keeps that page in the cache.</summary>
    private int SlotOf(ChunkBasedSegment<PersistentStore> segment, int chunkId)
    {
        var filePage = segment.Pages[segment.GetChunkLocation(chunkId).segmentIndex];
        var mmf = _dbe.MMF;
        for (var i = 0; i < mmf.CacheSlotCount; i++)
        {
            if (mmf.GetFilePageIndex(i) == filePage)
            {
                return i;
            }
        }

        Assert.Fail($"file page {filePage} is not in the cache");
        return -1;
    }

    /// <summary>A checkpoint can pin the page for its write, so this waits: a transient pin falls away, a leaked one stays.</summary>
    private void AssertNoPinLeaked(int slot, int pins, string path) =>
        Assert.That(SpinWait.SpinUntil(() => _dbe.MMF.SlotRefCountOf(slot) <= pins, TimeSpan.FromSeconds(1)), Is.True,
            $"{path} left a pin on its page after timing out: {_dbe.MMF.SlotRefCountOf(slot)} against {pins}");

    private void AssertChainLockIsFree(ChunkBasedSegment<PersistentStore> segment, int chunkId)
    {
        var depth = _dbe.EpochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            ref var header = ref accessor.GetChunk<CompRevStorageHeader>(chunkId, false);
            var wc = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(50));
            Assert.That(header.Control.EnterExclusiveAccess(ref wc), Is.True, "the chain lock is still held once its holder has gone: the timeout path kept a hold");
            header.Control.ExitExclusiveAccess();
            accessor.Dispose();
        }
        finally
        {
            _dbe.EpochManager.ExitScope(depth);
        }
    }

    private void AssertBufferLockIsFree(ChunkBasedSegment<PersistentStore> segment, int rootChunkId)
    {
        var depth = _dbe.EpochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            ref var header = ref accessor.GetChunk<VariableSizedBufferRootHeader>(rootChunkId, false);
            var wc = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(50));
            Assert.That(header.Lock.EnterExclusiveAccess(ref wc), Is.True, "the buffer lock is still held once its holder has gone: the timeout path kept a hold");
            header.Lock.ExitExclusiveAccess();
            accessor.Dispose();
        }
        finally
        {
            _dbe.EpochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// Looks up the revision chain's first chunk ID for a given entity via EntityMap.
    /// </summary>
    private unsafe int LookupRevisionChunkId(ComponentTable ct, EntityId entityId)
    {
        var depth = _dbe.EpochManager.EnterScope();
        try
        {
            // Two id spaces, and this helper used to conflate them: EntityId.ArchetypeId is the per-DB ROUTING id, while ArchetypeRegistry and
            // _archetypeStates are keyed by the per-process CATALOG id. Feeding a routing id to GetMetadata returns whichever archetype happens to hold that
            // catalog id — usually one this engine never registered, whose state slot is null. Hence "EntityMap should exist" firing before the test reached
            // anything it meant to assert. GetMetaByRouting is the translation the engine itself uses (see _archetypeStates' own remarks).
            var meta = _dbe.GetMetaByRouting(entityId.ArchetypeId);
            Assert.That(meta, Is.Not.Null, $"routing id {entityId.ArchetypeId} should resolve to an archetype registered in this engine");
            var es = _dbe._archetypeStates[meta.ArchetypeId];
            Assert.That(es?.EntityMap, Is.Not.Null, "EntityMap should exist");

            int targetSlot = -1;
            for (int s = 0; s < meta.ComponentCount; s++)
            {
                if (ReferenceEquals(es.SlotToComponentTable[s], ct))
                {
                    targetSlot = s;
                    break;
                }
            }
            Assert.That(targetSlot, Is.GreaterThanOrEqualTo(0), "Component slot should be found");

            byte* buf = stackalloc byte[meta._entityRecordSize];
            var accessor = es.EntityMap.Segment.CreateChunkAccessor();
            bool found = es.EntityMap.TryGet(entityId.EntityKey, buf, ref accessor);
            accessor.Dispose();
            Assert.That(found, Is.True, "Entity should exist in EntityMap");

            return EntityRecordAccessor.GetLocation(buf, targetSlot);
        }
        finally
        {
            _dbe.EpochManager.ExitScope(depth);
        }
    }

    #endregion
}
