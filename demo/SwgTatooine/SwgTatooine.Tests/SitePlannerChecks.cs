using System;
using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// Structures are planted without standing inside each other.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this replaced was visible from the first second in the browser and invisible to every test.</b> Each
/// structure was an independent <c>PointInDisc</c> draw, so a city of 190 buildings put several of them inside one
/// another. Nothing asserted on it because nothing but the renderer ever looked at two structures at once.
/// </para>
/// <para>
/// <b>These cases are on the planner, not on a built world</b>, because building a world is a 2 s transaction per city
/// and the question — does any pair overlap — is a property of the planner alone. The real map's densities are used as
/// the inputs so that a case fails when a city grows past what its radius can hold.
/// </para>
/// </remarks>
[TestFixture]
public sealed class SitePlannerChecks
{
    /// <summary>Every pair of placed boxes, tested the way the planner claims they are separated.</summary>
    /// <param name="placed">What was planted.</param>
    /// <returns>How many pairs overlap.</returns>
    private static int OverlappingPairs(List<(float X, float Z, float Half)> placed)
    {
        var overlaps = 0;
        for (var i = 0; i < placed.Count; i++)
        {
            for (var j = i + 1; j < placed.Count; j++)
            {
                // The drawn boxes themselves, with no margin: two structures may be closer than the margin asks and still
                // not overlap, and it is overlap that is the defect.
                var span = placed[i].Half + placed[j].Half;
                if (Math.Abs(placed[i].X - placed[j].X) < span && Math.Abs(placed[i].Z - placed[j].Z) < span)
                {
                    overlaps++;
                }
            }
        }

        return overlaps;
    }

    /// <summary>Plants a city's worth of structures and returns them.</summary>
    /// <param name="count">How many.</param>
    /// <param name="radius">The disc.</param>
    /// <param name="seed">The world seed.</param>
    /// <param name="planner">The planner used, for its give-up count.</param>
    /// <returns>The boxes.</returns>
    private static List<(float X, float Z, float Half)> PlantCity(int count, float radius, uint seed, out SitePlanner planner)
    {
        var rng = new Rng(seed);
        planner = new SitePlanner(StructureFootprint.HalfExtentM(StructureKind.Shuttleport));
        var placed = new List<(float X, float Z, float Half)>();
        for (var i = 0; i < count; i++)
        {
            // The builder's own kind mix: the first is the port, every ninth a terminal, the rest buildings.
            var kind = i == 0 ? StructureKind.Shuttleport : (i % 9 == 0 ? StructureKind.Terminal : StructureKind.Building);
            var half = StructureFootprint.HalfExtentM(kind);
            var (x, z, _) = planner.Place(ref rng, 0f, 0f, radius, half);
            placed.Add((x, z, half));
        }

        return placed;
    }

    /// <summary>Mos Eisley: the biggest city on the map, 190 structures in a 456 m disc.</summary>
    [Test]
    public void TheCapitalPlantsEveryStructureWithNoneInsideAnother()
    {
        var placed = PlantCity(190, 456f, 7u, out var planner);

        Assert.Multiple(() =>
        {
            Assert.That(placed, Has.Count.EqualTo(190), "the planner dropped structures the map asked for");
            Assert.That(OverlappingPairs(placed), Is.Zero, "buildings are standing inside each other, which is the defect this exists for");
            Assert.That(planner.Crowded, Is.Zero, "the capital is 9 % packed; giving up there means the planner is broken, not the city full");
        });
    }

    /// <summary>Anchorhead: the tightest city, 55 structures in a 125 m disc — 44.8 % of the ground once the street is counted.</summary>
    /// <remarks>
    /// <b>The case that decides the footprint shape and the attempt cap, and the only one on the map that is close to
    /// anything.</b> With a circumscribed circle per building the exclusion area is 57 %, past the 54.7 % where random
    /// sequential placement of discs jams: the planner would give up on a city that fits perfectly well as squares. At
    /// 44.8 % it places, but near enough to saturation that the cap matters — 64 attempts gave up on 3 seeds in 64,
    /// which is how <see cref="SitePlanner.Attempts"/> came to be measured rather than guessed.
    /// </remarks>
    [Test]
    public void TheTightestCityStillFits()
    {
        var placed = PlantCity(55, 125f, 7u, out var planner);

        Assert.Multiple(() =>
        {
            Assert.That(OverlappingPairs(placed), Is.Zero, "the tightest city overlaps");
            Assert.That(planner.Crowded, Is.Zero, "the tightest city on the map no longer fits; its radius or its building count has to move");
        });
    }

    /// <summary>Every structure lands inside the disc it was planned for.</summary>
    /// <remarks>
    /// A rejection loop that widened its search on failure would place a city's overflow in the desert outside it, which
    /// reads as "the city is bigger than it is" rather than as a defect. The planner redraws inside the same disc.
    /// </remarks>
    [Test]
    public void NothingIsPlantedOutsideTheCity()
    {
        const float Radius = 125f;
        foreach (var box in PlantCity(55, Radius, 11u, out _))
        {
            Assert.That(MathF.Sqrt((box.X * box.X) + (box.Z * box.Z)), Is.LessThanOrEqualTo(Radius + 0.001f), "a structure was planted outside its city");
        }
    }

    /// <summary>The same seed plans the same city, because a world has to be rebuildable.</summary>
    [Test]
    public void TheSameSeedPlansTheSameCity()
    {
        var first = PlantCity(190, 456f, 3u, out _);
        var second = PlantCity(190, 456f, 3u, out _);

        for (var i = 0; i < first.Count; i++)
        {
            Assert.That(second[i].X, Is.EqualTo(first[i].X), $"structure {i} moved between two runs of the same seed");
            Assert.That(second[i].Z, Is.EqualTo(first[i].Z), $"structure {i} moved between two runs of the same seed");
        }
    }

    /// <summary>Two seeds plan different cities, so the previous case is testing the seed and not a constant.</summary>
    [Test]
    public void ADifferentSeedPlansADifferentCity()
    {
        var first = PlantCity(40, 200f, 3u, out _);
        var second = PlantCity(40, 200f, 4u, out _);

        var same = 0;
        for (var i = 0; i < first.Count; i++)
        {
            if (first[i].X == second[i].X && first[i].Z == second[i].Z)
            {
                same++;
            }
        }

        Assert.That(same, Is.LessThan(first.Count), "two seeds planned the identical city, so determinism is a constant rather than the seed");
    }

    /// <summary>A disc asked for more than it can hold says so instead of looping forever.</summary>
    /// <remarks>
    /// The behaviour under impossible input is as load-bearing as the behaviour under possible input: a planner that
    /// searched until it succeeded would hang the whole world build on one bad number in the map.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    public void AnImpossibleCityIsReportedRatherThanSearchedForever()
    {
        // 60 buildings of 18 m in a 40 m disc: about four times the ground there is.
        var placed = PlantCity(60, 40f, 5u, out var planner);

        Assert.Multiple(() =>
        {
            Assert.That(placed, Has.Count.EqualTo(60), "structures were dropped rather than reported");
            Assert.That(planner.Crowded, Is.GreaterThan(0), "an impossible city was reported as fitting");
        });
    }

    /// <summary>The footprints are the ones the browser draws, which is the only reason planning means anything.</summary>
    /// <remarks>
    /// These are pinned against <c>demo/SwgTatooine/SwgTatooine.Client/src/render/styles.ts</c>. If a size changes there and not
    /// here, the server plans with a box the client does not draw and the overlap comes back on screen while every case
    /// above still passes.
    /// </remarks>
    [Test]
    public void TheFootprintsMatchWhatTheClientDraws()
    {
        Assert.Multiple(() =>
        {
            Assert.That(StructureFootprint.HalfExtentM(StructureKind.Building), Is.EqualTo(9f), "STRUCTURE_SIZES says a building is 18 x 18");
            Assert.That(StructureFootprint.HalfExtentM(StructureKind.Terminal), Is.EqualTo(0.8f), "STRUCTURE_SIZES says a terminal is 1.6 x 1.6");
            // 46.2 x 46.2 since 2026-10-22: the reservation follows the published `tat_shuttleport`, which is what a
            // shuttleport IS, rather than the 32 m square it used to be planted in and stand over.
            Assert.That(StructureFootprint.HalfExtentM(StructureKind.Shuttleport), Is.EqualTo(23.1f), "STRUCTURE_SIZES says the dome is 46.2 x 46.2");
            Assert.That(StructureFootprint.HalfExtentM(StructureKind.Factory), Is.EqualTo(11f), "STRUCTURE_SIZES says a factory is 22 x 22");

            // An unknown kind takes a building's footprint rather than none: a kind with no footprint could be planted
            // inside another structure, and a silent wrong answer is worse than a loud approximate one.
            Assert.That(StructureFootprint.HalfExtentM(99), Is.EqualTo(StructureFootprint.HalfExtentM(StructureKind.Building)), "an unknown kind has no footprint");
        });
    }
}
