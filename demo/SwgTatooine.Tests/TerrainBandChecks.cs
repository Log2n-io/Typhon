using System;
using NUnit.Framework;
using SwgTatooine.World.Terrain;

namespace SwgTatooine.Tests;

/// <summary>
/// Baking the planet in horizontal bands, one per core.
/// </summary>
/// <remarks>
/// <para>
/// The whole of this is worth one property: <b>a band must produce the same heights as the whole grid, exactly</b>. If it
/// does not, the failure is a seam along every band boundary — a line of subtly wrong rock on steep ground, which no
/// summary statistic shows and which moves with the machine's core count, so it would reproduce for some people and not
/// others. Everything else here exists to make that property hold or to say why it might not.
/// </para>
/// <para>
/// Measured at the shipping 4096 posts on a 7950X: <b>5 274 ms serial, 322 ms on 32 threads</b>, zero posts differing.
/// </para>
/// </remarks>
[TestFixture]
public sealed class TerrainBandChecks
{
    private const int Posts = 512;
    private const double Spacing = 16384.0 / Posts;

    [Test]
    public void BandRanges_CoverEveryRowOnce_AndAreEvenToWithinOneRow()
    {
        foreach ((int posts, int bands) in new[] { (4096, 8), (4096, 1), (4096, 6), (255, 8), (7, 3), (4, 16) })
        {
            (int RowOffset, int Rows)[] ranges = BandBake.BandRanges(posts, bands);
            int at = 0;
            int smallest = int.MaxValue;
            int largest = 0;
            foreach ((int rowOffset, int rows) in ranges)
            {
                Assert.That(rowOffset, Is.EqualTo(at), $"{posts} over {bands}");
                Assert.That(rows, Is.GreaterThan(0), "an empty band would throw when its window is made");
                at += rows;
                smallest = Math.Min(smallest, rows);
                largest = Math.Max(largest, rows);
            }

            Assert.That(at, Is.EqualTo(posts), $"{posts} over {bands}");
            Assert.That(largest - smallest, Is.LessThanOrEqualTo(1));
        }
    }

    [Test]
    public void HaloRows_IsOnePerSlopeFilteredLayer()
    {
        // One per slope-filtered layer, not one: a slope filter reads the accumulator one row either side, so the layer
        // BEFORE it has to be correct one row further out again, and so on down the tree.
        Assert.That(TerrainLayers.HaloRows(TatooineTerrain.LandformLayers()), Is.EqualTo(3), "mesa strata, highland strata, cliff detail");
        Assert.That(TerrainLayers.HaloRows([]), Is.EqualTo(0));
    }

    [Test]
    public void ABandedBake_ReproducesTheWholeGridBakePostForPost()
    {
        Heightfield whole = new(HeightGrid.Create(Posts, Spacing, -8192));
        TatooineTerrain.Bake(whole);

        foreach (int workers in new[] { 1, 2, 3, 8 })
        {
            Heightfield banded = new(HeightGrid.Create(Posts, Spacing, -8192));
            BandBake.Bake(banded, workers: workers);
            int differing = 0;
            for (int i = 0; i < whole.Grid.Height.Length; i++)
            {
                if (whole.Grid.Height[i] != banded.Grid.Height[i])
                {
                    differing++;
                }
            }

            Assert.That(differing, Is.Zero, $"{workers} workers");
            Assert.That(banded.MinHeightM, Is.EqualTo(whole.MinHeightM));
            Assert.That(banded.MaxHeightM, Is.EqualTo(whole.MaxHeightM));
        }
    }

    [Test]
    public void AWindowsOutermostRow_ReadsOutsideItself_AndSaysSoAsNaN()
    {
        // The flaw the C# port turned into a crash and JavaScript hides. A band's outermost row takes a central
        // difference across a row the window does not hold; the client reads past its array as `undefined` and gets NaN,
        // and the halo is sized so every row poisoned that way is discarded. Asserted so that "fixing" it by clamping —
        // which would silently disagree with the client — fails here.
        HeightGrid window = HeightGrid.Window(16, 10, 0, 8, 4);
        Assert.That(double.IsNaN(TerrainLayers.SlopeAtPost(window, 5, 8)), Is.True, "the window's first row");
        Assert.That(double.IsNaN(TerrainLayers.SlopeAtPost(window, 5, 11)), Is.True, "the window's last row");
        Assert.That(double.IsNaN(TerrainLayers.SlopeAtPost(window, 5, 9)), Is.False, "an interior row is answerable");

        // The planet's own rim is different: there the one-sided difference IS the answer, and a whole grid never NaNs.
        HeightGrid wholeGrid = HeightGrid.Create(16, 10, 0);
        Assert.That(double.IsNaN(TerrainLayers.SlopeAtPost(wholeGrid, 5, 0)), Is.False, "the planet's edge is answerable");
        Assert.That(double.IsNaN(TerrainLayers.SlopeAtPost(wholeGrid, 5, 15)), Is.False);
    }
}
