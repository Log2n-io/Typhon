using System;

namespace SwgTatooine.Bots.Brain;

/// <summary>
/// A horde's call (horde.md § 6): a bot hears the status every session hears, answers it by a roll — rarer the less help is needed and the farther it is
/// — and, once the server has counted it, goes to the rally point by whatever way its route takes (walk, swoop, shuttle) and fights the raiders there,
/// at any level, until the event is over or its commitment decays.
/// </summary>
/// <remarks>
/// <b>The server caps the answers, not the bots</b> (D2): a bot that answers past the attendance asked for is refused "enough" and simply does not go.
/// The roll keeps the refusals few, not the count right.
/// </remarks>
public sealed partial class BotBrain
{
    /// <summary>The chance, in percent per status heard, that a willing bot with all help still needed, beside the target, answers.</summary>
    public const int AnswerPercent = 10;

    /// <summary>Bots that would ever answer, in percent: the rest never do, as players who are busy.</summary>
    public const int WillingPercent = 70;

    /// <summary>Beyond this from the rally point, the bot travels to its city; within it, it walks to the point and fights.</summary>
    public const double RallyTravelM = 300d;

    /// <summary>A raider within this of a defending bot is fought, whatever its level.</summary>
    public const double DefendNoticeM = 60d;

    /// <summary>Close enough to the rally point to stand there.</summary>
    public const double RallyStandM = 12d;

    /// <summary>An answer with no rally point back in this long was refused: the bot may roll again after <see cref="AnswerRetrySeconds"/>.</summary>
    public const double AnswerRetrySeconds = 60d;

    /// <summary>Statuses heard, answers sent, rallies taken (committed), arrivals at the point, commitments ended (over or decayed).</summary>
    public long HordeHeard { get; private set; }

    public long HordeAnswered { get; private set; }

    public long RalliesTaken { get; private set; }

    public long RalliesArrived { get; private set; }

    public long RalliesEnded { get; private set; }

    private ulong _answeredEvent;
    private long _answeredAt;
    private ulong _rallyEvent;
    private int _rallyState;
    private float _rallyX;
    private float _rallyZ;
    private int _rallyCity = -1;
    private bool _defending;
    private bool _rallyArrived;

    // The running horde as the last status told it (horde.md § 10): its city, its town hall and how whole it is, the fortress and how whole it is.
    private ulong _hordeEvent;
    private int _hordePhase;
    private int _hordeCity = -1;
    private float _hallX;
    private float _hallZ;
    private int _hallHp;
    private int _hallHpBefore;
    private long _hallHitAt;
    private int _fortHp;
    private float _fortX;
    private float _fortZ;

    // The radar's clusters, last heard (horde.md § 10).
    private float[] _radarX = [];
    private float[] _radarZ = [];
    private int[] _radarN = [];

    // Where a defender is walking to when it seeks the fight (a cluster, the town hall, the fortress); 0 for none.
    private float _seekX;
    private float _seekZ;
    private bool _seeking;

    /// <summary>A defender seeks a radar cluster this close, metres; farther ones are someone else's.</summary>
    public const double RadarSeekM = 900d;

    /// <summary>A bot in the horde's city, or a defender this close to the town hall, goes to it while it is struck (Loïc, 2026-11-04: protect it at all cost).</summary>
    public const double HallDefendM = 1500d;

    /// <summary>The town hall counts as under attack this long after its hit points last fell, seconds.</summary>
    public const double HallAlarmSeconds = 45d;

    /// <summary>
    /// How close to the town hall's centre a defender goes, metres: past its half-footprint (17.8 m) and on its doorstep — a point inside the walls is no
    /// walk at all (review, 2026-11-04).
    /// </summary>
    public const double HallReachM = 26d;

    /// <summary>A failed walk is planned again after this long, seconds: not every tick (review, 2026-11-04).</summary>
    public const double WalkReplanSeconds = 2d;

    private long _walkPlanAt;

    // A defender's trip to arm itself (Rally): the bazaar visit under way is that one.
    private bool _gearRun;

    /// <summary>Defenders that left the fight to buy a weapon, having none (Loïc, 2026-11-04).</summary>
    public long GearRuns { get; private set; }

    /// <summary>A defender in view of a raider this close hunts it, metres: the whole of what it sees, nearly.</summary>
    public const double RaiderSeekM = 180d;

    /// <summary>The share of defenders that assault the fortress when there is nobody to fight, in percent, by their seed.</summary>
    public const int AssaultPercent = 30;

    /// <summary>An assaulting defender shoots the fortress from this far from its centre: its footprint's half and a pistol's reach.</summary>
    public const double AssaultStandM = 40d;

    /// <summary>Whether this bot would ever answer: a fixed draw of its own seed, so a fleet's willing share is the same in every run.</summary>
    private bool Willing => _seed % 100u < WillingPercent;

    /// <summary>
    /// A status heard: whether to answer it now. Only a horde announced or under way, with help still needed, not answered already (or answered long enough
    /// ago without a rally point back), and only on a roll that falls with the help already found and with the distance to the target.
    /// </summary>
    public bool ShouldAnswer(ulong hordeEvent, int phase, int help, float targetX, float targetZ, float x, float z, long tick)
    {
        HordeHeard++;
        if (hordeEvent == 0 || phase is not (1 or 2) || help >= 255 || !Willing)
        {
            return false;
        }

        if (_rallyEvent == hordeEvent && _rallyState is 1 or 2)
        {
            return false;
        }

        if (_answeredEvent == hordeEvent && tick - _answeredAt < Ticks(AnswerRetrySeconds))
        {
            return false;
        }

        var distance = Routing.Distance(x, z, targetX, targetZ);
        var chance = AnswerPercent * ((255 - help) / 255d) / (1d + (distance / 3000d));
        if (Next() % 10_000u >= (uint)(chance * 100d))
        {
            return false;
        }

        _answeredEvent = hordeEvent;
        _answeredAt = tick;
        HordeAnswered++;
        return true;
    }

    /// <summary>The horde's status (<c>HordeStatus</c>): its city, the town hall and the fortress, and whether the town hall is being struck.</summary>
    public void HearHorde(ulong hordeEvent, int phase, int city, float hallX, float hallZ, int hallHp, int fortHp, long tick, float fortX = 0f, float fortZ = 0f)
    {
        _fortX = fortX;
        _fortZ = fortZ;
        if (hordeEvent != _hordeEvent)
        {
            _hallHpBefore = 0;
        }

        _hordeEvent = hordeEvent;
        _hordePhase = phase;
        _hordeCity = phase is 1 or 2 ? city : -1;

        // The event over is the call over, whatever the last rally said: a stand-down lost or overtaken left 571 bots holding a rally of an event that had
        // ended, round the last raiders' blips beside the town hall (Loïc, 2026-11-05).
        if (phase is not (1 or 2))
        {
            ClearRadar();
            if (_rallyEvent == hordeEvent && _rallyState is 1 or 2)
            {
                EndRally(tick);
            }
        }

        _hallX = hallX;
        _hallZ = hallZ;
        if (hallHp != 0 && _hallHpBefore != 0 && hallHp < _hallHpBefore)
        {
            _hallHitAt = tick;
        }

        _hallHp = hallHp;
        _hallHpBefore = hallHp;
        _fortHp = fortHp;
    }

    /// <summary>The horde's radar (<c>HordeRadar</c>): where the raiders are.</summary>
    public void HearRadar(float[] x, float[] z, int[] n)
    {
        _radarX = x;
        _radarZ = z;
        _radarN = n;
    }

    /// <summary>Whether the town hall was struck lately (<see cref="HallAlarmSeconds"/>).</summary>
    private bool HallUnderAttack(long tick) => _hordeCity >= 0 && _hallHp > 1 && _hallHitAt != 0 && tick - _hallHitAt < Ticks(HallAlarmSeconds);

    /// <summary>The server's rally point (<c>HordeRally</c>): committed or arrived, go; decayed, or 0 for an event over, stop.</summary>
    public void HearRally(ulong hordeEvent, float x, float z, int state, long tick)
    {
        if (state is 1 or 2)
        {
            if (_rallyEvent != hordeEvent || _rallyState is not (1 or 2))
            {
                RalliesTaken++;
                _rallyArrived = false;
            }

            _rallyEvent = hordeEvent;
            _rallyState = state;
            _rallyX = x;
            _rallyZ = z;
            _rallyCity = NearestCity(x, z);
            return;
        }

        if (_rallyEvent == hordeEvent && _rallyState is 1 or 2)
        {
            EndRally(tick);
        }
    }

    /// <summary>The call over: back to its day, and the radar forgotten — its blips are where the raiders stood, not where they are.</summary>
    private void EndRally(long tick)
    {
        RalliesEnded++;
        _rallyState = 0;
        _defending = false;
        _seeking = false;
        ClearRadar();
        if (Goal is Goal.Defending or Goal.Hunting)
        {
            Become(Goal.Choosing, GoalWhy.RallyEnded, tick);
        }
    }

    private void ClearRadar()
    {
        _radarX = [];
        _radarZ = [];
        _radarN = [];
    }

    /// <summary>
    /// Answering a call (horde.md § 6, § 10): to the gather point's city by the route's way, then on foot to the point — and once there, never idle while
    /// there is a fight (Loïc, 2026-11-04): a raider in reach first, the town hall while it is struck, a raider in view, the radar's nearest cluster, and for
    /// some the fortress itself; the point only when the raid has nothing near. A bot of the raided town that never answered still runs to its town hall when
    /// it is struck. Null while the goal that gets it there (a journey, a fight) runs, or when there is no horde for it.
    /// </summary>
    private Intent? Rally(in Perceived world, long tick)
    {
        var here = RegionAt(world.X, world.Z);
        if (_rallyState is not (1 or 2))
        {
            // The call over in the middle of a trip to arm: the visit is the day's errand now (review, 2026-11-04).
            _gearRun = false;

            // Not a defender — but a townsman of a town whose hall is struck goes to it (Loïc, 2026-11-04: everybody in the city must protect it).
            if (_hordeCity >= 0 && here == _hordeCity + 1 && HallUnderAttack(tick))
            {
                // A raider in reach first, and a hunt it is in left to run: the hall is where the fight is, not a place to stand while it goes on.
                _defending = true;
                if (Goal == Goal.Hunting)
                {
                    return null;
                }

                if (world.NearestCreature != 0 && world.NearestCreatureM <= DefendNoticeM && world.NearestCreatureRaider)
                {
                    return StartHunt(in world, tick, GoalWhy.RallyFight);
                }

                return Seek(in world, tick, _hallX, _hallZ, HallReachM) ?? Intent.Move(0f, 0);
            }

            _defending = false;
            return null;
        }

        // Down to its fist with a weapon to buy (Loïc, 2026-11-04): the bazaar of the city it stands in — out in the desert, at the gather point, the raided
        // city's — and back to the fight; the errand under way runs to its end, and the call takes it up again once it is over. Not while something is on
        // it: Defend has that, and a run a bite cut short (Defend's flight ends the visit) is taken up again as soon as nothing is (review, 2026-11-04).
        if (Goal == Goal.Visiting && _gearRun)
        {
            return null;
        }

        var resume = _gearRun && world.GearUrgent;
        _gearRun = resume;
        var armsAt = here != 0 ? here : _rallyCity >= 0 ? _rallyCity + 1 : NearestCity(_rallyX, _rallyZ) + 1;
        if (world.GearUrgent && world.Attacker == 0 && (resume || tick >= _marketAt) && _assets != null && BazaarOf(in world, armsAt) is { } bazaar)
        {
            _marketAt = tick + Ticks(MarketRetrySeconds);
            if (StartVisitAt(in world, tick, Routing.BazaarKind, GoalWhy.GearChosen, bazaar))
            {
                _gearRun = true;
                GearRuns++;
                return Visit(in world, tick);
            }
        }

        var distance = Routing.Distance(world.X, world.Z, _rallyX, _rallyZ);
        var city = _rallyCity >= 0 ? _rallyCity : NearestCity(_rallyX, _rallyZ);
        // To the city first only for a long trip — farther from the gather point than the city is, and RallyTravelM more: a gather point out in the desert
        // (a fortress 800 m out) lies past the city's edge, and a defender walking out to it was sent back to the city as it crossed the edge, and out
        // again — 2 000 defenders milling at the edge, one at the gather point (the third 4K raid, 2026-11-05).
        var cityToRally = Routing.Distance(_cities[city].X, _cities[city].Z, _rallyX, _rallyZ);
        if (distance > RallyTravelM && distance > cityToRally + RallyTravelM && here != city + 1 && !_seeking)
        {
            _defending = false;
            // On its way there already, by road or shuttle: let the journey run. Going anywhere else: there instead.
            return Goal is Goal.Travelling or Goal.Waiting && TargetRegion == city + 1 ? null : GoTo(in world, tick, city, here, GoalWhy.RallyChosen);
        }

        // At the fight: it fights for the town now, whatever the level of what comes.
        _defending = true;

        // On foot at the fight (Loïc, 2026-11-04: "most of the bots are on their swoop doing nothing"): the journey's wish to ride ends here, and the swoop
        // step takes it off on the next tick. Until then it stands, rather than let the goal it travelled under wish the swoop back on. But a walk out to
        // a fight still far rides its leg (Loïc, 2026-11-06: "they all take their swoop a few metres, then stand down, and try again"): stopped here
        // every tick it was on, the walk's next plan wished the swoop back, and it got on and off all the way. Off once a raider is in view.
        if (!RidesToTheFight(in world))
        {
            _wantMounted = false;
            if (world.Mounted)
            {
                return Intent.Move(0f, 0);
            }
        }

        // An assault bot leaves a hunt as soon as nothing is on it: a defender's hunt has no end while raiders stand near, and the second live siege's
        // assault went into its first fight and never came out of it (fortress at 99 % after five waves).
        // Armed only: a fist reaches no wall from the stand-off (the garrison siege, 2026-11-04: the assault stood there, its blasters broken).
        var assault = _seed % 100u < AssaultPercent && _hordePhase == 2 && _fortHp > 1 && (_fortX != 0f || _fortZ != 0f) && !world.Unarmed;
        if (Goal == Goal.Hunting && !(assault && world.Attacker == 0))
        {
            return null;
        }

        // 0. The assault's share go for the fortress itself (Loïc, 2026-11-04: break it and the raid is beaten), past the raiders in reach — they answer one
        //    that attacks them (Defend), no more. Raiders swarm round the fortress, and an assault that fought whatever was near never set out (the
        //    second live siege: 280 assault bots, the fortress at 99 %).
        if (assault)
        {
            var fx = world.Fortress != 0 ? world.FortressX : _fortX;
            var fz = world.Fortress != 0 ? world.FortressZ : _fortZ;
            if (world.Fortress == 0 || Routing.Distance(world.X, world.Z, fx, fz) > AssaultStandM)
            {
                return Seek(in world, tick, fx, fz, AssaultStandM - 4d) ?? Intent.Move(0f, 0);
            }

            _seeking = false;
            return Intent.Attack(world.Fortress);
        }

        // Down to its fist with nothing to buy a weapon with: no hunt — a punch in a pack is a flight the next bite (Defend), and a hunt again after it
        // (review, 2026-11-04). It holds the gather point, out of the fight, and answers what comes to it.
        if (world.Unarmed)
        {
            return Seek(in world, tick, _rallyX, _rallyZ, RallyStandM) ?? Intent.Move(0f, 0);
        }

        // 1. A creature in reach — a raider, or whatever else stands in the fight.
        if (world.NearestCreature != 0 && world.NearestCreatureM <= DefendNoticeM && world.NearestCreature != GivenUp(tick))
        {
            return StartHunt(in world, tick, GoalWhy.RallyFight);
        }

        // 2. The town hall struck: there, before anything farther.
        if (HallUnderAttack(tick) && Routing.Distance(world.X, world.Z, _hallX, _hallZ) <= HallDefendM)
        {
            return Seek(in world, tick, _hallX, _hallZ, HallReachM) ?? Intent.Move(0f, 0);
        }

        // 4. A raider in view, farther: go and get it.
        if (world.NearestCreature != 0 && world.NearestCreatureRaider && world.NearestCreatureM <= RaiderSeekM && world.NearestCreature != GivenUp(tick))
        {
            return StartHunt(in world, tick, GoalWhy.RallyFight);
        }

        // 5. The radar: the nearest cluster within reach — the one it is already walking to while a cluster still stands there (no ping-pong between
        //    two cells at the same distance, review 2026-11-04).
        if (_seeking && BlipNear(_seekX, _seekZ, 60d))
        {
            return Seek(in world, tick, _seekX, _seekZ, 15d) ?? Intent.Move(0f, 0);
        }

        if (NearestBlip(world.X, world.Z, out var bx, out var bz))
        {
            return Seek(in world, tick, bx, bz, 15d) ?? Intent.Move(0f, 0);
        }

        // 6. Nothing near, nothing on the radar: the gather point.
        _seeking = false;
        if (distance <= RallyStandM)
        {
            if (!_rallyArrived)
            {
                _rallyArrived = true;
                RalliesArrived++;
            }

            if (Goal != Goal.Defending)
            {
                Become(Goal.Defending, GoalWhy.RallyArrived, tick);

                // Stopped where it stands: no intent at all would keep the body on its last course (review, 2026-11-04).
                return Intent.Move(0f, 0);
            }

            return Intent.None;
        }

        return Walk(in world, tick, _rallyX, _rallyZ, RallyStandM);
    }

    /// <summary>A hunt on the nearest creature, as the defender's: no timeout while it defends (Hunt's).</summary>
    private Intent StartHunt(in Perceived world, long tick, GoalWhy why)
    {
        _seeking = false;
        Become(Goal.Hunting, why, tick, world.NearestCreature, world.NearestCreatureM);
        _huntUntil = tick + Ticks(HuntSeconds);
        _hunted = world.NearestCreature;
        return Hunt(in world, tick);
    }

    /// <summary>On foot toward a point to fight there: re-planned when the point moves on; null once within <paramref name="reach"/>.</summary>
    private Intent? Seek(in Perceived world, long tick, float x, float z, double reach)
    {
        if (Routing.Distance(world.X, world.Z, x, z) <= reach)
        {
            _seeking = false;
            return null;
        }

        if (!_seeking || Routing.Distance(_seekX, _seekZ, x, z) > 30d)
        {
            _seeking = true;
            _seekX = x;
            _seekZ = z;
            _leg.Clear();
            _walkPlanAt = 0;
        }

        return Walk(in world, tick, x, z, reach);
    }

    /// <summary>A walk toward a point on foot, as the rally's: a planned leg round the walls, or straight at it when none plans.</summary>
    private Intent Walk(in Perceived world, long tick, float x, float z, double reach)
    {
        if ((Goal != Goal.Defending || !_leg.Active) && tick >= _walkPlanAt)
        {
            // Off for a walk, unless it rides out to a far fight (RidesToTheFight): a plan that found no way is ridden straight at it.
            _wantMounted = RidesToTheFight(in world);
            _walkPlanAt = tick + Ticks(WalkReplanSeconds);
            var planned = PlanWalk(in world, (x, z), [], tick);
            if (planned)
            {
                _leg.FinalReach = (float)reach;
            }

            if (Goal != Goal.Defending)
            {
                Become(Goal.Defending, GoalWhy.RallyChosen, tick);
            }

            if (!planned)
            {
                return Intent.MoveTo(x, z);
            }
        }

        if (!_leg.Active)
        {
            // Between two plans of a walk that found no way: straight at it, as the last plan said, without re-sending the same order every tick.
            return Intent.None;
        }

        var step = Follow(in world, tick, out var end);
        return end == LegEnd.Going ? step : Intent.MoveTo(x, z);
    }

    /// <summary>
    /// Whether a defender on its swoop rides on — arriving from its journey, or on a walk out — : what it walks to — the radar point it seeks, else the gather point — is a ride away (the walk's own rule,
    /// <see cref="Routing.RideOverSeconds"/> at a run), its leg not off for its end, and no creature within <see cref="RaiderSeekM"/>: the fight in view is
    /// fought on foot. Not the leg's ride alone: a radar point moving re-plans the walk, and a leg cleared for a tick got it off again.
    /// </summary>
    private bool RidesToTheFight(in Perceived world)
    {
        if (!world.Mounted || (_leg.Active && _leg.Dismounted)
            || (world.NearestCreature != 0 && world.NearestCreatureM <= RaiderSeekM))
        {
            return false;
        }

        // The point it seeks, else the radar's nearest cluster it is about to seek (Rally's step 5), else the gather point.
        var (x, z) = _seeking ? (_seekX, _seekZ) : NearestBlip(world.X, world.Z, out var bx, out var bz) ? (bx, bz) : (_rallyX, _rallyZ);
        return Routing.Distance(world.X, world.Z, x, z) > Routing.RunMps * Routing.RideOverSeconds;
    }

    /// <summary>Whether the radar still shows a cluster within <paramref name="m"/> of a point.</summary>
    private bool BlipNear(float x, float z, double m)
    {
        for (var i = 0; i < _radarX.Length; i++)
        {
            if (_radarN[i] > 0 && Routing.Distance(x, z, _radarX[i], _radarZ[i]) <= m)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The radar's nearest cluster within <see cref="RadarSeekM"/>; false with none.</summary>
    private bool NearestBlip(float x, float z, out float bx, out float bz)
    {
        bx = 0f;
        bz = 0f;
        var best = RadarSeekM;
        var found = false;
        for (var i = 0; i < _radarX.Length; i++)
        {
            var d = Routing.Distance(x, z, _radarX[i], _radarZ[i]);
            if (_radarN[i] > 0 && d <= best)
            {
                best = d;
                bx = _radarX[i];
                bz = _radarZ[i];
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Whether it holds a rally point and stands at it or in its city: a defender, which fights at any level. The point is before the fortress's gate, out in
    /// the desert (Loïc, 2026-11-04), so near it counts as the town does — and so does the raided town for a townsman, while a horde runs.
    /// </summary>
    private bool DefendsTown(in Perceived world)
        => (_rallyState is 1 or 2
                && (RegionAt(world.X, world.Z) == (_rallyCity >= 0 ? _rallyCity : NearestCity(_rallyX, _rallyZ)) + 1
                    || Routing.Distance(world.X, world.Z, _rallyX, _rallyZ) <= RallyTravelM
                    || _seeking))
            || (_hordeCity >= 0 && RegionAt(world.X, world.Z) == _hordeCity + 1);

    /// <summary>The city nearest a point, by index.</summary>
    private int NearestCity(float x, float z)
    {
        var best = 0;
        var bestD = double.MaxValue;
        for (var i = 0; i < _cities.Length; i++)
        {
            var d = Routing.Distance(x, z, (float)_cities[i].X, (float)_cities[i].Z);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }

        return best;
    }
}
