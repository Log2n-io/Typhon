using NUnit.Framework;
using SwgTatooine.Server;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Typhon.Engine;
using Typhon.Schema.Definition;
using SwgTatooine.Navigation;

namespace SwgTatooine.Tests;

/// <summary>
/// A house and its room as the pack publishes them, cut down to what the doors, the walls near them and the navigation need — the checks cannot read
/// <c>build/assets</c>, which is not in the repository.
/// </summary>
/// <remarks>
/// Figures from <c>tat_house_large-6</c> and <c>tat_house_large_interior-5</c> (catalogue 4): the front door, its spawn and its path; the door slab, the two
/// pilasters beside it and the house body; the landing in front of the door (0.7 m, where the front spawn stands) and one step up to it in place of the
/// published ramp; the room's way out, its entry, its front wall and a three-node graph whose cells serve their nearest node.
/// </remarks>
internal static class Published
{
    public static readonly int House = Array.IndexOf(StructureAssets.Table, "tat_house_large");
    public static readonly int Room = Array.IndexOf(StructureAssets.Table, "tat_house_large_interior");

    /// <summary>The room's waypoints, glTF frame.</summary>
    public static readonly (float X, float Z)[] Nodes = [(1.5f, 3.0f), (-1.5f, 1.5f), (-1.0f, -2.6f)];

    public const string HouseJson = """
        {"schema":1,"id":"tat_house_large",
         "portals":[{"id":"front","center":[1.5,2.225,5.45],"rot":[0,0,0,1],"half":[0.95,1.525,0.4],"forward":"-Z",
           "target":"tat_house_large_interior","target_spawn":"entry","exit_spawn":"front",
           "approach":[[1.5,0,11.75],[1.5,0,8.75],[1.5,0.7,7.0],[1.5,0.7,5.9],[1.5,0.96,5.2]]}],
         "spawns":[{"id":"front","pos":[1.5,0.7,6.3],"rot":[0,-1,0,0],"forward":"-Z"}],
         "collision":{"shapes":[{"type":"box","half":[1.45,1.0,0.275]},{"type":"box","half":[0.425,1.0,0.4]},{"type":"box","half":[5.5,1.0,4.5]},
                                {"type":"box","half":[1.2,0.35,0.55]},{"type":"box","half":[1.2,0.175,0.45]}],
           "colliders":[{"shape":0,"pos":[1.5,1.7,4.575]},{"shape":1,"pos":[-0.275,1.7,4.75]},{"shape":1,"pos":[3.275,1.7,4.75]},
                        {"shape":2,"pos":[2.0,1.7,0.0]},{"shape":3,"pos":[1.5,0.35,6.45]},{"shape":4,"pos":[1.5,0.175,7.45]}]}}
        """;

    public static string RoomJson()
    {
        // A grid over x -3..3, z -4..5 in the glTF frame; every cell serves its nearest node, which is what a room with no furniture publishes.
        const float OriginX = -3f;
        const float OriginZ = -4f;
        const float Size = 0.5f;
        const int Cols = 12;
        const int Rows = 18;
        var cells = new StringBuilder();
        for (var row = 0; row < Rows; row++)
        {
            for (var col = 0; col < Cols; col++)
            {
                var x = OriginX + ((col + 0.5f) * Size);
                var z = OriginZ + ((row + 0.5f) * Size);
                var best = 0;
                for (var n = 1; n < Nodes.Length; n++)
                {
                    if (Sq(Nodes[n].X - x, Nodes[n].Z - z) < Sq(Nodes[best].X - x, Nodes[best].Z - z))
                    {
                        best = n;
                    }
                }

                cells.Append(cells.Length == 0 ? string.Empty : ",").Append(best.ToString(CultureInfo.InvariantCulture));
            }
        }

        return """
            {"schema":1,"id":"tat_house_large_interior",
             "portals":[{"id":"front","center":[1.5,1.5,3.5],"rot":[0,-1,0,0],"half":[1.0,1.5,0.4],"forward":"-Z",
               "target":"tat_house_large","target_spawn":"front","exit_spawn":"entry","approach":[[1.5,0,3.0],[1.5,0,3.75]]}],
             "spawns":[{"id":"entry","pos":[1.5,0,2.5],"rot":[0,0,0,1],"forward":"-Z"}],
             "collision":{"shapes":[{"type":"box","half":[9.0,0.2,5.65]},{"type":"box","half":[4.58,1.0,0.2]}],
               "colliders":[{"shape":0,"pos":[0.8,-0.2,1.15]},{"shape":1,"pos":[1.07,1.0,4.3]}]},
             "nav":{"nodes":[[1.5,0,3.0],[-1.5,0,1.5],[-1.0,0,-2.6]],"edges":[[0,1],[1,2]],
                    "cells":{"origin":[-3,-4],"size":0.5,"cols":12,"rows":18,"node":[CELLS]}}}
            """.Replace("CELLS", cells.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The table the server builds from those two descriptors.</summary>
    public static AssetColliders Shapes()
    {
        var texts = new string[StructureAssets.Table.Length];
        texts[House] = HouseJson;
        texts[Room] = RoomJson();
        var byIndex = new AssetColliders.PlanBox[StructureAssets.Table.Length][];
        for (var i = 0; i < byIndex.Length; i++)
        {
            byIndex[i] = texts[i] == null ? [] : AssetColliders.ReadBlocking(texts[i]);
        }

        return AssetColliders.FromDescriptors(byIndex, texts);
    }

    private static float Sq(float x, float z) => (x * x) + (z * z);
}

/// <summary>
/// The doors as the server reads them out of a descriptor: in the world's frame, and connected to the spawn on the other side.
/// </summary>
[TestFixture]
public sealed class DoorReadingChecks
{
    /// <summary>
    /// The door stands in the opening the walls leave, which takes the same X mirror the walls take.
    /// </summary>
    /// <remarks>
    /// <b>The bug this pins:</b> the walls were mirrored (glTF to the renderer's frame) and the door was not, so the plane a body had to cross stood at the
    /// mirror image of the doorway — 3 m to the side of the large house's door, in the facade. The case asserts the relation, not the number: the door's
    /// centre lies between the two pilasters that frame it, as the walls are read.
    /// </remarks>
    [Test]
    public void TheDoorStandsInTheOpeningTheWallsLeave()
    {
        var shapes = Published.Shapes();
        var door = shapes.DoorFor(Published.House);
        var walls = shapes.For(Published.House);

        // The pilasters: the two narrow boxes either side of the door, in front of the body.
        var left = float.NegativeInfinity;
        var right = float.PositiveInfinity;
        foreach (var w in walls)
        {
            if (w.MaxZ > 5f && w.MaxX - w.MinX < 1f)
            {
                if (w.MaxX <= door.CenterX)
                {
                    left = MathF.Max(left, w.MaxX);
                }
                else if (w.MinX >= door.CenterX)
                {
                    right = MathF.Min(right, w.MinX);
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(door.Exists, Is.True);
            Assert.That(door.CenterX, Is.EqualTo(-1.5f).Within(1e-5f), "the published x, mirrored");
            Assert.That(left, Is.GreaterThan(float.NegativeInfinity), "no pilaster on the door's left: it is not in the opening");
            Assert.That(right, Is.LessThan(float.PositiveInfinity), "no pilaster on the door's right: it is not in the opening");
            Assert.That(right - left, Is.GreaterThanOrEqualTo(2f * door.HalfX), "the opening is narrower than the door");
        });
    }

    [Test]
    public void AHouseDoorLeadsToItsRoomsEntry_AndBackToItsOwnFront()
    {
        var shapes = Published.Shapes();
        var door = shapes.DoorFor(Published.House);
        Assert.Multiple(() =>
        {
            Assert.That(door.TargetAsset, Is.EqualTo(Published.Room), "the door names the room by its asset");
            Assert.That((door.ArriveX, door.ArriveZ), Is.EqualTo((-1.5f, 2.5f)), "arrives on the room's entry spawn, mirrored");
            Assert.That((door.ExitX, door.ExitZ), Is.EqualTo((-1.5f, 6.3f)), "comes back out on the house's own front spawn, mirrored");
            Assert.That((door.ForwardX, door.ForwardZ), Is.EqualTo((0f, -1f)), "into the house");
            Assert.That((door.ApproachX, door.ApproachZ), Is.EqualTo((-1.5f, 11.75f)), "the path starts in front of the ramp");
            Assert.That((door.ApproachEndX, door.ApproachEndZ), Is.EqualTo((-1.5f, 5.2f)), "and ends past the plane");
        });
    }

    [Test]
    public void ARoomsWayOut_PointsOutOfTheRoom_AndLandsOnTheHousesFront()
    {
        var shapes = Published.Shapes();
        var exit = shapes.DoorFor(Published.Room);
        Assert.Multiple(() =>
        {
            Assert.That(exit.Exists, Is.True);
            Assert.That(exit.ForwardX, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(exit.ForwardZ, Is.EqualTo(1f).Within(1e-6f), "the half turn is part of the forward: out of the room is +Z");
            Assert.That(exit.TargetAsset, Is.EqualTo(Published.House));
            Assert.That((exit.ArriveX, exit.ArriveZ), Is.EqualTo((-1.5f, 6.3f)), "the house's front spawn, in the house's frame");
            Assert.That(
                SimBridge.CrossedDoorForTest(exit.CenterX, exit.CenterZ, exit.ForwardX, exit.ForwardZ, exit.HalfX, exit.ApproachX, exit.ApproachZ,
                    exit.ApproachEndX, exit.ApproachEndZ),
                Is.True,
                "walking the published way out does not cross it");
        });
    }

    [Test]
    public void ADoorWithNoApproach_GetsAStraightOne_ThatCrossesIt()
    {
        var json = Published.HouseJson.Replace(
            "\"approach\":[[1.5,0,11.75],[1.5,0,8.75],[1.5,0.7,7.0],[1.5,0.7,5.9],[1.5,0.96,5.2]]", "\"approach\":[]", StringComparison.Ordinal);
        var door = AssetColliders.ReadDoor(json);
        Assert.That(door.Exists, Is.True);
        Assert.That(
            SimBridge.CrossedDoorForTest(door.CenterX, door.CenterZ, door.ForwardX, door.ForwardZ, door.HalfX, door.ApproachX, door.ApproachZ,
                door.ApproachEndX, door.ApproachEndZ),
            Is.True);
    }

    [Test]
    public void TheStandInDoor_IsCrossedByItsOwnPath_AndItsLandingsDoNotRetrigger()
    {
        var door = AssetColliders.StandInDoor(9f);
        var room = AssetColliders.StandInRoomDoor;
        Assert.Multiple(() =>
        {
            Assert.That(Cross(door, door.ApproachX, door.ApproachZ, door.ApproachEndX, door.ApproachEndZ), Is.True, "in, along its path");
            Assert.That(Cross(room, room.ApproachX, room.ApproachZ, room.ApproachEndX, room.ApproachEndZ), Is.True, "out, along its path");
            Assert.That(Side(door, door.ExitX, door.ExitZ), Is.LessThan(-0.5f), "coming out lands well outside the front door");
            Assert.That(Side(room, door.ArriveX, door.ArriveZ), Is.LessThan(-0.5f), "going in lands well inside the way out");
        });
    }

    internal static bool Cross(in AssetColliders.DoorPlan d, float fx, float fz, float tx, float tz) =>
        SimBridge.CrossedDoorForTest(d.CenterX, d.CenterZ, d.ForwardX, d.ForwardZ, d.HalfX, fx, fz, tx, tz);

    /// <summary>Signed distance along the door's forward: negative is the side a body crosses FROM.</summary>
    internal static float Side(in AssetColliders.DoorPlan d, float x, float z) => ((x - d.CenterX) * d.ForwardX) + ((z - d.CenterZ) * d.ForwardZ);
}

/// <summary>A room's navigation, read in the world's frame: cells find their node through the mirror, and routes follow the edges.</summary>
[TestFixture]
public sealed class NavGraphChecks
{
    [Test]
    public void ACell_IsFoundThroughTheMirror()
    {
        var nav = Published.Shapes().NavFor(Published.Room);
        Assert.That(nav, Is.Not.Null);
        Assert.Multiple(() =>
        {
            // Published node 1 is glTF (-1.5, 1.5): (1.5, 1.5) in the world. Looking the world point up unmirrored would land near node 0.
            Assert.That(nav.NodeAt(1.5f, 1.5f), Is.EqualTo(1));
            Assert.That(nav.Node(1), Is.EqualTo((1.5f, 1.5f)));
            Assert.That(nav.NodeAt(1f, -2.6f), Is.EqualTo(2));
            Assert.That(nav.NodeExactlyAt(-1.5f, 3.0f), Is.EqualTo(0), "the way out's path starts on node 0, found by position");
        });
    }

    [Test]
    public void ARoute_WalksTheEdges_ToTheDoorsNode()
    {
        var nav = Published.Shapes().NavFor(Published.Room);
        var route = new List<(float X, float Z)>();
        Assert.That(nav.TryRoute(1f, -2.5f, 0, route), Is.True);
        Assert.That(route, Is.EqualTo(new[] { nav.Node(2), nav.Node(1), nav.Node(0) }), "node 2 is only linked to node 0 through node 1");
    }

    [Test]
    public void APositionOutsideTheGrid_TakesTheNearestServedCell()
    {
        var nav = Published.Shapes().NavFor(Published.Room);
        Assert.That(nav.NodeAt(-3.2f, 3.1f), Is.EqualTo(0), "half a metre past the grid's edge, beside node 0");
        Assert.That(nav.NodeAt(40f, 40f), Is.EqualTo(-1), "nowhere near the room");
    }
}

/// <summary>
/// The world built with the published house and room: every enterable building has its room, the two sides of each door name each other, and everything a
/// program needs to walk through stays true once the database is closed and reopened.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class InteriorWorldChecks
{
    private string _dir;
    private TatooineSim _sim;

    [OneTimeSetUp]
    public void Build()
    {
        _dir = Worlds.NewDirectory();
        var config = Worlds.Small(_dir);
        config.Shuttles = false;
        config.Persist = true;
        _sim = new TatooineSim(config) { AssetShapesOverride = Published.Shapes() };
        _sim.Initialize();
    }

    [OneTimeTearDown]
    public void Dispose()
    {
        _sim?.Dispose();
        Worlds.Delete(_dir);
    }

    /// <summary>
    /// Every door stores a real landing height, a world without assets included: the terrain where nothing published stands under the landing.
    /// </summary>
    /// <remarks>A footprint's stand-in door used to store place.Y + -∞: an infinity in the database that only a <c>max</c> made harmless.</remarks>
    [Test]
    public void EveryDoor_StoresARealLandingHeight_WithOrWithoutAssets()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.Shuttles = false;
            using var bare = new TatooineSim(config);
            bare.Initialize();
            var terrain = SwgTatooine.Server.Terrain.TerrainField.Shared(config.ContentScale);
            foreach (var sim in new[] { _sim, bare })
            {
                var doors = sim.Portals;
                Assert.That(doors.Count, Is.GreaterThan(0));
                for (var row = 0; row < doors.Count; row++)
                {
                    var door = doors.Door[row];
                    Assert.That(float.IsFinite(door.ArriveY), Is.True, $"row {row} stores {door.ArriveY}");
                    if (sim == bare && door.TargetRealm < config.Planets)
                    {
                        Assert.That(door.ArriveY, Is.EqualTo(terrain.GroundAt(door.ArriveX, door.ArriveZ)).Within(1e-4f), $"row {row}");
                    }
                }
            }
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    [Test]
    public void EveryEnterableBuilding_HasItsOwnRoom()
    {
        var rooms = new HashSet<int>();
        using (var tx = _sim.Dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<WorldObject>().GetClusterEnumerator())
            {
                var structs = cluster.GetReadOnlySpan(WorldObject.Struct);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var st = structs[System.Numerics.BitOperations.TrailingZeroCount(bits)];
                    if (st.InteriorRealm != 0)
                    {
                        Assert.That(cluster.Realm.Value, Is.EqualTo(0), "a room's realm is written on a building of the planet");
                        Assert.That(rooms.Add(st.InteriorRealm), Is.True, $"realm {st.InteriorRealm} is the room of two buildings");
                    }
                }
            }
        }

        Assert.That(rooms, Has.Count.EqualTo(_sim.InteriorsPerPlanet));
    }

    [Test]
    public void TheTwoSidesOfEveryDoor_NameEachOther_AndTheirStructures()
    {
        var doors = _sim.Portals;
        var fronts = 0;
        using var tx = _sim.Dbe.CreateQuickTransaction();
        using var structures = tx.For<WorldObject>();
        for (var row = 0; row < doors.Count; row++)
        {
            if (doors.Realm[row] != 0)
            {
                continue;
            }

            fronts++;
            var front = doors.Door[row];
            var backRow = doors.Twin[row];
            var back = doors.Door[backRow];
            Assert.That(structures.TryOpen(front.Owner, out var house), Is.True, "a front door with no building");
            Assert.That(structures.TryOpen(back.Owner, out var room), Is.True, "a way out with no room");
            var houseStruct = house.Read(WorldObject.Struct);
            var roomStruct = room.Read(WorldObject.Struct);
            var roomRealm = room.Read(WorldObject.Realm).Value;
            Assert.Multiple(() =>
            {
                Assert.That(doors.Twin[backRow], Is.EqualTo(row), "the way out's twin is not the front door");
                Assert.That(front.TargetRealm, Is.EqualTo(houseStruct.InteriorRealm), "the front door does not lead to its building's room");
                Assert.That(doors.Realm[backRow], Is.EqualTo(front.TargetRealm), "the way out is not in the room the door leads to");
                Assert.That(back.TargetRealm, Is.EqualTo(0), "the way out does not lead back to the planet");
                Assert.That(roomRealm, Is.EqualTo(front.TargetRealm), "the room stands in another realm");
                Assert.That(roomStruct.Kind, Is.EqualTo(StructureKind.Interior));
                Assert.That(roomStruct.Asset, Is.EqualTo(Published.Room), "the room is not the asset the door names");
                Assert.That(doors.RoomDoor(front.TargetRealm), Is.EqualTo(backRow), "the room's realm does not find its way out");
            });
        }

        Assert.That(fronts, Is.EqualTo(_sim.InteriorsPerPlanet));
    }

    /// <summary>
    /// The pair is stored by ONE write — the way out names the door into its room — and paired both ways at open.
    /// </summary>
    /// <remarks>
    /// The door into a room used to be linked back in a second pass, after its way out had been committed: a stored world between the two had every
    /// front door unlinked, and the table refused it at every open after.
    /// </remarks>
    [Test]
    public void EveryDoor_IsLinkedByItsWayOut_AndPairedBothWaysAtOpen()
    {
        var doors = _sim.Portals;
        for (var row = 0; row < doors.Count; row++)
        {
            var stored = doors.Door[row].Twin;
            var intoARoom = doors.Realm[row] < _sim.Config.Planets;
            Assert.That(stored.IsNull, Is.EqualTo(intoARoom), $"row {row}: only a room's way out stores the link");
            Assert.That(doors.Twin[doors.Twin[row]], Is.EqualTo(row), $"row {row} is not paired both ways");
        }
    }

    [Test]
    public void EveryLanding_IsOnTheFarSideOfTheDoorItCameThrough_AndClearOfTheOther()
    {
        var doors = _sim.Portals;
        for (var row = 0; row < doors.Count; row++)
        {
            var self = doors.Door[row];
            var twin = doors.Door[doors.Twin[row]];

            // A body going through `self` lands at self.Arrive, in the twin's realm: on the side the twin is crossed FROM, so it cannot re-trigger.
            var side = ((self.ArriveX - twin.CenterX) * twin.ForwardX) + ((self.ArriveZ - twin.CenterZ) * twin.ForwardZ);
            Assert.That(side, Is.LessThan(-0.4f), $"door {doors.Id[row]} lands {side:F2} m from the plane of the door back");
        }
    }

    [Test]
    public void WalkingEveryPath_CrossesItsDoor_AndOnlyAtTheEnd()
    {
        var doors = _sim.Portals;
        for (var row = 0; row < doors.Count; row++)
        {
            var d = doors.Door[row];
            var points = new List<(float X, float Z)>();
            if (doors.Realm[row] != 0)
            {
                // A room's way out starts where a body arrives in the room.
                var twin = doors.Door[doors.Twin[row]];
                points.Add((twin.ArriveX, twin.ArriveZ));
                points.AddRange(doors.Route[row]);
            }
            else
            {
                points.Add(doors.ApproachStart[row]);
            }

            points.Add(doors.ApproachEnd[row]);
            for (var i = 1; i < points.Count; i++)
            {
                var crossed = SimBridge.CrossedDoorForTest(d.CenterX, d.CenterZ, d.ForwardX, d.ForwardZ, d.HalfWidth, points[i - 1].X, points[i - 1].Z,
                    points[i].X, points[i].Z);
                Assert.That(crossed, Is.EqualTo(i == points.Count - 1), $"door {doors.Id[row]}, leg {i} of {points.Count - 1}");
            }
        }
    }

    [Test]
    public void TheRoomsNpcs_StandOnItsNodes_AndLeaveTheDoorsFree()
    {
        var nodes = new HashSet<(float, float)>();
        for (var n = 1; n < Published.Nodes.Length; n++)
        {
            nodes.Add((-Published.Nodes[n].X, Published.Nodes[n].Z));
        }

        var counted = 0;
        using var tx = _sim.Dbe.CreateQuickTransaction();
        foreach (var cluster in tx.For<CityNpc>().GetClusterEnumerator())
        {
            if (cluster.Realm.Value < _sim.Config.Planets)
            {
                continue;
            }

            var places = cluster.GetReadOnlySpan(CityNpc.Bounds);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var p = places[System.Numerics.BitOperations.TrailingZeroCount(bits)];
                Assert.That(nodes, Does.Contain((MathF.Round(p.X, 3), MathF.Round(p.Z, 3))), $"an NPC stands at {p.X}, {p.Z}");
                counted++;
            }
        }

        Assert.That(counted, Is.EqualTo(_sim.Config.InteriorNpcs * _sim.InteriorsPerPlanet));
    }

    internal static List<string> Snapshot(PortalTable doors)
    {
        var rows = new List<string>();
        for (var row = 0; row < doors.Count; row++)
        {
            var d = doors.Door[row];
            var t = doors.Door[doors.Twin[row]];
            rows.Add(FormattableString.Invariant(
                $"{doors.Realm[row]} {d.CenterX:F3},{d.CenterZ:F3} -> {d.TargetRealm} @ {d.ArriveX:F3},{d.ArriveZ:F3} twin {t.CenterX:F3},{t.CenterZ:F3}"));
        }

        return rows;
    }
}

/// <summary>The doors are data: a world reopened without the asset set it was built with still knows every door, where it leads and where it lands.</summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class InteriorReopenChecks
{
    [Test]
    public void AReopenedWorld_KeepsEveryDoor_WithoutItsAssets()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.Shuttles = false;
            config.Persist = true;
            List<string> before;
            using (var built = new TatooineSim(config) { AssetShapesOverride = Published.Shapes() })
            {
                built.Initialize();
                before = InteriorWorldChecks.Snapshot(built.Portals);
            }

            using var reopened = new TatooineSim(config);
            reopened.Initialize();
            Assert.Multiple(() =>
            {
                Assert.That(reopened.Reopened, Is.True);
                Assert.That(InteriorWorldChecks.Snapshot(reopened.Portals), Is.EquivalentTo(before));
            });
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    /// <summary>
    /// A body that was walking to a door when the world was closed walks into THAT door's room once it is reopened.
    /// </summary>
    /// <remarks>
    /// The walk is persisted (<c>PlayerState.ShuttleDest</c>), and it used to hold the door's ROW — a number the table gives at open, in the order the
    /// portals enumerate, which nothing keeps across a reopen. It holds the room's realm now, which is stored. A room whose realm id is not its door's
    /// row, so the two readings name different doors.
    /// </remarks>
    [Test]
    public void ABodyWalkingToADoor_WhenTheWorldCloses_WalksIntoThatRoomOnceReopened()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.Shuttles = false;
            config.Persist = true;
            config.Dungeons = 0;
            config.InteriorShare = 0f;
            config.InteriorStayS = 30f;
            config.WarmTicks = 0;
            config.WorkerCount = 1;
            ushort interior = 0;
            EntityId player;
            using (var built = new TatooineSim(config) { AssetShapesOverride = Published.Shapes() })
            {
                built.Initialize();
                for (var r = config.Planets; r < config.Planets + built.Portals.Count / 2 && interior == 0; r++)
                {
                    var front = built.Portals.FrontDoor(r);
                    if (front >= 0 && front != r)
                    {
                        interior = (ushort)r;
                    }
                }

                Assert.That(interior, Is.Not.Zero, "every room's realm id equals its door's row: the case cannot tell the two apart");
                player = DoorWalkChecks.SendToDoor(built, built.Portals.FrontDoor(interior));
            }

            config.MeasuredTicks = 40;
            using var reopened = new TatooineSim(config) { AssetShapesOverride = Published.Shapes() };
            reopened.Initialize();
            Assert.That(reopened.Reopened, Is.True);
            reopened.Run();

            using var read = reopened.Dbe.CreateQuickTransaction();
            Assert.That(read.Open(player).Read(Player.Realm).Value, Is.EqualTo(interior));
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }
}

/// <summary>
/// Bodies through doors, both kinds: the scripted crowd walks a door's path in and its room's route out, and a client's body walking through a doorway goes
/// in even when its disc would have let it skip the query.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class DoorWalkChecks
{
    private string _dir;

    [SetUp]
    public void Directory() => _dir = Worlds.NewDirectory();

    [TearDown]
    public void Delete() => Worlds.Delete(_dir);

    private SimConfig Config(int ticks)
    {
        var config = Worlds.Small(_dir);
        config.Shuttles = false;
        config.Dungeons = 0;
        config.InteriorShare = 0f;
        config.InteriorStayS = 2f;
        config.WarmTicks = 0;
        config.MeasuredTicks = ticks;
        config.WorkerCount = 1;
        return config;
    }

    /// <summary>
    /// A scripted body sent to a door walks its path in, waits, walks the room's route to the way out, and comes out on the building's front spawn.
    /// </summary>
    /// <remarks>
    /// <b>190 ticks</b>: about 14 on the path, at most 80 inside (2 s stretched up to four times), 3 on the way out — and then an idle of at least 100 ticks,
    /// so the body is still standing where it came out when the run stops.
    /// </remarks>
    [Test]
    public void TheCrowd_WalksInThroughTheDoor_AndOutThroughTheRoom()
    {
        using var sim = new TatooineSim(Config(190)) { AssetShapesOverride = Published.Shapes() };
        sim.Initialize();
        var interior = (ushort)sim.Config.Planets;
        var row = Worlds.DoorInto(sim, interior);
        var player = SendToDoor(sim, row);

        sim.Run();

        var back = sim.Portals.Door[sim.Portals.Twin[row]];
        using var read = sim.Dbe.CreateQuickTransaction();
        var e = read.Open(player);
        var place = e.Read(Player.Bounds);
        var realm = e.Read(Player.Realm).Value;
        var ground = sim.Bridge.GroundAt(RealmId.Default, place.X, place.Z);
        Assert.Multiple(() =>
        {
            Assert.That(sim.CrossingTotals.Entries, Is.EqualTo(1), "it did not go in");
            Assert.That(sim.CrossingTotals.Exits, Is.EqualTo(1), "it did not come out");
            Assert.That(realm, Is.EqualTo(0), "it is not back on the planet");
            Assert.That(place.X, Is.EqualTo(back.ArriveX).Within(0.01f), "it did not come out on the building's front spawn");
            Assert.That(place.Z, Is.EqualTo(back.ArriveZ).Within(0.01f));

            // On the landing the spawn stands on, 0.7 m above the house's origin: not on the terrain under it, inside the landing's collider.
            Assert.That(place.Y, Is.EqualTo(MathF.Max(back.ArriveY, ground)).Within(0.01f), $"feet at {place.Y:F2}, terrain {ground:F2}");
            Assert.That(back.ArriveY, Is.GreaterThan(float.NegativeInfinity), "the front spawn's height was not stored");
        });
    }

    /// <summary>
    /// A crossing whose batch did not commit is tried once more on the next tick, and lands; two failures in a row drop it, and the body stays where it was.
    /// </summary>
    /// <remarks>
    /// Before, the result of the commit was ignored: the body counted as landed, its room was pinned and the crossing counted, all for a crossing that
    /// never happened. 40 ticks: the plane is reached on about the fourteenth.
    /// </remarks>
    [TestCase(1, 1)]
    [TestCase(2, 0)]
    public void ACrossingThatDidNotCommit_IsRetriedOnce(int failures, int entries)
    {
        using var sim = new TatooineSim(Config(40)) { AssetShapesOverride = Published.Shapes(), FailCrossingCommits = failures };
        sim.Initialize();
        var interior = (ushort)sim.Config.Planets;
        var row = Worlds.DoorInto(sim, interior);
        var player = SendToDoor(sim, row);

        sim.Run();

        using var read = sim.Dbe.CreateQuickTransaction();
        var realm = read.Open(player).Read(Player.Realm).Value;
        Assert.Multiple(() =>
        {
            Assert.That(sim.Bridge.FailedCrossingCommits, Is.EqualTo(failures));
            Assert.That(sim.CrossingTotals.Entries, Is.EqualTo(entries));
            Assert.That(sim.Bridge.DroppedCrossings, Is.EqualTo(failures - 1));
            Assert.That(realm, Is.EqualTo(entries == 1 ? interior : (ushort)0), "the body is not where its committed crossings put it");
        });
    }

    /// <summary>
    /// On a planet visited every third tick, a body walking to a door reaches the start of its path and goes in.
    /// </summary>
    /// <remarks>
    /// Waypoints are reached exactly, within 5 cm, and the steering capped one tick's step at the distance left — while a strided realm moves k ticks of
    /// velocity per visit. The body overshot the start of the path, then overshot back, and stood oscillating there until the walk timed out. 2.4 m
    /// short of the start, so it has to reach it: 60 ticks are 20 visits.
    /// </remarks>
    [Test]
    public void OnAStridedPlanet_ABodyReachesTheDoorsPath_AndGoesIn()
    {
        var config = Config(60);
        config.Planets = 2;
        config.PlanetDivisor = 3;
        using var sim = new TatooineSim(config) { AssetShapesOverride = Published.Shapes() };
        sim.Initialize();
        var row = -1;
        for (var r = 0; r < sim.Portals.Count && row < 0; r++)
        {
            if (sim.Portals.Realm[r] == 1)
            {
                row = r;
            }
        }

        Assert.That(row, Is.GreaterThanOrEqualTo(0), "planet 1 has no door");
        var interior = (ushort)sim.Portals.Door[row].TargetRealm;
        var player = SendToDoor(sim, row, back: 2.4f);

        sim.Run();

        using var read = sim.Dbe.CreateQuickTransaction();
        Assert.That(read.Open(player).Read(Player.Realm).Value, Is.EqualTo(interior));
    }

    /// <summary>Puts the first player on open ground at a door's path, told to walk it in.</summary>
    /// <param name="sim">The world.</param>
    /// <param name="row">The door, on its planet.</param>
    /// <param name="back">How far short of the path's start it stands, metres, on the line through the path: 0 to stand on it.</param>
    internal static EntityId SendToDoor(TatooineSim sim, int row, float back = 0f)
    {
        var player = sim.Index.Players[0];
        var (ax, az) = sim.Portals.ApproachStart[row];
        var (ex, ez) = sim.Portals.ApproachEnd[row];
        var length = MathF.Max(1e-3f, MathF.Sqrt(((ax - ex) * (ax - ex)) + ((az - ez) * (az - ez))));
        var sx = ax + ((ax - ex) / length * back);
        var sz = az + ((az - ez) / length * back);
        using (var tx = sim.Dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
        {
            var p = tx.OpenMut(player);
            var state = p.Read(Player.State);
            state.Activity = PlayerActivity.ToPortal;
            state.ActivityTicks = 200 * sim.Config.TickRateHz;
            state.ShuttleDest = sim.Portals.Door[row].TargetRealm;
            p.Set(Player.State, state);
            var move = p.Read(Player.Move);
            move.SpeedMps = TatooineData.PlayerRunSpeedMps;
            move.VelX = 0f;
            move.VelZ = 0f;
            move.DestX = ax;
            move.DestZ = az;
            p.Set(Player.Move, move);
            var at = default(PlayerPlacement);
            at.SetAt(sx, sz, 0f, p.Read(Player.Bounds).HalfExtent);
            tx.Teleport(player, Player.Bounds, new RealmId(sim.Portals.Realm[row]), in at);
            Assert.That(tx.Commit(), Is.True);
        }

        return player;
    }

    /// <summary>
    /// A client's body that walks out of a room stands on the planet's ground from the tick it lands, whichever tick the run stops on.
    /// </summary>
    /// <remarks>
    /// <b>Found live.</b> The teleport runs before the move in a tick, and the fence moves a crossing body into its new realm's cluster only at the end of
    /// it — so a body still walking was integrated once more as part of the ROOM, at its planet coordinates, and got the room's flat floor height. Walking
    /// on, the next tick corrected it; stopping on the landing kept it, and the camera that follows the server's altitude sat under the terrain. The stop
    /// can come on any tick, so the case stops the run on each of the ticks around the crossing.
    /// </remarks>
    [Test]
    public void AClientsBody_WalkingOutOfARoom_StandsOnThePlanetsGround()
    {
        for (var ticks = 3; ticks <= 7; ticks++)
        {
            using var sim = new TatooineSim(Config(ticks)) { AssetShapesOverride = Published.Shapes() };
            sim.Initialize();
            var interior = (ushort)sim.Config.Planets;
            var back = sim.Portals.Door[sim.Portals.RoomDoor(interior)];
            var player = sim.Index.Players[0];
            const float Walk = 0.2f;
            using (var tx = sim.Dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
            {
                var p = tx.OpenMut(player);
                p.Set(Player.Control, new PlayerControl { Kind = ControllerKind.Human });
                var state = p.Read(Player.State);
                state.Activity = PlayerActivity.Travelling;
                state.ActivityTicks = int.MaxValue / 2;
                p.Set(Player.State, state);
                var move = p.Read(Player.Move);
                move.VelX = back.ForwardX * Walk;
                move.VelZ = back.ForwardZ * Walk;
                move.BiasTick = 0;
                p.Set(Player.Move, move);
                var at = default(PlayerPlacement);
                at.SetAt(back.CenterX - (back.ForwardX * 0.3f), back.CenterZ - (back.ForwardZ * 0.3f), 0f, p.Read(Player.Bounds).HalfExtent);
                tx.Teleport(player, Player.Bounds, new RealmId(interior), in at);
                Assert.That(tx.Commit(), Is.True);
            }

            sim.Run();

            PlayerPlacement place;
            ushort realm;
            using (var read = sim.Dbe.CreateQuickTransaction())
            {
                var e = read.Open(player);
                place = e.Read(Player.Bounds);
                realm = e.Read(Player.Realm).Value;
            }

            if (realm != 0)
            {
                continue;   // stopped before the crossing applied
            }

            // On the planet's own frame: the terrain, or the landing in front of the door when it is still on it — never the room's floor height.
            var ground = sim.Bridge.GroundAt(RealmId.Default, place.X, place.Z);
            var landing = MathF.Max(ground, sim.Portals.Door[sim.Portals.RoomDoor(interior)].ArriveY);
            Assert.That(new[] { ground, landing }, Has.Some.EqualTo(place.Y).Within(0.05f),
                $"stopped after {ticks} ticks: standing at {place.Y:F2}, terrain {ground:F2}, landing {landing:F2}");
        }
    }

    /// <summary>
    /// A client's body walking straight through a doorway goes in.
    /// </summary>
    /// <remarks>
    /// <b>The case the disc used to lose.</b> The door's plane stands 0.6 m in front of the facade and the disc was measured to walls only, so a body could
    /// step over the plane while its disc still held, skip the query — the only place a client's crossing is tested — and never enter. Two metres out at
    /// a walk, on open ground, is exactly that: a wide disc and a plane inside it.
    /// <para>
    /// <b>14 ticks</b>: the plane is reached on the tenth. A body used to keep its velocity indoors, so a few ticks more could walk it out again through the
    /// room's way out; the case below runs long past the crossing for that reason.
    /// </para>
    /// </remarks>
    [Test]
    public void AClientsBody_WalkingThroughTheDoorway_GoesIn()
    {
        using var sim = new TatooineSim(Config(14)) { AssetShapesOverride = Published.Shapes() };
        sim.Initialize();
        var interior = (ushort)sim.Config.Planets;
        var front = sim.Portals.Door[Worlds.DoorInto(sim, interior)];
        var player = sim.Index.Players[0];
        const float Walk = 0.2f;
        using (var tx = sim.Dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
        {
            var p = tx.OpenMut(player);
            p.Set(Player.Control, new PlayerControl { Kind = ControllerKind.Human });
            var state = p.Read(Player.State);
            state.Activity = PlayerActivity.Travelling;
            state.ActivityTicks = int.MaxValue / 2;
            p.Set(Player.State, state);
            var move = p.Read(Player.Move);
            move.VelX = front.ForwardX * Walk;
            move.VelZ = front.ForwardZ * Walk;
            move.BiasTick = 0;
            p.Set(Player.Move, move);
            var at = default(PlayerPlacement);
            at.SetAt(front.CenterX - (front.ForwardX * 1.9f), front.CenterZ - (front.ForwardZ * 1.9f), 0f, p.Read(Player.Bounds).HalfExtent);
            tx.Teleport(player, Player.Bounds, RealmId.Default, in at);
            Assert.That(tx.Commit(), Is.True);
        }

        sim.Run();

        Assert.Multiple(() =>
        {
            Assert.That(sim.CrossingTotals.Entries, Is.EqualTo(1), "the body walked through the door and was never put through it");
            Assert.That(Worlds.RealmOf(sim, player), Is.EqualTo(interior));
        });
    }

    /// <summary>A client's body that goes through a door stands on the landing: it does not walk on at a destination from the realm it left.</summary>
    /// <remarks>
    /// The fourth live bot run: a bot walked into a house, turned for the way out, crossed — and was carried straight back in, then across the room into its
    /// far corner, by a destination the crossing had left in the other realm's frame. Sixty ticks is long past every way that walk could end.
    /// </remarks>
    [Test]
    public void AClientsBody_ThroughADoor_StandsOnTheLanding()
    {
        using var sim = new TatooineSim(Config(60)) { AssetShapesOverride = Published.Shapes() };
        sim.Initialize();
        var interior = (ushort)sim.Config.Planets;
        var front = sim.Portals.Door[Worlds.DoorInto(sim, interior)];
        var player = sim.Index.Players[0];
        const float Walk = 0.2f;
        using (var tx = sim.Dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
        {
            var p = tx.OpenMut(player);
            p.Set(Player.Control, new PlayerControl { Kind = ControllerKind.Human });
            var state = p.Read(Player.State);
            state.Activity = PlayerActivity.Travelling;
            state.ActivityTicks = int.MaxValue / 2;
            p.Set(Player.State, state);
            var move = p.Read(Player.Move);
            move.VelX = front.ForwardX * Walk;
            move.VelZ = front.ForwardZ * Walk;

            // A destination far on through the door, in the planet's frame — what a click past the doorway leaves behind.
            move.DestX = front.CenterX + (front.ForwardX * 500f);
            move.DestZ = front.CenterZ + (front.ForwardZ * 500f);
            move.SpeedMps = 2f;
            move.BiasTick = 0;
            p.Set(Player.Move, move);
            var at = default(PlayerPlacement);
            at.SetAt(front.CenterX - (front.ForwardX * 1.9f), front.CenterZ - (front.ForwardZ * 1.9f), 0f, p.Read(Player.Bounds).HalfExtent);
            tx.Teleport(player, Player.Bounds, RealmId.Default, in at);
            Assert.That(tx.Commit(), Is.True);
        }

        sim.Run();

        using var read = sim.Dbe.CreateQuickTransaction();
        Assert.That(read.TryOpen(player, out var body), Is.True);
        var place = body.Read(Player.Bounds);
        Assert.Multiple(() =>
        {
            Assert.That(sim.CrossingTotals.Entries, Is.EqualTo(1), "the premise: the body went in");
            Assert.That(sim.CrossingTotals.Exits, Is.Zero, "the body walked on through the room and out again");
            Assert.That(Worlds.RealmOf(sim, player), Is.EqualTo(interior));
            Assert.That(Routing2D(place.X, place.Z, front.ArriveX, front.ArriveZ), Is.LessThan(0.5f), "the body did not stand on the landing");
        });
    }

    private static float Routing2D(float ax, float az, float bx, float bz) => MathF.Sqrt(((ax - bx) * (ax - bx)) + ((az - bz) * (az - bz)));
}
