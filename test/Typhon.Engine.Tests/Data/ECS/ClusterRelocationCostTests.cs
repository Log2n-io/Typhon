using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-10.7</c> — what intra-cell detection and relocation actually cost, per entity, single-threaded and W-scaled
/// (#872 step 10).
/// </summary>
/// <remarks>
/// <para><b><c>[Explicit]</c>, and it must stay that way.</b> These are measurements, not assertions. A timing threshold in
/// the suite is a flake generator on shared CI hardware, and a measurement that has been weakened until it stops flaking
/// no longer measures anything. What this fixture owes step 11 is NUMBERS to calibrate <c>ClusterTargetExtentRatio</c> and
/// the re-clustering budget against — the design leaves both TBD precisely because nobody had any.</para>
///
/// <para><b>Run it in Release.</b> A Debug figure is dominated by unelided bounds checks and uninlined accessors on the
/// exact per-slot loop being measured, so it does not merely scale the answer — it changes which half is expensive:</para>
/// <code>
/// dotnet test test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj -c Release --filter "FullyQualifiedName~ClusterRelocationCostTests"
/// </code>
///
/// <para><b>The two halves are budgeted three orders of magnitude apart (§5.2), so they are reported separately.</b>
/// Detection is a scan over everything that moved, budgeted at <b>0.576 ns/entity</b>; relocation is a full migration per
/// drifter. Folding them into one figure would hide which one a regression landed in — and would hide the asymmetry that
/// is the design's entire premise.</para>
///
/// <para><b>Per-entity relocation cost comes from <c>MigrationExecuteMs</c>, which is summed across workers rather than
/// wall-clock.</b> That makes it CPU time per entity and therefore comparable across worker counts: a figure that stayed
/// flat as W rose would mean perfect scaling and no added contention, and a figure that climbs is the contention itself.
/// Wall-clock per entity would fall with W whether or not the work got cheaper, which is the less interesting question.</para>
/// </remarks>
[TestFixture]
[Explicit("Measurement, not an assertion — run manually, in Release. See AC-10.7.")]
// Manual, not Nightly, and the distinction is the point. The nightly runs on the shared c6id gate box alongside eight
// test shards, so a per-entity nanosecond figure taken there measures whatever else was resident — and a number nobody
// can act on is worse than no number, because it gets quoted. These figures exist to calibrate P4 and step 11's budget,
// which needs a quiet machine and a Release build. There is no assertion here to regress, so nothing is lost by CI
// skipping it; what would be lost is trust in the numbers.
[Category("Manual")]
[NonParallelizable]
class ClusterRelocationCostTests : TestBase<ClusterRelocationCostTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    /// <summary>Entities per cell — enough that per-cluster fixed costs are amortised across ~40 clusters.</summary>
    private const int EntitiesPerCell = 2_000;

    /// <summary>Cells used by default. Several so the fence has slices to distribute.</summary>
    /// <remarks>
    /// <b>This is a hard ceiling on the Migrate phase's parallelism, not a detail of the fixture (#912).</b> The plan carves Migrate slices on destination
    /// cell-key boundaries — a slice may never split one cell — so a workload occupying N cells produces at most N slices however many workers it is given.
    /// At the default 8, a run at W = 8 hands each worker exactly one cell and the phase's span is set by the largest of them. <see cref="CellsPerRow"/>
    /// and the grid layout exist so that ceiling can be raised and the effect measured rather than argued about.
    /// </remarks>
    private const int CellCount = 8;

    /// <summary>Cells per row in the populated grid. <c>WorldMax / CellSize</c>, so the grid fills the configured world.</summary>
    private const int CellsPerRow = 10;

    /// <summary>Origin of the cell with the given index, laid out row-major over the world.</summary>
    /// <remarks>
    /// Row-major rather than a single strip along X because the strip capped the fixture at ten cells, which is below the worker counts it sweeps. The
    /// destination-cell slicing rule then made the cell count — not the worker count — the binding constraint, invisibly.
    /// </remarks>
    private static (float X, float Y) CellOriginOf(int cell) =>
        ((cell % CellsPerRow) * CellSize, (cell / CellsPerRow) * CellSize);

    private const int MeasuredTicks = 20;

    /// <summary>Per-axis displacement per tick, in world units, for the arms that measure relocation.</summary>
    /// <remarks>
    /// <para><b>Twenty units, not the one-and-a-half this fixture shipped with, and the change is the difference between measuring relocation and measuring
    /// nothing (#912).</b> A drifter is an entity more than <c>targetExtent / 2 + cellSize x ClusterDriftMarginRatio</c> from its cluster's centroid on some
    /// axis — 12.5 + 5 = 17.5 units under this configuration. At +/-1.5 per tick a random walk covers about 6.7 units over the twenty measured ticks, so an
    /// entity can only become a drifter if its cluster was ALREADY spread when it was born.</para>
    /// <para><b>It was, and then step 15 fixed that.</b> The old comment on <see cref="SpawnPopulation"/> says "first-fit put these entities together and
    /// motion pulled them apart", and while placement was first-fit in arrival order a batch spawn did produce clusters at ~99 % of the cell. D4's Morton
    /// ordering of the spawn batch now lands them at 1.4x the packing bound instead — measured here at extent ratio 0.24-0.28 against a bound of 0.175 — so
    /// the gate fires on 70 % of clusters and the per-entity test then absorbs ~98 % of what it looks at. The fixture reported 12-27 drifters and ~35
    /// migrations per tick where the campaign behind the design's §5.8.1 measured 11 000-15 000 of each.</para>
    /// <para><b>So this is a faster world, not a broken one.</b> Twenty units per axis per tick against a 94-unit cell scatters a cluster's entities across
    /// their whole cell within two ticks, which is the regime where relocation is supposed to earn its keep: entities moving faster than placement can
    /// follow. It stays strictly INSIDE the cell, so the arm is still pure relocation with no crossings mixed in — <see cref="Totals.Report"/> prints the
    /// kind split so that is checked rather than assumed.</para>
    /// </remarks>
    private const float RelocationJitter = 20f;

    /// <summary>The displacement the fixture used before #912, kept for the settled-world arm.</summary>
    /// <remarks>
    /// A world this slow produces almost no relocation under current placement, which is the RESULT rather than a defect — see
    /// <see cref="RelocationJitter"/>. Kept as an arm so a regression in placement shows up as this arm starting to migrate.
    /// </remarks>
    private const float SettledJitter = 1.5f;

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
            reclusterBudgetMs: 0f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[ArchetypeId].ClusterState;

    /// <summary>
    /// Spawns a population whose clusters are ALREADY spread across their cell, then frees a fraction of the slots.
    /// </summary>
    /// <remarks>
    /// <para><b>Spread, not tight.</b> The first version spawned everything at one point and relied on per-tick jitter to
    /// spread it; a ±1.5-unit random walk covers about 6.7 units over 20 ticks against a 25-unit target extent, so the
    /// gate never opened and the run measured 0 drifters and 0 migrations. The steady state this step exists for is a world
    /// whose placement has ALREADY decayed — first-fit put these entities together and motion pulled them apart — so the
    /// measurement starts from decayed and asks what the repair costs.</para>
    /// <para><b>The destroy pass is what makes relocation possible at all.</b> Clusters hold 49 slots and
    /// <c>ClaimSlotInCell</c> packs first-fit, so a freshly spawned cell is a row of full clusters plus one remainder — and
    /// <c>ChooseRelocationTarget</c> skips full clusters, so almost every drifter would find nowhere to go. Freeing one slot
    /// in five models ordinary churn and gives placement something to work with. The size of the remaining gap between
    /// drifters and migrations is itself part of what this fixture reports.</para>
    /// </remarks>
    private static void SpawnPopulation(DatabaseEngine dbe, int cellCount, int perCell = EntitiesPerCell)
    {
        var ids = new List<EntityId>(cellCount * perCell);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int cell = 0; cell < cellCount; cell++)
            {
                var (originX, originY) = CellOriginOf(cell);
                for (int i = 0; i < perCell; i++)
                {
                    uint h = (uint)((cell * 0x9E3779B1) ^ (i * 0x85EBCA6B));
                    h ^= h >> 15;
                    h *= 0x2C1B3C6D;
                    h ^= h >> 12;
                    uint g = h * 0x27D4EB2F;
                    g ^= g >> 15;

                    float x = originX + 3f + (h % 10_000) * (94f / 10_000f);
                    float y = originY + 3f + (g % 10_000) * (94f / 10_000f);
                    ids.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(x, y, (cell * perCell) + i))));
                }
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < ids.Count; i += 5)
            {
                tx.Destroy(ids[i]);
            }

            tx.Commit();
        }
    }

    /// <summary>
    /// Jitters every entity within its own cell — the steady-state motion the step is designed for, not a teleport.
    /// </summary>
    /// <remarks>
    /// Deliberately intra-cell. An entity that crosses a cell boundary is the CELL-CROSSING detector's business and would
    /// mix that path's cost into a figure that is supposed to describe intra-cell drift alone.
    /// </remarks>
    private static unsafe void JitterEveryEntity(DatabaseEngine dbe, int tick, float amplitude)
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
                    float x = 0.5f * (b.MinX + b.MaxX);
                    float y = 0.5f * (b.MinY + b.MaxY);

                    uint h = (uint)((cluster.ChunkId * 0x9E3779B1) ^ (slot * 0x85EBCA6B) ^ (tick * 0x27D4EB2F));
                    h ^= h >> 15;
                    h *= 0x2C1B3C6D;
                    h ^= h >> 13;

                    // +/-amplitude world units per tick per axis, clamped inside the entity's own cell. The clamp is what keeps this arm free of cell
                    // crossings; see RelocationJitter for why the amplitude is a parameter rather than the constant it used to be.
                    // Both axes, because the population is a GRID now. The Y clamp used to be against [1, CellSize - 1] unconditionally, which was
                    // correct only while every cell sat in row zero; on a grid it would drag every entity into the bottom row — a cell crossing, on the
                    // arm whose whole premise is that it never crosses.
                    float cellOriginX = MathF.Floor(x / CellSize) * CellSize;
                    float cellOriginY = MathF.Floor(y / CellSize) * CellSize;
                    float span = amplitude * 2f;
                    float nx = Math.Clamp(x + (((h & 0xFF) / 255f) - 0.5f) * span, cellOriginX + 1f, cellOriginX + CellSize - 1f);
                    float ny = Math.Clamp(y + ((((h >> 8) & 0xFF) / 255f) - 0.5f) * span, cellOriginY + 1f, cellOriginY + CellSize - 1f);

                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(nx, ny));
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
    /// Walks every entity ACROSS cell boundaries — the cell-crossing counterpart of <see cref="JitterEveryEntity"/>, for #912's control arm.
    /// </summary>
    /// <remarks>
    /// <para><b>A full cell per tick per axis, wrapped inside the populated strip.</b> The population occupies the eight cells along X and the first cell
    /// along Y, so a displacement of exactly <see cref="CellSize"/> lands every entity in a neighbouring cell every tick and the queue is all
    /// <c>CellCrossing</c>. Wrapped rather than clamped: a clamp would pile the population into the two end cells within a few ticks and the arm would
    /// stop measuring a steady rate.</para>
    /// <para><b>X only.</b> The strip is one cell deep on Y, so a Y displacement would leave the populated region entirely and the migration would be a
    /// cell birth rather than a crossing between established cells — a different path with a different cost, which is not what the control is for.</para>
    /// </remarks>
    private static unsafe void WalkAcrossCells(DatabaseEngine dbe, int tick, int cellCount)
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
                    float x = 0.5f * (b.MinX + b.MaxX);
                    float y = 0.5f * (b.MinY + b.MaxY);

                    uint h = (uint)((cluster.ChunkId * 0x9E3779B1) ^ (slot * 0x85EBCA6B) ^ (tick * 0x27D4EB2F));
                    h ^= h >> 15;
                    h *= 0x2C1B3C6D;
                    h ^= h >> 13;

                    // One cell left or right, wrapped inside the entity's own ROW of the populated grid. The offset within the cell is preserved, so the
                    // destination cell's occupancy stays as uniform as the source's and no cell drifts toward being full. Wrapping within the row rather
                    // than across the whole grid keeps every destination inside the populated region, so a crossing is always between two established
                    // cells and never a cell birth — a different path with a different cost.
                    var row = MathF.Floor(y / CellSize);
                    var rowCells = Math.Min(CellsPerRow, Math.Max(1, cellCount - (int)row * CellsPerRow));
                    var rowSpan = rowCells * CellSize;
                    float nx = x + ((h & 1) == 0 ? CellSize : -CellSize);
                    if (nx < 1f)
                    {
                        nx += rowSpan;
                    }
                    else if (nx > rowSpan - 1f)
                    {
                        nx -= rowSpan;
                    }

                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(nx, y));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    private readonly record struct Totals(long Scanned, long Drifters, long Absorbed, long Migrations, double MigrationMs, double FenceMs,
        long Slices, double PrologueMs, double EpilogueMs, long Crossings, long Relocations, long Repairs, long LockAcquisitions,
        double ParallelismSum, int ParallelismSamples, double TotalMs)
    {
        internal Totals Add(SpatialMigrationTelemetry t, double fenceMs, double migrationParallelism = 0d) =>
            new(Scanned + t.ClustersScanned, Drifters + t.DriftersDetected, Absorbed + t.DriftAbsorbedCount,
                Migrations + t.MigrationCount, MigrationMs + t.MigrationExecuteMs, FenceMs + fenceMs,
                Slices + t.MigrationSliceCount, PrologueMs + t.MigrationPrologueMs, EpilogueMs + t.MigrationEpilogueMs,
                Crossings + t.CrossingsExecuted, Relocations + t.RelocationsExecuted, Repairs + t.RepairsExecuted,
                LockAcquisitions + t.FinalizeLockAcquisitions,
                ParallelismSum + migrationParallelism, ParallelismSamples + (migrationParallelism > 0d ? 1 : 0),
                TotalMs + t.MigrationTotalMs);

        internal void Report(StringBuilder sb, string label, int clusterCapacity)
        {
            // Scanned counts CLUSTERS; the per-entity budget in §5.2 is per SLOT, and the scan visits every slot of a
            // cluster it opens. Multiplying by capacity is the honest conversion and is stated rather than hidden.
            long slots = Scanned * clusterCapacity;
            sb.AppendLine($"  {label,-22} clustersScanned={Scanned,-9:N0} slots≈{slots,-11:N0} drifters={Drifters,-8:N0} absorbed={Absorbed,-8:N0} "
                + $"migrations={Migrations,-8:N0} unplaced={Drifters - Migrations,-8:N0} "
                + $"({(Drifters > 0 ? 100.0 * (Drifters - Migrations) / Drifters : 0),5:F1}%)");
            // "n/a", not 0.000. The parallel arm has no wall-clock term to divide, and a zero printed in a column labelled
            // "upper bound" reads as a measurement of a very fast thing rather than as the absence of one.
            var detection = FenceMs > 0d && slots > 0
                ? $"{FenceMs * 1e6 / slots,8:F3} ns/slot (whole fence, upper bound)"
                : $"{"n/a",8} (no wall-clock term on this arm)";
            sb.AppendLine($"  {"",-22} detection≈{detection}   "
                + $"migration={(Migrations > 0 ? MigrationMs * 1e6 / Migrations : 0),9:F1} ns/entity (CPU, summed over workers)");

            // #912. The quotient above is a sum of per-SLICE spans over a per-ENTITY count, so it is only a per-entity cost while `slices` is 1. Everything
            // below decomposes it: the fixed term is the prologue and epilogue, which scale with the slice count, and the variable term is what is left.
            // Printing them beside the quotient rather than in a doc comment is the whole remedy — the anomaly this fixture recorded for three steps was the
            // undecomposed quotient being read as contention.
            var fixedMs = PrologueMs + EpilogueMs;
            var perSliceFixedNs = Slices > 0 ? fixedMs * 1e6 / Slices : 0d;
            var fixedPerEntityNs = Migrations > 0 ? fixedMs * 1e6 / Migrations : 0d;
            var variablePerEntityNs = Migrations > 0 ? (MigrationMs - fixedMs) * 1e6 / Migrations : 0d;
            sb.AppendLine($"  {"",-22} slices={Slices,-6:N0} fixed/slice={perSliceFixedNs,8:F0} ns   "
                + $"of the per-entity figure: fixed={fixedPerEntityNs,8:F1} ns ({(MigrationMs > 0 ? 100 * fixedMs / MigrationMs : 0),4:F0}%)  "
                + $"VARIABLE={variablePerEntityNs,8:F1} ns");
            // The kind split is what makes the arm's identity checkable instead of assumed, and the three must sum to `migrations` exactly.
            sb.AppendLine($"  {"",-22} executed: crossings={Crossings,-8:N0} relocations={Relocations,-8:N0} repairs={Repairs,-8:N0} "
                + $"(sum={Crossings + Relocations + Repairs:N0}, migrations={Migrations:N0})   finalizeLock={LockAcquisitions,-7:N0} acquisitions");

            // #912. CPU per entity is the wrong number to stop on. The migration phases' achieved parallelism — CPU summed over chunks divided by the
            // phases' elapsed span, which the engine already computes for the repair budget — says what that CPU bought: a per-entity CPU figure that rises
            // by the same factor the parallelism rises is work being SPREAD, and the wall time is unchanged. It is only contention where the CPU rises
            // FASTER than the parallelism, and the two have to be printed together for that comparison to be possible at all.
            if (ParallelismSamples > 0)
            {
                var parallelism = ParallelismSum / ParallelismSamples;

                // TotalMs, NOT MigrationMs, and the distinction is the difference between a latency and a number with no referent.
                // LastFenceMigrationParallelism is CPU/span across Migrate + IndexMassUpdate + EntityMapUpdate (TyphonRuntime.RunParallelFence), so the only
                // numerator it can legitimately divide is the CPU summed over those same three phases — which is MigrationTotalMs. MigrationExecuteMs brackets
                // the migrant loop ALONE, roughly half of a migration since #872 step 6 moved the index and EntityMap applies into their own phases; dividing
                // it by a three-phase ratio produces a quotient that is not the wall time of the loop, of the migration, or of anything else.
                sb.AppendLine($"  {"",-22} migration parallelism={parallelism,5:F2}x (CPU / span, 3 phases)   "
                    + $"=> whole-migration wall {(Migrations > 0 ? TotalMs * 1e6 / Migrations / parallelism : 0),8:F1} ns/entity");
            }

            // The loop is under half of what a migration costs; the bulk index descent and the bulk EntityMap patch are the rest, and they run in their own
            // phases. Printed beside the loop figure because an inflation the loop shows and the whole pipeline does not would be a property of the loop,
            // and one both show is a property of the machine the phases share.
            if (TotalMs > 0d && Migrations > 0)
            {
                sb.AppendLine($"  {"",-22} whole migration (loop + both applies) = {TotalMs * 1e6 / Migrations,8:F1} ns/entity CPU");
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary><c>AC-10.7</c> — single-threaded cost, driven by the serial fence.</summary>
    /// <remarks>
    /// The serial arm reports detection as <i>whole fence time ÷ slots visited</i>, which is an UPPER bound rather than the
    /// isolated figure: the same fence also refreshes AABBs, drains shadow entries and executes migrations. Stated as a
    /// bound because an unqualified number here would be read as the 0.576 ns/entity budget's counterpart and quietly
    /// compared against it. Isolating detection needs a probe inside the slice, which is step 11's business.
    /// </remarks>
    [Test]
    [CancelAfter(600_000)]
    public void Cost_SerialFence() => RunSerial(RelocationJitter, "serial (W=1)");

    /// <summary>
    /// The same measurement on a SLOW world — the amplitude this fixture used before #912 — which now relocates almost nothing.
    /// </summary>
    /// <remarks>
    /// <para><b>An arm that is expected to read near zero, and is here because of what that proves.</b> Step 15's D4 Morton spawn ordering lands a batch at
    /// about 1.4x the packing bound instead of the ~99 % of the cell first-fit produced, so a world moving 1.5 units per tick never drifts far enough from
    /// its cluster's centroid to clear the 17.5-unit test. The design's §5.8.1 anomaly — relocation CPU per entity at W = 2/4/8 — was measured on a
    /// population this fixture can no longer build, which is why it could not be reproduced when #912 came to look at it.</para>
    /// <para>Kept as an arm rather than a comment so a placement regression announces itself: if this one starts migrating, D4 has stopped working.</para>
    /// </remarks>
    [Test]
    [CancelAfter(600_000)]
    public void Cost_SerialFence_SettledWorld() => RunSerial(SettledJitter, "serial settled (W=1)");

    private void RunSerial(float amplitude, string label)
    {
        const int cellCount = CellCount;
        using var dbe = SetupEngine();
        SpawnPopulation(dbe, cellCount);
        dbe.WriteTickFence(2);

        var cs = ClusterStateOf(dbe);
        var sb = new StringBuilder();
        sb.AppendLine($"AC-10.7 — intra-cell drift cost, {cellCount * EntitiesPerCell:N0} entities over {cellCount} cells, "
            + $"{cs.ActiveClusterCount:N0} clusters, {MeasuredTicks} ticks, jitter +/-{amplitude:F1} units/axis/tick");
        sb.AppendLine("  WORST CASE, NOT STEADY STATE. Every entity moves every tick and the jitter is large enough to scatter a cluster across its whole");
        sb.AppendLine("  cell, so the drifter fraction is an upper bound rather than a prediction — §5.2's model assumes ~1%. Relocation cannot converge");
        sb.AppendLine("  against a population that re-randomises itself every tick; what this measures is the ceiling.");
        sb.AppendLine("  `extent` vs `bound` is the reading that says whether the population is actually spread: at extent ~= bound the layout is already");
        sb.AppendLine("  as tight as geometry allows and there is nothing for relocation to do, whatever the drifter count says.");

        // One untimed warm-up tick: the first jitter dirties every cluster for the first time and pays page-cache and
        // JIT costs that no steady-state tick pays.
        JitterEveryEntity(dbe, 0, amplitude);
        dbe.WriteTickFence(3);

        var totals = default(Totals);
        var sw = new Stopwatch();
        for (int tick = 0; tick < MeasuredTicks; tick++)
        {
            JitterEveryEntity(dbe, tick + 1, amplitude);
            sw.Restart();
            dbe.WriteTickFence(4 + tick);
            sw.Stop();
            var tel = dbe.GetSpatialTelemetry(ArchetypeId);

            // Per-tick rows, because the aggregate hides the trend that matters. `clusters` falling is the step working:
            // relocation packs entities into fewer, tighter clusters, and a run where it stays flat means the repair is
            // detecting drift it cannot act on. `queued` is the exactly-once check — it must track this tick's drifters, not
            // accumulate — and watching it grow without bound is how the drain-prefix bug was found.
            sb.AppendLine($"    tick {tick,2}: scanned={tel.ClustersScanned,-6} gated={tel.DriftGatedClusters,-6} drifters={tel.DriftersDetected,-7} "
                + $"absorbed={tel.DriftAbsorbedCount,-6} suppressed={tel.DriftSuppressedByDensity,-6} throttled={tel.RelocationsThrottled,-6} "
                + $"admitted={tel.RelocationsAdmitted,-6} boost={cs.DriftTargetBoost,-5:F2} extent={tel.MeanClusterExtentRatio,-5:F3} "
                + $"bound={tel.MeanPackingBound,-5:F3} migrated={tel.MigrationCount,-7} "
                + $"queued={cs.PendingMigrationCount,-7} clusters={cs.ActiveClusterCount}");
            totals = totals.Add(tel, sw.Elapsed.TotalMilliseconds);
        }

        totals.Report(sb, label, cs.Layout.ClusterSize);
        Assert.Pass(sb.ToString());
    }

    /// <summary><c>AC-10.7</c> — the same cost under the parallel fence, at increasing worker counts.</summary>
    /// <remarks>
    /// <para><b>One arm per test case, not a loop inside one test.</b> The first version looped over the worker counts and
    /// called <c>SetupEngine()</c> each time; <c>TestBase</c> resolves ONE <c>DatabaseEngine</c> per test from the root
    /// provider, so the second call threw <c>ConfigureSpatialGrid must be called before InitializeArchetypes</c> and only
    /// <c>W=1</c> was ever measured. The W-scaling this fixture exists to produce was silently absent, and
    /// <c>[Explicit]</c> meant CI would never have said so.</para>
    /// <para>Read the arms together: the per-entity relocation figure is CPU time summed across workers, so it should stay
    /// roughly flat, and the amount by which it does not is the contention the parallel fence adds.</para>
    /// <para><b>Read the SLICE COUNT before reading the per-entity figure (#912).</b> <c>MigrationExecuteMs</c> is a sum of per-slice spans and the plan
    /// sizes slices from the worker count, so raising W raises the number of spans in the numerator while the workload fixes the denominator. The report
    /// prints <c>slices</c> and the prologue/epilogue split beside the quotient for that reason: a per-entity figure that climbs with W is contention only
    /// once the per-slice fixed term has been subtracted, and reading it without that is how the 425 -&gt; 844 -&gt; 1 440 sequence stood unexplained for
    /// three steps.</para>
    /// </remarks>
    [Test]
    [CancelAfter(600_000)]
    public void Cost_ParallelFence([Values(1, 2, 4, 8)] int workerCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"AC-10.7 — intra-cell drift cost under the parallel fence, {CellCount * EntitiesPerCell:N0} entities over {CellCount} cells");
        sb.AppendLine(MeasureParallel(workerCount, false, $"parallel reloc (W={workerCount})"));
        Assert.Pass(sb.ToString());
    }

    /// <summary>
    /// <c>#912 AC-3</c> — the CELL-CROSSING drain on the same axis, as the control for the relocation arm above.
    /// </summary>
    /// <remarks>
    /// <para><b>It is the control, and that is the whole point of it.</b> The two drains share <c>ExecuteMigrations</c> almost entirely: whatever explains a
    /// per-entity CPU figure that climbs with W has to explain why one kind climbs and the other does not — or, by showing that both do, say that the effect
    /// belongs to the drain rather than to relocation. The design's §5.8.1 asserted the asymmetry from a serial measurement of one arm and a parallel
    /// measurement of the other, which are not comparable.</para>
    /// <para><b>Same population, same tick count, same cells; only the clamp differs.</b> The relocation arm keeps every entity inside its own cell, so its
    /// queue is all <c>Relocation</c>; this one lets them walk across boundaries, so its queue is all <c>CellCrossing</c>. The report prints the executed
    /// kind split, so which arm got which is checked rather than assumed.</para>
    /// </remarks>
    [Test]
    [CancelAfter(600_000)]
    public void Cost_ParallelFence_CellCrossing([Values(1, 2, 4, 8)] int workerCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#912 AC-3 — cell-crossing drain under the parallel fence, {CellCount * EntitiesPerCell:N0} entities over {CellCount} cells");
        sb.AppendLine(MeasureParallel(workerCount, true, $"parallel cross (W={workerCount})"));
        Assert.Pass(sb.ToString());
    }

    /// <summary>
    /// <c>#912</c> — the same relocation arm at W = 8 with the cell count raised, which raises the Migrate phase's slice ceiling.
    /// </summary>
    /// <remarks>
    /// <para><b>The experiment the slicing rule demands.</b> A Migrate slice may never split a destination cell, so a workload occupying N cells yields at
    /// most N slices whatever the worker count. At the fixture's default of eight, a run at W = 8 gives each worker exactly one cell and the phase's span is
    /// whatever the largest cell costs — no amount of extra workers can help, and the shortfall is load imbalance rather than contention.</para>
    /// <para>Raising the cell count at fixed density is the discriminator: if achieved parallelism rises with the cell count at a fixed W, the ceiling was
    /// the slicing rule. If it does not, the loss is somewhere the plan cannot reach and the slicing rule is exonerated.</para>
    /// </remarks>
    [Test]
    [CancelAfter(600_000)]
    public void Cost_ParallelFence_CellCountSweep([Values(8, 16, 32)] int cellCount)
    {
        // The TOTAL population is held constant and spread thinner, so the arms differ in the number of slices the plan can carve and in nothing else. The
        // first version kept 2 000 entities per CELL, which quadrupled the working set at 32 cells and failed on page-cache back-pressure rather than on
        // anything to do with slicing — an experiment that varies two things at once and then dies of the second one.
        var perCell = CellCount * EntitiesPerCell / cellCount;
        var sb = new StringBuilder();
        sb.AppendLine($"#912 — relocation at W=8 over {cellCount} cells ({cellCount * perCell:N0} entities, {perCell:N0}/cell), slice ceiling = cell count");
        sb.AppendLine(MeasureParallel(8, false, $"W=8 x {cellCount} cells", cellCount, perCell));
        Assert.Pass(sb.ToString());
    }

    /// <summary>
    /// <c>#912</c> — the A/B for the per-migrant source-cluster flag writes, interleaved inside one binary.
    /// </summary>
    /// <remarks>
    /// <para><b>What is being tested.</b> Every migration issues two atomic read-modify-writes indexed by its SOURCE cluster's chunk id — a CAS into a
    /// <c>byte[]</c> that packs 64 clusters per cache line, and an <c>Interlocked.Or</c> into a bit-per-cluster <c>long[]</c> that packs 512. At a few
    /// hundred clusters the whole bitmap is ONE line, so eight workers take the same line exclusive on every migrant. Deferring both to the per-chunk drain
    /// — which already carries the source chunk id and already holds the finalize latch — turns them into plain writes issued once per chunk.</para>
    /// <para><b>Interleaved, in one binary, alternating.</b> Two batches run minutes apart drift with whatever else the machine is doing; the pairing is
    /// what makes the difference attributable to the switch rather than to the box.</para>
    /// </remarks>
    /// <remarks>
    /// <b>One arm per test case, because <c>TestBase</c> resolves ONE engine per test.</b> The obvious shape — loop the switch inside one test — throws
    /// <c>ConfigureSpatialGrid must be called before InitializeArchetypes</c> on the second iteration, which is the same trap
    /// <see cref="Cost_ParallelFence"/>'s remarks already record. NUnit's combinatorial expansion orders the cases so the two arms of a worker count are
    /// adjacent, and the fixture is <c>[NonParallelizable]</c>, so they run back to back on the same machine state — which is the pairing the comparison
    /// needs. Run the filter more than once for more pairs.
    /// </remarks>
    /// <summary>
    /// #926 A/B — the zone map's grow latch, taken once per migrant per indexed field against once per field per slice.
    /// </summary>
    /// <remarks>
    /// <para><c>ZoneMapArray.Widen</c> takes an archetype-wide padded word SHARED and releases it, so the pre-#926 Migrate loop issued two locked
    /// read-modify-writes into ONE cache line for every migrated entity. A shared acquire still takes the line exclusive, so this is MD-03's shape with a
    /// single word standing in for the bit-packed array — padding cannot help, because there is only one word. The batched form holds it once per field for
    /// the whole slice.</para>
    /// <para>The prize was sized by ablating the latch away entirely (−44 % CPU, −42 % wall at W = 8); that ablation was unsound and reverted, so this arm
    /// measures the sound fix against the same baseline.</para>
    /// <para><b>Interleaved, in one binary, alternating</b>, and one arm per test case for the same reason
    /// <see cref="Cost_DeferredClusterFlags_AB"/> records: <c>TestBase</c> resolves ONE engine per test, so looping the switch inside one test throws
    /// <c>ConfigureSpatialGrid must be called before InitializeArchetypes</c> on the second iteration.</para>
    /// </remarks>
    [Test]
    [CancelAfter(900_000)]
    public void Cost_ZoneMapBatch_AB([Values(2, 8)] int workerCount, [Values(false, true)] bool batched)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#926 A/B — zone-map grow latch per migrant vs per slice, W={workerCount}, batched={batched}");
        var original = ArchetypeClusterState.BatchMigrateZoneMaps;
        try
        {
            ArchetypeClusterState.BatchMigrateZoneMaps = batched;
            sb.AppendLine(MeasureParallel(workerCount, false, $"W={workerCount} {(batched ? "BATCHED" : "per-migrant")}"));
        }
        finally
        {
            ArchetypeClusterState.BatchMigrateZoneMaps = original;
        }

        Assert.Pass(sb.ToString());
    }

    /// <summary>
    /// #926 AC-4 — the serial fence must not pay for the batching. One writer, so there is no contention to remove and nothing may be added.
    /// </summary>
    [Test]
    [CancelAfter(900_000)]
    public void Cost_SerialZoneMapBatch_AB([Values(false, true)] bool batched)
    {
        var original = ArchetypeClusterState.BatchMigrateZoneMaps;
        try
        {
            ArchetypeClusterState.BatchMigrateZoneMaps = batched;
            RunSerial(RelocationJitter, $"#926 serial A/B — batched={batched}");
        }
        finally
        {
            // RunSerial ends in Assert.Pass, which THROWS — so the restore has to be in a finally, or the next case inherits this one's switch.
            ArchetypeClusterState.BatchMigrateZoneMaps = original;
        }
    }

    [Test]
    [CancelAfter(900_000)]
    public void Cost_DeferredClusterFlags_AB([Values(2, 8)] int workerCount, [Values(false, true)] bool deferred)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#912 A/B — per-migrant source-cluster flag atomics, W={workerCount}, deferred={deferred}");
        var original = ArchetypeClusterState.DeferMigrateClusterFlags;
        try
        {
            ArchetypeClusterState.DeferMigrateClusterFlags = deferred;
            sb.AppendLine(MeasureParallel(workerCount, false, $"W={workerCount} {(deferred ? "DEFERRED" : "inline")}"));
        }
        finally
        {
            ArchetypeClusterState.DeferMigrateClusterFlags = original;
        }

        Assert.Pass(sb.ToString());
    }

    private string MeasureParallel(int workerCount, bool crossCells, string label, int cellCount = CellCount, int perCell = EntitiesPerCell)
    {
        var dbe = SetupEngine();
        SpawnPopulation(dbe, cellCount, perCell);
        dbe.WriteTickFence(2);

        var cs = ClusterStateOf(dbe);
        var samples = new List<SpatialMigrationTelemetry>();
        var parallelisms = new List<double>();
        // #912. Per-PHASE span, CPU and chunk count, summed over the measured ticks. The engine-wide parallelism figure covers Migrate, IndexMassUpdate and
        // EntityMapUpdate together, so a single badly-scaling sibling drags it down and reads as though all three had. Six rows say which.
        var phaseSpan = new long[PhaseNames.Length];
        var phaseCpu = new long[PhaseNames.Length];
        var phaseChunks = new long[PhaseNames.Length];
        var phaseUnits = new long[PhaseNames.Length];
        // Assigned after Create so the callback can reach the runtime that owns it; the callback cannot run before Start.
        var runtimeRef = new TyphonRuntime[1];
        var ticks = 0;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Cost").CallbackSystem("Jitter", _ =>
                   {
                       // Sampled BEFORE this tick's jitter, so it describes the previous tick's completed fence — the same
                       // ordering the serial arm gets for free by reading after WriteTickFence returns.
                       int n = Interlocked.Increment(ref ticks);
                       if (n > 2)
                       {
                           // Locked, like the sibling fixture does for the same pattern. A callback system is not guaranteed
                           // to be the only writer, and an unsynchronised List<T> that grows under two threads loses entries
                           // or throws — in a measurement fixture that would silently change the denominator.
                           lock (samples)
                           {
                               samples.Add(dbe.GetSpatialTelemetry(ArchetypeId));
                               parallelisms.Add(dbe.LastFenceMigrationParallelism);
                               AccumulatePhaseStats(runtimeRef[0], phaseSpan, phaseCpu, phaseChunks, phaseUnits);
                           }
                       }

                       if (crossCells)
                       {
                           WalkAcrossCells(dbe, n, cellCount);
                       }
                       else
                       {
                           JitterEveryEntity(dbe, n, RelocationJitter);
                       }
                   });
               }, new RuntimeOptions
               {
                   WorkerCount = workerCount,
                   // 100 Hz. A rate the fence cannot keep up with makes ticks overlap, and the figures then describe an
                   // overrunning engine rather than the work being measured (AC-4's finding).
                   BaseTickRate = 100,
                   EnableParallelFence = true,
               }))
        {
            runtimeRef[0] = runtime;
            Exception unhandled = null;
            runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);

            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= MeasuredTicks + 2, TimeSpan.FromSeconds(120));
            runtime.Shutdown();

            Assert.That(unhandled, Is.Null, $"the parallel fence threw while being measured: {unhandled}");
        }

        var totals = default(Totals);
        lock (samples)
        {
            for (var i = 0; i < samples.Count; i++)
            {
                // No wall-clock term from OUTSIDE: the fence runs on the tick thread's own schedule here, so there is nothing to time around it, and the
                // report suppresses the detection column rather than printing a zero that would read as a measured bound. The engine's own migration-phase
                // parallelism is the wall-clock counterpart it does have, and it is what turns a summed-CPU figure back into a latency (#912).
                totals = totals.Add(samples[i], 0d, parallelisms[i]);
            }
        }

        var sb = new StringBuilder();
        totals.Report(sb, label, cs.Layout.ClusterSize);
        ReportPhases(sb, phaseSpan, phaseCpu, phaseChunks, phaseUnits, workerCount);
        dbe.Dispose();
        return sb.ToString().TrimEnd();
    }

    /// <summary>The fence's six phases, in dispatch order — the row labels of the per-phase scaling table.</summary>
    private static readonly string[] PhaseNames = ["Prep", "Migrate", "IndexMassUpdate", "EntityMapUpdate", "AabbRefresh", "Finalize"];

    /// <summary>Folds one completed tick's per-phase span, CPU and chunk count into the run totals.</summary>
    /// <remarks>
    /// <b>Span and CPU are different quantities and the naming does not say so.</b> <c>PhaseSpanTicks</c> is elapsed — start of the phase to the end of its
    /// last chunk. <c>TotalWallTicks</c> says "wall" and is a SUM across chunks, so it is CPU. Their ratio is the parallelism that phase achieved, and it is
    /// the only one of the three that answers "did giving it more workers help".
    /// </remarks>
    private static void AccumulatePhaseStats(TyphonRuntime runtime, long[] span, long[] cpu, long[] chunks, long[] units)
    {
        if (runtime == null)
        {
            return;
        }

        Fold(0, runtime.LastPrepStats);
        Fold(1, runtime.LastMigrateStats);
        Fold(2, runtime.LastIndexMassUpdateStats);
        Fold(3, runtime.LastEntityMapUpdateStats);
        Fold(4, runtime.LastAabbRefreshStats);
        Fold(5, runtime.LastFinalizeStats);
        return;

        void Fold(int i, (long SpanTicks, long CpuTicks, long Units, int Chunks) stats)
        {
            span[i] += stats.SpanTicks;
            cpu[i] += stats.CpuTicks;
            chunks[i] += stats.Chunks;
            units[i] += stats.Units;
        }
    }

    /// <summary>Prints the per-phase scaling table: where the fence's span goes and which phases the workers actually helped.</summary>
    /// <remarks>
    /// <b>`chunks/tick` is the row to read next to the parallelism.</b> A phase whose plan produced fewer chunks than there are workers cannot reach the
    /// worker count however cheap its work is, and the shortfall is the PLAN rather than contention. For the Migrate phase the binding constraint is the
    /// destination-cell rule: a slice may not split a cell, so a workload on N cells yields at most N chunks.
    /// </remarks>
    private static void ReportPhases(StringBuilder sb, long[] span, long[] cpu, long[] chunks, long[] units, int workerCount)
    {
        var totalSpan = 0L;
        for (var i = 0; i < span.Length; i++)
        {
            totalSpan += span[i];
        }

        if (totalSpan == 0)
        {
            return;
        }

        // `ns/unit` is the column that separates the two losses. Parallelism says how well the phase SPREAD its work; ns/unit says whether the work itself
        // got more expensive when several workers did it at once. A phase whose parallelism is good and whose ns/unit rises with W is paying for the
        // concurrency in cache coherence, and no amount of better scheduling recovers it.
        sb.AppendLine($"  {"",-22} per-phase (W={workerCount}):  phase            span%    parallelism   chunks/tick    ns/unit");
        for (var i = 0; i < PhaseNames.Length; i++)
        {
            if (span[i] == 0 && cpu[i] == 0)
            {
                continue;
            }

            var par = span[i] > 0 ? cpu[i] / (double)span[i] : 0d;
            var nsPerUnit = units[i] > 0 ? cpu[i] * 1e9 / Stopwatch.Frequency / units[i] : 0d;
            sb.AppendLine($"  {"",-22}                        {PhaseNames[i],-16} {100.0 * span[i] / totalSpan,5:F1}   "
                + $"{par,8:F2}x      {chunks[i] / (double)MeasuredTicks,8:F1}   {nsPerUnit,9:F1}");
        }
    }
}
