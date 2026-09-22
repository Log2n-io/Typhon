using System;
using System.Numerics;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The f64 world frame (#914 phase A): a grid whose bounds and cell origins are doubles, so world extent is limited by the cell INDEX range rather than by
/// float precision.
/// </summary>
/// <remarks>
/// <para><b>What this fixture is really testing is a precision claim, so every case is written as a comparison against what f32 does.</b> A test that merely
/// asserted "the grid places an entity at 10^9 correctly" would pass just as happily against a build that had never been widened, because the assertion
/// would be computed in the same broken arithmetic as the code. The ablation is the test.</para>
/// <para>Storage is deliberately NOT widened — <c>C15</c> keeps every stored bound f32 and cell-relative, and that is what makes an f32 mantissa enough:
/// 24 bits spans a 1 000-unit cell at ~6 x 10^-5 resolution wherever that cell sits. These tests pin the boundary between the two.</para>
/// </remarks>
[TestFixture]
class F64WorldFrameTests
{
    /// <summary>
    /// Far enough out that one f32 step is WIDER than a cell — which is the threshold that matters, not merely "a big number".
    /// </summary>
    /// <remarks>
    /// f32 keeps 24 bits of mantissa, so its resolution at magnitude <c>m</c> is about <c>m / 2^23</c>. At 2^36 that is 8 192 units against a 1 000-unit
    /// cell, so two points a cell apart collapse onto the same float and the whole 64-cell world folds into one column. At 2^30 — the first magnitude this
    /// fixture tried — the step is only 128 units and f32 still separates the cells perfectly well, which would have made the ablation below pass for the
    /// wrong reason. The precondition assertions exist to catch exactly that drift.
    /// </remarks>
    private const double FarOrigin = 68_719_476_736d;   // 2^36

    private const double CellSize = 1_000d;

    private static SpatialGridConfig FarWorld => new(
        new Vector3D(FarOrigin, FarOrigin, FarOrigin),
        new Vector3D(FarOrigin + (64 * CellSize), FarOrigin + (64 * CellSize), FarOrigin + (64 * CellSize)),
        CellSize);

    /// <summary>
    /// <c>AC-8</c>: at 2^30 units out, points one cell apart resolve to DIFFERENT cells — and the f32 arithmetic that used to compute this cannot tell them
    /// apart.
    /// </summary>
    /// <remarks>
    /// The second half is the part that matters. <c>(float)</c> of these coordinates collapses distinct positions onto the same value, so a grid whose frame
    /// was f32 would file both entities into one cell and every query for the second would answer with the first's neighbours. The assertion that the f32
    /// computation FAILS is what proves the f64 one is doing something.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void PointsOneCellApartAtExtentResolveToDifferentCells()
    {
        var grid = new SpatialGrid(FarWorld);

        // Cell centres of cells 0 and 1 on X.
        double x0 = FarOrigin + (0.5d * CellSize);
        double x1 = FarOrigin + (1.5d * CellSize);
        double y = FarOrigin + (0.5d * CellSize);

        grid.WorldToCellCoords(x0, y, y, out int cx0, out _, out _);
        grid.WorldToCellCoords(x1, y, y, out int cx1, out _, out _);

        Assert.Multiple(() =>
        {
            Assert.That(cx0, Is.EqualTo(0), "the first cell centre must land in cell 0");
            Assert.That(cx1, Is.EqualTo(1), "a point one cell further out must land in cell 1, not be quantised back into cell 0");

            // The ablation: the same floor done in f32, which is what the pre-#914 grid computed.
            int f32Cx0 = (int)MathF.Floor(((float)x0 - (float)FarOrigin) / (float)CellSize);
            int f32Cx1 = (int)MathF.Floor(((float)x1 - (float)FarOrigin) / (float)CellSize);
            Assert.That(f32Cx0, Is.EqualTo(f32Cx1),
                "PRECONDITION: at this magnitude the f32 computation must be unable to separate the two points. If this fires, the fixture has drifted to a "
                + "magnitude f32 can still represent and the test above proves nothing.");
        });
    }

    /// <summary>A cell origin at extent is exact in f64 and quantised in f32 — the value every <c>C15</c> bound is measured from.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void CellOriginIsExactAtExtent()
    {
        var grid = new SpatialGrid(FarWorld);
        int key = grid.ComputeCellKey(7, 3, 1);
        grid.CellOrigin(key, out double ox, out double oy, out double oz);

        Assert.Multiple(() =>
        {
            Assert.That(ox, Is.EqualTo(FarOrigin + (7 * CellSize)), "the X origin must be exact, not rounded to the nearest representable f32");
            Assert.That(oy, Is.EqualTo(FarOrigin + (3 * CellSize)));
            Assert.That(oz, Is.EqualTo(FarOrigin + (1 * CellSize)));

            Assert.That((double)(float)ox, Is.Not.EqualTo(ox),
                "PRECONDITION: this origin must be one f32 cannot hold exactly, or the test is not measuring the widening.");
        });
    }

    /// <summary>
    /// A cell-relative bound stays f32 and stays tight at extent — the other half of the floating-origin bargain.
    /// </summary>
    /// <remarks>
    /// This is what makes <c>C15</c> affordable rather than merely stated: the entity's OFFSET within its cell is at most one cell wide, so f32 holds it to
    /// ~6 x 10^-5 units no matter how far from the origin the cell is. Conversion still rounds outward, so <c>CA-01</c> holds.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void CellRelativeBoundsStayTightAtExtent()
    {
        var grid = new SpatialGrid(FarWorld);
        int key = grid.ComputeCellKey(11, 0, 0);
        grid.CellOrigin(key, out double ox, out _, out _);

        // A point 250.375 units into the cell — a value f32 represents exactly, so any error is the frame's, not the sample's.
        double world = ox + 250.375d;

        float relMin = ClusterSpatialAabb.ToCellRelativeMin(world, ox);
        float relMax = ClusterSpatialAabb.ToCellRelativeMax(world, ox);

        Assert.Multiple(() =>
        {
            Assert.That(relMin, Is.LessThanOrEqualTo(250.375f).Within(0.001f), "the lower bound must not round INTO the entity — that is CA-01");
            Assert.That(relMax, Is.GreaterThanOrEqualTo(250.375f).Within(0.001f));
            Assert.That(relMax - relMin, Is.LessThan(0.01f),
                "the cell-relative bound must stay tight at extent; that tightness is the entire reason storage can remain f32");
        });
    }

    /// <summary>An ordinary f32-sized world is unchanged by the widening — the regression guard for every existing caller.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void SmallWorldBehavesExactlyAsBefore()
    {
        var grid = new SpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(1000, 1000), cellSize: 100d));

        grid.WorldToCellCoords(250f, 750f, 0f, out int cx, out int cy, out int cz);
        int key = grid.ComputeCellKey(cx, cy, cz);
        grid.CellOrigin(key, out double ox, out double oy, out double oz);

        Assert.Multiple(() =>
        {
            Assert.That((cx, cy, cz), Is.EqualTo((2, 7, 0)));
            Assert.That(ox, Is.EqualTo(200d));
            Assert.That(oy, Is.EqualTo(700d));
            Assert.That(oz, Is.EqualTo(0d));
        });
    }
}
