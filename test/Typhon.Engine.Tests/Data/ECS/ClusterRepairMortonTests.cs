using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The intra-cell Morton encoder the repair path sorts on (#872 step 12).
/// </summary>
/// <remarks>
/// <para><b>Tested apart from the fence because its failures are silent there.</b> A wrong Morton key produces a sort
/// that is still a total order, so the re-pack completes, every invariant holds, and the only symptom is that the
/// clusters come out no tighter than they went in. That reads as "the repair is not very good" rather than as a defect,
/// which is exactly how a broken bit-spread survives a suite of end-to-end assertions.</para>
/// <para><b>Not the Morton step 8 deleted.</b> That one keyed the grid and was removed because 32-bit 3D codes cap the
/// world at 1 024 cells per axis. This one orders entities inside a single cell, where the coordinate range is one cell
/// by construction.</para>
/// </remarks>
[TestFixture]
class ClusterRepairMortonTests
{
    /// <summary>A cell 100 units on a side, matching the spatial fixtures.</summary>
    private const float CellSize = 100f;

    private const float InverseCellSize = 1f / CellSize;

    private static ulong Key(float x, float y, float z = 0f) =>
        ArchetypeClusterState.EncodeIntraCellMorton(x, y, z, InverseCellSize);

    /// <summary>The origin maps to zero, and the far corner to the all-ones key — the two ends of the curve.</summary>
    [Test]
    public void TheCurveSpansTheWholeCell()
    {
        Assert.That(Key(0f, 0f, 0f), Is.Zero);

        // 63 bits set: 21 per axis, interleaved. Anything less means an axis is not reaching its maximum.
        const ulong allOnes = (1UL << 63) - 1UL;
        Assert.That(Key(CellSize, CellSize, CellSize), Is.EqualTo(allOnes));
    }

    /// <summary>
    /// Positions outside the cell CLAMP into it rather than wrapping.
    /// </summary>
    /// <remarks>
    /// <b>The single most damaging thing this encoder could get wrong.</b> Migration hysteresis leaves an entity in its
    /// old cell for a dead zone past the boundary, so a cell legitimately holds points slightly outside its own extent.
    /// A quantiser that let those overflow would place a point just past one face at the opposite corner of the sort
    /// order, dragging a whole cluster's bound across the cell — the re-pack would then produce a layout WORSE than the
    /// one it replaced, and nothing downstream would notice.
    /// </remarks>
    [Test]
    public void OutOfCellPositionsClampRatherThanWrap()
    {
        var justInside = Key(CellSize - 0.01f, CellSize - 0.01f);
        var justOutside = Key(CellSize + 5f, CellSize + 5f);
        var farOutside = Key(CellSize * 10f, CellSize * 10f);

        Assert.That(justOutside, Is.GreaterThanOrEqualTo(justInside), "a point past the far face must not wrap below one inside it");
        Assert.That(farOutside, Is.EqualTo(justOutside), "everything past the far face clamps to the same maximum");

        Assert.That(Key(-5f, -5f), Is.Zero, "a point before the near face clamps to the origin, not to a huge key");
        Assert.That(Key(-5f, 50f), Is.EqualTo(Key(0f, 50f)), "clamping is per axis; the other axis must be unaffected");
    }

    /// <summary>Each axis moves its own third of the bits, and only its own.</summary>
    /// <remarks>
    /// A spread that overlapped two axes would still produce a monotone-looking key for motion along one axis, so the
    /// ordering test below would pass while the interleave was wrong. Masking is what separates the two questions.
    /// </remarks>
    [Test]
    public void EachAxisOwnsItsOwnBitPlane()
    {
        const ulong xPlane = 0x1249249249249249UL;
        const ulong yPlane = xPlane << 1;
        const ulong zPlane = xPlane << 2;

        var xOnly = Key(CellSize, 0f, 0f);
        var yOnly = Key(0f, CellSize, 0f);
        var zOnly = Key(0f, 0f, CellSize);

        Assert.That(xOnly & ~xPlane, Is.Zero, "X leaked into another axis' bits");
        Assert.That(yOnly & ~yPlane, Is.Zero, "Y leaked into another axis' bits");
        Assert.That(zOnly & ~zPlane, Is.Zero, "Z leaked into another axis' bits");

        Assert.That(xOnly | yOnly | zOnly, Is.EqualTo((1UL << 63) - 1UL), "the three planes must partition the 63 bits with none missing");
    }

    /// <summary>
    /// Sorting by the key groups points that are near each other in space — the property the whole re-pack rests on.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured as a locality ratio, not as an exact ordering.</b> A Morton curve does not order by distance —
    /// it has long jumps at every power-of-two boundary — so no assertion of the form "consecutive keys are close" can
    /// hold. What does hold, and what the re-pack needs, is that a contiguous RUN of the curve has a much smaller
    /// bounding box than a random selection of the same size. That is the quantity compared here.</para>
    /// <para>The random arm uses a fixed seed. The comparison is between two partitions of the same point set, so a
    /// seeded reference is not hiding variance — it is holding the control constant while the treatment is measured.</para>
    /// </remarks>
    [Test]
    public void SortingByTheKeyGroupsNeighbours()
    {
        const int count = 2048;
        const int groupSize = 49;

        var rng = new Random(20260903);
        var points = new List<(float x, float y, ulong key)>(count);
        for (var i = 0; i < count; i++)
        {
            var x = (float)rng.NextDouble() * CellSize;
            var y = (float)rng.NextDouble() * CellSize;
            points.Add((x, y, Key(x, y)));
        }

        var shuffled = new List<(float x, float y, ulong key)>(points);
        var sorted = new List<(float x, float y, ulong key)>(points);
        sorted.Sort(static (a, b) => a.key.CompareTo(b.key));

        var sortedMean = MeanGroupExtent(sorted, groupSize);
        var shuffledMean = MeanGroupExtent(shuffled, groupSize);

        Assert.That(sortedMean, Is.LessThan(shuffledMean * 0.5),
            $"Morton-sorted groups of {groupSize} have mean extent {sortedMean:F2} against {shuffledMean:F2} for the unsorted order — "
            + "the key is not producing locality");

        TestContext.Out.WriteLine($"locality: sorted {sortedMean:F2} vs unsorted {shuffledMean:F2} "
            + $"({shuffledMean / sortedMean:F2}x tighter), optimum {CellSize / Math.Sqrt((double)count / groupSize):F2}");
    }

    /// <summary>Mean of the maximum axis extent over consecutive groups of <paramref name="groupSize"/>.</summary>
    private static double MeanGroupExtent(List<(float x, float y, ulong key)> points, int groupSize)
    {
        var total = 0d;
        var groups = 0;
        for (var start = 0; start < points.Count; start += groupSize)
        {
            var end = Math.Min(start + groupSize, points.Count);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (var i = start; i < end; i++)
            {
                minX = MathF.Min(minX, points[i].x);
                minY = MathF.Min(minY, points[i].y);
                maxX = MathF.Max(maxX, points[i].x);
                maxY = MathF.Max(maxY, points[i].y);
            }

            total += MathF.Max(maxX - minX, maxY - minY);
            groups++;
        }

        return total / groups;
    }
}
