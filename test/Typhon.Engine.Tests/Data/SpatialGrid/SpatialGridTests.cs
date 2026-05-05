using System.Numerics;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
class SpatialGridTests
{
    private static SpatialGridConfig Config100 =>
        new(new Vector2(0, 0), new Vector2(1000, 1000), cellSize: 100f);

    [Test]
    public void Config_Derived_Width_Height_Correct()
    {
        var cfg = Config100;
        // 1000 / 100 = 10 cells per axis (world dims).
        // Morton pads the key space to the next pow2 = 16, so descriptor array holds 256 slots.
        Assert.That(cfg.GridWidth, Is.EqualTo(10));
        Assert.That(cfg.GridHeight, Is.EqualTo(10));
        Assert.That(cfg.KeySpaceDim, Is.EqualTo(16));
        Assert.That(cfg.CellCount, Is.EqualTo(256));
        Assert.That(cfg.InverseCellSize, Is.EqualTo(0.01f).Within(1e-6f));
    }

    [Test]
    public void WorldToCellKey_Origin_IsCell0()
    {
        var grid = new SpatialGrid(Config100);
        int key = grid.WorldToCellKey(0f, 0f);
        var (x, y) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(0));
        Assert.That(y, Is.EqualTo(0));
    }

    [Test]
    public void WorldToCellKey_KnownCellCenters_MapCorrectly()
    {
        var grid = new SpatialGrid(Config100);

        // Cell (5, 3) center is at (550, 350) in world space
        int key = grid.WorldToCellKey(550f, 350f);
        var (x, y) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(5));
        Assert.That(y, Is.EqualTo(3));

        // Cell (0, 0) — small offset
        key = grid.WorldToCellKey(1f, 1f);
        (x, y) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(0));
        Assert.That(y, Is.EqualTo(0));

        // Cell (0, 1) — just past Y boundary
        key = grid.WorldToCellKey(50f, 100.001f);
        (x, y) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(0));
        Assert.That(y, Is.EqualTo(1));
    }

    [Test]
    public void WorldToCellKey_OutOfBounds_ClampsToValidCell()
    {
        var grid = new SpatialGrid(Config100);

        // Negative world position clamps to cell (0,0)
        int key = grid.WorldToCellKey(-500f, -500f);
        var (x, y) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(0));
        Assert.That(y, Is.EqualTo(0));

        // Well past the max clamps to the last used cell on both axes. Morton pads to 16×16 but the
        // world only uses 10×10, so the clamp targets cell (9, 9).
        key = grid.WorldToCellKey(99999f, 99999f);
        (x, y) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(9));
        Assert.That(y, Is.EqualTo(9));
    }

    [Test]
    public void CellKeyToCoords_RoundTrip_CoversFullGrid()
    {
        var grid = new SpatialGrid(Config100);
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                int key = grid.ComputeCellKey(x, y);
                var (rx, ry) = grid.CellKeyToCoords(key);
                Assert.That(rx, Is.EqualTo(x));
                Assert.That(ry, Is.EqualTo(y));
            }
        }
    }

    [Test]
    public void GetCell_ReturnsMutableReference()
    {
        var grid = new SpatialGrid(Config100);
        int key = grid.WorldToCellKey(150f, 250f);
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
        int key = grid.WorldToCellKey(150f, 250f);
        ref var cell = ref grid.GetCell(key);
        cell.EntityCount = 7;
        cell.ClusterCount = 2;

        grid.ResetCellState();

        ref var after = ref grid.GetCell(key);
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
        var (x, y) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(1));  // 150 / 100 = 1
        Assert.That(y, Is.EqualTo(3));  // 300 / 100 = 3
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
        var (x, y) = grid.CellKeyToCoords(key);
        Assert.That(x, Is.EqualTo(5));
        Assert.That(y, Is.EqualTo(7));
    }

    [Test]
    public void ValidateSupportedFieldType_Throws_OnF64Tiers()
    {
        // Issue #230 Phase 3 extended the grid to support 3D f32; f64 tiers remain deferred to a follow-up sub-issue of #228.
        Assert.Throws<System.NotSupportedException>(
            () => SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.AABB2D, "MyArch"));
        Assert.Throws<System.NotSupportedException>(
            () => SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.AABB3D, "MyArch"));
    }

    [Test]
    public void ValidateSupportedFieldType_Passes_OnF32Tiers()
    {
        // Issue #230 Phase 3 extended the supported set from 2D-only to all f32 tiers (2D and 3D).
        SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.AABB2F, "MyArch");
        SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.BSphere2F, "MyArch");
        SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.AABB3F, "MyArch");
        SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.BSphere3F, "MyArch");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Guards added as code-review fixes
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WorldToCellKey_NaN_Throws()
    {
        var grid = new SpatialGrid(Config100);
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(float.NaN, 0f));
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(0f, float.NaN));
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(float.NaN, float.NaN));
    }

    [Test]
    public void WorldToCellKey_Infinity_Throws()
    {
        var grid = new SpatialGrid(Config100);
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(float.PositiveInfinity, 0f));
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(0f, float.PositiveInfinity));
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(float.NegativeInfinity, 0f));
        Assert.Throws<System.ArgumentException>(() => grid.WorldToCellKey(0f, float.NegativeInfinity));
    }

    [Test]
    public void SpatialGridConfig_KeySpaceDim_OverflowsInt32Morton_Throws()
    {
        // 4M cells per axis × 1-unit cell → KeySpaceDim would be NextPow2(4M) = 4 194 304 > 32 768.
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            new SpatialGridConfig(new Vector2(0, 0), new Vector2(4_000_000, 4_000_000), cellSize: 1f));
    }

    [Test]
    public void SpatialGridConfig_KeySpaceDim_AtLimit_Succeeds()
    {
        // 32 768 × 32 768 grid (exact limit). NextPow2(32768) == 32768.
        // 32 768 cells × 1-unit cell = 32 768-unit world.
        var cfg = new SpatialGridConfig(new Vector2(0, 0), new Vector2(32_768, 32_768), cellSize: 1f);
        Assert.That(cfg.KeySpaceDim, Is.EqualTo(32_768));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SetCellTier (strict)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SetCellTier_SingleBitTier_IsReadableViaGetCell()
    {
        var grid = new SpatialGrid(Config100);
        int key = grid.ComputeCellKey(3, 4);

        grid.SetCellTier(key, SimTier.Tier0);
        Assert.That(grid.GetCell(key).Tier, Is.EqualTo((byte)SimTier.Tier0));

        grid.SetCellTier(key, SimTier.Tier2);
        Assert.That(grid.GetCell(key).Tier, Is.EqualTo((byte)SimTier.Tier2));
    }

    [Test]
    public void SetCellTier_SameValue_DoesNotBumpTierVersion()
    {
        var grid = new SpatialGrid(Config100);
        int key = grid.ComputeCellKey(5, 5);

        grid.SetCellTier(key, SimTier.Tier1);
        int versionAfterFirst = grid.TierVersion;

        grid.SetCellTier(key, SimTier.Tier1);
        Assert.That(grid.TierVersion, Is.EqualTo(versionAfterFirst));
    }

    [Test]
    public void SetCellTier_DifferentValue_BumpsTierVersion()
    {
        var grid = new SpatialGrid(Config100);
        int key = grid.ComputeCellKey(2, 7);

        grid.SetCellTier(key, SimTier.Tier0);
        int versionAfterFirst = grid.TierVersion;

        grid.SetCellTier(key, SimTier.Tier3);
        Assert.That(grid.TierVersion, Is.EqualTo(versionAfterFirst + 1));
    }

    [Test]
    public void SetCellTier_BoundaryCells_FirstAndLast()
    {
        var grid = new SpatialGrid(Config100);

        // First cell: (0, 0)
        int firstKey = grid.ComputeCellKey(0, 0);
        grid.SetCellTier(firstKey, SimTier.Tier0);
        Assert.That(grid.GetCell(firstKey).Tier, Is.EqualTo((byte)SimTier.Tier0));

        // Last valid cell: (9, 9) — the grid is 10×10 in world cells
        int lastKey = grid.ComputeCellKey(9, 9);
        grid.SetCellTier(lastKey, SimTier.Tier3);
        Assert.That(grid.GetCell(lastKey).Tier, Is.EqualTo((byte)SimTier.Tier3));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // WorldToCellRange tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WorldToCellRange_FullyInsideOneCell_Returns1x1Range()
    {
        var grid = new SpatialGrid(Config100);
        // AABB sitting entirely within cell (3, 5): world X [310,390], Y [510,590]
        grid.WorldToCellRange(310f, 510f, 390f, 590f,
            out int cellMinX, out int cellMinY, out int cellMaxX, out int cellMaxY);

        Assert.That(cellMinX, Is.EqualTo(3));
        Assert.That(cellMinY, Is.EqualTo(5));
        Assert.That(cellMaxX, Is.EqualTo(3));
        Assert.That(cellMaxY, Is.EqualTo(5));
    }

    [Test]
    public void WorldToCellRange_SpanningMultipleCells_ReturnsCorrectRange()
    {
        var grid = new SpatialGrid(Config100);
        // AABB from world (150,250) to (450,650) → cells X [1,4], Y [2,6]
        grid.WorldToCellRange(150f, 250f, 450f, 650f,
            out int cellMinX, out int cellMinY, out int cellMaxX, out int cellMaxY);

        Assert.That(cellMinX, Is.EqualTo(1));
        Assert.That(cellMinY, Is.EqualTo(2));
        Assert.That(cellMaxX, Is.EqualTo(4));
        Assert.That(cellMaxY, Is.EqualTo(6));
    }

    [Test]
    public void WorldToCellRange_PartiallyOutsideWorldBounds_ClampedToValidRange()
    {
        var grid = new SpatialGrid(Config100);
        // AABB from (-200, -100) to (350, 250) — negative coords clamp to cell 0
        grid.WorldToCellRange(-200f, -100f, 350f, 250f,
            out int cellMinX, out int cellMinY, out int cellMaxX, out int cellMaxY);

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
        grid.WorldToCellRange(-500f, -500f, -100f, -100f,
            out int cellMinX, out int cellMinY, out int cellMaxX, out int cellMaxY);

        Assert.That(cellMinX, Is.EqualTo(0));
        Assert.That(cellMinY, Is.EqualTo(0));
        Assert.That(cellMaxX, Is.EqualTo(0));
        Assert.That(cellMaxY, Is.EqualTo(0));

        // Entirely above-right of world: both min and max clamp to cell (9,9)
        grid.WorldToCellRange(1500f, 1500f, 2000f, 2000f,
            out cellMinX, out cellMinY, out cellMaxX, out cellMaxY);

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
        grid.WorldToCellRange(200f, 300f, 500f, 600f,
            out int cellMinX, out int cellMinY, out int cellMaxX, out int cellMaxY);

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
        grid.WorldToCellRange(900f, 900f, 1000f, 1000f,
            out int cellMinX, out int cellMinY, out int cellMaxX, out int cellMaxY);

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
            () => grid.WorldToCellRange(float.NaN, 0f, 100f, 100f, out _, out _, out _, out _));
        Assert.Throws<System.ArgumentException>(
            () => grid.WorldToCellRange(0f, 0f, float.NaN, 100f, out _, out _, out _, out _));
    }

    [Test]
    public void WorldToCellRange_Infinity_Throws()
    {
        var grid = new SpatialGrid(Config100);
        Assert.Throws<System.ArgumentException>(
            () => grid.WorldToCellRange(float.PositiveInfinity, 0f, 100f, 100f, out _, out _, out _, out _));
        Assert.Throws<System.ArgumentException>(
            () => grid.WorldToCellRange(0f, 0f, 100f, float.NegativeInfinity, out _, out _, out _, out _));
    }
}
