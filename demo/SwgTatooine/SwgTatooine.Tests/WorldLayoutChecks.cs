using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// The layout claim as a player meets it: no two structures anywhere in a finished world stand inside each other, at any
/// world size.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the case <see cref="SitePlannerChecks"/> could not make, and the gap was not academic.</b> Those cases ask
/// a planner whether the structures IT placed collide, which is the right question for a planner and the wrong one for a
/// world: every structure was planted by one of four code paths — city, point of interest, player-city knot, desert —
/// and while each had its own planner, nothing compared a house against the city it was standing in. A finished
/// 16.384 km world held 74 overlapping pairs with every per-site planner reporting success.
/// </para>
/// <para>
/// <b>Read from the entities, not from the planner.</b> The boxes tested here are the ones in
/// <c>WorldObject.Bounds</c> — what the spatial index answers with, what the client draws, and what a collision test
/// would consult — so a plan that is right about a spot the entity did not end up at cannot pass.
/// </para>
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class WorldLayoutChecks
{
    /// <summary>Every structure's box in a realm, read through the clusters rather than through any index.</summary>
    /// <param name="sim">The world.</param>
    /// <returns>Centre and half-extent per structure, with its kind.</returns>
    private static List<(float X, float Z, float Half, int Kind)> Structures(TatooineSim sim)
    {
        var boxes = new List<(float X, float Z, float Half, int Kind)>();
        using var tx = sim.Dbe.CreateQuickTransaction();
        foreach (var cluster in tx.GetClusterEnumerator<WorldObject>())
        {
            var places = cluster.GetReadOnlySpan(WorldObject.Bounds);
            var facts = cluster.GetReadOnlySpan(WorldObject.Struct);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                var b = places[idx].Bounds;
                boxes.Add(((b.MinX + b.MaxX) * 0.5f, (b.MinY + b.MaxY) * 0.5f, (b.MaxX - b.MinX) * 0.5f, facts[idx].Kind));
            }
        }

        return boxes;
    }

    /// <summary>Overlapping pairs, found through a uniform grid so that a whole planet is not a quadratic scan.</summary>
    /// <param name="boxes">What was planted.</param>
    /// <returns>One description per overlapping pair.</returns>
    private static List<string> OverlappingPairs(List<(float X, float Z, float Half, int Kind)> boxes)
    {
        // 64 m: wider than the largest footprint (the 32 m dome), so a pair that overlaps shares a cell with its neighbour's 3 x 3 block.
        const float Cell = 64f;
        var cells = new Dictionary<long, List<int>>();
        for (var i = 0; i < boxes.Count; i++)
        {
            var cx = (int)MathF.Floor(boxes[i].X / Cell);
            var cz = (int)MathF.Floor(boxes[i].Z / Cell);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    var key = ((long)(cx + dx) << 32) ^ (uint)(cz + dz);
                    if (!cells.TryGetValue(key, out var bucket))
                    {
                        cells[key] = bucket = [];
                    }

                    bucket.Add(i);
                }
            }
        }

        var seen = new HashSet<(int, int)>();
        var found = new List<string>();
        foreach (var bucket in cells.Values)
        {
            for (var a = 0; a < bucket.Count; a++)
            {
                for (var b = a + 1; b < bucket.Count; b++)
                {
                    var i = Math.Min(bucket[a], bucket[b]);
                    var j = Math.Max(bucket[a], bucket[b]);

                    // The drawn boxes, with no margin: two structures may stand closer than the planner's street and still not overlap, and it is
                    // overlap — one building you can walk into because another is inside it — that is the defect.
                    var reach = boxes[i].Half + boxes[j].Half;
                    if (MathF.Abs(boxes[i].X - boxes[j].X) < reach && MathF.Abs(boxes[i].Z - boxes[j].Z) < reach && seen.Add((i, j)))
                    {
                        found.Add($"kind {boxes[i].Kind} at ({boxes[i].X:F0}, {boxes[i].Z:F0}) r{boxes[i].Half:F0} "
                            + $"inside kind {boxes[j].Kind} at ({boxes[j].X:F0}, {boxes[j].Z:F0}) r{boxes[j].Half:F0}");
                    }
                }
            }
        }

        return found;
    }

    /// <summary>A world built at a given size, with every structure it holds.</summary>
    /// <param name="km">The planet edge.</param>
    /// <param name="assert">What to check.</param>
    private static void InAWorldOf(float km, Action<TatooineSim, List<(float X, float Z, float Half, int Kind)>> assert)
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.WorldEdgeKm = km;
            config.WarmTicks = 0;
            config.MeasuredTicks = 0;
            using var sim = new TatooineSim(config);
            sim.Initialize();
            assert(sim, Structures(sim));
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    /// <summary>The real planet: nothing overlaps, and nothing was dropped to achieve it.</summary>
    [Test]
    public void AtTheRealPlanetSize_NothingOverlapsAndNothingIsClamped()
    {
        InAWorldOf(TatooineData.PlanetEdgeM / 1000f, (sim, boxes) =>
        {
            var overlaps = OverlappingPairs(boxes);
            Assert.Multiple(() =>
            {
                Assert.That(boxes, Is.Not.Empty, "the world has no structures, so this proves nothing");
                Assert.That(overlaps, Is.Empty, $"{overlaps.Count} structures stand inside another: {string.Join("; ", overlaps.GetRange(0, Math.Min(5, overlaps.Count)))}");
                Assert.That(sim.Map.ClampedSites, Is.Zero, "the authentic map had a site capped, so its building counts no longer match the game's");
                Assert.That(sim.Map.ClampedStructures, Is.Zero);
            });
        });
    }

    /// <summary>
    /// A compressed world: still nothing overlaps, because the counts were capped to what the shrunken discs hold.
    /// </summary>
    /// <remarks>
    /// <c>--world 2</c> is the case that exposed all of this. It scales every city's radius by 0.122 and leaves its building count alone, so Mos Eisley is
    /// asked to put 190 structures in a 55.7 m disc — 780 % of the ground. Before the cap the planner searched 256 times for each of them, printed a
    /// warning per city and planted them overlapping anyway.
    /// </remarks>
    [Test]
    public void AtATwoKilometrePlanet_TheCountsAreCappedAndNothingOverlaps()
    {
        InAWorldOf(2f, (sim, boxes) =>
        {
            var overlaps = OverlappingPairs(boxes);
            Assert.Multiple(() =>
            {
                Assert.That(overlaps, Is.Empty, $"{overlaps.Count} structures stand inside another: {string.Join("; ", overlaps.GetRange(0, Math.Min(5, overlaps.Count)))}");
                Assert.That(sim.Map.ClampedSites, Is.GreaterThan(0), "a 2 km planet cannot hold the real map's structures and nothing said so");
                foreach (var city in sim.Map.Cities)
                {
                    Assert.That(city.Buildings, Is.LessThanOrEqualTo(StructureFootprint.CapacityOf(city.Radius, StructureKind.Building)),
                        $"{city.Name} is still asked for more than its disc holds");
                    Assert.That(city.Buildings, Is.GreaterThan(0), $"{city.Name} was capped out of existence; a city with no shuttleport is unreachable");
                }
            });
        });
    }

    /// <summary>Capacity is the measured ceiling, and it never answers zero.</summary>
    /// <remarks>
    /// The floor of one is load-bearing rather than defensive: the first structure a city plants is its shuttleport, and the only transport in the world
    /// routes through those. A disc too small for one gets it anyway and lets it overhang.
    /// </remarks>
    [Test]
    public void CapacityFollowsTheMeasuredCeiling_AndIsNeverZero()
    {
        var span = (2f * StructureFootprint.HalfExtentM(StructureKind.Building)) + StructureFootprint.MarginM;
        Assert.Multiple(() =>
        {
            Assert.That(StructureFootprint.CapacityOf(0.5f, StructureKind.Building), Is.EqualTo(1), "a disc smaller than one building holds no city at all");
            Assert.That(StructureFootprint.CapacityOf(0f, StructureKind.Shuttleport), Is.EqualTo(1));

            // 125 m is Anchorhead's, the tightest city on the map: it asks for 55 and the ceiling has to leave it room.
            Assert.That(StructureFootprint.CapacityOf(125f, StructureKind.Building), Is.GreaterThanOrEqualTo(55), "the tightest authentic city no longer fits its own capacity");

            // And the figure is the packing ceiling applied to the area, not something else that happens to be bigger than 55.
            var expected = (int)(StructureFootprint.MaxPacking * MathF.PI * 125f * 125f / (span * span));
            Assert.That(StructureFootprint.CapacityOf(125f, StructureKind.Building), Is.EqualTo(expected));
            Assert.That(StructureFootprint.MaxPacking, Is.LessThan(0.489f), "the packing ceiling is past the 48.9 % that was measured to place cleanly");
        });
    }

    /// <summary>A site with no room says so without recording the structure, which is what lets the caller put it elsewhere.</summary>
    /// <remarks>
    /// The distinction between this and <see cref="SitePlanner.Place"/> is the whole reason the player-city knots no longer overlap. <c>Place</c> plants the
    /// structure wherever the last draw fell and counts it as crowded, because a city's buildings have nowhere else to be. A player's house does: the
    /// desert. So the knot is asked, and a refusal has to leave the planner exactly as it was.
    /// </remarks>
    [Test]
    public void AFullSiteRefusesWithoutRecordingAnything()
    {
        var rng = new Rng(17u);
        var plan = new SitePlanner(StructureFootprint.HalfExtentM(StructureKind.Building));
        var half = StructureFootprint.HalfExtentM(StructureKind.Building);

        // A disc that holds exactly one building: 10 m against an 18 m footprint.
        Assert.That(plan.TryPlace(ref rng, 0f, 0f, 10f, half, out _, out _), Is.True, "the first structure did not fit in an empty site");
        Assert.That(plan.Count, Is.EqualTo(1));

        var countBefore = plan.Count;
        var crowdedBefore = plan.Crowded;
        Assert.Multiple(() =>
        {
            Assert.That(plan.TryPlace(ref rng, 0f, 0f, 10f, half, out _, out _), Is.False, "a 10 m disc took a second 18 m building");
            Assert.That(plan.Count, Is.EqualTo(countBefore), "a refused structure was recorded anyway, so the site now believes something is there");
            Assert.That(plan.Crowded, Is.EqualTo(crowdedBefore), "a refusal was counted as a crowded placement, which is Place's business and not this one's");
        });
    }

    /// <summary>A spot the caller drew is vetoed when it overlaps and recorded when it does not.</summary>
    /// <remarks>
    /// The lone structures out in the desert cannot come from a disc — <c>WorldBuilder.RandomWildernessPoint</c> rejects points inside a city's keep-out —
    /// so the planner has to be able to judge a candidate it did not produce.
    /// </remarks>
    [Test]
    public void ACallerDrawnSpotIsVetoedOnlyWhenItOverlaps()
    {
        var plan = new SitePlanner(StructureFootprint.HalfExtentM(StructureKind.Building));
        var half = StructureFootprint.HalfExtentM(StructureKind.Building);

        Assert.Multiple(() =>
        {
            Assert.That(plan.TryPlaceAt(0f, 0f, half), Is.True, "an empty planet refused the first structure");
            Assert.That(plan.TryPlaceAt(4f, 0f, half), Is.False, "a structure 4 m from an 18 m building was accepted");
            Assert.That(plan.TryPlaceAt(0f, 2000f, half), Is.True, "a structure two kilometres away was refused");
            Assert.That(plan.Count, Is.EqualTo(2), "the vetoed candidate was recorded");
        });
    }
}
