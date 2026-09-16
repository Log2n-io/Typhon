using System;
using System.Numerics;
using System.Threading;

namespace SwgTatooine;

/// <summary>
/// The simulation's behaviour, in one place. The system classes in <c>Ecs/Systems.cs</c> are thin wrappers that declare
/// scheduling metadata and call into here.
/// </summary>
/// <remarks>
/// <para>The split follows AntHill's: a system's <c>Configure</c> is a declaration the scheduler reads, and its
/// <c>Execute</c> is one line. Keeping the bodies together makes the shared state obvious and stops per-tick scratch
/// from being duplicated eight times.</para>
/// </remarks>
public sealed partial class SimBridge
{
    private readonly SimConfig _config;
    private readonly TatooineMap _map;
    private readonly WorldIndex _index;

    // Per-tick counters. Written with Interlocked from parallel workers, drained by the telemetry system.
    private long _awarenessQueries;
    private long _awarenessHits;
    private long _aggroQueries;
    private long _aggroHits;
    private long _creaturesKilled;
    private long _playersEngaged;
    private long _economyTicks;
    private long _missionsIssued;
    private long _missionsCompleted;
    private long _creaturesRespawned;

    // Combat work over the measured window only, so a per-tick mean divides by the ticks that produced it.
    private long _combatQueries;
    private long _combatCandidates;
    private long _combatEvents;
    private long _combatDropped;
    private long _combatVerified;
    private long _combatMismatches;
    private long _combatPushHigher;

    public SimBridge(SimConfig config, TatooineMap map, WorldIndex index)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(index);
        _config = config;
        _map = map;
        _index = index;
        AggroRadius = TatooineData.AggroRadiusM * config.ContentScale;
        AwarenessRadius = TatooineData.AwarenessRadiusM * config.ContentScale;
        MeleeRange = TatooineData.MeleeRangeM * config.ContentScale;
        RangedRange = TatooineData.RangedRangeM * config.ContentScale;
        MetresPerTick = 1f / config.TickRateHz;
        AwarenessMinChunk = config.AwarenessMinChunk;
        _rngState = (uint)config.Seed | 1u;
        InitShuttles();
        InitChunkStats();
    }

    /// <summary>Per-system chunk floor for the awareness system; 0 inherits the global one.</summary>
    public int AwarenessMinChunk { get; }

    /// <summary>Aggro radius in world units, scaled with the world.</summary>
    public float AggroRadius { get; }

    /// <summary>Interest-management radius in world units.</summary>
    public float AwarenessRadius { get; }

    public float MeleeRange { get; }

    public float RangedRange { get; }

    /// <summary>Seconds of simulated time per tick — what converts a speed in m/s into a displacement.</summary>
    public float MetresPerTick { get; }

    /// <summary>The live database, set by the simulation once the engine exists.</summary>
    public DatabaseEngine Dbe { get; set; }

    /// <summary>Live view of the players, used by the systems that walk them.</summary>
    public EcsView<Player> PlayerView { get; set; }

    public EcsView<Creature> CreatureView { get; set; }

    public EcsView<CityNpc> NpcView { get; set; }

    public EcsView<CreatureLair> LairView { get; set; }

    public EcsView<WorldObject> StructureView { get; set; }

    /// <summary>Snapshot of this tick's counters, taken and reset by the telemetry system.</summary>
    public TickStats DrainStats() => new()
    {
        AwarenessQueries = Interlocked.Exchange(ref _awarenessQueries, 0),
        AwarenessHits = Interlocked.Exchange(ref _awarenessHits, 0),
        AggroQueries = Interlocked.Exchange(ref _aggroQueries, 0),
        AggroHits = Interlocked.Exchange(ref _aggroHits, 0),
        CreaturesKilled = Interlocked.Exchange(ref _creaturesKilled, 0),
        PlayersEngaged = Interlocked.Exchange(ref _playersEngaged, 0),
        EconomyTicks = Interlocked.Exchange(ref _economyTicks, 0),
        MissionsIssued = Interlocked.Exchange(ref _missionsIssued, 0),
        MissionsCompleted = Interlocked.Exchange(ref _missionsCompleted, 0),
        CreaturesRespawned = Interlocked.Exchange(ref _creaturesRespawned, 0),
        ShuttleBoardings = Interlocked.Exchange(ref _shuttleBoardings, 0),
        CombatQueries = Interlocked.Exchange(ref _combatQueries, 0),
        CombatCandidates = Interlocked.Exchange(ref _combatCandidates, 0),
        CombatEvents = Interlocked.Exchange(ref _combatEvents, 0),
        CombatDropped = Interlocked.Exchange(ref _combatDropped, 0),
        CombatVerified = Interlocked.Exchange(ref _combatVerified, 0),
        CombatMismatches = Interlocked.Exchange(ref _combatMismatches, 0),
        CombatPushHigher = Interlocked.Exchange(ref _combatPushHigher, 0),
    };

    // ── A per-worker-safe random ────────────────────────────────────────────────────────────────────────────────────
    //
    // One shared state advanced with Interlocked would serialise every worker on one cache line for the sake of a
    // wander heading. Instead each call mixes the shared counter with the caller's own inputs, which is not a good
    // generator and does not need to be: nothing here is a measurement, only a direction to walk in.

    private uint _rngState;

    /// <summary>A cheap decorrelated float in [0,1) from a per-entity salt. Deterministic given the same salt sequence.</summary>
    private static float Hash01(uint salt)
    {
        salt ^= salt >> 16;
        salt *= 0x7FEB352Du;
        salt ^= salt >> 15;
        salt *= 0x846CA68Bu;
        salt ^= salt >> 16;
        return (salt >> 8) * (1f / 16_777_216f);
    }

    /// <summary>Mix a tick, a slot and a cluster into a salt that decorrelates two entities in the same cluster.</summary>
    private static uint Salt(long tick, int cluster, int slot, uint stream)
        => (uint)tick * 2654435761u ^ ((uint)cluster * 2246822519u) ^ ((uint)slot * 3266489917u) ^ stream;
}

/// <summary>One tick's behavioural counters — what the simulation did, as opposed to what it cost.</summary>
public struct TickStats
{
    public long AwarenessQueries;
    public long AwarenessHits;
    public long AggroQueries;
    public long AggroHits;
    public long CreaturesKilled;
    public long PlayersEngaged;
    public long EconomyTicks;
    public long MissionsIssued;
    public long MissionsCompleted;
    public long CreaturesRespawned;
    public long ShuttleBoardings;

    /// <summary>Radius queries the combat range test issued, over the measured window: one per ready creature (pull) or one per player (push).</summary>
    public long CombatQueries;

    /// <summary>Entities those queries returned, over the measured window. Pull stops a creature's query at four players.</summary>
    public long CombatCandidates;

    /// <summary>Push only: hit events drained into the shooter lane, over the measured window.</summary>
    public long CombatEvents;

    /// <summary>Push only: hit events the queue dropped at its growth ceiling. Anything but zero makes the run's combat wrong.</summary>
    public long CombatDropped;

    /// <summary><c>--combat-verify</c>: creatures whose push shooter count was checked against pull's query, over the measured window.</summary>
    public long CombatVerified;

    /// <summary><c>--combat-verify</c>: of those, how many got a different count from the two models.</summary>
    public long CombatMismatches;

    /// <summary><c>--combat-verify</c>: of the mismatches, how many push counted MORE shooters for — a player the creature-centred query missed.</summary>
    public long CombatPushHigher;

    /// <summary>Mean objects returned by one interest query — the number that says how expensive awareness is.</summary>
    public readonly double HitsPerAwarenessQuery => AwarenessQueries == 0 ? 0d : (double)AwarenessHits / AwarenessQueries;
}
