using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Building interiors (Realms G1b): every enterable city building is a realm of its own, entered and left through a portal pair — the door on the planet
/// and the entrance inside.
/// </summary>
/// <remarks>
/// <para><b>A crossing is a realm change.</b> PlayerThink queues it from its parallel workers; <see cref="TeleportTick"/> applies the queue serially with
/// <c>Transaction.Teleport</c> in one side transaction per tick (GroupCommit — see <see cref="TeleportTick"/>), and the fence moves each crossing entity into a cluster of its new realm. The entity id
/// survives: nothing is destroyed and respawned.</para>
/// <para><b>Realm ids.</b> Planets are <c>[0, Planets)</c>; portal <c>j</c> of planet <c>p</c> leads to realm <c>Planets + p · InteriorsPerPlanet + j</c>.
/// The id alone locates the door back out, so a player inside carries no extra state.</para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>What a queued crossing is, for the report.</summary>
    private enum CrossingKind : byte
    {
        Enter,
        Exit,
        InterPlanet,
    }

    /// <summary>One queued crossing: the player, where it lands, and its placement's half extent.</summary>
    private readonly record struct TeleportRequest(EntityId Id, ushort Realm, float X, float Z, float HalfExtent, CrossingKind Kind);

    /// <summary>Distance from a door's centre a player leaving the building appears at: outside the 12 m footprint.</summary>
    private const float DoorStepM = 8f;

    private readonly ConcurrentQueue<TeleportRequest> _teleports = new();

    // Realms G2: a player inside an interior pins it active — no session observes in this demo, so without the pin an occupied interior would go dormant
    // under its player and stop running it. Touched only by TeleportTick, which is serial.
    private readonly System.Collections.Generic.Dictionary<EntityId, RealmObserver> _interiorPins = [];
    private readonly System.Collections.Generic.List<TeleportRequest> _appliedCrossings = [];
    private long _droppedCrossings;

    /// <summary>True when a player inside pins interior <paramref name="realm"/> active (Realms G2) — what the demo checks read.</summary>
    public bool IsInteriorPinned(ushort realm)
    {
        foreach (var pin in _interiorPins.Values)
        {
            if (pin.Realm.Value == realm)
            {
                return true;
            }
        }

        return false;
    }
    private long _portalEntriesTick;
    private long _portalExitsTick;
    private long _portalEntries;
    private long _portalExits;
    private long _interPlanetCrossings;
    private long _teleportBatches;
    private long _teleportTicks;
    private long _teleportCommitTicks;

    /// <summary>Every planet's index, by planet realm: the doors a player leaving an interior walks out of.</summary>
    public WorldIndex[] PlanetIndexes { get; init; }

    /// <summary>Interior realms per planet, 0 without <c>--interiors</c>.</summary>
    public int InteriorsPerPlanet { get; init; }

    private bool InteriorsActive => InteriorsPerPlanet > 0;

    /// <summary>Crossings so far: into interiors, out of them, and between planets by shuttle.</summary>
    public (long Entries, long Exits, long InterPlanet) CrossingTotals => (_portalEntries, _portalExits, _interPlanetCrossings);

    /// <summary>
    /// An idle decision in a city walks into one of its buildings with probability <see cref="SimConfig.InteriorShare"/>. False leaves the decision to the
    /// ordinary idle branch, untouched.
    /// </summary>
    private bool TryWalkToPortal(ref PlayerState state, ref PlayerMotion move, float x, float z, ushort planet, uint shareSalt, uint pickSalt)
    {
        if (!InteriorsActive || Hash01(shareSalt) >= _config.InteriorShare)
        {
            return false;
        }

        var city = CityAt(x, z);
        if (city < 0)
        {
            return false;
        }

        // Planets share the map, so a city's run of portals is the same on each; the doors differ (each planet draws its own world).
        var (first, count) = _index.CityPortals[city];
        if (count == 0)
        {
            return false;
        }

        var portal = first + Math.Min(count - 1, (int)(Hash01(pickSalt) * count));
        state.Activity = PlayerActivity.ToPortal;
        state.ActivityTicks = 200 * _config.TickRateHz;
        state.ShuttleDest = portal;
        move.SpeedMps = TatooineData.PlayerRunSpeedMps;
        var (dx, dz) = PlanetIndexes[planet].Portals[portal];
        move.DestX = dx;
        move.DestZ = dz;
        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
        return true;
    }

    /// <summary>
    /// A shuttle bound for another planet (Realms G1b): the boarding is a realm change, so it is queued for <see cref="TeleportTick"/>, which runs after the
    /// Shuttle system in the same phase, rather than written in place.
    /// </summary>
    private void BoardInterPlanet(EntityId id, ushort fromPlanet, int destCity, float halfExtent, uint salt)
    {
        var destPlanet = (ushort)((fromPlanet + 1 + Math.Min(_config.Planets - 2, (int)(Hash01(salt) * (_config.Planets - 1)))) % _config.Planets);
        var (portX, portZ) = PlanetIndexes[destPlanet].Shuttleports[destCity];
        var r = ArrivalScatterM * MathF.Sqrt(Hash01(salt ^ 0x2F9B1D63u));
        var a = Hash01(salt ^ 0x6C8E9CF5u) * MathF.PI * 2f;
        var half = (_config.WorldEdgeM * 0.5f) - halfExtent;
        _teleports.Enqueue(new TeleportRequest(id, destPlanet, Math.Clamp(portX + (MathF.Cos(a) * r), -half, half),
            Math.Clamp(portZ + (MathF.Sin(a) * r), -half, half), halfExtent, CrossingKind.InterPlanet));
    }

    /// <summary>A doorway in the frame a body walks in, as the crossing test needs it.</summary>
    /// <param name="CenterX">Where the opening is.</param>
    /// <param name="CenterZ">Where the opening is.</param>
    /// <param name="ForwardX">Unit vector through the door, the way a body crosses it.</param>
    /// <param name="ForwardZ">Unit vector through the door.</param>
    /// <param name="HalfX">Half the opening width, across the forward.</param>
    private readonly record struct DoorPlane(float CenterX, float CenterZ, float ForwardX, float ForwardZ, float HalfX);

    /// <summary>
    /// Whether a step crossed a doorway, going in.
    /// </summary>
    /// <param name="door">The doorway.</param>
    /// <param name="fromX">Where the body stood after the previous step resolved.</param>
    /// <param name="fromZ">Where the body stood after the previous step resolved.</param>
    /// <param name="toX">Where it stands after this one resolved.</param>
    /// <param name="toZ">Where it stands after this one resolved.</param>
    /// <returns>Whether it went through.</returns>
    /// <remarks>
    /// <para>
    /// <b>A directed crossing of the doorway's mid-plane, not "is the body inside the doorway box".</b> The published opening is 0.8 m deep and the tick
    /// is 10 Hz, so a sampled test is a function of speed: a run covers 0.50 m in a tick and fits, a burst run covers 0.91 m and can be in front of the
    /// opening on one tick and behind it on the next without ever being inside it, and a mount at 1.20 m never fits at all. The door would have worked for
    /// a walker and failed silently for anyone in a hurry. A segment against the plane is tick-rate independent, and it is what the physics session asked
    /// for when it was shown the sampled version.
    /// </para>
    /// <para>
    /// <b>Direction is what makes it need no state.</b> Crossing counts only from the outside face to the inside one, so a body that stops in a doorway and
    /// turns round leaves against the forward and does not fire; and the arrival spawn on the far side is published clear of the twin opening (0.65 m
    /// outside it going out, 0.8 m going in), so an arrival cannot re-trigger. No per-body armed flag, which the testbench needed because it tested
    /// containment.
    /// </para>
    /// <para>
    /// <b>In the plane, not in the volume — so the height of the probe does not enter.</b> The published opening spans 0.7 m to 3.75 m in asset space with
    /// the threshold at 0.7, so a body's feet sit exactly on the bottom face and a containment test has to probe the mid-body to be stable. A plane test in
    /// the ground plane has no such sensitivity. What it does assume is that a body cannot be at a door's position and at another altitude: true while
    /// every published door is at ground level and the collision pass keeps bodies out of walls, and false the day a building has a balcony door.
    /// </para>
    /// </remarks>
    private static bool CrossedDoor(in DoorPlane door, float fromX, float fromZ, float toX, float toZ)
    {
        var d0 = ((fromX - door.CenterX) * door.ForwardX) + ((fromZ - door.CenterZ) * door.ForwardZ);
        var d1 = ((toX - door.CenterX) * door.ForwardX) + ((toZ - door.CenterZ) * door.ForwardZ);
        if (d0 >= 0f || d1 < 0f)
        {
            return false;
        }

        // Where the step met the plane, and how far off the middle of the opening that was: a body walking past the wall beside a door must not enter it.
        var t = d0 / (d0 - d1);
        var px = fromX + ((toX - fromX) * t);
        var pz = fromZ + ((toZ - fromZ) * t);
        var lateral = ((px - door.CenterX) * -door.ForwardZ) + ((pz - door.CenterZ) * door.ForwardX);
        return MathF.Abs(lateral) <= door.HalfX;
    }

    /// <summary>The crossing rule, for a test: no world, no realms, just a doorway and a step.</summary>
    /// <param name="centerX">The doorway's centre.</param>
    /// <param name="centerZ">The doorway's centre.</param>
    /// <param name="forwardX">The direction through it.</param>
    /// <param name="forwardZ">The direction through it.</param>
    /// <param name="halfX">Half the opening's width.</param>
    /// <param name="fromX">Where the body stood.</param>
    /// <param name="fromZ">Where the body stood.</param>
    /// <param name="toX">Where it stands now.</param>
    /// <param name="toZ">Where it stands now.</param>
    /// <returns>Whether the step went through.</returns>
    internal static bool CrossedDoorForTest(
        float centerX,
        float centerZ,
        float forwardX,
        float forwardZ,
        float halfX,
        float fromX,
        float fromZ,
        float toX,
        float toZ) =>
        CrossedDoor(new DoorPlane(centerX, centerZ, forwardX, forwardZ, halfX), fromX, fromZ, toX, toZ);

    /// <summary>The doorway of planet <paramref name="planet"/>'s portal <paramref name="portal"/>, in planet coordinates.</summary>
    /// <param name="planet">The planet realm.</param>
    /// <param name="portal">The portal index.</param>
    /// <returns>The doorway, or null when the asset publishes none.</returns>
    private DoorPlane? PlanetDoor(ushort planet, int portal)
    {
        var index = PlanetIndexes[planet];
        if ((uint)portal >= (uint)index.Portals.Count || AssetShapes == null)
        {
            return null;
        }

        var (yaw, asset) = index.PortalFacings[portal];
        var plan = AssetShapes.DoorFor(asset);
        if (!plan.Exists)
        {
            return null;
        }

        // The asset's own forward, turned by the quarter the building was spawned at. NOT the "-Z" the field names:
        // that is a local axis, and the portal's own rotation is part of the answer. See AssetColliders.ForwardOf.
        var (sin, cos) = QuarterTurn(yaw);
        var fx = (plan.ForwardX * cos) - (plan.ForwardZ * sin);
        var fz = (plan.ForwardX * sin) + (plan.ForwardZ * cos);
        var (cx, cz) = index.Portals[portal];
        return new DoorPlane(cx, cz, fx, fz, plan.HalfX);
    }

    /// <summary>At the door: queue the crossing into portal <see cref="PlayerState.ShuttleDest"/>'s interior on planet <paramref name="planet"/>.</summary>
    private void EnterPortal(ref PlayerState state, EntityId id, ushort planet, float halfExtent, uint salt)
    {
        var portal = state.ShuttleDest;
        if (planet >= _config.Planets || (uint)portal >= (uint)InteriorsPerPlanet)
        {
            state.Activity = PlayerActivity.Idle;
            state.ActivityTicks = 0;
            return;
        }

        var realm = (ushort)(_config.Planets + (planet * InteriorsPerPlanet) + portal);
        var c = WorldBuilder.InteriorEdgeM * 0.5f;
        var jitter = (Hash01(salt) - 0.5f) * 8f;
        _teleports.Enqueue(new TeleportRequest(id, realm, c + jitter, 6f, halfExtent, CrossingKind.Enter));
        state.Activity = PlayerActivity.Inside;
        var stayTicks = _config.InteriorStayS * _config.TickRateHz;
        state.ActivityTicks = Math.Max(1, (int)(stayTicks * (1f + (3f * Hash01(salt ^ 0x68E31DA4u)))));
    }

    /// <summary>Leaving interior <paramref name="realm"/>: queue the crossing back to its door, a step outside it.</summary>
    private void ExitInterior(ref PlayerState state, ref PlayerMotion move, EntityId id, ushort realm, float halfExtent, uint salt)
    {
        var k = realm - _config.Planets;
        var planet = k / InteriorsPerPlanet;
        var (doorX, doorZ) = PlanetIndexes[planet].Portals[k % InteriorsPerPlanet];
        var a = Hash01(salt) * MathF.PI * 2f;
        var half = (_config.WorldEdgeM * 0.5f) - halfExtent;
        _teleports.Enqueue(new TeleportRequest(id, (ushort)planet, Math.Clamp(doorX + (MathF.Cos(a) * DoorStepM), -half, half),
            Math.Clamp(doorZ + (MathF.Sin(a) * DoorStepM), -half, half), halfExtent, CrossingKind.Exit));
        move.VelX = 0f;
        move.VelZ = 0f;
        state.Activity = PlayerActivity.Idle;
        state.ActivityTicks = (10 * _config.TickRateHz) + (int)(Hash01(salt ^ 0x1F83D9ABu) * 30 * _config.TickRateHz);
    }

    /// <summary>
    /// A body a client is driving has walked somewhere: if that step went through a doorway, queue the crossing.
    /// </summary>
    /// <param name="id">The body.</param>
    /// <param name="realm">The realm it is in.</param>
    /// <param name="halfExtent">Its half-extent, for the landing.</param>
    /// <param name="fromX">Where it stood after the previous step resolved.</param>
    /// <param name="fromZ">Where it stood after the previous step resolved.</param>
    /// <param name="toX">Where it stands now.</param>
    /// <param name="toZ">Where it stands now.</param>
    /// <returns>Whether a crossing was queued.</returns>
    /// <remarks>
    /// <para>
    /// <b>Possessed bodies had no way through a door at all.</b> Every portal path in this class is reached from
    /// <c>PlayerThink</c>, which returns at its first test for anything a client is driving (SWG-01: two deciders on one
    /// entity is the bug that guard exists for). So the scripted crowd walked in and out of buildings while a player could
    /// stand in the doorway for ever. This is the other half, and it decides from geometry alone -- where the body was and
    /// where it is -- so it needs no activity, no timer and no server-side intent, and cannot fight the client for the
    /// entity.
    /// </para>
    /// <para>
    /// <b>Both directions, and the realm says which.</b> On a planet a step may cross any door of the city the body stands
    /// in; in an interior the only door is the one back out. Asking the city rather than the planet is what keeps this a
    /// handful of plane tests: the dense case is a town of about 170 doors, and a body is in at most one town.
    /// </para>
    /// </remarks>
    private bool TryCrossDoor(EntityId id, ushort realm, float halfExtent, float fromX, float fromZ, float toX, float toZ)
    {
        if (!InteriorsActive || AssetShapes == null || (fromX == toX && fromZ == toZ))
        {
            return false;
        }

        if (realm < _config.Planets)
        {
            var city = CityAt(toX, toZ);
            if (city < 0)
            {
                return false;
            }

            var (first, count) = _index.CityPortals[city];
            for (var j = first; j < first + count; j++)
            {
                var door = PlanetDoor(realm, j);
                if (door.HasValue && CrossedDoor(door.Value, fromX, fromZ, toX, toZ))
                {
                    return EnterRoom(id, realm, j, halfExtent);
                }
            }

            return false;
        }

        var k = realm - _config.Planets;
        var planet = (ushort)(k / InteriorsPerPlanet);
        var portal = k % InteriorsPerPlanet;
        var back = RoomDoor(planet, portal);
        if (!back.HasValue || !CrossedDoor(back.Value, fromX, fromZ, toX, toZ))
        {
            return false;
        }

        var index = PlanetIndexes[planet];
        var (yaw, asset) = index.PortalFacings[portal];
        var plan = AssetShapes.DoorFor(asset);
        var (doorX, doorZ) = index.Portals[portal];
        var (sin, cos) = QuarterTurn(yaw);
        var half = (_config.WorldEdgeM * 0.5f) - halfExtent;

        // A step back out along the doorway's outward normal, which is the one direction the facade is not.
        var outX = doorX - (((plan.ForwardX * cos) - (plan.ForwardZ * sin)) * DoorStepM);
        var outZ = doorZ - (((plan.ForwardX * sin) + (plan.ForwardZ * cos)) * DoorStepM);
        _teleports.Enqueue(new TeleportRequest(id, planet, Math.Clamp(outX, -half, half), Math.Clamp(outZ, -half, half), halfExtent, CrossingKind.Exit));
        return true;
    }

    /// <summary>Queues a client-driven body into the room behind planet <paramref name="planet"/>'s portal.</summary>
    /// <param name="id">The body.</param>
    /// <param name="planet">The planet it is leaving.</param>
    /// <param name="portal">Which door.</param>
    /// <param name="halfExtent">Its half-extent.</param>
    /// <returns>Whether the crossing was queued.</returns>
    private bool EnterRoom(EntityId id, ushort planet, int portal, float halfExtent)
    {
        if ((uint)portal >= (uint)InteriorsPerPlanet)
        {
            return false;
        }

        var realm = (ushort)(_config.Planets + (planet * InteriorsPerPlanet) + portal);
        var room = AssetShapes.RoomFor(PlanetIndexes[planet].PortalFacings[portal].Asset);

        // The published arrival point when the asset names a room, and the middle of the realm when it does not: a door
        // whose interior will not read is a publishing fault, and refusing the crossing would strand the body in a wall.
        var x = room.Exists ? room.EntryX : WorldBuilder.InteriorEdgeM * 0.5f;
        var z = room.Exists ? room.EntryZ : 6f;
        _teleports.Enqueue(new TeleportRequest(id, realm, x, z, halfExtent, CrossingKind.Enter));
        return true;
    }

    /// <summary>The doorway back out of planet <paramref name="planet"/>'s interior <paramref name="portal"/>, in room coordinates.</summary>
    /// <param name="planet">The planet that owns it.</param>
    /// <param name="portal">Which interior.</param>
    /// <returns>The doorway, or null when the asset publishes no room.</returns>
    private DoorPlane? RoomDoor(ushort planet, int portal)
    {
        if (AssetShapes == null || (uint)portal >= (uint)PlanetIndexes[planet].PortalFacings.Count)
        {
            return null;
        }

        var room = AssetShapes.RoomFor(PlanetIndexes[planet].PortalFacings[portal].Asset);

        // A room is drawn at the identity, so its doorway needs no placement applied. Its forward is the one the
        // descriptor's own rotation gives, which for every interior in this pack is the opposite of the way in.
        return room.Exists ? new DoorPlane(room.DoorX, room.DoorZ, room.DoorForwardX, room.DoorForwardZ, room.DoorHalfX) : null;
    }

    /// <summary>Sine and cosine of a placement quarter turn; see <see cref="WorldIndex.QuarterTurn"/> for why it is not the obvious table.</summary>
    /// <param name="quarters">The quarter turns, 0 to 3.</param>
    /// <returns>The pair.</returns>
    private static (float Sin, float Cos) QuarterTurn(byte quarters) => WorldIndex.QuarterTurn(quarters);

    /// <summary>
    /// Apply last tick's crossings: one side transaction with the Commit discipline, so the crossings are committed — atomically, each in one realm or the
    /// other — before the fence moves them.
    /// </summary>
    /// <remarks>
    /// GroupCommit durability, not 02 § G1's Immediate (a deviation): Immediate measured ~1.4 ms per batch on this serial path (0.5 ms opening the
    /// transaction, 0.8 ms flushing at dispose), against ~0.1 ms here; the price is a crossing at risk for one group-commit interval (5 ms by default),
    /// after which a crash reopens the player on whichever side it had durably reached.
    /// </remarks>
    public void TeleportTick(TickContext ctx)
    {
        // Stopped by a client (TatooineReplication.SetPaused, a demo control). The simulation does nothing; replication,
        // the session system and the engine's own stages keep running, or no client could ever ask to resume.
        if (TatooineReplication.SimulationPaused)
        {
            return;
        }

        if (_teleports.IsEmpty)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        long entries = 0;
        long exits = 0;
        long planets = 0;
        _appliedCrossings.Clear();
        using (var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
        {
            while (_teleports.TryDequeue(out var r))
            {
                // A crossing that can no longer apply — its destination closed or gone, its player gone — is dropped, not allowed to throw: one bad
                // request must not abort the tick and lose every crossing already dequeued (review #4).
                var dest = new RealmId(r.Realm);
                if (!Dbe.Realms.IsRegistered(dest) || Dbe.Realms.StateOf(dest) == RealmRunState.Closing || !tx.IsAlive(r.Id))
                {
                    _droppedCrossings++;
                    continue;
                }

                var nb = default(PlayerPlacement);
                // The DESTINATION realm decides the altitude: a crossing into a building lands on its flat floor, and one
                // back out lands on the planet's relief at the door.
                nb.SetAt(r.X, r.Z, GroundAt(dest, r.X, r.Z), r.HalfExtent);
                tx.Teleport(r.Id, Player.Bounds, dest, in nb);
                _appliedCrossings.Add(r);
                switch (r.Kind)
                {
                    case CrossingKind.Enter:
                        entries++;
                        break;
                    case CrossingKind.Exit:
                        exits++;
                        break;
                    default:
                        planets++;
                        break;
                }
            }

            var commit = Stopwatch.GetTimestamp();
            tx.Commit();
            _teleportCommitTicks += Stopwatch.GetTimestamp() - commit;
        }

        // Pins follow the COMMITTED crossings only.
        foreach (var r in _appliedCrossings)
        {
            if (r.Kind == CrossingKind.Enter && _config.InteriorSleepS > 0f && !_interiorPins.ContainsKey(r.Id))
            {
                _interiorPins[r.Id] = Dbe.Realms.Observe(new RealmId(r.Realm));
            }
            else if (r.Kind != CrossingKind.Enter && _interiorPins.Remove(r.Id, out var pin))
            {
                pin.Dispose();
            }
        }

        _teleportTicks += Stopwatch.GetTimestamp() - start;
        _teleportBatches++;
        _portalEntries += entries;
        _portalExits += exits;
        _interPlanetCrossings += planets;
        Interlocked.Add(ref _portalEntriesTick, entries);
        Interlocked.Add(ref _portalExitsTick, exits);
    }

    /// <summary>What the portals did over the run.</summary>
    public void PrintPortalReport()
    {
        if (_teleportBatches == 0)
        {
            return;
        }

        var batches = _teleportBatches;
        Console.WriteLine();
        Console.WriteLine($"  crossings: {InteriorsPerPlanet * _config.Planets:N0} interior realms; {_portalEntries:N0} entries, {_portalExits:N0} exits, "
            + $"{_interPlanetCrossings:N0} inter-planet in {_teleportBatches:N0} batches; per batch "
            + $"{(double)(_portalEntries + _portalExits + _interPlanetCrossings) / batches:F1} crossings, "
            + $"{Stopwatch.GetElapsedTime(0, _teleportTicks / batches).TotalMicroseconds:F0} us of which commit "
            + $"{Stopwatch.GetElapsedTime(0, _teleportCommitTicks / batches).TotalMicroseconds:F0} us");
    }
}
