using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace SwgTatooine;

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
        if (_teleports.IsEmpty)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        long entries = 0;
        long exits = 0;
        long planets = 0;
        using (var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
        {
            while (_teleports.TryDequeue(out var r))
            {
                var nb = default(PlayerPlacement);
                nb.SetAt(r.X, r.Z, r.HalfExtent);
                tx.Teleport(r.Id, Player.Bounds, new RealmId(r.Realm), in nb);
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
