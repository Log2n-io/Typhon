using System;
using System.Collections.Generic;
using System.Numerics;

namespace SwgTatooine;

/// <summary>
/// Rebuilding a planet's <see cref="WorldIndex"/> and <see cref="WorldCensus"/> from the entities on disk, for a world that is reopened rather than built (P-2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not "just run the generator again".</b> The generator is a pure function of the seed, so re-running it would reproduce the same coordinates —
/// and that is precisely the version that must not be written. A world on disk has been TICKED: creatures have wandered and died, players have travelled and
/// been cloned, mission lairs have teleported a kilometre and back. An index re-derived from the seed would describe the world as it was born, and every reader
/// of it — the shuttle table, the portal table, the clone destinations — would be pointing at where things used to be. It would also mean the demo could never
/// open a database it had not generated itself, which is the case that matters for a server that restarts.
/// </para>
/// <para>
/// <b>What comes from the map and what comes from the entities.</b> Cities and points of interest are geometry the map states outright, identical in every
/// world built from the same configuration, so they are read from the map — deriving them from entities would be inference where a fact is available. Everything
/// the generator's RNG produced — which building in a city is its shuttleport, which buildings have doors and in what order — exists nowhere but on the entities,
/// and is read from them. The split is the answer to "could the map have known this?".
/// </para>
/// <para>
/// <b>It walks clusters rather than opening entities.</b> A per-entity <c>Open</c> over a reopened million-entity world is a million dictionary lookups; the
/// cluster enumerator reads the same components out of contiguous spans at a few nanoseconds each. This runs once per planet at startup, so the difference is
/// between a reopen measured in milliseconds and one measured in seconds.
/// </para>
/// </remarks>
public static class WorldRebuild
{
    /// <summary>
    /// Rebuilds <paramref name="index"/> for one planet from what is on disk, and returns the census of what was found.
    /// </summary>
    /// <param name="dbe">The open engine.</param>
    /// <param name="map">The planet's geometry — the half of the index that is a fact rather than a recollection.</param>
    /// <param name="index">The index to fill. Must be empty.</param>
    /// <param name="realm">Which planet.</param>
    /// <param name="interiorRealms">This planet's interior realms, whose NPCs count toward its census; empty without <c>--interiors</c>.</param>
    /// <returns>The census of the entities actually present, which is what makes a reopened run comparable with the one that built the world.</returns>
    public static WorldCensus Rebuild(DatabaseEngine dbe, TatooineMap map, WorldIndex index, ushort realm, ReadOnlySpan<ushort> interiorRealms = default)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(index);

        // The "must be empty" above was a doc comment and nothing else. A second call appends a second copy of every city,
        // shuttleport, door and player, and none of it fails loudly: `PickCityIndex` walks a weight list summing to 2 and
        // returns early so half the cities stop being reachable, one dungeon can draw the same player into its party
        // twice, and the door check below then throws a message blaming the map for a doubled count of its own making.
        if (index.Cities.Count != 0 || index.Players.Count != 0 || index.Portals.Count != 0)
        {
            throw new InvalidOperationException(
                $"Rebuild needs an empty index; this one already holds {index.Cities.Count} cities, {index.Players.Count} players and "
                + $"{index.Portals.Count} doors. Rebuilding on top of a filled index doubles every list it appends to.");
        }

        var census = new WorldCensus();

        // The map's own geometry, in the map's own order — the same values SpawnCities and SpawnPointsOfInterest record.
        foreach (var city in map.Cities)
        {
            index.Cities.Add((city.X, city.Z, city.Radius, city.PlayerWeight));
        }

        foreach (var poi in map.Pois)
        {
            index.Pois.Add((poi.X, poi.Z, poi.Radius));
        }

        using var tx = dbe.CreateQuickTransaction();
        RebuildStructures(tx, index, census, realm);
        RebuildLairs(tx, index, census, realm);
        RebuildCreatures(tx, census, realm);
        RebuildNpcs(tx, census, realm);
        RebuildPlayers(tx, index, census, realm);
        tx.Commit();

        // Interior NPCs count toward the planet that owns their buildings, which is what PopulateInteriors does on the build path — so a reopened run's census
        // matches the run that built the world instead of being short by however many interiors it had.
        if (interiorRealms.Length > 0)
        {
            census.CityNpcs += CountNpcsIn(dbe, interiorRealms);
        }

        BuildCityPortalRuns(map, index);
        return census;
    }

    /// <summary>
    /// The shuttleports and the doors, which only the entities know. Also the static/player-structure split, which <see cref="Structure.Kind"/> states.
    /// </summary>
    /// <remarks>
    /// Written into pre-sized slots rather than appended, because the order is load-bearing on both lists: <c>Shuttleports[c]</c> must be city <c>c</c>'s port,
    /// and <c>Portals[j]</c> must be the door that leads to interior realm <c>Planets + p·N + j</c>. A spatial index returns clusters in whatever order it holds
    /// them, so an append would produce a correct set in an arbitrary order — which is the subtlest way this could look right and be wrong.
    /// </remarks>
    private static void RebuildStructures(Transaction tx, WorldIndex index, WorldCensus census, ushort realm)
    {
        var ports = new (float X, float Z)[index.Cities.Count];
        var portsSeen = new bool[index.Cities.Count];

        // Grown on demand: the door count is a property of the world on disk, and trusting the map's count here would make a mismatch invisible.
        var doors = new (float X, float Z)[16];
        var doorsSeen = new bool[16];
        var maxDoor = -1;

        foreach (var cluster in tx.For<WorldObject>().GetClusterEnumerator())
        {
            if (cluster.Realm.Value != realm)
            {
                continue;
            }

            var places = cluster.GetReadOnlySpan(WorldObject.Bounds);
            var structs = cluster.GetReadOnlySpan(WorldObject.Struct);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                ref readonly var st = ref structs[slot];
                var x = places[slot].X;
                var z = places[slot].Z;

                switch (st.Kind)
                {
                    case StructureKind.PlayerHouse:
                    case StructureKind.Factory:
                    case StructureKind.Harvester:
                        census.PlayerStructures++;
                        break;
                    default:
                        census.StaticObjects++;
                        break;
                }

                if (st.Kind == StructureKind.Shuttleport && (uint)st.OwnerRegion < (uint)ports.Length)
                {
                    ports[st.OwnerRegion] = (x, z);
                    portsSeen[st.OwnerRegion] = true;
                }

                if (st.PortalIndex >= 0)
                {
                    if (st.PortalIndex >= doors.Length)
                    {
                        var grown = Math.Max(st.PortalIndex + 1, doors.Length * 2);
                        Array.Resize(ref doors, grown);
                        Array.Resize(ref doorsSeen, grown);
                    }

                    doors[st.PortalIndex] = (x, z);
                    doorsSeen[st.PortalIndex] = true;
                    maxDoor = Math.Max(maxDoor, st.PortalIndex);
                }
            }
        }

        for (var c = 0; c < ports.Length; c++)
        {
            // Loud, not defaulted. A missing port would otherwise become (0, 0) — the centre of the planet — and every shuttle passenger from that city would
            // walk into the desert, which is a behaviour nobody would trace back to a rebuild.
            if (!portsSeen[c])
            {
                throw new InvalidOperationException(
                    $"Reopened world: city {c} has no shuttleport on disk. The database was built by an incompatible version, or it is damaged.");
            }

            index.Shuttleports.Add(ports[c]);
        }

        for (var j = 0; j <= maxDoor; j++)
        {
            if (!doorsSeen[j])
            {
                throw new InvalidOperationException(
                    $"Reopened world: door {j} is missing while door {maxDoor} is present, so the door list has a hole and every interior realm after it "
                    + "would be off by one.");
            }

            index.Portals.Add(doors[j]);
        }
    }

    /// <summary>Each city's run in <see cref="WorldIndex.Portals"/>, recomputed from the map exactly as the build computes it.</summary>
    /// <remarks>
    /// From the map rather than from the entities, and this is the one place where that needs saying: the run lengths follow from
    /// <c>WorldBuilder.IsEnterable</c> over each city's building count, which the map states. Reading them off the entities instead would need the building's
    /// index within its city, which nothing stores — and would gain nothing, because the two must agree or the door list is wrong anyway. The count is checked
    /// against what was actually found.
    /// </remarks>
    private static void BuildCityPortalRuns(TatooineMap map, WorldIndex index)
    {
        var first = 0;
        foreach (var city in map.Cities)
        {
            var n = 0;
            for (var i = 0; i < city.Buildings; i++)
            {
                n += WorldBuilder.IsEnterable(i) ? 1 : 0;
            }

            index.CityPortals.Add((first, n));
            first += n;
        }

        // No `!= 0` escape hatch: zero doors on disk is exactly the mismatch worth catching. A world built without
        // --interiors and reopened with it has none, while the map still accounts for `first` of them, and `TryWalkToPortal`
        // then indexes an empty list inside PlayerThinkTick — a throw from the tick path, which is never allowed.
        if (index.Portals.Count != first)
        {
            throw new InvalidOperationException(
                $"Reopened world: {index.Portals.Count} doors on disk but the map's city layout accounts for {first}. The database was built from a "
                + "different map or a different content scale.");
        }
    }

    private static void RebuildLairs(Transaction tx, WorldIndex index, WorldCensus census, ushort realm)
    {
        foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
        {
            if (cluster.Realm.Value != realm)
            {
                continue;
            }

            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                index.Lairs.Add(cluster.GetEntityId(BitOperations.TrailingZeroCount(bits)));
                census.Lairs++;
            }
        }
    }

    /// <summary>The creature count and its composition by template — the number that makes one run's world comparable with another's (S0-5).</summary>
    private static void RebuildCreatures(Transaction tx, WorldCensus census, ushort realm)
    {
        foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
        {
            if (cluster.Realm.Value != realm)
            {
                continue;
            }

            var brains = cluster.GetReadOnlySpan(Creature.Ai);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                census.Creatures++;
                var template = brains[slot].Template;
                if (template < census.CreaturesByTemplate.Length)
                {
                    census.CreaturesByTemplate[template]++;
                }
            }
        }
    }

    /// <summary>The starships in the space realm (Realms G1c), counted rather than spawned.</summary>
    public static int CountStarships(DatabaseEngine dbe, ushort realm)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        using var tx = dbe.CreateQuickTransaction();
        var n = 0;
        foreach (var cluster in tx.For<Starship>().GetClusterEnumerator())
        {
            if (cluster.Realm.Value == realm)
            {
                n += BitOperations.PopCount(cluster.OccupancyBits);
            }
        }

        tx.Commit();
        return n;
    }

    private static void RebuildNpcs(Transaction tx, WorldCensus census, ushort realm)
    {
        foreach (var cluster in tx.For<CityNpc>().GetClusterEnumerator())
        {
            if (cluster.Realm.Value != realm)
            {
                continue;
            }

            census.CityNpcs += BitOperations.PopCount(cluster.OccupancyBits);
        }
    }

    /// <summary>City NPCs across a set of realms — the interiors of one planet.</summary>
    private static int CountNpcsIn(DatabaseEngine dbe, ReadOnlySpan<ushort> realms)
    {
        using var tx = dbe.CreateQuickTransaction();
        var n = 0;
        foreach (var cluster in tx.For<CityNpc>().GetClusterEnumerator())
        {
            var value = cluster.Realm.Value;
            foreach (var realm in realms)
            {
                if (value == realm)
                {
                    n += BitOperations.PopCount(cluster.OccupancyBits);
                    break;
                }
            }
        }

        tx.Commit();
        return n;
    }

    /// <summary>
    /// The players, and the one piece of per-entity repair a reopen needs: a possessed player has no session any more.
    /// </summary>
    /// <remarks>
    /// <b>Possession does not survive a restart and must not appear to.</b> <c>PlayerControl.Kind</c> and <c>PlayerSession.Controller</c> are ordinary
    /// persisted components, so a world saved while a client was connected reopens with a player marked as that client's — and the client is gone. Nothing would
    /// ever clear it: <c>PlayerThink</c> skips anything not <c>InProcess</c>, so the player would stand still for the life of the process while the simulation
    /// politely waited for intents from a session that does not exist. Handing them back is the only correct answer, and doing it here is what makes the count
    /// this method returns the count of players the simulation will actually drive.
    /// </remarks>
    private static void RebuildPlayers(Transaction tx, WorldIndex index, WorldCensus census, ushort realm)
    {
        // Collected on the read walk and released afterwards, rather than written through the cluster span in place.
        //
        // A mutable cluster span outside system dispatch fails on `ChunkAccessor must be created inside an epoch scope`: the scope a system body runs under is
        // the scheduler's, and a plain transaction walking clusters to READ has no equivalent. It is a real constraint rather than an inconvenience — the write
        // would be to pages nothing is holding open. Per-entity OpenMut is the supported path and costs nothing here, because the list is the players a client
        // happened to be driving when the world was saved: usually none, never many.
        var possessed = new List<EntityId>();

        foreach (var cluster in tx.For<Player>().GetClusterEnumerator())
        {
            if (cluster.Realm.Value != realm)
            {
                continue;
            }

            var controls = cluster.GetReadOnlySpan(Player.Control);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                var id = cluster.GetEntityId(slot);
                index.Players.Add(id);
                census.Players++;
                if (controls[slot].Kind != ControllerKind.InProcess)
                {
                    possessed.Add(id);
                }
            }
        }

        foreach (var id in possessed)
        {
            if (!tx.TryOpenMut(id, out var player))
            {
                continue;
            }

            var control = player.Read(Player.Control);
            control.Kind = ControllerKind.InProcess;
            player.Set(Player.Control, control);
            var session = player.Read(Player.Session);
            session.Controller = 0u;
            session.Target = EntityId.Null;
            player.Set(Player.Session, session);
        }

        if (possessed.Count != 0)
        {
            Console.WriteLine($"  .. {possessed.Count} player(s) were possessed when this world was last saved; their sessions are gone, so they are back on "
                + "the simulation's own activity mix");
        }
    }
}
