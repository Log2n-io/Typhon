using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SwgTatooine.Navigation;
using SwgTatooine.Server.Terrain;
using Typhon.Engine;

namespace SwgTatooine.Server;

/// <summary>An operator's horde (horde.md § 7): start one with its knobs, or end the running one. Applied by the <c>Horde</c> system, answered on commit.</summary>
public sealed class HordeRequest
{
    /// <summary>End the running horde (the rest is ignored).</summary>
    public bool End;

    /// <summary>The city attacked, by name (case and spaces ignored).</summary>
    public string City;

    /// <summary>The countdown to the first wave, seconds: the time to gather (Loïc, 2026-11-04: 3 minutes, 5 was too much).</summary>
    public int StartsInS = 180;

    public int DurationS = 900;

    public int WaveSize = 20;

    public int WavePeriodS = 60;

    /// <summary>How many waves; 0 endless until the duration ends.</summary>
    public int Waves = 3;

    public bool Respawn;

    public int Level = 5;

    public int LevelSpread = 2;

    /// <summary>The <see cref="CreatureTemplates"/> raiders are drawn from, as bits; aggressive ones only, at most two.</summary>
    public int TemplateMask = (1 << CreatureTemplates.Rat) | (1 << CreatureTemplates.Viper);

    /// <summary>The attendance asked for; 0, the default, is the raid's force in defenders (<see cref="SimBridge.HordeDefendersFor"/>).</summary>
    public int TargetDefenders;

    /// <summary>The longest an answer is given to arrive, seconds: each one is given twice its own trip and a minute, within this (horde.md § 5).</summary>
    public int CommitTimeoutS = 300;

    public int CooldownS = 120;

    /// <summary>The share of each wave that holds the fortress rather than march on the town, in percent (horde.md § 10).</summary>
    public int GarrisonPercent = 20;

    /// <summary>
    /// How far beyond the city's edge the fortress stands, metres: drawn from this to 150 m more (Loïc, 2026-11-05: a battle farther out). 0, the
    /// default, is 250-400 m.
    /// </summary>
    public int FortressM;

    /// <summary>
    /// The raiders' health and hit against a creature of their level, in percent (Loïc, 2026-11-05: stronger raiders, fewer defenders): a raider of the
    /// default lasts three times as long and hits half as hard again.
    /// </summary>
    public int RaiderHealthPct = 300;

    public int RaiderHitPct = 150;

    internal readonly TaskCompletionSource<HordeOutcome> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 0 queued, 1 taken by a pass, 2 given up by its caller.
    private int _state;

    internal bool TryTake() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    internal bool TryAbandon() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;
}

/// <summary>A horde request's answer: the event's raw id, or why it was refused.</summary>
public readonly record struct HordeOutcome(bool Ok, long Event, string Message);

/// <summary>
/// The Horde (horde.md): a raid on a city, triggered by an operator. One <see cref="HordeCore"/> holds the event; its fortress and its raid lairs are its
/// view, spawned when it is announced and destroyed — the raiders with them, by the lairs' cascade — when it ends. Raiders march on the target along one
/// route planned per event (<see cref="HordeMarchPoint"/>); every session hears the status on change (<see cref="HordeStatus"/>); a character answers with
/// <see cref="AnswerCall"/>, counted at once and capped at the attendance asked for (D2), persisted (<see cref="HordeResponder"/>, D3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Serial, in the Economy phase after the lair director, through a side transaction</b> — the lair director's arrangement, since it spawns and destroys
/// lairs, creatures and a structure. It does its work once a second, and on a tick that carries an answer or an operator's request.
/// </para>
/// <para>
/// <b>The lair director does the killing and the respawning.</b> A raider is a creature of a raid lair: its corpse is removed there and, with respawn on,
/// replaced there at the fortress's gate (it marches like its wave). With respawn off, this system holds each lair's limit at its living count, so nothing
/// is refilled. The two counters the director bumps for a raid lair (<see cref="_hordeRaidersKilled"/>, <see cref="_hordeRaidersSpawned"/>) are read here,
/// in the same phase after it.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>
    /// The largest wave and attendance an operator may ask for (horde.md § 9): 500 raiders a wave, for the giga battle, and 4 000 defenders, for the one
    /// after it at 4 000 bots (Loïc, 2026-11-05).
    /// </summary>
    public const int HordeMaxWaveSize = 500;

    public const int HordeMaxTargetDefenders = 4000;

    /// <summary>
    /// How much stronger than one wave the defence is asked to be, in number: 1.5 is 2.25 times its strength under the square law — a fight the defence
    /// wins, with losses (<see cref="HordeDefendersFor"/>).
    /// </summary>
    public const double HordeDefenceEdge = 1.5d;

    /// <summary>The raiders' power up to its percent; 0 (not set) is the value as it is.</summary>
    internal static int Scaled(int value, int percent) => percent <= 0 ? value : (int)Math.Max(1L, (long)Math.Round((double)value * percent / 100d));

    /// <summary>The speed an answer's trip is reckoned at, m/s: the swoop's, which every character has (a shuttle is faster over a long way).</summary>
    internal const float HordeTravelMps = TatooineData.PlayerSwoopSpeedMps * TatooineData.PlayerGaitScale;

    /// <summary>
    /// The shortest window an answer is given to arrive, seconds, however near it answered from: a body has a fight to leave and a swoop to mount.
    /// </summary>
    internal const int HordeMinAnswerS = 120;

    /// <summary>
    /// The window an answer from <paramref name="distanceM"/> is given to arrive, seconds: twice its trip at <see cref="HordeTravelMps"/> and a minute, at
    /// least <see cref="HordeMinAnswerS"/>, at most the event's commit timeout. One fixed timeout cut the bots that answered from far away and kept a slot
    /// for a near one that never set off (night of 2026-11-04: 622 of 1 289 answers decayed).
    /// </summary>
    internal static int AnswerWindowS(float distanceM, int timeoutS)
        => (int)Math.Clamp((2d * Math.Max(0f, distanceM) / HordeTravelMps) + 60d, Math.Min(HordeMinAnswerS, timeoutS), Math.Max(5, timeoutS));

    /// <summary>A raider this close to the target has arrived: it wanders round it, attacking everyone in sight.</summary>
    internal const float HordeArriveM = 25f;

    /// <summary>A defender this close to the rally point (<see cref="RallyOf"/>) is present; a committed one has arrived.</summary>
    public const float HordeRallyM = 60f;

    /// <summary>
    /// The defenders' gather point stands this far from the fortress, on the line from it to the city's centre (Loïc, 2026-11-04: "a gather point for the
    /// defenders that is at 100 m of the fortress, on the line between the fortress position and the city's one"): they gather there to stop the raiders
    /// on their way, while the raiders' own target stays the square between the shuttleport and the town hall.
    /// </summary>
    /// <remarks>
    /// 120 m, from the bots: a wave is born 20 to 43 m from the fortress (<see cref="HordeGateM"/> and its rings), a defender stands within 12 m of the point and
    /// turns on a raider within 60 m, and so does a raider on a defender (<see cref="RaiderNoticeM"/>). 43 + 60 + 12 is 115: nobody fights at the spawn, and
    /// the first raiders meet the defenders a few seconds out of it.
    /// </remarks>
    public const float HordeRallyFromFortM = 120f;

    /// <summary>A committed defender downed farther than this from the target decays out of the count.</summary>
    public const float HordeDownedFarM = 150f;

    /// <summary>The fortress stands this far beyond the city's edge, drawn between the two, on ground clear of anything for <see cref="HordeClearM"/>.</summary>
    private const float HordeFortressMinM = 250f;

    private const float HordeFortressMaxM = 400f;

    // An operator's distance (HordeRequest.FortressM): drawn over this much beyond it, and bounded.
    private const float HordeFortressSpanM = 150f;

    private const int HordeFortressAskedMinM = 100;

    private const int HordeFortressAskedMaxM = 3000;

    private const float HordeClearM = 60f;

    /// <summary>The target stands this far north of the city's centre: the plaza between its shuttleport and its town hall (WorldBuilder.TownHallZ).</summary>
    private const float HordeTargetNorthM = 30f;

    /// <summary>Where the raiders appear: in front of the fortress's origin, at its gate's spawn arc (tat_horde_fortress rev 1: an 8 m arc round z 12.5).</summary>
    /// <remarks>
    /// <b>Clear of the walls, not on them.</b> It was 16 m — the footprint's own edge — and a wave is born in rings round it (WorldBuilder.LairRingM, three
    /// rings: 11.4 m out): half of each ring stood inside the walls, and once raiders collided (SimConfig.CollideRaiders) they could not walk out (Loïc,
    /// 2026-11-04: "tons of enemies stuck inside the fortress"). 16 m of footprint, 11.4 m of rings, a body and room to spare.
    /// </remarks>
    private const float HordeGateM = 32f;

    /// <summary>A status is sent at most this often, unless its phase changed (horde.md § 4).</summary>
    private const int HordeStatusMinSeconds = 1;

    private readonly ConcurrentQueue<HordeRequest> _hordeRequests = new();

    /// <summary>The running horde's raid lairs and the march their raiders are spawned with; read by the lair director's refill (same phase, before).</summary>
    private readonly Dictionary<EntityId, WorldBuilder.MarchOrder> _hordeLairs = [];

    // Bumped by the lair director for a raid lair's corpse and refill, read and cleared here.
    private int _hordeRaidersKilled;
    private int _hordeRaidersSpawned;

    /// <summary>The march route, x z pairs from the fortress's gate to the target; null without a horde. Written here, read by CreatureThink's workers.</summary>
    private float[] _hordeRoute;

    // The running (or last) horde, as committed; and whether it has been read since the start or a failed commit.
    private EntityId _horde;
    private HordeCore _hordeCore;
    private bool _hordeLoaded;

    // Its responders by character raw id: the row and what it holds.
    private readonly Dictionary<long, (EntityId Row, HordeResponder R)> _responders = [];

    // Scratch: who is in the world and where; the rallies to tell after the commit.
    private readonly Dictionary<EntityId, EntityId> _hordeBodies = [];
    private readonly HashSet<EntityId> _hordeBodySet = [];
    private readonly Dictionary<EntityId, uint> _hordeSessions = [];
    private readonly List<(long Character, HordeRally Rally)> _rallies = [];
    private readonly List<(HordeRequest Request, HordeOutcome Outcome)> _hordeAnswers = [];

    // The last status sent, and when.
    private HordeStatus _lastStatus;
    private long _lastStatusTick = long.MinValue / 2;
    private bool _hordeStatusDue;

    // Live metrics (horde.md § 8), for the running event.
    private int _hordePresent;
    private int _hordeCommitted;
    private int _hordeRaidersAlive;
    private int _hordeRaidersAtTarget;
    private readonly long[] _hordeRefusedBy = new long[4];
    private readonly List<int> _hordeArrivalS = [];
    private int _hordeDecays;

    // Why answers decayed — late, or gone offline — and the arrived defenders sent back to Committed, away from the fight (horde.md § 5).
    private int _hordeDecayLate;
    private int _hordeDecayOffline;
    private int _hordeRecommitted;
    private string _hordeLastReport;

    // Where the raiders are and what they are doing, once a pass (horde.md § 8): by AI mode, and their distance to the target, nearest and mean.
    private readonly int[] _raidersByMode = new int[6];
    private int _raidersLiving;

    // The raiders inside the city, from the survey, for the report.
    private readonly List<(EntityId Id, float X, float Z, int Level)> _raidersInTown = [];


    /// <summary>How often the raiders' advance is judged, seconds (<see cref="JudgeAdvance"/>); the checks shorten it.</summary>
    internal int HordeAdvanceWindowS { get; set; } = 30;

    /// <summary>The raiders made more than this share of an unopposed march over a window: the defence does not slow them, and more help is asked for.</summary>
    internal const float HordeUnslowedShare = 0.5f;

    /// <summary>The fortress losing less than this share of its whole over a judgement's window is not falling: more help is asked for.</summary>
    internal const float HordeFortressPaceShare = 0.01f;

    // The fortress's hit points at the last judgement; 0 before the first.
    private int _fortHpJudged;

    // Each marching raider's distance to the target at the last judgement, and this one's; the last advance judged (0: none yet) and the raises.
    private Dictionary<EntityId, float> _raiderLastM = [];
    private Dictionary<EntityId, float> _raiderNowM = [];
    private long _advanceJudgedTick;
    private float _hordeAdvance;
    private int _hordeRaises;
    private float _raiderNearestM;
    private float _raiderMeanM;

    // What HTTP reads, published by the pass that changed it: the report and the status, as JSON. Never the live fields, which this thread writes.
    private string _hordeReportJson;
    private string _hordeStatusJson;

    /// <summary>The running (or last) horde with its live metrics, as the last pass left it (<c>GET /admin/horde</c>, <c>/swg/stats</c>); null for none.</summary>
    public string HordeReportJson => Volatile.Read(ref _hordeReportJson);

    /// <summary>The running horde's status, as the last pass left it (<c>GET /swg/horde</c>); null when none runs.</summary>
    public string HordeStatusJson => Volatile.Read(ref _hordeStatusJson);

    /// <summary>Hands an operator's horde request to the simulation: answered when the pass that applies it commits.</summary>
    public Task<HordeOutcome> RequestHorde(HordeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _hordeRequests.Enqueue(request);
        return request.Done.Task;
    }

    /// <summary>
    /// Where a raider at (<paramref name="x"/>, <paramref name="z"/>) steers next: the end of the route segment nearest it, or the one after when it is
    /// there already. The target itself without a route. From CreatureThink's workers: reads one published array.
    /// </summary>
    internal (float X, float Z) HordeMarchPoint(float x, float z, float homeX, float homeZ)
    {
        var route = Volatile.Read(ref _hordeRoute);
        if (route == null || route.Length < 4)
        {
            return (homeX, homeZ);
        }

        var points = route.Length / 2;
        var best = float.MaxValue;
        var nearest = 0;
        for (var i = 0; i + 1 < points; i++)
        {
            var d = SegmentDistanceSq(x, z, route[i * 2], route[(i * 2) + 1], route[(i * 2) + 2], route[(i * 2) + 3]);
            if (d < best)
            {
                best = d;
                nearest = i;
            }
        }

        var next = nearest + 1;
        var dx = route[next * 2] - x;
        var dz = route[(next * 2) + 1] - z;
        if ((dx * dx) + (dz * dz) < 16f && next + 1 < points)
        {
            next++;
        }

        return (route[next * 2], route[(next * 2) + 1]);
    }

    /// <summary>The squared distance from a point to the raiders' route (the march's polyline); infinity without one.</summary>
    private float RouteDistanceSq(float x, float z)
    {
        var route = Volatile.Read(ref _hordeRoute);
        if (route == null || route.Length < 4)
        {
            return float.PositiveInfinity;
        }

        var best = float.PositiveInfinity;
        for (var i = 0; (i * 2) + 3 < route.Length; i++)
        {
            best = MathF.Min(best, SegmentDistanceSq(x, z, route[i * 2], route[(i * 2) + 1], route[(i * 2) + 2], route[(i * 2) + 3]));
        }

        return best;
    }

    private static float SegmentDistanceSq(float px, float pz, float ax, float az, float bx, float bz)
    {
        var abx = bx - ax;
        var abz = bz - az;
        var len = (abx * abx) + (abz * abz);
        var t = len <= 0f ? 0f : Math.Clamp((((px - ax) * abx) + ((pz - az) * abz)) / len, 0f, 1f);
        var cx = ax + (abx * t) - px;
        var cz = az + (abz * t) - pz;
        return (cx * cx) + (cz * cz);
    }

    /// <summary>The horde system's pass: operator requests, answers, and once a second the event's life, its responders and its status.</summary>
    public void HordeTick(TickContext ctx)
    {
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var subs = ctx.Subscriptions;
        var answers = subs == null ? default : subs.Commands<AnswerCall>();
        var hz = Math.Max(1, _config.TickRateHz);
        var pass = ctx.TickNumber % hz == 0;
        if (!pass && answers.Count == 0 && _hordeRequests.IsEmpty)
        {
            return;
        }

        var now = ctx.WorldTick;
        var structures = false;
        bool committed;
        _rallies.Clear();
        _hordeAnswers.Clear();
        using (var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
        {
            EnsureHorde(tx, now);
            while (_hordeRequests.TryDequeue(out var request))
            {
                if (request.TryTake())
                {
                    _hordeAnswers.Add((request, request.End ? EndHorde(tx, now, ref structures) : StartHorde(tx, now, request, ref structures)));
                }
            }

            if (answers.Count > 0)
            {
                foreach (var command in answers)
                {
                    Answer(tx, subs, in command, now);
                }
            }

            if (pass && !_horde.IsNull && !HordePhase.IsOver(_hordeCore.Phase))
            {
                Advance(ctx, tx, now, hz, ref structures);

                // Not once the pass has ended it: Finish told every responder to stand down, and judged as an event over — near the gather point alone,
                // the raiders' route gone — every defender in the town was "away from the fight" and committed again, a rally told after the stand-down
                // (Loïc, 2026-11-05: 571 bots left defending beside the town hall after the 4K battle was lost).
                if (!HordePhase.IsOver(_hordeCore.Phase))
                {
                    Responders(tx, now, hz);
                    CountPresent(tx);
                }
            }

            committed = tx.Commit();
        }

        if (!committed)
        {
            // What this pass changed did not happen: read the event again on the next one; answers and requests are refused.
            _hordeLoaded = false;
            foreach (var (request, _) in _hordeAnswers)
            {
                request.Done.TrySetResult(new HordeOutcome(false, 0, "the tick's transaction did not commit"));
            }

            return;
        }

        foreach (var (request, outcome) in _hordeAnswers)
        {
            request.Done.TrySetResult(outcome);
        }

        if (structures)
        {
            // The fortress came or went: every static neighbourhood taken before it is stale (SimBridge.Proximity.cs).
            InvalidateStaticNeighbourhoods();
        }

        if (_rallies.Count > 0)
        {
            TatooineReplication.FillCharacterSessions(_hordeSessions);
            foreach (var (character, rally) in _rallies)
            {
                if (_hordeSessions.TryGetValue(EntityId.FromRawValue((ulong)character), out var session))
                {
                    TatooineReplication.Tell(ctx, session, in rally);
                }
            }
        }

        MaybeAnnounce(ctx, now, hz);
        if (pass || _hordeAnswers.Count > 0)
        {
            Volatile.Write(ref _hordeReportJson, HordeJson(now));
            Volatile.Write(ref _hordeStatusJson, _horde.IsNull || HordePhase.IsOver(_hordeCore.Phase) ? null : StatusJson(CurrentHordeStatus(now, hz)));
        }
    }

    private string StatusJson(in HordeStatus s) => string.Create(CultureInfo.InvariantCulture,
        $"{{\"event\":\"{s.Event}\",\"phase\":{s.Phase},\"city\":{s.City},\"cityName\":\"{HordeCityName(s.City)}\",\"help\":{s.HelpNeeded},\"startsIn\":{s.StartsInS},\"raiders\":{s.RaidersAlive},\"committed\":{s.Committed},\"present\":{s.Present},\"x\":{s.TargetX:F1},\"z\":{s.TargetZ:F1},\"fx\":{s.FortX:F1},\"fz\":{s.FortZ:F1},\"hx\":{s.HallX:F1},\"hz\":{s.HallZ:F1},\"hall\":{s.HallHp},\"fort\":{s.FortHp}}}");

    /// <summary>Reads the running horde, and its responders, on the first pass after a start or a failed commit.</summary>
    private void EnsureHorde(Transaction tx, long now)
    {
        if (_hordeLoaded)
        {
            return;
        }

        _hordeLoaded = true;
        _horde = EntityId.Null;
        _hordeCore = default;
        _responders.Clear();
        _hordeLairs.Clear();
        Volatile.Write(ref _hordeRoute, null);
        foreach (var found in tx.Query<HordeArch>())
        {
            var c = found.Read(HordeArch.Core);
            if (!HordePhase.IsOver(c.Phase) && (_horde.IsNull || c.StartTick > _hordeCore.StartTick))
            {
                _horde = found.Id;
                _hordeCore = c;
            }
        }

        // The running event's answers; any other's are an event over, whose answers a server before 2026-11-06 kept for good (18 620 rows with no raid
        // running): swept, once a start.
        var raw = _horde.IsNull ? 0L : (long)_horde.RawValue;
        var stale = new List<EntityId>();
        foreach (var found in tx.Query<HordeResponderArch>())
        {
            var r = found.Read(HordeResponderArch.Core);
            if (raw != 0 && r.Event == raw)
            {
                _responders[r.Character] = (found.Id, r);
            }
            else
            {
                stale.Add(found.Id);
            }
        }

        foreach (var id in stale)
        {
            tx.Destroy(id);
        }

        if (_horde.IsNull)
        {
            return;
        }

        // Its view, as it stood: the march its lairs refill with, and the route.
        TatooineReplication.SetSiegeFortress(EntityId.FromRawValue((ulong)_hordeCore.Fortress));
        var march = MarchOf(in _hordeCore);
        foreach (var lair in new[] { _hordeCore.LairA, _hordeCore.LairB })
        {
            if (lair != 0)
            {
                _hordeLairs[EntityId.FromRawValue((ulong)lair)] = march;
            }
        }

        PlanHordeRoute(in _hordeCore);
        _hordeStatusDue = true;
    }

    private static WorldBuilder.MarchOrder MarchOf(in HordeCore c)
    {
        var dx = c.TargetX - c.FortX;
        var dz = c.TargetZ - c.FortZ;
        return new WorldBuilder.MarchOrder(c.TargetX, c.TargetZ, MathF.Sqrt((dx * dx) + (dz * dz)) + 150f, RaiderMarches, c.RaiderHealthPct, c.RaiderHitPct);
    }

    /// <summary>Starts a horde: the event, its fortress and its raid lairs, the route, and the mail to every character — in this pass's transaction.</summary>
    private HordeOutcome StartHorde(Transaction tx, long now, HordeRequest r, ref bool structures)
    {
        if (!_horde.IsNull && !HordePhase.IsOver(_hordeCore.Phase))
        {
            return new HordeOutcome(false, 0, "a horde is running already: end it first");
        }

        var index = PlanetIndexes?[0] ?? _index;
        var city = -1;
        for (var i = 0; i < _map.Cities.Count && i < index.Cities.Count && city < 0; i++)
        {
            city = string.Equals(Squeeze(_map.Cities[i].Name), Squeeze(r.City), StringComparison.OrdinalIgnoreCase)
                || string.Equals(Squeeze(HordeCityName(i)), Squeeze(r.City), StringComparison.OrdinalIgnoreCase) ? i : -1;
        }

        if (city < 0)
        {
            return new HordeOutcome(false, 0, $"no city named \"{r.City}\"");
        }

        var templates = new List<int>(2);
        for (var t = 0; t < CreatureTemplates.WildCount && templates.Count < 2; t++)
        {
            if ((r.TemplateMask & (1 << t)) != 0 && CreatureTemplates.Aggressive[t])
            {
                templates.Add(t);
            }
        }

        if (templates.Count == 0)
        {
            return new HordeOutcome(false, 0, "no aggressive template in the mask");
        }

        var hz = Math.Max(1, _config.TickRateHz);
        var (cx, cz, radius, _) = index.Cities[city];
        var (targetX, targetZ) = HordeTargetOf(city, cx, cz);
        var asked = Math.Clamp(r.FortressM, HordeFortressAskedMinM, HordeFortressAskedMaxM);
        var (fortMin, fortMax) = r.FortressM <= 0 ? (HordeFortressMinM, HordeFortressMaxM) : (asked, asked + HordeFortressSpanM);
        if (!PlaceFortress(cx, cz, radius, fortMin, fortMax, now, out var fx, out var fz))
        {
            return new HordeOutcome(false, 0, "no clear ground for the fortress round that city");
        }

        // The gate faces the target, by quarter turns (WorldIndex.QuarterTurn: 0 +z, 1 +x, 2 -z, 3 -x).
        var ddx = targetX - fx;
        var ddz = targetZ - fz;
        var yaw = MathF.Abs(ddz) >= MathF.Abs(ddx) ? (ddz >= 0f ? 0 : 2) : (ddx >= 0f ? 1 : 3);
        var (gx, gz) = yaw switch { 1 => (fx + HordeGateM, fz), 2 => (fx, fz - HordeGateM), 3 => (fx - HordeGateM, fz), _ => (fx, fz + HordeGateM) };

        var terrain = TerrainField.Shared(_config.ContentScale);
        var level = Math.Clamp(r.Level, 1, 50);
        var waveSize = Math.Clamp(r.WaveSize, 1, HordeMaxWaveSize);
        var waves = r.Waves > 0 ? r.Waves : (Math.Max(10, r.DurationS) + Math.Max(2, r.WavePeriodS) - 1) / Math.Max(2, r.WavePeriodS);
        var defenderLevel = MeanSessionLevel(tx);
        // 0 is "as is" (100 %), the zero-means-no-data rule; anything else is bounded.
        var healthPct = r.RaiderHealthPct <= 0 ? 100 : Math.Clamp(r.RaiderHealthPct, 10, 2000);
        var hitPct = r.RaiderHitPct <= 0 ? 100 : Math.Clamp(r.RaiderHitPct, 10, 2000);
        var target = r.TargetDefenders > 0
            ? Math.Clamp(r.TargetDefenders, 1, HordeMaxTargetDefenders)
            : HordeDefendersFor(waveSize, level, templates, defenderLevel == 0 ? level : defenderLevel, healthPct, hitPct);
        var fortress = WorldBuilder.SpawnFortress(tx, fx, fz, yaw, terrain, 0);
        var lairA = WorldBuilder.SpawnHordeLair(tx, templates[0], level, gx, gz, _config, terrain, 0);
        var lairB = templates.Count > 1 ? WorldBuilder.SpawnHordeLair(tx, templates[1], level, gx, gz, _config, terrain, 0) : EntityId.Null;
        structures = true;

        // The operator's knobs bounded (a day at most), so no product of them overflows a tick count.
        r.StartsInS = Math.Clamp(r.StartsInS, 0, 86_400);
        r.DurationS = Math.Clamp(r.DurationS, 10, 86_400);
        r.WavePeriodS = Math.Clamp(r.WavePeriodS, 2, 86_400);
        r.CommitTimeoutS = Math.Clamp(r.CommitTimeoutS, 5, 86_400);
        r.CooldownS = Math.Clamp(r.CooldownS, 0, 86_400);
        var startTick = now + (Math.Max(0, r.StartsInS) * (long)hz);
        var core = new HordeCore
        {
            City = city,
            Phase = HordePhase.Incoming,
            Respawn = r.Respawn ? (byte)1 : (byte)0,
            Level = (byte)level,
            LevelSpread = (byte)Math.Clamp(r.LevelSpread, 0, 20),
            StartTick = startTick,
            EndTick = startTick + (Math.Max(10, r.DurationS) * (long)hz),
            FortX = fx,
            FortZ = fz,
            FortYaw = yaw,
            TargetX = targetX,
            TargetZ = targetZ,
            WaveSize = waveSize,
            WavePeriodTicks = Math.Max(2, r.WavePeriodS) * hz,
            Waves = Math.Max(0, r.Waves),
            NextWaveTick = startTick,
            TemplateMask = r.TemplateMask,
            TargetDefenders = target,
            CommitTimeoutS = Math.Max(5, r.CommitTimeoutS),
            CooldownS = Math.Max(0, r.CooldownS),
            GarrisonPercent = (byte)Math.Clamp(r.GarrisonPercent, 0, 100),
            RaiderHealthPct = healthPct,
            RaiderHitPct = hitPct,
            Fortress = (long)fortress.RawValue,
            LairA = (long)lairA.RawValue,
            LairB = lairB.IsNull ? 0 : (long)lairB.RawValue,
        };

        // The siege (horde.md § 10): the town hall the raiders break — three minutes of sixty of them — and the fortress the defence's assault breaks, three
        // minutes of the share of the defenders asked for that assaults it.
        var (hallHp, fortHp) = SiegeHitPoints(level, templates, defenderLevel == 0 ? level : defenderLevel, hitPct, target);
        if (TownHallOf(tx, city, cx, cz) is { } townHall)
        {
            core.Hall = (long)townHall.Id.RawValue;
            core.HallX = townHall.X;
            core.HallZ = townHall.Z;
            core.HallHp = core.HallMaxHp = hallHp;
        }
        else
        {
            Console.WriteLine($"  horde: {HordeCityName(city)} has no town hall found — this raid cannot be lost by its fall");
        }

        core.FortHp = core.FortMaxHp = fortHp;
        // Spawned bare and written after: the core is past the 112 bytes a spawn carries inline (TYPHON013).
        var id = tx.Spawn<HordeArch>();
        tx.OpenMut(id).Enable(HordeArch.Core, in core);
        _horde = id;
        _hordeCore = core;
        TatooineReplication.SetSiegeFortress(fortress);
        _fortAttackers.Clear();
        _fortHitters.Clear();
        Interlocked.Exchange(ref _siegeFortDamage, 0);
        Volatile.Write(ref _fortGuard, null);
        _responders.Clear();
        ResetHordeMetrics();
        var march = MarchOf(in core);
        _hordeLairs.Clear();
        _hordeLairs[lairA] = march;
        if (!lairB.IsNull)
        {
            _hordeLairs[lairB] = march;
        }

        PlanHordeRoute(in core);
        _hordeStatusDue = true;

        // One mail to every character (horde.md § 3), spread over the Workshop's passes by its cap.
        var name = HordeCityName(city);
        var side = Compass(fx - cx, fz - cz);
        var minutes = Math.Max(1, (r.StartsInS + 59) / 60);
        var subject = $"A horde marches on {name}";
        var text = FormattableString.Invariant(
            $"Raiders gather in a fortress {side} of {name}. They march on the town in {minutes} min, and attack everyone in its square. Answer the call to defend it: gather {HordeRallyFromFortM:F0} m from the fortress, on the way to the town, to stop them before they reach it.");
        var mailed = 0;
        foreach (var character in tx.Query<CharacterArch>())
        {
            QueueSystemMail(unchecked((long)character.Id.RawValue), MailKind.Horde, (long)id.RawValue, subject, text);
            mailed++;
        }

        Console.WriteLine(FormattableString.Invariant(
            $"  horde: {name} in {r.StartsInS} s — fortress at ({fx:F0}, {fz:F0}) {side}, {core.Waves} wave(s) of {core.WaveSize} every {r.WavePeriodS} s, level {level}, raiders {healthPct} % health {hitPct} % hit, target {core.TargetDefenders} defenders{(r.TargetDefenders > 0 ? "" : $" (a wave's strength, against CL {defenderLevel})")}, fortress {fortHp} hp, {mailed} mailed"));
        return new HordeOutcome(true, (long)id.RawValue, $"a horde marches on {name} in {r.StartsInS} s");
    }

    /// <summary>
    /// The defenders a raid asks for (horde.md § 10; Loïc, 2026-11-05: stronger raiders, fewer defenders): enough to beat one wave by
    /// <see cref="HordeDefenceEdge"/> under Lanchester's square law. A mass fight's strength is its number squared × each one's health × damage, so N
    /// defenders match W raiders when N = W √(raider ÷ player); the edge is what wins it.
    /// </summary>
    /// <remarks>
    /// <b>A wave's, not the raid's.</b> The waves meet the defence one after the other, so it fights a wave at a time. The first rule summed every raider of
    /// every wave, linear, against a defender worth 2.5 beasts: 999 defenders for five waves of 145, and armed they razed the fortress in two minutes
    /// without a raider reaching the town (night of 2026-11-04). The square root is why a stronger raider asks fewer defenders than as many weaker ones:
    /// three times the health and half again the hit is 4.5 times the strength and asks 2.1 times the defenders, where 4.5 times the raiders asks 4.5.
    /// </remarks>
    /// <param name="waveSize">Raiders a wave.</param>
    /// <param name="level">The raiders' level, the spread's centre.</param>
    /// <param name="templates">The raiders' templates, drawn evenly.</param>
    /// <param name="defenderLevel">The level the defenders are reckoned at.</param>
    /// <param name="healthPct">The raiders' health against a creature of their level, percent; 0 is 100.</param>
    /// <param name="hitPct">The raiders' hit against a creature of their level, percent; 0 is 100.</param>
    internal static int HordeDefendersFor(int waveSize, int level, IReadOnlyList<int> templates, int defenderLevel, int healthPct, int hitPct)
    {
        double raider = 0;
        foreach (var t in templates)
        {
            var foe = CreatureTemplates.IsCamp(t);
            raider += (double)Scaled(ContentLevels.CreatureHealth(level, foe), healthPct) * Scaled(ContentLevels.CreatureHit(level, foe), hitPct);
        }

        raider /= Math.Max(1, templates.Count);
        var player = ContentLevels.PlayerStrength(Math.Max(1, defenderLevel));
        return (int)Math.Clamp(Math.Ceiling(Math.Max(0, waveSize) * Math.Sqrt(raider / player) * HordeDefenceEdge), 1, HordeMaxTargetDefenders);
    }

    /// <summary>The mean level of the bodies sessions drive, the ones who can answer; 0 with none on. Once, when a horde starts.</summary>
    private static int MeanSessionLevel(Transaction tx)
    {
        long sum = 0;
        var n = 0;
        foreach (var cluster in tx.For<Player>().GetClusterEnumerator())
        {
            var controls = cluster.GetReadOnlySpan(Player.Control);
            var looks = cluster.GetReadOnlySpan(Player.Look);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                if (controls[slot].Kind != ControllerKind.Scripted && looks[slot].Level > 0)
                {
                    sum += looks[slot].Level;
                    n++;
                }
            }
        }

        return n == 0 ? 0 : (int)Math.Round((double)sum / n);
    }

    /// <summary>The defenders' gather point: <see cref="HordeRallyFromFortM"/> from the fortress toward the city's centre.</summary>
    internal (float X, float Z) RallyOf(in HordeCore c)
    {
        var index = PlanetIndexes?[0] ?? _index;
        var (cx, cz) = c.City < index.Cities.Count ? (index.Cities[c.City].X, index.Cities[c.City].Z) : (c.TargetX, c.TargetZ);
        var dx = cx - c.FortX;
        var dz = cz - c.FortZ;
        var d = MathF.Max(1e-3f, MathF.Sqrt((dx * dx) + (dz * dz)));
        var reach = MathF.Min(HordeRallyFromFortM, d);
        return (c.FortX + (dx / d * reach), c.FortZ + (dz / d * reach));
    }

    /// <summary>
    /// The raiders' target in a city: halfway between its shuttleport and its town hall, as they stand (the ones nearest its centre); without either,
    /// <see cref="HordeTargetNorthM"/> north of the centre, where the build puts that square.
    /// </summary>
    private (float X, float Z) HordeTargetOf(int city, float cx, float cz)
    {
        var structures = _cityStructures?[0];
        if (structures == null || city >= structures.Length)
        {
            return (cx, cz + HordeTargetNorthM);
        }

        SeenStructure? port = null;
        SeenStructure? hall = null;
        float Sq(in SeenStructure s) => ((s.X - cx) * (s.X - cx)) + ((s.Z - cz) * (s.Z - cz));
        foreach (var s in structures[city])
        {
            if (s.Kind == StructureKind.Shuttleport && (port == null || Sq(in s) < Sq(port.Value)))
            {
                port = s;
            }
            else if (s.Kind == StructureKind.TownHall && (hall == null || Sq(in s) < Sq(hall.Value)))
            {
                hall = s;
            }
        }

        return port is { } p && hall is { } h ? ((p.X + h.X) * 0.5f, (p.Z + h.Z) * 0.5f) : (cx, cz + HordeTargetNorthM);
    }

    /// <summary>A city by name, as the horde reads it: its centre and radius (the checks' ruler).</summary>
    internal (float X, float Z, float Radius) CityForTests(string name)
    {
        var index = PlanetIndexes?[0] ?? _index;
        for (var i = 0; i < _map.Cities.Count && i < index.Cities.Count; i++)
        {
            if (string.Equals(Squeeze(_map.Cities[i].Name), Squeeze(name), StringComparison.OrdinalIgnoreCase)
                || string.Equals(Squeeze(HordeCityName(i)), Squeeze(name), StringComparison.OrdinalIgnoreCase))
            {
                return (index.Cities[i].X, index.Cities[i].Z, index.Cities[i].Radius);
            }
        }

        throw new ArgumentException($"no city named {name}", nameof(name));
    }

    /// <summary>A city's name as the players read it (the original names, 06-gameplay.md § 7); an operator may name it either way.</summary>
    private string HordeCityName(int city) => city < CityDisplayNames.Length ? CityDisplayNames[city] : _map.Cities[city].Name;

    private static string Squeeze(string s) => (s ?? string.Empty).Replace(" ", string.Empty).Replace("'", string.Empty);

    private static string Compass(float dx, float dz)
    {
        var a = MathF.Atan2(dz, dx) * 180f / MathF.PI;
        string[] names = ["east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east"];
        return names[(int)MathF.Round(((a + 360f) % 360f) / 45f) % 8];
    }

    /// <summary>
    /// Draws the fortress's spot: <paramref name="minM"/>-<paramref name="maxM"/> beyond the city's edge, inside the world, out of every city, with
    /// nothing standing round it.
    /// </summary>
    private bool PlaceFortress(float cx, float cz, float radius, float minM, float maxM, long now, out float fx, out float fz)
    {
        var rng = new Rng((uint)(now * 2654435761L) | 1u);
        var half = (_config.WorldEdgeM * 0.5f) - 100f;
        var index = PlanetIndexes?[0] ?? _index;
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var angle = rng.NextRange(0f, MathF.PI * 2f);
            var d = radius + rng.NextRange(minM, maxM);
            fx = cx + (MathF.Cos(angle) * d);
            fz = cz + (MathF.Sin(angle) * d);
            if (MathF.Abs(fx) > half || MathF.Abs(fz) > half)
            {
                continue;
            }

            var inCity = false;
            foreach (var (ox, oz, oradius, _) in index.Cities)
            {
                var ex = fx - ox;
                var ez = fz - oz;
                inCity |= (ex * ex) + (ez * ez) < (oradius + CityPath.CityMarginM + HordeClearM) * (oradius + CityPath.CityMarginM + HordeClearM);
            }

            if (!inCity && Nothing<WorldObject>(fx, fz) && Nothing<CreatureLair>(fx, fz) && Nothing<InstallationMarker>(fx, fz))
            {
                return true;
            }
        }

        fx = fz = 0f;
        return false;
    }

    private bool Nothing<TArch>(float x, float z) where TArch : Archetype<TArch>, new()
    {
        var sphere = new BSphere2F { CenterX = x, CenterY = z, Radius = HordeClearM };
        var e = Dbe.ClusterSpatialQuery<TArch>(new RealmId(0)).Radius(in sphere);
        try
        {
            return e.Count() == 0;
        }
        finally
        {
            e.Dispose();
        }
    }

    /// <summary>
    /// The march (horde.md § 2): straight from the gate to where the line enters the city, then round its buildings on the crowd's search to the target.
    /// A search that finds no way leaves the straight line, as the crowd's does.
    /// </summary>
    private void PlanHordeRoute(in HordeCore c)
    {
        var (gx, gz) = c.FortYaw switch
        {
            1 => (c.FortX + HordeGateM, c.FortZ),
            2 => (c.FortX, c.FortZ - HordeGateM),
            3 => (c.FortX - HordeGateM, c.FortZ),
            _ => (c.FortX, c.FortZ + HordeGateM),
        };
        var points = new List<float> { gx, gz };
        var index = PlanetIndexes?[0] ?? _index;
        if (_cityStructures != null && _crowdWalls != null && c.City < index.Cities.Count)
        {
            var (cx, cz, radius, _) = index.Cities[c.City];

            // Where the straight line from the gate first comes within the city's disc and margin: the search starts there.
            var reach = radius + CityPath.CityMarginM;
            var dx = c.TargetX - gx;
            var dz = c.TargetZ - gz;
            var length = MathF.Sqrt((dx * dx) + (dz * dz));
            var enter = 0f;
            for (var s = 0f; s < length; s += 2f)
            {
                var px = gx + (dx * s / length) - cx;
                var pz = gz + (dz * s / length) - cz;
                if ((px * px) + (pz * pz) <= reach * reach)
                {
                    enter = s;
                    break;
                }
            }

            var ex = gx + (dx * enter / length);
            var ez = gz + (dz * enter / length);
            points.Add(ex);
            points.Add(ez);
            var path = CityPath.Route(_cityStructures[0][c.City], _crowdWalls, (ex, ez), (c.TargetX, c.TargetZ), SearchExpansions);
            if (path != null)
            {
                foreach (var (px, pz) in path)
                {
                    points.Add(px);
                    points.Add(pz);
                }
            }
        }

        points.Add(c.TargetX);
        points.Add(c.TargetZ);
        Volatile.Write(ref _hordeRoute, points.ToArray());
    }

    /// <summary>Ends the running horde early (an operator): over, as Ended.</summary>
    private HordeOutcome EndHorde(Transaction tx, long now, ref bool structures)
    {
        if (_horde.IsNull || HordePhase.IsOver(_hordeCore.Phase))
        {
            return new HordeOutcome(false, 0, "no horde is running");
        }

        var raw = (long)_horde.RawValue;
        Finish(tx, now, HordePhase.Ended, ref structures);
        return new HordeOutcome(true, raw, "the horde was ended");
    }

    /// <summary>The event's life, once a second: the start, the waves, the respawn rule, the counts, the outcome.</summary>
    private void Advance(TickContext ctx, Transaction tx, long now, int hz, ref bool structures)
    {
        ref var c = ref _hordeCore;
        var killed = _hordeRaidersKilled;
        var refilled = _hordeRaidersSpawned;
        _hordeRaidersKilled = 0;
        _hordeRaidersSpawned = 0;
        c.Killed += killed;
        c.Spawned += refilled;
        var dirty = killed + refilled > 0;

        if (c.Phase == HordePhase.Incoming && now >= c.StartTick)
        {
            c.Phase = HordePhase.Active;
            dirty = true;
            Console.WriteLine($"  horde: {HordeCityName(c.City)} — the waves start");
        }

        var spawnedNow = 0;
        if (c.Phase == HordePhase.Active && (c.Waves == 0 || c.WavesSpawned < c.Waves) && now >= c.NextWaveTick && now < c.EndTick)
        {
            var before = c.Spawned;
            SpawnWave(tx, now);
            spawnedNow = c.Spawned - before;
            c.WavesSpawned++;
            c.NextWaveTick += c.WavePeriodTicks;
            dirty = true;
        }

        // The living, and the respawn rule: off, each lair's limit is held at its living count, so the lair director has nothing to refill.
        var alive = 0;
        foreach (var raw in new[] { c.LairA, c.LairB })
        {
            if (raw != 0 && tx.TryOpenMut(EntityId.FromRawValue((ulong)raw), out var lair))
            {
                var spawner = lair.Read(CreatureLair.Spawner);
                alive += spawner.AliveCount;
                if (c.Respawn == 0 && spawner.SpawnLimit != spawner.AliveCount)
                {
                    spawner.SpawnLimit = spawner.AliveCount;
                    spawner.NextSpawnTick = 0;
                    lair.Set(CreatureLair.Spawner, spawner);
                }
            }
        }

        _hordeRaidersAtTarget = CountRaidersAtTarget(tx, in c);
        SurveyRaiders(tx, in c);

        // The living, not the lairs' count: a corpse is in that until it is removed, which held the win back by its few seconds (review, 2026-11-04) —
        // and the wave spawned in this very transaction, which the survey's walk does not see yet.
        alive = _raidersLiving + spawnedNow;
        _hordeRaidersAlive = alive;
        if (c.Phase != HordePhase.Active)
        {
            // Before the waves the fortress cannot be broken: blows struck at it during the countdown land nowhere (the first live siege took a fifth of
            // it before a raider came out).
            Interlocked.Exchange(ref _siegeFortDamage, 0);
            _fortHitters.Clear();
        }
        else
        {
            dirty |= Siege(ctx, tx, now, hz, ref c);
            SendRadar(ctx, now, hz);

            // The siege decides before the waves do (horde.md § 10): the town hall broken, the town has fallen; the fortress broken, the raid is beaten
            // and its raiders go with it.
            if (c.HallMaxHp > 0 && c.HallHp <= 0)
            {
                Finish(tx, now, HordePhase.Lost, ref structures);
                return;
            }

            if (c.FortMaxHp > 0 && c.FortHp <= 0)
            {
                Finish(tx, now, HordePhase.Won, ref structures);
                return;
            }
        }

        if (c.Phase == HordePhase.Active && now - _advanceJudgedTick >= HordeAdvanceWindowS * (long)hz)
        {
            _advanceJudgedTick = now;
            dirty |= JudgeAdvance(tx, ref c);
        }

        if (c.Phase == HordePhase.Active)
        {
            if (c.Waves > 0 && c.WavesSpawned >= c.Waves && alive == 0)
            {
                Finish(tx, now, HordePhase.Won, ref structures);
                return;
            }

            if (now >= c.EndTick)
            {
                Finish(tx, now, alive == 0 ? HordePhase.Won : c.Waves == 0 ? HordePhase.Ended : HordePhase.Lost, ref structures);
                return;
            }
        }

        if (dirty)
        {
            tx.OpenMut(_horde).Set(HordeArch.Core, c);
        }
    }

    /// <summary>A raider that marches on the town (<see cref="CreatureBrain.March"/>).</summary>
    internal const byte RaiderMarches = 1;

    /// <summary>A raider of the garrison: it holds the fortress (Loïc, 2026-11-04 — a garrison, not the whole horde, defends it).</summary>
    internal const byte RaiderGarrison = 2;

    /// <summary>How far a garrison raider ranges from the fortress's gate, metres: round the walls, and back.</summary>
    internal const float HordeGarrisonLeashM = 60f;

    /// <summary>One wave at the gate: split over the raid lairs, each raider drawn round the level, marching from birth.</summary>
    private void SpawnWave(Transaction tx, long now)
    {
        ref var c = ref _hordeCore;
        var terrain = TerrainField.Shared(_config.ContentScale);
        var rng = new Rng((uint)((now * 0x9E3779B1L) ^ c.WavesSpawned) | 1u);
        var march = MarchOf(in c);
        var lairs = c.LairB != 0 ? new[] { c.LairA, c.LairB } : new[] { c.LairA };
        for (var l = 0; l < lairs.Length; l++)
        {
            var id = EntityId.FromRawValue((ulong)lairs[l]);
            if (!tx.TryOpenMut(id, out var lair))
            {
                continue;
            }

            var spawner = lair.Read(CreatureLair.Spawner);
            var place = lair.Read(CreatureLair.Bounds);

            // The garrison's share holds the fortress: home at the gate, a short tether, no march (horde.md § 10).
            var garrison = new WorldBuilder.MarchOrder(place.X, place.Z, HordeGarrisonLeashM, RaiderGarrison, c.RaiderHealthPct, c.RaiderHitPct);
            var count = (c.WaveSize / lairs.Length) + (l < c.WaveSize % lairs.Length ? 1 : 0);
            for (var i = 0; i < count; i++)
            {
                var level = Math.Clamp(c.Level + rng.NextInt(-c.LevelSpread, c.LevelSpread + 1), 1, 50);
                var holds = rng.NextInt(0, 100) < c.GarrisonPercent;
                WorldBuilder.SpawnCreature(tx, ref rng, id, spawner.CreatureTemplate, level, place.X, place.Z, i, _config, terrain, 0,
                    in holds ? ref garrison : ref march);
            }

            spawner.AliveCount += count;
            spawner.SpawnLimit += count;
            spawner.Guards += count;
            lair.Set(CreatureLair.Spawner, spawner);
            c.Spawned += count;
        }
    }

    /// <summary>The event is over: its fortress and lairs destroyed, the raiders with them; the waypoints withdrawn; the report written.</summary>
    private void Finish(Transaction tx, long now, byte phase, ref bool structures)
    {
        ref var c = ref _hordeCore;
        c.Phase = phase;
        TatooineReplication.SetSiegeFortress(EntityId.Null);
        Volatile.Write(ref _fortGuard, null);
        _fortAttackers.Clear();
        foreach (var raw in new[] { c.Fortress, c.LairA, c.LairB })
        {
            if (raw != 0 && tx.TryOpen(EntityId.FromRawValue((ulong)raw), out _))
            {
                tx.Destroy(EntityId.FromRawValue((ulong)raw));
            }
        }

        structures = true;
        _hordeLairs.Clear();
        Volatile.Write(ref _hordeRoute, null);
        tx.OpenMut(_horde).Set(HordeArch.Core, c);
        foreach (var (character, (_, r)) in _responders)
        {
            if (r.State != ResponderState.Decayed)
            {
                var (rx, rz) = RallyOf(in c);
                _rallies.Add((character, new HordeRally { Event = _horde.RawValue, X = rx, Z = rz, State = 0 }));
            }
        }

        // Its answers go with it: nothing reads an event's answers once it is over, and each kept one was a row for good. The map stays, for the report.
        foreach (var (_, (row, _)) in _responders)
        {
            if (!row.IsNull && tx.TryOpen(row, out _))
            {
                tx.Destroy(row);
            }
        }

        _hordeLastReport = HordeJson(now);
        Console.WriteLine($"  horde: {HordeCityName(c.City)} — {HordePhase.NameOf(phase)}: {_hordeLastReport}");
        _hordeStatusDue = true;
    }

    /// <summary>An answer to the call (horde.md § 5): committed at once, capped, persisted; the rally point told after the commit.</summary>
    private void Answer(Transaction tx, SubscriptionsCommands subs, in ClientCommand<AnswerCall> command, long now)
    {
        if (_horde.IsNull || HordePhase.IsOver(_hordeCore.Phase) || command.Value.Event != _horde.RawValue)
        {
            RefuseHorde(subs, in command, TatooineReplication.HordeNone, 0);
            return;
        }

        if (!TatooineReplication.TryBodyOfSession(command.Session, out var body, out var characterId))
        {
            RefuseHorde(subs, in command, TatooineReplication.NotInWorld, -1);
            return;
        }

        var character = unchecked((long)characterId.RawValue);
        var hz = Math.Max(1, _config.TickRateHz);
        _responders.TryGetValue(character, out var held);
        if (!held.Row.IsNull && held.R.State is ResponderState.Committed or ResponderState.Arrived)
        {
            // Told its gather point again: a session that reconnected (a page reloaded, a bot restarted) holds a commitment it was told of on another one.
            RefuseHorde(subs, in command, TatooineReplication.HordeAnswered, 1);
            var (againX, againZ) = RallyOf(in _hordeCore);
            _rallies.Add((character, new HordeRally { Event = _horde.RawValue, X = againX, Z = againZ, State = held.R.State }));
            return;
        }

        if (!held.Row.IsNull && held.R.State == ResponderState.Decayed && now < held.R.CooldownUntilTick)
        {
            RefuseHorde(subs, in command, TatooineReplication.HordeCooldown, 2);
            return;
        }

        if (_hordeCommitted >= _hordeCore.TargetDefenders)
        {
            RefuseHorde(subs, in command, TatooineReplication.HordeEnough, 3);
            return;
        }

        // Its own window to arrive, from where it answered (horde.md § 5): a body not found is given the event's whole timeout.
        var (rallyAtX, rallyAtZ) = RallyOf(in _hordeCore);
        var window = _hordeCore.CommitTimeoutS;
        // Indoors a body's coordinates are its room's, which say nothing of the way to the gather point: the whole timeout.
        if (tx.TryOpen(body, out var answering) && answering.Read(Player.Realm).Value == 0)
        {
            var at = answering.Read(Player.Bounds);
            window = AnswerWindowS(MathF.Sqrt(((at.X - rallyAtX) * (at.X - rallyAtX)) + ((at.Z - rallyAtZ) * (at.Z - rallyAtZ))), _hordeCore.CommitTimeoutS);
        }

        var r = new HordeResponder
        {
            Event = (long)_horde.RawValue,
            Character = character,
            CommittedTick = now,
            DeadlineTick = now + (window * (long)hz),
            State = ResponderState.Committed,
        };
        var row = held.Row;
        if (row.IsNull)
        {
            row = tx.Spawn<HordeResponderArch>(HordeResponderArch.Core.Set(in r));
        }
        else
        {
            tx.OpenMut(row).Set(HordeResponderArch.Core, r);
        }

        _responders[character] = (row, r);
        _hordeCommitted++;
        _hordeStatusDue = true;
        var (rallyX, rallyZ) = RallyOf(in _hordeCore);
        _rallies.Add((character, new HordeRally { Event = _horde.RawValue, X = rallyX, Z = rallyZ, State = ResponderState.Committed }));
    }

    private void RefuseHorde(SubscriptionsCommands subs, in ClientCommand<AnswerCall> command, byte reason, int slot)
    {
        subs.Reject(command, reason);
        if (slot >= 0)
        {
            Interlocked.Increment(ref _hordeRefusedBy[slot]);
        }
    }

    // The committed defenders by what they are doing, this pass: down, riding, targeting, moving, idle.
    private readonly int[] _defenderStates = new int[5];

    // A responder's body not found since this tick: gone offline (in memory: a restart gives everyone a fresh look).
    private readonly Dictionary<long, long> _responderAbsentSince = [];

    // A committed responder's way to the fight: the nearest it has been, and when it last came HordeProgressM nearer (in memory, as the absence is).
    private readonly Dictionary<long, (float Best, long At)> _responderProgress = [];

    /// <summary>
    /// A committed defender past its window does not decay while it keeps coming: this much nearer the fight than it has ever been, within
    /// <see cref="HordeProgressS"/> (Loïc, 2026-11-05: in the second 4K raid 1 688 answers decayed late, most of them on their way).
    /// </summary>
    internal const float HordeProgressM = 20f;

    internal const int HordeProgressS = 60;

    // The responders whose state this pass changes, applied after the walk.
    private readonly List<(long Character, HordeResponder R)> _responderChanges = [];

    // Whether a committed responder is still on its way: it came HordeProgressM nearer the fight within the last HordeProgressS.
    private bool Coming(long character, long now, int hz)
        => _responderProgress.TryGetValue(character, out var way) && now - way.At <= HordeProgressS * (long)hz;

    /// <summary>Once a second: who has arrived, who decays (late, or downed far away), and the committed count the cap reads.</summary>
    private void Responders(Transaction tx, long now, int hz)
    {
        ref var c = ref _hordeCore;
        var (rallyX, rallyZ) = RallyOf(in c);
        TatooineReplication.FillCharacterBodies(_hordeBodies);
        _responderChanges.Clear();
        var committed = 0;
        Array.Clear(_defenderStates);
        var fighting = c.Phase == HordePhase.Active;
        foreach (var (character, (_, r)) in _responders)
        {
            if (r.State == ResponderState.Decayed)
            {
                continue;
            }

            var far = false;
            var downed = false;
            var near = false;
            var online = false;
            if (_hordeBodies.TryGetValue(EntityId.FromRawValue((ulong)character), out var body) && tx.TryOpen(body, out var player))
            {
                // In the fight is at the gather point OR along the raiders' route (review, 2026-11-04): a defender who meets them on the way, or follows them
                // into the town, is where the call asked it to be — measured from the gather point alone, it decayed as soon as it chased one.
                var at = player.Read(Player.Bounds);
                var dx = at.X - rallyX;
                var dz = at.Z - rallyZ;
                // The route only once the raid is on: before it, a townsman standing by the road through its own town had arrived without a step.
                // In the fight: at the gather point, or — once the raid is on — on the raiders' route, where a defender meets them or follows them into
                // the town (the first live siege: 400 commitments timed out while their bots fought in the town, never passing the gather point).
                var toRally = (dx * dx) + (dz * dz);
                var toFight = fighting ? MathF.Min(toRally, RouteDistanceSq(at.X, at.Z)) : toRally;
                near = toFight <= HordeRallyM * HordeRallyM;
                far = toFight > HordeDownedFarM * HordeDownedFarM;
                downed = player.Read(Player.Vitals).Health <= 0;

                // Its way to the fight: a fresh best when it comes HordeProgressM nearer; downed, it starts again from where it wakes.
                var dist = MathF.Sqrt(toFight);
                // First seen, or woken: where it starts from — no progress yet.
                if (downed || !_responderProgress.TryGetValue(character, out var way))
                {
                    _responderProgress[character] = (dist, long.MinValue / 2);
                }
                else if (dist < way.Best - HordeProgressM)
                {
                    _responderProgress[character] = (dist, now);
                }

                // What the defenders are doing (the event's metrics): down, riding, shooting at something, moving, or none of these — idle.
                if (r.State != ResponderState.Decayed)
                {
                    var move = player.Read(Player.Move);
                    var targeting = !player.Read(Player.Session).Target.IsNull;
                    var riding = player.Read(Player.State).Mounted != 0;
                    var moving = move.VelX != 0f || move.VelZ != 0f;
                    _defenderStates[downed ? 0 : riding ? 1 : targeting ? 2 : moving ? 3 : 4]++;
                }
                online = true;
                _responderAbsentSince.Remove(character);
            }
            else if (!_responderAbsentSince.ContainsKey(character))
            {
                _responderAbsentSince[character] = now;
            }

            // Gone offline for longer than an answer has to arrive: its place goes to someone who is here (review, 2026-11-04 — an absent Arrived
            // responder held its slot for the whole event).
            // Offline for the shortest window an answer has (a bot restarted is back within a minute): within an answer's own window, or a committed one
            // that went offline decayed as late before it could as offline (review, 2026-11-05).
            var absent = !online && _responderAbsentSince.TryGetValue(character, out var since)
                && now - since > Math.Min(c.CommitTimeoutS, HordeMinAnswerS) * (long)hz;

            // Its own window (horde.md § 5); an answer written before there was one has the event's timeout.
            var deadline = r.DeadlineTick != 0 ? r.DeadlineTick : r.CommittedTick + (c.CommitTimeoutS * (long)hz);
            var next = r;
            if (r.State == ResponderState.Committed && near)
            {
                // Back in the fight. Its first arrival is the one the report times; a defender coming back from its clone is not a second answer.
                next.State = ResponderState.Arrived;
                if (r.ArrivedTick == 0)
                {
                    _hordeArrivalS.Add((int)((now - r.CommittedTick) / hz));
                }

                next.ArrivedTick = now;
            }
            else if (absent || (r.State == ResponderState.Committed && now > deadline && !Coming(character, now, hz)))
            {
                next.State = ResponderState.Decayed;
                next.CooldownUntilTick = now + (c.CooldownS * (long)hz);
                _hordeDecays++;
                if (absent)
                {
                    _hordeDecayOffline++;
                }
                else
                {
                    _hordeDecayLate++;
                }
            }
            else if (r.State == ResponderState.Arrived && far)
            {
                // Away from the fight — downed out there, or woken at its clone (Loïc, 2026-11-05): committed again, with the event's timeout to come
                // back, rather than out of the count at once. Being downed on the way is no reason to decay: the deadline still holds a Committed one.
                next.State = ResponderState.Committed;
                next.DeadlineTick = now + (c.CommitTimeoutS * (long)hz);
                _hordeRecommitted++;
            }

            if (next.State != r.State)
            {
                _responderChanges.Add((character, next));
            }

            committed += next.State == ResponderState.Decayed ? 0 : 1;
        }

        foreach (var (character, next) in _responderChanges)
        {
            var row = _responders[character].Row;
            tx.OpenMut(row).Set(HordeResponderArch.Core, next);
            _responders[character] = (row, next);
            _rallies.Add((character, new HordeRally { Event = _horde.RawValue, X = rallyX, Z = rallyZ, State = next.State }));
        }

        _hordeCommitted = committed;
    }

    /// <summary>Players standing at the rally point: session bodies only (the scripted crowd is a <c>Player</c> too), alive.</summary>
    private void CountPresent(Transaction tx)
    {
        _hordeBodySet.Clear();
        foreach (var (_, body) in _hordeBodies)
        {
            _hordeBodySet.Add(body);
        }

        var (rallyX, rallyZ) = RallyOf(in _hordeCore);
        var sphere = new BSphere2F { CenterX = rallyX, CenterY = rallyZ, Radius = HordeRallyM };
        var e = Dbe.ClusterSpatialQuery<Player>(new RealmId(0)).Radius(in sphere);
        var present = 0;
        try
        {
            while (e.MoveNext())
            {
                var hit = e.Current;
                if (_hordeBodySet.Contains(hit.Entity) && tx.TryOpen(hit.Entity, out var player) && player.Read(Player.Vitals).Health > 0)
                {
                    present++;
                }
            }
        }
        finally
        {
            e.Dispose();
        }

        _hordePresent = present;
    }

    /// <summary>The raiders' modes and their distance to the target: whether the horde marches, is held up fighting, or stands still.</summary>
    private void SurveyRaiders(Transaction tx, in HordeCore c)
    {
        Array.Clear(_raidersByMode);
        _raidersInTown.Clear();
        _radarCells.Clear();
        _raidersLiving = 0;
        var index = PlanetIndexes?[0] ?? _index;
        var townReach = c.City < index.Cities.Count ? index.Cities[c.City].Radius + CityPath.CityMarginM : 0f;
        var nearest = float.MaxValue;
        double sum = 0;
        var n = 0;
        foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
        {
            var ai = cluster.GetReadOnlySpan(Creature.Ai);
            var at = cluster.GetReadOnlySpan(Creature.Bounds);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                if (ai[slot].March == 0)
                {
                    continue;
                }

                _raidersByMode[Math.Clamp(ai[slot].Mode, 0, _raidersByMode.Length - 1)]++;
                var dx = at[slot].X - c.TargetX;
                var dz = at[slot].Z - c.TargetZ;
                var d = MathF.Sqrt((dx * dx) + (dz * dz));
                if (d <= townReach && ai[slot].Mode != AiMode.Dead)
                {
                    _raidersInTown.Add((cluster.GetEntityId(slot), at[slot].X, at[slot].Z, ai[slot].Level));
                }

                if (ai[slot].Mode != AiMode.Dead)
                {
                    RadarCount(at[slot].X, at[slot].Z);
                    _raidersLiving++;
                }
                nearest = MathF.Min(nearest, d);
                sum += d;
                n++;
            }
        }

        _raiderNearestM = n == 0 ? 0f : nearest;
        _raiderMeanM = n == 0 ? 0f : (float)(sum / n);
    }

    /// <summary>
    /// Whether the defence slows the horde (Loïc, 2026-11-04: "help needed should be dynamically raised if the progression of the horde toward the city is not
    /// slown down"): over the last <see cref="HordeAdvanceWindowS"/>, the raiders on their way — farther than <see cref="HordeArriveM"/> at the window's
    /// start, alive at both ends — closed on the target by more than <see cref="HordeUnslowedShare"/> of what their speed would have walked them unopposed.
    /// Then the attendance asked for grows by a quarter, at least five, so the help needed goes up. It is never lowered.
    /// </summary>
    /// <returns>Whether the attendance was raised: the core is dirty.</returns>
    private bool JudgeAdvance(Transaction tx, ref HordeCore c)
    {
        _raiderNowM.Clear();
        double made = 0;
        double could = 0;
        foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
        {
            var ai = cluster.GetReadOnlySpan(Creature.Ai);
            var at = cluster.GetReadOnlySpan(Creature.Bounds);
            var move = cluster.GetReadOnlySpan(Creature.Move);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                if (ai[slot].March != RaiderMarches || ai[slot].Mode == AiMode.Dead)
                {
                    continue;
                }

                var id = cluster.GetEntityId(slot);
                var dx = at[slot].X - c.TargetX;
                var dz = at[slot].Z - c.TargetZ;
                var d = MathF.Sqrt((dx * dx) + (dz * dz));
                _raiderNowM[id] = d;
                if (_raiderLastM.TryGetValue(id, out var before) && before > HordeArriveM)
                {
                    made += before - d;
                    could += Math.Min(before - HordeArriveM, move[slot].SpeedMps * HordeAdvanceWindowS);
                }
            }
        }

        (_raiderLastM, _raiderNowM) = (_raiderNowM, _raiderLastM);
        _hordeAdvance = could > 0 ? (float)(made / could) : 0f;
        var unslowed = could > 0 && _hordeAdvance > HordeUnslowedShare;

        // The fortress not falling (Loïc, 2026-11-04: ask for more players if it is not damaged quickly enough): under HordeFortressPaceShare of its whole
        // since the last judgement, the defence is not breaking it.
        var fortLost = _fortHpJudged > 0 ? _fortHpJudged - c.FortHp : int.MaxValue;
        var standing = c.FortMaxHp > 0 && c.FortHp > 0 && fortLost < c.FortMaxHp * HordeFortressPaceShare;
        _fortHpJudged = c.FortHp;

        // Raised only when the attendance is what holds the defence back: with places still open, more asked for helps nobody (review, 2026-11-04).
        if (!(unslowed || standing) || c.TargetDefenders >= HordeMaxTargetDefenders || _hordeCommitted < c.TargetDefenders)
        {
            return false;
        }

        var was = c.TargetDefenders;
        c.TargetDefenders = Math.Min(HordeMaxTargetDefenders, was + Math.Max(5, (was + 3) / 4));

        // The fortress is three minutes of the assault, which grows with the attendance: its pool too, the share it has left kept (review, 2026-11-05).
        if (c.FortMaxHp > 0 && was > 0)
        {
            var max = (int)Math.Min(int.MaxValue, (long)c.FortMaxHp * c.TargetDefenders / was);
            c.FortHp = (int)Math.Min(max, (long)c.FortHp * max / c.FortMaxHp);
            c.FortMaxHp = max;
        }
        _hordeRaises++;
        _hordeStatusDue = true;
        var why = unslowed ? FormattableString.Invariant($"the raiders made {_hordeAdvance:P0} of an unopposed march") : "the fortress is not falling";
        Console.WriteLine($"  horde: {why} — {was} defenders asked for is not enough, now {c.TargetDefenders}");
        return true;
    }

    /// <summary>Raiders within <see cref="HordeRallyM"/> of the target: the horde has reached the town.</summary>
    private int CountRaidersAtTarget(Transaction tx, in HordeCore c)
    {
        // Raiders, living ones (review, 2026-11-04: every creature in the sphere was counted, corpses and the desert's alike).
        var sphere = new BSphere2F { CenterX = c.TargetX, CenterY = c.TargetZ, Radius = HordeRallyM };
        var e = Dbe.ClusterSpatialQuery<Creature>(new RealmId(0)).Radius(in sphere);
        var n = 0;
        try
        {
            while (e.MoveNext())
            {
                if (tx.TryOpen(e.Current.Entity, out var creature) && creature.Read(Creature.Ai) is { March: not 0, Mode: not AiMode.Dead })
                {
                    n++;
                }
            }
        }
        finally
        {
            e.Dispose();
        }

        return n;
    }

    /// <summary>
    /// Sends the status when it changed enough (horde.md § 4): the phase at once; otherwise at most once a second, when the help needed moved by a 64th or
    /// a count by 5 %. A steady event sends nothing.
    /// </summary>
    private void MaybeAnnounce(TickContext ctx, long now, int hz)
    {
        if (_horde.IsNull)
        {
            return;
        }

        var status = CurrentHordeStatus(now, hz);
        var phaseChanged = status.Phase != _lastStatus.Phase || status.Event != _lastStatus.Event;
        if (!phaseChanged && now - _lastStatusTick < Math.Max(1, hz) * HordeStatusMinSeconds)
        {
            return;
        }

        var moved = Math.Abs(status.HelpNeeded - _lastStatus.HelpNeeded) >= 4
            || Moved(status.RaidersAlive, _lastStatus.RaidersAlive)
            || Moved(status.Committed, _lastStatus.Committed)
            || Moved(status.Present, _lastStatus.Present)
            || Math.Abs(status.HallHp - _lastStatus.HallHp) >= 3
            || Math.Abs(status.FortHp - _lastStatus.FortHp) >= 3;
        if (!phaseChanged && !moved && !_hordeStatusDue)
        {
            return;
        }

        TatooineReplication.Announce(ctx, in status);
        _lastStatus = status;
        _lastStatusTick = now;
        _hordeStatusDue = false;
    }

    /// <summary>A hit-point pool as the wire carries it: 1 + 254ths of its whole, 1 broken; 0 for a pool that is not there (0 means no data).</summary>
    internal static byte Fraction(int hp, int max) => max <= 0 ? (byte)0 : hp <= 0 ? (byte)1 : (byte)(2 + Math.Clamp((long)hp * 253 / max, 0, 253));

    private static bool Moved(uint now, uint before) => now != before && Math.Abs((long)now - before) * 20 >= Math.Max(1L, before);

    /// <summary>The running (or last) horde's status, as sent and as <c>GET /swg/horde</c> gives it.</summary>
    internal HordeStatus CurrentHordeStatus(long now, int hz)
    {
        ref readonly var c = ref _hordeCore;
        var help = c.TargetDefenders <= 0 ? 255 : (int)Math.Clamp(_hordeCommitted * 255L / c.TargetDefenders, 0, 255);
        return new HordeStatus
        {
            Realm = 0,
            Event = _horde.RawValue,
            Phase = c.Phase,
            City = (byte)c.City,
            HelpNeeded = (byte)help,
            StartsInS = c.Phase == HordePhase.Incoming ? (uint)Math.Max(0, (c.StartTick - now) / Math.Max(1, hz)) : 0u,
            RaidersAlive = (uint)Math.Max(0, _hordeRaidersAlive),
            Committed = (uint)Math.Max(0, _hordeCommitted),
            Present = (uint)Math.Max(0, _hordePresent),
            // The point the call asks everyone to: the rally point (the wire's `x`/`z`; horde.md § 4).
            TargetX = RallyOf(in c).X,
            TargetZ = RallyOf(in c).Z,
            FortX = c.FortX,
            FortZ = c.FortZ,
            HallX = c.HallX,
            HallZ = c.HallZ,
            HallHp = Fraction(c.HallHp, c.HallMaxHp),
            FortHp = Fraction(c.FortHp, c.FortMaxHp),
        };
    }

    private void ResetHordeMetrics()
    {
        _responderAbsentSince.Clear();
        _responderProgress.Clear();
        _hordeRaidersKilled = 0;
        _hordeRaidersSpawned = 0;
        _hordePresent = 0;
        _hordeCommitted = 0;
        _hordeRaidersAlive = 0;
        _hordeRaidersAtTarget = 0;
        _hordeDecays = 0;
        _hordeDecayLate = 0;
        _hordeDecayOffline = 0;
        _hordeRecommitted = 0;
        _hordeArrivalS.Clear();
        _hallSiegers = 0;
        _hallDamage = 0;
        _fortDamage = 0;
        _radarDueTick = 0;
        Array.Clear(_hordeRefusedBy);
        _raiderLastM.Clear();
        _advanceJudgedTick = 0;
        _fortHpJudged = 0;
        _hordeAdvance = 0f;
        _hordeRaises = 0;
    }

    /// <summary>The running (or last) horde as JSON: the event, its gameplay counts and its live metrics (horde.md § 8). Null without one.</summary>
    private string HordeJson(long now)
    {
        if (_horde.IsNull)
        {
            return _hordeLastReport;
        }

        ref readonly var c = ref _hordeCore;
        var hz = Math.Max(1, _config.TickRateHz);
        var arrivals = _hordeArrivalS.ToArray();
        Array.Sort(arrivals);
        int Pct(double p) => arrivals.Length == 0 ? 0 : arrivals[Math.Min(arrivals.Length - 1, (int)(p * arrivals.Length))];
        var j = new StringBuilder(512);
        j.Append(CultureInfo.InvariantCulture,
            $"{{\"event\":\"{_horde.RawValue}\",\"city\":\"{HordeCityName(c.City)}\",\"phase\":\"{HordePhase.NameOf(c.Phase)}\"");
        j.Append(CultureInfo.InvariantCulture,
            $",\"startsInS\":{Math.Max(0, (c.StartTick - now) / hz)},\"endsInS\":{Math.Max(0, (c.EndTick - now) / hz)}");
        j.Append(CultureInfo.InvariantCulture,
            $",\"fortress\":[{c.FortX:F1},{c.FortZ:F1}],\"target\":[{c.TargetX:F1},{c.TargetZ:F1}]");
        j.Append(CultureInfo.InvariantCulture,
            $",\"waves\":{{\"spawned\":{c.WavesSpawned},\"of\":{c.Waves},\"size\":{c.WaveSize},\"periodS\":{c.WavePeriodTicks / hz},\"respawn\":{(c.Respawn != 0 ? "true" : "false")},\"level\":{c.Level},\"healthPct\":{(c.RaiderHealthPct == 0 ? 100 : c.RaiderHealthPct)},\"hitPct\":{(c.RaiderHitPct == 0 ? 100 : c.RaiderHitPct)}}}");
        j.Append(CultureInfo.InvariantCulture,
            $",\"raiders\":{{\"spawned\":{c.Spawned},\"alive\":{_hordeRaidersAlive},\"killed\":{c.Killed},\"atTarget\":{_hordeRaidersAtTarget}");
        j.Append(CultureInfo.InvariantCulture, $",\"inTown\":{_raidersInTown.Count}");
        j.Append(CultureInfo.InvariantCulture,
            $",\"nearestM\":{_raiderNearestM:F0},\"meanM\":{_raiderMeanM:F0},\"byMode\":{{\"idle\":{_raidersByMode[AiMode.Idle]},\"wander\":{_raidersByMode[AiMode.Wander]},\"pursue\":{_raidersByMode[AiMode.Pursue]},\"fighting\":{_raidersByMode[AiMode.Fighting]},\"leashing\":{_raidersByMode[AiMode.Leashing]},\"dead\":{_raidersByMode[AiMode.Dead]}}}}}");
        j.Append(CultureInfo.InvariantCulture,
            $",\"defenders\":{{\"target\":{c.TargetDefenders},\"raises\":{_hordeRaises},\"advance\":{_hordeAdvance:F2},\"committed\":{_hordeCommitted},\"present\":{_hordePresent},\"arrived\":{arrivals.Length},\"decayed\":{_hordeDecays},\"decay\":{{\"late\":{_hordeDecayLate},\"offline\":{_hordeDecayOffline}}},\"recommitted\":{_hordeRecommitted},\"answers\":{_responders.Count}");
        j.Append(CultureInfo.InvariantCulture,
            $",\"doing\":{{\"down\":{_defenderStates[0]},\"riding\":{_defenderStates[1]},\"targeting\":{_defenderStates[2]},\"moving\":{_defenderStates[3]},\"idle\":{_defenderStates[4]}}}");
        j.Append(CultureInfo.InvariantCulture,
            $",\"refused\":{{\"none\":{_hordeRefusedBy[0]},\"answered\":{_hordeRefusedBy[1]},\"cooldown\":{_hordeRefusedBy[2]},\"enough\":{_hordeRefusedBy[3]}}}");
        j.Append(CultureInfo.InvariantCulture, $",\"arrivalS\":{{\"p50\":{Pct(0.5)},\"p90\":{Pct(0.9)}}}}}");
        j.Append(CultureInfo.InvariantCulture,
            $",\"siege\":{{\"hall\":{c.HallHp},\"hallMax\":{c.HallMaxHp},\"fort\":{c.FortHp},\"fortMax\":{c.FortMaxHp},\"hallSiegers\":{_hallSiegers},\"hallDamage\":{_hallDamage},\"fortDamage\":{_fortDamage},\"fortAttackers\":{_fortAttackers.Count}}}");
        j.Append(CultureInfo.InvariantCulture,
            $",\"raiderContacts\":{RaiderContacts},\"creatureRetargets\":{CreatureRetargets},\"statusesSent\":{TatooineReplication.HordeStatusesSent},\"combatOverflow\":{CombatQueue?.OverflowCount ?? 0}}}");
        return j.ToString();
    }
}
