using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// What cell size costs and what it buys — the measurement that decides whether a per-cell R-Tree is needed at all (#872 step 9).
/// </summary>
/// <remarks>
/// <para><b>The question this answers.</b> Clusters-per-cell is not a property of a world; it is the world <b>times the cell size chosen for it</b>. Halving the
/// cell edge divides the population of each cell by four in 2D. So the density at which a tree starts to win is reachable from either direction, and "should we
/// add a second index inside the cell?" is really "is anything forcing us into cells big enough to need one?". If shrinking cells is cheap, tuning the grid is
/// a smaller change than a second index with its own maintenance path, hysteresis margin and threshold.</para>
/// <para><b>What is actually swept, and why it is not free.</b> <see cref="AabbClusterEnumerator"/> walks EVERY integer cell coordinate in the query's
/// bounding box, occupied or not, calling <c>TryGetCellKey</c> on each. So the sweep cost grows as the query area divided by the cell area — quadratically as
/// cells shrink in 2D — while the in-cell scan shrinks linearly in the cell's population. Those two curves cross somewhere, and where they cross is the whole
/// answer.</para>
/// <para><b>Memory is reported two ways on purpose.</b> The grid self-reports <see cref="SpatialGrid.ResidentBytes"/>, but that deliberately excludes the
/// per-archetype pools — and those are where small cells actually hurt: a <see cref="CellSpatialIndex"/> allocates eight arrays at a floor of
/// <see cref="CellSpatialIndex.DefaultInitialCapacity"/> entries whether the cell holds one cluster or sixteen. A cell containing a single cluster still pays
/// the full eight-array floor, so halving the cell edge can quadruple index memory while the entity count stays flat. The computed figure is cross-checked
/// against a real managed-heap delta, because a computed number that nobody validated is a model, not a measurement.</para>
/// <para>Run deliberately in Release:</para>
/// <code>
/// dotnet test test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj -c Release --filter "FullyQualifiedName~GridCellSizeSweepTests"
/// </code>
/// </remarks>
[TestFixture]
[Explicit("Measurement instrument — run deliberately in Release, not as part of the suite.")]
// Manual, not Nightly: these are wall-clock timings whose point is to be compared against each other, and a contended CI runner moves them by more than the
// effect being measured. It also spawns 700k entities. Re-run it when the grid's cell resolution or the per-cell index layout changes.
[Category("Manual")]
class GridCellSizeSweepTests : TestBase<GridCellSizeSweepTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    /// <summary>World edge in world units. Fixed across the sweep — only the cell size moves, so cells-per-axis is the dependent variable.</summary>
    private const float WorldExtent = 10_000f;

    /// <summary>Entities spawned into that world, uniformly. 100k over a 10k square puts ~1563 clusters in a single cell at the coarsest setting.</summary>
    private const int EntityCount = 100_000;

    private static ClCohPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Mass = 1.0f };

    /// <summary>
    /// Bytes the per-archetype cell indexes hold. Counted from the live arrays rather than assumed, because the capacity is a doubling growth from a floor and
    /// the floor is what dominates once cells are small.
    /// </summary>
    private static (long bytes, int occupiedSlots, long clusters) MeasureIndexBytes(ArchetypeClusterState cs) =>
        MeasureIndexBytes(cs, null);

    private static (long bytes, int occupiedSlots, long clusters) MeasureIndexBytes(ArchetypeClusterState cs, List<int> perCellCounts)
    {
        if (cs.PerCellIndex == null)
        {
            return (0, 0, 0);
        }

        // The PerCellIndex array itself: one reference per cell key the grid has ever handed out.
        long bytes = 24 + ((long)cs.PerCellIndex.Length * 8);
        int occupied = 0;
        long clusters = 0;

        for (int i = 0; i < cs.PerCellIndex.Length; i++)
        {
            var slot = cs.PerCellIndex[i];
            if (slot == null)
            {
                continue;
            }

            occupied++;
            bytes += 32; // PerCellSpatialSlot: object header plus two references.
            int cellClusters = 0;
            foreach (var idx in (CellSpatialIndex[])[slot.DynamicIndex, slot.StaticIndex])
            {
                if (idx == null)
                {
                    continue;
                }
                // Eight parallel arrays, all 4-byte elements, each with its own object header.
                bytes += 8L * (24 + ((long)idx.Capacity * 4));
                cellClusters += idx.ClusterCount;
            }
            clusters += cellClusters;
            perCellCounts?.Add(cellClusters);
        }

        return (bytes, occupied, clusters);
    }

    private static double TimeQueryNs(Func<int> body, out int hits)
    {
        hits = 0;
        for (int i = 0; i < 8; i++) { hits = body(); }

        int reps = 16;
        while (true)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < reps; i++) { hits = body(); }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms >= 80 || reps >= (1 << 18)) { break; }
            reps *= 4;
        }

        double best = double.MaxValue;
        for (int pass = 0; pass < 3; pass++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < reps; i++) { hits = body(); }
            double ns = (Stopwatch.GetTimestamp() - t0) * 1_000_000_000.0 / Stopwatch.Frequency / reps;
            if (ns < best) { best = ns; }
        }
        return best;
    }

    [Test]
    [CancelAfter(1_800_000)]
    // 100k entities per configuration will not fit the fixture default of 8 MiB — the checkpoint cycle hits page-cache back-pressure and the run dies partway
    // through the second cell size. The cache size is not what is being measured here, so it is raised out of the way rather than trimming the population,
    // which would cost the coarse end of the sweep exactly the density the measurement exists to reach.
    [Property("CacheSize", 256 * 1024 * 1024)]
    public void CellSize_MemoryAndSweepCost_AgainstInCellScan()
    {
        float[] cellSizes = [10_000f, 2_500f, 1_000f, 500f, 250f, 100f, 50f];
        float[] queryEdges = [200f, 2_000f];

        TestContext.Out.WriteLine($"GRIDSWEEP world {WorldExtent:F0} square, {EntityCount} entities uniform, {EntityCount / 64} clusters total");
        TestContext.Out.WriteLine($"GRIDSWEEP {"cell",7} {"cells",8} {"clu/cell",9} {"gridKiB",9} {"idxKiB",10} {"heapKiB",10} {"totKiB",10}");

        int baselineHits = -1;
        foreach (float cellSize in cellSizes)
        {
            // A fresh DATABASE per configuration, not merely a fresh scope. Every scope opens the same file under the fixture's database name, so a second
            // engine loads the first one's entities and spawns another 100k on top: the population, the cluster counts and the memory all compound, and the
            // query hit count climbs by a constant amount per configuration. That is what the first run of this fixture actually measured. The previous
            // iteration's scope has been disposed by the time control reaches here, so the file is closed and deletable.
            ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();

            using var scope = ServiceProvider.CreateScope();

            long heapBefore = GC.GetTotalMemory(true);

            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<ClCohPos>();
            dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldExtent, WorldExtent), cellSize));
            dbe.InitializeArchetypes();

            var rng = new Random(20260902);
            // Spawned in batches: one transaction for 100k entities builds a write set big enough to distort what is being measured here.
            const int BatchSize = 5_000;
            for (int spawned = 0; spawned < EntityCount; spawned += BatchSize)
            {
                using var tx = dbe.CreateQuickTransaction();
                for (int i = 0; i < BatchSize; i++)
                {
                    float x = (float)rng.NextDouble() * WorldExtent;
                    float y = (float)rng.NextDouble() * WorldExtent;
                    tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y)));
                }
                tx.Commit();
            }
            dbe.WriteTickFence(1);

            var grid = dbe.SpatialGrid;
            var cs = dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;
            var (idxBytes, occupiedSlots, clusters) = MeasureIndexBytes(cs);
            long heapAfter = GC.GetTotalMemory(true);

            double cluPerCell = occupiedSlots == 0 ? 0 : (double)clusters / occupiedSlots;
            TestContext.Out.WriteLine($"GRIDSWEEP {cellSize,7:F0} {grid.CellCount,8} {cluPerCell,9:F1} {grid.ResidentBytes / 1024.0,9:F0} "
                + $"{idxBytes / 1024.0,10:F0} {(heapAfter - heapBefore) / 1024.0,10:F0} {(grid.ResidentBytes + idxBytes) / 1024.0,10:F0}");

            foreach (float qEdge in queryEdges)
            {
                float qMin = (WorldExtent - qEdge) * 0.5f;
                float qMax = qMin + qEdge;

                // Cells the enumerator walks: every integer coordinate the box spans, occupied or not.
                int cellsPerAxis = ((int)(qMax / cellSize)) - ((int)(qMin / cellSize)) + 1;
                long cellsSwept = (long)cellsPerAxis * cellsPerAxis;

                double ns = TimeQueryNs(() =>
                {
                    int n = 0;
                    foreach (var _ in cs.QueryAabb(grid, qMin, qMin, float.NegativeInfinity, qMax, qMax, float.PositiveInfinity))
                    {
                        n++;
                    }
                    return n;
                }, out int hits);

                TestContext.Out.WriteLine($"GRIDQUERY {cellSize,7:F0}  qEdge {qEdge,6:F0}  cellsSwept {cellsSwept,7}  hits {hits,6}  "
                    + $"{ns / 1000.0,10:F2} us  {(cellsSwept == 0 ? 0 : ns / cellsSwept),7:F1} ns/cell");

                // The answer to a fixed world query cannot depend on how the world was partitioned — same entities, same box, same result. Asserting it makes
                // the sweep self-checking: it is what turns a silently accumulating population into a failure rather than a plausible-looking table.
                if (Math.Abs(qEdge - queryEdges[0]) < 0.001f)
                {
                    if (baselineHits < 0)
                    {
                        baselineHits = hits;
                    }
                    else
                    {
                        // EXACT, not within 10%. The population is identical (fixed seed, fixed count) and QueryAabb is entity-exact, so a tolerance here
                        // would let a sweep that silently dropped up to a tenth of its cells pass — and this is the fixture's only self-check.
                        Assert.That(hits, Is.EqualTo(baselineHits),
                            $"cell size {cellSize} returned {hits} for the same query that returned {baselineHits} at the coarsest setting — the result set "
                            + "must not depend on cell size, so either the population differs between configurations or the sweep is dropping cells");
                    }
                }
            }

            TestContext.Out.WriteLine("");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Clumped worlds — the case the uniform sweep could not speak for
    // ═══════════════════════════════════════════════════════════════════════

    private static float _hotspotCentreX;
    private static float _hotspotCentreY;

    /// <summary>
    /// Positions for a clumped world: <paramref name="hotFraction"/> of the population packed into <paramref name="hotspots"/> discs of
    /// <paramref name="hotRadius"/>, the remainder spread uniformly as background.
    /// </summary>
    /// <remarks>
    /// Uniform-in-disc, not linear-in-radius: <c>r = R * sqrt(u)</c> rather than <c>r = R * u</c>. The naive form concentrates points towards the centre and
    /// would exaggerate the very tail this measurement exists to read honestly — the density reported has to come from the clumping being modelled, not from an
    /// artefact of how a radius was sampled.
    /// </remarks>
    private static (float[] xs, float[] ys) MakeClumped(int count, int hotspots, float hotRadius, double hotFraction, int seed)
    {
        var rng = new Random(seed);
        var xs = new float[count];
        var ys = new float[count];

        var hx = new float[hotspots];
        var hy = new float[hotspots];
        for (int h = 0; h < hotspots; h++)
        {
            hx[h] = hotRadius + ((float)rng.NextDouble() * (WorldExtent - (2 * hotRadius)));
            hy[h] = hotRadius + ((float)rng.NextDouble() * (WorldExtent - (2 * hotRadius)));
        }

        for (int i = 0; i < count; i++)
        {
            if (rng.NextDouble() < hotFraction)
            {
                int h = rng.Next(hotspots);
                double theta = rng.NextDouble() * Math.PI * 2;
                double r = hotRadius * Math.Sqrt(rng.NextDouble());
                xs[i] = Math.Clamp(hx[h] + (float)(r * Math.Cos(theta)), 0f, WorldExtent);
                ys[i] = Math.Clamp(hy[h] + (float)(r * Math.Sin(theta)), 0f, WorldExtent);
            }
            else
            {
                xs[i] = (float)rng.NextDouble() * WorldExtent;
                ys[i] = (float)rng.NextDouble() * WorldExtent;
            }
        }

        _hotspotCentreX = hx[0];
        _hotspotCentreY = hy[0];
        return (xs, ys);
    }

    private DatabaseEngine BuildEngine(IServiceScope scope, float cellSize, float[] xs, float[] ys)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldExtent, WorldExtent), cellSize));
        dbe.InitializeArchetypes();

        const int BatchSize = 5_000;
        for (int spawned = 0; spawned < xs.Length; spawned += BatchSize)
        {
            using var tx = dbe.CreateQuickTransaction();
            int end = Math.Min(spawned + BatchSize, xs.Length);
            for (int i = spawned; i < end; i++)
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(xs[i], ys[i])));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);
        return dbe;
    }

    private static (int max, int p99, int p90, double mean) Tail(List<int> counts)
    {
        if (counts.Count == 0)
        {
            return (0, 0, 0, 0);
        }
        counts.Sort();
        double sum = 0;
        for (int i = 0; i < counts.Count; i++)
        {
            sum += counts[i];
        }
        return (counts[^1], counts[(int)(counts.Count * 0.99)], counts[(int)(counts.Count * 0.90)], sum / counts.Count);
    }

    private static double TimeBox(ArchetypeClusterState cs, SpatialGrid grid, float minX, float minY, float maxX, float maxY, out int hits) =>
        TimeQueryNs(() =>
        {
            int n = 0;
            foreach (var _ in cs.QueryAabb(grid, minX, minY, float.NegativeInfinity, maxX, maxY, float.PositiveInfinity))
            {
                n++;
            }
            return n;
        }, out hits);

    /// <summary>
    /// The cell-size sweep again, but on a clumped world, reading the <b>tail</b> of the clusters-per-cell distribution rather than its mean.
    /// </summary>
    /// <remarks>
    /// <para>The uniform sweep concluded that cell size beats a per-cell tree by two to three orders of magnitude, and that at any sane cell size a cell holds
    /// 1-18 clusters against the tree's ~512 crossover. That conclusion is only as good as the distribution it was measured on, and a uniform world is the one
    /// shape that guarantees no cell is an outlier. Real worlds clump, and the whole remaining case for the tree is that a handful of cells hold orders of
    /// magnitude more than the mean while the grid stays tuned for the common case.</para>
    /// <para>So what is reported is <c>MAX</c> and <c>p99</c>, not the average. The question is not "how many clusters does a typical cell hold" — the uniform
    /// run answered that. It is "how many does the WORST cell hold, at the cell size the rest of the world wants".</para>
    /// </remarks>
    [Test]
    [CancelAfter(1_800_000)]
    [Property("CacheSize", 256 * 1024 * 1024)]
    public void Clumped_ClusterCountTail_AcrossCellSize()
    {
        float[] cellSizes = [2_500f, 1_000f, 500f, 250f, 100f];
        const int Hotspots = 4;
        const float HotRadius = 250f;
        const double HotFraction = 0.70;

        var (xs, ys) = MakeClumped(EntityCount, Hotspots, HotRadius, HotFraction, 20260902);
        float hotX = _hotspotCentreX;
        float hotY = _hotspotCentreY;

        TestContext.Out.WriteLine($"CLUMP world {WorldExtent:F0} sq, {EntityCount} entities, {HotFraction:P0} in {Hotspots} discs r={HotRadius:F0}");
        TestContext.Out.WriteLine($"CLUMP {"cell",7} {"cells",7} {"mean",7} {"p90",6} {"p99",6} {"MAX",7} {"totKiB",9} {"hot q",10} {"cold q",10}");

        foreach (float cellSize in cellSizes)
        {
            ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
            using var scope = ServiceProvider.CreateScope();
            using var dbe = BuildEngine(scope, cellSize, xs, ys);

            var grid = dbe.SpatialGrid;
            var cs = dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;
            var counts = new List<int>();
            var (idxBytes, _, _) = MeasureIndexBytes(cs, counts);
            var (max, p99, p90, mean) = Tail(counts);

            // A 200-unit box on the hotspot, and the same box on empty background — the two extremes a clumped world actually serves.
            double hotNs = TimeBox(cs, grid, hotX - 100f, hotY - 100f, hotX + 100f, hotY + 100f, out int hotHits);
            double coldNs = TimeBox(cs, grid, 100f, 100f, 300f, 300f, out int coldHits);

            TestContext.Out.WriteLine($"CLUMP {cellSize,7:F0} {grid.CellCount,7} {mean,7:F1} {p90,6} {p99,6} {max,7} "
                + $"{(grid.ResidentBytes + idxBytes) / 1024.0,9:F0} {hotNs / 1000.0,8:F1}us {coldNs / 1000.0,8:F2}us  hits {hotHits}/{coldHits}");
        }
    }

    /// <summary>
    /// How concentrated a world has to be before the worst cell reaches the tree's crossover, at a cell size the rest of the world is happy with.
    /// </summary>
    /// <remarks>
    /// The tree needs roughly 512 clusters — about 33 000 entities — inside ONE cell before it starts to pay on a selective query. This sweep tightens the
    /// hotspots until that happens, if it happens, and reports the concentration required. A figure that turns out to demand an implausible crowd is as useful
    /// an answer as one that does not: it would say the tree's regime is not reachable by clumping at all, only by mis-sizing the grid.
    /// </remarks>
    [Test]
    [CancelAfter(1_800_000)]
    [Property("CacheSize", 256 * 1024 * 1024)]
    public void Clumped_HowTightBeforeTheTreeMatters()
    {
        float[] radii = [1_000f, 500f, 250f, 125f, 60f, 30f];
        const float CellSize = 250f;
        const int Hotspots = 2;
        const double HotFraction = 0.90;

        TestContext.Out.WriteLine($"TIGHT cell {CellSize:F0}, {EntityCount} entities, {HotFraction:P0} in {Hotspots} discs — sweeping disc radius");
        TestContext.Out.WriteLine($"TIGHT {"radius",7} {"mean",7} {"p99",7} {"MAX",7} {"totKiB",9} {"hot q",10}  verdict at the worst cell");

        foreach (float radius in radii)
        {
            var (xs, ys) = MakeClumped(EntityCount, Hotspots, radius, HotFraction, 20260902);
            float hotX = _hotspotCentreX;
            float hotY = _hotspotCentreY;

            ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
            using var scope = ServiceProvider.CreateScope();
            using var dbe = BuildEngine(scope, CellSize, xs, ys);

            var grid = dbe.SpatialGrid;
            var cs = dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;
            var counts = new List<int>();
            var (idxBytes, _, _) = MeasureIndexBytes(cs, counts);
            var (max, p99, _, mean) = Tail(counts);

            double hotNs = TimeBox(cs, grid, hotX - 100f, hotY - 100f, hotX + 100f, hotY + 100f, out int hotHits);

            // A TINY box into the same hot cell. This is the one query shape where a broadphase should decide everything: the answer is a handful of entities,
            // so anything the query spends beyond reading them is the index failing to discriminate. If it costs the same as the 200-unit box that returns
            // thousands, the cost is not the broadphase at all — it is that the clusters themselves are not spatially coherent, so nearly every one of them
            // overlaps any query and the narrowphase opens the whole cell regardless.
            double tinyNs = TimeBox(cs, grid, hotX - 10f, hotY - 10f, hotX + 10f, hotY + 10f, out int tinyHits);

            // Read against the measured crossover: the tree overtakes the linear scan at ~512 clusters for a selective query, and loses below it.
            string verdict = max >= 512 ? "TREE WINS here" : max >= 200 ? "borderline" : "linear scan wins";
            TestContext.Out.WriteLine($"TIGHT {radius,7:F0} {mean,7:F1} {p99,7} {max,7} "
                + $"{(grid.ResidentBytes + idxBytes) / 1024.0,9:F0} {hotNs / 1000.0,8:F1}us  {verdict} (hits={hotHits})");
            TestContext.Out.WriteLine($"TINY  {radius,7:F0} {tinyNs / 1000.0,10:F1}us for {tinyHits,6} hits   "
                + $"{(tinyHits == 0 ? 0 : tinyNs / tinyHits),8:F0} ns/hit   vs 200-unit box {hotNs / 1000.0,8:F1}us for {hotHits} hits");
        }
    }
}
