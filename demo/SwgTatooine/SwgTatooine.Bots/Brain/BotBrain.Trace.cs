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

    /// <summary>Holding no mission: off to the nearest mission terminal for one (the day's loop, BotBrain remarks).</summary>
    DeskChosen,

    /// <summary>Its city's mission terminals had nothing for it twice in a row: off to another city.</summary>
    DeskEmpty,

    /// <summary>Its inventory nearly full, or goods reserved for it: off to its city's bazaar to trade (bazaar.md § 8); a rush bot's every goal.</summary>
    MarketChosen,

    /// <summary>The bazaar errand done: traded, and back to the day's loop.</summary>
    MarketDone,

    /// <summary>Off to trade with <see cref="BotBrain.MarketFullStacks"/> stacks or more: already losing drops — how often is the measure of too little headroom.</summary>
    MarketFull,

    /// <summary>A crafter holding the inputs of a craft: off to its city's nearest crafting station (crafting-build.md § 6).</summary>
    CraftChosen,

    /// <summary>The craft sent: back to the day's loop.</summary>
    CraftDone,
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

    /// <summary>A walk across a city toward somewhere beyond it, dropped at the city's edge: the desert is ridden straight.</summary>
    CityEdge,
}

/// <summary>
/// What a bot is doing, as the fleet's statistics and the traced bots show it (Loïc, 2026-11-03: crafting was nowhere on the dashboard): its
/// <see cref="Goal"/>, with <see cref="Goal.Visiting"/> told apart by what it visits — a mission terminal, a bazaar, a crafting station. The values up to
/// <see cref="Questing"/> are the goal's own, so a goal that is not a visit is its activity by value.
/// </summary>
/// <remarks>The brain decides by <see cref="Goal"/>; this is only what it reports, so no decision changes with it.</remarks>
public enum Activity
{
    Choosing = Goal.Choosing,
    Travelling = Goal.Travelling,
    Hunting = Goal.Hunting,
    Idling = Goal.Idling,
    Waiting = Goal.Waiting,

    /// <summary>Going to a mission terminal and using it.</summary>
    Visiting = Goal.Visiting,
    Indoors = Goal.Indoors,
    Questing = Goal.Questing,

    /// <summary>Going to a bazaar terminal and trading at it: a visit of <see cref="Routing.BazaarKind"/>.</summary>
    Trading,

    /// <summary>Going to a crafting station and crafting, or repairing, at it: a visit of <see cref="Routing.CraftingStationKind"/>.</summary>
    Crafting,
}

/// <summary>
/// An observer of one brain's decisions, attached to the few bots a run traces and null on every other (bot-telemetry.md): what it chose and why, and
/// what its walks did. Called on the bot's own driving thread, inside <see cref="BotBrain.Decide"/>.
/// </summary>
public interface IBrainTrace
{
    /// <summary>A goal ended and another began. A pass through <see cref="Goal.Choosing"/> is folded into one call carrying both reasons.</summary>
    /// <param name="tick">The fleet's tick.</param>
    /// <param name="from">What it was doing: the goal left, a visit told by what it visited (<see cref="Activity"/>).</param>
    /// <param name="to">What it does now.</param>
    /// <param name="exit">Why <paramref name="from"/> ended; <see cref="GoalWhy.None"/> for a direct change, which <paramref name="enter"/> explains.</param>
    /// <param name="enter">Why <paramref name="to"/> began.</param>
    /// <param name="subject">What it is about: a creature's netId, a facility kind, a region; 0 for nothing.</param>
    /// <param name="metres">How far that subject was, or 0.</param>
    void OnGoal(long tick, Activity from, Activity to, GoalWhy exit, GoalWhy enter, uint subject, float metres);

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

    // What it was doing before the choice under way, and what it does now, as reported: a visit's kind is set before the goal that starts it.
    private Activity _choosingFromActivity = Activity.Choosing;
    private Activity _activity = Activity.Choosing;

    /// <summary>What it is doing, as the fleet's statistics and its trace show it.</summary>
    public Activity Activity => _activity;

    /// <summary>A goal's activity: a visit by what it visits, any other goal by its value.</summary>
    public static Activity ActivityOf(Goal goal, int visitKind) => goal != Goal.Visiting
        ? (Activity)goal
        : visitKind switch
        {
            Routing.CraftingStationKind => Activity.Crafting,
            Routing.BazaarKind => Activity.Trading,
            _ => Activity.Visiting,
        };

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
                _choosingFromActivity = _activity;
                _exitWhy = why;
            }

            Goal = to;
            _activity = Activity.Choosing;
            return;
        }

        Goal from;
        Activity fromActivity;
        GoalWhy exit;
        if (Goal == Goal.Choosing)
        {
            (from, fromActivity, exit) = (_choosingFrom, _choosingFromActivity, _exitWhy);
            _choosingFrom = Goal.Choosing;
            _choosingFromActivity = Activity.Choosing;
            _exitWhy = GoalWhy.None;
        }
        else
        {
            (from, fromActivity, exit) = (Goal, _activity, GoalWhy.None);
        }

        Goal = to;
        _activity = ActivityOf(to, _visitKind);
        var counters = Counters;
        counters.Why[(int)exit]++;
        counters.Why[(int)why]++;
        if (from != to)
        {
            counters.GoalChanges++;
        }

        Trace?.OnGoal(tick, fromActivity, _activity, exit, why, subject, metres);
    }

    /// <summary>A choice made straight from a goal that ran out, without passing through <see cref="Goal.Choosing"/>: an idle's end.</summary>
    private void ChoosingFromHere()
    {
        if (Goal != Goal.Choosing)
        {
            _choosingFrom = Goal;
            _choosingFromActivity = _activity;
            _exitWhy = Goal == Goal.Idling ? GoalWhy.IdleOver : GoalWhy.None;
            Goal = Goal.Choosing;
            _activity = Activity.Choosing;
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
