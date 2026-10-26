using System;
using System.Collections.Generic;

namespace SwgTatooine.Server;

/// <summary>
/// Every door side in the world, read once from the <see cref="Portal"/> entities at open, laid out for the tick: the plane, the landing, the twin, and the
/// path a program walks to go through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived from the database, never the other way round.</b> The doors are entities; this is their projection into arrays, rebuilt identically whether
/// the world was just built or reopened, so nothing here can disagree with what is stored. It exists because the crowd's decisions run on parallel workers
/// over thousands of bodies and want an index, not an entity open: "a random door of this city" is a lookup here.
/// </para>
/// <para>
/// <b>The approach is not stored, and comes from the asset.</b> Where a program-driven body starts walking and the line it follows is published in the
/// building's descriptor (<c>ASSET_FORMAT.md</c> § 5.7), so it is placed here with the building's own placement rather than copied onto every door row. A
/// run with no asset set walks the stand-in door's path, the same shape.
/// </para>
/// </remarks>
public sealed class PortalTable
{
    private readonly int[][][] _cityDoors;
    private readonly int[] _roomDoor;

    internal EntityId[] Id { get; }

    internal ushort[] Realm { get; }

    internal PortalDoor[] Door { get; }

    /// <summary>The other side's row.</summary>
    internal int[] Twin { get; }

    /// <summary>Where a program-driven body walks to before going through: the published approach's first point, in the door's realm.</summary>
    internal (float X, float Z)[] ApproachStart { get; }

    /// <summary>The approach's last point, past the plane: walking to it crosses.</summary>
    internal (float X, float Z)[] ApproachEnd { get; }

    /// <summary>
    /// For a room's way out, the waypoints from where a body arrives in the room to <see cref="ApproachStart"/> through the room's navigation (§ 5.9),
    /// <see cref="ApproachStart"/> last; empty for a planet door, whose approach starts on open ground.
    /// </summary>
    internal (float X, float Z)[][] Route { get; }

    /// <summary>How many door sides there are.</summary>
    public int Count => Id.Length;

    private PortalTable(int n, int planets, int realms)
    {
        Id = new EntityId[n];
        Realm = new ushort[n];
        Door = new PortalDoor[n];
        Twin = new int[n];
        ApproachStart = new (float, float)[n];
        ApproachEnd = new (float, float)[n];
        Route = new (float, float)[n][];
        _cityDoors = new int[planets][][];
        _roomDoor = new int[realms];
        Array.Fill(_roomDoor, -1);
    }

    /// <summary>An empty table, for a world with no doors.</summary>
    internal static PortalTable Empty(int planets, int realms) => new(0, planets, realms);

    /// <summary>The planet doors of one city, as rows in this table, ordered by the room they lead to.</summary>
    /// <param name="planet">The planet realm.</param>
    /// <param name="city">The city, as <see cref="WorldIndex.Cities"/> counts them.</param>
    /// <returns>The rows; empty when the city has none.</returns>
    internal ReadOnlySpan<int> CityDoors(int planet, int city)
    {
        if ((uint)planet >= (uint)_cityDoors.Length)
        {
            return default;
        }

        var cities = _cityDoors[planet];
        return cities != null && (uint)city < (uint)cities.Length ? cities[city] : default;
    }

    /// <summary>The row of a room's way out, by the room's realm, or -1 for a realm that is not a room.</summary>
    /// <param name="realm">A realm id.</param>
    /// <returns>The row, or -1.</returns>
    internal int RoomDoor(int realm) => (uint)realm < (uint)_roomDoor.Length ? _roomDoor[realm] : -1;

    /// <summary>The row of the door INTO a room, by the room's realm, or -1 for a realm that is not a room.</summary>
    /// <param name="realm">A realm id.</param>
    /// <returns>The row, or -1.</returns>
    /// <remarks>With <see cref="RoomDoor"/>, how a persisted reference to a door is resolved: rows are numbered in the order the portals enumerate at open,
    /// which nothing keeps across a reopen, while the room's realm is stored and unique per pair of sides.</remarks>
    internal int FrontDoor(int realm)
    {
        var back = RoomDoor(realm);
        return back < 0 ? -1 : Twin[back];
    }

    /// <summary>The row of a door side, by its entity, or -1.</summary>
    /// <param name="id">A <see cref="Portal"/> entity.</param>
    /// <returns>The row, or -1.</returns>
    internal int RowOf(EntityId id) => _rows.TryGetValue(id, out var row) ? row : -1;

    private readonly Dictionary<EntityId, int> _rows = [];

    /// <summary>
    /// Reads every door side and the structures they belong to.
    /// </summary>
    /// <param name="dbe">The open engine.</param>
    /// <param name="shapes">The published assets, or <see langword="null"/>: only the approach paths depend on them.</param>
    /// <param name="planets">How many planets there are.</param>
    /// <param name="cities">How many cities each planet has.</param>
    /// <param name="realms">How many realm ids the run registers, so a room's row can be found by its realm.</param>
    /// <returns>The table.</returns>
    /// <exception cref="InvalidOperationException">A door side whose twin is missing: one half of a door is not a door, and a crossing would strand a body.</exception>
    public static PortalTable Build(DatabaseEngine dbe, AssetColliders shapes, int planets, int cities, int realms)
    {
        ArgumentNullException.ThrowIfNull(dbe);

        using var tx = dbe.CreateQuickTransaction();
        var ids = new List<EntityId>();
        var doors = new List<PortalDoor>();
        var realmOf = new List<ushort>();
        foreach (var cluster in tx.For<Portal>().GetClusterEnumerator())
        {
            var door = cluster.GetReadOnlySpan(Portal.Door);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                ids.Add(cluster.GetEntityId(slot));
                doors.Add(door[slot]);
                realmOf.Add(cluster.Realm.Value);
            }
        }

        var table = new PortalTable(ids.Count, planets, realms);
        for (var i = 0; i < ids.Count; i++)
        {
            table.Id[i] = ids[i];
            table.Realm[i] = realmOf[i];
            table.Door[i] = doors[i];
            table._rows[ids[i]] = i;
        }

        var byCity = new List<(int Room, int Row)>[planets][];
        for (var p = 0; p < planets; p++)
        {
            byCity[p] = new List<(int, int)>[cities];
        }

        // Pairs the sides. The link is stored one way — a room's way out names the door into the room (WorldBuilder.BuildInteriors) — so it is
        // inverted here: one write per pair, and no stored world with a door half linked.
        Array.Fill(table.Twin, -1);
        for (var i = 0; i < ids.Count; i++)
        {
            var link = table.Door[i].Twin;
            if (link.IsNull)
            {
                continue;
            }

            var j = table.RowOf(link);
            if (j < 0 || table.Twin[j] >= 0)
            {
                throw new InvalidOperationException(
                    $"Door {ids[i]} in realm {table.Realm[i]} names a twin that is not in the database, or one another side names too.");
            }

            table.Twin[i] = j;
            table.Twin[j] = i;
        }

        using var structures = tx.For<WorldObject>();
        var route = new List<(float X, float Z)>();
        for (var i = 0; i < ids.Count; i++)
        {
            ref readonly var d = ref table.Door[i];
            if (table.Twin[i] < 0)
            {
                throw new InvalidOperationException(
                    $"Door {ids[i]} in realm {table.Realm[i]} has no other side in the database. A door needs both its sides.");
            }

            var onPlanet = table.Realm[i] < planets;
            Structure owner = default;
            StructurePlacement place = default;
            var region = 0;
            var haveOwner = false;
            if (!d.Owner.IsNull && structures.TryOpen(d.Owner, out var entity))
            {
                haveOwner = true;
                owner = entity.Read(WorldObject.Struct);
                place = entity.Read(WorldObject.Bounds);
                region = entity.Read(WorldObject.Region).Value;
            }

            if (onPlanet)
            {
                // The building's own published path, placed as the building stands. Without its asset — a run with none, or a world reopened without the
                // set it was built with — the path is the stored plane's own: straight through it, which is the shape every published path has.
                var plan = haveOwner ? shapes?.DoorFor(owner.Asset) ?? default : default;
                if (plan.Exists)
                {
                    var (sin, cos) = WorldIndex.QuarterTurn((byte)owner.Yaw);
                    table.ApproachStart[i] = Placed(place, plan.ApproachX, plan.ApproachZ, sin, cos);
                    table.ApproachEnd[i] = Placed(place, plan.ApproachEndX, plan.ApproachEndZ, sin, cos);
                }
                else
                {
                    (table.ApproachStart[i], table.ApproachEnd[i]) = Through(in d, 3f);
                }

                table.Route[i] = [];
                if (region > 0 && region <= cities)
                {
                    (byCity[table.Realm[i]][region - 1] ??= []).Add((d.TargetRealm, i));
                }

                continue;
            }

            // A room's way out, at the realm's origin: the room's published path and navigation when its asset is loaded — the owner is the room — and
            // otherwise straight through the stored plane, a metre in, which an arrival a metre or more inside reaches in a line.
            var asset = haveOwner ? owner.Asset : StructureAssets.NoAsset;
            var exit = asset != StructureAssets.NoAsset ? shapes?.DoorFor(asset) ?? default : default;
            var nav = exit.Exists ? shapes.NavFor(asset) : null;
            if (exit.Exists)
            {
                table.ApproachStart[i] = (exit.ApproachX, exit.ApproachZ);
                table.ApproachEnd[i] = (exit.ApproachEndX, exit.ApproachEndZ);
            }
            else
            {
                (table.ApproachStart[i], table.ApproachEnd[i]) = Through(in d, 1f);
            }

            // From where a body arrives — the front door's landing — through the room's graph to the node the way out starts on.
            ref readonly var front = ref table.Door[table.Twin[i]];
            route.Clear();
            var (ax, az) = table.ApproachStart[i];
            var start = nav?.NodeExactlyAt(ax, az) ?? -1;
            if (nav == null || start < 0 || !nav.TryRoute(front.ArriveX, front.ArriveZ, start, route))
            {
                // An open floor, or a room whose graph does not reach its own door: straight to the path.
                route.Clear();
                route.Add((ax, az));
            }

            table.Route[i] = [.. route];
            if (table.Realm[i] < table._roomDoor.Length)
            {
                table._roomDoor[table.Realm[i]] = i;
            }
        }

        for (var p = 0; p < planets; p++)
        {
            table._cityDoors[p] = new int[cities][];
            for (var c = 0; c < cities; c++)
            {
                var list = byCity[p][c];
                if (list == null)
                {
                    table._cityDoors[p][c] = [];
                    continue;
                }

                // By the room each leads to, which the build assigned in building order: the same doors in the same order on every open.
                list.Sort((a, b) => a.Room.CompareTo(b.Room));
                var rows = new int[list.Count];
                for (var k = 0; k < rows.Length; k++)
                {
                    rows[k] = list[k].Row;
                }

                table._cityDoors[p][c] = rows;
            }
        }

        tx.Commit();
        return table;
    }

    /// <summary>A path straight through a stored door: from <paramref name="beforeM"/> in front of its plane to a quarter metre past it.</summary>
    private static ((float X, float Z) Start, (float X, float Z) End) Through(in PortalDoor d, float beforeM) =>
        ((d.CenterX - (d.ForwardX * beforeM), d.CenterZ - (d.ForwardZ * beforeM)), (d.CenterX + (d.ForwardX * 0.25f), d.CenterZ + (d.ForwardZ * 0.25f)));

    /// <summary>A point of a building's plan, turned and moved to where the building stands.</summary>
    private static (float X, float Z) Placed(in StructurePlacement place, float x, float z, float sin, float cos) =>
        (place.X + ((x * cos) - (z * sin)), place.Z + ((x * sin) + (z * cos)));
}
