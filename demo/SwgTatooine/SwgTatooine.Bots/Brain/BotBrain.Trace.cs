using System;

namespace SwgTatooine.Bots.Brain;

/// <summary>Why a goal ended or began: the condition the brain acted on (bot-telemetry.md).</summary>
/// <remarks>
/// One enum for both ends of a transition, so a fleet counter is one array indexed by it. Appended to, never renumbered: the report names them.
/// </remarks>
public enum GoalWhy : byte
{
    None = 0,

    /// <summary>The bot's first decision.</summary>
    Spawned,

    // Ends.
    HuntTimeout,
    HuntTargetGone,
    HuntGivenUpTarget,
    HuntNotHuntable,
    HuntOutOfRange,
    IdleOver,
    Arrived,
    NoDestination,
    Landed,
    BoardRefused,
    ShuttlePatienceOut,
    VisitDone,
    VisitFailed,
    EnterFailed,
    LeftBuilding,
    QuestDone,
    QuestTimeout,
    PortExited,

    // Beginnings.
    CreatureNoticed,
    QuestHeld,
    GoingHome,
    TravelChosen,
    FlightChosen,
    ErrandStarted,
    DoorChosen,
    IdleChosen,
    BoardAsked,
    EnteredBuilding,
}

/// <summary>What happened to a walk (a leg): planned, a point reached, re-planned, stuck, given up.</summary>
public enum LegWhy : byte
{
    /// <summary>A walk searched and started — every one, re-plans and detours included.</summary>
    Planned = 0,

    /// <summary>One of its points reached. Counted, not traced: a walk has dozens.</summary>
    Waypoint,

    /// <summary>Walked to its end.</summary>
    Done,

    /// <summary>The next stretch of a walk too long to search in one go.</summary>
    Stretch,

    /// <summary>Stuck, and searched again from where it stands.</summary>
    Replanned,

    /// <summary>Stuck after its re-plans: the goal decides what next.</summary>
    Stuck,

    /// <summary>Given up for good: no way found, or a terminal it could not reach.</summary>
    Abandoned,

    /// <summary>A straight-line travel that stopped closing on its goal (the travel watchdog).</summary>
    TravelStuck,

    /// <summary>The way round the obstacle the travel watchdog found.</summary>
    Detour,
}

/// <summary>
/// An observer of one brain's decisions, attached to the few bots a run traces and null on every other (bot-telemetry.md): what it chose and why, and
/// what its walks did. Called on the bot's own driving thread, inside <see cref="BotBrain.Decide"/>.
/// </summary>
public interface IBrainTrace
{
    /// <summary>A goal ended and another began. A pass through <see cref="Goal.Choosing"/> is folded into one call carrying both reasons.</summary>
    /// <param name="tick">The fleet's tick.</param>
    /// <param name="from">The goal left.</param>
    /// <param name="to">The goal taken.</param>
    /// <param name="exit">Why <paramref name="from"/> ended; <see cref="GoalWhy.None"/> for a direct change, which <paramref name="enter"/> explains.</param>
    /// <param name="enter">Why <paramref name="to"/> began.</param>
    /// <param name="subject">What it is about: a creature's netId, a facility kind, a region; 0 for nothing.</param>
    /// <param name="metres">How far that subject was, or 0.</param>
    void OnGoal(long tick, Goal from, Goal to, GoalWhy exit, GoalWhy enter, uint subject, float metres);

    /// <summary>A walk's event.</summary>
    /// <param name="tick">The fleet's tick.</param>
    /// <param name="what">What happened.</param>
    /// <param name="points">The walk's point count.</param>
    /// <param name="metres">Its length from the point being walked to its end.</param>
    void OnLeg(long tick, LegWhy what, int points, float metres);
}

/// <summary>A brain's own counters, for the fleet's statistics: kept on every bot, an increment each, no allocation after construction.</summary>
public sealed class BrainCounters
{
    /// <summary>Goal transitions whose goal changed (Idling to Idling through a choice is a decision, not a change).</summary>
    public long GoalChanges;

    /// <summary>Ticks spent stuck before each stuck verdict, summed: a leg's, or a travel's straight line.</summary>
    public long StuckTicks;

    /// <summary>Each reason's count, as an end or a beginning, indexed by <see cref="GoalWhy"/>.</summary>
    public readonly long[] Why = new long[Enum.GetValues<GoalWhy>().Length];

    /// <summary>Each walk event's count, indexed by <see cref="LegWhy"/>.</summary>
    public readonly long[] Legs = new long[Enum.GetValues<LegWhy>().Length];
}

public sealed partial class BotBrain
{
    /// <summary>The goal before the choice under way, and why it ended: the first half of the transition <see cref="Become"/> reports.</summary>
    private Goal _choosingFrom = Goal.Choosing;

    private GoalWhy _exitWhy = GoalWhy.Spawned;

    /// <summary>The observer of this brain's decisions, or null — every bot but the few a run traces, at the cost of a null check per transition.</summary>
    public IBrainTrace Trace { get; set; }

    /// <summary>This brain's counters, for the fleet's statistics.</summary>
    public BrainCounters Counters { get; } = new();

    /// <summary>
    /// Changes the goal, saying why. <see cref="Goal.Choosing"/> only remembers what it left and why: the choice that follows in the same call reports
    /// the whole transition once.
    /// </summary>
    private void Become(Goal to, GoalWhy why, long tick, uint subject = 0, float metres = 0f)
    {
        if (to == Goal.Choosing)
        {
            if (Goal != Goal.Choosing)
            {
                _choosingFrom = Goal;
                _exitWhy = why;
            }

            Goal = to;
            return;
        }

        Goal from;
        GoalWhy exit;
        if (Goal == Goal.Choosing)
        {
            (from, exit) = (_choosingFrom, _exitWhy);
            _choosingFrom = Goal.Choosing;
            _exitWhy = GoalWhy.None;
        }
        else
        {
            (from, exit) = (Goal, GoalWhy.None);
        }

        Goal = to;
        var counters = Counters;
        counters.Why[(int)exit]++;
        counters.Why[(int)why]++;
        if (from != to)
        {
            counters.GoalChanges++;
        }

        Trace?.OnGoal(tick, from, to, exit, why, subject, metres);
    }

    /// <summary>A choice made straight from a goal that ran out, without passing through <see cref="Goal.Choosing"/>: an idle's end.</summary>
    private void ChoosingFromHere()
    {
        if (Goal != Goal.Choosing)
        {
            _choosingFrom = Goal;
            _exitWhy = Goal == Goal.Idling ? GoalWhy.IdleOver : GoalWhy.None;
            Goal = Goal.Choosing;
        }
    }

    /// <summary>Counts a walk's event, and tells the observer, if any, with the walk's shape.</summary>
    private void NoteLeg(LegWhy what, long tick)
    {
        Counters.Legs[(int)what]++;
        if (Trace != null && what != LegWhy.Waypoint)
        {
            Trace.OnLeg(tick, what, _leg.Points.Count, (float)_leg.LengthTo(_leg.Next, _leg.Points.Count));
        }
    }

    /// <summary>Counts a stuck verdict's wait: the ticks since the walk or the travel last closed on its goal.</summary>
    private void NoteStuck(long since, long tick) => Counters.StuckTicks += Math.Max(0L, tick - since);
}
