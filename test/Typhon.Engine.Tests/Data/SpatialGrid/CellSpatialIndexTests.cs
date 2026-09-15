using System;
using System.Threading;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
class CellSpatialIndexTests
{
    private static ClusterSpatialAabb Aabb(float minX, float minY, float maxX, float maxY, uint cat = 1u) =>
        new() { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY, CategoryMask = cat };

    [Test]
    public void NewIndex_IsEmpty_WithDefaultCapacity()
    {
        var index = new CellSpatialIndex();
        Assert.That(index.ClusterCount, Is.EqualTo(0));
        Assert.That(index.Capacity, Is.EqualTo(CellSpatialIndex.DefaultInitialCapacity));
        Assert.That(index.ClusterIds.Length, Is.EqualTo(CellSpatialIndex.DefaultInitialCapacity));
        Assert.That(index.MinX.Length, Is.EqualTo(CellSpatialIndex.DefaultInitialCapacity));
    }

    [Test]
    public void Constructor_ClampsZeroCapacity_ToOne()
    {
        var index = new CellSpatialIndex(initialCapacity: 0);
        Assert.That(index.Capacity, Is.EqualTo(1));
    }

    [Test]
    public void Add_FirstCluster_ReturnsSlotZero()
    {
        var index = new CellSpatialIndex();
        int slot = index.Add(clusterChunkId: 42, Aabb(0, 0, 10, 10, cat: 0x7u));

        Assert.That(slot, Is.EqualTo(0));
        Assert.That(index.ClusterCount, Is.EqualTo(1));
        Assert.That(index.ClusterIds[0], Is.EqualTo(42));
        Assert.That(index.MinX[0], Is.EqualTo(0f));
        Assert.That(index.MaxX[0], Is.EqualTo(10f));
        Assert.That(index.CategoryMasks[0], Is.EqualTo(0x7u));
    }

    [Test]
    public void Add_ManyClusters_AssignsSequentialSlots()
    {
        var index = new CellSpatialIndex();
        for (int i = 0; i < 10; i++)
        {
            int slot = index.Add(clusterChunkId: 100 + i, Aabb(i, i, i + 1, i + 1));
            Assert.That(slot, Is.EqualTo(i));
        }
        Assert.That(index.ClusterCount, Is.EqualTo(10));
        for (int i = 0; i < 10; i++)
        {
            Assert.That(index.ClusterIds[i], Is.EqualTo(100 + i));
            Assert.That(index.MinX[i], Is.EqualTo((float)i));
        }
    }

    [Test]
    public void Add_BeyondInitialCapacity_GrowsByDoubling()
    {
        var index = new CellSpatialIndex(initialCapacity: 4);
        Assert.That(index.Capacity, Is.EqualTo(4));

        for (int i = 0; i < 5; i++)
        {
            index.Add(clusterChunkId: 10 + i, Aabb(i, i, i + 1, i + 1));
        }
        // 5th add should have triggered growth (4 → 8).
        Assert.That(index.Capacity, Is.EqualTo(8));
        Assert.That(index.ClusterCount, Is.EqualTo(5));
        Assert.That(index.ClusterIds[4], Is.EqualTo(14));

        // Fill the rest and trigger another growth.
        for (int i = 5; i < 9; i++)
        {
            index.Add(clusterChunkId: 10 + i, Aabb(i, i, i + 1, i + 1));
        }
        Assert.That(index.Capacity, Is.EqualTo(16));
        Assert.That(index.ClusterCount, Is.EqualTo(9));
    }

    [Test]
    public void UpdateAt_OverwritesAabbAndMask()
    {
        var index = new CellSpatialIndex();
        int slot = index.Add(clusterChunkId: 7, Aabb(0, 0, 5, 5, cat: 0x1u));
        index.UpdateAt(slot, Aabb(-10, -10, 20, 20, cat: 0xFu));

        Assert.That(index.MinX[slot], Is.EqualTo(-10f));
        Assert.That(index.MinY[slot], Is.EqualTo(-10f));
        Assert.That(index.MaxX[slot], Is.EqualTo(20f));
        Assert.That(index.MaxY[slot], Is.EqualTo(20f));
        Assert.That(index.CategoryMasks[slot], Is.EqualTo(0xFu));
        // Back-ref unchanged
        Assert.That(index.ClusterIds[slot], Is.EqualTo(7));
    }

    [Test]
    public void RemoveAt_LastEntry_NoSwap_ReturnsMinusOne()
    {
        var index = new CellSpatialIndex();
        int slot0 = index.Add(clusterChunkId: 11, Aabb(0, 0, 1, 1));
        int slot1 = index.Add(clusterChunkId: 22, Aabb(1, 1, 2, 2));

        int swapped = index.RemoveAt(slot1);
        Assert.That(swapped, Is.EqualTo(-1),
            "removing the last entry leaves nothing to swap in → no back-pointer fixup needed");
        Assert.That(index.ClusterCount, Is.EqualTo(1));
        // Slot 0 untouched
        Assert.That(index.ClusterIds[0], Is.EqualTo(11));
    }

    [Test]
    public void RemoveAt_MiddleEntry_SwapsLastIntoSlot_ReturnsMovedClusterId()
    {
        var index = new CellSpatialIndex();
        index.Add(clusterChunkId: 100, Aabb(0, 0, 1, 1, cat: 0x1u));   // slot 0
        index.Add(clusterChunkId: 200, Aabb(2, 2, 3, 3, cat: 0x2u));   // slot 1  ← removed
        index.Add(clusterChunkId: 300, Aabb(4, 4, 5, 5, cat: 0x4u));   // slot 2  ← will move to slot 1

        int swapped = index.RemoveAt(slot: 1);

        Assert.That(swapped, Is.EqualTo(300), "cluster 300 was moved from slot 2 into slot 1");
        Assert.That(index.ClusterCount, Is.EqualTo(2));
        Assert.That(index.ClusterIds[0], Is.EqualTo(100));
        Assert.That(index.ClusterIds[1], Is.EqualTo(300), "slot 1 now holds the cluster that was at slot 2");
        // The AABB was moved too
        Assert.That(index.MinX[1], Is.EqualTo(4f));
        Assert.That(index.CategoryMasks[1], Is.EqualTo(0x4u));
    }

    [Test]
    public void RemoveAt_AllEntriesInReverse_LeavesEmptyIndex()
    {
        var index = new CellSpatialIndex();
        for (int i = 0; i < 5; i++)
        {
            index.Add(clusterChunkId: 10 + i, Aabb(i, i, i + 1, i + 1));
        }
        for (int i = 4; i >= 0; i--)
        {
            index.RemoveAt(i);
        }
        Assert.That(index.ClusterCount, Is.EqualTo(0));
    }

    [Test]
    public void AddRemoveAdd_ReusesSlotAfterSwapWithLast()
    {
        var index = new CellSpatialIndex();
        index.Add(clusterChunkId: 10, Aabb(0, 0, 1, 1));
        index.Add(clusterChunkId: 20, Aabb(2, 2, 3, 3));

        int swappedIdOnRemove = index.RemoveAt(slot: 0);
        Assert.That(swappedIdOnRemove, Is.EqualTo(20));
        Assert.That(index.ClusterIds[0], Is.EqualTo(20));

        int newSlot = index.Add(clusterChunkId: 30, Aabb(4, 4, 5, 5));
        Assert.That(newSlot, Is.EqualTo(1), "add after removal fills the tail, not the vacated slot");
        Assert.That(index.ClusterCount, Is.EqualTo(2));
        Assert.That(index.ClusterIds[0], Is.EqualTo(20));
        Assert.That(index.ClusterIds[1], Is.EqualTo(30));
    }

    [Test]
    public void ClusterSpatialAabb_Empty_HasInfiniteMinAndNegativeInfiniteMax()
    {
        var aabb = ClusterSpatialAabb.Empty;
        Assert.That(aabb.MinX, Is.EqualTo(float.PositiveInfinity));
        Assert.That(aabb.MinY, Is.EqualTo(float.PositiveInfinity));
        Assert.That(aabb.MaxX, Is.EqualTo(float.NegativeInfinity));
        Assert.That(aabb.MaxY, Is.EqualTo(float.NegativeInfinity));
        Assert.That(aabb.CategoryMask, Is.EqualTo(0u));
    }

    [Test]
    public void ClusterSpatialAabb_Union_WithOneEntity_ExactlyMatchesEntityBounds()
    {
        var aabb = ClusterSpatialAabb.Empty;
        aabb.Union2F(entityMinX: 5f, entityMinY: 10f, entityMaxX: 15f, entityMaxY: 20f, entityCategoryMask: 0x3u);

        Assert.That(aabb.MinX, Is.EqualTo(5f));
        Assert.That(aabb.MinY, Is.EqualTo(10f));
        Assert.That(aabb.MaxX, Is.EqualTo(15f));
        Assert.That(aabb.MaxY, Is.EqualTo(20f));
        Assert.That(aabb.CategoryMask, Is.EqualTo(0x3u));
    }

    [Test]
    public void ClusterSpatialAabb_Union_MultipleEntities_EnclosesAllAndCombinesMasks()
    {
        var aabb = ClusterSpatialAabb.Empty;
        aabb.Union2F(0f, 0f, 10f, 10f, 0x1u);
        aabb.Union2F(-5f, 20f, 5f, 30f, 0x2u);
        aabb.Union2F(15f, -3f, 25f, 7f, 0x8u);

        Assert.That(aabb.MinX, Is.EqualTo(-5f));
        Assert.That(aabb.MinY, Is.EqualTo(-3f));
        Assert.That(aabb.MaxX, Is.EqualTo(25f));
        Assert.That(aabb.MaxY, Is.EqualTo(30f));
        Assert.That(aabb.CategoryMask, Is.EqualTo(0xBu));
    }

    private const string Ca02Marker = "CA-02: a widen made during a grow's copy was lost";

    /// <summary>
    /// A widen that runs while a grow has copied the arrays but not yet published them must land in the grown arrays (CA-02, before any fence). The two
    /// test seams make the interleaving deterministic: the grow parks after its copy, the widen runs, and the grow publishes once the widen has either
    /// returned (the defect: its writes went to the arrays the grow had already copied) or started waiting the grow out.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CA-02")]
    public void AWidenDuringAGrowsCopy_LandsInTheGrownArrays() => WidenDuringAGrowsCopy((index, slot, aabb) => index.WidenAt(slot, in aabb));

    /// <summary>
    /// The first form of <see cref="CellSpatialIndex.WidenAt"/>, which re-checked <see cref="CellSpatialIndex.ClusterIds"/> as its witness. The grow
    /// publishes that array last, so a widen that re-checks before then sees no move, and its writes stay in the abandoned arrays.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("CA-02")]
    public void AWidenThatReChecksTheIdArray_IsLostInTheGrowsCopy() =>
        RuleMutants.AssertDetects("CA-02", Ca02Marker, () => WidenDuringAGrowsCopy(WidenAgainstTheIdWitness));

    /// <summary>
    /// A widen that took its stamp before a grow and writes after the grow's copy puts its writes in arrays the grow has already copied: it must see the
    /// stamp move and redo them in the grown arrays (CA-02). The widen parks on its even stamp, the grow runs to the end of its copy, the widen resumes.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CA-02")]
    public void AWidenStampedBeforeAGrow_IsRedoneInTheGrownArrays() =>
        WidenStampedBeforeAGrowsCopy((index, slot, aabb, parkOnStamp) =>
        {
            index.WidenStampedProbe = parkOnStamp;
            index.WidenAt(slot, in aabb);
        });

    /// <summary>A widen with no re-check: its writes after the grow's copy stay in the abandoned arrays.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("CA-02")]
    public void AWidenThatNeverReChecks_IsLostInTheGrowsCopy() =>
        RuleMutants.AssertDetects("CA-02", Ca02Marker, () => WidenStampedBeforeAGrowsCopy((index, slot, aabb, parkOnStamp) =>
        {
            parkOnStamp();
            ClusterSpatialAabb.CasMin(ref Volatile.Read(ref index.MinX)[slot], aabb.MinX);
            ClusterSpatialAabb.CasMin(ref Volatile.Read(ref index.MinY)[slot], aabb.MinY);
            ClusterSpatialAabb.CasMin(ref Volatile.Read(ref index.MinZ)[slot], aabb.MinZ);
            ClusterSpatialAabb.CasMax(ref Volatile.Read(ref index.MaxX)[slot], aabb.MaxX);
            ClusterSpatialAabb.CasMax(ref Volatile.Read(ref index.MaxY)[slot], aabb.MaxY);
            ClusterSpatialAabb.CasMax(ref Volatile.Read(ref index.MaxZ)[slot], aabb.MaxZ);
            Interlocked.Or(ref Volatile.Read(ref index.CategoryMasks)[slot], aabb.CategoryMask);
        }));

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly ClusterSpatialAabb Wide = Aabb(-50, -60, 70, 80, cat: 0x10u);

    private static void WidenDuringAGrowsCopy(Action<CellSpatialIndex, int, ClusterSpatialAabb> widen)
    {
        var index = FullIndexOfFour();
        using var widenReleased = new ManualResetEventSlim();
        using var widenSettled = new ManualResetEventSlim();   // the widen returned, or it is waiting the grow out
        index.WidenWaitProbe = widenSettled.Set;
        index.GrowCopiedProbe = () =>
        {
            widenReleased.Set();
            Await(widenSettled, "the widen neither returned nor waited for the grow");
        };

        var widener = new Widener(() =>
        {
            Await(widenReleased, "the grow never reached its copy");
            widen(index, 2, Wide);
        }, widenSettled);

        index.Add(clusterChunkId: 99, Aabb(9, 9, 10, 10));   // the fifth add grows 4 → 8 and parks in the probe
        widener.AssertReturned();
        AssertTheWidenLanded(index);
    }

    private static void WidenStampedBeforeAGrowsCopy(Action<CellSpatialIndex, int, ClusterSpatialAabb, Action> widen)
    {
        var index = FullIndexOfFour();
        using var widenStamped = new ManualResetEventSlim();
        using var growCopied = new ManualResetEventSlim();
        using var widenSettled = new ManualResetEventSlim();   // the widen returned, or it is waiting the grow out
        var parked = 0;
        Action parkOnStamp = () =>
        {
            // The first attempt only: a redo runs straight through.
            if (Interlocked.Exchange(ref parked, 1) == 0)
            {
                widenStamped.Set();
                Await(growCopied, "the grow never reached its copy");
            }
        };
        index.WidenWaitProbe = widenSettled.Set;
        index.GrowCopiedProbe = () =>
        {
            growCopied.Set();
            Await(widenSettled, "the widen neither returned nor waited for the grow");
        };

        var widener = new Widener(() => widen(index, 2, Wide, parkOnStamp), widenSettled);
        Await(widenStamped, "the widen never took its stamp");
        index.Add(clusterChunkId: 99, Aabb(9, 9, 10, 10));   // the fifth add grows 4 → 8 and parks in the probe
        widener.AssertReturned();
        AssertTheWidenLanded(index);
    }

    private static CellSpatialIndex FullIndexOfFour()
    {
        var index = new CellSpatialIndex(initialCapacity: 4);
        for (var i = 0; i < 4; i++)
        {
            index.Add(clusterChunkId: 10 + i, Aabb(i, i, i + 1, i + 1));
        }

        return index;
    }

    private static void AssertTheWidenLanded(CellSpatialIndex index)
    {
        Assert.That(index.Capacity, Is.EqualTo(8), "the add must have grown the index, or the widen raced nothing");
        var bound = (index.MinX[2], index.MinY[2], index.MaxX[2], index.MaxY[2], index.CategoryMasks[2]);
        Assert.That(bound, Is.EqualTo((-50f, -60f, 70f, 80f, 0x11u)), Ca02Marker);
    }

    private static void Await(ManualResetEventSlim signal, string what)
    {
        if (!signal.Wait(HandshakeTimeout))
        {
            throw new TimeoutException(what);
        }
    }

    /// <summary>Runs a widen on its own thread and sets <c>settled</c> when it returns, whether it threw or not.</summary>
    private sealed class Widener
    {
        private readonly Thread _thread;
        private Exception _failure;

        public Widener(Action widen, ManualResetEventSlim settled)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    widen();
                }
                catch (Exception e)
                {
                    _failure = e;
                }
                finally
                {
                    settled.Set();
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void AssertReturned()
        {
            Assert.That(_thread.Join(HandshakeTimeout), Is.True, "the widen never returned");
            Assert.That(_failure, Is.Null);
        }
    }

    private static void WidenAgainstTheIdWitness(CellSpatialIndex index, int slot, ClusterSpatialAabb aabb)
    {
        while (true)
        {
            var witness = Volatile.Read(ref index.ClusterIds);
            ClusterSpatialAabb.CasMin(ref Volatile.Read(ref index.MinX)[slot], aabb.MinX);
            ClusterSpatialAabb.CasMin(ref Volatile.Read(ref index.MinY)[slot], aabb.MinY);
            ClusterSpatialAabb.CasMin(ref Volatile.Read(ref index.MinZ)[slot], aabb.MinZ);
            ClusterSpatialAabb.CasMax(ref Volatile.Read(ref index.MaxX)[slot], aabb.MaxX);
            ClusterSpatialAabb.CasMax(ref Volatile.Read(ref index.MaxY)[slot], aabb.MaxY);
            ClusterSpatialAabb.CasMax(ref Volatile.Read(ref index.MaxZ)[slot], aabb.MaxZ);
            Interlocked.Or(ref Volatile.Read(ref index.CategoryMasks)[slot], aabb.CategoryMask);
            if (ReferenceEquals(Volatile.Read(ref index.ClusterIds), witness))
            {
                return;
            }
        }
    }
}
