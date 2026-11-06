using System;
using SwgTatooine.Navigation;

namespace SwgTatooine.Bots.Brain;

/// <summary>
/// What a bot does when something attacks it (Loïc, 2026-11-04: "I saw bots being attacked and not fight back or walk away, so just wait and die"): it
/// fights back or runs, on its odds — the time it needs to kill the attacker against the time the attacker needs to kill it (brain.md § Attacked).
/// </summary>
/// <remarks>
/// <para>
/// <b>Before it, nothing told the brain it was attacked.</b> A fight began only in <see cref="Choose"/>, on the nearest creature at or below
/// <see cref="MaxLevel"/>: an idling bot stood still under the bites, a bot waiting for a shuttle stayed queued, and a hunt bailed out on an attacker past its
/// level. Now a creature whose replicated <c>target</c> is this body is read in the scan that finds the nearest one (<see cref="Perceived.Attacker"/>), and
/// this runs before the goal does, whatever the goal.
/// </para>
/// <para>
/// <b>One decision per engagement, not per attacker.</b> An engagement lasts while anything is on the bot, and ends after <see cref="ClearSeconds"/> with
/// nothing on it. The first build rolled per attacker: a pack's nearest biter changes from one tick to the next, so a bot rolled again and again, flipped
/// between fighting and running, and almost never got away — 33 135 rolls for 2 000 bots in five minutes, 640 escapes of 15 851 runs (live, 2026-11-04).
/// Inside an engagement the bot fights, or runs from, whichever attacker is nearest.
/// </para>
/// <para>
/// <b>On the odds, not on the level gap (Loïc, 2026-11-05).</b> The roll by level — 90 % at the bot's level, 15 points less per level above — and the
/// fight's 30 s limit gave 1.01 runs per engagement against 0.75 fights over 1 h 30 of an armed fleet: the bots took fights they then broke off. Now it
/// fights when <see cref="Odds"/> says it wins with its nerve's margin, and inside the fight it watches both health bars: every
/// <see cref="OddsWindowSeconds"/> the rates they fell at replace the model's guess, and it runs when the fight has turned (<see cref="StayMargin"/>), or when
/// it has not hurt the attacker for <see cref="StallSeconds"/>. <see cref="FightBackstopSeconds"/> is the last word, for a fight that neither turns nor ends.
/// A run that has not shaken the attackers off in <see cref="FleeSeconds"/>, or gains nothing on them, turns into a fight (cornered).
/// </para>
/// </remarks>
public sealed partial class BotBrain
{
    /// <summary>Under this share of its health a bot that is losing runs, from a fight it chose or one it was dragged into.</summary>
    public const float FleeHealth = 0.3f;

    /// <summary>The longest a bot runs from attackers still on it before it turns and fights, seconds.</summary>
    public const double FleeSeconds = 12d;

    /// <summary>Nothing on the bot for this long ends an engagement, seconds: a pack re-targeting, or a biter out of view for a frame, does not.</summary>
    public const double ClearSeconds = 1.5d;

    /// <summary>
    /// A creature this close to a bot inside a city is a threat before it bites (Loïc, 2026-11-04: in a raided town, everyone fights or flees). Creatures do
    /// not come into a city but for a horde; outside one, a creature not on the bot is the desert's, and the hunt decides about it.
    /// </summary>
    public const double TownThreatM = 20d;

    /// <summary>How often a fight's odds are read from the health bars, seconds: a creature strikes every 2 s, so two or three blows a window.</summary>
    public const double OddsWindowSeconds = 5d;

    /// <summary>
    /// A fight goes on while the time to kill is within this share of the time to die: past its nerve to start one, short of a coin flip to stay.
    /// </summary>
    public const double StayMargin = 1.25d;

    /// <summary>
    /// A fight in which the attacker's bar has not moved for this long is not being won — out of reach, or behind a wall — and the bot leaves it.
    /// </summary>
    public const double StallSeconds = 12d;

    /// <summary>The longest a fight lasts whatever its odds say, seconds: the 30 s limit it replaced cut fights the bot was winning slowly.</summary>
    public const double FightBackstopSeconds = 120d;

    /// <summary>What started a run (<see cref="RunsBy"/>).</summary>
    public enum RunCause : byte
    {
        /// <summary>The odds said no at the first bite.</summary>
        Odds = 0,

        /// <summary>The fight turned: the bars say the attacker wins.</summary>
        Losing,

        /// <summary>The attacker's bar did not move for <see cref="StallSeconds"/>.</summary>
        Stalled,

        /// <summary>Under <see cref="FleeHealth"/> and losing.</summary>
        Hurt,

        /// <summary>Down to its fist.</summary>
        Unarmed,

        /// <summary><see cref="FightBackstopSeconds"/> ran out.</summary>
        Backstop,
    }

    /// <summary>How many <see cref="RunCause"/> values there are.</summary>
    public const int RunCauseCount = 6;

    /// <summary>Engagements decided: something attacked a bot that was not already fighting it by its own choice.</summary>
    public long Attacked { get; private set; }

    /// <summary>Fights started: on the odds, or cornered after a run that did not shake the attackers off.</summary>
    public long FoughtBack { get; private set; }

    /// <summary>Runs started, whatever the cause (<see cref="RunsBy"/>).</summary>
    public long Fled { get; private set; }

    /// <summary>Runs that shook the attackers off.</summary>
    public long Escaped { get; private set; }

    /// <summary>Fights that ended with nothing left on the bot and the last attacker's bar near empty: won.</summary>
    public long FightsWon { get; private set; }

    /// <summary>Runs started for one cause.</summary>
    public long RunsBy(RunCause cause) => _runsBy[(int)cause];

    private readonly long[] _runsBy = new long[RunCauseCount];

    private enum Defence : byte
    {
        None = 0,
        Fighting,
        Fleeing,
    }

    private Defence _defence;

    // The last tick anything was on it; when a run turns into a fight; the run's heading, kept through a moment with nothing in view.
    private long _attackedAt;
    private long _fleeUntil;
    private float _fleeHeading;

    // A run's progress, checked every CorneredSeconds: the gap to the attacker then, and when it was taken.
    private long _fleeCheckAt;
    private float _fleeCheckGap;

    // Turned to fight because the run could not get away: a hurt bot then fights on rather than run into the same wall (review, 2026-11-04).
    private bool _cornered;

    // The fight's odds as the bars tell them. The window's start and the bot's own share then; the attackers' bars dealt down since, summed tick by tick
    // per attacker — a pack's nearest biter changes every tick, and a jump from one bar to the next is not damage, so the last few attackers' bars are
    // kept (BarsKept, by netId, the oldest replaced); the nearest attacker's share on the last tick. The last window's reading (seconds to kill, to
    // die; valid once a window has closed), the last tick an attacker's bar fell, and when the fight began.
    private const int BarsKept = 4;
    private readonly uint[] _barIds = new uint[BarsKept];
    private readonly float[] _barShares = new float[BarsKept];
    private int _barNext;
    private long _oddsAt;
    private float _oddsOwn;
    private float _oddsDealt;
    private float _theirsLast;
    private bool _oddsRead;
    private double _seenKill;
    private double _seenDie;
    private long _hurtItAt;
    private long _fightSince;

    /// <summary>
    /// A run that has not opened the gap by <see cref="CorneredGainM"/> in this long is cornered, and fights back, hurt or not (review, 2026-11-04: a bot under
    /// <see cref="FleeHealth"/> ran into a wall for the rest of its life, shooting nothing).
    /// </summary>
    public const double CorneredSeconds = 2.5d;

    /// <summary>What a run must gain on its attacker over <see cref="CorneredSeconds"/> to be getting away, metres.</summary>
    public const float CorneredGainM = 2f;

    /// <summary>
    /// The balance model's odds of a fight (content-levels.md; <c>ContentLevels</c> on the server, <c>balance_model.py</c>): the seconds this bot needs to kill
    /// the attacker and the seconds the attacker needs to kill it. On level, a player kills a beast in 8 s and a beast kills a player in 75 s; a level's
    /// damage grows 1.25 × every ten levels and its effective health with its hit points and mitigation.
    /// </summary>
    /// <param name="level">The bot's combat level; 0 (not sent) is taken as the attacker's.</param>
    /// <param name="health">The bot's share of its health, 0-1; 0 (not sent) is taken as whole.</param>
    /// <param name="attackerLevel">The attacker's level; 0 (not sent) is taken as the bot's.</param>
    /// <param name="attackerHealth">The attacker's share of its health; 0 (not sent) is taken as whole.</param>
    /// <param name="attackers">How many are on the bot: the time to die is shared between them; 0 (not sent) is one.</param>
    /// <returns>Seconds to kill, seconds to die.</returns>
    /// <remarks>
    /// A model of one beast against one player, as the server's content is tuned. A camp foe, a raider of a stronger raid, a pack or better gear are what
    /// the model does not know, and what the bars correct within a window of the fight.
    /// </remarks>
    public static (double Kill, double Die) Odds(int level, float health, int attackerLevel, float attackerHealth, int attackers = 1)
    {
        var cl = level > 0 ? level : Math.Max(1, attackerLevel);
        var foe = attackerLevel > 0 ? attackerLevel : cl;
        var own = health > 0f ? health : 1f;
        var theirs = attackerHealth > 0f ? attackerHealth : 1f;
        return (8d * theirs * ModelDps(foe) / ModelDps(cl), 75d * own * ModelEffHp(cl) / ModelEffHp(foe) / Math.Max(1, attackers));
    }

    // balance_model.py's dps_growth and effective health (hp_base, hp_per_cl, mit_cl5, mit_per_cl), as ContentLevels ports them.
    private static double ModelDps(int level) => Math.Pow(1.25d, (level - 5) / 10d);

    private static double ModelEffHp(int level) => (1000d + (40d * level)) / (1d - (0.12d + (0.006d * (level - 5))));

    /// <summary>
    /// This bot's nerve: the share of its time to die it is ready to need to kill, to start a fight — 0.5 (cautious) to 1.0 (a coin flip), by its seed. A
    /// fleet of one temper made the same call in the same place, every time.
    /// </summary>
    private double Nerve => 0.5d + ((((_seed * 2654435761u) >> 16) % 51u) / 100d);

    /// <summary>
    /// The fight's odds now: what the bars said over the last window, once one has closed, else the model's. A bot whose own bar did not move has a time
    /// to die that does not end (infinity); an attacker whose bar did not move keeps the time to kill read before it — the stall is
    /// <see cref="StallSeconds"/>'s to judge, not the odds'.
    /// </summary>
    private (double Kill, double Die) FightOdds(in Perceived world)
    {
        if (_oddsRead)
        {
            return (_seenKill, _seenDie);
        }

        return Odds(CombatLevel, world.Health, world.AttackerLevel, world.AttackerHealth, world.Attackers);
    }

    /// <summary>Starts reading a fight's bars: the window opens now.</summary>
    private void OpenOdds(in Perceived world, long tick)
    {
        _oddsAt = tick;
        _oddsOwn = world.Health;
        _oddsDealt = 0f;
        Array.Clear(_barIds);
        _barNext = 0;
        Bar(world.Attacker, world.AttackerHealth);
        _theirsLast = world.AttackerHealth;
        _oddsRead = false;
        _hurtItAt = tick;
        _fightSince = tick;
    }

    /// <summary>
    /// Follows the bars a tick, and closes a window when it is due: the bot's share lost over it and the attackers' shares dealt down become the fight's
    /// odds. The damage dealt is summed tick by tick on the same attacker, so a pack whose nearest biter changes every tick is still read; a bar not sent
    /// (0) is skipped, never a reason to stop reading.
    /// </summary>
    private void ReadOdds(in Perceived world, long tick)
    {
        var before = Bar(world.Attacker, world.AttackerHealth);
        if (world.AttackerHealth > 0f && before > 0f && world.AttackerHealth < before)
        {
            _oddsDealt += before - world.AttackerHealth;
            _hurtItAt = tick;
        }

        _theirsLast = world.AttackerHealth;

        if (tick - _oddsAt < Ticks(OddsWindowSeconds))
        {
            return;
        }

        var seconds = (tick - _oddsAt) / (double)_rate;
        if (world.Health > 0f && _oddsOwn > 0f)
        {
            var lost = _oddsOwn - world.Health;
            var theirs = world.AttackerHealth > 0f ? world.AttackerHealth : 1f;
            var (modelKill, _) = Odds(CombatLevel, world.Health, world.AttackerLevel, world.AttackerHealth, world.Attackers);
            _seenKill = _oddsDealt > 0f ? theirs / (_oddsDealt / seconds) : _oddsRead ? _seenKill : modelKill;
            _seenDie = lost > 0f ? world.Health / (lost / seconds) : double.PositiveInfinity;
            _oddsRead = true;
        }

        _oddsAt = tick;
        _oddsOwn = world.Health;
        _oddsDealt = 0f;
    }

    /// <summary>Notes an attacker's bar, and gives the one noted for it before (0 for none): <see cref="BarsKept"/> attackers, the oldest replaced.</summary>
    private float Bar(uint attacker, float share)
    {
        for (var i = 0; i < BarsKept; i++)
        {
            if (_barIds[i] == attacker)
            {
                var before = _barShares[i];
                _barShares[i] = share;
                return before;
            }
        }

        _barIds[_barNext] = attacker;
        _barShares[_barNext] = share;
        _barNext = (_barNext + 1) % BarsKept;
        return 0f;
    }

    /// <summary>Whether the fight is lost on its odds: the time to kill past <see cref="StayMargin"/> of the time to die.</summary>
    private bool Losing(in Perceived world)
    {
        var (kill, die) = FightOdds(in world);
        return !double.IsPositiveInfinity(die) && kill > die * StayMargin;
    }

    /// <summary>
    /// Fights back or runs; null when nothing attacks it, when what attacks it is the creature it is already fighting by its own choice (a hunt, a
    /// mission's defenders) and the fight is not lost, or when it is on a swoop it must get off to fight.
    /// </summary>
    /// <param name="world">What the bot was sent.</param>
    /// <param name="tick">The fleet's tick.</param>
    /// <returns>The command, or null to let the swoop step and the goal run.</returns>
    private Intent? Defend(in Perceived seen, long tick)
    {
        // Inside a city, a raider near is as good as on the bot (the desert's creatures do not come into towns; a corpse is no threat — Perception skips it).
        var world = seen.Attacker == 0 && seen.NearestCreature != 0 && seen.NearestCreatureRaider && seen.NearestCreatureM <= TownThreatM
                && RegionAt(seen.X, seen.Z) != 0
            ? seen with
            {
                Attacker = seen.NearestCreature,
                AttackerX = seen.NearestCreatureX,
                AttackerZ = seen.NearestCreatureZ,
                AttackerM = seen.NearestCreatureM,
                AttackerLevel = seen.NearestCreatureLevel,
                AttackerHealth = seen.NearestCreatureHealth,
            }
            : seen;
        if (world.Attacker == 0)
        {
            if (_defence == Defence.None)
            {
                return null;
            }

            if (tick - _attackedAt < Ticks(ClearSeconds))
            {
                // A moment with nothing on it: keep the course rather than end the engagement and roll again on the next bite.
                return _defence == Defence.Fleeing ? Intent.Attack(0u, _fleeHeading, Routing.SpeedClassOf(TravelMode.Run)) : null;
            }

            var fled = _defence == Defence.Fleeing;
            if (fled)
            {
                Escaped++;
            }
            else if (_theirsLast > 0f && _theirsLast <= WonShare && seen.Health > 0f)
            {
                // The last attacker's bar was near empty when it left the bot: it fell (a bar is 8 bits, and a corpse leaves the view). A bot downed is
                // dropped by its attacker too, and that is no fight won.
                FightsWon++;
            }

            // Over: decide again — a mission held is picked up again, an errand chosen again.
            _defence = Defence.None;
            _cornered = false;
            _oddsRead = false;
            Become(Goal.Choosing, fled ? GoalWhy.Escaped : GoalWhy.HuntTargetGone, tick);
            return null;
        }

        _attackedAt = tick;

        // Health is 0 for "not sent" (the zero-means-no-data rule); a body at 0 is downed and does nothing anyway.
        var low = world.Health > 0f && world.Health < FleeHealth;
        switch (_defence)
        {
            case Defence.Fleeing:
                // Cornered — the run gains nothing on the attacker — it fights, hurt or not: a run into a wall saves nobody.
                if (tick >= _fleeCheckAt)
                {
                    var gaining = world.AttackerM >= _fleeCheckGap + CorneredGainM;
                    _fleeCheckAt = tick + Ticks(CorneredSeconds);
                    _fleeCheckGap = world.AttackerM;
                    if (!gaining)
                    {
                        _cornered = true;
                        return FightBack(in world, tick);
                    }
                }

                if (tick < _fleeUntil || low)
                {
                    return Away(in world);
                }

                // Still pursued after a full run: cornered, it fights.
                _cornered = true;
                return FightBack(in world, tick);

            case Defence.Fighting:
                ReadOdds(in world, tick);
                if (Broken(in world, tick, low) is { } cause)
                {
                    return Flee(in world, tick, cause);
                }

                return Stand(in world) ?? Engage(world.X, world.Z, world.Attacker, world.AttackerX, world.AttackerZ, world.AttackerM, mobile: true,
                    melee: world.Unarmed);
        }

        // Already in the fight it chose: a hunt on this very creature, or a mission at its lair, whose defenders it means to fight at any level. A mission
        // anywhere else — walking to its lair, standing while the lair is unknown — is no fight, and the attacker is answered (review, 2026-11-04). Hurt,
        // it runs only from a fight it is losing: a creature on its last legs is finished off.
        if ((Goal == Goal.Questing && _questNear && world.HasMissionLair
                && Routing.Distance(world.AttackerX, world.AttackerZ, world.MissionLairX, world.MissionLairZ) <= DefenderM)
            || (Goal == Goal.Hunting && world.Attacker == world.NearestCreature))
        {
            if (!low)
            {
                return null;
            }

            // The model's odds: this fight's bars are not being read (it is the goal's, not an engagement).
            var (huntKill, huntDie) = Odds(CombatLevel, world.Health, world.AttackerLevel, world.AttackerHealth, world.Attackers);
            if (huntKill <= huntDie * StayMargin)
            {
                return null;
            }

            Attacked++;
            return Flee(in world, tick, RunCause.Hurt);
        }

        Attacked++;

        // Down to its fist, it runs (Loïc, 2026-11-04): a punch a metre off a creature that bites is no fight — and a run that gains nothing turns it round
        // all the same (cornered). The weapon comes first: GearPlan sends it to the bazaar.
        if (world.Unarmed)
        {
            return Flee(in world, tick, RunCause.Unarmed);
        }

        // On the odds: a fight it wins with its nerve's margin, else a run. Hurt is in the odds already — a short time to die.
        var (kill, die) = Odds(CombatLevel, world.Health, world.AttackerLevel, world.AttackerHealth, world.Attackers);

        // A defender of a horde's town fights whatever comes, at any level: it answered for that (horde.md § 6). Hurt and losing, it runs — decided here,
        // not a tick into the fight (review, 2026-11-05: it turned and ran on the next tick).
        if (DefendsTown(in world))
        {
            return low && kill > die * StayMargin ? Flee(in world, tick, RunCause.Hurt) : FightBack(in world, tick);
        }

        if (kill <= die * Nerve)
        {
            return FightBack(in world, tick);
        }

        return Flee(in world, tick, low ? RunCause.Hurt : RunCause.Odds);
    }

    /// <summary>A last attacker's share at or under this, as it left the bot's view, is a kill.</summary>
    public const float WonShare = 0.12f;

    /// <summary>
    /// Why a fight in progress is broken off, or null to go on: hurt and losing, the odds turned, the attacker's bar stalled, or the backstop. A defender
    /// holds through all but its health: it answered for the fight (review, 2026-11-04: they ran after 30 s). A cornered bot fights on to the backstop —
    /// it ran, and could not get away; running again was a flip between the two until it died (review, 2026-11-05).
    /// </summary>
    private RunCause? Broken(in Perceived world, long tick, bool low)
    {
        if (_cornered)
        {
            return tick - _fightSince >= Ticks(FightBackstopSeconds) ? RunCause.Backstop : null;
        }

        if (low && Losing(in world))
        {
            return RunCause.Hurt;
        }

        if (DefendsTown(in world))
        {
            return null;
        }

        if (_oddsRead && Losing(in world))
        {
            return RunCause.Losing;
        }

        // Only on a bar the server sends: 0 is "not sent", and a fight on an unknown bar is judged by its odds and the backstop alone.
        if (_theirsLast > 0f && tick - _hurtItAt >= Ticks(StallSeconds))
        {
            return RunCause.Stalled;
        }

        return tick - _fightSince >= Ticks(FightBackstopSeconds) ? RunCause.Backstop : null;
    }

    /// <summary>Turns on the nearest attacker, whatever its level and whether it was given up on, on foot.</summary>
    private Intent? FightBack(in Perceived world, long tick)
    {
        FoughtBack++;
        _defence = Defence.Fighting;
        _wantMounted = false;
        _leg.Clear();
        Become(Goal.Hunting, GoalWhy.AttackedFought, tick, world.Attacker, world.AttackerM);
        _hunted = world.Attacker;
        _huntUntil = tick + Ticks(FightBackstopSeconds);
        OpenOdds(in world, tick);
        return Stand(in world) ?? Engage(world.X, world.Z, world.Attacker, world.AttackerX, world.AttackerZ, world.AttackerM, mobile: true,
            melee: world.Unarmed);
    }

    /// <summary>
    /// On a swoop with a fight to fight: stopped, its wish off, until the swoop step takes it down (review, 2026-11-04 — returning null let the goal it rode
    /// under wish the swoop back on and ride on, so the fight never began). Null on foot.
    /// </summary>
    private Intent? Stand(in Perceived world)
    {
        if (!world.Mounted)
        {
            return null;
        }

        _wantMounted = false;
        return Intent.Move(0f, 0);
    }

    /// <summary>Starts a run from the attackers, for <paramref name="cause"/>.</summary>
    private Intent Flee(in Perceived world, long tick, RunCause cause)
    {
        Fled++;
        _runsBy[(int)cause]++;
        _defence = Defence.Fleeing;
        _fleeUntil = tick + Ticks(FleeSeconds);
        _fleeCheckAt = tick + Ticks(CorneredSeconds);
        _fleeCheckGap = world.AttackerM;
        _leg.Clear();
        Become(Goal.Choosing, GoalWhy.AttackedFled, tick, world.Attacker, world.AttackerM);
        return Away(in world);
    }

    /// <summary>Straight away from the nearest attacker at a run, the target dropped; on the swoop if it is on one, which is faster.</summary>
    private Intent Away(in Perceived world)
    {
        _fleeHeading = Routing.HeadingTo(world.AttackerX, world.AttackerZ, world.X, world.Z);
        return Intent.Attack(0u, _fleeHeading, world.Mounted ? Routing.SwoopClass : Routing.SpeedClassOf(TravelMode.Run));
    }
}
