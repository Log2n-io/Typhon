using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #886 lead D. The Prep head orders each shadow field's entries once (<see cref="FieldShadowBuffer.BuildDrainPlan"/>) and a slice takes the contiguous
/// range of that order for its own clusters (<see cref="FieldShadowBuffer.DrainOrderForClusters"/>). These pin the range boundaries, which nothing else
/// tests directly: the non-quarantined equivalence arms only see ranges that happen to hold crossings.
/// </summary>
[TestFixture]
class ShadowDrainPlanTests
{
    private static FieldShadowBuffer Fill(Random rng, int count, int minCluster, int maxClusterInclusive)
    {
        var buffer = new FieldShadowBuffer();
        for (var e = 0; e < count; e++)
        {
            var cluster = rng.Next(minCluster, maxClusterInclusive + 1);
            buffer.Append((cluster << 6) + rng.Next(64), default, default);
        }

        return buffer;
    }

    private static int[] FullOrder(FieldShadowBuffer buffer)
    {
        int[] order = [];
        int[] counts = [];
        return ArchetypeClusterState.BuildDrainOrder(buffer, buffer.Count, ref order, ref counts).ToArray();
    }

    private static int[] Concatenate(FieldShadowBuffer buffer, int width, int upTo)
    {
        var all = new List<int>();
        for (var start = 0; start < upTo; start += width)
        {
            all.AddRange(buffer.DrainOrderForClusters(start, start + width).ToArray());
        }

        return all.ToArray();
    }

    [Test]
    public void DisjointRanges_ConcatenateToExactlyTheFullOrder([Values(1, 7, 16, 128)] int width)
    {
        var buffer = Fill(new Random(886), 800, 3, 70);
        buffer.BuildDrainPlan();
        Assert.That(Concatenate(buffer, width, 200), Is.EqualTo(FullOrder(buffer)),
            "ranges tiled over the id space must yield the drain order the atomic path builds, element for element — including the within-cluster order");
    }

    [Test]
    public void RangesOutsideThePlan_AreEmpty_AndTheWholeSpaceIsTheWholePlan()
    {
        var buffer = Fill(new Random(7), 300, 10, 20);
        buffer.BuildDrainPlan();
        Assert.Multiple(() =>
        {
            Assert.That(buffer.DrainOrderForClusters(0, 10).Length, Is.EqualTo(0), "below the plan's first cluster");
            Assert.That(buffer.DrainOrderForClusters(21, 1000).Length, Is.EqualTo(0), "above the plan's last cluster");
            Assert.That(buffer.DrainOrderForClusters(0, int.MaxValue).ToArray(), Is.EqualTo(FullOrder(buffer)), "the whole space");
            Assert.That(buffer.DrainOrderForClusters(15, 15).Length, Is.EqualTo(0), "an empty range");
            Assert.That(buffer.DrainOrderForClusters(20, 21).Length, Is.GreaterThan(0), "the last cluster alone is reachable");
        });
    }

    [Test]
    public void EmptyBuffer_HasNoPlan()
    {
        var buffer = new FieldShadowBuffer();
        buffer.BuildDrainPlan();
        Assert.That(buffer.DrainOrderForClusters(0, int.MaxValue).Length, Is.EqualTo(0));
    }

    /// <summary>A build on top of a plan that was never cleared — a tick whose tail did not run — must start from a clean histogram.</summary>
    [Test]
    public void RebuildWithoutClear_IsTheSameAsAFreshBuild()
    {
        var rng = new Random(42);
        var buffer = Fill(rng, 500, 0, 40);
        buffer.BuildDrainPlan();
        for (var e = 0; e < 300; e++)
        {
            buffer.Append((rng.Next(0, 60) << 6) + rng.Next(64), default, default);
        }

        buffer.BuildDrainPlan();
        Assert.That(buffer.DrainOrderForClusters(0, int.MaxValue).ToArray(), Is.EqualTo(FullOrder(buffer)));

        buffer.ClearDrainPlan();
        buffer.Reset();
        buffer.Append(5 << 6, default, default);
        buffer.BuildDrainPlan();
        Assert.That(buffer.DrainOrderForClusters(0, int.MaxValue).ToArray(), Is.EqualTo(new[] { 0 }), "after a clear and a reset the plan is the new buffer's");
    }
}
