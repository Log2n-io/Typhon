using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-10.3</c>, <c>AC-10.4</c>, <c>AC-10.5</c> and <c>AC-10.9</c> — intra-cell relocation PLACEMENT and the batch that
/// applies it (#872 step 10).
/// </summary>
/// <remarks>
/// Detection decides <i>who</i> moves; this decides <i>where to</i>, and then that the move actually happened correctly.
/// The two halves fail differently: a placement bug produces a legal database with bad bounds — nothing throws, queries
/// stay correct, and the only symptom is that the ~24× selectivity win never arrives — while a batch bug corrupts the
/// index or the EntityMap and is loud. So placement is asserted on the CHOICE and the batch on the INVARIANTS.
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterRelocationTests : TestBase<ClusterRelocationTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        // #872 step 12 turned the repair path on by default, and these fixtures are about the DELTA path. Repair legitimately
        // preempts relocation — a cell it re-packs comes out tight, so the drift gate stops firing and the deliberately
        // distinct clusters this fixture builds are collapsed into a Morton packing before a single placement decision is
        // made. Three placement tests and one shrink test went red that way, all of them correctly. Pinning the budget to
        // zero scopes each fixture to the mechanism it is written to measure; it is not a workaround for a defect.
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize,
            reclusterBudgetMs: 0f, batchSpawnSortThreshold: 0 /* step 15: this fixture builds its layout by spawn ORDER; the Morton sort would tighten it at birth */,
            // Constant-mode target (step 14): the assertions here are written against the configured ratio; at 128-600 entities per cell the
            // density-derived target would sit between 0.49 and off, and the drifter sets they pin would change.
            clusterTargetPackingSlack: 0f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClMigUnit>.Metadata.ArchetypeId].ClusterState;

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    /// <summary>Every live (clusterChunkId, slot) → world centre, read straight from cluster storage.</summary>
    private static unsafe List<(int chunk, int slot, float x, float y)> ReadAll(DatabaseEngine dbe)
    {
        var result = new List<(int, int, float, float)>();
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
#pragma warning disable TYPHON009 // Read-only.
                var positions = cluster.GetSpan(ClMigUnit.Pos);
#pragma warning restore TYPHON009
                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    ref readonly var b = ref positions[slot].Bounds;
                    result.Add((cluster.ChunkId, slot, 0.5f * (b.MinX + b.MaxX), 0.5f * (b.MinY + b.MaxY)));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
        return result;
    }

    private static unsafe void MoveAll(DatabaseEngine dbe, Func<int, int, (float x, float y)> position)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var (x, y) = position(cluster.ChunkId, slot);
                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(x, y));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
        tx.Commit();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Placement — AC-10.3 / AC-10.4
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a cell holding three clusters with distinctly different boxes, each with at least one free slot.
    /// </summary>
    /// <remarks>
    /// <para><b>Every constraint here is load-bearing.</b> <c>ChooseRelocationTarget</c> skips FULL clusters and skips the
    /// SOURCE, so a cell whose clusters are all full, or that holds only one cluster, offers nothing to choose between and
    /// any assertion about the choice is vacuous. The first version of this test spawned a single entity and then admitted
    /// in a comment that "the interesting assertion needs two" — it asserted only that a returned id was a live cluster of
    /// the cell, which the least-enlargement rule could have been deleted entirely without breaking.</para>
    /// <para><b>How the three boxes arise.</b> Cluster capacity is 49 slots and <c>ClaimSlotInCell</c> fills first-fit, so
    /// 64 entities at the low corner followed by 64 at the high corner produce a low cluster, a STRADDLING cluster that
    /// caught the tail of one group and the head of the next, and a high cluster. Three genuinely different shapes for the
    /// price of one spawn pattern — and the straddler is the interesting one, because it is exactly what first-fit
    /// placement produces and exactly what this step exists to stop producing.</para>
    /// <para>Destroying one entity from each of the two full clusters is what makes them candidates at all. One is the
    /// right number: enough to free a slot, few enough to leave the boxes where they were.</para>
    /// </remarks>
    private static void SpawnThreeDistinctClusters(DatabaseEngine dbe)
    {
        var low = new List<EntityId>();
        var high = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 64; i++)
            {
                low.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(10f + (i % 5), 10f + (i / 5) * 0.5f, i))));
            }
            for (int i = 0; i < 64; i++)
            {
                high.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(90f + (i % 5), 90f + (i / 5) * 0.5f, 1000 + i))));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(low[0]);
            tx.Destroy(high[0]);
            tx.Commit();
        }
        dbe.WriteTickFence(2);
        dbe.WriteTickFence(3);
    }

    /// <summary>The clusters of <paramref name="cellKey"/> that have a free slot, excluding <paramref name="source"/>.</summary>
    private static unsafe List<int> NonFullCandidates(ArchetypeClusterState cs, int cellKey, int source, ref ChunkAccessor<PersistentStore> accessor)
    {
        var result = new List<int>();
        for (int i = 0; i < cs.ActiveClusterCount; i++)
        {
            int c = cs.ActiveClusterIds[i];
            if (c == source || cs.ClusterCellMap[c] != cellKey)
            {
                continue;
            }

            ulong occupancy = *(ulong*)accessor.GetChunkAddress(c);
            if ((~occupancy & cs.Layout.FullMask) != 0)
            {
                result.Add(c);
            }
        }

        return result;
    }

    /// <summary>
    /// Production's candidate set for one (cell, source) pair — the first half of the two-step placement API.
    /// </summary>
    /// <remarks>
    /// Candidate boxes are SNAPSHOTTED when this is built, so any test that hand-writes <c>ClusterAabbs</c> must do so
    /// BEFORE calling this, not between this and the choice. That ordering is a property of the design rather than an
    /// inconvenience: the snapshot is what stops a candidate's fields being re-read and mixed while sibling fence slices
    /// blind-store them.
    /// </remarks>
    private static List<ArchetypeClusterState.RelocationCandidate> Candidates(ArchetypeClusterState cs, int cellKey, int source,
        ref ChunkAccessor<PersistentStore> accessor)
    {
        var list = new List<ArchetypeClusterState.RelocationCandidate>();
        cs.BuildRelocationCandidates(cellKey, source, ref accessor, list);
        return list;
    }

    /// <summary>
    /// The least-enlargement answer, computed independently of the production predicate: area growth, an empty box counting
    /// as zero, ties to the lowest chunk id.
    /// </summary>
    private static int PlacementOracle(ArchetypeClusterState cs, List<int> candidates, float px, float py)
    {
        int best = -1;
        float bestGrowth = float.PositiveInfinity;
        foreach (int c in candidates)
        {
            ref readonly var b = ref cs.ClusterAabbs[c];
            float growth;
            if (float.IsPositiveInfinity(b.MinX))
            {
                growth = 0f;   // AC-10.4 — an empty cluster admits anything for free.
            }
            else
            {
                float area = (b.MaxX - b.MinX) * (b.MaxY - b.MinY);
                float grown = (MathF.Max(b.MaxX, px) - MathF.Min(b.MinX, px)) * (MathF.Max(b.MaxY, py) - MathF.Min(b.MinY, py));
                growth = grown - area;
            }

            if (growth < bestGrowth || (growth == bestGrowth && c < best))
            {
                bestGrowth = growth;
                best = c;
            }
        }

        return best;
    }

    /// <summary>
    /// <c>AC-10.3</c> — of several candidate clusters the one whose AABB grows least is chosen, and the answer flips when
    /// the point moves to the other side of the cell.
    /// </summary>
    /// <remarks>
    /// Asserted against an independent computation AND both ways round. A placement function that always returned the
    /// lowest chunk id, or always the last candidate, satisfies a single-direction test; requiring the two queries to
    /// return DIFFERENT clusters is what shows the decision tracks the geometry rather than the iteration order.
    /// </remarks>
    [Test]
    [VerifiesRule("CR-02")]
    [CancelAfter(30_000)]
    public unsafe void Placement_ChoosesTheLeastEnlargementCandidate()
    {
        using var dbe = SetupEngine();
        var cs = ClusterStateOf(dbe);
        SpawnThreeDistinctClusters(dbe);

        int cellKey = cs.ClusterCellMap[cs.ActiveClusterIds[0]];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            var candidates = NonFullCandidates(cs, cellKey, source: -1, accessor: ref accessor);
            Assert.That(candidates, Has.Count.GreaterThanOrEqualTo(2),
                "the cell must offer at least two non-full clusters, or there is nothing for least-enlargement to choose between");

            var built = Candidates(cs, cellKey, source: -1, accessor: ref accessor);
            int nearLow = cs.ChooseRelocationTarget(built, px: 5f, py: 5f, pz: 0f, flat: true);
            int nearHigh = cs.ChooseRelocationTarget(built, px: 95f, py: 96f, pz: 0f, flat: true);

            Assert.Multiple(() =>
            {
                Assert.That(nearLow, Is.EqualTo(PlacementOracle(cs, candidates, 5f, 5f)), "a point at the low corner did not go to the cheapest box");
                Assert.That(nearHigh, Is.EqualTo(PlacementOracle(cs, candidates, 95f, 96f)), "a point at the high corner did not go to the cheapest box");
                Assert.That(nearLow, Is.Not.EqualTo(nearHigh),
                    "both corners chose the same cluster, so this would also pass for a function that ignores the point entirely");
            });
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary><c>AC-10.4</c> — an empty cluster counts as enlargement <b>0</b>, which is not the same as counting as -∞.</summary>
    /// <remarks>
    /// <para><b>The first version of this test could not fail, and the ablation is what showed it.</b> It emptied the worst
    /// candidate and asserted the empty one then won. Deleting the special case left it green — because
    /// <c>GrowthToAdmit</c> on the <c>+∞</c>-min / <c>-∞</c>-max sentinel computes an area of <c>+∞</c> and a grown area of
    /// <c>0</c>, so growth is <b>-∞</b>, and -∞ beats every finite growth just as 0 does. The assertion was true under both
    /// the rule and its absence, which makes it worth nothing.</para>
    /// <para><b>Where the two answers actually differ is a TIE.</b> Against a cluster whose box IS the point — genuine growth
    /// <c>0</c> and, since step 15 ranks equal growth by the smaller resulting box, resulting size <c>0</c> as well — the spec's
    /// <c>0</c> ties on both terms and resolves to the lower chunk id, while <c>-∞</c> wins outright. So the construction puts
    /// the containing cluster at the LOWER id and the emptied one at a higher id: the spec says the containing cluster, the
    /// unfixed arithmetic says the empty one. That is also the behaviour you want on the merits — dropping an entity into a
    /// box that already covers it beats opening a fresh cluster for it. (A wider containing box would tie on growth but lose
    /// on size, which is the size term doing its job, not the defect this test is after.)</para>
    /// <para><b>As of the candidate-hoist refactor this test no longer reddens when the special case is deleted, and that
    /// is a property of the code rather than a weakness here.</b> <c>ChooseRelocationTarget</c> now clamps negative growth to
    /// zero — a defence against a box snapshotted mid-store, whose min can exceed its max — and that clamp catches the
    /// <c>-∞</c> the sentinel produces just as the explicit case catches it. Two mechanisms, identical answer, so no input
    /// can distinguish them. The explicit case is kept because it states the INTENT that <c>AC-10.4</c> specifies, and the
    /// clamp is a safety net rather than a rule; if the clamp is ever removed, this assertion becomes discriminating again.
    /// Recorded because a green ablation that is not understood is indistinguishable from a vacuous test.</para>
    /// <para><b>The sentinel is written directly, and that is the honest way to reach this state.</b> A cluster whose last
    /// entity left has exactly this bound; arriving there by draining one would take a longer setup to produce the same two
    /// words and would leave the assertion depending on the drain path rather than on the branch under test.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("CR-02")]
    [CancelAfter(30_000)]
    public unsafe void Placement_TreatsAnEmptyClusterAsZeroEnlargement()
    {
        using var dbe = SetupEngine();
        var cs = ClusterStateOf(dbe);
        SpawnThreeDistinctClusters(dbe);

        int cellKey = cs.ClusterCellMap[cs.ActiveClusterIds[0]];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            var candidates = NonFullCandidates(cs, cellKey, source: -1, accessor: ref accessor);
            Assert.That(candidates, Has.Count.GreaterThanOrEqualTo(2), "the construction needs one cluster to empty and one to compete with it");

            candidates.Sort();
            int container = candidates[0];                       // lower id — its box will already contain the point
            int emptied = candidates[candidates.Count - 1];      // higher id — this one gets the sentinel

            // A box that is exactly the query point: growth 0 AND resulting size 0 (step 15 ranks equal growth by the smaller resulting box), so it
            // TIES with a correctly-scored empty cluster on both terms and wins the tiebreak on id, while it loses outright to an empty cluster
            // scored as -∞. A wider containing box would tie on growth but lose on size, which is the new rule doing its job, not the defect.
            cs.ClusterAabbs[container] = new ClusterSpatialAabb
            {
                MinX = 50f, MaxX = 50f, MinY = 50f, MaxY = 50f, MinZ = float.PositiveInfinity, MaxZ = float.NegativeInfinity,
            };
            cs.ClusterAabbs[emptied] = ClusterSpatialAabb.Empty;

            // Every other candidate is given the whole cell as its box: growth 0 for (50,50) too, but a resulting size of 99 × 99 against the
            // container's 0, so the size term excludes it before the id ever has to. The test still discriminates — an empty cluster scored at -∞
            // would beat all of them.
            foreach (int c in candidates)
            {
                if (c != container && c != emptied)
                {
                    cs.ClusterAabbs[c] = new ClusterSpatialAabb
                    {
                        MinX = 0f, MaxX = 99f, MinY = 0f, MaxY = 99f, MinZ = float.PositiveInfinity, MaxZ = float.NegativeInfinity,
                    };
                }
            }

            // Built AFTER the boxes above are written — candidate boxes are snapshotted at build time.
            int chosen = cs.ChooseRelocationTarget(Candidates(cs, cellKey, -1, ref accessor), px: 50f, py: 50f, pz: 0f, flat: true);
            Assert.That(chosen, Is.EqualTo(container),
                "a cluster whose box already contains the point grows by 0 and must tie with the empty cluster, then win on the lower id — "
                + "scoring the empty cluster from the ±∞ sentinel gives it -∞ and lets it win outright");
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary><c>AC-10.3</c> — candidates that grow by the same amount resolve to the lower chunk id, not to scan order.</summary>
    /// <remarks>
    /// <para>Cluster ids come from an allocator whose order depends on worker interleaving, so "whichever I saw first" would
    /// make placement — and therefore the resulting bounds, and therefore query cost — a function of scheduling. The
    /// candidates are made IDENTICAL rather than merely similar, because a tiebreak only has to be stable and equality is
    /// the only input that tests stability rather than arithmetic.</para>
    /// <para><b>Deleting the tiebreak clause does NOT redden this test, and that is recorded rather than papered over.</b>
    /// <c>CellClusterPool.GetClusters</c> returns a cell's clusters in ascending chunk-id order today, so "first candidate
    /// with strictly smaller growth" already resolves to the lowest id and the explicit tiebreak is redundant with the
    /// enumeration order. Nothing documents that order as a guarantee, and the pool is a structure step 11 may well re-pack,
    /// so the clause is worth keeping and this test is worth keeping — but its value is catching the day the order changes,
    /// not proving the clause does anything now. Claiming otherwise would misrepresent the coverage.</para>
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public unsafe void Placement_BreaksTiesTowardTheLowerChunkId()
    {
        using var dbe = SetupEngine();
        var cs = ClusterStateOf(dbe);
        SpawnThreeDistinctClusters(dbe);

        int cellKey = cs.ClusterCellMap[cs.ActiveClusterIds[0]];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            var candidates = NonFullCandidates(cs, cellKey, source: -1, accessor: ref accessor);
            Assert.That(candidates, Has.Count.GreaterThanOrEqualTo(2), "a tiebreak needs two candidates to tie");

            var box = new ClusterSpatialAabb
            {
                MinX = 20f, MaxX = 30f, MinY = 20f, MaxY = 30f, MinZ = float.PositiveInfinity, MaxZ = float.NegativeInfinity,
            };
            int lowest = int.MaxValue;
            foreach (int c in candidates)
            {
                cs.ClusterAabbs[c] = box;
                lowest = Math.Min(lowest, c);
            }

            Assert.That(cs.ChooseRelocationTarget(Candidates(cs, cellKey, -1, ref accessor), px: 60f, py: 60f, pz: 0f, flat: true), Is.EqualTo(lowest),
                "identical candidates must resolve to the lowest chunk id, or placement depends on cluster-allocation order");
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary><c>AC-10.3</c> — the source cluster is never chosen as its own destination.</summary>
    /// <remarks>
    /// The failure this guards is silent and total: <c>ClaimSlotInCell</c>'s first-fit scan will happily return the source
    /// cluster, so a placement function that did not exclude it would produce relocations that move an entity to a
    /// different slot in the same cluster — a full migration's cost for exactly no change in any bound.
    /// </remarks>
    [Test]
    [VerifiesRule("CR-02")]
    [CancelAfter(30_000)]
    public unsafe void Placement_NeverChoosesTheSourceCluster()
    {
        using var dbe = SetupEngine();
        var cs = ClusterStateOf(dbe);

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 200; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(10f + (i % 10), 10f + (i / 10), i)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        int cellKey = cs.ClusterCellMap[cs.ActiveClusterIds[0]];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < cs.ActiveClusterCount; i++)
            {
                int source = cs.ActiveClusterIds[i];
                if (cs.ClusterCellMap[source] != cellKey)
                {
                    continue;
                }

                int chosen = cs.ChooseRelocationTarget(Candidates(cs, cellKey, source, ref accessor), px: 50f, py: 50f, pz: 0f, flat: true);
                Assert.That(chosen, Is.Not.EqualTo(source), $"cluster {source} was offered itself as a relocation destination");
            }
        }
        finally
        {
            accessor.Dispose();
        }

        Assert.That(cs.ActiveClusterCount, Is.GreaterThan(1), "the population must produce several clusters, or there was nothing to choose between");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The batch — AC-10.5 / AC-10.9
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>AC-10.5</c> — after a tick of intra-cell relocation the database is intact: every entity still resident exactly
    /// once, every cluster still mapped to its cell, and the entity count unchanged.
    /// </summary>
    /// <remarks>
    /// <b>The entity-count assertion is the one that earns its place.</b> An intra-cell relocation claims a destination slot
    /// and releases a source slot in the SAME cell, so <c>CellState.EntityCount</c> is incremented and decremented against
    /// one counter. A pinned claim that forgot its increment — the scan overloads do it at three separate sites and the
    /// shared helper does not do it at all — would leave the cell under-counting by one per relocation, and nothing else
    /// in the engine would notice.
    /// </remarks>
    [Test]
    [VerifiesRule("CR-02")]
    [CancelAfter(60_000)]
    public void Relocation_LeavesEveryEntityResidentExactlyOnce()
    {
        using var dbe = SetupEngine();
        var cs = ClusterStateOf(dbe);

        const int Count = 300;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < Count; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(50f, 50f, i)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Spread inside cell (0,0) so relocation has something to do, then run enough ticks for the batch to drain.
        var rng = new Random(7788);
        MoveAll(dbe, (chunk, slot) => (3f + (float)rng.NextDouble() * 94f, 3f + (float)rng.NextDouble() * 94f));

        for (int tick = 2; tick <= 6; tick++)
        {
            dbe.WriteTickFence(tick);
        }

        var residents = ReadAll(dbe);
        var totalMigrations = cs.TotalMigrationCount;

        // The duplicate-slot check that used to sit here could not fail: ReadAll walks each cluster once and each occupancy
        // bit once, so a (chunk, slot) pair is unique by construction of the reader, not by any property of relocation.
        // What CAN fail — and what CR-02 names this test for — is the cell's entity count, so that is asserted instead.
        var cellTotal = 0;
        var countedCells = new HashSet<int>();
        foreach (var r in residents)
        {
            int cellKey = cs.ClusterCellMap[r.chunk];
            if (cellKey >= 0 && countedCells.Add(cellKey))
            {
                cellTotal += dbe.SpatialGrid.GetCell(cellKey).EntityCount;
            }
        }

        Assert.Multiple(() =>
        {
            // FIRST, because every assertion below is about the state AFTER relocation and all of them hold trivially if no
            // relocation ever ran. A cumulative count is the right instrument: the per-tick counter describes whichever tick
            // happened to be last, which on a settling world is usually a quiet one.
            Assert.That(totalMigrations, Is.GreaterThan(0),
                "no relocation executed across the whole run, so nothing below is evidence that relocation preserves anything");

            Assert.That(residents, Has.Count.EqualTo(Count), "an entity was lost or duplicated by relocation");
            foreach (var r in residents)
            {
                Assert.That(cs.ClusterCellMap[r.chunk], Is.GreaterThanOrEqualTo(0), $"cluster {r.chunk} holds entities but maps to no cell (C13)");
            }

            // CR-02's fourth invariant, asserted rather than merely described. A pinned claim is a fourth success site for
            // CellState.EntityCount — TryClaimSlotInCluster does not touch it and the scan overloads bump it at three other
            // sites — so a pinned path that forgot its increment leaves the cell short by one per relocation. Nothing else
            // in the engine reads the discrepancy, which is why it has to be read here.
            Assert.That(cellTotal, Is.EqualTo(Count),
                $"the cells report {cellTotal} entities but hold {Count} — a relocation claimed a slot without bumping CellState.EntityCount");
        });
    }

    /// <summary>
    /// The controlled experiment: identical population, identical per-entity motion, relocation ON versus OFF.
    /// </summary>
    /// <remarks>
    /// <para><b>What it settles.</b> The convergence reproducer shows mean cluster extent RISING on a decayed cell. Two
    /// explanations fit that single number — step 10 is merely ineffective on decayed input and the rise is the motion
    /// itself (in which case step 11's re-sort is the answer), or step 10 is actively degrading the layout (in which case it
    /// is a step-10 defect and step 11 would mask it). One run cannot distinguish them; the same run with relocation
    /// disabled can.</para>
    /// <para><b>Relocation is disabled through the P4 ratio</b>, not by deleting code: a target extent of 100 cells means
    /// the per-cluster gate never opens, so detection returns immediately and nothing is ever queued. Everything else —
    /// AABB refresh, the outlier guard, the fence itself — runs identically, which is what makes the arms comparable.</para>
    /// <para><b>Result, 2026-09-03 (Debug, 450 live entities, 30 ticks):</b> relocation OFF went 85.00 → 88.88; relocation ON
    /// went 70.46 → 83.27 with 11 986 migrations. Two things follow, and the second is the one that needed the experiment.
    /// First, step 10 HELPS: it drove the initial layout from 85.00 to 70.46 during the three setup ticks and still ended
    /// 5.6 units tighter than the untouched arm. Second, it does not achieve convergence under sustained motion — both arms
    /// rise, so decay outruns repair, and the re-sort of step 11 is what closes that.</para>
    /// <para><b>An earlier reproducer concluded step 10 was making bounds WORSE, and it was wrong.</b> It compared a single
    /// arm's before-and-after and read the +12.81 rise as degradation, not noticing that the "before" was taken after
    /// relocation had already improved the layout by 14.5 units. A delta measured from a baseline the mechanism itself
    /// produced is not evidence about the mechanism. That test has been deleted rather than patched: its premise, not its
    /// threshold, was the defect, and this A/B is what should have been written instead.</para>
    /// <para><b>The exchange rate is the open question this hands to step 11.</b> 11 986 migrations bought 5.6 units of mean
    /// extent — roughly 26 relocations per live entity over 30 ticks. That is thrash, and it is exactly what the
    /// re-clustering budget exists to bound.</para>
    /// <para><b>Motion is keyed on the entity TAG, not on (chunk, slot) or on RNG draw order.</b> Relocation moves entities
    /// between slots, so any motion derived from position in storage would diverge between the arms for reasons that have
    /// nothing to do with the layout, and the comparison would measure the divergence rather than the repair. Hashing the
    /// tag with the tick gives every entity the identical sequence of deltas in both arms.</para>
    /// </remarks>
    [Test]
    [Explicit("Controlled A/B measurement — run manually and read both arms together.")]
    [Category("Manual")]
    [CancelAfter(120_000)]
    public void AB_RelocationOnVersusOff_MeanClusterExtent([Values(false, true)] bool relocationEnabled)
    {
        const int Count = 600;
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();

        // A target extent of 100 cells cannot be exceeded, so the gate never opens and no drifter is ever detected.
        float targetRatio = relocationEnabled ? 0.25f : 100f;
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            new Vector3(0, 0, 0), new Vector3(WorldMax, WorldMax, 1f), CellSize,
            migrationHysteresisRatio: 0.05f, clusterTargetExtentRatio: targetRatio, clusterDriftMarginRatio: 0.05f,
            // Zero, for the reason SetupEngine records: this is an A/B of the DELTA path against itself, and a repair
            // would tighten both arms and mask the difference the measurement is about.
            reclusterBudgetMs: 0f, batchSpawnSortThreshold: 0 /* step 15: this fixture builds its layout by spawn ORDER; the Morton sort would tighten it at birth */,
            // Constant-mode target (step 14): the assertions here are written against the configured ratio; at 128-600 entities per cell the
            // density-derived target would sit between 0.49 and off, and the drifter sets they pin would change.
            clusterTargetPackingSlack: 0f));
        dbe.InitializeArchetypes();

        using var _ = dbe;
        var cs = ClusterStateOf(dbe);

        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < Count; i++)
            {
                ids.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(4f + (i * 37 % 92), 4f + (i * 61 % 92), i))));
            }

            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < ids.Count; i += 4)
            {
                tx.Destroy(ids[i]);
            }

            tx.Commit();
        }
        dbe.WriteTickFence(2);
        dbe.WriteTickFence(3);

        float before = MeanClusterExtent(cs);

        int tick = 4;
        for (int step = 0; step < 30; step++, tick++)
        {
            JitterByTag(dbe, step);
            dbe.WriteTickFence(tick);
        }

        float after = MeanClusterExtent(cs);
        var t = dbe.GetSpatialTelemetry(ArchetypeId);

        Assert.Pass($"AB relocation={(relocationEnabled ? "ON " : "OFF")} meanExtent {before:F2} -> {after:F2} "
            + $"(delta {after - before:+0.00;-0.00}) clusters={cs.ActiveClusterCount} totalMigrations={cs.TotalMigrationCount} "
            + $"lastTickDrifters={t.DriftersDetected}");
    }

    /// <summary>Moves every entity by a delta derived from its TAG and the step number — layout-independent.</summary>
    private static unsafe void JitterByTag(DatabaseEngine dbe, int step)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
#pragma warning disable TYPHON009 // Read to derive the next position from the current one.
                var positions = cluster.GetSpan(ClMigUnit.Pos);
#pragma warning restore TYPHON009
                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;

                    ref readonly var pos = ref positions[slot];
                    uint h = (uint)((pos.Tag * 0x9E3779B1) ^ (step * 0x85EBCA6B));
                    h ^= h >> 15;
                    h *= 0x2C1B3C6D;
                    h ^= h >> 13;

                    float dx = (((h & 0xFF) / 255f) - 0.5f) * 2f;
                    float dy = ((((h >> 8) & 0xFF) / 255f) - 0.5f) * 2f;
                    float x = Math.Clamp((0.5f * (pos.Bounds.MinX + pos.Bounds.MaxX)) + dx, 2f, 98f);
                    float y = Math.Clamp((0.5f * (pos.Bounds.MinY + pos.Bounds.MaxY)) + dy, 2f, 98f);
                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(x, y, pos.Tag));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    /// <summary>Mean over live clusters of the larger of the two axis extents, in world units.</summary>
    private static float MeanClusterExtent(ArchetypeClusterState cs)
    {
        float total = 0f;
        int counted = 0;
        for (int i = 0; i < cs.ActiveClusterCount; i++)
        {
            int c = cs.ActiveClusterIds[i];
            ref readonly var b = ref cs.ClusterAabbs[c];
            if (float.IsPositiveInfinity(b.MinX))
            {
                continue;
            }

            total += MathF.Max(b.MaxX - b.MinX, b.MaxY - b.MinY);
            counted++;
        }

        return counted == 0 ? 0f : total / counted;
    }

    /// <summary>
    /// Moves every entity by a small random delta from where it currently is, clamped inside cell (0,0).
    /// </summary>
    /// <remarks>
    /// Relative, unlike <see cref="MoveAll"/>, and that is the whole difference between measuring convergence and measuring
    /// nothing: a position computed from (chunk, slot) alone is re-randomised every tick, so the layout placement is
    /// chasing changes completely between ticks and no amount of correct relocation can tighten anything. Reading the
    /// current position and stepping from it is what makes the motion local, which is what real motion is.
    /// </remarks>
    private static unsafe void JitterEveryEntity(DatabaseEngine dbe, Random rng)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
#pragma warning disable TYPHON009 // Read to derive the next position from the current one.
                var positions = cluster.GetSpan(ClMigUnit.Pos);
#pragma warning restore TYPHON009
                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;

                    ref readonly var b = ref positions[slot].Bounds;
                    float x = Math.Clamp((0.5f * (b.MinX + b.MaxX)) + ((float)rng.NextDouble() - 0.5f) * 2f, 2f, 98f);
                    float y = Math.Clamp((0.5f * (b.MinY + b.MaxY)) + ((float)rng.NextDouble() - 0.5f) * 2f, 2f, 98f);
                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(x, y));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    /// <summary>
    /// <c>AC-10.5</c> — a queued relocation is executed once and then leaves the queue, however many ticks run.
    /// </summary>
    /// <remarks>
    /// <para><b>The invariant, stated as the assertion:</b> what is still queued when a tick ends is only what THAT tick
    /// filed. The pending queue has producers on both sides of its consumer — cell-crossing detection files during Prep,
    /// which precedes Migrate, while the outlier guard and drift detection file during AabbRefresh, which follows it — so
    /// the tick has to remove exactly the prefix it executed and keep the rest. Removing too much silently discards the
    /// AabbRefresh producers' work; removing too little re-executes stale requests against slots their entities have
    /// already left, forever.</para>
    /// <para><b>Written because the second failure actually happened, and nothing in the suite noticed.</b> The drain prefix
    /// was recorded at the bottom of Prep's body, and an archetype written through the spatial barrier leaves Prep through
    /// an earlier <c>return true</c> — the clean-bitmap branch, taken on every ordinary tick, because the barrier sets
    /// <c>ClusterProcessBitmap</c> and leaves <c>ClusterDirtyBitmap</c> clean. The prefix stayed zero, the queue grew by its
    /// drifter count every tick, and the entire backlog re-executed each time: 16 000 entities produced 17 234 migrations on
    /// the first tick and 224 854 by the twentieth. Every functional test still passed, because each individual relocation
    /// was applied correctly — only the COUNT was absurd.</para>
    /// <para><b>Asserted per tick against that tick's own drifter count</b>, which is the tightest form available without
    /// coupling to placement: a bound like "fewer than the population" would let a slow leak through, and the failure mode
    /// here is unbounded growth that a generous constant hides for exactly as long as the test is short.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("CR-01")]
    [CancelAfter(60_000)]
    public void PendingQueue_KeepsOnlyWhatTheCurrentTickFiled()
    {
        const int Count = 400;
        using var dbe = SetupEngine();
        var cs = ClusterStateOf(dbe);

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < Count; i++)
            {
                // Spread across the cell from the start, so every cluster is past its target extent and detection has work
                // on every tick. A tight population would leave the queue empty and the assertion vacuous.
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(4f + (i * 37 % 92), 4f + (i * 61 % 92), i)));
            }

            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var rng = new Random(7717);
        int maxQueued = 0;
        int totalDrifters = 0;

        for (int tick = 2; tick <= 10; tick++)
        {
            MoveAll(dbe, (chunk, slot) => (3f + (float)rng.NextDouble() * 94f, 3f + (float)rng.NextDouble() * 94f));
            dbe.WriteTickFence(tick);

            int drifters = dbe.GetSpatialTelemetry(ArchetypeId).DriftersDetected;
            totalDrifters += drifters;
            maxQueued = Math.Max(maxQueued, cs.PendingMigrationCount);

            Assert.That(cs.PendingMigrationCount, Is.LessThanOrEqualTo(drifters),
                $"tick {tick} left {cs.PendingMigrationCount} requests queued but only detected {drifters} drifters — the queue is retaining executed"
                + $"requests");
        }

        Assert.Multiple(() =>
        {
            Assert.That(totalDrifters, Is.GreaterThan(0), "no drifter was ever detected, so the queue was empty and this asserted nothing");
            Assert.That(maxQueued, Is.GreaterThan(0), "nothing was ever queued, so the bound above held trivially");
        });
    }

    /// <summary>
    /// <c>AC-10.9</c> — the source cluster's stored bound tightens after its outlying entity leaves, in BOTH spatial
    /// maintenance modes.
    /// </summary>
    /// <remarks>
    /// <para><b>Asserted on the BOUND, not on the flag.</b> Checking that <c>ClusterShrinkPendingAxes</c> was set would pass
    /// even if nothing ever consumed it — and that is the exact failure mode, because migration also clears the source
    /// slot's dirty bit and can drop the cluster out of the refresh pass entirely.</para>
    /// <para>Before step 10 this was documented behaviour rather than a bug: <i>"The src cluster's AABB stays conservative
    /// (not shrunk) — Phase 1 trade-off."</i> A relocation that tightens nothing is a relocation that bought nothing.</para>
    /// <para><b>Both refresh branches, because only one of them can fail.</b> The LEGACY branch recomputes every dirty
    /// cluster's bound unconditionally, so a migration source tightens there whether or not anything flagged it — ablating
    /// <c>FlagClusterForShrinkRefresh</c> leaves that arm green. The BARRIER-ONLY branch gates the recompute on the shrink
    /// mask and keeps the stored box otherwise, so there and only there does the flag decide the outcome. Running only the
    /// legacy arm measures a mechanism that predates step 10 and reports it as step 10 working.</para>
    /// <para><b>This was briefly recorded as a known-red gap, and that was wrong.</b> The barrier arm failed once, in a run
    /// that immediately followed an ablation whose restore preserved the backup's older mtime — so MSBuild considered the
    /// assembly current and the ablated <c>FlagClusterForShrinkRefresh</c> was still in the binary. The symptom matched a
    /// real defect exactly (barrier mode not shrinking, legacy mode shrinking), which is what made it convincing. Re-run
    /// against a forced rebuild, both modes tighten. Left in the record because a stale-binary result is indistinguishable
    /// from a genuine one by inspection, and the only defence is rebuilding before believing a red.</para>
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void Relocation_ShrinksTheSourceClusterBound([Values(false, true)] bool barrierOnly)
    {
        using var dbe = SetupEngine();
        var cs = ClusterStateOf(dbe);
        if (barrierOnly)
        {
            // Legitimate here: every spatial write in this fixture goes through WriteSpatial, which is exactly the
            // precondition SetSpatialBarrierOnly documents.
            dbe.SetSpatialBarrierOnly<ClMigUnit>();
        }

        // Two tight groups in cell (0,0): a core near the low corner and a second group near the high corner. The second
        // group is not decoration — it is the DESTINATION. With a single cluster in the cell, placement has nowhere to put a
        // drifter, correctly returns none, and nothing shrinks; that is real behaviour ("this cell has run out of room"),
        // but it is not what this test is about.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 64; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(10f + (i % 5), 10f + (i / 5), i)));
            }
            for (int i = 0; i < 20; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(88f + (i % 5), 88f + (i / 5), 1000 + i)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Shove one entity to the far corner of the same cell, which spreads its cluster past the target extent.
        // Shove ONE entity of the low-corner cluster to the far corner. Its own cluster is stretched to width ~80; the
        // high-corner cluster is right where it landed, so placement has an obvious, cheap destination.
        int lowCluster = cs.ActiveClusterIds[0];
        MoveAll(dbe, (chunk, slot) =>
        {
            if (chunk == lowCluster && slot == 0)
            {
                return (90f, 90f);
            }
            if (chunk == lowCluster)
            {
                return (10f + (slot % 5), 10f + (slot / 5));
            }
            return (88f + (slot % 5), 88f + (slot / 5));
        });
        dbe.WriteTickFence(2);

        // The tick that detects, asserted before the ticks that repair. Without this the test still passes when detection
        // finds nothing and the bound narrows for some unrelated reason — and "the width went down" is far too weak an
        // observation to carry the whole AC on its own.
        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).DriftersDetected, Is.GreaterThan(0),
            "the outlier must have been detected as a drifter, or the shrink below is not the one this test is about");

        int stretched = -1;
        float widthBefore = 0f;
        for (int i = 0; i < cs.ActiveClusterCount; i++)
        {
            int c = cs.ActiveClusterIds[i];
            ref readonly var box = ref cs.ClusterAabbs[c];
            float w = box.MaxX - box.MinX;
            if (w > widthBefore)
            {
                widthBefore = w;
                stretched = c;
            }
        }

        Assert.That(widthBefore, Is.GreaterThan(50f), "the outlier must actually have stretched a cluster, or there is nothing to shrink");

        // Let the queued relocation drain and the refresh recompute the source.
        for (int tick = 3; tick <= 7; tick++)
        {
            dbe.WriteTickFence(tick);
        }

        ref readonly var after = ref cs.ClusterAabbs[stretched];
        float widthAfter = after.MaxX - after.MinX;

        Assert.That(widthAfter, Is.LessThan(widthBefore),
            $"barrierOnly={barrierOnly}: the source cluster kept its stretched bound ({widthBefore:F2}) after the outlier left — "
            + "the relocation tightened nothing");
    }
}
