using System;
using SwgTatooine.Navigation;

namespace SwgTatooine.Bots.Brain;

/// <summary>
/// What a bot does when something attacks it (Loïc, 2026-11-04: "I saw bots being attacked and not fight back or walk away, so just wait and die"): it
/// fights back or runs, by a roll weighted by the attacker's level against its own and by its health (brain.md § Attacked).
/// </summary>
/// <remarks>
/// <para>
/// <b>Before it, nothing told the brain it was attacked.</b> A fight began only in <see cref="Choose"/>, on the nearest creature at or below
/// <see cref="MaxLevel"/>: an idling bot stood still under the bites, a bot waiting for a shuttle stayed queued, and a hunt bailed out on an attacker past its
/// level. Now a creature whose replicated <c>target</c> is this body is read in the scan that finds the nearest one (<see cref="Perceived.Attacker"/>), and
/// this runs before the goal does, whatever the goal.
/// </para>
/// <para>
/// <b>One roll per attacker</b>, so a bot does not change its mind every tick: a new attacker is rolled for, the same one is not — unless the health falls
/// under <see cref="FleeHealth"/>, which always runs. A run that cannot shake the attacker off in <see cref="FleeSeconds"/> turns and fights: cornered.
/// </para>
/// </remarks>
public sealed partial class BotBrain
{
    /// <summary>Under this share of its health a bot always runs, from a fight it chose or one it was dragged into.</summary>
    public const float FleeHealth = 0.3f;

    /// <summary>The longest a bot runs from an attacker still on it before it turns and fights, seconds.</summary>
    public const double FleeSeconds = 12d;

    /// <summary>
    /// The chance, in percent, that a bot fights back an attacker <paramref name="gap"/> levels above its own (negative: below): 90 % at its level or under,
    /// 15 points less per level above, never under 5 %.
    /// </summary>
    /// <param name="gap">The attacker's level less the bot's.</param>
    /// <returns>Percent, 5 to 90.</returns>
    public static int FightChancePercent(int gap) => Math.Clamp(90 - (15 * Math.Max(0, gap)), 5, 90);

    /// <summary>Attackers rolled for: each a new creature on this bot.</summary>
    public long Attacked { get; private set; }

    /// <summary>Of those, fought back — by the roll, or cornered after a run that did not shake it off.</summary>
    public long FoughtBack { get; private set; }

    /// <summary>Runs started: by the roll, or because the health fell under <see cref="FleeHealth"/>.</summary>
    public long Fled { get; private set; }

    /// <summary>Runs that shook the attacker off.</summary>
    public long Escaped { get; private set; }

    // The attacker rolled for, and what the roll said; the one being fought back (0 for none); the one being run from, and the run's end.
    private uint _rolledFor;
    private uint _fightingBack;
    private uint _fleeingFrom;
    private long _fleeUntil;

    /// <summary>
    /// Fights back or runs from an attacker; null when nothing attacks it, or when what attacks it is the creature it is already fighting by its own choice
    /// (a hunt, a mission's defenders) and its health holds.
    /// </summary>
    /// <param name="world">What the bot was sent.</param>
    /// <param name="tick">The fleet's tick.</param>
    /// <returns>The command, or null to let the goal run.</returns>
    private Intent? Defend(in Perceived world, long tick)
    {
        if (world.Attacker == 0)
        {
            if (_fleeingFrom != 0)
            {
                // Shaken off: nothing is on it now. Decide again — a mission held is picked up again, an errand chosen again.
                _fleeingFrom = 0;
                Escaped++;
                Become(Goal.Choosing, GoalWhy.Escaped, tick);
            }

            _fightingBack = 0;
            return null;
        }

        // Health is 0 for "not sent" (the zero-means-no-data rule); a body at 0 is downed and does nothing anyway.
        var low = world.Health > 0f && world.Health < FleeHealth;

        if (_fleeingFrom == world.Attacker)
        {
            if (tick < _fleeUntil || low)
            {
                return Away(in world);
            }

            // It is still on it after a full run: cornered, it fights.
            _fleeingFrom = 0;
            return FightBack(in world, tick);
        }

        if (low)
        {
            return Flee(in world, tick);
        }

        if (_fightingBack == world.Attacker && Goal == Goal.Hunting)
        {
            return null;
        }

        // Already in the fight it chose: a hunt on this very creature, or a mission, whose defenders it means to fight at any level.
        if (Goal == Goal.Questing || (Goal == Goal.Hunting && _fightingBack == 0 && world.Attacker == world.NearestCreature))
        {
            return null;
        }

        if (_rolledFor == world.Attacker)
        {
            return null;
        }

        _rolledFor = world.Attacker;
        Attacked++;
        var gap = world.AttackerLevel == 0 ? 0 : world.AttackerLevel - CombatLevel;
        return Next() % 100u < (uint)FightChancePercent(gap) ? FightBack(in world, tick) : Flee(in world, tick);
    }

    /// <summary>Turns on the attacker: a hunt on it, whatever its level and whether it was given up on, on foot.</summary>
    private Intent FightBack(in Perceived world, long tick)
    {
        FoughtBack++;
        _fightingBack = world.Attacker;
        _rolledFor = world.Attacker;
        _wantMounted = false;
        _leg.Clear();
        Become(Goal.Hunting, GoalWhy.AttackedFought, tick, world.Attacker, world.AttackerM);
        _huntUntil = tick + Ticks(HuntSeconds);
        return Engage(world.X, world.Z, world.Attacker, world.AttackerX, world.AttackerZ, world.AttackerM, mobile: true);
    }

    /// <summary>Starts a run from the attacker.</summary>
    private Intent Flee(in Perceived world, long tick)
    {
        Fled++;
        _fleeingFrom = world.Attacker;
        _rolledFor = world.Attacker;
        _fightingBack = 0;
        _fleeUntil = tick + Ticks(FleeSeconds);
        _leg.Clear();
        Become(Goal.Choosing, GoalWhy.AttackedFled, tick, world.Attacker, world.AttackerM);
        return Away(in world);
    }

    /// <summary>Straight away from the attacker at a run, the target dropped; on the swoop if it is on one, which is faster.</summary>
    private static Intent Away(in Perceived world) =>
        Intent.Attack(0u, Routing.HeadingTo(world.AttackerX, world.AttackerZ, world.X, world.Z), Routing.SpeedClassOf(TravelMode.Run));

    /// <summary>
    /// The hunt's half of fighting back: the attacker named, not the nearest creature, and no level bound — it chose the fight. Null when this hunt is not
    /// one; done when the attacker is no longer on it.
    /// </summary>
    private Intent? HuntAttacker(in Perceived world, long tick)
    {
        if (_fightingBack == 0)
        {
            return null;
        }

        if (world.Attacker != _fightingBack)
        {
            // Killed, or it turned on someone else: the fight is over.
            _fightingBack = 0;
            Become(Goal.Choosing, GoalWhy.HuntTargetGone, tick);
            return Choose(in world, tick);
        }

        if (tick >= _huntUntil)
        {
            // A fight it cannot win in this long: it runs after all.
            return Flee(in world, tick);
        }

        return Engage(world.X, world.Z, world.Attacker, world.AttackerX, world.AttackerZ, world.AttackerM, mobile: true);
    }
}
