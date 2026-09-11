using System.Numerics;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
class SpatialGridTests
{
    /// <summary>A flat 10 x 10 world — the shape every pre-#872 fixture used, now expressed as a grid one cell deep.</summary>
    private static SpatialGridConfig Config100 =>
        SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(1000, 1000), cellSize: 100f);

    /// <summary>A cubic 10 x 10 x 10 world. The only fixture in this file where the Z axis is not degenerate.</summary>
    private static SpatialGridConfig Config100Cubic =>
        new(new Vector3(0, 0, 0), new Vector3(1000, 1000, 1000), cellSize: 100f);

    [Test]
    public void Config_Derived_Width_Height_Correct()
    {
        var cfg = Config100;
        // 1000 / 100 = 10 cells per axis. Row-major keys, so the descriptor count is exactly W x H x D — no power-of-two padding, which the 2D Morton
        // encoding needed and which would have made a 3D grid's descriptor count KeySpaceDim cubed.
        Assert.That(cfg.GridWidth, Is.EqualTo(10));
        Assert.That(cfg.GridHeight, Is.EqualTo(10));
        Assert.That(cfg.GridDepth, Is.EqualTo(1), "Flat builds a world one cell deep");
        Assert.That(cfg.CellCount, Is.EqualTo(100));
        Assert.That(cfg.InverseCellSize, Is.EqualTo(0.01f).Within(1e-6f));
    }

    [Test]
    public void Config_Cubic_DerivesAllThreeAxes()
    {
        var cfg = Config100Cubic;
        Assert.That(cfg.GridWidth, Is.EqualTo(10));
        Assert.That(cfg.GridHeight, Is.EqualTo(10));
        Assert.That(cfg.GridDepth, Is.EqualTo(10));
        Assert.That(cfg.CellCount, Is.EqualTo(1000));
    }

    [Test]
    public void Config_FlatWorld_ResolvesEveryCellToItsOwnCoordinates()
    {
        // The equivalence #872 step 8 rests on, stated in the only terms that survived the sparse rewrite: every coordinate in a flat world resolves to a
        // distinct cell that reports those coordinates back.
        //
        // It used to assert `ComputeCellKey(x, y, 0) == y * 10 + x` — the row-major formula. That passed only because this loop happens to create cells in
        // row-major order: a key is now a POOL SLOT (`slot = _cellCount++`), so swapping the two loops would have failed it with the grid unchanged. The
        // formula belongs to the dense oracle, which still asserts it, and nothing in the engine computes a key that way any more.
        var grid = new SpatialGrid(Config100);
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                int key = grid.ComputeCellKey(x, y, 0);
                Assert.That(seen.Add(key), Is.True, $"cell ({x}, {y}) reused key {key}");
                Assert.That(grid.CellKeyToCoords(key), Is.EqualTo((x, y, 0)), $"cell ({x}, {y})");
            }
        }

        Assert.That(grid.CellCount, Is.EqualTo(100), "every coordinate of a 10 x 10 flat world is its own cell");
    }

    [Test]
    public void WorldToCellKey_Origin_IsCell0()
    {
        var grid = new SpatialGrid(Config100);
        int key = grid.WorldToCellKey(0f, 0f, 0f);
        var (x, y, z) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(0));
        Assert.That(y, Is.EqualTo(0));
        Assert.That(z, Is.EqualTo(0));
    }

    [Test]
    public void WorldToCellKey_KnownCellCenters_MapCorrectly()
    {
        var grid = new SpatialGrid(Config100);

        // Cell (5, 3) center is at (550, 350) in world space
        int key = grid.WorldToCellKey(550f, 350f, 0f);
        var (x, y, _) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(5));
        Assert.That(y, Is.EqualTo(3));

        // Cell (0, 0) — small offset
        key = grid.WorldToCellKey(1f, 1f, 0f);
        (x, y, _) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(0));
        Assert.That(y, Is.EqualTo(0));

        // Cell (0, 1) — just past Y boundary
        key = grid.WorldToCellKey(50f, 100.001f, 0f);
        (x, y, _) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(0));
        Assert.That(y, Is.EqualTo(1));
    }

    [Test]
    public void WorldToCellKey_CubicWorld_SeparatesEntitiesByZ()
    {
        // The property a flat world cannot show: two points sharing XY but not Z must land in DIFFERENT cells. Before #872 step 8 the grid dropped Z
        // entirely, so a 3D archetype's entities all shared one cell per XY column no matter how far apart they were vertically.
        var grid = new SpatialGrid(Config100Cubic);

        int low = grid.WorldToCellKey(550f, 350f, 50f);
        int high = grid.WorldToCellKey(550f, 350f, 750f);
        Assert.That(low, Is.Not.EqualTo(high), "same XY, different Z must not share a cell in a cubic world");

        var (lx, ly, lz) = grid.CellKeyToCoords(low);
        var (hx, hy, hz) = grid.CellKeyToCoords(high);
        Assert.That((lx, ly), Is.EqualTo((5, 3)));
        Assert.That((hx, hy), Is.EqualTo((5, 3)));
        Assert.That(lz, Is.EqualTo(0));
        Assert.That(hz, Is.EqualTo(7));
    }

    [Test]
    public void WorldToCellKey_FlatWorld_IgnoresZ()
    {
        // The complement of the test above, and the reason a 2D game can hand the 3D grid whatever Z it likes: a one-cell-deep world clamps every Z into
        // the single plane, so a stray Z can never move an entity to a different cell.
        var grid = new SpatialGrid(Config100);
        int atZero = grid.WorldToCellKey(550f, 350f, 0f);
        Assert.That(grid.WorldToCellKey(550f, 350f, 99f), Is.EqualTo(atZero));
        Assert.That(grid.WorldToCellKey(550f, 350f, 100_000f), Is.EqualTo(atZero));
        Assert.That(grid.WorldToCellKey(550f, 350f, -100_000f), Is.EqualTo(atZero));
    }

    [Test]
    public void WorldToCellKey_OutOfBounds_ClampsToValidCell()
    {
        var grid = new SpatialGrid(Config100);

        // Negative world position clamps to cell (0,0)
        int key = grid.WorldToCellKey(-500f, -500f, 0f);
        var (x, y, _) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(0));
        Assert.That(y, Is.EqualTo(0));

        // Well past the max clamps to the last cell on both axes — the world is 10 x 10.
        key = grid.WorldToCellKey(99999f, 99999f, 0f);
        (x, y, _) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(9));
        Assert.That(y, Is.EqualTo(9));
    }

    [Test]
    public void CellKeyToCoords_RoundTrip_CoversFullGrid()
    {
        var grid = new SpatialGrid(Config100Cubic);
        for (int z = 0; z < 10; z++)
        {
            for (int y = 0; y < 10; y++)
            {
                for (int x = 0; x < 10; x++)
                {
                    int key = grid.ComputeCellKey(x, y, z);
                    var (rx, ry, rz) = grid.CellKeyToCoords(key);
                    Assert.That((rx, ry, rz), Is.EqualTo((x, y, z)));
                }
            }
        }
    }

    [Test]
    public void GetCell_ReturnsMutableReference()
    {
        var grid = new SpatialGrid(Config100);
        int key = grid.WorldToCellKey(150f, 250f, 0f);
        ref var cell = ref grid.GetCell(key);
        Assert.That(cell.ClusterCount, Is.EqualTo(0));
        Assert.That(cell.EntityCount, Is.EqualTo(0));

        cell.EntityCount = 42;
        Assert.That(grid.GetCell(key).EntityCount, Is.EqualTo(42));
    }

    [Test]
    public void ResetCellState_ClearsGlobalCellCounters()
    {
        var grid = new SpatialGrid(Config100);
        int key = grid.WorldToCellKey(150f, 250f, 0f);
        ref var cell = ref grid.GetCell(key);
        cell.EntityCount = 7;
        cell.ClusterCount = 2;

        grid.ResetCellState();

        // The old key is not reusable: a reset drops every cell, so slots are handed out from zero again and the coordinate must be resolved afresh. Reading
        // the stale key would touch a cell that no longer exists — which is the whole point of asserting the count through a re-resolve.
        Assert.That(grid.CellCount, Is.Zero, "a reset leaves no cells at all, not cells with zeroed counters");

        int rebuilt = grid.WorldToCellKey(150f, 250f, 0f);
        ref var after = ref grid.GetCell(rebuilt);
        Assert.That(after.EntityCount, Is.EqualTo(0));
        Assert.That(after.ClusterCount, Is.EqualTo(0));
    }

    [Test]
    public unsafe void WorldToCellKeyFromSpatialField_AABB2F_UsesCenter()
    {
        var grid = new SpatialGrid(Config100);
        // AABB2F layout: MinX, MinY, MaxX, MaxY (4 floats, 16 bytes)
        float* fieldData = stackalloc float[4];
        fieldData[0] = 100f;  // MinX
        fieldData[1] = 200f;  // MinY
        fieldData[2] = 200f;  // MaxX (center = 150)
        fieldData[3] = 400f;  // MaxY (center = 300)

        int key = grid.WorldToCellKeyFromSpatialField((byte*)fieldData, SpatialFieldType.AABB2F);
        var (x, y, z) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(1));  // 150 / 100 = 1
        Assert.That(y, Is.EqualTo(3));  // 300 / 100 = 3
        Assert.That(z, Is.EqualTo(0), "a 2D field reports posZ = 0 and lands in the first Z plane");
    }

    [Test]
    public unsafe void WorldToCellKeyFromSpatialField_BSphere2F_UsesCenter()
    {
        var grid = new SpatialGrid(Config100);
        // BSphere2F layout: CenterX, CenterY, Radius
        float* fieldData = stackalloc float[3];
        fieldData[0] = 550f;  // CenterX
        fieldData[1] = 750f;  // CenterY
        fieldData[2] = 25f;   // Radius (irrelevant)

        int key = grid.WorldToCellKeyFromSpatialField((byte*)fieldData, SpatialFieldType.BSphere2F);
        var (x, y, _) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(5));
        Assert.That(y, Is.EqualTo(7));
    }

    [Test]
    public unsafe void WorldToCellKeyFromSpatialField_AABB3F_UsesTheZCentreToo()
    {
        // Before #872 step 8 this method read a 3D AABB's min/max at the WRONG offsets for Z and then discarded it. Both halves matter: the layout is
        // [minX, minY, minZ, maxX, maxY, maxZ], so maxX lives at index 3, not index 2.
        var grid = new SpatialGrid(Config100Cubic);
        float* fieldData = stackalloc float[6];
        fieldData[0] = 100f;  // MinX
        fieldData[1] = 200f;  // MinY
        fieldData[2] = 600f;  // MinZ
        fieldData[3] = 200f;  // MaxX (centre 150)
        fieldData[4] = 400f;  // MaxY (centre 300)
        fieldData[5] = 800f;  // MaxZ (centre 700)

        int key = grid.WorldToCellKeyFromSpatialField((byte*)fieldData, SpatialFieldType.AABB3F);
        Assert.That(grid.CellKeyToCoords(key), Is.EqualTo((1, 3, 7)));
    }

    [Test]
    public unsafe void WorldToCellKeyFromSpatialField_BSphere3F_UsesTheZCentre()
    {
        var grid = new SpatialGrid(Config100Cubic);
        // BSphere3F layout: CenterX, CenterY, CenterZ, Radius
        float* fieldData = stackalloc float[4];
        fieldData[0] = 550f;
        fieldData[1] = 750f;
        fieldData[2] = 250f;
        fieldData[3] = 25f;   // Radius (irrelevant)

        int key = grid.WorldToCellKeyFromSpatialField((byte*)fieldData, SpatialFieldType.BSphere3F);
        Assert.That(grid.CellKeyToCoords(key), Is.EqualTo((5, 7, 2)));
    }

    [Test]
    [VerifiesRule("SQ-06")]
    public unsafe void ReadSpatialCenter3D_ComputesTheMidpointInDouble_SoItAgreesWithTheWriteBarrier()
    {
        // The centre of an entity is computed in TWO places and they must produce the same number: here (fence-time cell placement, spawn routing,
        // migration detection) and ClusterRef.ApplySpatialWrite (write-time). Since #914 the write barrier takes f64 bounds, so this decoder does the
        // addition in double too — for the f32 tiers as well, where it is exact.
        //
        // The values below are chosen so the two arithmetics DISAGREE if this one narrows: at 10^7 the f32 step is 1, so 10^7 and 10^7+1 are adjacent
        // floats whose float-arithmetic midpoint rounds to one of them, while the true midpoint has a half in it. That half is what a cell boundary can
        // fall on, and the failure it produces is trap 1 of #914 in miniature — write-time and fence-time placing one entity in two different cells,
        // each self-consistent, no counter out of balance.
        const float Min = 10_000_000f;
        const float Max = 10_000_001f;

        Assert.That(Max, Is.Not.EqualTo(Min), "PRECONDITION: the two bounds must be distinct floats at this magnitude.");
        Assert.That((float)((Min + Max) * 0.5f), Is.Not.EqualTo(((double)Min + Max) * 0.5d),
            "PRECONDITION: f32 and f64 midpoint arithmetic must actually differ here, or the assertion below proves nothing.");

        float* fieldData = stackalloc float[4];
        fieldData[0] = Min;   // MinX
        fieldData[1] = Min;   // MinY
        fieldData[2] = Max;   // MaxX
        fieldData[3] = Max;   // MaxY

        SpatialGrid.ReadSpatialCenter3D((byte*)fieldData, SpatialFieldType.AABB2F, out var posX, out var posY, out var posZ);

        Assert.Multiple(() =>
        {
            Assert.That(posX, Is.EqualTo(((double)Min + Max) * 0.5d), "the X centre must be the exact f64 midpoint the write barrier computes");
            Assert.That(posY, Is.EqualTo(((double)Min + Max) * 0.5d));
            Assert.That(posZ, Is.EqualTo(0d), "a 2D tier reports the flat plane on Z");
        });
    }

    [Test]
    public void ValidateSupportedFieldType_Passes_OnEveryDeclaredTier()
    {
        // #230 Phase 3 extended the supported set from 2D-only to all f32 tiers; #914 opened the remaining four. Asserting the WHOLE enum rather than a list
        // is the point: a variant added later without a decode case in ReadSpatialCenter3D fails here rather than at a customer's spawn.
        foreach (SpatialFieldType fieldType in System.Enum.GetValues<SpatialFieldType>())
        {
            Assert.DoesNotThrow(() => SpatialGrid.ValidateSupportedFieldType(fieldType, "MyArch"), $"tier {fieldType}");
        }
    }

    [Test]
    public void ValidateSupportedFieldType_Throws_OnAnUndeclaredValue()
    {
        // The guard that remains after #914: a byte that is not a SpatialFieldType at all, which is what a corrupted or hand-rolled schema produces.
        Assert.Throws<System.NotSupportedException>(
            () => SpatialGrid.ValidateSupportedFieldType((SpatialFieldType)200, "MyArch"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Guards added as code-review fixes
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WorldToCellKey_NaN_Throws()
    {
        var grid = new SpatialGrid(Config100);
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(float.NaN, 0f, 0f));
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(0f, float.NaN, 0f));
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(0f, 0f, float.NaN));
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(float.NaN, float.NaN, float.NaN));
    }

    [Test]
    public void WorldToCellKey_Infinity_Throws()
    {
        var grid = new SpatialGrid(Config100);
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(float.PositiveInfinity, 0f, 0f));
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(0f, float.PositiveInfinity, 0f));
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(0f, 0f, float.PositiveInfinity));
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(float.NegativeInfinity, 0f, 0f));
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(0f, float.NegativeInfinity, 0f));
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(0f, 0f, float.NegativeInfinity));
    }

    [Test]
    public void SpatialGridConfig_CellCountExceedingAnInt32Key_Throws()
    {
        // 2M x 2M cells = 4e12, well past what a 32-bit cell key can name. Computed in long inside the ctor precisely so this reports the real number
        // instead of overflowing to a negative CellCount and failing later at the descriptor allocation.
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(2_000_000, 2_000_000), cellSize: 1f));
    }

    [Test]
    public void SpatialGridConfig_CellCountJustUnderTheInt32Key_Succeeds()
    {
        // 1290 cubed = 2 146 689 000, just under int.MaxValue. The config allocates nothing, so this is a bound on the KEY type, not on memory.
        var cfg = new SpatialGridConfig(new Vector3(0, 0, 0), new Vector3(1290, 1290, 1290), cellSize: 1f);
        Assert.That(cfg.CellCount, Is.EqualTo(1290 * 1290 * 1290));
    }

    [Test]
    public void SpatialGridConfig_DegenerateZExtent_Throws()
    {
        // A caller reaching for the 3D constructor with a 2D world would otherwise get GridDepth == 0 and a zero-cell grid. Flat() is the supported way to
        // express a flat world.
        Assert.Throws<System.ArgumentException>(() =>
            new SpatialGridConfig(new Vector3(0, 0, 0), new Vector3(1000, 1000, 0), cellSize: 100f));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SetCellTier (strict)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SetCellTier_SingleBitTier_IsReadableViaGetCell()
    {
        var grid = new SpatialGrid(Config100);
        int key = grid.ComputeCellKey(3, 4, 0);

        grid.SetCellTier(key, SimTier.Tier0);
        Assert.That(grid.GetCell(key).Tier, Is.EqualTo((byte)SimTier.Tier0));

        grid.SetCellTier(key, SimTier.Tier2);
        Assert.That(grid.GetCell(key).Tier, Is.EqualTo((byte)SimTier.Tier2));
    }

    [Test]
    public void SetCellTier_SameValue_DoesNotBumpTierVersion()
    {
        var grid = new SpatialGrid(Config100);
        int key = grid.ComputeCellKey(5, 5, 0);

        grid.SetCellTier(key, SimTier.Tier1);
        int versionAfterFirst = grid.TierVersion;

        grid.SetCellTier(key, SimTier.Tier1);
        Assert.That(grid.TierVersion, Is.EqualTo(versionAfterFirst));
    }

    [Test]
    public void SetCellTier_DifferentValue_BumpsTierVersion()
    {
        var grid = new SpatialGrid(Config100);
        int key = grid.ComputeCellKey(2, 7, 0);

        grid.SetCellTier(key, SimTier.Tier0);
        int versionAfterFirst = grid.TierVersion;

        grid.SetCellTier(key, SimTier.Tier3);
        Assert.That(grid.TierVersion, Is.EqualTo(versionAfterFirst + 1));
    }

    [Test]
    public void SetCellTier_BoundaryCells_FirstAndLast()
    {
        var grid = new SpatialGrid(Config100Cubic);

        // First cell: (0, 0, 0)
        int firstKey = grid.ComputeCellKey(0, 0, 0);
        grid.SetCellTier(firstKey, SimTier.Tier0);
        Assert.That(grid.GetCell(firstKey).Tier, Is.EqualTo((byte)SimTier.Tier0));

        // Last valid cell: (9, 9, 9) — the grid is 10 x 10 x 10 in world cells.
        //
        // No assertion that the key equals `CellCount - 1`: that holds for ANY freshly created cell and is therefore constant regardless of the code path.
        // It also restated a dense-grid property ("the last coordinate names the last descriptor slot") that stopped being true when keys became pool slots.
        int lastKey = grid.ComputeCellKey(9, 9, 9);
        Assert.That(lastKey, Is.Not.EqualTo(firstKey), "the two corners must be different cells");
        grid.SetCellTier(lastKey, SimTier.Tier3);
        Assert.That(grid.GetCell(lastKey).Tier, Is.EqualTo((byte)SimTier.Tier3));
        Assert.That(grid.GetCell(firstKey).Tier, Is.EqualTo((byte)SimTier.Tier0), "setting one corner must not disturb the other");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // WorldToCellRange tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WorldToCellRange_FullyInsideOneCell_Returns1x1Range()
    {
        var grid = new SpatialGrid(Config100);
        // AABB sitting entirely within cell (3, 5): world X [310,390], Y [510,590]
        grid.WorldToCellRange(310f, 510f, 0f, 390f, 590f, 0f,
            out int cellMinX, out int cellMinY, out int cellMinZ, out int cellMaxX, out int cellMaxY, out int cellMaxZ);

        Assert.That(cellMinX, Is.EqualTo(3));
        Assert.That(cellMinY, Is.EqualTo(5));
        Assert.That(cellMaxX, Is.EqualTo(3));
        Assert.That(cellMaxY, Is.EqualTo(5));
        Assert.That(cellMinZ, Is.EqualTo(0));
        Assert.That(cellMaxZ, Is.EqualTo(0));
    }

    [Test]
    public void WorldToCellRange_SpanningMultipleCells_ReturnsCorrectRange()
    {
        var grid = new SpatialGrid(Config100);
        // AABB from world (150,250) to (450,650) → cells X [1,4], Y [2,6]
        grid.WorldToCellRange(150f, 250f, 0f, 450f, 650f, 0f,
            out int cellMinX, out int cellMinY, out _, out int cellMaxX, out int cellMaxY, out _);

        Assert.That(cellMinX, Is.EqualTo(1));
        Assert.That(cellMinY, Is.EqualTo(2));
        Assert.That(cellMaxX, Is.EqualTo(4));
        Assert.That(cellMaxY, Is.EqualTo(6));
    }

    [Test]
    public void WorldToCellRange_SpanningZ_ReturnsTheZRangeToo()
    {
        var grid = new SpatialGrid(Config100Cubic);
        grid.WorldToCellRange(150f, 250f, 350f, 450f, 650f, 850f,
            out _, out _, out int cellMinZ, out _, out _, out int cellMaxZ);

        Assert.That(cellMinZ, Is.EqualTo(3));
        Assert.That(cellMaxZ, Is.EqualTo(8));
    }

    [Test]
    public void WorldToCellRange_InfiniteZ_SaturatesToTheFullDepth()
    {
        // ArchetypeClusterState.QueryAabb hands ±Infinity Z for a 2D archetype, meaning "every Z". Flooring an infinity would give int.MinValue/MaxValue,
        // so the clamp is done on the float BEFORE the cast — this test is what says that distinction is load-bearing rather than stylistic.
        var grid = new SpatialGrid(Config100Cubic);
        grid.WorldToCellRange(150f, 250f, float.NegativeInfinity, 450f, 650f, float.PositiveInfinity,
            out _, out _, out int cellMinZ, out _, out _, out int cellMaxZ);

        Assert.That(cellMinZ, Is.EqualTo(0));
        Assert.That(cellMaxZ, Is.EqualTo(9));
    }

    [Test]
    public void WorldToCellRange_PartiallyOutsideWorldBounds_ClampedToValidRange()
    {
        var grid = new SpatialGrid(Config100);
        // AABB from (-200, -100) to (350, 250) — negative coords clamp to cell 0
        grid.WorldToCellRange(-200f, -100f, 0f, 350f, 250f, 0f,
            out int cellMinX, out int cellMinY, out _, out int cellMaxX, out int cellMaxY, out _);

        Assert.That(cellMinX, Is.EqualTo(0));
        Assert.That(cellMinY, Is.EqualTo(0));
        Assert.That(cellMaxX, Is.EqualTo(3));
        Assert.That(cellMaxY, Is.EqualTo(2));
    }

    [Test]
    public void WorldToCellRange_FullyOutsideWorldBounds_ReturnsClamped0WidthRange()
    {
        var grid = new SpatialGrid(Config100);

        // Entirely below-left of world: both min and max clamp to cell (0,0)
        grid.WorldToCellRange(-500f, -500f, 0f, -100f, -100f, 0f,
            out int cellMinX, out int cellMinY, out _, out int cellMaxX, out int cellMaxY, out _);

        Assert.That(cellMinX, Is.EqualTo(0));
        Assert.That(cellMinY, Is.EqualTo(0));
        Assert.That(cellMaxX, Is.EqualTo(0));
        Assert.That(cellMaxY, Is.EqualTo(0));

        // Entirely above-right of world: both min and max clamp to cell (9,9)
        grid.WorldToCellRange(1500f, 1500f, 0f, 2000f, 2000f, 0f,
            out cellMinX, out cellMinY, out _, out cellMaxX, out cellMaxY, out _);

        Assert.That(cellMinX, Is.EqualTo(9));
        Assert.That(cellMinY, Is.EqualTo(9));
        Assert.That(cellMaxX, Is.EqualTo(9));
        Assert.That(cellMaxY, Is.EqualTo(9));
    }

    [Test]
    public void WorldToCellRange_ExactlyAlignedOnCellBoundaries_ReturnsCorrectRange()
    {
        var grid = new SpatialGrid(Config100);
        // AABB from (200,300) to (500,600) — exact cell boundaries.
        // Floor((200-0)*0.01)=2, Floor((300-0)*0.01)=3, Floor((500-0)*0.01)=5, Floor((600-0)*0.01)=6.
        // Points exactly on a cell boundary (e.g. 500) land in the next cell via Floor.
        grid.WorldToCellRange(200f, 300f, 0f, 500f, 600f, 0f,
            out int cellMinX, out int cellMinY, out _, out int cellMaxX, out int cellMaxY, out _);

        Assert.That(cellMinX, Is.EqualTo(2));
        Assert.That(cellMinY, Is.EqualTo(3));
        Assert.That(cellMaxX, Is.EqualTo(5));
        Assert.That(cellMaxY, Is.EqualTo(6));
    }

    [Test]
    public void WorldToCellRange_ExactlyOnMaxWorldEdge_ClampsToLastCell()
    {
        var grid = new SpatialGrid(Config100);
        // maxX=1000 → Floor((1000-0)*0.01)=10, clamped to 9. Same for Y.
        grid.WorldToCellRange(900f, 900f, 0f, 1000f, 1000f, 0f,
            out int cellMinX, out int cellMinY, out _, out int cellMaxX, out int cellMaxY, out _);

        Assert.That(cellMinX, Is.EqualTo(9));
        Assert.That(cellMinY, Is.EqualTo(9));
        Assert.That(cellMaxX, Is.EqualTo(9));
        Assert.That(cellMaxY, Is.EqualTo(9));
    }

    [Test]
    public void WorldToCellRange_NaN_Throws()
    {
        var grid = new SpatialGrid(Config100);
        Assert.Throws<System.ArgumentException>(
            () => grid.WorldToCellRange(float.NaN, 0f, 0f, 100f, 100f, 0f, out _, out _, out _, out _, out _, out _));
        Assert.Throws<System.ArgumentException>(
            () => grid.WorldToCellRange(0f, 0f, 0f, float.NaN, 100f, 0f, out _, out _, out _, out _, out _, out _));
        Assert.Throws<System.ArgumentException>(
            () => grid.WorldToCellRange(0f, 0f, float.NaN, 100f, 100f, 0f, out _, out _, out _, out _, out _, out _),
            "NaN has no 'every Z' reading, so it is rejected on Z as well");
    }

    [Test]
    public void WorldToCellRange_InfinityOnXOrY_Throws()
    {
        // Only Z carries the "unbounded" reading (a 2D archetype's query). An infinite X or Y is still corrupt input.
        var grid = new SpatialGrid(Config100);
        Assert.Throws<System.ArgumentException>(
            () => grid.WorldToCellRange(float.PositiveInfinity, 0f, 0f, 100f, 100f, 0f, out _, out _, out _, out _, out _, out _));
        Assert.Throws<System.ArgumentException>(
            () => grid.WorldToCellRange(0f, 0f, 0f, 100f, float.NegativeInfinity, 0f, out _, out _, out _, out _, out _, out _));
    }
}
