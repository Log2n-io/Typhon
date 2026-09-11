using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-12.1</c> through <c>AC-12.6</c> — the repair path, a full intra-cell Morton re-sort and re-pack (#872 step 12).
/// </summary>
/// <remarks>
/// <para><b>What separates this from step 10's fixtures.</b> Those assert that the right entities are chosen and that each
/// one lands somewhere better. This asserts a property of the WHOLE cell after one pass — mean cluster extent, zone-map
/// width, and the invariants the re-pack could break wholesale. A repair that moved every entity to a legal-but-wrong slot
/// would satisfy every step-10 assertion and fail every one here.</para>
/// <para><b>The degraded cell is built by spawning in scattered order, not by moving anything.</b> <c>ClaimSlotInCell</c>
/// fills first-fit in spawn order, so spawning positions that jump around the cell puts geometrically unrelated entities
/// in the same cluster — which is exactly the decayed layout, arrived at through the engine's own placement rather than
/// through a test-only poke at cluster storage. That matters: a fixture that reached in and wrote the bad layout directly
/// would also be free to write one the engine can never produce.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterRepairTests : TestBase<ClusterRepairTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    /// <summary>Entities spawned into the single cell under test — 41 clusters at 49 slots each.</summary>
    /// <remarks>
    /// <b>Not a smaller number, and the reason is the curve rather than the code.</b> A Morton packing groups a
    /// CONTIGUOUS RUN of the curve into each cluster, and a run's bounding box is square only when the run aligns with a
    /// quadtree cell. At four or five clusters almost no run does, so the achieved mean sits ~1.45x the theoretical
    /// optimum and a threshold tuned there would be measuring the curve's coarse structure. At forty it is the same
    /// ~1.47x but against an optimum ten times tighter, so the improvement the re-sort actually delivers is the dominant
    /// term. Measured: 220 entities give 86.4 -> 64.2 (optimum 44.7); 2 000 give 87.0 -> 23.0 (optimum 15.6).
    /// </remarks>
    private const int Population = 2000;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClMigUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>
    /// Build an engine whose repair path is armed, with every knob the tests need to steer passed explicitly.
    /// </summary>
    /// <remarks>
    /// <paramref name="budgetMs"/> is the seam <c>AC-12.5</c> drives. <paramref name="worstClustersPerUnit"/> at 0 means
    /// the whole cell, which is what the tightness and zone-map assertions want — a unit of eight clusters repairs eight
    /// and leaves the rest, so a cell-wide mean would be diluted by the untouched remainder and would measure the unit
    /// size rather than the re-sort.
    /// </remarks>
    private DatabaseEngine SetupEngine(float budgetMs = 100f, int worstClustersPerUnit = 0, float repairExtentRatio = 0.75f)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0),
            new Vector2(WorldMax, WorldMax),
            CellSize,
            clusterRepairExtentRatio: repairExtentRatio,
            reclusterBudgetMs: budgetMs, batchSpawnSortThreshold: 0 /* step 15: this fixture builds its layout by spawn ORDER; the Morton sort would tighten it at birth */,
            repairWorstClustersPerUnit: worstClustersPerUnit));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// Spawn <see cref="Population"/> entities scattered across cell (0,0) in an order uncorrelated with position.
    /// </summary>
    /// <remarks>
    /// The two coprime strides are what make the order uncorrelated: consecutive spawns land far apart, so every cluster
    /// first-fit fills ends up holding points from all over the cell. A margin of 4 keeps every point strictly inside the
    /// cell, so nothing here is a cell-crossing migration in disguise. The 0.2 sub-step separates the entities that would
    /// otherwise land on identical coordinates once the population exceeds the 92 x 92 lattice the strides walk — without
    /// it a fifth of the population shares a Morton key with someone else, and the sort's tie-break rather than its
    /// ordering would be deciding the packing.
    /// </remarks>
    private static List<EntityId> SpawnDegradedCell(DatabaseEngine dbe)
    {
        var ids = new List<EntityId>(Population);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Population; i++)
            {
                var x = 4f + ((i * 37) % 92) + ((i / 92) % 4) * 0.2f;
                var y = 4f + ((i * 61) % 92) + ((i / 92) % 4) * 0.2f;
                ids.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(x, y, i))));
            }
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return ids;
    }

    /// <summary>Resolve one entity THROUGH THE ENTITY MAP and return what its current slot actually holds.</summary>
    /// <remarks>
    /// <b>This is the point of the whole check, not a convenience.</b> Reading cluster storage directly proves the bytes
    /// moved; resolving by id proves the map that finds them moved with them. A re-pack that copies every entity
    /// correctly and forgets one <c>EntityMap</c> patch is invisible to a storage walk and fatal in production — the stale
    /// entry resolves to whatever a later spawn puts in the vacated slot.
    /// </remarks>
    private static List<(float x, float y, int tag)> ResolveAllThroughMap(DatabaseEngine dbe, List<EntityId> ids)
    {
        var result = new List<(float, float, int)>(ids.Count);
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < ids.Count; i++)
        {
            var eref = tx.Open(ids[i]);
            ref readonly var pos = ref eref.Read(ClMigUnit.Pos);
            ref readonly var b = ref pos.Bounds;
            result.Add((0.5f * (b.MinX + b.MaxX), 0.5f * (b.MinY + b.MaxY), pos.Tag));
        }

        return result;
    }

    /// <summary>Mean of the per-cluster maximum axis extent, over every cluster of cell 0 that holds an entity.</summary>
    private static (double mean, int clusters) MeanClusterExtent(DatabaseEngine dbe)
    {
        var state = ClusterStateOf(dbe);
        var clusters = state.CellClusterPool.GetClusters(0);
        var total = 0d;
        var counted = 0;
        for (var i = 0; i < clusters.Length; i++)
        {
            var chunkId = clusters[i];
            if ((uint)chunkId >= (uint)state.ClusterAabbs.Length)
            {
                continue;
            }

            ref var box = ref state.ClusterAabbs[chunkId];
            if (float.IsPositiveInfinity(box.MinX))
            {
                continue;
            }

            total += MathF.Max(box.MaxX - box.MinX, box.MaxY - box.MinY);
            counted++;
        }

        return counted == 0 ? (0d, 0) : (total / counted, counted);
    }

    /// <summary>Total occupied slots across every cluster of the archetype — the population, read from the authoritative bitmaps.</summary>
    /// <remarks>
    /// Deliberately a popcount over occupancy rather than a count of the entities the enumerator yields: those are the same
    /// number only when storage and its bitmaps agree, and a re-pack that set a bit without filling it, or filled a slot
    /// without setting one, is exactly the defect worth separating from "the right entities are present".
    /// </remarks>
    private static unsafe int TotalOccupancy(DatabaseEngine dbe)
    {
        var total = 0;
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                total += System.Numerics.BitOperations.PopCount(cluster.OccupancyBits);
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return total;
    }

    /// <summary>Every live entity, as (entityId, chunkId, slot, x, y), read straight from cluster storage.</summary>
    private static unsafe List<(long id, int chunk, int slot, float x, float y)> ReadAll(DatabaseEngine dbe)
    {
        var result = new List<(long, int, int, float, float)>();
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
#pragma warning disable TYPHON009 // Read-only.
                var positions = cluster.GetSpan(ClMigUnit.Pos);
#pragma warning restore TYPHON009
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    ref readonly var b = ref positions[slot].Bounds;
                    result.Add((positions[slot].Tag, cluster.ChunkId, slot, 0.5f * (b.MinX + b.MaxX), 0.5f * (b.MinY + b.MaxY)));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return result;
    }

    /// <summary>Run ticks until the repair planner has admitted at least one unit, or the tick budget runs out.</summary>
    /// <remarks>
    /// Nomination happens in the AABB pass and planning in the NEXT tick's Prep, so a repair is never observable on the
    /// tick that provoked it. Looping rather than hard-coding the count keeps the test honest about that latency instead
    /// of encoding one particular value of it.
    /// </remarks>
    private static int TickUntilRepaired(DatabaseEngine dbe, int firstTick, out SpatialMigrationTelemetry atRepair, int maxTicks = 6)
    {
        atRepair = default;
        for (var t = 0; t < maxTicks; t++)
        {
            dbe.WriteTickFence(firstTick + t);
            var telemetry = dbe.GetSpatialTelemetry(ArchetypeId);
            if (telemetry.RepairUnitCount > 0)
            {
                // Snapshotted HERE, not returned for the caller to read afterwards. Every ...Count member of the telemetry
                // describes the most recently completed tick and is reset at the top of the next one, so a caller reading
                // it after the settling fence below would see the zeros of a tick in which nothing was repaired — which is
                // a true reading of the wrong tick, and reads as "the repair never happened".
                atRepair = telemetry;

                // One more fence so the requests the planner emitted have been executed and the AABB pass has recomputed
                // the bounds they changed — the plan is filed in Prep and applied in Migrate, but the SOURCE clusters only
                // shrink on the refresh that follows.
                dbe.WriteTickFence(firstTick + t + 1);
                return firstTick + t + 2;
            }
        }

        return -1;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-12.1 — tightness recovers
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One repair pass over a deliberately degraded cell brings the mean cluster extent down, and the factor against the
    /// theoretical optimum is reported.
    /// </summary>
    /// <remarks>
    /// <para><b>The optimum for C clusters tiling a square cell is <c>cellSize / sqrt(C)</c></b> — C equal squares in a
    /// grid. No packing of C clusters can beat it, so the ratio of the achieved mean to that number is a scale-free score
    /// the design's "within a reported factor of the theoretical optimum" asks for.</para>
    /// <para><b>The assertion is on the improvement, not on the factor.</b> A hard bound on the factor would encode the
    /// spawn pattern's particular geometry, and would fail for a reason that has nothing to do with the re-sort the day
    /// the pattern changes. What must hold is that a re-sort of a scattered cell tightens it substantially; the factor is
    /// reported so a regression in QUALITY, as opposed to in direction, is visible in the failure message.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("RP-04")]
    public void ARepairPassTightensADegradedCell()
    {
        var dbe = SetupEngine();
        SpawnDegradedCell(dbe);

        var (before, clustersBefore) = MeanClusterExtent(dbe);
        Assert.That(clustersBefore, Is.GreaterThan(1), "the degraded cell must hold several clusters or there is nothing to re-partition");
        Assert.That(before, Is.GreaterThan(CellSize * 0.75f),
            $"the spawn pattern was supposed to produce clusters covering most of the cell, but the mean extent is only {before:F1}");

        var nextTick = TickUntilRepaired(dbe, 2, out _);
        Assert.That(nextTick, Is.GreaterThan(0), "no repair unit was ever admitted");

        var (after, clustersAfter) = MeanClusterExtent(dbe);

        // Without this the test passes on a repair that stored NO bounds at all. MeanClusterExtent skips a cluster with
        // no recorded box, so `after` would be 0 over 0 clusters, `optimum` would be 100/sqrt(0) = +Infinity, and both
        // assertions below would read a flawless repair off an empty measurement. The surviving mutation is "stop storing
        // AABBs for repair-allocated destinations", which is precisely the kind of defect this AC exists to catch.
        Assert.That(clustersAfter, Is.GreaterThan(1), "no destination cluster recorded a bound, so every measurement below is over an empty set");
        Assert.That(TotalOccupancy(dbe), Is.EqualTo(Population), "the re-pack lost or duplicated entities, so a tighter mean means nothing");

        var optimum = CellSize / Math.Sqrt(clustersAfter);
        var factor = after / optimum;
        var report = $"mean cluster extent {before:F2} -> {after:F2} over {clustersBefore} -> {clustersAfter} clusters; optimum for "
            + $"{clustersAfter} clusters is {optimum:F2}, so the repair landed at {factor:F2}x optimum";

        Assert.That(after, Is.LessThan(before * 0.5), report);

        // The upper bound on the FACTOR is what stops this from passing on a repair that tightened a little and stopped.
        // 2.0 is deliberately loose: a Morton run is a union of quadtree cells rather than a square, so its bounding box
        // is intrinsically wider than the equal-area optimum and ~1.47 is what the curve gives here. A bound at 1.5 would
        // fail the day the population changes the run alignment, for a reason that is not a regression.
        Assert.That(factor, Is.LessThan(2.0), report);

        TestContext.Out.WriteLine($"AC-12.1: {report}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-12.3 — every invariant survives the re-pack
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A tick on which the planner returns early — a converged cell nominating nothing, which is the steady state — leaves no cluster reserved.
    /// </summary>
    /// <remarks>
    /// <para>The reservation is per-tick, and its clear has to sit above every early return in the planner rather than beside the plans it guards. Below them,
    /// the last plan's reservations outlive it for as long as the planner keeps returning early, and a destination that received nothing is freed by the
    /// same Finalize — so its chunk id is reallocated, possibly into another cell, and that live cluster is excluded from every claim path for good.</para>
    /// <para><b>A guard, not a proof.</b> Moving the clear back below the early returns does NOT redden this test: in this fixture the queue keeps the
    /// planner past its first exit, so the misplaced clear still runs. The defect it documents was found by reading the early returns, and the shape that
    /// would prove it needs a cell whose queue empties while another cell's plan still holds live destinations — worth building when this file next grows
    /// a multi-cell repair scenario.</para>
    /// </remarks>
    [Test]
    public void AConvergedTickLeavesNoClusterReserved()
    {
        var dbe = SetupEngine();
        SpawnDegradedCell(dbe);

        var cs = dbe._archetypeStates[ArchetypeId].ClusterState;
        var tick = 1;
        // Until the QUEUE is empty, not merely until no unit is admitted: the early return this guards is the one at the top of the planner, and a cell
        // that is still queued reaches every line below it. Without that distinction the test passes whether the clear runs early or late.
        for (; tick <= 40 && (cs.RepairQueue == null || cs.RepairQueue.Count > 0 || tick < 3); tick++)
        {
            dbe.WriteTickFence(tick);
        }

        Assert.That(cs.RepairQueue?.Count ?? 0, Is.Zero, "precondition: the queue has drained, so the planner returns at its first early exit");
        dbe.WriteTickFence(tick);
        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).RepairUnitCount, Is.Zero, "precondition: the cell has converged, so nothing is being planned");

        var reserved = new List<int>();
        for (var i = 0; i < cs.ActiveClusterCount; i++)
        {
            var id = cs.ActiveClusterIds[i];
            if (cs.IsRepairDestination(id))
            {
                reserved.Add(id);
            }
        }

        Assert.That(reserved, Is.Empty, "a converged tick left clusters reserved for a repair plan that is not running");
    }

    /// <summary>
    /// A repair's destination clusters are its own: entities crossing into the same cell on the same tick must not take their slots, so every entity the
    /// plan pinned lands in the cluster the Morton sort chose for it (<c>AC-15.4</c>'s pin-rejection budget, decision D5).
    /// </summary>
    /// <remarks>
    /// <para><b>The whole pin-rejection population turned out to be this.</b> A repair pins each moved entity to an exact <c>(cluster, slot)</c> of a
    /// freshly allocated EMPTY cluster — the plan is a permutation, and an empty destination is what makes it collide-free. But an empty cluster attached
    /// to a cell is also the first thing the cursor first-fit scan finds, and the crossing drain runs thousands of requests a tick against a repair's
    /// hundreds. Measured over 40 ticks of the <c>Cruise</c> matrix point before the reservation: 26 434 of 178 145 repair pins rejected (14.6 %), every
    /// one of them "the pinned cluster is full" and never a stale cell — the re-pack's grouping undone for one entity in seven, silently, with every
    /// counter still balancing. After: 0.97 rejections per tick across the whole archetype.</para>
    /// <para>The ablation is <c>IsRepairDestination</c> returning <see langword="false"/>.</para>
    /// </remarks>
    [Test]
    public void ARepairKeepsItsDestinationsFromTheCrossingDrain()
    {
        var dbe = SetupEngine();
        SpawnDegradedCell(dbe);

        // A second cell's population, poised on the boundary. Every one of these crosses into cell (0,0) on the tick the repair runs, so the crossing
        // drain and the repair's pinned claims compete for the same cell's free slots.
        var crossers = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 400; i++)
            {
                crossers.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(CellSize + 4f + ((i * 37) % 92), 4f + ((i * 61) % 92), 90_000 + i))));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var cs = dbe._archetypeStates[ArchetypeId].ClusterState;
        var rejected = 0;
        var repaired = 0;
        for (var t = 0; t < 6; t++)
        {
            // Walk the crossers over the boundary into cell (0,0) every tick, so whichever tick the planner admits a unit on has crossings in flight.
            using (var tx = dbe.CreateQuickTransaction())
            {
                var accessor = tx.For<ClMigUnit>();
                try
                {
                    foreach (var cluster in accessor.GetClusterEnumerator())
                    {
                        var bits = cluster.OccupancyBits;
                        while (bits != 0)
                        {
                            var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                            bits &= bits - 1;
                            ref readonly var pos = ref cluster.GetReadOnly(ClMigUnit.Pos, slot);
                            if (pos.Tag < 90_000)
                            {
                                continue;
                            }

                            // Half in, half out, phases opposite. Even-tag entities enter cell (0,0) on even ticks; odd-tag ones oscillate between
                            // cells (1,0) and (2,0) and never reach it, so three of the six ticks carry crossings into the repaired cell — enough to
                            // overlap a repair, which is what this needs, and measured: the ablation reddens on two of them.
                            var goIn = ((pos.Tag & 1) == 0) == ((t & 1) == 0);
                            var x = goIn ? pos.Bounds.MinX - CellSize : pos.Bounds.MinX + CellSize;
                            if (x > 0f && x < WorldMax)
                            {
                                cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(x, pos.Bounds.MinY, pos.Tag));
                            }
                        }
                    }
                }
                finally
                {
                    accessor.Dispose();
                }

                tx.Commit();
            }

            dbe.WriteTickFence(2 + t);
            var telemetry = dbe.GetSpatialTelemetry(ArchetypeId);
            repaired += telemetry.RepairUnitCount;
            rejected += cs.LastTickPinsRejected;
            // Ablated (IsRepairDestination => false) this reads 1 858 and 2 005 on alternating ticks — the crossing drain filling the plan's output.
            // The counter is broader than this test's subject: TryClaimPinnedSlot also bumps it for a step-10 relocation whose pinned cluster changed
            // cell, which CR-02 calls a legal fallback. Stable at zero here across runs; if it ever flakes, split the counter by request kind rather
            // than loosening the assertion.
        }

        Assert.Multiple(() =>
        {
            Assert.That(repaired, Is.GreaterThan(0), "no repair unit was admitted, so nothing here was exercised");
            Assert.That(rejected, Is.Zero, "a claimant took a slot in a cluster this tick's repair plan had allocated for its own output");
            Assert.That(TotalOccupancy(dbe), Is.EqualTo(Population + crossers.Count), "an entity was lost or duplicated");
        });
    }

    /// <summary>
    /// After a repair: the same entities exist at the same positions, every cluster's stored AABB contains its entities
    /// (<c>CA-01</c>), every cluster of the cell maps back to it (cluster→cell exclusivity, <c>C13</c>), the cell's entity
    /// count matches the population, and every entity resolves through the <c>EntityMap</c> to the slot it now occupies.
    /// </summary>
    /// <remarks>
    /// <b>Set equality, not cardinality.</b> An earlier step-10 assertion of this shape compared counts and would have
    /// passed for a re-pack that duplicated one entity and dropped another. The identity carried here is the spawn tag,
    /// which is written once at spawn and never touched again, so it survives an arbitrary permutation of slots and is
    /// what makes "the same entities" checkable at all.
    /// </remarks>
    [Test]
    [VerifiesRule("RP-03")]
    public void ARepairPreservesEveryEntityAndEveryInvariant()
    {
        var dbe = SetupEngine();
        var ids = SpawnDegradedCell(dbe);

        var before = ReadAll(dbe);
        Assert.That(before, Has.Count.EqualTo(Population));
        var beforeSet = new Dictionary<long, (float x, float y)>();
        foreach (var (tag, _, _, x, y) in before)
        {
            beforeSet[tag] = (x, y);
        }

        Assert.That(TickUntilRepaired(dbe, 2, out _), Is.GreaterThan(0), "no repair unit was ever admitted");

        var after = ReadAll(dbe);
        Assert.That(after, Has.Count.EqualTo(Population), "the re-pack changed the population");

        var state = ClusterStateOf(dbe);
        var seen = new HashSet<long>();
        foreach (var (tag, chunk, _, x, y) in after)
        {
            Assert.That(seen.Add(tag), Is.True, $"entity tag {tag} appears twice after the repair");
            Assert.That(beforeSet.ContainsKey(tag), Is.True, $"entity tag {tag} did not exist before the repair");
            var (bx, by) = beforeSet[tag];
            Assert.That(x, Is.EqualTo(bx).Within(0.001f), $"entity {tag} moved in space, not just in storage");
            Assert.That(y, Is.EqualTo(by).Within(0.001f), $"entity {tag} moved in space, not just in storage");

            // C13 — every cluster holding an entity of this cell maps back to it.
            Assert.That(state.ClusterCellMap[chunk], Is.EqualTo(0), $"cluster {chunk} holds a cell-0 entity but maps to cell {state.ClusterCellMap[chunk]}");

            // CA-01 — the stored, cell-relative bound contains the entity. Cell 0's origin is (0,0), so world and
            // cell-relative coincide here; asserting against a cell whose origin is non-zero would be testing the rebase
            // rather than containment, which ClusterMigrationTests already covers.
            ref var box = ref state.ClusterAabbs[chunk];
            Assert.That(x, Is.GreaterThanOrEqualTo(box.MinX).And.LessThanOrEqualTo(box.MaxX), $"CA-01: entity {tag} at x={x} outside cluster {chunk}");
            Assert.That(y, Is.GreaterThanOrEqualTo(box.MinY).And.LessThanOrEqualTo(box.MaxY), $"CA-01: entity {tag} at y={y} outside cluster {chunk}");
        }

        Assert.That(seen, Has.Count.EqualTo(Population));

        // The EntityMap half, asserted by RESOLUTION rather than by existence. Every id must still find its own entity,
        // which is only true if the bulk location patch landed for all of them.
        var resolved = ResolveAllThroughMap(dbe, ids);
        for (var i = 0; i < resolved.Count; i++)
        {
            var (x, y, tag) = resolved[i];
            Assert.That(tag, Is.EqualTo(i), $"EntityMap resolved entity {i} to a slot holding tag {tag}");
            var (bx, by) = beforeSet[i];
            Assert.That(x, Is.EqualTo(bx).Within(0.001f), $"EntityMap resolved entity {i} to the wrong position");
            Assert.That(y, Is.EqualTo(by).Within(0.001f), $"EntityMap resolved entity {i} to the wrong position");
        }

        Assert.That(dbe.SpatialGrid.GetCell(0).EntityCount, Is.EqualTo(Population),
            "CellState.EntityCount drifted from the population the re-pack moved");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-12.5 — a unit the budget cannot finish is never begun
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// With the budget set just below the projected cost of the only unit on offer, nothing is moved — and the refusal is
    /// counted rather than silently dropped.
    /// </summary>
    /// <remarks>
    /// <para><b>Just below, not zero.</b> A zero budget short-circuits before the planner ranks anything, so it would pass
    /// whether or not the whole-unit rule exists. The interesting case is a budget that is real but insufficient, which is
    /// the one §5.6 legislates: a Morton sort cannot be halved, so the unit is not begun at all rather than begun and cut
    /// short.</para>
    /// <para>The cost model is <c>population x RepairNsPerEntity</c>, which the test reproduces rather than reads, so a
    /// change to the projection that this assertion no longer bounds shows up as a failure here rather than as a quietly
    /// weaker test.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("RP-01")]
    public void ARepairIsNeverBegunWithoutTheBudgetToFinishIt()
    {
        // Mirrors SpatialGridConfig.RepairNsPerEntity's default. Reproduced rather than read, so a change to the projection
        // this assertion no longer bounds shows up as a failure here instead of as a quietly weaker test.
        const float nsPerEntity = 1500f;
        var justUnderMs = (float)(Population * nsPerEntity / 1_000_000d * 0.99d);

        var dbe = SetupEngine(budgetMs: justUnderMs);
        SpawnDegradedCell(dbe);

        var before = ReadAll(dbe);

        // Checked on EVERY tick, not once at the end. The ...Count members describe the most recently completed tick and
        // are reset at the top of the next one, so a single read after the loop asserts about tick 8 alone — and a unit
        // begun on tick 3 would leave it entirely unmarked. `refusals` accumulates across the loop for the same reason.
        var refusals = 0;
        for (var tick = 2; tick <= 8; tick++)
        {
            dbe.WriteTickFence(tick);
            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            Assert.That(t.RepairUnitCount, Is.Zero, $"tick {tick} began a unit the budget could not finish");
            Assert.That(t.RepairedEntityCount, Is.Zero, $"tick {tick} re-packed entities under an insufficient budget");
            Assert.That(t.ReclusterBudgetUsedMs, Is.Zero, $"tick {tick} reported budget spent on a unit that was refused");
            refusals += t.RepairUnitsRefused;
        }

        Assert.That(refusals, Is.GreaterThan(0),
            "the unit was neither begun nor counted as refused, so the budget check never ran and this test proves nothing");

        // A refusal must leave the layout alone, not half-sort it. Same slots, same entities.
        var after = ReadAll(dbe);
        Assert.That(after, Is.EquivalentTo(before), "a refused repair still moved entities");
    }

    /// <summary>
    /// The same cell, one notch of budget higher, IS repaired — so the refusal above is the budget rule firing, not a
    /// setup in which nothing was ever nominated.
    /// </summary>
    /// <remarks>
    /// Without this twin, <c>ARepairIsNeverBegunWithoutTheBudgetToFinishIt</c> would pass just as well against a
    /// nomination gate that never fires, a planner that always returns zero, or a spawn pattern that produces one cluster.
    /// It is the control, and it is deliberately identical apart from the one number under test.
    /// </remarks>
    [Test]
    public void TheSameCellIsRepairedOnceTheBudgetCoversTheUnit()
    {
        // Mirrors SpatialGridConfig.RepairNsPerEntity's default. Reproduced rather than read, so a change to the projection
        // this assertion no longer bounds shows up as a failure here instead of as a quietly weaker test.
        const float nsPerEntity = 1500f;
        var justOverMs = (float)(Population * nsPerEntity / 1_000_000d * 1.5d);

        var dbe = SetupEngine(budgetMs: justOverMs);
        SpawnDegradedCell(dbe);

        Assert.That(TickUntilRepaired(dbe, 2, out var telemetry), Is.GreaterThan(0), "the budget covered the unit and it still was not begun");

        Assert.That(telemetry.RepairedEntityCount, Is.GreaterThan(0));
        Assert.That(telemetry.ReclusterBudgetUsedMs, Is.GreaterThan(0d));
        Assert.That(telemetry.RepairUnitsRefused, Is.Zero, "a unit was refused on the tick a repair was admitted");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-12.2 — zone maps shrink
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A repair NARROWS the zone maps of the cell it re-packs — the only narrowing that happens anywhere in the engine.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is not automatic.</b> <c>ZoneMapArray.Widen</c> is the only writer on the hot path and its name
    /// is the whole contract: it never narrows. A cluster that gains and loses entities accumulates the union of every
    /// value it has ever held, and a chunk id recycled from a freed cluster starts from the previous tenant's bounds
    /// because nothing invalidated them — <c>ZoneMapArray.Invalidate</c> existed with no caller at all before step 12.
    /// So the measurement here is of the repair path calling it, and of <c>Widen</c> then rebuilding from the re-packed
    /// contents.</para>
    /// <para><b>The indexed field has to be position-correlated or there is nothing to measure.</b> A zone map records
    /// min/max of an INDEXED field, and grouping entities by position tightens that only when the field varies with
    /// position. So this fixture tags each entity with its own quantised X. That is not a rigged case: it is the shape
    /// the claim is actually about — index the coordinate, or a quantity that tracks it, and locality-grouping buys
    /// pruning. For an index on something uncorrelated with space, a re-sort neither helps nor harms, which is worth
    /// stating rather than measuring.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("RP-05")]
    public void ARepairNarrowsTheZoneMapsOfTheCellItRepacks()
    {
        var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Population; i++)
            {
                var x = 4f + ((i * 37) % 92) + ((i / 92) % 4) * 0.2f;
                var y = 4f + ((i * 61) % 92) + ((i / 92) % 4) * 0.2f;
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(x, y, (int)x)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var before = TotalZoneMapWidth(dbe, out var clustersBefore);
        Assert.That(clustersBefore, Is.GreaterThan(1), "the cell must hold several clusters or a zone map cannot be narrowed by re-partitioning");
        Assert.That(before, Is.GreaterThan(0L), "no zone map was recorded, so this test would pass with the repair path deleted");

        Assert.That(TickUntilRepaired(dbe, 2, out _), Is.GreaterThan(0), "no repair unit was ever admitted");

        var after = TotalZoneMapWidth(dbe, out var clustersAfter);
        Assert.That(after, Is.GreaterThan(0L), "every zone map is now unrecorded — the repair invalidated them and nothing widened them back");

        // The count, not just the total. A sum shrinks just as convincingly when maps go MISSING as when they narrow,
        // and the mutation that exploits it is one line: widen InvalidateClusterZoneMaps to every cluster of the cell
        // rather than only the destinations. Most maps would then read "unrecorded", `after` would collapse, and the test
        // would get greener the more it broke.
        Assert.That(clustersAfter, Is.GreaterThanOrEqualTo(clustersBefore),
            $"{clustersBefore - clustersAfter} clusters lost their zone map entirely — that is an invalidation with no widen behind it, not a narrowing");
        Assert.That(after, Is.LessThan(before), $"total zone-map width {before} -> {after} over {clustersBefore} -> {clustersAfter} clusters");

        TestContext.Out.WriteLine($"AC-12.2: total zone-map width {before} -> {after} ({(double)after / before:P0} of before), "
            + $"clusters {clustersBefore} -> {clustersAfter}");
    }

    /// <summary>
    /// Sum of <c>max - min</c> over every recorded zone map of every cluster in cell 0, across every indexed field.
    /// </summary>
    /// <remarks>
    /// A cluster with no recorded bounds contributes nothing and is not counted, which is the conservative direction: an
    /// invalidation that was never followed by a <c>Widen</c> would drive the total DOWN and could look like a narrowing.
    /// The caller therefore also asserts the cluster count and that the total is non-zero, so "narrower" cannot be
    /// satisfied by "absent".
    /// </remarks>
    private static long TotalZoneMapWidth(DatabaseEngine dbe, out int clustersCounted)
    {
        var state = ClusterStateOf(dbe);
        var clusters = state.CellClusterPool.GetClusters(0);
        var total = 0L;
        clustersCounted = 0;

        for (var i = 0; i < clusters.Length; i++)
        {
            var chunkId = clusters[i];
            var contributed = false;
            var ixSlots = state.IndexSlots;
            for (var si = 0; si < (ixSlots?.Length ?? 0); si++)
            {
                ref var ixSlot = ref ixSlots[si];
                for (var fi = 0; fi < ixSlot.Fields.Length; fi++)
                {
                    var zoneMap = ixSlot.Fields[fi].ZoneMap;
                    if (zoneMap != null && zoneMap.TryGetBounds(chunkId, out var min, out var max))
                    {
                        total += max - min;
                        contributed = true;
                    }
                }
            }

            if (contributed)
            {
                clustersCounted++;
            }
        }

        return total;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-12.7 — cost per entity
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Measure and report the tick fence's wall-clock cost per entity a repair moved.
    /// </summary>
    /// <remarks>
    /// <para><b>This is a fence-per-repaired-entity figure, not the cost of the re-sort.</b> The timer wraps the whole
    /// <c>WriteTickFence</c> and the divisor is <c>RepairedEntityCount</c>, so the quotient also carries the AABB
    /// refresh, the WAL emit and — because this fixture owns the ChangeSet — <c>SaveChanges</c> page writeback. It is
    /// the right shape for sizing <c>ReclusterBudgetMs</c>, which is charged against the whole fence, and the wrong
    /// shape for costing the re-sort, which the phase breakdown puts at nearer half. The label mattered: read as a
    /// repair figure it was published as one, and the published range then also carried the outlier below.</para>
    /// <para><b>Read the median of the warm rounds, not the spread.</b> Ten of thirteen warm rounds land at
    /// 1 058-1 619 ns. Three land at 5 200-10 600 with zero GCs, flat total pause, identical allocation, and the stall
    /// in a DIFFERENT phase each time — an external stall of a single-threaded fence on a shared box, not a property of
    /// the pipeline. Quoting the worst of those as the top of a range overstates it by about 5x.</para>
    /// </remarks>
    /// <remarks>
    /// <c>[Explicit]</c> because it is a measurement, not a check: a wall-clock threshold in the suite is a flake on a
    /// loaded machine and says nothing on an unloaded one. Run it deliberately, read the number, and compare it against
    /// <c>SpatialGridConfig.RepairNsPerEntity</c> — the constant the admission decision projects with, which is only
    /// honest for as long as this measurement agrees with it. In a running engine the same quantity is measured
    /// continuously and published as <c>MeasuredNsPerEntity</c>, which is what an operator should read instead of this.
    /// </remarks>
    [Test]
    [Explicit("measurement, not an assertion — run deliberately and read the reported ns/entity")]
    [Category("Manual")]
    public void MeasureRepairCostPerEntity()
    {
        var dbe = SetupEngine();
        SpawnDegradedCell(dbe);

        // ── Repeated, and the repeats are the measurement ──────────────────────────────────────────────────────────
        //
        // The FIRST repair a process performs is tick two of its life: the whole fence pipeline is still being JIT-ed,
        // the cluster segment is growing into pages nobody has touched, and the WAL is writing its first real batch.
        // Measured that way the answer was 22 000-29 700 ns/entity — an order of magnitude out, and it says almost nothing about
        // the re-sort. Scrambling the population and repairing again, on the same engine, is what isolates the steady
        // cost, and the spread across repeats is what says whether the first number was warm-up or real.
        var samples = new List<(int moved, double ms, int migrations, double loopMs, double totalMs)>();
        var tick = 2;
        for (var round = 0; round < Rounds; round++)
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                dbe.WriteTickFence(tick++);
                sw.Stop();

                var t = dbe.GetSpatialTelemetry(ArchetypeId);
                if (t.RepairedEntityCount > 0)
                {
                    samples.Add((t.RepairedEntityCount, sw.Elapsed.TotalMilliseconds, t.MigrationCount, t.MigrationExecuteMs, t.MigrationTotalMs));
                    break;
                }
            }

            Scramble(dbe, round);
        }

        Assert.That(samples, Is.Not.Empty, "no repair was ever measured");

        var report = new System.Text.StringBuilder();
        report.AppendLine($"AC-12.7 — {Population} entities, {samples.Count} repairs measured. FENCE ns per repaired entity, not re-sort cost:");
        // #918 D6: the split the bulk repair loop has to be sized against. It can only ever shorten the LOOP — the per-entity copy/claim/fold/release
        // pass — and never the index and EntityMap applies, which run in their own phases, nor the rest of the fence (Prep's Morton sort and plan,
        // AabbRefresh, WAL). `migrations` is every kind that ran this tick, not repairs alone, so the loop and apply columns are per MIGRATED entity.
        // The rest column subtracts MigrationTotalMs — summed CPU across workers — from the fence's WALL time. That is only sound because this fixture
        // drives the serial fence, one thread, where the two coincide; on the parallel fence the column would be meaningless.
        report.AppendLine("   repaired   fence ms   fence ns/repaired   migrations   loop ns/mig   applies ns/mig   rest-of-fence ns/repaired");
        var best = double.MaxValue;
        foreach (var (moved, ms, migrations, loopMs, totalMs) in samples)
        {
            var ns = ms * 1_000_000d / moved;
            best = Math.Min(best, ns);
            var loopNs = migrations > 0 ? loopMs * 1_000_000d / migrations : 0d;
            var appliesNs = migrations > 0 ? (totalMs - loopMs) * 1_000_000d / migrations : 0d;
            var restNs = (ms - totalMs) * 1_000_000d / moved;
            report.AppendLine($"   {moved,8} {ms,10:F3} {ns,19:F1} {migrations,12} {loopNs,13:F1} {appliesNs,16:F1} {restNs,27:F1}");
        }

        report.AppendLine($"  best {best:F1} ns/entity => {best * 100_000 / 1_000_000d:F2} ms projected for a 100 K-entity cell");
        report.AppendLine("  read the MEDIAN of the warm rounds; sporadic 5-10x rounds are box stalls, not the pipeline (see the remarks)");
        Assert.Pass(report.ToString());
    }

    /// <summary>Repeats of the measurement. The first is warm-up by construction; the rest are the number.</summary>
    private const int Rounds = 6;

    /// <summary>
    /// Move every entity to a new pseudo-random position in the cell, so the next tick has a genuinely degraded layout
    /// to repair rather than the one it just produced.
    /// </summary>
    /// <remarks>
    /// A pure function of (chunkId, slot, round) rather than a sequential RNG, so the world does not depend on the order
    /// the cluster enumerator happens to visit — the same reason <c>ClusterDriftParallelTests.SpreadPosition</c> hashes
    /// its coordinates.
    /// </remarks>
    private static unsafe void Scramble(DatabaseEngine dbe, int round)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
#pragma warning disable TYPHON009 // Read-only: the tag is carried across, the position is written through WriteSpatial below.
                var positions = cluster.GetSpan(ClMigUnit.Pos);
#pragma warning restore TYPHON009
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;

                    var h = (uint)((cluster.ChunkId * 0x9E3779B1) ^ (slot * 0x85EBCA6B) ^ (round * 0x27D4EB2F));
                    h ^= h >> 15;
                    h *= 0x2C1B3C6D;
                    h ^= h >> 12;
                    var g = h * 0x27D4EB2F;
                    g ^= g >> 15;

                    cluster.WriteSpatial(ClMigUnit.Pos, slot,
                        PointAt(4f + (h % 10_000) * (92f / 10_000f), 4f + (g % 10_000) * (92f / 10_000f), positions[slot].Tag));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }
}
