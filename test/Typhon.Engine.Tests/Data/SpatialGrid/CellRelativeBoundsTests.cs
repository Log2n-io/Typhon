using System;
using System.Numerics;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// The <c>C15</c> world → cell-relative conversion (#872 step 9): does the directed rounding in
/// <see cref="ClusterSpatialAabb.ToCellRelativeMin"/> / <see cref="ClusterSpatialAabb.ToCellRelativeMax"/> ever actually change a value, and is it always
/// conservative when it does?
/// </summary>
/// <remarks>
/// Two different questions, and only the second is a correctness property. The first is a MEASUREMENT, kept as a test because the answer decides whether the
/// rounding is load-bearing or insurance — and that claim is currently made in <see cref="ClusterSpatialAabb"/>'s own remarks, where a wrong claim would
/// outlive anyone's memory of checking it.
/// </remarks>
[TestFixture]
class CellRelativeBoundsTests
{
    /// <summary>
    /// Containment is the invariant: a converted MIN must never sit above the value it came from, and a converted MAX never below it, once both are compared
    /// in the same frame. This is <c>CA-01</c>'s silent failure mode expressed at the one arithmetic step that can cause it.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CA-01")]
    public void Conversion_IsAlwaysConservative_AcrossMagnitudesAndOrigins()
    {
        var rng = new Random(515151);
        int minWidened = 0;
        int maxWidened = 0;
        int total = 0;

        foreach (double origin in new[] { 0d, 0.1d, 100d, 1_000d, 100_000d, 1_000_000d, 16_777_216d, -1_000_000d })
        {
            for (int i = 0; i < 20_000; i++)
            {
                // A coordinate anywhere from inside the cell to far outside it: the query box conversion is not bounded by the cell, only the stored bound is.
                double offset = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(0, 8));
                double world = origin + offset;

                float lo = ClusterSpatialAabb.ToCellRelativeMin(world, origin);
                float hi = ClusterSpatialAabb.ToCellRelativeMax(world, origin);
                total++;

                double exact = world - origin;
                Assert.That((double)lo, Is.LessThanOrEqualTo(exact), $"min rounded INTO the value: origin={origin} world={world}");
                Assert.That((double)hi, Is.GreaterThanOrEqualTo(exact), $"max rounded INTO the value: origin={origin} world={world}");

                if (lo != (float)exact) { minWidened++; }
                if (hi != (float)exact) { maxWidened++; }
            }
        }

        TestContext.Out.WriteLine($"C15ROUNDING samples={total} minWidened={minWidened} maxWidened={maxWidened} "
            + $"({100.0 * (minWidened + maxWidened) / (2.0 * total):F2}% of conversions moved a bound)");

        // Deliberately not asserted as non-zero. If it ever measures zero for every configuration the engine can produce, that is a finding about the
        // rounding being insurance rather than a defect in this test — and the printed number is how anyone would find out.
        Assert.Pass($"conservative on {total} samples per bound");
    }

    /// <summary>
    /// The same conversion applied to a bound and to a query box must not open a gap between them: a box that touches a bound exactly in world space must
    /// still touch it after both sides are converted. This is the <c>SQ-01</c> half — the storage-side test above cannot see it.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("SQ-01")]
    public void QueryBoxAndStoredBound_StillOverlapAfterConversion_WhenTheyTouchInWorldSpace()
    {
        var rng = new Random(626262);
        foreach (double origin in new[] { 0d, 0.1d, 1_000d, 1_000_000d, -1_000_000d })
        {
            for (int i = 0; i < 20_000; i++)
            {
                // An entity bound and a query box that share an edge exactly. In world space the overlap test passes on equality; it must still pass after
                // the two sides go through OPPOSITE-signed rounding — the bound inward-safe, the box outward-safe.
                double edge = origin + ((rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(0, 8)));
                double boundMax = edge;
                double queryMin = edge;

                float storedMax = ClusterSpatialAabb.ToCellRelativeMax(boundMax, origin);
                float cellQueryMin = ClusterSpatialAabb.ToCellRelativeMin(queryMin, origin);

                // The broadphase rejects with `cMaxX < queryMinX`. Touching must not reject.
                Assert.That(storedMax, Is.GreaterThanOrEqualTo(cellQueryMin),
                    $"a touching pair separated under conversion — SQ-01 false negative. origin={origin} edge={edge}");
            }
        }
    }

    /// <summary>
    /// <see cref="SpatialGrid.CellOrigin"/> must agree with the grid's own idea of where a cell starts, or every bound in that cell is measured from the
    /// wrong place while remaining perfectly self-consistent — the failure mode that produces correct-looking, uniformly-shifted results.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void CellOrigin_MatchesTheCellTheCoordinateResolvesTo()
    {
        var cfg = new SpatialGridConfig(new Vector3(-500f, -500f, -500f), new Vector3(1500f, 1500f, 1500f), cellSize: 100f);
        var grid = new SpatialGrid(cfg);

        var rng = new Random(737373);
        for (int i = 0; i < 2_000; i++)
        {
            float x = -500f + ((float)rng.NextDouble() * 2000f);
            float y = -500f + ((float)rng.NextDouble() * 2000f);
            float z = -500f + ((float)rng.NextDouble() * 2000f);

            int key = grid.ComputeCellKey(
                Math.Clamp((int)Math.Floor((x - cfg.WorldMin.X) / cfg.CellSize), 0, cfg.GridWidth - 1),
                Math.Clamp((int)Math.Floor((y - cfg.WorldMin.Y) / cfg.CellSize), 0, cfg.GridHeight - 1),
                Math.Clamp((int)Math.Floor((z - cfg.WorldMin.Z) / cfg.CellSize), 0, cfg.GridDepth - 1));

            grid.CellOrigin(key, out double ox, out double oy, out double oz);
            var (cx, cy, cz) = grid.CellKeyToCoords(key);

            Assert.Multiple(() =>
            {
                Assert.That(ox, Is.EqualTo(cfg.WorldMin.X + (cx * cfg.CellSize)).Within(0.001f));
                Assert.That(oy, Is.EqualTo(cfg.WorldMin.Y + (cy * cfg.CellSize)).Within(0.001f));
                Assert.That(oz, Is.EqualTo(cfg.WorldMin.Z + (cz * cfg.CellSize)).Within(0.001f));
            });

            // And the point really is inside the box the origin opens — the property every converted bound depends on.
            Assert.That(x, Is.GreaterThanOrEqualTo(ox - 0.001f).And.LessThanOrEqualTo(ox + cfg.CellSize + 0.001f));
        }
    }
}
