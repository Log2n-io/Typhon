using System;
using System.Threading;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

// Issue #229 Q10 resolution: CellClusterPool is now a self-contained data structure owned by each ArchetypeClusterState. The API no longer takes a
// `ref CellState` — heads / counts / capacities all live on the pool itself so N archetypes sharing a grid never share pool state.
[TestFixture]
class CellClusterPoolTests
{
    [Test]
    public void NewPool_EmptyCell_HasZeroClusters()
    {
        var pool = new CellClusterPool(initialCellCapacity: 16);
        Assert.That(pool.GetClusters(cellKey: 5).Length, Is.EqualTo(0));
        Assert.That(pool.GetClusterCount(cellKey: 5), Is.EqualTo(0));
    }

    [Test]
    public void AddCluster_FirstEntry_AllocatesSegment()
    {
        var pool = new CellClusterPool(initialCellCapacity: 16);

        pool.AddCluster(cellKey: 5, clusterChunkId: 42);

        Assert.That(pool.GetClusterCount(cellKey: 5), Is.EqualTo(1));
        var span = pool.GetClusters(cellKey: 5);
        Assert.That(span.Length, Is.EqualTo(1));
        Assert.That(span[0], Is.EqualTo(42));
    }

    [Test]
    public void AddCluster_ManyEntries_InSameCell_PreservesOrder()
    {
        var pool = new CellClusterPool(initialCellCapacity: 16);
        for (int i = 0; i < 20; i++)
        {
            pool.AddCluster(cellKey: 3, clusterChunkId: 100 + i);
        }
        Assert.That(pool.GetClusterCount(cellKey: 3), Is.EqualTo(20));
        var span = pool.GetClusters(cellKey: 3);
        for (int i = 0; i < 20; i++)
        {
            Assert.That(span[i], Is.EqualTo(100 + i));
        }
    }

    [Test]
    public void AddCluster_MultipleCells_Independent()
    {
        var pool = new CellClusterPool(initialCellCapacity: 16);

        pool.AddCluster(cellKey: 1, clusterChunkId: 10);
        pool.AddCluster(cellKey: 2, clusterChunkId: 20);
        pool.AddCluster(cellKey: 1, clusterChunkId: 11);
        pool.AddCluster(cellKey: 2, clusterChunkId: 21);

        Assert.That(pool.GetClusters(cellKey: 1).ToArray(), Is.EqualTo(new[] { 10, 11 }));
        Assert.That(pool.GetClusters(cellKey: 2).ToArray(), Is.EqualTo(new[] { 20, 21 }));
    }

    [Test]
    public void RemoveCluster_SwapWithLast_RemovesMiddleEntry()
    {
        var pool = new CellClusterPool(initialCellCapacity: 16);
        pool.AddCluster(cellKey: 0, clusterChunkId: 1);
        pool.AddCluster(cellKey: 0, clusterChunkId: 2);
        pool.AddCluster(cellKey: 0, clusterChunkId: 3);
        pool.AddCluster(cellKey: 0, clusterChunkId: 4);

        bool removed = pool.RemoveCluster(cellKey: 0, clusterChunkId: 2);
        Assert.That(removed, Is.True);
        Assert.That(pool.GetClusterCount(cellKey: 0), Is.EqualTo(3));

        // Swap-with-last: 4 should have moved into slot 1 (where 2 was)
        var span = pool.GetClusters(cellKey: 0);
        Assert.That(span.ToArray(), Is.EquivalentTo(new[] { 1, 3, 4 }));
        // Specifically: first entry should still be 1, order of the remainder is swap-with-last.
        Assert.That(span[0], Is.EqualTo(1));
    }

    [Test]
    public void RemoveCluster_Missing_ReturnsFalse()
    {
        var pool = new CellClusterPool(initialCellCapacity: 16);
        pool.AddCluster(cellKey: 0, clusterChunkId: 1);
        bool removed = pool.RemoveCluster(cellKey: 0, clusterChunkId: 999);
        Assert.That(removed, Is.False);
        Assert.That(pool.GetClusterCount(cellKey: 0), Is.EqualTo(1));
    }

    [Test]
    public void RemoveCluster_AllEntries_LeavesEmptySegment()
    {
        var pool = new CellClusterPool(initialCellCapacity: 16);
        pool.AddCluster(cellKey: 0, clusterChunkId: 1);
        pool.AddCluster(cellKey: 0, clusterChunkId: 2);
        pool.RemoveCluster(cellKey: 0, clusterChunkId: 1);
        pool.RemoveCluster(cellKey: 0, clusterChunkId: 2);
        Assert.That(pool.GetClusterCount(cellKey: 0), Is.EqualTo(0));
        Assert.That(pool.GetClusters(cellKey: 0).Length, Is.EqualTo(0));
    }

    [Test]
    public void Grow_BeyondInitialCapacity_Succeeds()
    {
        var pool = new CellClusterPool(initialCellCapacity: 16, initialPoolCapacity: 16);
        // Force multiple capacity doublings
        for (int i = 0; i < 100; i++)
        {
            pool.AddCluster(cellKey: 0, clusterChunkId: i);
        }
        Assert.That(pool.GetClusterCount(cellKey: 0), Is.EqualTo(100));
        Assert.That(pool.PoolCapacity, Is.GreaterThanOrEqualTo(100));
        var span = pool.GetClusters(cellKey: 0);
        for (int i = 0; i < 100; i++)
        {
            Assert.That(span[i], Is.EqualTo(i));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Per-cell scan cursor (ClaimSlotInCell O(M²) re-scan collapse)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ScanCursor_New_DefaultsToZero()
    {
        var pool = new CellClusterPool(initialCellCapacity: 16);
        Assert.That(pool.GetScanCursor(cellKey: 7), Is.EqualTo(0));
    }

    [Test]
    public void AdvanceScanCursor_MovesForwardOnly()
    {
        var pool = new CellClusterPool(initialCellCapacity: 16);

        pool.AdvanceScanCursor(cellKey: 2, value: 5);
        Assert.That(pool.GetScanCursor(cellKey: 2), Is.EqualTo(5));

        // Forward advance moves it.
        pool.AdvanceScanCursor(cellKey: 2, value: 9);
        Assert.That(pool.GetScanCursor(cellKey: 2), Is.EqualTo(9));

        // Backward / equal advance is a no-op — the cursor is monotonic.
        pool.AdvanceScanCursor(cellKey: 2, value: 3);
        Assert.That(pool.GetScanCursor(cellKey: 2), Is.EqualTo(9));
        pool.AdvanceScanCursor(cellKey: 2, value: 9);
        Assert.That(pool.GetScanCursor(cellKey: 2), Is.EqualTo(9));
    }

    [Test]
    public void ScanCursor_PerCell_Independent()
    {
        var pool = new CellClusterPool(initialCellCapacity: 16);
        pool.AdvanceScanCursor(cellKey: 1, value: 4);
        pool.AdvanceScanCursor(cellKey: 2, value: 8);
        Assert.That(pool.GetScanCursor(cellKey: 1), Is.EqualTo(4));
        Assert.That(pool.GetScanCursor(cellKey: 2), Is.EqualTo(8));
        Assert.That(pool.GetScanCursor(cellKey: 3), Is.EqualTo(0));
    }

    [Test]
    public void ResetScanCursor_ReturnsToZero()
    {
        var pool = new CellClusterPool(initialCellCapacity: 16);
        pool.AdvanceScanCursor(cellKey: 0, value: 12);
        pool.ResetScanCursor(cellKey: 0);
        Assert.That(pool.GetScanCursor(cellKey: 0), Is.EqualTo(0));

        // After reset the cursor advances again normally.
        pool.AdvanceScanCursor(cellKey: 0, value: 3);
        Assert.That(pool.GetScanCursor(cellKey: 0), Is.EqualTo(3));
    }

    [Test]
    public void SetScanCursor_MovesBackward()
    {
        var pool = new CellClusterPool(initialCellCapacity: 16);
        pool.AdvanceScanCursor(cellKey: 4, value: 12);

        // SetScanCursor is unconditional — unlike AdvanceScanCursor it moves the cursor backward (phase-2 self-healing).
        pool.SetScanCursor(cellKey: 4, value: 3);
        Assert.That(pool.GetScanCursor(cellKey: 4), Is.EqualTo(3));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Single-writer guard
    //
    // The pool has no lock and deliberately cannot get a useful one: AddCluster writes _pool, bumps _tail and may Array.Resize, so a lock over the four
    // side arrays would leave exactly the corruption it appears to prevent. Its safety is a CALLER-side contract (ClaimSlotInCell holds _finalizeLock, the
    // rebuild reduce is serial, both RemoveCluster paths are serial for the archetype) that no signature expresses. EnterWriter turns that contract into
    // something checked, and these tests are what stop the check itself from being decorative.
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void AddCluster_WhileAnotherThreadIsMidMutation_ThrowsInsteadOfLosingTheAppend()
    {
        // Without the guard this is silent: both writers read the same `count`, both store count + 1, and one cluster ends up attached to a cell that
        // GetClusters never returns — entities in a cell no query finds, with no exception anywhere near the cause.
        var pool = new CellClusterPool(initialCellCapacity: 16);
        pool.AddCluster(cellKey: 1, clusterChunkId: 7);

        var ex = RunWhileAnotherThreadHoldsTheWriterSlot(pool, p => p.AddCluster(cellKey: 2, clusterChunkId: 9));

        Assert.That(ex, Is.InstanceOf<InvalidOperationException>(), "a concurrent structural mutation must be reported, not absorbed");
        Assert.That(ex.Message, Does.Contain(nameof(CellClusterPool.AddCluster)), "the message must name the offending entry point");
    }

    [Test]
    [CancelAfter(15_000)]
    public void RemoveCluster_WhileAnotherThreadIsMidMutation_ThrowsInsteadOfCorruptingTheSegment()
    {
        // Removal is swap-with-last, so a concurrent one drops an unrelated cluster id on the floor rather than merely losing a count.
        var pool = new CellClusterPool(initialCellCapacity: 16);
        pool.AddCluster(cellKey: 1, clusterChunkId: 7);
        pool.AddCluster(cellKey: 1, clusterChunkId: 8);

        var ex = RunWhileAnotherThreadHoldsTheWriterSlot(pool, p => p.RemoveCluster(cellKey: 1, clusterChunkId: 7));

        Assert.That(ex, Is.InstanceOf<InvalidOperationException>());
        Assert.That(ex.Message, Does.Contain(nameof(CellClusterPool.RemoveCluster)));
    }

    [Test]
    [CancelAfter(15_000)]
    public void WriterSlot_IsReleasedWhenAMutationThrows()
    {
        // AddCluster rejects a negative cell key from inside the guarded region. If the release were not in a finally, that one rejected call would poison
        // the pool for the rest of the process — every later mutation, on any thread, reporting a phantom concurrent writer. A caller that legitimately
        // probes with a -1 from SpatialGridAccessor is exactly how that would happen.
        var pool = new CellClusterPool(initialCellCapacity: 16);

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.AddCluster(cellKey: -1, clusterChunkId: 3));

        Assert.DoesNotThrow(() => pool.AddCluster(cellKey: 4, clusterChunkId: 3), "the failed call must not have left the writer slot claimed");
        Assert.That(pool.GetClusterCount(cellKey: 4), Is.EqualTo(1));
    }

    /// <summary>
    /// Run <paramref name="call"/> on a fresh thread while another thread sits inside the guarded region, and return whatever it threw (or null).
    /// </summary>
    /// <remarks>
    /// The holder is parked by calling <see cref="CellClusterPool.EnterWriter"/> directly rather than by racing two real mutations. That is the whole point:
    /// a race-based version of this test would pass or fail on scheduling luck, and a flaky guard test gets deleted long before the guard does.
    /// </remarks>
    private static Exception RunWhileAnotherThreadHoldsTheWriterSlot(CellClusterPool pool, Action<CellClusterPool> call)
    {
        var parked = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        Exception captured = null;

        var holder = new Thread(() =>
        {
            pool.EnterWriter("parked-by-the-test");
            parked.Set();
            release.Wait();
            pool.ExitWriter();
        })
        { IsBackground = true };
        holder.Start();
        parked.Wait();

        var offender = new Thread(() =>
        {
            try
            {
                call(pool);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        })
        { IsBackground = true };
        offender.Start();
        offender.Join();

        release.Set();
        holder.Join();
        return captured;
    }

    [Test]
    public void SetScanCursor_MovesForward()
    {
        var pool = new CellClusterPool(initialCellCapacity: 16);
        pool.SetScanCursor(cellKey: 4, value: 9);
        Assert.That(pool.GetScanCursor(cellKey: 4), Is.EqualTo(9));
    }
}
