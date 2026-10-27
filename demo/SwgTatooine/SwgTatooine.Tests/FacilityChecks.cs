using NUnit.Framework;
using SwgTatooine.Server;
using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using SwgTatooine.Bots.Brain;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// Every city has one shuttleport, a bazaar and a mission terminal at its gate, and more of each spread over its disc by area.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class FacilityChecks
{
    private string _dir;
    private TatooineSim _sim;
    private readonly Dictionary<int, List<(int Kind, float X, float Z)>> _byCity = [];

    [OneTimeSetUp]
    public void Build()
    {
        _dir = Worlds.NewDirectory();
        var config = Worlds.Small(_dir);
        config.Shuttles = false;
        _sim = new TatooineSim(config);
        _sim.Initialize();
        using var tx = _sim.Dbe.CreateQuickTransaction();
        foreach (var cluster in tx.For<WorldObject>().GetClusterEnumerator())
        {
            if (cluster.Realm.Value != 0)
            {
                continue;
            }

            var structs = cluster.GetReadOnlySpan(WorldObject.Struct);
            var bounds = cluster.GetReadOnlySpan(WorldObject.Bounds);
            var regions = cluster.GetReadOnlySpan(WorldObject.Region);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                // A region key is a city's OR a point of interest's index plus one, so the key alone does not say which: a city's own structures are the
                // ones within its disc (the gate's pair stands just outside a disc smaller than the shuttleport's reach in a compressed world).
                var region = regions[slot].Value;
                if (region <= 0 || region > _sim.Map.Cities.Count)
                {
                    continue;
                }

                var city = _sim.Map.Cities[region - 1];
                var b = bounds[slot].Bounds;
                var x = (b.MinX + b.MaxX) * 0.5f;
                var z = (b.MinY + b.MaxY) * 0.5f;
                if (MathF.Sqrt(((x - city.X) * (x - city.X)) + ((z - city.Z) * (z - city.Z))) > city.Radius + 40f)
                {
                    continue;
                }

                if (!_byCity.TryGetValue(region, out var list))
                {
                    _byCity[region] = list = [];
                }

                list.Add((structs[slot].Kind, x, z));
            }
        }
    }

    [OneTimeTearDown]
    public void Dispose()
    {
        _sim?.Dispose();
        Worlds.Delete(_dir);
    }

    [Test]
    public void EveryCity_HasOneShuttleport_AndItsShareOfBazaarsAndMissionTerminals()
    {
        Assert.That(_byCity, Has.Count.EqualTo(_sim.Map.Cities.Count));
        for (var c = 0; c < _sim.Map.Cities.Count; c++)
        {
            var city = _sim.Map.Cities[c];
            var list = _byCity[c + 1];
            var want = 1 + WorldBuilder.SpreadFacilities(city.Radius, _sim.Config.FacilityDensityPerKm2);
            Assert.Multiple(() =>
            {
                Assert.That(list.FindAll(s => s.Kind == StructureKind.Shuttleport), Has.Count.EqualTo(1), city.Name);
                Assert.That(list.FindAll(s => s.Kind == StructureKind.Bazaar), Has.Count.InRange(1, want), $"{city.Name}: bazaars");
                Assert.That(list.FindAll(s => s.Kind == StructureKind.MissionTerminal), Has.Count.InRange(1, want), $"{city.Name}: mission terminals");
            });
        }

        // The big city must actually get its share: a full city may get fewer, but not every city.
        var largest = 0;
        for (var c = 1; c < _sim.Map.Cities.Count; c++)
        {
            largest = _sim.Map.Cities[c].Radius > _sim.Map.Cities[largest].Radius ? c : largest;
        }

        var big = _sim.Map.Cities[largest];
        Assert.That(_byCity[largest + 1].FindAll(s => s.Kind == StructureKind.Bazaar),
            Has.Count.EqualTo(1 + WorldBuilder.SpreadFacilities(big.Radius, _sim.Config.FacilityDensityPerKm2)), big.Name);
    }

    [Test]
    public void ABazaarAndAMissionTerminal_StandAtEveryShuttleportsGate()
    {
        foreach (var (region, list) in _byCity)
        {
            var port = list.Find(s => s.Kind == StructureKind.Shuttleport);
            var gateX = port.X;
            var gateZ = port.Z + WorldBuilder.GateOutsideZ;
            foreach (var kind in new[] { StructureKind.Bazaar, StructureKind.MissionTerminal })
            {
                var nearest = float.MaxValue;
                foreach (var s in list)
                {
                    if (s.Kind == kind)
                    {
                        nearest = MathF.Min(nearest, MathF.Sqrt(((s.X - gateX) * (s.X - gateX)) + ((s.Z - gateZ) * (s.Z - gateZ))));
                    }
                }

                Assert.That(nearest, Is.LessThan(8f), $"region {region}: the nearest of kind {kind} is {nearest:F1} m from the gate");
            }
        }
    }

    [Test]
    public void TheSpreadOnes_AreSpread_NotClumped()
    {
        // Best-candidate sampling: no two of a kind in a city closer than a fraction of the spacing an even spread would give.
        foreach (var (region, list) in _byCity)
        {
            var city = _sim.Map.Cities[region - 1];
            foreach (var kind in new[] { StructureKind.Bazaar, StructureKind.MissionTerminal })
            {
                var of = list.FindAll(s => s.Kind == kind);
                if (of.Count < 3)
                {
                    continue;
                }

                var even = city.Radius * MathF.Sqrt(MathF.PI / of.Count);
                for (var i = 0; i < of.Count; i++)
                {
                    for (var j = i + 1; j < of.Count; j++)
                    {
                        var d = MathF.Sqrt(((of[i].X - of[j].X) * (of[i].X - of[j].X)) + ((of[i].Z - of[j].Z) * (of[i].Z - of[j].Z)));
                        Assert.That(d, Is.GreaterThan(even * 0.25f), $"{city.Name}: two of kind {kind} {d:F0} m apart, an even spread is {even:F0} m");
                    }
                }
            }
        }
    }

    [Test]
    public void NothingTheCitiesPlant_Overlaps()
    {
        // The gate's pair and the plaza beyond them are planted first after the dome, at fixed spots: a spot inside the planner's margin of another is a
        // structure overlapping one, in every city — which is what the first version of this did, one per city.
        Assert.That(_sim.Census.Crowded, Is.Zero);
    }

    [Test]
    public void TheCount_FollowsTheArea_AndTheDensity()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WorldBuilder.SpreadFacilities(456f, 8f), Is.EqualTo(5), "Mos Eisley: 6 of each with the gate's");
            Assert.That(WorldBuilder.SpreadFacilities(125f, 8f), Is.EqualTo(0), "Anchorhead: the gate's only");
            Assert.That(WorldBuilder.SpreadFacilities(456f, 0f), Is.EqualTo(0));
            Assert.That(WorldBuilder.SpreadFacilities(456f, 16f), Is.EqualTo(10));
        });
    }

    /// <summary>
    /// BM-1: <c>GET /swg/facilities</c> lists every shuttleport, bazaar and mission terminal the world holds — each where it stands, under its region and
    /// asset — and a bot reads the list into a map whose nearest terminal is the nearest in the world. Unpublished, it answers 404.
    /// </summary>
    [Test]
    public void TheFacilityList_IsEveryFacility_AndABotsNearestIsTheWorldsNearest()
    {
        var world = new List<(int Kind, int Region, ushort Realm, float X, float Z, int Asset, int Yaw)>();
        using (var tx = _sim.Dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<WorldObject>().GetClusterEnumerator())
            {
                var structs = cluster.GetReadOnlySpan(WorldObject.Struct);
                var places = cluster.GetReadOnlySpan(WorldObject.Bounds);
                var regions = cluster.GetReadOnlySpan(WorldObject.Region);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    var kind = structs[slot].Kind;
                    if (kind is StructureKind.Shuttleport or StructureKind.Bazaar or StructureKind.MissionTerminal)
                    {
                        world.Add((kind, regions[slot].Value, cluster.Realm.Value, places[slot].X, places[slot].Z, structs[slot].Asset, structs[slot].Yaw));
                    }
                }
            }
        }

        var json = FacilityEndpoints.Build(_sim.Dbe);
        FacilityEndpoints.Json = null;
        var unpublished = FacilityEndpoints.Get();
        FacilityEndpoints.Json = json;
        IResult published;
        try
        {
            published = FacilityEndpoints.Get();
        }
        finally
        {
            FacilityEndpoints.Json = null;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var listed = new List<(int Kind, int Region, ushort Realm, float X, float Z, int Asset, int Yaw)>();
        foreach (var f in doc.RootElement.GetProperty("facilities").EnumerateArray())
        {
            listed.Add((f.GetProperty("kind").GetInt32(), f.GetProperty("region").GetInt32(), (ushort)f.GetProperty("realm").GetInt32(),
                f.GetProperty("x").GetSingle(), f.GetProperty("z").GetSingle(),
                f.TryGetProperty("asset", out var a) ? a.GetInt32() : StructureAssets.NoAsset, f.GetProperty("yaw").GetInt32()));
        }

        var map = new FacilityMap();
        map.Seed(FacilityMap.Parse(json));
        var terminals = world.FindAll(f => f.Kind == StructureKind.MissionTerminal && f.Realm == 0);
        Assert.Multiple(() =>
        {
            Assert.That(unpublished, Is.InstanceOf<NotFound>(), "an unpublished list answered something other than 404");
            Assert.That(published, Is.InstanceOf<ContentHttpResult>().And.Property("ResponseContent").EqualTo(json));
            Assert.That(world.FindAll(f => f.Kind == StructureKind.Shuttleport), Has.Count.EqualTo(_sim.Map.Cities.Count), "the premise");
            Assert.That(listed, Is.EquivalentTo(world).Using<(int, int, ushort, float, float, int, int)>(Same), "not every facility, or not as it stands");
            Assert.That(map.Count, Is.EqualTo(world.FindAll(f => f.Realm == 0 && f.Region > 0).Count), "the bot's map is not the list");
        });

        // Against what the world build recorded on its own, not a second copy of Build's loop (review 6): the mission desk's terminals and each city's
        // shuttleport from the index, and every city's bazaars within what its area plants.
        static bool Near((float X, float Z) a, (float X, float Z) b) => MathF.Abs(a.X - b.X) < 0.01f && MathF.Abs(a.Z - b.Z) < 0.01f;
        var index = _sim.Index;
        var listedTerminals = listed.FindAll(f => f.Kind == StructureKind.MissionTerminal && f.Realm == 0).ConvertAll(f => (f.X, f.Z));
        var listedPorts = listed.FindAll(f => f.Kind == StructureKind.Shuttleport && f.Realm == 0).ConvertAll(f => (f.X, f.Z));
        var indexTerminals = index.Terminals.FindAll(t => t.Realm == 0).ConvertAll(t => (t.X, t.Z));
        Assert.Multiple(() =>
        {
            Assert.That(indexTerminals, Is.Not.Empty, "the premise: the desk's terminals");
            Assert.That(listedTerminals, Is.EquivalentTo(indexTerminals).Using<(float X, float Z)>(Near), "not the mission desk's terminals");
            Assert.That(listedPorts, Is.EquivalentTo(index.Shuttleports).Using<(float X, float Z)>(Near), "not the cities' shuttleports");
            for (var c = 0; c < _sim.Map.Cities.Count; c++)
            {
                var city = _sim.Map.Cities[c];
                var bazaars = listed.FindAll(f => f.Kind == StructureKind.Bazaar && f.Realm == 0 && f.Region == c + 1).Count;
                Assert.That(bazaars, Is.InRange(1, 1 + WorldBuilder.SpreadFacilities(city.Radius, _sim.Config.FacilityDensityPerKm2)), $"{city.Name}: bazaars");
            }
        });

        // From points across the planet, the bot's nearest terminal is the world's.
        for (var i = 0; i < 64; i++)
        {
            var x = MathF.Cos(i * 2.4f) * 300f * (i % 23);
            var z = MathF.Sin(i * 2.4f) * 300f * (i % 23);
            var nearest = terminals[0];
            foreach (var t in terminals)
            {
                nearest = Sq(t.X - x) + Sq(t.Z - z) < Sq(nearest.X - x) + Sq(nearest.Z - z) ? t : nearest;
            }

            Assert.That(map.TryNearest(Routing.MissionTerminalKind, x, z, out var found, out var region), Is.True);
            Assert.That((found.X, found.Z, region), Is.EqualTo((nearest.X, nearest.Z, nearest.Region)).Using<(float, float, int)>(
                (a, b) => MathF.Abs(a.Item1 - b.Item1) < 0.01f && MathF.Abs(a.Item2 - b.Item2) < 0.01f && a.Item3 == b.Item3), $"from ({x:F0}, {z:F0})");
        }

        static bool Same((int, int, ushort, float, float, int, int) a, (int, int, ushort, float, float, int, int) b) =>
            a.Item1 == b.Item1 && a.Item2 == b.Item2 && a.Item3 == b.Item3 && MathF.Abs(a.Item4 - b.Item4) < 0.01f && MathF.Abs(a.Item5 - b.Item5) < 0.01f
            && a.Item6 == b.Item6 && a.Item7 == b.Item7;

        static float Sq(float v) => v * v;
    }
}
