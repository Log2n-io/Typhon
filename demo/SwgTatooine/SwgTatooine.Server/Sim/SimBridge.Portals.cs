using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Building interiors (Realms G1b): every enterable city building has a room of its own, in a realm of its own, and a door with a side in each.
/// </summary>
/// <remarks>
/// <para><b>A crossing is a realm change.</b> A body's step over a door plane queues it, from parallel workers; <see cref="TeleportTick"/> applies the
/// queue serially with <c>Transaction.Teleport</c> in one side transaction per tick (GroupCommit — see <see cref="TeleportTick"/>), and the fence moves
/// each crossing entity into a cluster of its new realm. The entity id survives: nothing is destroyed and respawned.</para>
/// <para><b>Everything a crossing reads is stored.</b> Each side of a door is a <see cref="Portal"/> entity carrying its plane, the realm on the other side,
/// the landing there, its twin and the structure it belongs to; <see cref="PortalTable"/> is those rows laid out for the tick. No realm id is computed
/// here: the door says where it goes.</para>
/// <para><b>One test for everyone.</b> A body a client drives crosses when its resolved step goes over a plane (<see cref="DoorsAround"/>, in the collision
/// pass); the scripted crowd walks the door's published path and crosses on the same test, in <see cref="ThinkPortal"/>. Both land on the published spawn
/// the door names, and both leave a room through its way out.</para>
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
    /// <param name="Y">The feet's height there — a door's stored landing, or the ground: the landing is this or the ground, whichever is higher.</param>
    /// <param name="Retried">Whether this is the second attempt, after a batch that did not commit; a second failure drops it.</param>
    private readonly record struct TeleportRequest(EntityId Id, ushort Realm, float X, float Z, float HalfExtent, CrossingKind Kind, float Y,
        bool Retried = false);

    /// <summary>How long a scripted body may take to walk through a door before it gives up and decides something else, in seconds.</summary>
    /// <remarks>The path is a few metres; a body that has not crossed in this long is blocked — by another body, or by a neighbour the placer let stand
    /// on the path — and standing there for ever is the one behaviour that is never right.</remarks>
    private const float DoorWalkTimeoutS = 15f;

    /// <summary>How long a scripted body may take to walk out of a room before it is put through the door regardless, in seconds.</summary>
    /// <remarks>Giving up is not an option inside: there is no other way out, and a body that cannot leave would hold its room awake for ever.</remarks>
    private const float RoomWalkTimeoutS = 30f;

    /// <summary>How close counts as at a waypoint, metres. The steering lands a body exactly on its destination, so this only absorbs a shortened step.</summary>
    private const float WaypointReachM = 0.05f;

    private readonly ConcurrentQueue<TeleportRequest> _teleports = new();

    // Realms G2: a player inside an interior pins it active — no session observes in this demo, so without the pin an occupied interior would go dormant
    // under its player and stop running it. Touched only by TeleportTick, which is serial.
    private readonly System.Collections.Generic.Dictionary<EntityId, RealmObserver> _interiorPins = [];
    private readonly System.Collections.Generic.List<TeleportRequest> _appliedCrossings = [];
    private long _droppedCrossings;
    private long _failedCrossingCommits;

    /// <summary>Test seam: this many crossing batches are rolled back instead of committed, as a commit that failed would be.</summary>
    internal int FailCrossingCommits;

    /// <summary>Crossings that never applied: their destination or player gone, or two batches in a row not committed.</summary>
    internal long DroppedCrossings => _droppedCrossings;

    /// <summary>Crossing batches that did not commit.</summary>
    internal long FailedCrossingCommits => _failedCrossingCommits;

    /// <summary>
    /// The bodies <see cref="TeleportTick"/> moved this tick, which <c>PlayerMove</c> leaves where they landed until the fence has put them in their new
    /// realm's cluster.
    /// </summary>
    /// <remarks>
    /// <b>Teleport runs in the Spawn phase and the move after it</b>, while the fence only moves a crossing body into the new realm's cluster at the end of
    /// the tick. So a body a client keeps walking through a door was integrated once more in its OLD cluster: at its new coordinates, under the old realm's
    /// rules — out of a room onto the planet, at the room's flat floor height. A body that then stopped kept that altitude, and the camera that follows the
    /// server's altitude sat under the terrain. One tick of standing on the landing is the honest answer: it is where the door put it. Written only by
    /// <see cref="TeleportTick"/>, which is serial, and read during the move phase, which writes nothing here.
    /// </remarks>
    private readonly System.Collections.Generic.HashSet<EntityId> _landedThisTick = [];

    /// <summary>The bodies landed this tick, or <see langword="null"/> when none did, which is almost every tick.</summary>
    private System.Collections.Generic.HashSet<EntityId> LandedThisTick => _landedThisTick.Count > 0 ? _landedThisTick : null;

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

    /// <summary>Every planet's index, by planet realm: its shuttleports, for a shuttle to another planet.</summary>
    public WorldIndex[] PlanetIndexes { get; init; }

    /// <summary>Every door side, read from the database at open.</summary>
    public PortalTable Portals { get; init; }

    /// <summary>Interior realms per planet, 0 without <c>--interiors</c>. Only the report reads it: no crossing computes a realm.</summary>
    public int InteriorsPerPlanet { get; init; }

    private bool InteriorsActive => Portals != null && Portals.Count > 0;

    /// <summary>Crossings so far: into interiors, out of them, and between planets by shuttle.</summary>
    public (long Entries, long Exits, long InterPlanet) CrossingTotals => (_portalEntries, _portalExits, _interPlanetCrossings);

    /// <summary>
    /// Ticks whose crossings committed at least one teleport: each is one durable write, because a player carries its <c>Inventory</c>, which is
    /// <c>Versioned</c>, from one realm's cluster to the other's.
    /// </summary>
    public long CrossingCommits => _crossingCommits;

    private long _crossingCommits;

    /// <summary>
    /// An idle decision in a city walks into one of its buildings with probability <see cref="SimConfig.InteriorShare"/>. False leaves the decision to the
    /// ordinary idle branch, untouched.
    /// </summary>
    /// <remarks>
    /// <b>To the start of the door's path, not to the door.</b> The path's first point is on open ground in front of the ramp, so walking there in a straight
    /// line from anywhere in town is open-ground navigation; the path from there is published clear of the building's own walls (ASSET_FORMAT.md § 5.7).
    /// </remarks>
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

        var doors = Portals.CityDoors(planet, city);
        if (doors.IsEmpty)
        {
            return false;
        }

        var row = doors[Math.Min(doors.Length - 1, (int)(Hash01(pickSalt) * doors.Length))];
        state.Activity = PlayerActivity.ToPortal;
        state.ActivityTicks = 200 * _config.TickRateHz;
        state.ShuttleDest = Portals.Door[row].TargetRealm;
        move.SpeedMps = TatooineData.PlayerRunSpeedMps;
        (move.DestX, move.DestZ) = Portals.ApproachStart[row];
        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
        return true;
    }

    /// <summary>
    /// One think of a scripted body walking to, through or out of a door. False when the body is doing something else.
    /// </summary>
    /// <param name="state">Its behaviour.</param>
    /// <param name="move">Its motion; the velocity is still the one the last think set, which the step just taken was made with.</param>
    /// <param name="id">The body.</param>
    /// <param name="x">Where it stands now.</param>
    /// <param name="z">Where it stands now.</param>
    /// <param name="k">Ticks since its last visit: the last step was <c>k</c> ticks of velocity.</param>
    /// <param name="halfExtent">Its half-extent, for the landing.</param>
    /// <returns>Whether this handled the body.</returns>
    /// <remarks>
    /// <para>
    /// <b>The crossing is the same test a client's body takes</b>, on the step this body just made: from where it stood, <c>k</c> ticks of its velocity ago,
    /// to where it stands. That is exact for a body nothing pushed — the crowd is not collided unless <c>--collide-crowd</c> — and still right for one that
    /// was: a push only shortens the step, which keeps its start on the near side.
    /// </para>
    /// <para>
    /// <b>Waypoints are reached exactly</b>, because the steering caps the last step at the distance left, so the next think finds the body on its waypoint
    /// and turns it to the next. The coarse "within five metres" every other destination uses would cut the corner of a ramp or a room.
    /// </para>
    /// </remarks>
    private bool ThinkPortal(ref PlayerState state, ref PlayerMotion move, ref PlayerRoute walk, ushort realm, EntityId id, float x, float z, int k,
        float halfExtent)
    {
        if (state.Activity is not (PlayerActivity.ToPortal or PlayerActivity.EnteringDoor or PlayerActivity.LeavingRoom))
        {
            return false;
        }

        // The door is held as its ROOM's realm, not as a row: rows are numbered at open, and this state is persisted (PlayerState.ShuttleDest).
        var row = !InteriorsActive ? -1
            : state.Activity == PlayerActivity.LeavingRoom ? Portals.RoomDoor(state.ShuttleDest)
            : Portals.FrontDoor(state.ShuttleDest);
        if (row < 0)
        {
            state.Activity = PlayerActivity.Idle;
            state.ActivityTicks = 0;
            return true;
        }

        if (state.Activity != PlayerActivity.ToPortal)
        {
            var fromX = x - (move.VelX * k);
            var fromZ = z - (move.VelZ * k);
            if (CrossedDoor(PlaneOf(in Portals.Door[row]), fromX, fromZ, x, z))
            {
                Cross(ref state, ref move, id, row, halfExtent);
                return true;
            }
        }

        var dx = move.DestX - x;
        var dz = move.DestZ - z;
        if ((dx * dx) + (dz * dz) > WaypointReachM * WaypointReachM)
        {
            // To the foot of the door's ramp round the town's buildings; the door's own path, from there, is published and walked as it is.
            if (state.Activity == PlayerActivity.ToPortal)
            {
                SteerRouted(ref walk, ref move, realm, x, z, k);
            }
            else
            {
                SteerToWaypoint(ref move, x, z, k);
            }

            return true;
        }

        switch (state.Activity)
        {
            case PlayerActivity.ToPortal:
                // In front of the ramp: now the path, through the door.
                state.Activity = PlayerActivity.EnteringDoor;
                state.ActivityTicks = (int)(DoorWalkTimeoutS * _config.TickRateHz);
                (move.DestX, move.DestZ) = Portals.ApproachEnd[row];
                break;
            case PlayerActivity.LeavingRoom:
                var route = Portals.Route[row];
                state.ShuttleFrom++;
                (move.DestX, move.DestZ) = state.ShuttleFrom < route.Length ? route[state.ShuttleFrom] : Portals.ApproachEnd[row];
                break;
            default:
                // At the end of the path without having crossed: something stopped it short of the plane. Decide again.
                move.VelX = 0f;
                move.VelZ = 0f;
                state.Activity = PlayerActivity.Idle;
                state.ActivityTicks = 0;
                return true;
        }

        SteerToWaypoint(ref move, x, z, k);
        return true;
    }

    /// <summary>
    /// Steers at the waypoint so that the next visit finds the body ON it: <paramref name="k"/> ticks of this velocity, in a realm visited every
    /// <paramref name="k"/>-th tick, end exactly there.
    /// </summary>
    /// <remarks>
    /// <see cref="Steer(ref float, ref float, float, float, float, float, float)"/> caps ONE tick's step at the distance left, and Move integrates
    /// <c>k</c> of them: in a strided realm the body overshot by (k-1) times what was left, then by more on the way back, and never came within
    /// <see cref="WaypointReachM"/> — it stood oscillating until the walk timed out. Aiming at the point 1/k of the way there caps the step at the
    /// distance left over k, and changes nothing for a realm visited every tick.
    /// </remarks>
    private void SteerToWaypoint(ref PlayerMotion move, float x, float z, int k)
    {
        var over = k > 1 ? 1f / k : 1f;
        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, x + ((move.DestX - x) * over), z + ((move.DestZ - z) * over));
    }

    /// <summary>
    /// A scripted body done with a room: out through its way out — or, when walking out already failed, through it regardless.
    /// </summary>
    /// <param name="state">Its behaviour.</param>
    /// <param name="move">Its motion.</param>
    /// <param name="id">The body.</param>
    /// <param name="realm">The room's realm.</param>
    /// <param name="x">Where it stands.</param>
    /// <param name="z">Where it stands.</param>
    /// <param name="halfExtent">Its half-extent, for the landing.</param>
    private void LeaveRoom(ref PlayerState state, ref PlayerMotion move, EntityId id, ushort realm, float x, float z, float halfExtent)
    {
        var row = InteriorsActive ? Portals.RoomDoor(realm) : -1;
        if (row < 0)
        {
            // A realm with no way out is not a room this world built. Nothing to walk to; stay idle rather than throw on the tick path.
            state.Activity = PlayerActivity.Idle;
            state.ActivityTicks = 10 * _config.TickRateHz;
            return;
        }

        if (state.Activity == PlayerActivity.LeavingRoom)
        {
            // The walk out timed out. A room has no other exit, so the body goes through the door it could not reach.
            Cross(ref state, ref move, id, row, halfExtent);
            return;
        }

        state.Activity = PlayerActivity.LeavingRoom;
        state.ActivityTicks = (int)(RoomWalkTimeoutS * _config.TickRateHz);
        state.ShuttleDest = realm;
        state.ShuttleFrom = 0;
        move.SpeedMps = TatooineData.PlayerRunSpeedMps;
        var route = Portals.Route[row];
        (move.DestX, move.DestZ) = route.Length > 0 ? route[0] : Portals.ApproachEnd[row];
        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
    }

    /// <summary>Queues a scripted body through door side <paramref name="row"/> and gives it what it does on the far side.</summary>
    private void Cross(ref PlayerState state, ref PlayerMotion move, EntityId id, int row, float halfExtent)
    {
        var entering = QueueCrossing(id, row, halfExtent);

        // Standing still until TeleportTick applies it, so Move writes nothing the teleport would overwrite.
        move.VelX = 0f;
        move.VelZ = 0f;
        if (entering)
        {
            state.Activity = PlayerActivity.Inside;
            var stayTicks = _config.InteriorStayS * _config.TickRateHz;
            state.ActivityTicks = Math.Max(1, (int)(stayTicks * (1f + (3f * Hash01((uint)id.EntityKey ^ 0x68E31DA4u)))));
        }
        else
        {
            state.Activity = PlayerActivity.Idle;
            state.ActivityTicks = (10 * _config.TickRateHz) + (int)(Hash01((uint)id.EntityKey ^ 0x1F83D9ABu) * 30 * _config.TickRateHz);
        }
    }

    /// <summary>Queues the crossing of door side <paramref name="row"/>: to the realm it names, at the landing it names.</summary>
    /// <returns>Whether the body is going into a room rather than out of one.</returns>
    private bool QueueCrossing(EntityId id, int row, float halfExtent)
    {
        ref readonly var door = ref Portals.Door[row];
        var entering = door.TargetRealm >= _config.Planets;
        _teleports.Enqueue(new TeleportRequest(id, (ushort)door.TargetRealm, door.ArriveX, door.ArriveZ, halfExtent,
            entering ? CrossingKind.Enter : CrossingKind.Exit, door.ArriveY));
        return entering;
    }

    /// <summary>
    /// A shuttle bound for another planet (Realms G1b): the boarding is a realm change, so it is queued for <see cref="TeleportTick"/>, which runs after the
    /// Shuttle system in the same phase, rather than written in place.
    /// </summary>
    private void BoardInterPlanet(EntityId id, ushort fromPlanet, int destCity, float halfExtent, uint salt)
    {
        var destPlanet = (ushort)((fromPlanet + 1 + Math.Min(_config.Planets - 2, (int)(Hash01(salt) * (_config.Planets - 1)))) % _config.Planets);
        // Outside the destination gate, as a local hop lands (SimBridge.Shuttles), never inside the ring wall.
        var (portX, portZ) = PlanetIndexes[destPlanet].Shuttleports[destCity];
        var (gx, gz) = WorldBuilder.OutsideGate(portX, portZ, Hash01(salt ^ 0x2F9B1D63u), Hash01(salt ^ 0x6C8E9CF5u));
        var half = (_config.WorldEdgeM * 0.5f) - halfExtent;
        var x = Math.Clamp(gx, -half, half);
        var z = Math.Clamp(gz, -half, half);
        _teleports.Enqueue(new TeleportRequest(id, destPlanet, x, z, halfExtent, CrossingKind.InterPlanet, GroundAt(new RealmId(destPlanet), x, z)));
    }

    /// <summary>A doorway in the frame a body walks in, as the crossing test needs it.</summary>
    /// <param name="CenterX">Where the opening is.</param>
    /// <param name="CenterZ">Where the opening is.</param>
    /// <param name="ForwardX">Unit vector through the door, the way a body crosses it.</param>
    /// <param name="ForwardZ">Unit vector through the door.</param>
    /// <param name="HalfX">Half the opening width, across the forward.</param>
    private readonly record struct DoorPlane(float CenterX, float CenterZ, float ForwardX, float ForwardZ, float HalfX);

    /// <summary>The plane a stored door side is crossed on.</summary>
    private static DoorPlane PlaneOf(in PortalDoor door) => new(door.CenterX, door.CenterZ, door.ForwardX, door.ForwardZ, door.HalfWidth);

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

    /// <summary>
    /// How far a body at (<paramref name="x"/>, <paramref name="z"/>) is from the part of a door's plane that counts — the opening, not the wall beside it.
    /// </summary>
    /// <param name="door">The door side.</param>
    /// <param name="x">The body, east.</param>
    /// <param name="z">The body, north.</param>
    /// <returns>The distance to the nearest point of the opening's segment in the plane.</returns>
    internal static float DistanceToOpening(in PortalDoor door, float x, float z)
    {
        // The segment through the centre, across the forward, half the width each way.
        var ax = -door.ForwardZ;
        var az = door.ForwardX;
        var t = Math.Clamp(((x - door.CenterX) * ax) + ((z - door.CenterZ) * az), -door.HalfWidth, door.HalfWidth);
        var dx = x - (door.CenterX + (ax * t));
        var dz = z - (door.CenterZ + (az * t));
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>
    /// The doors near a body's resolved step, in the collision pass: queues the crossing when a client's step went through one, and bounds the body's disc so
    /// a held step can never go through one unseen.
    /// </summary>
    /// <param name="realm">The body's realm.</param>
    /// <param name="probe">The collision pass's probe for this body: the disc it is handed lies inside it.</param>
    /// <param name="client">The body, when a client drives it; <see cref="EntityId.Null"/> for the scripted crowd, which crosses in its own think.</param>
    /// <param name="half">Its half-extent.</param>
    /// <param name="fromX">Where it stood after the previous step resolved.</param>
    /// <param name="fromZ">Where it stood.</param>
    /// <param name="toX">Where it stands after this one resolved.</param>
    /// <param name="toZ">Where it stands.</param>
    /// <returns>The largest disc the doors allow around (<paramref name="toX"/>, <paramref name="toZ"/>); +∞ with none in reach.</returns>
    /// <remarks>
    /// <para>
    /// <b>The disc is what makes this needed, and this is what makes the disc safe.</b> A body whose step stays inside its disc skips the query entirely,
    /// and the disc used to be measured to walls only — so a body could walk through a door plane while held, never asked, and never enter: the doorway is
    /// open, and the door stands 0.6 m in front of the facade the disc was measured to. Bounding the disc by the distance to every opening in reach means any
    /// step that could cross one leaves the disc first, and is tested here.
    /// </para>
    /// <para>
    /// <b>The same probe the walls are found with</b>, against the <see cref="Portal"/> archetype: the disc lies inside the probe, so any opening that could
    /// meet the disc has its box in the probe. One radius query over a sparse archetype, on the refresh path only.
    /// </para>
    /// </remarks>
    private float DoorsAround(RealmId realm, in BSphere2F probe, EntityId client, float half, float fromX, float fromZ, float toX, float toZ)
    {
        var clear = float.PositiveInfinity;
        var crossed = client.IsNull || (fromX == toX && fromZ == toZ);
        var e = Dbe.ClusterSpatialQuery<Portal>(realm).Radius(in probe);
        try
        {
            while (e.MoveNext())
            {
                var row = Portals.RowOf(e.Current.Entity);
                if (row < 0)
                {
                    continue;
                }

                ref readonly var door = ref Portals.Door[row];
                if (!crossed && CrossedDoor(PlaneOf(in door), fromX, fromZ, toX, toZ))
                {
                    // Nothing else to do, and deliberately no attempt to stop the body: this pass does not own its velocity, a client keeps steering it,
                    // and the teleport overwrites the position. It cannot queue twice either: the next step starts past the plane and the directed test
                    // refuses it.
                    QueueCrossing(client, row, half);
                    crossed = true;
                }

                // Held only while the centre cannot reach the opening: DiscHolds keeps the centre within ClearM - half of the anchor. A hair short, since
                // a step that ends exactly on the plane counts as through.
                clear = MathF.Min(clear, DistanceToOpening(in door, toX, toZ) + half - 0.01f);
            }
        }
        finally
        {
            e.Dispose();
        }

        return clear;
    }

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
        // Last tick's landings have been placed by the fence since; see _landedThisTick.
        _landedThisTick.Clear();

        // Stopped by a client (TatooineReplication.SetPaused, a demo control), or past a measured run's last tick (Halted). The simulation does nothing;
        // replication, the session system and the engine's own stages keep running, or no client could ever ask to resume.
        if (Halted(ctx.TickNumber))
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
                // The DESTINATION realm decides the ground: a room's flat floor, or the planet's relief at the door. A door's landing is a published spawn,
                // and when that spawn stands on something — the landing at the top of a house's ramp — the feet go there rather than inside it.
                nb.SetAt(r.X, r.Z, MathF.Max(GroundAt(dest, r.X, r.Z), r.Y), r.HalfExtent);
                tx.Teleport(r.Id, Player.Bounds, dest, in nb);

                // A swoop does not fit a 0.8 m door, and a rider going through one gets off it: on foot on the far side, whichever way it went. Idle too:
                // another realm is another fight, and the client drops its stance on the crossing.
                var rider = tx.OpenMut(r.Id);
                var riding = rider.Read(Player.State);
                if (riding.Mounted != 0 || riding.Stance != Stances.None)
                {
                    riding.Mounted = 0;
                    riding.Stance = Stances.None;
                    rider.Set(Player.State, riding);

                    // Marked, as SetMount marks it (ADR-067): a replicated field goes out when it is marked, not because the realm changed under it.
                    TatooineReplication.Replicate(in rider);
                }

                _appliedCrossings.Add(r);
                _landedThisTick.Add(r.Id);
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
            bool committed;
            if (FailCrossingCommits > 0)
            {
                FailCrossingCommits--;
                committed = false;
            }
            else
            {
                committed = tx.Commit();
            }

            _teleportCommitTicks += Stopwatch.GetTimestamp() - commit;
            if (!committed)
            {
                // Nothing moved: no body landed, nothing is pinned, nothing is counted. The scripted bodies already believe they are through (their
                // activity was set when the crossing was queued), so each crossing is tried once more next tick rather than leaving a body inside a
                // doorway thinking it is in the room; a second failure drops it.
                foreach (var r in _appliedCrossings)
                {
                    _landedThisTick.Remove(r.Id);
                    if (r.Retried)
                    {
                        _droppedCrossings++;
                    }
                    else
                    {
                        _teleports.Enqueue(r with { Retried = true });
                    }
                }

                _appliedCrossings.Clear();
                entries = exits = planets = 0;
                _failedCrossingCommits++;
            }
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
        if (_appliedCrossings.Count > 0)
        {
            _crossingCommits++;
        }

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
            + $"{Stopwatch.GetElapsedTime(0, _teleportCommitTicks / batches).TotalMicroseconds:F0} us; "
            + $"{_droppedCrossings:N0} dropped, {_failedCrossingCommits:N0} batches not committed");
    }
}
