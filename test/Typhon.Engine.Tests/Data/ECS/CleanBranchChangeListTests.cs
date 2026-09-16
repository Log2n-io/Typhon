using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ══════════════════════════════════════════════════════════════════════════
// #939 — the fence's clean branch builds a change list nothing on that path reads.
//
// Own archetype rather than a borrowed one: ArchetypeRegistry is process-global and unsynchronised across
// parallel fixtures (#720), so a fixture that shares another's archetype inherits its flakes.
// ══════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.Cbl.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CblPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class CblUnit : Archetype<CblUnit>
{
    public static readonly Comp<CblPos> Pos = Register<CblPos>();
}

/// <summary>
/// MD-02: the fence's change list may legitimately be absent, and a barrier-only archetype's clean branch is where that
/// happens (#939).
/// </summary>
/// <remarks>
/// <para><b>What the branch used to do.</b> On a tick whose dirty bitmap is empty, <c>PrepareArchetypeFenceCore</c>
/// allocated a <c>long[PrimarySegmentCapacity]</c> — ≥256 KB on the large-object heap for the SWG demo's Creature at
/// x64 — walked every active cluster, and read an occupancy word through the page cache for each one carrying a signal.
/// Then it handed the result to <c>DetectClusterMigrations</c>, which for a <c>SpatialBarrierOnly</c> archetype returns
/// at its step-(b) guard without reading a single word of it.</para>
/// <para><b>Why dropping it is equivalence rather than a trade.</b> The AABB refresh does not read the list either: for a
/// barrier-only archetype <c>RecomputeDirtyClusterAabbsSlice</c> takes its <c>ClusterProcessBitmap</c> arm and never
/// consults one — it is the non-barrier arm that gates on <c>ClusterNeedsAabbRecompute</c>. And the removed walk wrote a
/// word only where that same predicate was true, of which the process bit is one of three signals, so the clusters the
/// refresh visits are the process-bitmap set either way. Same clusters re-derived, one fewer array.
/// <see cref="TheRefreshStillReDerivesAClusterWhoseBoundGrew"/> and
/// <see cref="ABarrierOnlyCrossingIsStillDetectedWithoutAChangeList"/> are what hold that claim up.</para>
/// <para><b>The legacy half must not move.</b> An archetype that does NOT declare the barrier still needs the list,
/// because its crossings are found by scanning it — <see cref="ALegacyArchetypeStillGetsAChangeListOnTheCleanBranch"/>.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class CleanBranchChangeListTests : TestBase<CleanBranchChangeListTests>
{
    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    /// <summary>Repair off, for the reason the drift fixtures pin it: a re-pack changes the cluster shapes asserted on here.</summary>
    private DatabaseEngine SetupEngine(bool barrierOnly)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CblPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldMax, WorldMax),
            cellSize: CellSize,
            reclusterBudgetMs: 0f));
        dbe.InitializeArchetypes();
        if (barrierOnly)
        {
            dbe.SetSpatialBarrierOnly<CblUnit>();
        }

        return dbe;
    }

    private static CblPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<CblUnit>.Metadata.ArchetypeId].ClusterState;

    private static int ArchetypeId => Archetype<CblUnit>.Metadata.ArchetypeId;

    private static EntityId Spawn(DatabaseEngine dbe, float x, float y)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<CblUnit>(CblUnit.Pos.Set(PointAt(x, y)));
        tx.Commit();
        return id;
    }

    /// <summary>
    /// Moves an entity through <c>ClusterRef.WriteSpatial</c> — the barrier API. A fixture that declares
    /// <c>SetSpatialBarrierOnly</c> and then writes through <c>OpenMut</c> has broken the contract it just declared.
    /// </summary>
    private static unsafe void WriteSpatialTo(DatabaseEngine dbe, EntityId id, float x, float y)
    {
        var (chunkId, slot) = LocateSlot(dbe, id);
        Assert.That(chunkId, Is.GreaterThanOrEqualTo(0), "entity must be resident in a cluster before a barrier write");

        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<CblUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (cluster.ChunkId != chunkId)
                {
                    continue;
                }

                cluster.WriteSpatial(CblUnit.Pos, slot, PointAt(x, y));
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    private static unsafe (int ChunkId, int Slot) LocateSlot(DatabaseEngine dbe, EntityId id)
    {
        var cs = ClusterStateOf(dbe);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var cid = cs.ActiveClusterIds[i];
                var clusterBase = accessor.GetChunkAddress(cid);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (*(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8) == (long)id.RawValue)
                    {
                        return (cid, slot);
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return (-1, 0);
    }

    [Test]
    [VerifiesRule("MD-02")]
    public void ABarrierOnlyCleanTickPublishesNoChangeList()
    {
        using var dbe = SetupEngine(barrierOnly: true);
        var id = Spawn(dbe, 20f, 20f);
        dbe.WriteTickFence(1);

        // A move that grows the bound but stays well inside the cell: the process bit is set, so the tick has spatial work
        // to do, but nothing crosses and no migration is queued.
        WriteSpatialTo(dbe, id, 60f, 60f);
        dbe.WriteTickFence(2);

        var cs = ClusterStateOf(dbe);
        Assert.Multiple(() =>
        {
            Assert.That(cs.FenceBranchPath, Is.EqualTo(1), "precondition: the tick must have taken the clean branch");
            Assert.That(cs.FenceDirtyBits, Is.Null,
                "the clean branch built a change list for an archetype whose detection never scans one, and whose refresh reads the flags directly");
        });
    }

    [Test]
    [VerifiesRule("MD-02")]
    public void ABarrierOnlyCrossingIsStillDetectedWithoutAChangeList()
    {
        // The crossing is found by step (a) — the drain of ClusterMigrationPendingSlots that WriteSpatial fills at write
        // time — which is exactly why step (b)'s change list is redundant here. If dropping the array had broken detection,
        // this is the test that reddens.
        using var dbe = SetupEngine(barrierOnly: true);
        var id = Spawn(dbe, 50f, 50f);
        dbe.WriteTickFence(1);

        WriteSpatialTo(dbe, id, 250f, 50f);   // two cells east, far outside the 5-unit hysteresis margin
        dbe.WriteTickFence(2);

        var cs = ClusterStateOf(dbe);
        Assert.Multiple(() =>
        {
            // Without this the scenario could quietly route to branch 2 one day and the test would pass while covering nothing.
            Assert.That(cs.FenceBranchPath, Is.EqualTo(1), "precondition: the tick must have taken the clean branch");
            Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).MigrationCount, Is.EqualTo(1),
                "the barrier drain must still find the crossing with no change list to scan");
        });
    }

    [Test]
    [VerifiesRule("MD-02")]
    public void AMigrationTickStillGetsItsBufferBeforeTheMigratePhase()
    {
        // Observed through PrepQueueProbe rather than after the fence, and that distinction IS the test. A non-null array at
        // the end of the tick proves nothing: it is equally explained by GrowFenceDirtyBitsForChunkId firing on demand
        // inside ExecuteMigrations, so an after-the-fact assertion stays green even with the pre-size gate deleted. The
        // probe runs last in Prep's tail, deliberately — "so the probe observes the archetype exactly as the Migrate phase
        // will find it, including the pre-size" — which is the one moment where the two explanations differ. The on-demand
        // path yields max(dstChunkId + 1, 16); the pre-size yields LastPreSizeUpperBound, which is far larger.
        using var dbe = SetupEngine(barrierOnly: true);
        var id = Spawn(dbe, 50f, 50f);
        dbe.WriteTickFence(1);

        long[] bufferAtPrepEnd = null;
        var preSizeBound = 0;
        var queuedAtPrepEnd = 0;
        var branchPath = -1;
        ArchetypeClusterState.PrepQueueProbe = (state, _) =>
        {
            if (state.ArchetypeId != ArchetypeId)
            {
                return;
            }

            bufferAtPrepEnd = state.FenceDirtyBits;
            preSizeBound = state.LastPreSizeUpperBound;
            queuedAtPrepEnd = state.PendingMigrationCount;
            branchPath = state.FenceBranchPath;
        };
        try
        {
            WriteSpatialTo(dbe, id, 250f, 50f);
            dbe.WriteTickFence(2);
        }
        finally
        {
            ArchetypeClusterState.PrepQueueProbe = null;
        }

        Assert.Multiple(() =>
        {
            Assert.That(branchPath, Is.EqualTo(1), "precondition: the tick must have taken the clean branch");
            Assert.That(queuedAtPrepEnd, Is.GreaterThan(0), "precondition: the tick must have queued a migration to execute");
            Assert.That(bufferAtPrepEnd, Is.Not.Null,
                "the Migrate phase would have begun with no buffer for its dirty-bit deltas");
            Assert.That(bufferAtPrepEnd.Length, Is.GreaterThanOrEqualTo(preSizeBound),
                "the buffer was grown on demand inside ExecuteMigrations rather than pre-sized in Prep's tail, which is the thing this gate exists to keep");
        });
    }

    [Test]
    [VerifiesRule("MD-02")]
    public void TheBranchOneChangeListIsReusedAcrossTicks_AndHandedOutZeroed()
    {
        // #963. The buffer the Migrate phase writes its dirty-bit deltas into used to be a fresh long[upperBound] on every tick with a crossing — ~273 KB on
        // the large-object heap per archetype per tick at the demo's x64. It is now retained and reused, which is safe on THIS branch only: branch 2 hands
        // its array to the next tick as PreviousTickDirtySnapshot, branch 1 sets that to null and returns before the assignment.
        //
        // Two things are asserted, and the second is the one that matters. Reuse is the performance claim. Being handed out ZEROED is the correctness claim:
        // the previous tick's Migrate left destination bits set, and a stale bit makes an untouched cluster read as dirty to ClusterNeedsAabbRecompute.
        using var dbe = SetupEngine(barrierOnly: true);
        var id = Spawn(dbe, 50f, 50f);
        dbe.WriteTickFence(1);

        var buffers = new List<long[]>();
        var staleBitObserved = 0;
        ArchetypeClusterState.PrepQueueProbe = (state, _) =>
        {
            if (state.ArchetypeId != ArchetypeId || state.PendingMigrationCount <= 0)
            {
                return;
            }

            var bits = state.FenceDirtyBits;
            if (bits == null)
            {
                return;
            }

            buffers.Add(bits);
            foreach (var word in bits)
            {
                if (word != 0)
                {
                    staleBitObserved++;
                    break;
                }
            }
        };

        try
        {
            // Two crossings on consecutive ticks, so the buffer is rented twice with a Migrate phase in between to dirty it.
            WriteSpatialTo(dbe, id, 250f, 50f);
            dbe.WriteTickFence(2);
            WriteSpatialTo(dbe, id, 450f, 50f);
            dbe.WriteTickFence(3);
        }
        finally
        {
            ArchetypeClusterState.PrepQueueProbe = null;
        }

        Assert.Multiple(() =>
        {
            Assert.That(buffers, Has.Count.EqualTo(2), "both ticks must have queued a migration, or there is nothing to reuse");
            Assert.That(staleBitObserved, Is.Zero,
                "the reused buffer was handed to the Migrate phase still carrying the previous tick's bits — an untouched cluster now reads as dirty");
            Assert.That(ReferenceEquals(buffers[0], buffers[1]), Is.True,
                "the change list was reallocated rather than reused, which is the large-object-heap allocation this change removes");
        });
    }

    [Test]
    [VerifiesRule("MD-02")]
    public void ALegacyArchetypeStillGetsAChangeListOnTheCleanBranch()
    {
        // The guard on the other side: an archetype that has NOT declared the barrier finds its crossings by scanning this
        // list, so the clean branch must keep building one for it. A settled tick is the clean branch for a legacy
        // archetype too — nothing was written, so the dirty bitmap is empty.
        using var dbe = SetupEngine(barrierOnly: false);
        Spawn(dbe, 20f, 20f);
        dbe.WriteTickFence(1);
        dbe.WriteTickFence(2);

        var cs = ClusterStateOf(dbe);
        Assert.Multiple(() =>
        {
            Assert.That(cs.FenceBranchPath, Is.EqualTo(1), "precondition: a settled spatial archetype takes the clean branch");
            Assert.That(cs.FenceDirtyBits, Is.Not.Null,
                "the legacy scan lost the change list it detects crossings from");
        });
    }

    [Test]
    [VerifiesRule("MD-02")]
    public void TheRefreshStillReDerivesAClusterWhoseBoundGrew()
    {
        // The equivalence claim, stated as behaviour: with no change list, ClusterNeedsAabbRecompute falls through to the
        // process bit WriteSpatial set — so the bound must still follow the entity. A refresh that started skipping this
        // cluster would leave a bound that no longer contains its entities, which is CA-01's silent failure.
        using var dbe = SetupEngine(barrierOnly: true);
        var id = Spawn(dbe, 10f, 10f);
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        var (chunkId, _) = LocateSlot(dbe, id);
        Assert.That(cs.ClusterAabbs[chunkId].MaxX, Is.EqualTo(10f), "precondition: the bound is exact before the move");

        WriteSpatialTo(dbe, id, 70f, 70f);
        dbe.WriteTickFence(2);

        Assert.Multiple(() =>
        {
            Assert.That(cs.FenceDirtyBits, Is.Null, "precondition: this tick published no change list");
            Assert.That(cs.ClusterAabbs[chunkId].MaxX, Is.EqualTo(70f), "the bound must still follow the entity that moved");
            Assert.That(cs.ClusterAabbs[chunkId].MaxY, Is.EqualTo(70f), "same on Y — the refresh must re-derive every axis");
        });
    }
}
