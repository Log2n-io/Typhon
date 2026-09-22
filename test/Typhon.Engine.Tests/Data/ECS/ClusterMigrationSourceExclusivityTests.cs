using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>CR-05</c> — one source slot, one request (#877).
/// </summary>
/// <remarks>
/// <para><b>The defect this fixture exists for was worth three symptoms and no diagnosis.</b> A sustained-motion workload lost entities on a few percent of
/// seeds, and surfaced as <c>InvalidOperationException: Entity(...) not found or not visible</c>, as
/// <c>B+Tree bulk update reached an invalid child</c>, and once as an <c>AccessViolationException</c> — three failures far enough apart to look like three
/// bugs. All three were one cause: two queued migrations naming the same <c>(cluster, slot)</c>.</para>
/// <para><b>Why the engine does not catch it on its own.</b> <c>ExecuteMigrations</c> has a stale-source guard, and it looks like it should cover exactly
/// this — but it tests OCCUPANCY, not identity. It therefore saves the case where the slot is still empty when the second request drains, and misses the
/// case where an unrelated migrant has claimed it, which the repair path makes likely because it allocates fresh destinations and frees drained sources
/// inside one fence. The second request then moves the WRONG entity to a destination reserved for someone else.</para>
/// <para><b>Two producers, two exclusions, asserted separately.</b> The throttle drops a relocation a mandatory request supersedes; the repair planner
/// refuses to gather a slot already claimed. Neither subsumes the other — the first is cross-tick (drift is detected in AabbRefresh and decided by the next
/// Prep), the second is same-tick (the crossing detector and the planner both run in Prep).</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterMigrationSourceExclusivityTests : TestBase<ClusterMigrationSourceExclusivityTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    /// <summary>
    /// 250 over a 1 000 world is a 4x4 grid, and the size is load-bearing rather than arbitrary.
    /// </summary>
    /// <remarks>
    /// The collision needs a relocation and a crossing for the SAME entity on consecutive ticks, so the workload has to produce both at once. A finer grid
    /// gives plenty of crossings but spreads the population so thin that each cell holds one cluster inside its target extent — no drifters, no relocations,
    /// nothing to supersede. At 250 each cell holds ~56 entities across two clusters wide enough to drift, and a quarter-cell step still crosses cell
    /// boundaries regularly. Measured at 100: zero supersedes in 15 ticks, and the test said so rather than passing vacuously.
    /// </remarks>
    private const float CellSize = 250f;

    private const float WorldMax = 1000f;

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[ArchetypeId].ClusterState;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    /// <summary>An engine with drift, repair and the valve all live — the configuration the defect needs, and the one the other throttle fixtures suppress.</summary>
    private DatabaseEngine SetupEngine(float budgetMs)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize, reclusterBudgetMs: budgetMs,
            // Constant-mode target (step 14): at ~56 entities per cell the density-derived target is off, and this fixture needs relocations.
            clusterTargetPackingSlack: 0f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The throttle's supersede rule — a relocation whose entity is already leaving under a crossing
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A relocation is dropped when a mandatory request already names its source slot, and is counted as superseded rather than throttled.
    /// </summary>
    /// <remarks>
    /// Driven directly against the queue rather than through a motion workload, because the collision is a property of the QUEUE and reproducing it through
    /// motion takes thousands of entities and a hundred ticks — which is a benchmark, not a test. The end-to-end arm below covers the same ground
    /// statistically; this one pins the rule.
    /// </remarks>
    [Test]
    [VerifiesRule("CR-05")]
    public void ARelocationSharingASourceSlotWithACrossing_IsDroppedAndCountedAsSuperseded()
    {
        using var dbe = SetupEngine(budgetMs: 1000f);   // deliberately generous: the drop must not be the budget's doing
        var state = ClusterStateOf(dbe);

        // Cluster 7 slot 3 is claimed by a crossing; the relocation for the same slot must not survive. The other two are controls: a relocation on a
        // different slot of the same cluster, and one on the same slot index of a different cluster.
        state.PendingMigrationCount = 0;
        state.EnqueueMigration(new MigrationRequest(7, 3, 42));
        state.EnqueueMigration(new MigrationRequest(7, 3, 11, 5, MigrationRequest.AnySlot, MigrationKind.Relocation));
        state.EnqueueMigration(new MigrationRequest(7, 4, 11, 5, MigrationRequest.AnySlot, MigrationKind.Relocation));
        state.EnqueueMigration(new MigrationRequest(9, 3, 11, 5, MigrationRequest.AnySlot, MigrationKind.Relocation));

        state.ApplyMigrationThrottle(dbe.SpatialGrid);

        var survivors = new List<string>();
        for (var i = 0; i < state.PendingMigrationCount; i++)
        {
            ref readonly var r = ref state.PendingMigrations[i];
            survivors.Add($"{r.SourceClusterChunkId}:{r.SourceSlotIndex}:{r.Kind}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(survivors, Has.Count.EqualTo(3),
                $"expected the crossing plus the two non-colliding relocations, got [{string.Join(", ", survivors)}]");
            Assert.That(survivors, Does.Contain("7:3:CellCrossing"), "the crossing is mandatory and must never be dropped");
            Assert.That(survivors, Does.Not.Contain("7:3:Relocation"),
                "the relocation names a source slot the crossing already claims — whichever drains second migrates whatever occupies the slot by then");
            Assert.That(survivors, Does.Contain("7:4:Relocation"), "a different slot of the same cluster does not collide");
            Assert.That(survivors, Does.Contain("9:3:Relocation"), "the same slot index of a different cluster does not collide");
            Assert.That(state.LastTickRelocationsSuperseded, Is.EqualTo(1), "the drop must be counted, and counted as superseded");
            Assert.That(state.LastTickRelocationsThrottled, Is.Zero,
                "a superseded relocation was refused by a crossing, not by the budget — folding it into RelocationsThrottled reports budget pressure that "
                + "does not exist");
        });
    }

    /// <summary>Only a MANDATORY request supersedes: a relocation leaves no claim behind.</summary>
    /// <remarks>
    /// The distinction matters because throttled relocations are dropped rather than carried, so the queue never holds two relocations for one slot — and a
    /// filter that recorded relocation sources anyway would silently drop the surviving one whenever detection legitimately re-filed it.
    /// </remarks>
    [Test]
    [VerifiesRule("CR-05")]
    public void ARelocationDoesNotSupersede_OnlyMandatoryRequestsDo()
    {
        using var dbe = SetupEngine(budgetMs: 1000f);
        var state = ClusterStateOf(dbe);

        state.PendingMigrationCount = 0;
        state.EnqueueMigration(new MigrationRequest(7, 3, 11, 5, MigrationRequest.AnySlot, MigrationKind.Relocation));
        state.EnqueueMigration(new MigrationRequest(8, 3, 11, 5, MigrationRequest.AnySlot, MigrationKind.Relocation));

        state.ApplyMigrationThrottle(dbe.SpatialGrid);

        Assert.Multiple(() =>
        {
            Assert.That(state.PendingMigrationCount, Is.EqualTo(2), "neither relocation collides, and the budget is generous");
            Assert.That(state.LastTickRelocationsSuperseded, Is.Zero, "nothing mandatory was queued, so nothing could be superseded");
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // End to end — crossings, drift and repair all firing on the same ticks
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// No two requests in a tick's drain prefix name the same source slot, on a world where crossings, drift and repair all fire.
    /// </summary>
    /// <remarks>
    /// <para>This is the arm that found the defect. The engine's own <c>AssertNoDuplicateMigrationSources</c> runs on every Prep in DEBUG, so most of the
    /// assertion here is the WORKLOAD: the test's job is to produce ticks where all three producers file at once, which needs entities that cross cells
    /// (crossings), clusters wider than their target extent (drift), and cells degraded enough to nominate (repair).</para>
    /// <para><b>The budget is the one that failed.</b> Measured over the band, losses peaked at 1 ms and vanished at both ends — below it the planner admits
    /// nothing, above it a cell is repaired in one unit rather than left part-packed with the rest of its entities still queued. A fixture at an arbitrary
    /// budget would very likely have sat in one of the clean bands and proved nothing.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("CR-05")]
    public void AcrossCrossingsDriftAndRepair_NoTwoRequestsShareASourceSlot()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        var rng = new Random(877);

        const int population = 900;
        var ids = new List<EntityId>(population);
        var xs = new float[population];
        var ys = new float[population];

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < population; i++)
            {
                xs[i] = (float)(rng.NextDouble() * WorldMax);
                ys[i] = (float)(rng.NextDouble() * WorldMax);
                ids.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(xs[i], ys[i], i))));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var sawSuperseded = false;
        for (var tick = 2; tick <= 16; tick++)
        {
            // A quarter-cell step, which is what makes an entity both a drifter (it leaves its cluster's target region) and, often, a crosser (it leaves
            // the cell) — the overlap the supersede rule exists for.
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < population; i++)
                {
                    xs[i] = Math.Clamp(xs[i] + (float)((rng.NextDouble() - 0.5) * CellSize * 0.5f), 2f, WorldMax - 2f);
                    ys[i] = Math.Clamp(ys[i] + (float)((rng.NextDouble() - 0.5) * CellSize * 0.5f), 2f, WorldMax - 2f);
                    tx.OpenMut(ids[i]).Write(ClMigUnit.Pos) = PointAt(xs[i], ys[i], i);
                }

                tx.Commit();
            }

            // The DEBUG assertion inside Prep is what actually checks CR-05; reaching the next tick at all is the pass condition.
            dbe.WriteTickFence(tick);
            sawSuperseded |= dbe.GetSpatialTelemetry(ArchetypeId).RelocationsSuperseded > 0;
        }

        // Every entity still resolves. Before the fix this is where the run died — a request naming a slot its entity no longer occupied moved someone
        // else, leaving that entity's EntityMap entry and index rows pointing at storage it does not live in.
        Assert.DoesNotThrow(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < population; i++)
            {
                tx.OpenMut(ids[i]).Write(ClMigUnit.Pos) = PointAt(xs[i], ys[i], i);
            }

            tx.Commit();
        }, "an entity became unreachable after sustained migration");

        Assert.That(sawSuperseded, Is.True,
            "no relocation was ever superseded by a crossing, so this workload never produced the collision and proves nothing about the fix");
    }
}
