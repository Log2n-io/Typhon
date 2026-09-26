using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.Pcp.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct PcpPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class PcpUnit : Archetype<PcpUnit>
{
    public static readonly Comp<PcpPos> Pos = Register<PcpPos>();
}

/// <summary>
/// CA-02: a spawn racing a cell's promotion to a tree must not observe the retired back-pointers promotion leaves behind (#940).
/// </summary>
/// <remarks>
/// <para><b>The window.</b> <c>PromoteCellHalf</c> rebuilds a cell half from its linear index into a <c>CellClusterTree</c>. It retires every linear slot
/// index in the shared <c>ClusterSpatialIndexSlot</c> array to <c>NullHandle</c>, re-issues packed tree handles into the same array, then publishes the
/// tree. It holds <c>_finalizeLock</c> throughout; the spawn path did not take that latch for its "is this cluster already indexed" read.</para>
/// <para><b>What the lie costs.</b> Reading a retired handle, the commit concludes the cluster is not indexed, resets its <c>ClusterAabbs</c> entry to
/// <c>Empty</c> — discarding every concurrent spawner's widening — and re-adds it to the cell, where <c>CellClusterTree.Add</c>'s duplicate guard throws out
/// of the middle of a commit.</para>
/// <para><b>Two seams, because one is not enough.</b> The first version of this fixture parked only the promoter and let the racer run a whole
/// <c>SpawnInto</c> afterwards; it passed with the fix ABLATED, because the racer never reached the index read while the window was open. It therefore
/// proved nothing. The racer now parks in <see cref="ArchetypeClusterState.SpawnIndexReadProbe"/> — fired immediately before the read — and is released
/// only once the promoter reports itself inside the retire window, so the interleaving is driven rather than hoped for.</para>
/// <para><b>The handshake is one-way on purpose.</b> Promotion holds <c>_finalizeLock</c> while parked and the fixed read blocks on that same latch, so a
/// promoter that waited for the racer to FINISH would deadlock against its own fix. It waits only for the racer to reach the read.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class CellPromotionSpawnRaceTests : TestBase<CellPromotionSpawnRaceTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    /// <summary>Small, so a few dozen spawns into one cell cross it and promotion runs inside the test rather than at some unrelated moment.</summary>
    private const int PromoteAt = 4;

    private static readonly TimeSpan Handshake = TimeSpan.FromSeconds(10);

    private static PcpPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<PcpUnit>.Metadata.ArchetypeId].ClusterState;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<PcpPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize, reclusterBudgetMs: 0f));

        // Both must precede InitializeArchetypes — they are copied onto the cluster state as it is built. Tightness off: this fixture is about the
        // promotion WINDOW, not the gate that decides which cells deserve a tree.
        dbe.ClusterCellTreePromoteThreshold = PromoteAt;
        dbe.ClusterCellTreePromoteTightness = 1f;
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Spawns into one cell, one entity per transaction, so each commit is its own chance to land inside the window.</summary>
    private static void SpawnInto(DatabaseEngine dbe, int count, float baseX, float baseY)
    {
        for (var i = 0; i < count; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Spawn<PcpUnit>(PcpUnit.Pos.Set(PointAt(baseX + (i % 40) * 0.5f, baseY + (i % 7) * 0.5f)));
            tx.Commit();
        }
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("CA-02")]
    public void ASpawnRacingACellsPromotion_DoesNotObserveTheRetiredBackPointers()
    {
        using var dbe = SetupEngine();
        var cs = ClusterStateOf(dbe);

        // Fill one cell to just under the promotion threshold, leaving ONE free slot in an existing cluster.
        //
        // The free slot is what makes this fixture bite, and its absence is why the first version passed against the defect. A cell filled to exactly
        // 64 x (PromoteAt - 1) has only FULL clusters, so the racer's spawn opens a brand-new one — whose back-pointer was never in the linear index
        // promotion retires, making "not indexed" the correct answer and leaving nothing to corrupt. Landing the racer in an EXISTING cluster puts its
        // back-pointer among the retired handles, which is the read #940 is about.
        SpawnInto(dbe, (64 * (PromoteAt - 1)) - 1, 10f, 10f);
        Assert.That(cs.Realm0Spatial.PromotedCellCount, Is.Zero, "precondition: the cell must not have promoted yet");
        var indexedBefore = 0;
        for (var i = 0; i < cs.ActiveClusterCount; i++)
        {
            if (cs.ClusterCellMap[cs.ActiveClusterIds[i]] >= 0 && cs.ClusterSpatialIndexSlot[cs.ActiveClusterIds[i]] >= 0)
            {
                indexedBefore++;
            }
        }

        Assert.That(indexedBefore, Is.GreaterThan(0), "precondition: the cell must already hold indexed clusters for promotion to retire");

        using var promoterInWindow = new ManualResetEventSlim(false);
        using var racerAtRead = new ManualResetEventSlim(false);
        Exception racerFault = null;
        var probeFired = 0;
        var racerParked = 0;

        // Fired immediately before the racer's "is this cluster indexed" read: park until the promoter is inside the retire window.
        cs.SpawnIndexReadProbe = () =>
        {
            if (Thread.CurrentThread.ManagedThreadId != Volatile.Read(ref _racerThreadId) || Interlocked.Exchange(ref racerParked, 1) != 0)
            {
                return;
            }

            racerAtRead.Set();
            promoterInWindow.Wait(Handshake);
        };

        cs.PromoteRetiredProbe = () =>
        {
            if (Interlocked.Exchange(ref probeFired, 1) != 0)
            {
                return;
            }

            // Release the racer INTO the read, then hold the window open while it is in there.
            promoterInWindow.Set();
            racerAtRead.Wait(Handshake);
            Thread.SpinWait(400_000);
        };

        var racer = Task.Run(() =>
        {
            Volatile.Write(ref _racerThreadId, Thread.CurrentThread.ManagedThreadId);
            try
            {
                // ONE entity, at the pre-fill's own coordinates, so first fit puts it in the partially-full cluster left above rather than opening a new one.
                SpawnInto(dbe, 1, 10f, 10f);
            }
            catch (Exception e)
            {
                racerFault = e;
            }
        });

        try
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref _racerThreadId) != 0, Handshake);
            SpawnInto(dbe, 64, 14f, 14f);   // crosses the promotion threshold
            Assert.That(racer.Wait(Handshake), Is.True, "the racing spawner never finished — the promotion window deadlocked against it");
        }
        finally
        {
            cs.PromoteRetiredProbe = null;
            cs.SpawnIndexReadProbe = null;
        }

        Assert.Multiple(() =>
        {
            Assert.That(probeFired, Is.EqualTo(1), "the cell never promoted, so the race this fixture is about never happened");
            Assert.That(racerParked, Is.EqualTo(1), "the racing spawner never reached the index read, so it was never inside the window");
            Assert.That(racerFault, Is.Null,
                $"the racing spawn threw out of its commit — CellClusterTree.Add's duplicate guard is the expected face. Got: {racerFault}");
            AssertEveryClusterIsIndexedExactlyOnce(cs);
            AssertEveryBoundContainsItsEntities(dbe, cs);
        });
    }

    private int _racerThreadId;

    /// <summary>Face 1's lasting damage: a cluster left carrying a retired handle belongs to no index at all.</summary>
    private static void AssertEveryClusterIsIndexedExactlyOnce(ArchetypeClusterState cs)
    {
        var live = 0;
        for (var i = 0; i < cs.ActiveClusterCount; i++)
        {
            var chunkId = cs.ActiveClusterIds[i];
            if (cs.ClusterCellMap[chunkId] < 0)
            {
                continue;
            }

            Assert.That(SpatialRTree<TransientStore>.IsNullHandle(cs.ClusterSpatialIndexSlot[chunkId]), Is.False,
                $"cluster {chunkId} carries a retired back-pointer after the race — it is in no index at all (SQ-01)");
            live++;
        }

        Assert.That(live, Is.GreaterThan(0), "sanity: the fixture must leave live clusters behind");
    }

    /// <summary>Faces 2 and 3: a box reset to Empty, or a widen dropped, leaves entities outside their own cluster's bound (CA-01).</summary>
    private static unsafe void AssertEveryBoundContainsItsEntities(DatabaseEngine dbe, ArchetypeClusterState cs)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<PcpUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var chunkId = cluster.ChunkId;
                var cellKey = cs.ClusterCellMap[chunkId];
                if (cellKey < 0)
                {
                    continue;
                }

                ref var box = ref cs.ClusterAabbs[chunkId];
                Assert.That(float.IsPositiveInfinity(box.MinX), Is.False,
                    $"cluster {chunkId} holds entities but its bound was reset to Empty — a spawn acted on the retired back-pointers (CA-01)");

                dbe.Realm0Grid.CellOrigin(cellKey, out var originX, out var originY, out _);
#pragma warning disable TYPHON009
                var positions = cluster.GetSpan(PcpUnit.Pos);
#pragma warning restore TYPHON009
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    ref readonly var b = ref positions[slot].Bounds;
                    Assert.That((float)(b.MinX - originX), Is.InRange(box.MinX, box.MaxX),
                        $"cluster {chunkId} slot {slot} sits outside its own bound on X — a widen was lost in the promotion window (CA-01)");
                    Assert.That((float)(b.MinY - originY), Is.InRange(box.MinY, box.MaxY),
                        $"cluster {chunkId} slot {slot} sits outside its own bound on Y — a widen was lost in the promotion window (CA-01)");
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }
}
