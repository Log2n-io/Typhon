using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Typhon.Schema.Definition;

namespace SpaceBattle;

/// <summary>
/// The game. Two factions of stations spawn ships, ships acquire the nearest enemy through a spatial radius query,
/// close to weapons range, fire projectiles, and die.
/// </summary>
/// <remarks>
/// <para>
/// The gameplay is a vehicle for spatial traffic, and the traffic is chosen to be adversarial in the ways that
/// matter: ships move continuously (cluster AABB churn + cell migration), projectiles spawn and die by the
/// thousand every second (cluster allocation and drain), and every hit test is a <em>tiny</em> radius query inside
/// a <em>huge</em> cell — the exact shape whose selectivity is under investigation.
/// </para>
/// <para>
/// Everything runs serially on the caller's thread. The engine's parallel dispatch is not the subject here, and a
/// serial loop keeps every frame reproducible from the seed.
/// </para>
/// </remarks>
internal sealed class Simulation
{
    private readonly Config _cfg;
    private readonly TyphonHost _host;
    private readonly Random _rng;

    private readonly List<PendingShot> _pendingShots = new();
    private readonly List<EntityId> _dead = new();
    /// <summary>Reused single-element buffer for the "enumerate exactly one cluster by chunk id" trick.</summary>
    private readonly int[] _oneCluster = new int[1];

    // Faction lookup cache. Query hits arrive grouped by cluster (the enumerator walks cell by cell, cluster by
    // cluster), so caching one cluster's faction bytes turns a per-HIT cluster open into a per-CLUSTER one. In a
    // dense battle that is the difference between ~1500 and ~30 opens for a single acquisition query.
    private int _facCacheChunk = -1;
    private ulong _facCacheOccupancy;
    private readonly byte[] _facCache = new byte[64];
    private readonly byte[] _kindCache = new byte[64];
    private readonly Dictionary<long, int> _damage = new();   // (chunkId << 8 | slot) -> damage
    private readonly List<Vector2> _stationPos = new();
    private readonly List<byte> _stationFaction = new();

    /// <summary>Ship kinds, stored in <see cref="Combat.Kind"/>.</summary>
    public const byte KindFighter = 0;
    public const byte KindHeavy = 1;
    public const byte KindMiner = 2;

    public const byte PickupPower = 0;
    public const byte PickupShield = 1;
    public const byte PickupSpeed = 2;
    public const byte PickupMining = 3;
    public const int PickupKindCount = 4;

    public static string PickupName(byte kind) => kind switch
    {
        PickupPower => "POWER",
        PickupShield => "SHIELD",
        PickupSpeed => "SPEED",
        _ => "MINING",
    };

    public int[] ShipsAlive { get; } = new int[4];
    public int[] MinersAlive { get; } = new int[4];
    /// <summary>Mined material per faction. Ships cost material, so the economy gates the war.</summary>
    public int[] Material { get; } = new int[4];
    public int AsteroidsAlive { get; private set; }
    public int PickupsAlive { get; private set; }

    /// <summary>Ticks remaining of the weapon-power effect, per faction.</summary>
    public int[] PowerTicks { get; } = new int[4];

    /// <summary>Ticks remaining of the shield effect, per faction.</summary>
    public int[] ShieldTicks { get; } = new int[4];

    /// <summary>Ticks remaining of the faction-wide speed effect.</summary>
    public int[] SpeedTicks { get; } = new int[4];

    /// <summary>Ticks remaining of the faction-wide mining effect.</summary>
    public int[] MiningTicks { get; } = new int[4];

    /// <summary>Hits landed on the live pickup, per faction — mirrored out of the component for the HUD.</summary>
    public int[] PickupProgress { get; } = new int[4];

    /// <summary>Kind of the live pickup, or -1 when none is alive.</summary>
    public int LivePickupKind { get; private set; } = -1;

    /// <summary>World position of the live pickup. Only meaningful when <see cref="LivePickupKind"/> is not -1.</summary>
    public Vector2 LivePickupPos { get; private set; }

    /// <summary>Total hits landed on pickups, all factions — the size of the contest, for the report.</summary>
    public int PickupHits { get; private set; }

    /// <summary>Ticks until the next pickup spawns, or 0 when one is already due.</summary>
    public long TicksToNextPickup => Math.Max(0, _nextPickupTick - _host.Tick);

    public int PickupsCollected { get; private set; }

    /// <summary>Ticks during which each faction had ANY effect active — lets the spawn formula be verified.</summary>
    public long[] EffectTicks { get; } = new long[4];
    public long TicksElapsed { get; private set; }
    public int ShotsAbsorbed { get; private set; }
    private long _nextPickupTick;
    private readonly long[] _nextFloorTick = new long[4];
    public int TotalMined { get; private set; }
    public int ShotsAlive { get; private set; }
    public int TotalSpawned { get; private set; }
    public int TotalKilled { get; private set; }

    /// <summary>Projectiles fired, all factions, for the whole run.</summary>
    public long TotalShotsFired { get; private set; }

    /// <summary>Projectiles that landed on a ship (damage dealt or shield-absorbed).</summary>
    public long TotalShotHits { get; private set; }

    /// <summary>Projectiles that expired without hitting anything.</summary>
    public long TotalShotsMissed { get; private set; }

    /// <summary>
    /// Fraction of fired projectiles that reached a target.
    /// </summary>
    /// <remarks>
    /// The number to watch when tuning aim. Ships miss for three compounding reasons — the aim point is stale by up
    /// to <see cref="Config.TargetReacquireTicks"/>, the shot is not led, and both ships keep moving during its
    /// flight — and only a measured hit rate separates "the fix helped" from "the fix felt like it helped".
    /// </remarks>
    public float ShotHitRate => TotalShotsFired > 0 ? TotalShotHits / (float)TotalShotsFired : 0f;

    /// <summary>Per-faction centre of mass, refreshed each tick — cheap flocking target when a ship has no enemy.</summary>
    private readonly Vector2[] _centroid = new Vector2[4];

    /// <summary>
    /// Per-faction MINER centre of mass. Fighters with no enemy in sight rally here instead of to the map centre,
    /// which is what stops the war collapsing into one blob in the middle: miners follow the asteroids, which are
    /// scattered, so the fighting spreads out with them.
    /// </summary>
    private readonly Vector2[] _minerCentroid = new Vector2[4];
    private readonly bool[] _hasMiners = new bool[4];

    /// <summary>Fixed points asteroids spawn and respawn at. Empty means "place at random".</summary>
    private readonly List<Vector2> _oreAnchors = new();

    /// <summary>
    /// Live asteroid state this tick, keyed by ENTITY KEY — not by (chunk, slot).
    /// </summary>
    /// <remarks>
    /// A cluster chunk id is a storage location, not an identity: an asteroid that drifts across a cell boundary
    /// migrates to a different cluster and its chunk id changes. Keying on it meant every miner holding the old id
    /// failed its lookup, dropped the rock and restarted the approach — 4 857 times in 8 000 ticks, which is why
    /// miners hovered at the edge of mining range and never once filled a hold. The entity key is stable for the
    /// asteroid's whole life, which is the property this lookup actually needs.
    /// </remarks>
    private readonly Dictionary<long, (Vector2 Pos, int Chunk, int Slot)> _orePos = new();

    /// <summary>
    /// Miner state census, refreshed each tick: [0] seeking, [1] mining, [2] returning, [3] holding no ore target.
    /// </summary>
    /// <remarks>
    /// A miner that is not where you expect is in one of four states, and they look identical on screen — a dot
    /// that is not on a rock. Counting them separates "cannot find ore", "flying to ore", "parked on ore" and
    /// "carrying it home", which is the difference between a targeting bug and a perfectly healthy commute.
    /// </remarks>
    public int[] MinerModeCount { get; } = new int[4];

    /// <summary>Sum and count of distance from a MINING miner to its target, to catch mining-at-range.</summary>
    public double MiningDistanceSum { get; private set; }
    public int MiningDistanceCount { get; private set; }
    public float MiningDistanceMax { get; private set; }

    /// <summary>
    /// Distance from the station centre at which cargo was actually unloaded. Deliberately CUMULATIVE, unlike the
    /// mining counters above: unloading is a rare event, a handful per tick across the whole map, so a per-tick
    /// window would report a mean drawn from one or two samples and a max that resets before it means anything.
    /// </summary>
    public double DropDistanceSum { get; private set; }
    public int DropDistanceCount { get; private set; }
    public float DropDistanceMax { get; private set; }

    /// <summary>Mean distance from the station at which miners unloaded. Should sit just under the dock range.</summary>
    public float MeanDropDistance => DropDistanceCount > 0 ? (float)(DropDistanceSum / DropDistanceCount) : 0f;

    // ─── Stand-off instrumentation ───────────────────────────────────────────────────────────────────────────────
    // Added BEFORE the behaviour it measures, so "did it work" is a comparison rather than an impression.

    /// <summary>Sum and count of the distance from an engaging fighter to its target — the headline metric.</summary>
    public double StandoffDistanceSum { get; private set; }
    public int StandoffSamples { get; private set; }
    public float MeanEngagementDistance => StandoffSamples > 0 ? (float)(StandoffDistanceSum / StandoffSamples) : 0f;

    /// <summary>
    /// Fighters that reversed their radial decision this tick (approach to retreat or back).
    /// </summary>
    /// <remarks>
    /// The failure mode this whole design risks is chatter at the boundary, and it is invisible on screen at
    /// 20 000 ships — the fleet looks busy either way. Counting reversals turns it into a number: a healthy dead
    /// band produces a handful per thousand fighters per second, a chattering one produces hundreds.
    /// </remarks>
    public long StandoffFlips { get; private set; }

    /// <summary>Fighters currently holding station in the dead band, rather than closing or backing off.</summary>
    public int StandoffOrbiting { get; private set; }

    /// <summary>
    /// Mean distance to the NEAREST other ship, sampled during acquisition. The metric for "are they still on top
    /// of each other" — the engagement distance says how far they fight from their target, this says how tightly
    /// they are packed regardless of who they are shooting.
    /// </summary>
    public double NearestNeighbourSum { get; private set; }
    public int NearestNeighbourCount { get; private set; }
    public float MeanNearestNeighbour => NearestNeighbourCount > 0
        ? (float)(NearestNeighbourSum / NearestNeighbourCount)
        : 0f;

    /// <summary>Separation accumulator, valid only for the duration of one acquisition walk.</summary>
    private float _sepX;
    private float _sepY;
    private float _sepNearest;

    /// <summary>Miners carrying at least some ore, and the mean load — separates "cannot fill" from "cannot reach".</summary>
    /// <summary>
    /// Times a miner lost its rock because the cached (chunk, slot) no longer resolved. A cluster id is NOT a
    /// stable entity handle — an asteroid that migrates between cells changes chunk, and every miner holding the
    /// old one drops its target and starts over.
    /// </summary>
    public long OreRetargets { get; private set; }

    public int LadenMiners { get; private set; }
    public float MeanCargo => LadenMiners > 0 ? (float)(_cargoSum / LadenMiners) : 0f;
    private double _cargoSum;

    /// <summary>Damage dealt to each station this tick, indexed into <see cref="_stationPos"/>.</summary>
    private int[] _stationDamage = [];

    /// <summary>Ticks each station still counts as "under attack", for pulling defenders.</summary>
    private int[] _stationThreat = [];

    /// <summary>Mirror of each station's disabled flag, so the linear scans never open a cluster.</summary>
    private bool[] _stationDown = [];

    /// <summary>Stations knocked out over the run, and how many have come back.</summary>
    public int StationsDisabled { get; private set; }
    public int StationsRebuilt { get; private set; }
    public int StationsDestroyed { get; private set; }

    /// <summary>
    /// Per-station tombstone. The parallel station arrays are NEVER compacted on a death — every one of them is
    /// addressed by an index that <see cref="StationIndexAt"/> derives from a world position, so removing an entry
    /// would silently renumber every station after it and re-point damage, threat and health at the wrong base.
    /// A tombstone costs one byte per station and keeps every index valid for the life of the run.
    /// </summary>
    private bool[] _stationDead = [];

    /// <summary>Surviving stations per faction. Zero means that faction can no longer spawn or bank ore.</summary>
    public int[] StationsAlive { get; } = new int[4];

    /// <summary>Live shield/HP mirrors for the HUD. Indexed as <see cref="_stationPos"/>.</summary>
    public int[] StationShield => _stationShield;
    public int[] StationHp => _stationHp;
    private int[] _stationShield = [];
    private int[] _stationHp = [];

    /// <summary>(rockChunkId &lt;&lt; 8 | slot) -&gt; material removed this tick.</summary>
    private readonly Dictionary<long, int> _mined = new();
    private long _nextRockSpawnTick;

    public Simulation(Config cfg, TyphonHost host)
    {
        _cfg = cfg;
        _host = host;
        _rng = new Random(cfg.Seed);
        for (var f = 0; f < 4; f++)
        {
            Material[f] = cfg.StartingMaterial;
        }
    }

    // ─── World construction ───────────────────────────────────────────────────────────────────────────────────────

    public void BuildWorld()
    {
        using var tx = _host.DBE.CreateQuickTransaction();

        var slots = string.Equals(_cfg.StationLayout, "edges", StringComparison.OrdinalIgnoreCase)
            ? BuildEdgeStations()
            : string.Equals(_cfg.StationLayout, "lattice", StringComparison.OrdinalIgnoreCase)
                ? BuildLatticeStations()
                : BuildRingStations();

        foreach (var (p, f) in slots)
        {
            var pos = Pos.At(p.X, p.Y);
            var info = new StationInfo
            {
                Faction = f,
                SpawnCooldown = (short)_rng.Next(_cfg.SpawnIntervalTicks),
                Hp = (short)Math.Min(short.MaxValue, _cfg.StationHpMax),
                Shield = (short)Math.Min(short.MaxValue, _cfg.StationShieldMax),
                CalmTicks = short.MaxValue,
            };
            tx.Spawn<Station>(Station.Position.Set(in pos), Station.Info.Set(in info));
            _stationPos.Add(p);
            _stationFaction.Add(f);
        }
        tx.Commit();

        _stationDamage = new int[_stationPos.Count];
        _stationThreat = new int[_stationPos.Count];
        _stationDown = new bool[_stationPos.Count];
        _stationDead = new bool[_stationPos.Count];
        Array.Clear(StationsAlive);
        foreach (var f in _stationFaction)
        {
            StationsAlive[f & 3]++;
        }
        _stationShield = new int[_stationPos.Count];
        _stationHp = new int[_stationPos.Count];
        Array.Fill(_stationShield, _cfg.StationShieldMax);
        Array.Fill(_stationHp, _cfg.StationHpMax);

        BuildOreAnchors();
        SpawnAsteroids(_cfg.AsteroidCount);

        for (byte f = 0; f < _cfg.Factions; f++)
        {
            SpawnShips(f, _cfg.InitialShipsPerFaction, spread: _cfg.WorldSize * _cfg.InitialSpread, free: true);
        }
    }

    /// <summary>
    /// The old layout: each faction gets a column near its own edge, so a single battle line forms in the middle.
    /// </summary>
    private List<(Vector2 pos, byte faction)> BuildEdgeStations()
    {
        var w = _cfg.WorldSize;
        var result = new List<(Vector2, byte)>();
        for (byte f = 0; f < _cfg.Factions; f++)
        {
            for (var s = 0; s < _cfg.StationsPerFaction; s++)
            {
                // Spread across the FULL usable height: s/(n-1) reaches both ends, whereas (s+1)/(n+1) never does
                // and leaves the outer stations bunched toward the middle.
                var t = _cfg.StationsPerFaction > 1 ? s / (_cfg.StationsPerFaction - 1f) : 0.5f;
                var inset = _cfg.StationEdgeInset;
                var x = f == 0 ? w * inset : w * (1f - inset);
                if (_cfg.Factions > 2)
                {
                    x = w * (inset + (1f - 2f * inset) * f / MathF.Max(1, _cfg.Factions - 1));
                }
                var vi = _cfg.StationVerticalInset;
                var y = w * (vi + (1f - 2f * vi) * t);
                x += (float)(_rng.NextDouble() - 0.5) * w * 0.01f;
                y += (float)(_rng.NextDouble() - 0.5) * w * 0.02f;
                result.Add((new Vector2(x, y), f));
            }
        }
        return result;
    }

    /// <summary>
    /// Interleaved layout: one jittered grid over the whole map, factions assigned in a checkerboard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parity assignment is what does the work — <c>(col + row) % Factions</c> guarantees that a station's
    /// orthogonal neighbours are enemies, so every base sits on a front rather than behind one. Six stations become
    /// nine adjacent enemy pairs and therefore nine places a fight can happen, instead of one.
    /// </para>
    /// <para>
    /// Parity alone would not respect <see cref="Config.StationsPerFaction"/> — an odd grid gives one faction the
    /// extra slot — so the preferred faction is taken only while it still has quota, and otherwise the slot falls
    /// to whoever is left. That keeps the counts exact and degrades the interleaving gracefully instead of
    /// silently handing someone a spare base.
    /// </para>
    /// </remarks>
    private const float StaggerFraction = 0.18f;

    /// <summary>Minimum gap between two ore fields, as a fraction of WorldSize.</summary>
    private const float OreAnchorMinSeparation = 0.13f;

    private List<(Vector2 pos, byte faction)> BuildLatticeStations()
    {
        var w = _cfg.WorldSize;
        var total = _cfg.Factions * _cfg.StationsPerFaction;
        var cols = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(total)));
        var rows = Math.Max(1, (int)MathF.Ceiling(total / (float)cols));

        // Prefer the wider axis to carry more columns, so a 6-station lattice reads as 3x2 rather than 2x3.
        if (rows > cols)
        {
            (cols, rows) = (rows, cols);
        }

        // CELL-CENTRE placement, not endpoint placement.
        //
        // The obvious formula, inset + i/(n-1) * usable, pins the first and last slot to the border. With two rows
        // that puts every station in a band at 13 % and another at 87 % and leaves the middle three-quarters of the
        // map empty — the fights end up in two horizontal stripes, which is a different failure from one central
        // scrum but no better. Placing each slot at the CENTRE of its share of the map, (i + 0.5)/n, distributes
        // the points over the map's AREA: two rows land at 25 % and 75 %, three columns at 17/50/83 %.
        var inset = _cfg.StationLatticeInset;
        var usable = 1f - 2f * inset;
        var stepX = usable / cols;
        var stepY = usable / rows;
        var jitter = _cfg.StationJitter * MathF.Min(stepX, stepY);

        var quota = new int[Math.Max(1, _cfg.Factions)];
        Array.Fill(quota, _cfg.StationsPerFaction);

        var result = new List<(Vector2, byte)>();
        for (var r = 0; r < rows && result.Count < total; r++)
        {
            for (var c = 0; c < cols && result.Count < total; c++)
            {
                // Brick stagger: alternate rows shift in opposite directions by a fraction of a step. Deliberately
                // NOT a half step — that pushes the last column of a staggered row past the far edge, and either
                // wrapping it or clamping it puts a station hard against the border, which is the one place a base
                // has no enemies on one side. A partial offset gets the brick pattern's benefit (diagonal
                // neighbours at a distance comparable to side neighbours) with nothing leaving the map.
                var offset = ((r & 1) == 1 ? 1f : -1f) * StaggerFraction;
                var fx = inset + (c + 0.5f + offset) * stepX;
                var fy = inset + (r + 0.5f) * stepY;
                fx += (float)(_rng.NextDouble() - 0.5) * jitter;
                fy += (float)(_rng.NextDouble() - 0.5) * jitter;

                var preferred = (c + r) % Math.Max(1, _cfg.Factions);
                var chosen = -1;
                for (var k = 0; k < quota.Length; k++)
                {
                    var candidate = (preferred + k) % quota.Length;
                    if (quota[candidate] > 0)
                    {
                        chosen = candidate;
                        break;
                    }
                }
                if (chosen < 0)
                {
                    continue;
                }
                quota[chosen]--;
                result.Add((new Vector2(Clamp(fx * w, 0, w), Clamp(fy * w, 0, w)), (byte)chosen));
            }
        }
        return result;
    }

    /// <summary>
    /// Stations evenly spaced around one circle centred on the map, factions alternating by index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaced <c>lattice</c> as the default because a lattice cannot avoid banding at these counts. Six
    /// stations resolve to a 3x2 grid, and two rows are two horizontal lanes however you place them inside the
    /// rows — the cell-centre formula moved the lanes to 25 %/75 % but there were still exactly two, with the
    /// middle half of the map dead. Ore that landed in that band was never mined by anyone, because no station had
    /// a reason to be there. A ring has no rows: it distributes stations over ANGLE, so the interior is equidistant
    /// from every base instead of belonging to none of them.
    /// </para>
    /// <para>
    /// Alternation is <c>i % Factions</c> around the circumference, so each station's two angular neighbours are
    /// enemies and every base sits on a front. Note the seam: with an ODD total the first and last slot are the
    /// same faction and that one adjacency is friendly. Unavoidable on a closed ring, and harmless — it costs one
    /// front out of N. The quota fallback below is what keeps <see cref="Config.StationsPerFaction"/> exact when
    /// alternation and quota disagree, exactly as the lattice does.
    /// </para>
    /// </remarks>
    private List<(Vector2 pos, byte faction)> BuildRingStations()
    {
        var w = _cfg.WorldSize;
        var total = Math.Max(1, _cfg.Factions * _cfg.StationsPerFaction);
        var cx = w * 0.5f;
        var cy = w * 0.5f;
        var step = MathF.PI * 2f / total;

        var quota = new int[Math.Max(1, _cfg.Factions)];
        Array.Fill(quota, _cfg.StationsPerFaction);

        var result = new List<(Vector2, byte)>();
        for (var i = 0; i < total; i++)
        {
            var preferred = i % Math.Max(1, _cfg.Factions);
            var chosen = -1;
            for (var k = 0; k < quota.Length; k++)
            {
                var candidate = (preferred + k) % quota.Length;
                if (quota[candidate] > 0)
                {
                    chosen = candidate;
                    break;
                }
            }
            if (chosen < 0)
            {
                continue;
            }
            quota[chosen]--;

            // Angular jitter is scaled by the STEP, not by the world, so it can never reorder two stations around
            // the ring however large StationJitter is set — the pattern degrades to uneven spacing, never to a
            // scrambled faction sequence, which is the property the alternation depends on.
            var a = -MathF.PI / 2f + i * step + (float)(_rng.NextDouble() - 0.5) * step * _cfg.StationJitter;
            var rad = w * _cfg.StationRingRadiusPct
                      * (1f + (float)(_rng.NextDouble() - 0.5) * 2f * _cfg.StationRingRadiusJitter);
            result.Add((new Vector2(Clamp(cx + MathF.Cos(a) * rad, 0, w), Clamp(cy + MathF.Sin(a) * rad, 0, w)), (byte)chosen));
        }
        return result;
    }

    /// <summary>
    /// Computes the fixed points asteroids spawn (and respawn) at.
    /// </summary>
    /// <remarks>
    /// For the <c>contested</c> layout an anchor is the midpoint of a cross-faction station pair, taking the
    /// closest pairs first: ore lands exactly between two hostile bases, so both sides' miners have the same claim
    /// on it and both sides' fighters have a reason to be there. That is what makes the conflict local — the
    /// alternative, one ore field at the map centre, gives every miner on the map the same destination.
    /// </remarks>
    private void BuildOreAnchors()
    {
        _oreAnchors.Clear();
        var w = _cfg.WorldSize;
        var want = Math.Max(1, _cfg.AsteroidCount);

        if (string.Equals(_cfg.AsteroidLayout, "contested", StringComparison.OrdinalIgnoreCase) && _stationPos.Count > 1)
        {
            var pairs = new List<(float d2, Vector2 mid)>();
            for (var i = 0; i < _stationPos.Count; i++)
            {
                for (var j = i + 1; j < _stationPos.Count; j++)
                {
                    if (_stationFaction[i] == _stationFaction[j])
                    {
                        continue;   // ore between two friendly bases is not contested by anyone
                    }
                    var d = _stationPos[i] - _stationPos[j];
                    pairs.Add((d.LengthSquared(), (_stationPos[i] + _stationPos[j]) * 0.5f));
                }
            }
            pairs.Sort((a, b) => a.d2.CompareTo(b.d2));

            // Shortest pairs first, but never two ore fields on top of each other.
            //
            // Taking the N shortest midpoints outright looks right and is not: with three stations per side, the
            // midpoints of the three LONG cross-map pairs all land within a few kilometres of the map centre, so
            // three of the eight asteroids would pile up there and quietly rebuild the single central ore field
            // this layout exists to abolish. Enforcing a minimum separation is what actually distributes them.
            //
            // The separation is relaxed rather than abandoned if it cannot be met, so the asteroid count is always
            // honoured — a spacing preference must not silently become a cap on how much ore exists.
            var minSep = w * OreAnchorMinSeparation;
            for (var relax = 0; relax < 4 && _oreAnchors.Count < want; relax++)
            {
                var sep2 = minSep * minSep;
                foreach (var p in pairs)
                {
                    if (_oreAnchors.Count >= want)
                    {
                        break;
                    }
                    var tooClose = false;
                    foreach (var existing in _oreAnchors)
                    {
                        if (Vector2.DistanceSquared(existing, p.mid) < sep2)
                        {
                            tooClose = true;
                            break;
                        }
                    }
                    if (!tooClose)
                    {
                        _oreAnchors.Add(p.mid);
                    }
                }
                minSep *= 0.5f;
            }

            // More asteroids than adjacent enemy pairs: fill the remainder on a ring so the count is always honoured.
            for (var k = 0; _oreAnchors.Count < want; k++)
            {
                var a = -MathF.PI / 2f + k * (MathF.PI * 2f / want);
                var rad = w * _cfg.AsteroidFieldRadiusPct;
                _oreAnchors.Add(new Vector2(w * 0.5f + MathF.Cos(a) * rad, w * 0.5f + MathF.Sin(a) * rad));
            }
            return;
        }

        if (string.Equals(_cfg.AsteroidLayout, "ring", StringComparison.OrdinalIgnoreCase))
        {
            var rad = w * _cfg.AsteroidFieldRadiusPct;
            for (var v = 0; v < want; v++)
            {
                // Start at -90 degrees so an odd count points straight up — a 3-asteroid ring reads as a triangle.
                var a = -MathF.PI / 2f + v * (MathF.PI * 2f / want);
                _oreAnchors.Add(new Vector2(w * 0.5f + MathF.Cos(a) * rad, w * 0.5f + MathF.Sin(a) * rad));
            }
        }
        // "scatter" leaves the anchor list empty: SpawnAsteroids falls back to random placement.
    }

    /// <summary>Chooses the anchor furthest from anything already occupying one, so a respawn fills the gap.</summary>
    private Vector2 PickVacantAnchor(List<Vector2> taken)
    {
        var best = _oreAnchors[0];
        var bestScore = -1f;
        foreach (var anchor in _oreAnchors)
        {
            var nearest = float.MaxValue;
            foreach (var t in taken)
            {
                nearest = MathF.Min(nearest, Vector2.DistanceSquared(anchor, t));
            }
            if (nearest > bestScore)
            {
                bestScore = nearest;
                best = anchor;
            }
        }
        return best;
    }

    /// <summary>Scatters asteroids over the whole map — material is where they are, and everything follows.</summary>
    private void SpawnAsteroids(int count)
    {
        if (count <= 0)
        {
            return;
        }
        // Anchors already taken — live asteroids plus any placed earlier in this same call — so a respawn lands on
        // the vacant anchor rather than on top of a survivor.
        var anchored = _oreAnchors.Count > 0;
        var chosen = anchored ? CollectLiveAsteroidPositions() : new List<Vector2>();

        using var tx = _host.DBE.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var fieldR = _cfg.WorldSize * _cfg.AsteroidFieldRadiusPct;
            var cx = _cfg.WorldSize * 0.5f;
            var cy = _cfg.WorldSize * 0.5f;
            float x, y;
            if (anchored)
            {
                var anchor = PickVacantAnchor(chosen);
                // A little jitter so a respawned rock is not pixel-identical to the one it replaces, and so two
                // anchors that happen to coincide do not stack.
                var j = _cfg.WorldSize * _cfg.AsteroidAnchorJitter * _cfg.AsteroidFieldRadiusPct;
                x = Clamp(anchor.X + (float)(_rng.NextDouble() - 0.5) * j, 0, _cfg.WorldSize);
                y = Clamp(anchor.Y + (float)(_rng.NextDouble() - 0.5) * j, 0, _cfg.WorldSize);
                chosen.Add(new Vector2(x, y));
            }
            else
            {
                // Uniform over the disc, not over (r, theta): sqrt() on the radius, otherwise everything bunches
                // at the centre.
                var theta = (float)(_rng.NextDouble() * Math.PI * 2);
                var rad = fieldR * MathF.Sqrt((float)_rng.NextDouble());
                x = Clamp(cx + MathF.Cos(theta) * rad, 0, _cfg.WorldSize);
                y = Clamp(cy + MathF.Sin(theta) * rad, 0, _cfg.WorldSize);
            }
            var ang = (float)(_rng.NextDouble() * Math.PI * 2);
            var speed = _cfg.AsteroidSpeed * (0.3f + (float)_rng.NextDouble());
            var cap = (int)(_cfg.AsteroidCapacity * (0.5 + _rng.NextDouble()));
            var pos = Pos.At(x, y);
            var a = new Asteroid
            {
                VX = MathF.Cos(ang) * speed,
                VY = MathF.Sin(ang) * speed,
                Capacity = cap,
                MaxCapacity = cap,
            };
            tx.Spawn<Rock>(Rock.Position.Set(in pos), Rock.Asteroid.Set(in a));
            AsteroidsAlive++;
        }
        tx.Commit();
    }

    private void SpawnShips(byte faction, int count, float spread, bool free = false)
    {
        if (count <= 0)
        {
            return;
        }
        using var tx = _host.DBE.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            if (ShipsAlive[faction] >= _cfg.MaxShipsPerFaction)
            {
                break;
            }
            // Ships cost material. With no miners bringing ore home, a faction simply stops reinforcing.
            if (!free)
            {
                if (Material[faction] < _cfg.ShipCost)
                {
                    break;
                }
                Material[faction] -= _cfg.ShipCost;
            }
            var origin = PickStation(faction);
            var x = Clamp(origin.X + (float)(_rng.NextDouble() - 0.5) * spread, 0, _cfg.WorldSize);
            var y = Clamp(origin.Y + (float)(_rng.NextDouble() - 0.5) * spread, 0, _cfg.WorldSize);

            var pos = Pos.At(x, y);
            var isMiner = _forceMiner || _rng.NextDouble() < _cfg.MinerRatio;
            var heavy = !isMiner && _rng.NextDouble() < 0.15;
            var kind = isMiner ? KindMiner : heavy ? KindHeavy : KindFighter;
            var speedScale = isMiner ? 0.7f : heavy ? 0.6f : 1f;
            var mot = new Motion
            {
                // Expressed as a fraction of top speed, not as an absolute: this was the one distance in the whole
                // simulation hardcoded in world units, so it was also the one thing that did not follow the rescale.
                VX = (float)(_rng.NextDouble() - 0.5) * _cfg.ShipMaxSpeed * 0.06f,
                VY = (float)(_rng.NextDouble() - 0.5) * _cfg.ShipMaxSpeed * 0.06f,
                MaxSpeed = _cfg.ShipMaxSpeed * speedScale,
            };
            var com = new Combat
            {
                Faction = faction,
                Hp = (short)(_cfg.ShipHp * (heavy ? 3 : isMiner ? 2 : 1)),
                Shield = (short)(_cfg.ShipShieldMax * (heavy ? 3 : isMiner ? 2 : 1)),
                CalmTicks = short.MaxValue,
                // Orbit direction, fixed for life. Half the fleet circles each way, so opposing groups interleave
                // rather than all rotating together as one rigid body.
                SteerFlags = (byte)(_rng.Next(2)),
                Cooldown = (short)_rng.Next(_cfg.WeaponCooldownTicks),
                ReacquireIn = (short)_rng.Next(_cfg.TargetReacquireTicks),
                Kind = kind,
                Damage = (short)(isMiner ? 0 : _cfg.ShipDamage * (heavy ? 2 : 1)),
            };
            var min = new Miner
            {
                HomeX = origin.X,
                HomeY = origin.Y,
                CargoMax = (short)_cfg.CargoMax,
                OreKey = 0,
            };
            tx.Spawn<Ship>(Ship.Position.Set(in pos), Ship.Motion.Set(in mot), Ship.Combat.Set(in com), Ship.Miner.Set(in min));
            ShipsAlive[faction]++;
            if (isMiner)
            {
                MinersAlive[faction]++;
            }
            TotalSpawned++;
        }
        tx.Commit();
    }

    /// <summary>Spawns exactly one free miner for a faction that has fallen below the floor.</summary>
    private void SpawnMiner(byte faction, ref SimStats stats)
    {
        var before = MinersAlive[faction];
        _forceMiner = true;
        try
        {
            SpawnShips(faction, 1, spread: _cfg.WorldSize * 0.02f, free: true);
        }
        finally
        {
            _forceMiner = false;
        }
        stats.Spawned += MinersAlive[faction] - before;
    }

    private bool _forceMiner;

    /// <summary>
    /// Nearest station of the given faction, or null if that faction has none left.
    /// </summary>
    /// <remarks>
    /// A linear scan over a list that holds one entry per station — six by default, eight at most. Called once per
    /// tick per targetless fighter, which is a few thousand distance comparisons against a list that fits in a
    /// single cache line's worth of vectors. Not worth indexing, and deliberately not routed through a spatial
    /// query: stations are the one thing in this simulation that never move.
    /// </remarks>
    private Vector2? NearestStation(float px, float py, int faction)
    {
        var best = float.MaxValue;
        Vector2? found = null;
        for (var i = 0; i < _stationPos.Count; i++)
        {
            if (_stationFaction[i] != faction || _stationDead[i])
            {
                continue;
            }
            var d = _stationPos[i] - new Vector2(px, py);
            var d2 = d.LengthSquared();
            if (d2 < best)
            {
                best = d2;
                found = _stationPos[i];
            }
        }
        return found;
    }

    /// <summary>True if a station stands at this position and has not been destroyed.</summary>
    private bool IsLiveStationAt(float x, float y)
    {
        var idx = StationIndexAt(x, y);
        return idx >= 0 && !_stationDead[idx];
    }

    /// <summary>
    /// Index of the station at a position, or -1. Positions are fixed at world build, so an exact-ish match is
    /// enough and no lookup structure is warranted for six entries. Deliberately does NOT filter tombstones —
    /// callers that care use <see cref="IsLiveStationAt"/>, while damage banking needs the index of a station
    /// whatever its state.
    /// </summary>
    private int StationIndexAt(float x, float y)
    {
        var best = -1;
        var bestD2 = 1f;   // a metre of slop; stations never move, so this only absorbs float round-trip
        for (var i = 0; i < _stationPos.Count; i++)
        {
            var dx = _stationPos[i].X - x;
            var dy = _stationPos[i].Y - y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Nearest enemy ship to a station, for its gun. One spatial query per station per shot — six stations on an
    /// 8-tick cooldown is under one query per tick, which is noise beside the ~10 000 projectile hit tests.
    /// </summary>
    private bool TryAcquireForStation(float x, float y, byte faction, out float tx, out float ty)
    {
        tx = 0;
        ty = 0;
        var best = float.MaxValue;
        var found = false;
        var sphere = new BSphere2F { CenterX = x, CenterY = y, Radius = _cfg.StationWeaponRange };
        using var tr = _host.DBE.CreateQuickTransaction();
        var q = _host.DBE.ClusterSpatialQuery<Ship>().Radius(in sphere);
        using var acc = tr.For<Ship>();
        var examined = 0;
        while (q.MoveNext())
        {
            if (++examined > _cfg.AcquireScanCap)
            {
                break;
            }
            var hit = q.Current;
            if (!TryReadFaction(acc, hit.ClusterChunkId, hit.SlotIndex, out var hf) || hf == faction)
            {
                continue;
            }
            if (hit.DistanceSq < best)
            {
                best = hit.DistanceSq;
                tx = hit.MinX;
                ty = hit.MinY;
                found = true;
            }
        }
        q.Dispose();
        return found;
    }

    /// <summary>
    /// A projectile against the stations. Linear scan, deliberately — see the note in <see cref="Config"/>.
    /// </summary>
    private bool TryHitStation(float x, float y, byte faction, int damage)
    {
        var reach = _cfg.StationRadius + _cfg.ShotHitRadius;
        var reach2 = reach * reach;
        for (var i = 0; i < _stationPos.Count; i++)
        {
            if (_stationFaction[i] == faction || _stationDead[i])
            {
                continue;   // no friendly fire, and a destroyed station is not there to be hit
            }
            // A DISABLED station is still a valid target (destructible mode off). It cannot be hurt further, but
            // the hits keep its calm timer at zero, which suppresses the rebuild — so holding the ground keeps the
            // base out of the war, and walking away lets it come back. A DESTROYED one is skipped above: there is
            // no wreck to garrison and no rebuild to suppress, so rounds spent on it would vanish into empty space.
            var dx = _stationPos[i].X - x;
            var dy = _stationPos[i].Y - y;
            if (dx * dx + dy * dy > reach2)
            {
                continue;
            }
            _stationDamage[i] += damage;
            TotalShotHits++;
            return true;
        }
        return false;
    }

    /// <summary>Nearest own station that is currently under attack and within defending range.</summary>
    private bool TryFindThreatenedStation(float px, float py, int faction, out float sx, out float sy)
    {
        sx = 0;
        sy = 0;
        var best = _cfg.StationDefendRadius * _cfg.StationDefendRadius;
        var found = false;
        for (var i = 0; i < _stationPos.Count; i++)
        {
            if (_stationFaction[i] != faction || _stationThreat[i] <= 0 || _stationDead[i])
            {
                continue;
            }
            var dx = _stationPos[i].X - px;
            var dy = _stationPos[i].Y - py;
            var d2 = dx * dx + dy * dy;
            if (d2 < best)
            {
                best = d2;
                sx = _stationPos[i].X;
                sy = _stationPos[i].Y;
                found = true;
            }
        }
        return found;
    }

    /// <summary>Shield/HP percentages per station, for the report.</summary>
    public string DescribeStationHealth()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < _stationPos.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("  ");
            }
            if (_stationDead[i])
            {
                sb.Append((char)('A' + _stationFaction[i])).Append("=DESTROYED");
                continue;
            }
            var sh = _cfg.StationShieldMax > 0 ? 100 * _stationShield[i] / _cfg.StationShieldMax : 0;
            var hp = _cfg.StationHpMax > 0 ? 100 * _stationHp[i] / _cfg.StationHpMax : 0;
            sb.Append((char)('A' + _stationFaction[i]))
              .Append(_stationDown[i] ? "!" : ":")
              .Append("s").Append(sh).Append("/h").Append(hp);
        }
        return sb.ToString();
    }

    /// <summary>Compact station listing for the auto report — verifies the layout is what it claims to be.</summary>
    /// <summary>Position of the station closest to a point. Six-element scan; used by the auto-mode station probe.</summary>
    public Vector2 NearestStationPosition(float x, float y)
    {
        var best = new Vector2(x, y);
        var bestD2 = float.MaxValue;
        for (var i = 0; i < _stationPos.Count; i++)
        {
            var d2 = Vector2.DistanceSquared(_stationPos[i], new Vector2(x, y));
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = _stationPos[i];
            }
        }
        return best;
    }

    public string DescribeStations()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < _stationPos.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("  ");
            }
            sb.Append((char)('A' + _stationFaction[i]))
              .Append('(').Append((int)(_stationPos[i].X / 1000f)).Append(',')
              .Append((int)(_stationPos[i].Y / 1000f)).Append(')');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Re-points every miner homed on a just-destroyed station at the nearest surviving friendly base.
    /// </summary>
    /// <remarks>
    /// Without this a laden miner flies to the coordinates of a base that no longer exists, satisfies the unload
    /// test against nothing, and banks its ore into a crater — or rather never banks it at all, because the drop
    /// only fires inside <see cref="Config.StationDockRange"/> of a home that is still there in the miner's own
    /// component. It would orbit the wreck site with a full hold for the rest of the run.
    /// <para>
    /// Done eagerly, as ONE full ship pass at the moment of death, rather than by checking "is my home still
    /// alive?" every tick in <c>MinerSteer</c>. A station dies at most a handful of times in a run, so this is a
    /// rare O(ships) cost instead of a permanent O(miners) one; the alternative would also have needed a spare byte
    /// in <c>Miner</c> to cache the home index, and that component is exactly 32 bytes with nothing to spare.
    /// </para>
    /// </remarks>
    private void RehomeMinersFrom(Vector2 deadPos, byte faction)
    {
        var replacement = NearestStation(deadPos.X, deadPos.Y, faction);
        if (!replacement.HasValue)
        {
            // The faction has no bases left. Leave the homes pointing at the ruin: there is nowhere to send them,
            // and inventing a destination would only disguise an elimination as a working economy.
            return;
        }
        var to = replacement.Value;
        var moved = 0;
        using var tx = _host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Ship>();
        using var e = acc.GetClusterEnumerator();
        foreach (var cluster in e)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var com = cluster.GetReadOnlySpan(Ship.Combat);
            var mnr = cluster.GetSpan(Ship.Miner);
            var touched = false;
            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (com[i].Kind != KindMiner || com[i].Faction != faction)
                {
                    continue;
                }
                ref var mi = ref mnr[i];
                var dx = mi.HomeX - deadPos.X;
                var dy = mi.HomeY - deadPos.Y;
                if (dx * dx + dy * dy > 1f)
                {
                    continue;   // homed on a different base of the same faction
                }
                mi.HomeX = to.X;
                mi.HomeY = to.Y;
                touched = true;
                moved++;
            }
            if (touched)
            {
                cluster.MarkDirty(Ship.Miner);
            }
        }
        tx.Commit();
        MinersRehomed += moved;
    }

    /// <summary>Miners re-pointed at a new base after their own was destroyed. Cumulative.</summary>
    public int MinersRehomed { get; private set; }

    private Vector2 PickStation(byte faction)
    {
        var candidates = new List<Vector2>();
        for (var i = 0; i < _stationPos.Count; i++)
        {
            if (_stationFaction[i] == faction && !_stationDead[i])
            {
                candidates.Add(_stationPos[i]);
            }
        }
        return candidates.Count == 0
            ? new Vector2(_cfg.WorldSize * 0.5f, _cfg.WorldSize * 0.5f)
            : candidates[_rng.Next(candidates.Count)];
    }

    // ─── Tick ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public SimStats Step(float dt)
    {
        var stats = default(SimStats);
        _pendingShots.Clear();
        _dead.Clear();
        _damage.Clear();
        _mined.Clear();

        // Station damage is banked by the projectile pass and consumed by StationTick on the FOLLOWING tick — one
        // tick of latency on a 20 000-HP structure is invisible. It is cleared where it is READ, not here: clearing
        // at the top of the tick would wipe the previous tick's damage before StationTick ever saw it.
        for (var i = 0; i < _stationThreat.Length; i++)
        {
            if (_stationThreat[i] > 0)
            {
                _stationThreat[i]--;
            }
        }

        Array.Clear(MinerModeCount);
        MiningDistanceSum = 0;
        MiningDistanceCount = 0;
        MiningDistanceMax = 0;
        LadenMiners = 0;
        _cargoSum = 0;
        StandoffDistanceSum = 0;
        StandoffSamples = 0;
        StandoffOrbiting = 0;
        NearestNeighbourSum = 0;
        NearestNeighbourCount = 0;

        InvalidateFactionCache();
        ComputeCentroids();
        AsteroidTick(dt, ref stats);
        PickupTick(ref stats);
        StationTick(ref stats);
        ShipTick(dt, ref stats);
        if (_cfg.ProjectilesEnabled)
        {
            ShotTick(dt, ref stats);
        }
        ApplyMining(ref stats);
        InvalidateFactionCache();
        ApplyDamage(ref stats);
        FlushSpawns(ref stats);
        Reap(ref stats);

        _host.RunTickFence();
        return stats;
    }

    private void ComputeCentroids()
    {
        Span<Vector2> sum = stackalloc Vector2[4];
        Span<int> n = stackalloc int[4];
        Span<Vector2> msum = stackalloc Vector2[4];
        Span<int> mn = stackalloc int[4];
        using var tx = _host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Ship>();
        using var e = acc.GetClusterEnumerator();
        foreach (var cluster in e)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var pos = cluster.GetReadOnlySpan(Ship.Position);
            var com = cluster.GetReadOnlySpan(Ship.Combat);
            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var f = com[i].Faction & 3;
                var p = new Vector2(pos[i].Bounds.MinX, pos[i].Bounds.MinY);
                sum[f] += p;
                n[f]++;
                if (com[i].Kind == KindMiner)
                {
                    msum[f] += p;
                    mn[f]++;
                }
            }
        }
        for (var f = 0; f < 4; f++)
        {
            _centroid[f] = n[f] > 0 ? sum[f] / n[f] : new Vector2(_cfg.WorldSize * 0.5f, _cfg.WorldSize * 0.5f);
            _hasMiners[f] = mn[f] > 0;
            _minerCentroid[f] = mn[f] > 0 ? msum[f] / mn[f] : _centroid[f];
        }
    }

    private void StationTick(ref SimStats stats)
    {
        var toSpawn = new List<(byte faction, int count)>();
        var killed = new List<(Vector2 pos, byte faction)>();
        using (var tx = _host.DBE.CreateQuickTransaction())
        {
            using var acc = tx.For<Station>();
            using var e = acc.GetClusterEnumerator();
            foreach (var cluster in e)
            {
                var bits = cluster.OccupancyBits;
                if (bits == 0)
                {
                    continue;
                }
                var info = cluster.GetSpan(Station.Info);
                var spos = cluster.GetReadOnlySpan(Station.Position);
                var touched = true;
                while (bits != 0)
                {
                    var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    ref var s = ref info[i];
                    var sx = spos[i].Bounds.MinX;
                    var sy = spos[i].Bounds.MinY;
                    var idx = StationIndexAt(sx, sy);
                    if (idx >= 0 && _stationDead[idx])
                    {
                        continue;   // destroyed earlier this tick; the entity is queued for Reap but still visible here
                    }

                    // ── damage banked by the projectile pass, shield first ──
                    var dmg = 0;
                    if (idx >= 0)
                    {
                        dmg = _stationDamage[idx];
                        _stationDamage[idx] = 0;
                    }
                    if (dmg > 0)
                    {
                        s.CalmTicks = 0;
                        if (idx >= 0)
                        {
                            _stationThreat[idx] = _cfg.StationThreatTicks;
                        }
                        var toShield = Math.Min(dmg, s.Shield);
                        s.Shield -= (short)toShield;
                        var toHull = dmg - toShield;
                        if (toHull > 0)
                        {
                            s.Hp = (short)Math.Max(0, s.Hp - toHull);
                        }
                        if (s.Hp <= 0 && s.Disabled == 0)
                        {
                            s.Disabled = 1;
                            StationsDisabled++;
                            if (_cfg.StationsDestructible && idx >= 0)
                            {
                                // Destroyed for good. The entity is queued for the same Reap that collects dead
                                // ships, so the despawn happens at the tick boundary rather than inside this
                                // enumeration — mutating the cluster we are walking would be the classic version
                                // of this bug. The tombstone is set NOW so every scan this tick already skips it.
                                _stationDead[idx] = true;
                                StationsDestroyed++;
                                StationsAlive[s.Faction & 3] = Math.Max(0, StationsAlive[s.Faction & 3] - 1);
                                _dead.Add(cluster.GetEntityId(i));
                                // Rehoming is deferred past this transaction's commit: it needs a WRITE pass over
                                // Ship, and opening one inside the open Station transaction is a nesting this code
                                // has no reason to risk when the work can simply happen a few lines later.
                                killed.Add((_stationPos[idx], s.Faction));
                                continue;
                            }
                        }
                    }
                    else if (s.CalmTicks < short.MaxValue)
                    {
                        s.CalmTicks++;
                    }

                    // ── regeneration ──
                    if (s.Disabled != 0)
                    {
                        // Rebuilding — but only while nothing is shooting it. A garrison sitting on a wreck keeps
                        // CalmTicks pinned at zero and the base stays out of the war; the moment the attackers
                        // leave or die, it starts coming back. Without this gate the rebuild ran regardless of the
                        // swarm parked on top of it, which made capturing ground pointless.
                        if (s.CalmTicks >= _cfg.StationRegenDelayTicks)
                        {
                            s.Hp = (short)Math.Min(_cfg.StationHpMax, s.Hp + _cfg.StationHpRegen);
                            if (s.Hp >= _cfg.StationHpMax)
                            {
                                s.Disabled = 0;
                                s.Shield = (short)Math.Min(short.MaxValue, _cfg.StationShieldMax);
                                StationsRebuilt++;
                            }
                        }
                    }
                    else if (s.CalmTicks >= _cfg.StationRegenDelayTicks && s.Shield < _cfg.StationShieldMax)
                    {
                        s.Shield = (short)Math.Min(_cfg.StationShieldMax, s.Shield + _cfg.StationShieldRegen);
                    }

                    if (idx >= 0)
                    {
                        _stationDown[idx] = s.Disabled != 0;
                        _stationShield[idx] = s.Shield;
                        _stationHp[idx] = s.Hp;
                    }

                    // ── gun ──
                    if (s.Cooldown > 0)
                    {
                        s.Cooldown--;
                    }
                    else if (s.Disabled == 0 && _cfg.StationsShoot && _cfg.ProjectilesEnabled && ShotsAlive < _cfg.MaxShots)
                    {
                        if (TryAcquireForStation(sx, sy, s.Faction, out var tx2, out var ty2))
                        {
                            s.Cooldown = (short)_cfg.StationCooldownTicks;
                            var ddx = tx2 - sx;
                            var ddy = ty2 - sy;
                            var l = MathF.Sqrt(ddx * ddx + ddy * ddy);
                            if (l > 1e-3f && !float.IsNaN(l))
                            {
                                _pendingShots.Add(new PendingShot
                                {
                                    X = sx, Y = sy,
                                    VX = ddx / l * _cfg.ShotSpeed,
                                    VY = ddy / l * _cfg.ShotSpeed,
                                    Faction = s.Faction,
                                    Damage = (short)_cfg.StationDamage,
                                    Boosted = false,
                                });
                            }
                        }
                    }

                    // ── spawning, gated on being operational ──
                    if (s.Disabled != 0)
                    {
                        continue;
                    }
                    if (s.SpawnCooldown > 0)
                    {
                        s.SpawnCooldown--;
                        continue;
                    }
                    s.SpawnCooldown = (short)_cfg.SpawnIntervalTicks;
                    s.SpawnedTotal += _cfg.SpawnBatch;
                    toSpawn.Add((s.Faction, _cfg.SpawnBatch));
                }
                if (touched)
                {
                    cluster.MarkDirty(Station.Info);
                }
            }
            tx.Commit();
        }

        foreach (var (pos, faction) in killed)
        {
            RehomeMinersFrom(pos, faction);
        }

        // Endless-run floor: a faction with no miners can never earn material again, so top it up for free.
        var leadMiners = 0;
        for (var f = 0; f < _cfg.Factions; f++)
        {
            leadMiners = Math.Max(leadMiners, MinersAlive[f]);
        }
        var floor = Math.Max(_cfg.MinerFloor, (int)(leadMiners * _cfg.MinerFloorRatio));
        for (byte f = 0; f < _cfg.Factions; f++)
        {
            if (MinersAlive[f] >= floor || _host.Tick < _nextFloorTick[f])
            {
                continue;
            }
            _nextFloorTick[f] = _host.Tick + _cfg.MinerFloorIntervalTicks;
            SpawnMiner(f, ref stats);
        }

        foreach (var (faction, count) in toSpawn)
        {
            if (faction >= 4)
            {
                continue;
            }
            var before = ShipsAlive[faction];
            SpawnShips(faction, count, spread: _cfg.WorldSize * 0.015f);
            stats.Spawned += ShipsAlive[faction] - before;
        }
    }

    private void ShipTick(float dt, ref SimStats stats)
    {
        using var tx = _host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Ship>();
        using var e = acc.GetClusterEnumerator();

        var world = _cfg.WorldSize;
        var range2 = _cfg.WeaponRange * _cfg.WeaponRange;
        var mineRange2 = _cfg.MineRange * _cfg.MineRange;
        var dropRange2 = _cfg.StationDockRange * _cfg.StationDockRange;

        foreach (var cluster in e)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var pos = cluster.GetReadOnlySpan(Ship.Position);
            var mot = cluster.GetSpan(Ship.Motion);
            var com = cluster.GetSpan(Ship.Combat);
            var mnr = cluster.GetSpan(Ship.Miner);

            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                ref var c = ref com[i];
                if (c.Dead != 0)
                {
                    continue;
                }
                ref var m = ref mot[i];
                ref var mi = ref mnr[i];
                if (c.HitFlash > 0)
                {
                    c.HitFlash--;
                }
                if (c.ThreatTicks > 0)
                {
                    c.ThreatTicks--;
                }

                // Regeneration, gated on not having been hit recently. Integer cadence rather than a fractional
                // per-tick rate: the pools are shorts, so "one point every N ticks" is exact where "0.033 per
                // tick" would need accumulator state per ship.
                if (c.CalmTicks < short.MaxValue)
                {
                    c.CalmTicks++;
                }
                if (c.CalmTicks >= _cfg.ShipRegenDelayTicks)
                {
                    var maxShield = (short)(_cfg.ShipShieldMax * (c.Kind == KindHeavy ? 3 : c.Kind == KindMiner ? 2 : 1));
                    if (c.Shield < maxShield && _host.Tick % Math.Max(1, _cfg.ShipShieldRegenTicks) == 0)
                    {
                        c.Shield++;
                    }
                    var maxHp = (short)(_cfg.ShipHp * (c.Kind == KindHeavy ? 3 : c.Kind == KindMiner ? 2 : 1));
                    if (c.Hp < maxHp && _host.Tick % Math.Max(1, _cfg.ShipHpRegenTicks) == 0)
                    {
                        c.Hp++;
                    }
                }
                var px = pos[i].Bounds.MinX;
                var py = pos[i].Bounds.MinY;

                float dirX;
                float dirY;

                if (c.Kind == KindMiner)
                {
                    MinerSteer(ref c, ref mi, px, py, mineRange2, dropRange2, ref stats, out dirX, out dirY);
                }
                else
                {
                    FighterSteer(ref c, px, py, ref stats, out dirX, out dirY);
                    // Fighters only — miners must be able to close all the way onto a rock.
                    ApplyStandoff(ref c, px, py, ref dirX, ref dirY);
                    // Separation is applied OUTSIDE ApplyStandoff, which returns early for a fighter with no
                    // target — precisely the ships that idle in a heap and most need pushing apart.
                    ApplySeparation(in c, ref dirX, ref dirY);
                }

                var len = MathF.Sqrt(dirX * dirX + dirY * dirY);
                if (len > 1e-3f)
                {
                    dirX /= len;
                    dirY /= len;
                }

                // A mining miner parks; so does a ship that has just fired. Both mean no thrust and a hard velocity
                // bleed, so the ship settles rather than coasting through the whole root window.
                if (c.RootTicks > 0)
                {
                    c.RootTicks--;
                }
                var parked = (c.Kind == KindMiner && mi.Mode == 1) || c.RootTicks > 0;
                if (parked)
                {
                    m.VX *= 0.85f;
                    m.VY *= 0.85f;
                }
                else
                {
                    var jitter = _cfg.WanderStrength;
                    m.VX += (dirX * _cfg.ShipAccel + ((float)_rng.NextDouble() - 0.5f) * _cfg.ShipAccel * jitter) * dt;
                    m.VY += (dirY * _cfg.ShipAccel + ((float)_rng.NextDouble() - 0.5f) * _cfg.ShipAccel * jitter) * dt;
                }

                // The speed effect multiplies the CAP at use rather than rewriting Motion.MaxSpeed, so it needs no
                // per-entity bookkeeping to expire and cannot leave a ship permanently fast if a tick is missed.
                var cap = m.MaxSpeed * SpeedMul(c.Faction);
                var sp = MathF.Sqrt(m.VX * m.VX + m.VY * m.VY);
                if (sp > cap && sp > 1e-3f)
                {
                    var k = cap / sp;
                    m.VX *= k;
                    m.VY *= k;
                }

                var nx = px + m.VX * dt;
                var ny = py + m.VY * dt;

                if (nx < 0f) { nx = 0f; m.VX = MathF.Abs(m.VX); }
                else if (nx > world) { nx = world; m.VX = -MathF.Abs(m.VX); }
                if (ny < 0f) { ny = 0f; m.VY = MathF.Abs(m.VY); }
                else if (ny > world) { ny = world; m.VY = -MathF.Abs(m.VY); }

                if (Bad("ship.move", nx, ny, m.VX, m.VY, dirX, dirY, c.TargetX, c.TargetY))
                {
                    nx = Clamp(px, 0, world);
                    ny = Clamp(py, 0, world);
                    m.VX = 0;
                    m.VY = 0;
                    c.HasTarget = 0;
                }
                cluster.WriteSpatial(Ship.Position, i, Pos.At(nx, ny));
                stats.ShipsMoved++;

                // ── firing: miners are unarmed ──
                if (c.Kind == KindMiner)
                {
                    continue;
                }
                if (c.Cooldown > 0)
                {
                    c.Cooldown--;
                }
                else if (c.HasTarget != 0)
                {
                    var ddx = c.TargetX - nx;
                    var ddy = c.TargetY - ny;
                    if (ddx * ddx + ddy * ddy <= range2 && _cfg.ProjectilesEnabled && ShotsAlive < _cfg.MaxShots)
                    {
                        c.Cooldown = (short)_cfg.WeaponCooldownTicks;
                        c.RootTicks = (byte)Math.Clamp(_cfg.FireRootTicks, 0, 255);
                        var l = MathF.Sqrt(ddx * ddx + ddy * ddy);
                        if (l > 1e-3f && !float.IsNaN(l))
                        {
                            if (Bad("ship.fire", nx, ny, ddx, ddy, l))
                            {
                                continue;
                            }
                            var boosted = PowerTicks[c.Faction & 3] > 0;
                            _pendingShots.Add(new PendingShot
                            {
                                X = nx, Y = ny,
                                VX = ddx / l * _cfg.ShotSpeed,
                                VY = ddy / l * _cfg.ShotSpeed,
                                Faction = c.Faction,
                                Damage = (short)(boosted ? c.Damage * _cfg.PowerDamageMultiplier : c.Damage),
                                Boosted = boosted,
                            });
                        }
                    }
                }
            }
            cluster.MarkDirty(Ship.Combat);
            cluster.MarkDirty(Ship.Miner);
        }
        tx.Commit();
    }

    /// <summary>
    /// Fighter behaviour: hunt the nearest enemy; with none in sight, escort friendly miners rather than charging
    /// the map centre. The escort rule is what keeps the fighting where the economy is.
    /// </summary>
    private void FighterSteer(ref Combat c, float px, float py, ref SimStats stats, out float dirX, out float dirY)
    {
        if (c.ReacquireIn > 0)
        {
            c.ReacquireIn--;
        }
        else
        {
            c.ReacquireIn = (short)_cfg.TargetReacquireTicks;

            // Defending a station under active attack outranks everything, including the pickup: a buff lasts
            // thirty seconds, a station is the thing that produces ships at all. Gated on a RECENT hit, or
            // fighters would garrison permanently and never leave home.
            if (TryFindThreatenedStation(px, py, c.Faction, out var dsx, out var dsy))
            {
                stats.AcquireQueries++;
                var sd2 = (dsx - px) * (dsx - px) + (dsy - py) * (dsy - py);
                if (TryAcquireTarget(ref c, px, py, defending: true, out var eex, out var eey)
                    && (eex - px) * (eex - px) + (eey - py) * (eey - py) < sd2)
                {
                    c.TargetX = eex;
                    c.TargetY = eey;
                    c.HasTarget = 1;
                }
                else
                {
                    c.TargetX = dsx;
                    c.TargetY = dsy;
                    c.HasTarget = 0;   // steer home, but do not shoot our own station
                }
                dirX = c.TargetX - px;
                dirY = c.TargetY - py;
                return;
            }

            // ── The engage-or-race decision ──────────────────────────────────────────────────────────────────────
            //
            // A pickup within reach outranks everything, including being under fire: winning one takes 200 hits,
            // so it is worth far more than any single skirmish. But a fighter parked next to the objective with an
            // enemy on top of it should shoot the enemy — every shot an opponent does not fire is a point they do
            // not score, so denial and racing are the same currency.
            //
            // The rule is parameter-free: shoot whichever is CLOSER, the nearest enemy or the pickup. That is
            // enough to produce both behaviours without scripting either. On the fringe of the crowd the pickup is
            // nearer and you race; inside the crowd an enemy is nearer and you fight; and it self-balances,
            // because committing shots to enemies is committing them away from your own tally.
            if (TryFindPickup(px, py, _cfg.PickupAttractRadius, out var lx, out var ly))
            {
                stats.AcquireQueries++;
                var pd2 = (lx - px) * (lx - px) + (ly - py) * (ly - py);
                if (TryAcquireTarget(ref c, px, py, defending: true, out var ex, out var ey))
                {
                    var ed2 = (ex - px) * (ex - px) + (ey - py) * (ey - py);
                    if (ed2 < pd2)
                    {
                        c.TargetX = ex;
                        c.TargetY = ey;
                        c.HasTarget = 1;
                        dirX = c.TargetX - px;
                        dirY = c.TargetY - py;
                        return;
                    }
                }
                c.TargetX = lx;
                c.TargetY = ly;
                c.HasTarget = 1;
                dirX = c.TargetX - px;
                dirY = c.TargetY - py;
                return;
            }

            if (TryAcquireTarget(ref c, px, py, c.ThreatTicks > 0, out var tx2, out var ty2))
            {
                c.TargetX = tx2;
                c.TargetY = ty2;
                c.HasTarget = 1;
            }
            else
            {
                c.HasTarget = 0;
            }
            stats.AcquireQueries++;
        }

        if (c.HasTarget != 0)
        {
            dirX = c.TargetX - px;
            dirY = c.TargetY - py;
            return;
        }

        var f = c.Faction & 3;

        // Wrap on the number of factions in PLAY, not on the size of the arrays.
        //
        // This was `(f + 1) & 3`, which wraps at four because the per-faction arrays are four wide. With the
        // default two factions that made faction 1's enemy faction 2 — which does not exist. Every fallback then
        // failed in a way that looked deliberate: NearestStation found no faction-2 base, _hasMiners[2] was false,
        // and ComputeCentroids seeds an empty faction's centroid with the MAP CENTRE. So faction 1's fighters with
        // no target rallied to the middle of the map and milled about there, permanently, while faction 0's worked
        // perfectly. One-sided symptoms point at asymmetric arithmetic, and this is the only arithmetic in the
        // simulation that is not symmetric in the faction index.
        var enemy = (f + 1) % Math.Max(1, _cfg.Factions);

        // Rally targets are LOCAL — the nearest station, never a global centre of mass.
        //
        // This is the rule that decides the shape of the whole war, and getting it wrong makes the station layout
        // decorative. An average position is a single point, so "head for the enemy's centre of mass" sends every
        // fighter on the map to the same place; with the factions interleaved that place is roughly the middle of
        // the map, which is precisely the one global scrum the lattice exists to break up. Nearest-station rallying
        // gives each fighter a different destination depending on where it already is, so the map supports as many
        // simultaneous fronts as it has adjacent enemy pairs.
        //
        // An earlier version rallied to the friendly miner centroid past EscortRadius and to the enemy centroid
        // inside it. At 100 km that oscillated: approach, flip, drift out, flip back — an orbit that never crossed
        // the map. Fourteen thousand ticks produced 10,500 ore mined and ZERO shots fired.
        Vector2 rally;
        if (c.ThreatTicks > 0)
        {
            // Under fire: fall back to the nearest friendly base and defend the economy around it. No target is
            // set — steer home, but never shoot our own station.
            rally = NearestStation(px, py, f) ?? _minerCentroid[f];
        }
        else
        {
            // Hunting. The objective is the enemy economy, and the nearest enemy base is where a local one lives.
            //
            // The station is set as a real TARGET, not merely a heading. Rallying without one meant fighters flew
            // to an enemy base, arrived, and sat there indefinitely: firing requires HasTarget, so they never shot
            // it. Every point of damage a station had ever taken was stray fire aimed at passing ships, which is
            // why bases only fell by accident and why a swarm could park on one for a minute doing nothing.
            var st = NearestStation(px, py, enemy);
            if (st.HasValue)
            {
                rally = st.Value;
                c.TargetX = rally.X;
                c.TargetY = rally.Y;
                c.HasTarget = 1;
            }
            else
            {
                rally = _hasMiners[enemy] ? _minerCentroid[enemy] : _centroid[enemy];
            }
        }
        dirX = rally.X - px;
        dirY = rally.Y - py;
    }

    /// <summary>
    /// Rewrites a fighter's steering so it holds <see cref="Config.StandoffRange"/> from its target and circles,
    /// instead of flying into it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applied once, at the call site, rather than inside <c>FighterSteer</c> — that method has four branches and
    /// four early returns, and threading this through each is exactly how one gets missed. It is called only on
    /// the fighter path, so miners are untouched and still close to <see cref="Config.MineDockRange"/> to dock.
    /// </para>
    /// <para>
    /// <b>Three radial states, not two.</b> Beyond the outer edge, close. Inside the inner edge, back off. Between
    /// them, neither — orbit. The middle state is the whole design: with a single threshold the ship reverses every
    /// time it crosses, and the resulting chatter is invisible at a glance but wrecks the motion.
    /// </para>
    /// <para>
    /// The tangential term is present even while closing (weighted by <see cref="Config.OrbitStrength"/>), so a
    /// fighter spirals in rather than charging down the radius and overshooting through the band in one step.
    /// </para>
    /// </remarks>
    private void ApplyStandoff(ref Combat c, float px, float py, ref float dirX, ref float dirY)
    {
        if (c.HasTarget == 0)
        {
            return;
        }
        var dx = c.TargetX - px;
        var dy = c.TargetY - py;
        var d2 = dx * dx + dy * dy;
        if (d2 < 1e-6f || !float.IsFinite(d2))
        {
            return;
        }
        var d = MathF.Sqrt(d2);

        // Sample only fighters actually IN an engagement. Averaging over everything with a target includes ships
        // still crossing 20 km to reach one, which swamped the figure at 10 km and said nothing about how close
        // combat happens. Twice weapon range is the envelope where the stand-off rule is the thing in control.
        //
        // Sampled BEFORE the disabled-check, so the baseline is measurable too. An instrument that only runs when
        // the feature is on can report the "after" and never the "before".
        if (d <= _cfg.WeaponRange * 2f)
        {
            StandoffDistanceSum += d;
            StandoffSamples++;
        }

        if (_cfg.StandoffRange <= 0f)
        {
            return;
        }

        var half = MathF.Max(0f, _cfg.StandoffBand) * 0.5f;
        var outer = _cfg.StandoffRange + half;
        var inner = MathF.Max(1f, _cfg.StandoffRange - half);

        // 0 = hold, 1 = approach, 2 = retreat. Packed into bits 1-2 of SteerFlags.
        var radial = d > outer ? 1 : d < inner ? 2 : 0;
        var previous = (c.SteerFlags >> 1) & 0x3;
        if (radial != 0 && previous != 0 && radial != previous)
        {
            StandoffFlips++;
        }
        if (radial == 0)
        {
            StandoffOrbiting++;
        }
        c.SteerFlags = (byte)((c.SteerFlags & ~0x6) | (radial << 1));

        // Unit radial (toward the target) and unit tangential, sign fixed per ship so it keeps circling one way.
        var rx = dx / d;
        var ry = dy / d;
        var tx = -ry;
        var ty = rx;
        if ((c.SteerFlags & 1) != 0)
        {
            tx = -tx;
            ty = -ty;
        }

        var radialWeight = radial == 0 ? 0f : radial == 1 ? 1f : -1f;
        var tangentWeight = radial == 0 ? 1f : _cfg.OrbitStrength;

        dirX = rx * radialWeight + tx * tangentWeight;
        dirY = ry * radialWeight + ty * tangentWeight;
    }

    /// <summary>
    /// Adds the stored push-away-from-neighbours term to a fighter's steering.
    /// </summary>
    /// <remarks>
    /// Applied every tick from the value cached at the last re-acquisition, rather than only on the tick the walk
    /// happens. Applying it one tick in eight at eight times the strength would average out the same and look like
    /// a stutter.
    /// </remarks>
    private void ApplySeparation(in Combat c, ref float dirX, ref float dirY)
    {
        if (_cfg.SeparationRadius <= 0f || (c.SepX == 0 && c.SepY == 0))
        {
            return;
        }
        dirX += c.SepX / 1000f * _cfg.SeparationStrength;
        dirY += c.SepY / 1000f * _cfg.SeparationStrength;
    }

    /// <summary>
    /// Miner behaviour: find ore, park on it, fill the hold, carry it home. Mode 0 seek, 1 mine, 2 return.
    /// </summary>
    private void MinerSteer(ref Combat c, ref Miner mi, float px, float py, float mineRange2, float dropRange2,
                            ref SimStats stats, out float dirX, out float dirY)
    {
        // Census FIRST, before any of the early returns below.
        //
        // It was originally taken further down and reported "returning 0" on a run that was demonstrably
        // delivering ore — because the mode-2 branch returns before ever reaching it. A census placed after an
        // early return counts only the states that fall through, which is the one thing a census must not do.
        MinerModeCount[Math.Clamp((int)mi.Mode, 0, 2)]++;
        if (mi.HasOre == 0)
        {
            MinerModeCount[3]++;
        }
        if (mi.Cargo > 0)
        {
            LadenMiners++;
            _cargoSum += mi.Cargo;
        }

        if (mi.Mode == 2)
        {
            var hdx = mi.HomeX - px;
            var hdy = mi.HomeY - py;
            var home2 = hdx * hdx + hdy * hdy;

            // The drop test is geometric, so on its own it cannot tell a station from the crater where one used to
            // be — an eliminated faction went on banking ore into thin air and funding fresh miners with it, which
            // is the economy continuing to run after the thing that ran it was destroyed. Cheap to close: the
            // lookup only happens for a miner already standing on its drop point, a handful per tick, not for every
            // miner in flight. No-op when StationsDestructible is off, since nothing is ever marked dead.
            if (home2 <= dropRange2 && IsLiveStationAt(mi.HomeX, mi.HomeY))
            {
                // Sampled on the delivery itself, not inside a branch gated on the feature being on. The mining
                // distance metric and the nearest-neighbour metric were both written the gated way and both then
                // reported a baseline of zero, which is worse than no metric at all.
                var drop = MathF.Sqrt(home2);
                DropDistanceSum += drop;
                DropDistanceCount++;
                if (drop > DropDistanceMax)
                {
                    DropDistanceMax = drop;
                }
                Material[c.Faction & 3] += mi.Cargo;
                TotalMined += mi.Cargo;
                stats.Delivered += mi.Cargo;
                mi.Cargo = 0;
                mi.Mode = 0;
                mi.HasOre = 0;
                mi.OreKey = 0;
            }
            dirX = hdx;
            dirY = hdy;
            return;
        }

        if (mi.HasOre == 0)
        {
            if (mi.SearchCooldown > 0)
            {
                mi.SearchCooldown--;
                dirX = _centroid[c.Faction & 3].X - px;
                dirY = _centroid[c.Faction & 3].Y - py;
                return;
            }
            mi.SearchCooldown = (short)_cfg.TargetReacquireTicks;
            if (TryFindOre(px, py, out var ox, out var oy, out var key))
            {
                mi.OreX = ox;
                mi.OreY = oy;
                mi.OreKey = key;
                mi.HasOre = 1;
                mi.Mode = 0;
            }
            stats.OreQueries++;
        }

        // Refresh the target's position before using it.
        //
        // OreX/OreY used to be written once, at acquisition, and never again — so a miner flew to where the rock
        // HAD been, parked there, and kept extracting. Asteroids drift at 30 m/s and a 20 km approach takes ~63 s,
        // so the remembered point could be ~1.9 km stale against a 1 km mine range: miners visibly mined from far
        // outside the rock. The extraction is keyed by chunk+slot, so nothing ever rechecked the distance.
        //
        // A missing key means the rock was depleted or destroyed while we were en route, which is also the moment
        // to let go of it rather than mine a hole in space until the hold fills.
        var oreChunk = -1;
        var oreSlot = 0;
        if (mi.HasOre != 0)
        {
            if (_orePos.TryGetValue(mi.OreKey, out var live))
            {
                mi.OreX = live.Pos.X;
                mi.OreY = live.Pos.Y;
                oreChunk = live.Chunk;
                oreSlot = live.Slot;
            }
            else
            {
                OreRetargets++;
                mi.HasOre = 0;
                mi.OreKey = 0;
                mi.Mode = 0;
                dirX = 0;
                dirY = 0;
                return;
            }
        }

        var dx = mi.OreX - px;
        var dy = mi.OreY - py;
        var d2ore = dx * dx + dy * dy;

        if (mi.HasOre != 0 && d2ore <= mineRange2)
        {
            MiningDistanceSum += MathF.Sqrt(d2ore);
            MiningDistanceCount++;
            MiningDistanceMax = MathF.Max(MiningDistanceMax, MathF.Sqrt(d2ore));

            // Extract from mining range, but only PARK once docked. Mode 1 kills thrust, so setting it at the
            // outer edge of the range stops the miner exactly where the asteroid's drift can push the rock back
            // out from under it — an oscillation that never converges. Keep closing until docked.
            var dock = _cfg.MineDockRange * _cfg.MineDockRange;
            mi.Mode = d2ore <= dock ? (byte)1 : (byte)0;
            // The mining effect multiplies both extraction rate AND hold size, applied at use rather than baked
            // into Miner.CargoMax — a miner already carrying an over-full boosted load when the effect lapses just
            // delivers it, instead of being stuck above a cap it can no longer reach.
            var mul = MiningMul(c.Faction);
            var holdMax = (short)Math.Clamp((int)(mi.CargoMax * mul), 1, short.MaxValue);
            var take = (short)Math.Min((int)(_cfg.MineRate * mul), holdMax - mi.Cargo);
            if (take > 0)
            {
                var key = ((long)oreChunk << 8) | (uint)(oreSlot & 0xFF);
                _mined.TryGetValue(key, out var acc);
                _mined[key] = acc + take;
                mi.Cargo += take;
            }
            if (mi.Cargo >= holdMax)
            {
                mi.Mode = 2;
            }
        }
        else if (mi.HasOre != 0)
        {
            mi.Mode = 0;
        }

        dirX = dx;
        dirY = dy;
    }

    /// <summary>
    /// Nearest enemy within <see cref="Config.AcquireRadius"/>, via the engine's cluster spatial query.
    /// </summary>
    /// <summary>
    /// Fighter target selection. A fighter's job is to kill the enemy ECONOMY, so enemy miners are preferred over
    /// enemy fighters — unless it is itself under attack, in which case it engages whatever is nearest. That single
    /// switch produces the behaviour you want: raids push through toward the miners, but a fighter being shot at
    /// turns and fights instead of ignoring its attacker.
    /// </summary>
    private bool TryAcquireTarget(ref Combat c, float x, float y, bool defending, out float tx, out float ty)
    {
        var faction = c.Faction;
        tx = 0;
        ty = 0;
        var best = float.MaxValue;
        var bestMiner = float.MaxValue;
        float minerX = 0, minerY = 0;
        var found = false;
        var foundMiner = false;

        _sepX = 0f;
        _sepY = 0f;
        _sepNearest = float.MaxValue;

        var sphere = new BSphere2F { CenterX = x, CenterY = y, Radius = _cfg.AcquireRadius };
        using var tr = _host.DBE.CreateQuickTransaction();
        var q = _host.DBE.ClusterSpatialQuery<Ship>().Radius(in sphere);
        using var acc = tr.For<Ship>();

        var examined = 0;
        var sepR = _cfg.SeparationRadius;
        var sepR2 = sepR * sepR;
        while (q.MoveNext())
        {
            if (++examined > _cfg.AcquireScanCap)
            {
                break;
            }
            var hit = q.Current;

            // ── separation, accumulated on the walk we are already doing ──
            //
            // Every ship in range, friend or foe: the pile-up is both factions mixed, and a rule that only pushed
            // away from enemies would leave each side free to stack on itself. Linear falloff, so the push is
            // bounded as the distance goes to zero — there is no collision to stop two coincident ships, and an
            // inverse-square law would fling them apart at absurd speed.
            if (sepR > 0f && hit.DistanceSq < sepR2 && hit.DistanceSq > 1e-6f)
            {
                var nd = MathF.Sqrt(hit.DistanceSq);
                var w = 1f - nd / sepR;
                _sepX += (x - hit.MinX) / nd * w;
                _sepY += (y - hit.MinY) / nd * w;
                if (nd < _sepNearest)
                {
                    _sepNearest = nd;
                }
            }

            // The hit carries bounds; faction and kind still need a component read, resolved through the cluster.
            if (!TryReadShip(acc, hit.ClusterChunkId, hit.SlotIndex, out var hf, out var hk))
            {
                continue;
            }
            if (hf == faction)
            {
                continue;
            }
            if (hit.DistanceSq < best)
            {
                best = hit.DistanceSq;
                tx = hit.MinX;
                ty = hit.MinY;
                found = true;
            }
            if (hk == KindMiner && hit.DistanceSq < bestMiner)
            {
                bestMiner = hit.DistanceSq;
                minerX = hit.MinX;
                minerY = hit.MinY;
                foundMiner = true;
            }
        }
        q.Dispose();

        // Publish the separation gathered on this walk. Stored as fixed-point so it survives until the next
        // re-acquisition and can be applied on every tick in between, rather than as an 8x impulse once.
        var sl = MathF.Sqrt(_sepX * _sepX + _sepY * _sepY);
        if (sl > 1e-4f)
        {
            c.SepX = (short)Math.Clamp((int)(_sepX / sl * 1000f), -1000, 1000);
            c.SepY = (short)Math.Clamp((int)(_sepY / sl * 1000f), -1000, 1000);
        }
        else
        {
            c.SepX = 0;
            c.SepY = 0;
        }
        if (_sepNearest < float.MaxValue)
        {
            NearestNeighbourSum += _sepNearest;
            NearestNeighbourCount++;
        }

        if (!defending && foundMiner)
        {
            tx = minerX;
            ty = minerY;
            return true;
        }
        return found;
    }

    /// <summary>
    /// Reads one entity's faction straight out of its cluster page, without an EntityMap lookup.
    /// </summary>
    /// <remarks>
    /// <c>ArchetypeAccessor</c> has no "get cluster by chunk id", but the tier-partition overload
    /// <c>GetClusterEnumerator(int[] clusterIds, start, end)</c> accepts an explicit id list — so a one-element list
    /// is a public, allocation-free way to address a single cluster. The alternative, <c>acc.Open(entityId)</c>,
    /// costs a hash probe into the paged EntityMap per hit; at thousands of hits per tick that dominates the frame.
    /// </remarks>
    private bool TryReadFaction(ArchetypeAccessor<Ship> acc, int chunkId, int slot, out byte faction) =>
        TryReadShip(acc, chunkId, slot, out faction, out _);

    /// <summary>As <see cref="TryReadFaction"/>, but also yields the ship kind — fighters need it to prefer miners.</summary>
    private bool TryReadShip(ArchetypeAccessor<Ship> acc, int chunkId, int slot, out byte faction, out byte kind)
    {
        faction = 0;
        kind = 0;
        if (chunkId != _facCacheChunk)
        {
            _facCacheChunk = -1;
            _oneCluster[0] = chunkId;
            using var e = acc.GetClusterEnumerator(_oneCluster, 0, 1);
            if (!e.MoveNext())
            {
                return false;
            }
            var cluster = e.Current;
            var com = cluster.GetReadOnlySpan(Ship.Combat);
            var n = Math.Min(com.Length, _facCache.Length);
            for (var k = 0; k < n; k++)
            {
                _facCache[k] = com[k].Faction;
                _kindCache[k] = com[k].Kind;
            }
            _facCacheOccupancy = cluster.OccupancyBits;
            _facCacheChunk = chunkId;
        }
        if ((uint)slot >= 64u || (_facCacheOccupancy & (1UL << slot)) == 0)
        {
            return false;
        }
        faction = _facCache[slot];
        kind = _kindCache[slot];
        return true;
    }

    /// <summary>Invalidated whenever ship Combat data may have changed under us.</summary>
    private void InvalidateFactionCache() => _facCacheChunk = -1;

    private void ShotTick(float dt, ref SimStats stats)
    {
        var world = _cfg.WorldSize;
        using var tx = _host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Shot>();
        using var e = acc.GetClusterEnumerator();

        foreach (var cluster in e)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var pos = cluster.GetReadOnlySpan(Shot.Position);
            var bul = cluster.GetSpan(Shot.Bullet);

            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref var b = ref bul[i];
                if (b.Dead != 0)
                {
                    continue;
                }
                if (--b.Life <= 0)
                {
                    b.Dead = 1;
                    _dead.Add(cluster.GetEntityId(i));
                    continue;
                }

                var nx = pos[i].Bounds.MinX + b.VX * dt;
                var ny = pos[i].Bounds.MinY + b.VY * dt;
                if (Bad("shot.move", nx, ny, b.VX, b.VY, pos[i].Bounds.MinX, pos[i].Bounds.MinY))
                {
                    b.Dead = 1;
                    _dead.Add(cluster.GetEntityId(i));
                    continue;
                }
                if (nx < 0 || ny < 0 || nx > world || ny > world)
                {
                    b.Dead = 1;
                    _dead.Add(cluster.GetEntityId(i));
                    continue;
                }
                cluster.WriteSpatial(Shot.Position, i, Pos.At(nx, ny));
                stats.ShotsMoved++;

                // A projectile crossing a pickup collects it for its faction.
                if (TryCollectWithShot(nx, ny, b.Faction, ref stats))
                {
                    b.Dead = 1;
                    _dead.Add(cluster.GetEntityId(i));
                    continue;
                }

                // Stations first, and by LINEAR SCAN over six cached positions — see the note in Config. Six
                // distance comparisons, against the ~1000 entity examinations a spatial query would cost. Doing
                // this the "consistent" way would have doubled the hottest path in the simulation for six entities.
                if (TryHitStation(nx, ny, b.Faction, b.Damage))
                {
                    b.Dead = 1;
                    _dead.Add(cluster.GetEntityId(i));
                    stats.Hits++;
                    continue;
                }

                // ── hit test: a TINY radius query inside a HUGE cell — the pathological shape ──
                if (TryHit(nx, ny, b.Faction, b.Damage))
                {
                    b.Dead = 1;
                    _dead.Add(cluster.GetEntityId(i));
                    stats.Hits++;
                }
                stats.HitQueries++;
            }
            cluster.MarkDirty(Shot.Bullet);
        }
        tx.Commit();
    }

    private bool TryHit(float x, float y, byte faction, int damage)
    {
        var sphere = new BSphere2F { CenterX = x, CenterY = y, Radius = _cfg.ShotHitRadius };
        using var tr = _host.DBE.CreateQuickTransaction();
        using var acc = tr.For<Ship>();
        var q = _host.DBE.ClusterSpatialQuery<Ship>().Radius(in sphere);
        try
        {
            while (q.MoveNext())
            {
                var hit = q.Current;
                if (!TryReadFaction(acc, hit.ClusterChunkId, hit.SlotIndex, out var hf) || hf == faction)
                {
                    continue;
                }
                // Shielded factions absorb the round: the shot dies, no damage is dealt.
                if (ShieldTicks[hf & 3] > 0)
                {
                    ShotsAbsorbed++;
                    TotalShotHits++;
                    return true;
                }
                var key = ((long)hit.ClusterChunkId << 8) | (uint)hit.SlotIndex;
                _damage.TryGetValue(key, out var acc2);
                _damage[key] = acc2 + damage;
                TotalShotHits++;
                return true;
            }
        }
        finally
        {
            q.Dispose();
        }
        return false;
    }

    /// <summary>
    /// Applies the tick's accumulated damage. Walks the ship clusters once and consults the damage map, rather than
    /// addressing each damaged entity individually — one pass over hot cluster pages beats N random lookups.
    /// </summary>
    private void ApplyDamage(ref SimStats stats)
    {
        if (_damage.Count == 0)
        {
            return;
        }
        using var tx = _host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Ship>();
        using var e = acc.GetClusterEnumerator();
        foreach (var cluster in e)
        {
            var chunkId = cluster.ChunkId;
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var com = cluster.GetSpan(Ship.Combat);
            var touched = false;
            while (bits != 0)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var key = ((long)chunkId << 8) | (uint)slot;
                if (!_damage.TryGetValue(key, out var dmg))
                {
                    continue;
                }
                ref var c = ref com[slot];
                if (c.Dead != 0)
                {
                    continue;
                }
                // Shield first, hull with the remainder. A round that only grazes the shield still resets the calm
                // timer, so sustained light fire suppresses regeneration even when it is not killing anything.
                var toShield = Math.Min(dmg, (int)c.Shield);
                c.Shield -= (short)toShield;
                var toHull = dmg - toShield;
                if (toHull > 0)
                {
                    c.Hp -= (short)toHull;
                }
                c.CalmTicks = 0;
                c.HitFlash = (byte)Math.Min(255, _cfg.HitFlashTicks);
                if (c.ThreatTicks == 0)
                {
                    // Newly under attack: drop whatever it was doing and re-target on the next tick.
                    c.ReacquireIn = 0;
                }
                c.ThreatTicks = (byte)Math.Min(255, _cfg.ThreatMemoryTicks);
                touched = true;
                if (c.Hp <= 0)
                {
                    c.Dead = 1;
                    _dead.Add(cluster.GetEntityId(slot));
                    ShipsAlive[c.Faction & 3]--;
                    if (c.Kind == KindMiner)
                    {
                        MinersAlive[c.Faction & 3]--;
                    }
                    TotalKilled++;
                    stats.Killed++;
                }
            }
            if (touched)
            {
                cluster.MarkDirty(Ship.Combat);
            }
        }
        tx.Commit();
    }

    private void FlushSpawns(ref SimStats stats)
    {
        if (_pendingShots.Count == 0)
        {
            return;
        }
        using var tx = _host.DBE.CreateQuickTransaction();
        foreach (var s in _pendingShots)
        {
            if (ShotsAlive >= _cfg.MaxShots)
            {
                break;
            }
            var pos = Pos.At(s.X, s.Y);
            var b = new Bullet
            {
                VX = s.VX, VY = s.VY,
                Life = (short)_cfg.ShotLifeTicks,
                Faction = s.Faction,
                Damage = s.Damage,
                Boosted = (byte)(s.Boosted ? 1 : 0),
            };
            tx.Spawn<Shot>(Shot.Position.Set(in pos), Shot.Bullet.Set(in b));
            ShotsAlive++;
            stats.ShotsFired++;
            TotalShotsFired++;
        }
        tx.Commit();
    }

    private void Reap(ref SimStats stats)
    {
        if (_dead.Count == 0)
        {
            return;
        }
        using var tx = _host.DBE.CreateQuickTransaction();
        foreach (var id in _dead)
        {
            if (id.ArchetypeId == _host.ShotArchetypeId)
            {
                ShotsAlive--;
            }
            tx.Destroy(id);
            stats.Destroyed++;
        }
        tx.Commit();
    }

    /// <summary>
    /// Asteroids drift slowly and respawn on a slow timer. Slow is not static: their clusters still churn and
    /// migrate occasionally, which is exactly the low-rate traffic worth being able to watch in isolation.
    /// </summary>
    private void AsteroidTick(float dt, ref SimStats stats)
    {
        var world = _cfg.WorldSize;
        // Rebuilt every tick from the authoritative positions. There are eight asteroids, so this is free, and it
        // is what lets a miner mine the ROCK rather than the place the rock used to be.
        _orePos.Clear();
        using (var tx = _host.DBE.CreateQuickTransaction())
        {
            using var acc = tx.For<Rock>();
            using var e = acc.GetClusterEnumerator();
            foreach (var cluster in e)
            {
                var bits = cluster.OccupancyBits;
                if (bits == 0)
                {
                    continue;
                }
                var pos = cluster.GetReadOnlySpan(Rock.Position);
                var ast = cluster.GetSpan(Rock.Asteroid);
                while (bits != 0)
                {
                    var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    ref var a = ref ast[i];
                    if (a.Dead != 0)
                    {
                        continue;
                    }
                    if (a.Capacity <= 0)
                    {
                        a.Dead = 1;
                        _dead.Add(cluster.GetEntityId(i));
                        AsteroidsAlive--;
                        stats.AsteroidsDepleted++;
                        continue;
                    }
                    var nx = pos[i].Bounds.MinX + a.VX * dt;
                    var ny = pos[i].Bounds.MinY + a.VY * dt;
                    if (nx < 0f || nx > world)
                    {
                        a.VX = -a.VX;
                        nx = Clamp(nx, 0, world);
                    }
                    if (ny < 0f || ny > world)
                    {
                        a.VY = -a.VY;
                        ny = Clamp(ny, 0, world);
                    }
                    cluster.WriteSpatial(Rock.Position, i, Pos.At(nx, ny));
                    _orePos[cluster.GetEntityId(i).EntityKey] = (new Vector2(nx, ny), cluster.ChunkId, i);
                }
                cluster.MarkDirty(Rock.Asteroid);
            }
            tx.Commit();
        }

        if (_host.Tick >= _nextRockSpawnTick && AsteroidsAlive < _cfg.AsteroidCount)
        {
            _nextRockSpawnTick = _host.Tick + _cfg.AsteroidRespawnTicks;
            SpawnAsteroids(Math.Min(2, _cfg.AsteroidCount - AsteroidsAlive));
            stats.AsteroidsSpawned++;
        }
    }

    /// <summary>
    /// Spawns pickups on a jittered timer, ages the live ones, and counts down each faction's active effects.
    /// </summary>
    private void PickupTick(ref SimStats stats)
    {
        TicksElapsed++;
        for (var f = 0; f < 4; f++)
        {
            if (PowerTicks[f] > 0 || ShieldTicks[f] > 0 || SpeedTicks[f] > 0 || MiningTicks[f] > 0)
            {
                EffectTicks[f]++;
            }
            if (PowerTicks[f] > 0)
            {
                PowerTicks[f]--;
            }
            if (ShieldTicks[f] > 0)
            {
                ShieldTicks[f]--;
            }
            if (SpeedTicks[f] > 0)
            {
                SpeedTicks[f]--;
            }
            if (MiningTicks[f] > 0)
            {
                MiningTicks[f]--;
            }
        }

        if (!_cfg.PickupsEnabled)
        {
            return;
        }

        LivePickupKind = -1;
        Array.Clear(PickupProgress);

        // Decay is applied on a shared tick phase rather than per-hit, so a tally measures SUSTAINED pressure.
        var decay = _cfg.PickupProgressDecayTicks > 0 && _host.Tick % _cfg.PickupProgressDecayTicks == 0;

        using (var tx = _host.DBE.CreateQuickTransaction())
        {
            using var acc = tx.For<Loot>();
            using var e = acc.GetClusterEnumerator();
            foreach (var cluster in e)
            {
                var bits = cluster.OccupancyBits;
                if (bits == 0)
                {
                    continue;
                }
                var inf = cluster.GetSpan(Loot.Info);
                var pos = cluster.GetReadOnlySpan(Loot.Position);
                var touched = false;
                while (bits != 0)
                {
                    var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    ref var pi = ref inf[i];
                    if (pi.Dead != 0)
                    {
                        continue;
                    }
                    touched = true;
                    if (decay)
                    {
                        for (var f = 0; f < _cfg.Factions; f++)
                        {
                            pi.AddProgress(f, -1);
                        }
                    }

                    // Mirror the live pickup's state out for the HUD and the renderer's marker. One alive at a
                    // time by design, so the last one seen is the one.
                    LivePickupKind = pi.Kind;
                    LivePickupPos = new Vector2(pos[i].Bounds.MinX, pos[i].Bounds.MinY);
                    for (var f = 0; f < 4; f++)
                    {
                        PickupProgress[f] = pi.Progress(f);
                    }

                    if (--pi.Life <= 0)
                    {
                        pi.Dead = 1;
                        _dead.Add(cluster.GetEntityId(i));
                        PickupsAlive--;
                        LivePickupKind = -1;
                        stats.PickupsExpired++;
                    }
                }
                if (touched)
                {
                    cluster.MarkDirty(Loot.Info);
                }
            }
            tx.Commit();
        }

        if (_host.Tick >= _nextPickupTick && PickupsAlive < _cfg.MaxPickupsAlive)
        {
            ScheduleNextPickup();
            SpawnPickup(ref stats);
        }
    }

    private void ScheduleNextPickup()
    {
        var jitter = 1f + ((float)_rng.NextDouble() * 2f - 1f) * _cfg.PickupSpawnJitter;
        _nextPickupTick = _host.Tick + Math.Max(30, (int)(_cfg.PickupSpawnIntervalTicks * jitter));
    }

    /// <summary>
    /// Spawns one pickup at a contested ore anchor, so the race happens where a fight already is.
    /// </summary>
    /// <remarks>
    /// Placing it at the map centre would pull the nearest bases toward the middle every time and slowly undo the
    /// interleaved layout. An ore anchor is by construction equidistant between two enemy stations, which is
    /// exactly the ground both sides already have a reason to hold.
    /// </remarks>
    private void SpawnPickup(ref SimStats stats)
    {
        float x, y;
        if (_oreAnchors.Count > 0)
        {
            var anchor = _oreAnchors[_rng.Next(_oreAnchors.Count)];
            var j = _cfg.WorldSize * 0.02f;
            x = Clamp(anchor.X + (float)(_rng.NextDouble() - 0.5) * j, 0, _cfg.WorldSize);
            y = Clamp(anchor.Y + (float)(_rng.NextDouble() - 0.5) * j, 0, _cfg.WorldSize);
        }
        else
        {
            var r = _cfg.WorldSize * _cfg.PickupSpawnRadiusPct * MathF.Sqrt((float)_rng.NextDouble());
            var a = (float)(_rng.NextDouble() * Math.PI * 2);
            x = Clamp(_cfg.WorldSize * 0.5f + MathF.Cos(a) * r, 0, _cfg.WorldSize);
            y = Clamp(_cfg.WorldSize * 0.5f + MathF.Sin(a) * r, 0, _cfg.WorldSize);
        }

        var pos = Pos.At(x, y);
        var info = new PickupInfo
        {
            Life = (short)Math.Min(short.MaxValue, _cfg.PickupLifeTicks),
            Kind = (byte)_rng.Next(PickupKindCount),
        };
        using var tx = _host.DBE.CreateQuickTransaction();
        tx.Spawn<Loot>(Loot.Position.Set(in pos), Loot.Info.Set(in info));
        tx.Commit();
        PickupsAlive++;
        stats.PickupsSpawned++;
    }

    /// <summary>Awards a won pickup to a faction. Effects are faction-wide and refresh rather than stack.</summary>
    private void CollectPickup(byte kind, byte faction, ref SimStats stats)
    {
        var f = faction & 3;
        switch (kind)
        {
            case PickupPower: PowerTicks[f] = _cfg.PickupPowerDurationTicks; break;
            case PickupShield: ShieldTicks[f] = _cfg.PickupShieldDurationTicks; break;
            case PickupSpeed: SpeedTicks[f] = _cfg.PickupSpeedDurationTicks; break;
            default: MiningTicks[f] = _cfg.PickupMiningDurationTicks; break;
        }
        PickupsCollected++;
        stats.PickupsCollected++;
    }

    /// <summary>Speed multiplier currently applying to a faction's ships.</summary>
    private float SpeedMul(int faction) => SpeedTicks[faction & 3] > 0 ? _cfg.SpeedBoostMultiplier : 1f;

    /// <summary>Mining multiplier (rate and cargo capacity) currently applying to a faction's miners.</summary>
    private float MiningMul(int faction) => MiningTicks[faction & 3] > 0 ? _cfg.MiningBoostMultiplier : 1f;

    /// <summary>Nearest uncollected pickup, if any. Skipped entirely when none are live.</summary>
    private bool TryFindPickup(float x, float y, float radius, out float px, out float py)
    {
        px = 0;
        py = 0;
        if (PickupsAlive <= 0)
        {
            return false;
        }
        var best = float.MaxValue;
        var found = false;
        var sphere = new BSphere2F { CenterX = x, CenterY = y, Radius = radius };
        using var tr = _host.DBE.CreateQuickTransaction();
        var q = _host.DBE.ClusterSpatialQuery<Loot>().Radius(in sphere);
        try
        {
            while (q.MoveNext())
            {
                var hit = q.Current;
                if (hit.DistanceSq < best)
                {
                    best = hit.DistanceSq;
                    px = hit.MinX;
                    py = hit.MinY;
                    found = true;
                }
            }
        }
        finally
        {
            q.Dispose();
        }
        return found;
    }

    /// <summary>
    /// A projectile crossing a pickup adds one to the firing faction's tally. Returns true if the shot was consumed.
    /// </summary>
    /// <remarks>
    /// The pickup is won only when a tally reaches <see cref="Config.PickupHitsToWin"/>, so a single lucky shot no
    /// longer decides it. The shot is consumed either way — otherwise a projectile would pass through and go on to
    /// hit a ship behind, which would make firing at the pickup strictly better than firing at anything else.
    /// </remarks>
    private bool TryCollectWithShot(float x, float y, byte faction, ref SimStats stats)
    {
        if (PickupsAlive <= 0)
        {
            return false;
        }
        var sphere = new BSphere2F { CenterX = x, CenterY = y, Radius = _cfg.PickupRadius };
        using var tr = _host.DBE.CreateQuickTransaction();
        using var acc = tr.For<Loot>();
        var q = _host.DBE.ClusterSpatialQuery<Loot>().Radius(in sphere);
        var hitChunk = -1;
        var hitSlot = 0;
        try
        {
            if (q.MoveNext())
            {
                hitChunk = q.Current.ClusterChunkId;
                hitSlot = q.Current.SlotIndex;
            }
        }
        finally
        {
            q.Dispose();
        }
        if (hitChunk < 0)
        {
            return false;
        }

        _oneCluster[0] = hitChunk;
        using var e = acc.GetClusterEnumerator(_oneCluster, 0, 1);
        if (!e.MoveNext())
        {
            return false;
        }
        var cluster = e.Current;
        if ((cluster.OccupancyBits & (1UL << hitSlot)) == 0)
        {
            return false;
        }
        ref var pi = ref cluster.Get(Loot.Info, hitSlot);
        if (pi.Dead != 0)
        {
            return false;
        }
        var f = faction & 3;
        pi.AddProgress(f, 1);
        PickupHits++;
        cluster.MarkDirty(Loot.Info);

        if (pi.Progress(f) < _cfg.PickupHitsToWin)
        {
            return true;   // consumed, but the race continues
        }

        pi.Dead = 1;
        _dead.Add(cluster.GetEntityId(hitSlot));
        PickupsAlive--;
        LivePickupKind = -1;
        CollectPickup(pi.Kind, faction, ref stats);
        return true;
    }

    /// <summary>Applies the tick's mining in one pass over the asteroid clusters, mirroring <see cref="ApplyDamage"/>.</summary>
    private void ApplyMining(ref SimStats stats)
    {
        if (_mined.Count == 0)
        {
            return;
        }
        using var tx = _host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Rock>();
        using var e = acc.GetClusterEnumerator();
        foreach (var cluster in e)
        {
            var chunkId = cluster.ChunkId;
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var ast = cluster.GetSpan(Rock.Asteroid);
            var touched = false;
            while (bits != 0)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var key = ((long)chunkId << 8) | (uint)slot;
                if (!_mined.TryGetValue(key, out var amount))
                {
                    continue;
                }
                ref var a = ref ast[slot];
                if (a.Dead != 0)
                {
                    continue;
                }
                a.Capacity -= amount;
                stats.Mined += amount;
                touched = true;
            }
            if (touched)
            {
                cluster.MarkDirty(Rock.Asteroid);
            }
        }
        tx.Commit();
    }

    private List<Vector2> CollectLiveAsteroidPositions()
    {
        var list = new List<Vector2>();
        using var tx = _host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Rock>();
        using var e = acc.GetClusterEnumerator();
        foreach (var cluster in e)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var pos = cluster.GetReadOnlySpan(Rock.Position);
            var ast = cluster.GetReadOnlySpan(Rock.Asteroid);
            while (bits != 0)
            {
                var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (ast[i].Dead == 0)
                {
                    list.Add(new Vector2(pos[i].Bounds.MinX, pos[i].Bounds.MinY));
                }
            }
        }
        return list;
    }

    /// <summary>Nearest asteroid within <see cref="Config.OreSearchRadius"/>, via the cluster spatial query.</summary>
    /// <summary>
    /// Nearest live asteroid, by linear scan over the per-tick table rather than through the spatial index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same reasoning as station targeting: there are eight asteroids, their live positions are already
    /// gathered once per tick by <c>AsteroidTick</c>, and eight distance comparisons beat a radius query that
    /// examines far more. It also removed ~236 spatial queries per tick — one per searching miner.
    /// </para>
    /// <para>
    /// The decisive reason is correctness, though, not cost. Using the query meant the identity came back as
    /// <c>ClusterSpatialQueryResult.EntityId</c> — the full packed 64-bit value — while the table was keyed by
    /// <c>EntityId.EntityKey</c>, the 48-bit key with the archetype routing bits shifted off. Two representations
    /// of the same identity that silently never compare equal. Reading both sides from ONE source removes the
    /// entire class of mistake.
    /// </para>
    /// </remarks>
    private bool TryFindOre(float x, float y, out float ox, out float oy, out long key)
    {
        ox = 0;
        oy = 0;
        key = 0;
        var best = _cfg.OreSearchRadius * _cfg.OreSearchRadius;
        var found = false;

        foreach (var (k, v) in _orePos)
        {
            var dx = v.Pos.X - x;
            var dy = v.Pos.Y - y;
            var d2 = dx * dx + dy * dy;
            if (d2 < best)
            {
                best = d2;
                ox = v.Pos.X;
                oy = v.Pos.Y;
                key = k;
                found = true;
            }
        }
        return found;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

    private static readonly HashSet<string> s_reported = new();

    /// <summary>Reports the first non-finite value seen at each call site, once, with its inputs.</summary>
    private static bool Bad(string where, params float[] vals)
    {
        foreach (var v in vals)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                if (s_reported.Add(where))
                {
                    Console.Error.WriteLine($"[diag] non-finite at {where}: [{string.Join(", ", vals)}]");
                }
                return true;
            }
        }
        return false;
    }

    private struct PendingShot
    {
        public float X, Y, VX, VY;
        public byte Faction;
        public short Damage;
        public bool Boosted;
    }
}

internal struct SimStats
{
    public int ShipsMoved;
    public int ShotsMoved;
    public int ShotsFired;
    public int Hits;
    public int Killed;
    public int Spawned;
    public int Destroyed;
    public int AcquireQueries;
    public int HitQueries;
    public int OreQueries;
    public int Mined;
    public int Delivered;
    public int AsteroidsDepleted;
    public int AsteroidsSpawned;
    public int PickupsSpawned;
    public int PickupsCollected;
    public int PickupsExpired;
}
