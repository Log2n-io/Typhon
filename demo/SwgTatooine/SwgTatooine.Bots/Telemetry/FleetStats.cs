using System;
using SwgTatooine.Bots.Brain;

namespace SwgTatooine.Bots.Telemetry;

/// <summary>The fleet's own counters beside a brain's, summed over every bot (bot-telemetry.md).</summary>
public enum FleetStat
{
    GoalChanges,
    StuckTicks,
    Hunts,
    MissionsAsked,
    MissionsTaken,
    MissionsCompleted,
    MissionsCancelled,
    Mounts,
    Dismounts,
    Reversals,

    // The bazaar (bazaar.md § 8): visits traded at, commands sent by kind, races lost (153), buys seen collected, refusals of every reason.
    MarketTrades,
    MarketListed,
    MarketVendored,
    MarketBought,
    MarketBuyLost,
    MarketWon,
    MarketCollected,
    MarketRefused,
}

/// <summary>A per-bot rate's spread over the fleet.</summary>
public readonly record struct Spread(double Mean, double P50, double P90, double Max);

/// <summary>
/// The whole fleet's statistics, summed bot by bot from counters every bot keeps anyway, with rates over a window of one to two minutes: goals now,
/// goal changes and reversals per bot-minute with their spread, reasons, walk events, missions, mounts.
/// </summary>
/// <remarks>
/// <b>Reused, so a report allocates nothing per bot.</b> The window is two baselines a minute apart: the rates are read against the older, which is then
/// one to two minutes old — long enough that a bot's few changes a minute are not quantised to zero or to thirty.
/// </remarks>
public sealed class FleetStats
{
    /// <summary>How often the newer baseline is taken, and so the shortest window once the run is that old.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private static readonly int StatCount = Enum.GetValues<FleetStat>().Length;
    private static readonly int GoalCount = Enum.GetValues<Goal>().Length;
    private static readonly int WhyCount = Enum.GetValues<GoalWhy>().Length;
    private static readonly int LegCount = Enum.GetValues<LegWhy>().Length;

    private readonly long[] _botGoalChanges;
    private readonly long[] _botReversals;
    private readonly bool[] _botSeen;
    private readonly double[] _scratch;
    private Baseline _older;
    private Baseline _newer;

    /// <summary>Statistics for a fleet of at most <paramref name="capacity"/> slots, its window starting at <paramref name="start"/>.</summary>
    public FleetStats(int capacity, DateTime start)
    {
        Capacity = Math.Max(1, capacity);
        _botGoalChanges = new long[Capacity];
        _botReversals = new long[Capacity];
        _botSeen = new bool[Capacity];
        _scratch = new double[Capacity];
        _older = new Baseline(Capacity, start);
        _newer = new Baseline(Capacity, start);
    }

    public int Capacity { get; }

    /// <summary>Bots summed in the last sample.</summary>
    public int Bots { get; private set; }

    /// <summary>Bots per goal now, by <see cref="Goal"/>.</summary>
    public long[] Goals { get; } = new long[GoalCount];

    /// <summary>Totals since the start, by <see cref="FleetStat"/>.</summary>
    public long[] Stats { get; } = new long[StatCount];

    /// <summary>Goal reasons since the start, by <see cref="GoalWhy"/>.</summary>
    public long[] Why { get; } = new long[WhyCount];

    /// <summary>Walk events since the start, by <see cref="LegWhy"/>.</summary>
    public long[] Legs { get; } = new long[LegCount];

    /// <summary>The window the rates are over, seconds.</summary>
    public double WindowSeconds { get; private set; }

    /// <summary>The fleet's rates per minute over the window, by the same indices.</summary>
    public double[] StatsPerMin { get; } = new double[StatCount];

    public double[] WhyPerMin { get; } = new double[WhyCount];

    public double[] LegsPerMin { get; } = new double[LegCount];

    /// <summary>Goal changes per bot-minute over the window, across the bots: the "changes its mind too often" measure.</summary>
    public Spread GoalChangesPerBotMin { get; private set; }

    /// <summary>Navigation reversals per bot-minute over the window (<see cref="MoveWatch"/>).</summary>
    public Spread ReversalsPerBotMin { get; private set; }

    /// <summary>The bazaar's refusals since the start, by slot (<see cref="BotSwarm.MarketRefusalSlot"/>): what <see cref="FleetStat.MarketRefused"/> sums.</summary>
    public long[] MarketRefusedBy { get; } = new long[BotSwarm.MarketRefusals];

    /// <summary>Adds one bot's bazaar refusals, by reason.</summary>
    public void AddMarketRefusals(ReadOnlySpan<long> byReason)
    {
        for (var i = 0; i < byReason.Length && i < MarketRefusedBy.Length; i++)
        {
            MarketRefusedBy[i] += byReason[i];
        }
    }

    /// <summary>Starts a sample.</summary>
    public void Begin()
    {
        Bots = 0;
        Array.Clear(Goals);
        Array.Clear(Stats);
        Array.Clear(Why);
        Array.Clear(Legs);
        Array.Clear(MarketRefusedBy);
        Array.Clear(_botSeen);
    }

    /// <summary>Adds one bot, at fleet slot <paramref name="slot"/>.</summary>
    public void Add(int slot, Goal goal, BrainCounters brain, ReadOnlySpan<long> stats)
    {
        Bots++;
        Goals[(int)goal]++;
        for (var i = 0; i < stats.Length && i < StatCount; i++)
        {
            Stats[i] += stats[i];
        }

        Stats[(int)FleetStat.GoalChanges] += brain.GoalChanges;
        Stats[(int)FleetStat.StuckTicks] += brain.StuckTicks;
        for (var i = 0; i < WhyCount; i++)
        {
            Why[i] += brain.Why[i];
        }

        for (var i = 0; i < LegCount; i++)
        {
            Legs[i] += brain.Legs[i];
        }

        if ((uint)slot < (uint)Capacity)
        {
            _botSeen[slot] = true;
            _botGoalChanges[slot] = brain.GoalChanges;
            _botReversals[slot] = stats.Length > (int)FleetStat.Reversals ? stats[(int)FleetStat.Reversals] : 0;
        }
    }

    /// <summary>Ends the sample: the rates against the older baseline, then the baselines move on once the newer is a window old.</summary>
    public void End(DateTime now)
    {
        WindowSeconds = Math.Max(0d, (now - _older.At).TotalSeconds);
        var minutes = WindowSeconds / 60d;
        Rates(Stats, _older.Stats, StatsPerMin, minutes);
        Rates(Why, _older.Why, WhyPerMin, minutes);
        Rates(Legs, _older.Legs, LegsPerMin, minutes);
        GoalChangesPerBotMin = PerBot(_botGoalChanges, _older.BotGoalChanges, minutes);
        ReversalsPerBotMin = PerBot(_botReversals, _older.BotReversals, minutes);

        if (now - _newer.At >= Window)
        {
            (_older, _newer) = (_newer, _older);
            _newer.Take(this, now);
        }
    }

    private static void Rates(long[] now, long[] then, double[] perMin, double minutes)
    {
        for (var i = 0; i < now.Length; i++)
        {
            perMin[i] = minutes > 0d ? Math.Max(0L, now[i] - then[i]) / minutes : 0d;
        }
    }

    private Spread PerBot(long[] now, long[] then, double minutes)
    {
        var n = 0;
        var sum = 0d;
        for (var slot = 0; slot < Capacity; slot++)
        {
            if (!_botSeen[slot])
            {
                continue;
            }

            // A slot whose bot was replaced counts from zero: its counters started again.
            var delta = now[slot] >= then[slot] ? now[slot] - then[slot] : now[slot];
            var rate = minutes > 0d ? delta / minutes : 0d;
            _scratch[n++] = rate;
            sum += rate;
        }

        if (n == 0)
        {
            return default;
        }

        Array.Sort(_scratch, 0, n);
        return new Spread(sum / n, _scratch[n / 2], _scratch[(int)Math.Floor(0.9 * (n - 1))], _scratch[n - 1]);
    }

    private sealed class Baseline(int capacity, DateTime at)
    {
        public DateTime At = at;
        public readonly long[] Stats = new long[StatCount];
        public readonly long[] Why = new long[WhyCount];
        public readonly long[] Legs = new long[LegCount];
        public readonly long[] BotGoalChanges = new long[capacity];
        public readonly long[] BotReversals = new long[capacity];

        public void Take(FleetStats from, DateTime at)
        {
            At = at;
            from.Stats.CopyTo(Stats, 0);
            from.Why.CopyTo(Why, 0);
            from.Legs.CopyTo(Legs, 0);
            from._botGoalChanges.CopyTo(BotGoalChanges, 0);
            from._botReversals.CopyTo(BotReversals, 0);
        }
    }
}
