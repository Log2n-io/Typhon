using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The VDB cell grid (#872 step 8): sparse storage, lazy creation, block-local neighbours. Acceptance criteria 8.1 to 8.7.
/// </summary>
/// <remarks>
/// <c>SpatialGridTests</c> covers the coordinate space — clamping, key round-trips, field decoding — and passes unchanged against both the dense grid and
/// this one, which is the point: the storage swap must not move the partition. What is here is everything only the sparse structure can get wrong.
/// </remarks>
[TestFixture]
class VdbSpatialGridTests
{
    private static SpatialGridConfig Flat100 => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(1000, 1000), cellSize: 100f);

    private static SpatialGridConfig Cubic100 => new(new Vector3(0, 0, 0), new Vector3(1000, 1000, 1000), cellSize: 100f);

    /// <summary>
    /// 64 cells per axis at the default 16-cell block extent, i.e. four blocks per axis. Any test about a BLOCK boundary needs a world several blocks wide;
    /// at 10 cells per axis the whole world is one block and there is no interior boundary to cross.
    /// </summary>
    private static SpatialGridConfig MultiBlock => new(new Vector3(0, 0, 0), new Vector3(6400, 6400, 6400), cellSize: 100f);

    // ═══════════════════════════════════════════════════════════════════════
    // AC-8.1 — differential against the dense oracle
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void AC81_RandomPopulation_ResolvesToTheSameCellsAsTheDenseOracle()
    {
        // The SQ-01 guard. Keys are not comparable — the VDB's key is a pool slot, the oracle's is a dense index — so the comparison is on COORDINATES and
        // on the induced partition: two points share a cell in one implementation exactly when they share one in the other. That equivalence is what a
        // query's correctness rests on, and it is stronger than comparing keys would be.
        var cfg = Cubic100;
        var vdb = new SpatialGrid(cfg);
        var dense = new DenseSpatialGridReference(cfg);
        var rng = new Random(20260902);

        var byVdbKey = new Dictionary<int, List<int>>();
        var byDenseKey = new Dictionary<int, List<int>>();

        for (int i = 0; i < 4000; i++)
        {
            // Deliberately overshoots the world on every axis: out-of-bounds points must clamp identically, and clamping is where two implementations of
            // "which cell is this" most easily disagree.
            float x = (float)(rng.NextDouble() * 1400 - 200);
            float y = (float)(rng.NextDouble() * 1400 - 200);
            float z = (float)(rng.NextDouble() * 1400 - 200);

            int vdbKey = vdb.WorldToCellKey(x, y, z);
            var expected = dense.CellOfPoint(x, y, z);
            dense.Occupy(expected.x, expected.y, expected.z);

            Assert.That(vdb.CellKeyToCoords(vdbKey), Is.EqualTo(expected), $"point ({x}, {y}, {z}) resolved to a different cell");

            byVdbKey.TryAdd(vdbKey, []);
            byVdbKey[vdbKey].Add(i);
            int denseKey = dense.KeyOf(expected.x, expected.y, expected.z);
            byDenseKey.TryAdd(denseKey, []);
            byDenseKey[denseKey].Add(i);
        }

        Assert.That(vdb.CellCount, Is.EqualTo(dense.OccupiedCount), "the two must have found the same number of distinct cells");

        var vdbGroups = byVdbKey.Values.Select(g => string.Join(",", g)).OrderBy(s => s).ToList();
        var denseGroups = byDenseKey.Values.Select(g => string.Join(",", g)).OrderBy(s => s).ToList();
        Assert.That(vdbGroups, Is.EqualTo(denseGroups), "the two must induce the same partition of the population");
    }

    [Test]
    [CancelAfter(15_000)]
    public void AC81_CellAndBlockBoundaryStraddlers_ResolveIdentically()
    {
        // Named explicitly by AC-8.1. A block boundary is invisible to the oracle and is exactly where the VDB's `>> logBlock` / `& (dim-1)` split can be off
        // by one, so the interesting coordinates are the ones adjacent to a multiple of the block extent as well as of the cell size.
        var cfg = MultiBlock;
        var vdb = new SpatialGrid(cfg);
        var dense = new DenseSpatialGridReference(cfg);
        var (bx, _, _) = vdb.BlockDimensions;
        Assert.That(cfg.GridWidth / bx, Is.GreaterThan(1), "the world must span several blocks, or there is no interior block boundary to straddle");

        var offsets = new[] { -0.001f, 0f, 0.001f, 49.999f, 50f };
        foreach (int cellIndex in new[] { 0, 1, bx - 1, bx, bx + 1, (2 * bx) - 1, 2 * bx, cfg.GridWidth - 1 })
        {
            foreach (float delta in offsets)
            {
                float w = (float)((cellIndex * cfg.CellSize) + delta);
                int key = vdb.WorldToCellKey(w, w, w);
                var expected = dense.CellOfPoint(w, w, w);
                Assert.That(vdb.CellKeyToCoords(key), Is.EqualTo(expected), $"world {w} (cell index {cellIndex}, delta {delta})");
            }
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void AC81_NeighbourSetsMatchTheOracle()
    {
        // MultiBlock, not Cubic100: a 10-cell axis at a 16-cell block extent is ONE block, so on Cubic100 every neighbour step stays in-block and the test
        // would pass against an implementation that could not cross a block face at all — the single thing this differential exists to check.
        var cfg = MultiBlock;
        var vdb = new SpatialGrid(cfg);
        var dense = new DenseSpatialGridReference(cfg);
        var rng = new Random(4242);

        var seeded = new List<(int x, int y, int z)>();
        for (int i = 0; i < 400; i++)
        {
            int cx = rng.Next(cfg.GridWidth);
            int cy = rng.Next(cfg.GridHeight);
            int cz = rng.Next(cfg.GridDepth);
            vdb.ComputeCellKey(cx, cy, cz);
            dense.Occupy(cx, cy, cz);
            seeded.Add((cx, cy, cz));
        }

        foreach (var (cx, cy, cz) in seeded)
        {
            int key = vdb.ComputeCellKey(cx, cy, cz);
            var actual = new List<(int, int, int)>();
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) { continue; }
                        if (vdb.TryGetNeighbourCellKey(key, dx, dy, dz, out int n)) { actual.Add(vdb.CellKeyToCoords(n)); }
                    }
                }
            }

            Assert.That(actual, Is.EqualTo(dense.OccupiedNeighbours(cx, cy, cz)), $"neighbourhood of ({cx}, {cy}, {cz})");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // AC-8.2 — a neighbour in an absent block
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("VG-01")]
    public void AC82_NeighbourAcrossAnAbsentBlockBoundary_IsAbsentThenAppears()
    {
        // The silent-false-negative case §3.3 warns about: "a cached or assumed NEGATIVE that later becomes a real cell". Both halves are asserted, in
        // order, on the SAME grid instance and the same thread — because the per-thread last-block cache is precisely what could make the second half keep
        // answering with the first half's "absent".
        var vdb = new SpatialGrid(MultiBlock);
        var (bx, _, _) = vdb.BlockDimensions;

        int edge = vdb.ComputeCellKey(bx - 1, 0, 0);
        Assert.That(vdb.TryGetNeighbourCellKey(edge, 1, 0, 0, out _), Is.False,
            "the +X neighbour lives in the next block, which no cell has created yet");

        int acrossTheBoundary = vdb.ComputeCellKey(bx, 0, 0);
        Assert.That(vdb.TryGetNeighbourCellKey(edge, 1, 0, 0, out int found), Is.True,
            "once that block exists the neighbour must be found — a remembered 'absent' here is an SQ-01 false negative");
        Assert.That(found, Is.EqualTo(acrossTheBoundary));
    }

    [Test]
    [CancelAfter(15_000)]
    public void AC82_NeighbourInsideTheSameBlock_DoesNotDependOnAnyLookup()
    {
        // C3's claim, stated as a test: within a block a neighbour is arithmetic. Both cells share a block, so the answer must be there immediately, and it
        // must be absent while the neighbour cell itself has not been created — "the block exists" is not "the cell exists".
        var vdb = new SpatialGrid(Cubic100);
        int origin = vdb.ComputeCellKey(2, 2, 2);

        Assert.That(vdb.TryGetNeighbourCellKey(origin, 1, 0, 0, out _), Is.False, "an uncreated cell inside a live block is still absent");

        int neighbour = vdb.ComputeCellKey(3, 2, 2);
        Assert.That(vdb.TryGetNeighbourCellKey(origin, 1, 0, 0, out int found), Is.True);
        Assert.That(found, Is.EqualTo(neighbour));
        Assert.That(vdb.BlockCount, Is.EqualTo(1), "both cells belong to one block — this test would prove nothing about in-block arithmetic otherwise");
    }

    [Test]
    [CancelAfter(15_000)]
    public void AC82_NeighbourOutsideTheWorld_IsAbsentRatherThanClamped()
    {
        // Clamping is right for a world POSITION and wrong for a neighbour step: clamping would report cell (0,y,z) as its own -X neighbour, and a caller
        // walking outward would loop.
        var vdb = new SpatialGrid(Cubic100);
        int corner = vdb.ComputeCellKey(0, 0, 0);
        Assert.That(vdb.TryGetNeighbourCellKey(corner, -1, 0, 0, out _), Is.False);
        Assert.That(vdb.TryGetNeighbourCellKey(corner, 0, -1, 0, out _), Is.False);
        Assert.That(vdb.TryGetNeighbourCellKey(corner, 0, 0, -1, out _), Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // AC-8.5 / AC-8.7 — memory and intra-block fill
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void AC85_MemoryAt80PercentEmpty_IsAFractionOfTheDenseBaseline()
    {
        // C2's whole argument, measured. A 40 x 40 x 40 world is 64 000 cells = 4 MB of dense CellState; occupying 20 % of it must cost a small fraction of
        // that. The assertion is deliberately loose — this is a report with a floor under it, not a tuned threshold.
        var cfg = new SpatialGridConfig(new Vector3(0, 0, 0), new Vector3(4000, 4000, 4000), cellSize: 100f);
        var vdb = new SpatialGrid(cfg);
        Assert.That(cfg.CellCount, Is.EqualTo(64_000));

        var rng = new Random(808);
        int target = cfg.CellCount / 5;

        // BOUNDED, not `while (CellCount < target)`. That loop's termination depends on the code under test: an ablation that collapses distinct cells onto
        // one key caps CellCount below the target and the test spins forever — and NUnit's CancelAfter cannot interrupt a tight CPU loop, so the whole run
        // hangs rather than failing. Found the hard way, by an ablation that stalled two 45-minute runs before it was diagnosed.
        for (int attempt = 0; attempt < cfg.CellCount * 8 && vdb.CellCount < target; attempt++)
        {
            vdb.ComputeCellKey(rng.Next(cfg.GridWidth), rng.Next(cfg.GridHeight), rng.Next(cfg.GridDepth));
        }

        Assert.That(vdb.CellCount, Is.GreaterThanOrEqualTo(target),
            $"only {vdb.CellCount} of {target} cells materialised — distinct coordinates are collapsing onto one cell");

        long dense = vdb.DenseEquivalentBytes;
        long sparse = vdb.ResidentBytes;
        TestContext.Out.WriteLine(
            $"AC-8.5 — 80 % empty, {cfg.GridWidth}x{cfg.GridHeight}x{cfg.GridDepth} cells, {vdb.CellCount} occupied: "
            + $"dense {dense / 1024.0:F0} KiB, VDB {sparse / 1024.0:F0} KiB ({(double)sparse / dense:P0}), "
            + $"{vdb.BlockCount} blocks of {vdb.BlockCellCapacity}, intra-block fill {vdb.IntraBlockFill:P1}");

        Assert.That(sparse, Is.LessThan(dense), "a fifth of the cells must not cost what all of them would");
    }

    [Test]
    [CancelAfter(15_000)]
    public void AC85_AnEmptyRegionCostsNothing()
    {
        // "The empty-region cost is one hash entry" (AC-8.5), stated as the thing that is observable: occupying one corner of a large world must not
        // allocate anything proportional to the world. This is also the guard that catches a read path resolving with CREATE by mistake.
        var cfg = new SpatialGridConfig(new Vector3(0, 0, 0), new Vector3(100_000, 100_000, 100_000), cellSize: 100f);
        var vdb = new SpatialGrid(cfg);
        Assert.That(cfg.CellCount, Is.EqualTo(1000 * 1000 * 1000), "a world the dense grid could never have held: 64 GB of descriptors");

        vdb.ComputeCellKey(0, 0, 0);
        vdb.ComputeCellKey(999, 999, 999);

        Assert.That(vdb.BlockCount, Is.EqualTo(2), "two far-apart cells occupy two blocks and nothing in between");
        Assert.That(vdb.ResidentBytes, Is.LessThan(200_000), $"got {vdb.ResidentBytes} bytes for two occupied cells");
    }

    [Test]
    [CancelAfter(15_000)]
    public void AC87_IntraBlockFill_IsInstrumented()
    {
        // Q3's measurement (P1/P2). Filling one block completely must report 100 %, and a single cell in a second block must drag the mean to the value the
        // arithmetic says — a fill number that does not move with occupancy would be a counter nobody can act on.
        var vdb = new SpatialGrid(MultiBlock);
        var (bx, by, bz) = vdb.BlockDimensions;

        for (int z = 0; z < bz; z++)
        {
            for (int y = 0; y < by; y++)
            {
                for (int x = 0; x < bx; x++)
                {
                    vdb.ComputeCellKey(x, y, z);
                }
            }
        }

        Assert.That(vdb.BlockCount, Is.EqualTo(1));
        Assert.That(vdb.BlockCellCapacity, Is.EqualTo(bx * by * bz));
        Assert.That(vdb.IntraBlockFill, Is.EqualTo(1.0).Within(1e-9), "a completely filled block is 100 % full");

        // A single cell in a SECOND block must drag the mean down by the arithmetic. Without this the only fill value ever exercised is 1.0, and a metric
        // that is right at 100 % and wrong everywhere else would pass — which is useless, because Q3 reads it in the middle of the range.
        int filled = vdb.CellCount;
        vdb.ComputeCellKey(bx, 0, 0);
        Assert.That(vdb.BlockCount, Is.EqualTo(2), "one cell past the block face must open a second block");
        Assert.That(vdb.IntraBlockFill, Is.EqualTo((filled + 1) / (2.0 * vdb.BlockCellCapacity)).Within(1e-9));

        TestContext.Out.WriteLine(
            $"AC-8.7 — block {bx}x{by}x{bz} = {vdb.BlockCellCapacity} cells; {vdb.CellCount} occupied in {vdb.BlockCount} blocks; "
            + $"fill {vdb.IntraBlockFill:P1}");
    }

    [Test]
    [CancelAfter(15_000)]
    public void AC87_FillIgnoresSlotsTheWorldBoundsMakeUnreachable()
    {
        // 40 cells per axis at a 16-cell block extent: three blocks, the last of which is truncated to 8. Only 40³ of 3³ x 16³ index slots can ever hold a
        // cell, so dividing by the raw block capacity caps the reported fill at 57.9 % however densely the world is filled — and Q3 reads this number to
        // decide between the dense index array and P2's bitmask. AC87_IntraBlockFill_IsInstrumented cannot see this: MultiBlock is 64 = 4 x 16 exactly, so
        // reachable and capacity coincide there.
        var cfg = new SpatialGridConfig(new Vector3(0, 0, 0), new Vector3(4000, 4000, 4000), cellSize: 100f);
        var vdb = new SpatialGrid(cfg);
        Assert.That((cfg.GridWidth, vdb.BlockDimensions.x), Is.EqualTo((40, 16)), "this fixture needs an axis that is not a whole number of blocks");

        for (int z = 0; z < cfg.GridDepth; z++)
        {
            for (int y = 0; y < cfg.GridHeight; y++)
            {
                for (int x = 0; x < cfg.GridWidth; x++)
                {
                    vdb.ComputeCellKey(x, y, z);
                }
            }
        }

        Assert.That(vdb.CellCount, Is.EqualTo(cfg.CellCount), "every cell of the world now exists");
        Assert.That(vdb.IntraBlockFill, Is.EqualTo(1.0).Within(1e-9),
            $"a completely full world is 100 % full whatever the block truncation — got {vdb.IntraBlockFill:P1} across {vdb.BlockCount} blocks");
    }

    [Test]
    [CancelAfter(15_000)]
    public void SetTierInAABB_ReachesEveryExistingCellInTheBox()
    {
        // The block-major walk replaced a per-cell one for cost, and a block-major loop is exactly where an off-by-one drops the last block on an axis —
        // silently, because the cells are still there and merely keep the wrong tier. The box deliberately spans several blocks and stops mid-block on the
        // far side, so both the "whole block" and "partial block" arms are exercised.
        var cfg = MultiBlock;
        var vdb = new SpatialGrid(cfg);
        var (bx, by, bz) = vdb.BlockDimensions;

        var inside = new List<int>();
        var outside = new List<int>();
        for (int z = 0; z < cfg.GridDepth; z += 3)
        {
            for (int y = 0; y < cfg.GridHeight; y += 3)
            {
                for (int x = 0; x < cfg.GridWidth; x += 3)
                {
                    int key = vdb.ComputeCellKey(x, y, z);
                    bool within = x <= (2 * bx) + 5 && y <= (2 * by) + 5 && z <= (2 * bz) + 5;
                    (within ? inside : outside).Add(key);
                }
            }
        }

        Assert.That(inside, Is.Not.Empty);
        Assert.That(outside, Is.Not.Empty);

        vdb.ResetAllTiers(SimTier.Tier3);
        float box = (float)cfg.CellSize;
        vdb.SetTierInAABB(0f, 0f, 0f, ((2 * bx) + 5) * box, ((2 * by) + 5) * box, ((2 * bz) + 5) * box, SimTier.Tier0);

        foreach (int key in inside)
        {
            Assert.That(vdb.GetCell(key).Tier, Is.EqualTo((byte)SimTier.Tier0), $"cell {vdb.CellKeyToCoords(key)} is inside the box");
        }

        foreach (int key in outside)
        {
            Assert.That(vdb.GetCell(key).Tier, Is.EqualTo((byte)SimTier.Tier3), $"cell {vdb.CellKeyToCoords(key)} is outside the box");
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void Accessor_RejectsTheMinusOneItHandsOutForAnEmptyRegion()
    {
        // ComputeCellKey and WorldToCell return -1 for a coordinate with no cell, and the obvious next call is GetCellCoords. Without the guard that reaches
        // the cell pool as index -1 and surfaces as a bare IndexOutOfRangeException from an internal array — an error message that names none of this.
        var vdb = new SpatialGrid(Cubic100);
        var accessor = new SpatialGridAccessor(vdb);

        Assert.That(accessor.ComputeCellKey(4, 4, 4), Is.EqualTo(-1), "nothing occupies that cell yet");
        Assert.That(accessor.WorldToCell(450f, 450f, 450f), Is.EqualTo(-1));

        Assert.Throws<ArgumentOutOfRangeException>(() => accessor.GetCellCoords(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => accessor.GetCellCoords(0), "no cell exists at all yet");

        int key = vdb.ComputeCellKey(4, 4, 4);
        Assert.That(accessor.ComputeCellKey(4, 4, 4), Is.EqualTo(key), "once the cell exists the accessor finds it");
        Assert.That(accessor.GetCellCoords(key), Is.EqualTo((4, 4, 4)));
    }

    [Test]
    [CancelAfter(15_000)]
    public void AC87_FlatWorldBlocksAreOneCellDeep()
    {
        // The per-axis block extent, which is the decision a fixed 16³ would get wrong: a flat world's block would then hold 4 096 index slots for 256
        // reachable cells, and AC-8.7's fill would read 6.25 % for a reason that has nothing to do with spatial sparsity.
        var flat = new SpatialGrid(Flat100);
        Assert.That(flat.BlockDimensions.z, Is.EqualTo(1));
        Assert.That(flat.BlockCellCapacity, Is.EqualTo(flat.BlockDimensions.x * flat.BlockDimensions.y));

        var cubic = new SpatialGrid(Cubic100);
        Assert.That(cubic.BlockDimensions.z, Is.GreaterThan(1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // AC-8.6 — concurrent creation
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void AC86_ConcurrentCreation_ProducesTheSameGridAsSerialCreation()
    {
        var cfg = MultiBlock;
        var coords = new List<(int x, int y, int z)>();
        var rng = new Random(31337);
        for (int i = 0; i < 3000; i++)
        {
            coords.Add((rng.Next(cfg.GridWidth), rng.Next(cfg.GridHeight), rng.Next(cfg.GridDepth)));
        }

        var serial = new SpatialGrid(cfg);
        foreach (var (x, y, z) in coords)
        {
            serial.ComputeCellKey(x, y, z);
        }

        foreach (int workers in new[] { 1, 2, 8 })
        {
            var parallel = new SpatialGrid(cfg);
            var observed = new System.Collections.Concurrent.ConcurrentBag<string>();
            using var start = new ManualResetEventSlim();
            var threads = new Task[workers];
            for (int w = 0; w < workers; w++)
            {
                int slice = w;
                threads[w] = Task.Run(() =>
                {
                    start.Wait();
                    // Overlapping, not partitioned: every worker walks the WHOLE list from a different offset, so the same cell is created concurrently many
                    // times over. A partitioned version would never make two threads race for one cell, which is the case that can duplicate a block.
                    for (int i = 0; i < coords.Count; i++)
                    {
                        var (x, y, z) = coords[(i + (slice * 37)) % coords.Count];
                        int key = parallel.ComputeCellKey(x, y, z);

                        // Read the coordinates back HERE, inside the race, not after the join. This is the only assertion in the fixture that can observe the
                        // publication pair CreateCell depends on: the cell's CellX/Y/Z are written before its slot is released into the block array, and a
                        // reader that saw the slot first would read (0, 0, 0). After Task.WaitAll every write is long since visible and the pair is untested.
                        if (parallel.CellKeyToCoords(key) != (x, y, z))
                        {
                            observed.Add($"slot {key} reported {parallel.CellKeyToCoords(key)} for ({x}, {y}, {z})");
                        }
                    }
                });
            }

            start.Set();
            Task.WaitAll(threads);

            Assert.That(observed, Is.Empty, $"W={workers}: a cell was observable before its coordinates were written — {string.Join("; ", observed)}");
            Assert.That(parallel.CellCount, Is.EqualTo(serial.CellCount), $"W={workers}: cell count must not depend on the worker count");
            Assert.That(parallel.BlockCount, Is.EqualTo(serial.BlockCount), $"W={workers}: a duplicated block would show up here");

            // Compare by COORDINATES: slot indices legitimately differ with the interleaving, the partition of space must not.
            var serialCells = CellCoordSet(serial);
            var parallelCells = CellCoordSet(parallel);
            Assert.That(parallelCells, Is.EquivalentTo(serialCells), $"W={workers}: the set of live cells must be identical");

            foreach (var (x, y, z) in coords)
            {
                Assert.That(parallel.TryGetCellKey(x, y, z, out int key), Is.True, $"W={workers}: ({x},{y},{z}) was created but cannot be resolved");
                Assert.That(parallel.CellKeyToCoords(key), Is.EqualTo((x, y, z)), $"W={workers}: cell {key} reports the wrong coordinates");
            }
        }
    }

    private static HashSet<(int, int, int)> CellCoordSet(SpatialGrid grid)
    {
        var set = new HashSet<(int, int, int)>();
        for (int i = 0; i < grid.CellCount; i++)
        {
            Assert.That(set.Add(grid.CellKeyToCoords(i)), Is.True, $"cell slot {i} duplicates another slot's coordinates");
        }
        return set;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Sparsity is not an accident of the read paths
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("VG-02")]
    public void ReadPaths_DoNotCreateCells()
    {
        // The failure mode the whole structure is one careless call away from: a read path using ComputeCellKey instead of TryGetCellKey materialises a cell
        // per coordinate it touches, and nothing else in the suite would notice — the answers stay correct, the memory quietly becomes dense again.
        var vdb = new SpatialGrid(Cubic100);
        vdb.ComputeCellKey(5, 5, 5);
        int before = vdb.CellCount;

        for (int z = 0; z < 10; z++)
        {
            for (int y = 0; y < 10; y++)
            {
                for (int x = 0; x < 10; x++)
                {
                    vdb.TryGetCellKey(x, y, z, out _);
                }
            }
        }

        vdb.TryGetCellKeyAt(500f, 500f, 500f, out _);
        vdb.SetTierInAABB(0f, 0f, 0f, 1000f, 1000f, 1000f, SimTier.Tier0);

        Assert.That(vdb.CellCount, Is.EqualTo(before), "a sweep of read-only calls over the whole world must create nothing");
    }

    [Test]
    [CancelAfter(15_000)]
    public void OutOfRangeCellCoordinates_ThrowRatherThanCreatingAPhantomBlock()
    {
        // The dense grid rejected an out-of-range key with an IndexOutOfRangeException from its array; a sparse one would happily CREATE a block outside the
        // world and hand back a usable key for a cell no world position can reach. The phantom would then survive until the next rebuild, which would not
        // reproduce it — a grid that disagrees with itself across a reopen.
        var vdb = new SpatialGrid(Cubic100);
        var cfg = Cubic100;

        Assert.Throws<ArgumentOutOfRangeException>(() => vdb.ComputeCellKey(cfg.GridWidth, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => vdb.ComputeCellKey(0, cfg.GridHeight, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => vdb.ComputeCellKey(0, 0, cfg.GridDepth));
        Assert.Throws<ArgumentOutOfRangeException>(() => vdb.ComputeCellKey(-1, 0, 0));

        Assert.That(vdb.TryGetCellKey(cfg.GridWidth, 0, 0, out int key), Is.False, "the read path answers absent rather than throwing");
        Assert.That(key, Is.EqualTo(-1));
        Assert.That(vdb.TryGetCellKey(-1, -1, -1, out _), Is.False);

        Assert.That(vdb.BlockCount, Is.Zero, "not one of those calls may have allocated a block");
        Assert.That(vdb.CellCount, Is.Zero);
    }

    [Test]
    [CancelAfter(15_000)]
    public void AC84_RebuildFromTheSamePopulation_ReproducesAnIdenticalGrid()
    {
        // RB-01: derived state is rebuilt, never repaired — so a rebuild from the same source must land on the same grid. Compared by COORDINATES and by
        // per-cell counters, never by key: slots are handed out in creation order, so the keys legitimately differ and comparing them would assert the one
        // thing the design says is not stable.
        var cfg = MultiBlock;
        var rng = new Random(1607);
        var population = new List<(int x, int y, int z)>();
        for (int i = 0; i < 2000; i++)
        {
            population.Add((rng.Next(cfg.GridWidth), rng.Next(cfg.GridHeight), rng.Next(cfg.GridDepth)));
        }

        var grid = new SpatialGrid(cfg);
        var before = Populate(grid, population);

        grid.ResetCellState();
        Assert.That(grid.CellCount, Is.Zero);

        // Rebuilt in a DIFFERENT order, which is the point: the grid must be a function of the population, not of the order it arrived in. Only the slot
        // numbering may differ.
        population.Reverse();
        var after = Populate(grid, population);

        Assert.That(after, Is.EqualTo(before), "the rebuilt grid must hold the same cells with the same counters");
    }

    /// <summary>Create every cell the population implies and return a coordinate-keyed snapshot of the grid's observable state.</summary>
    private static SortedDictionary<(int, int, int), (int entities, int clusters)> Populate(
        SpatialGrid grid, List<(int x, int y, int z)> population)
    {
        foreach (var (x, y, z) in population)
        {
            ref var cell = ref grid.GetCell(grid.ComputeCellKey(x, y, z));
            cell.EntityCount++;
            cell.ClusterCount = 1;
        }

        var snapshot = new SortedDictionary<(int, int, int), (int, int)>();
        for (int slot = 0; slot < grid.CellCount; slot++)
        {
            ref var cell = ref grid.GetCell(slot);
            snapshot[grid.CellKeyToCoords(slot)] = (cell.EntityCount, cell.ClusterCount);
        }
        return snapshot;
    }

    [Test]
    [CancelAfter(15_000)]
    public void NewCell_InheritsTheTierSetByTheLastResetAllTiers()
    {
        // Without this a cell created after the tick's ResetAllTiers starts at SimTier.None, TierClusterIndex skips it, and every cluster in it goes
        // undispatched for a tick. Silent: nothing throws, the entities simply do not run.
        var vdb = new SpatialGrid(Cubic100);
        vdb.ResetAllTiers(SimTier.Tier3);

        int fresh = vdb.ComputeCellKey(7, 7, 7);
        Assert.That(vdb.GetCell(fresh).Tier, Is.EqualTo((byte)SimTier.Tier3),
            "a cell created after the bulk reset must adopt the tier the reset established");
    }

    [Test]
    [CancelAfter(15_000)]
    public void ResetCellState_InvalidatesTheBlockCacheOnEveryThread_NotOnlyTheResettingOne()
    {
        // The per-thread block cache is a `[ThreadStatic]` keyed by grid instance. Clearing only the resetting thread's copy leaves every OTHER thread
        // holding a block id from the discarded numbering, and the fast path returns it without probing the map — so that thread resolves a coordinate to a
        // block belonging to a different region and files entities into the wrong cell. Silent, and invisible to a single-threaded reset test.
        var vdb = new SpatialGrid(MultiBlock);
        var (bx, _, _) = vdb.BlockDimensions;

        using var warmed = new ManualResetEventSlim();
        using var reset = new ManualResetEventSlim();
        string failure = null;

        var worker = Task.Run(() =>
        {
            // Warm this thread's cache on a block that will NOT be block 0 after the reset.
            vdb.ComputeCellKey(2 * bx, 0, 0);
            warmed.Set();
            reset.Wait();

            // After the reset the grid holds one cell, created below at (0,0,0) in block 0. A stale cache would answer for the old block 2.
            if (vdb.TryGetCellKey(2 * bx, 0, 0, out int stale))
            {
                failure = $"a coordinate with no cell resolved to slot {stale} from the pre-reset numbering";
            }
        });

        warmed.Wait();
        vdb.ResetCellState();
        int fresh = vdb.ComputeCellKey(0, 0, 0);
        Assert.That(fresh, Is.Zero, "the first cell after a reset is slot 0");
        reset.Set();
        worker.Wait();

        Assert.That(failure, Is.Null, failure);
    }

    [Test]
    [CancelAfter(15_000)]
    public void ResetCellState_DropsBlocksAndCells()
    {
        var vdb = new SpatialGrid(Cubic100);
        for (int i = 0; i < 50; i++)
        {
            vdb.ComputeCellKey(i % 10, (i / 10) % 10, i % 7);
        }

        Assert.That(vdb.CellCount, Is.GreaterThan(0));
        Assert.That(vdb.BlockCount, Is.GreaterThan(0));

        vdb.ResetCellState();

        Assert.That(vdb.CellCount, Is.Zero);
        Assert.That(vdb.BlockCount, Is.Zero);
        Assert.That(vdb.ResidentBytes, Is.Zero);
        Assert.That(vdb.TryGetCellKey(0, 0, 0, out _), Is.False, "the per-thread block cache must not survive a reset either");
    }
}
