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

    /// <summary>
    /// The planet's ground, shared across the process and baked once. See <see cref="GroundAt"/>.
    /// </summary>
    private readonly TerrainField _terrain;

    /// <summary>
    /// The A/B switch for the cost of sampling the ground, read once from <c>SWG_NO_GROUND</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rung (b) made <see cref="GroundAt"/> a per-tick cost — one sample per moved entity, in the Move phase — and
    /// nothing measured it. The sample is a bilinear read of a 67 MB array whose two rows are 16 KB apart, in a field that
    /// does not fit L3, for entities scattered over the whole planet: roughly two misses that go to memory, per mover,
    /// per tick, at 50 Hz.
    /// </para>
    /// <para>
    /// A <see langword="static readonly"/> <see cref="bool"/> and not a config field, deliberately: the JIT folds it
    /// after the static constructor, so the OFF arm pays no branch and the two arms are the SAME BINARY. Two builds would
    /// differ in inlining and layout and would measure that instead.
    /// </para>
    /// </remarks>
    private static readonly bool GroundDisabled = ReadGroundSwitch();

    /// <summary>Reads the switch, and SAYS SO — a run with the ground off must never be mistaken for a normal one.</summary>
    private static bool ReadGroundSwitch()
    {
        if (Environment.GetEnvironmentVariable("SWG_NO_GROUND") != "1")
        {
            return false;
        }

        Console.WriteLine("SWG_NO_GROUND=1: every entity is at altitude 0. This is the A/B arm, not a world.");
        return true;
    }

    /// <summary>
    /// Ground height for a point in a realm: the planet's relief on a planet, and 0 anywhere else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Altitude is a property of the realm, not of the coordinate.</b> A cantina's interior is its own realm with its
    /// own flat floor a few metres across, and its local (x, z) collides with a point on the planet that has 200 m of
    /// mesa under it. Sampling the heightfield there would put the furniture inside a hill. The space realm is the same
    /// argument in the other direction.
    /// </para>
    /// <para>
    /// Realms below <see cref="SimConfig.Planets"/> are planets; interiors and space are allocated above them
    /// (<c>TatooineSim</c>), so the test is an index comparison and not a lookup.
    /// </para>
    /// </remarks>
    internal float GroundAt(RealmId realm, float x, float z)
    {
        if (GroundDisabled)
        {
            return 0f;
        }

        return realm.Value < _config.Planets ? _terrain.GroundAt(x, z) : 0f;
    }

    // Per-tick counters. Written with Interlocked from parallel workers, drained by the telemetry system.
    private long _awarenessQueries;
    private long _awarenessHits;
    private long _aggroQueries;
    private long _aggroHits;
    private long _creaturesKilled;

    // SWG-02's combat counters. Every one of them exists because an acceptance criterion is a claim about what does NOT happen — a player that is not fighting
    // does no damage, a shot beyond weapon range does no damage — and a silent zero cannot tell that apart from a system that never ran.
    private long _playerShots;
    private long _creatureAttacks;
    private long _damageApplied;
    private long _shotsSkippedNotFighting;
    private long _shotsRefusedRange;
    private long _shotsWithoutTarget;
    private long _creaturesLostTarget;
    private long _chaseGivenUp;
    private long _lairHits;
    private long _playersIncapacitated;
    private long _missionRewards;
    private long _missionsAssigned;
    private long _eventsStale;
    private long _economyTicks;
    private long _missionsIssued;
    private long _missionsCompleted;
    private long _creaturesRespawned;

    public SimBridge(SimConfig config, TatooineMap map, WorldIndex index)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(index);
        _terrain = TerrainField.Shared(config.ContentScale);
        _config = config;
        _map = map;
        _index = index;
        AggroRadius = TatooineData.AggroRadiusM * config.ContentScale;
        AwarenessRadius = TatooineData.AwarenessRadiusM * config.ContentScale;
        MeleeRange = TatooineData.MeleeRangeM * config.ContentScale;
        RangedRange = TatooineData.RangedRangeM * config.ContentScale;
        MaxChaseRange = TatooineData.MaxChaseRangeM * config.ContentScale;
        MetresPerTick = 1f / config.TickRateHz;

        // A leg is about a second and a half of ambling, and a rest four times that, so a wandering creature is stationary roughly 80 % of the time. Both
        // are expressed in TICKS off the configured rate so that an --hz sweep changes the sampling and not the behaviour being sampled.
        _wanderLegTicks = Math.Max(1, (int)(WanderLegSeconds * config.TickRateHz));

        // A creature closes to melee before it stops. Ranged range is the PLAYER's weapon in this model — the distance at which a player shoots a creature —
        // so using it here would have creatures halt 75 m out and never reach anything.
        _creatureAttackRange = MeleeRange;
        AwarenessMinChunk = config.AwarenessMinChunk;
        _rngState = (uint)config.Seed | 1u;

        // Every per-tick draw is salted with this, so --seed reproduces a run (M0-4). Odd, so a seed of 0 still perturbs the mix.
        _seedSalt = ((uint)config.Seed * 0x9E3779B1u) | 1u;
        InitShuttles();
        InitChunkStats();
    }

    /// <summary>How long one wander leg lasts, in seconds, before the creature stands still.</summary>
    private const float WanderLegSeconds = 1.5f;

    /// <summary>Rests per unit of walking. Four gives a creature that is stationary 80 % of the time.</summary>
    private const int WanderRestToMoveRatio = 4;

    /// <summary>
    /// How much further than its attack range a target may drift before a fighting creature starts closing again.
    /// </summary>
    /// <remarks>
    /// Hysteresis, not slack. At a single threshold a creature sitting exactly on the boundary flips between Fighting and Pursue on alternate decisions and
    /// writes its position more often than one that simply kept walking — the failure mode the band exists to prevent.
    /// </remarks>
    private const float AttackRangeHysteresis = 1.5f;

    /// <summary>Ticks in one wander leg, derived from the tick rate.</summary>
    private readonly int _wanderLegTicks;

    /// <summary>The run's seed, folded into every per-tick salt. See <see cref="Salt"/>.</summary>
    private readonly uint _seedSalt;

    /// <summary>The distance at which a creature stops closing and begins attacking.</summary>
    private readonly float _creatureAttackRange;

    /// <summary>Per-system chunk floor for the awareness system; 0 inherits the global one.</summary>
    public int AwarenessMinChunk { get; }

    /// <summary>Aggro radius in world units, scaled with the world.</summary>
    public float AggroRadius { get; }

    /// <summary>Interest-management radius in world units.</summary>
    public float AwarenessRadius { get; }

    public float MeleeRange { get; }

    public float RangedRange { get; }

    /// <summary>How far a creature will chase its quarry before giving up, in world units, scaled with the world (SWG-02).</summary>
    public float MaxChaseRange { get; }

    /// <summary>Seconds of simulated time per tick — what converts a speed in m/s into a displacement.</summary>
    public float MetresPerTick { get; }

    /// <summary>The live database, set by the simulation once the engine exists.</summary>
    public DatabaseEngine Dbe { get; set; }

    /// <summary>Live view of the players, used by the systems that walk them.</summary>
    public EcsView<Player> PlayerView { get; set; }

    public EcsView<Creature> CreatureView { get; set; }

    public EcsView<CityNpc> NpcView { get; set; }

    /// <summary>The starships (Realms G1c); null without <c>--space</c>.</summary>
    public EcsView<Starship> ShipView { get; set; }

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
        PlayerShots = Interlocked.Exchange(ref _playerShots, 0),
        CreatureAttacks = Interlocked.Exchange(ref _creatureAttacks, 0),
        DamageApplied = Interlocked.Exchange(ref _damageApplied, 0),
        ShotsSkippedNotFighting = Interlocked.Exchange(ref _shotsSkippedNotFighting, 0),
        ShotsRefusedRange = Interlocked.Exchange(ref _shotsRefusedRange, 0),
        ShotsWithoutTarget = Interlocked.Exchange(ref _shotsWithoutTarget, 0),
        CreaturesLostTarget = Interlocked.Exchange(ref _creaturesLostTarget, 0),
        ChaseGivenUp = Interlocked.Exchange(ref _chaseGivenUp, 0),
        LairHits = Interlocked.Exchange(ref _lairHits, 0),
        PlayersIncapacitated = Interlocked.Exchange(ref _playersIncapacitated, 0),
        MissionRewards = Interlocked.Exchange(ref _missionRewards, 0),
        MissionsAssigned = Interlocked.Exchange(ref _missionsAssigned, 0),
        EventsStale = Interlocked.Exchange(ref _eventsStale, 0),
        EconomyTicks = Interlocked.Exchange(ref _economyTicks, 0),
        MissionsIssued = Interlocked.Exchange(ref _missionsIssued, 0),
        MissionsCompleted = Interlocked.Exchange(ref _missionsCompleted, 0),
        CreaturesRespawned = Interlocked.Exchange(ref _creaturesRespawned, 0),
        ShuttleBoardings = Interlocked.Exchange(ref _shuttleBoardings, 0),
        PortalEntries = Interlocked.Exchange(ref _portalEntriesTick, 0),
        PortalExits = Interlocked.Exchange(ref _portalExitsTick, 0),
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

    /// <summary>Mix the run's seed, a stable entity identity and the tick into a salt that decorrelates two entities in the same cluster.</summary>
    /// <remarks>
    /// <b>Keyed on identity, not layout, and salted by <c>--seed</c> (S0-4).</b> It used to mix the cluster id and the slot — where an entity happens to be
    /// stored — so the same seed did not reproduce the same run, and a repair pass or a cross-realm migration silently changed every draw. An
    /// <see cref="EntityId.EntityKey"/> is monotonic per archetype and never recycled, so it survives both. Callers pass an entity's key, or a logical
    /// identity of their own where the draw is not per entity (a realm id, say) — never a chunk or a slot.
    /// </remarks>
    private uint Salt(long tick, long key, uint stream)
        => ((uint)tick * 2654435761u) ^ ((uint)key * 2246822519u) ^ ((uint)(key >> 32) * 3266489917u) ^ stream ^ _seedSalt;
}

/// <summary>One tick's behavioural counters — what the simulation did, as opposed to what it cost.</summary>
public struct TickStats
{
    public long AwarenessQueries;
    public long AwarenessHits;
    public long AggroQueries;
    public long AggroHits;
    public long CreaturesKilled;

    /// <summary>Shots a fighting player fired at its target.</summary>
    public long PlayerShots;

    /// <summary>Attacks a fighting creature made on the player it was fighting.</summary>
    public long CreatureAttacks;

    /// <summary>Damage events the resolver applied — the two counters above, less whatever arrived at a target that had already gone.</summary>
    public long DamageApplied;

    /// <summary>Player-ticks that did no damage because the player was not in <see cref="PlayerActivity.Combat"/>. AC-4's first refusal.</summary>
    public long ShotsSkippedNotFighting;

    /// <summary>Shots refused because the player's target was beyond weapon range. AC-4's second refusal.</summary>
    public long ShotsRefusedRange;

    /// <summary>Weapon cycles a fighting player wasted because nothing shootable was within range to acquire.</summary>
    public long ShotsWithoutTarget;

    /// <summary>Creatures that dropped out of a fight because what they were fighting had gone or been incapacitated.</summary>
    public long CreaturesLostTarget;

    /// <summary>Chases abandoned because the quarry got further away than <see cref="TatooineData.MaxChaseRangeM"/>. AC-3's leash, and the proof it holds.</summary>
    public long ChaseGivenUp;

    /// <summary>Hits landed on a destroy-mission lair — what closes the mission loop.</summary>
    public long LairHits;

    /// <summary>Players reduced to zero health, each one cloned at the nearest city.</summary>
    public long PlayersIncapacitated;

    /// <summary>Destroy missions that paid their owner, each one a <c>Versioned</c> write in the tick's unit of work.</summary>
    public long MissionRewards;

    /// <summary>Missions whose owner was actually sent to the lair. Below <c>MissionsIssued</c> by however many were offered to a possessed player.</summary>
    public long MissionsAssigned;

    /// <summary>Events whose target had gone by the time the resolver opened it. Expected to be zero; a non-zero value means an assumption has stopped holding.</summary>
    public long EventsStale;
    public long EconomyTicks;
    public long MissionsIssued;
    public long MissionsCompleted;
    public long CreaturesRespawned;
    public long ShuttleBoardings;
    public long PortalEntries;
    public long PortalExits;

    /// <summary>Mean objects returned by one interest query — the number that says how expensive awareness is.</summary>
    public readonly double HitsPerAwarenessQuery => AwarenessQueries == 0 ? 0d : (double)AwarenessHits / AwarenessQueries;


}
