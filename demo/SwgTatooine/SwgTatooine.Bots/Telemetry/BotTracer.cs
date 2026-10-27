using System;
using System.Text.Json;
using System.Threading;
using SwgTatooine.Bots.Brain;

namespace SwgTatooine.Bots.Telemetry;

/// <summary>
/// One traced bot's recent life (bot-telemetry.md): a ring of its last decisions — goal transitions with their reasons, walk events, changes of intent —
/// a trail of positions a second apart, and its time per goal. Written by the bot's driving thread, read by the reporter's.
/// </summary>
/// <remarks>
/// <b>Fixed rings of structs, so recording allocates nothing.</b> Each entry carries a sequence number, and the reporter sends only what is newer than
/// the last report the dashboard accepted — so a report is a few seconds of events, not the whole ring every time.
/// </remarks>
public sealed class BotTracer : IBrainTrace
{
    /// <summary>How many events are kept.</summary>
    public const int EventCapacity = 200;

    /// <summary>How many position samples are kept: four minutes at one a second.</summary>
    public const int TrailCapacity = 240;

    /// <summary>An intent is recorded when its heading turns by more than this, radians (30°).</summary>
    public const float TurnRad = MathF.PI / 6f;

    private readonly Lock _gate = new();
    private readonly TraceEvent[] _events = new TraceEvent[EventCapacity];
    private readonly TrailPoint[] _trail = new TrailPoint[TrailCapacity];
    private readonly long[] _goalTicks = new long[Enum.GetValues<Goal>().Length];
    private readonly long[] _intents = new long[Enum.GetValues<IntentKind>().Length];
    private readonly long _sampleTicks;
    private long _seq;
    private long _trailSeq;
    private long _goalSince;
    private long _lastSampleAt = long.MinValue / 2;
    private (IntentKind Kind, byte Speed, float Heading) _lastIntent = (IntentKind.None, 0, float.NaN);
    private long _reversals;

    /// <summary>A tracer for bot <paramref name="index"/>, driven at <paramref name="ticksPerSecond"/>.</summary>
    public BotTracer(int index, double ticksPerSecond)
    {
        Index = index;
        TicksPerSecond = ticksPerSecond > 0d ? ticksPerSecond : 1d;
        _sampleTicks = Math.Max(1L, (long)Math.Round(TicksPerSecond));
    }

    /// <summary>The bot's index in the fleet.</summary>
    public int Index { get; }

    /// <summary>The driver's rate, which turns ticks into seconds.</summary>
    public double TicksPerSecond { get; }

    // What the bot is now, as of its last decision.
    private Goal _goal = Goal.Choosing;
    private long _tick;
    private float _x;
    private float _z;
    private int _realm;
    private int _level;
    private bool _mounted;
    private bool _home;
    private uint _netId;
    private long _goalChanges;
    private long _stucks;
    private long _legs;

    /// <summary>The last event sequence number and trail sequence number the dashboard accepted: what the next report starts after.</summary>
    public long SentEvents;

    public long SentTrail;

    /// <inheritdoc />
    public void OnGoal(long tick, Goal from, Goal to, GoalWhy exit, GoalWhy enter, uint subject, float metres)
    {
        lock (_gate)
        {
            _goalTicks[(int)from] += Math.Max(0L, tick - _goalSince);
            _goalSince = tick;
            _goal = to;
            _goalChanges += from != to ? 1 : 0;
            Add(new TraceEvent { Tick = tick, Kind = TraceKind.Goal, From = from, To = to, Exit = exit, Enter = enter, Subject = subject, Metres = metres });
        }
    }

    /// <inheritdoc />
    public void OnLeg(long tick, LegWhy what, int points, float metres)
    {
        lock (_gate)
        {
            _stucks += what is LegWhy.Stuck or LegWhy.TravelStuck ? 1 : 0;
            _legs += what == LegWhy.Planned ? 1 : 0;
            Add(new TraceEvent { Tick = tick, Kind = TraceKind.Leg, Leg = what, Points = points, Metres = metres });
        }
    }

    /// <summary>
    /// What the bot did with its decision: where it stands, the intent it sent — recorded when it is a different command, speed or a turn of more than
    /// <see cref="TurnRad"/>, or a reversal — and, once a second, a point of its trail.
    /// </summary>
    /// <param name="tick">The fleet's tick.</param>
    /// <param name="world">What the bot was sent.</param>
    /// <param name="intent">What it decided.</param>
    /// <param name="home">Whether it lives in the home city.</param>
    /// <param name="heading">The intent's direction of motion, NaN for none (<see cref="MoveWatch.See"/>).</param>
    /// <param name="reversal">Whether it reversed the previous move.</param>
    /// <param name="netId">The body's wire netId as the bot's session was told it (its <c>SELF</c> block), 0 for none: what the dashboard's "watch" hands
    /// the god camera. Read every decision, because a body that crosses into another realm is replicated under another one.</param>
    public void Observe(long tick, in Perceived world, Intent intent, bool home, float heading, bool reversal, uint netId)
    {
        lock (_gate)
        {
            _tick = tick;
            _netId = netId;
            _x = world.X;
            _z = world.Z;
            _realm = world.Realm;
            _level = world.Level;
            _mounted = world.Mounted;
            _home = home;
            if (tick - _lastSampleAt >= _sampleTicks && world.HasBody)
            {
                _lastSampleAt = tick;
                _trailSeq++;
                _trail[(_trailSeq - 1) % TrailCapacity] = new TrailPoint(_trailSeq, tick, world.X, world.Z, _goal, world.Realm, world.Mounted);
            }

            if (intent.Kind == IntentKind.None)
            {
                return;
            }

            _intents[(int)intent.Kind]++;
            _reversals += reversal ? 1 : 0;
            var turned = !float.IsNaN(heading) && (float.IsNaN(_lastIntent.Heading) || MoveWatch.Angle(heading, _lastIntent.Heading) > TurnRad);
            if (intent.Kind != _lastIntent.Kind || intent.SpeedClass != _lastIntent.Speed || turned || reversal)
            {
                _lastIntent = (intent.Kind, intent.SpeedClass, heading);
                Add(new TraceEvent
                {
                    Tick = tick, Kind = TraceKind.Intent, Intent = intent.Kind, Speed = intent.SpeedClass, Heading = heading, Reversal = reversal,
                    Subject = intent.NetId,
                });
            }
        }
    }

    private void Add(TraceEvent e)
    {
        e.Seq = ++_seq;
        _events[(e.Seq - 1) % EventCapacity] = e;
    }

    /// <summary>
    /// Writes this bot as the report's <c>traced[]</c> entry (BotReport.cs on the dashboard), with the events and trail points newer than
    /// <see cref="SentEvents"/> and <see cref="SentTrail"/>.
    /// </summary>
    /// <returns>The last event and trail sequence numbers written, to be stored once the dashboard accepts the report.</returns>
    public (long Events, long Trail) Write(Utf8JsonWriter w)
    {
        lock (_gate)
        {
            var seconds = 1d / TicksPerSecond;
            w.WriteStartObject();
            w.WriteNumber("index", Index);
            w.WriteString("goal", _goal.ToString());
            w.WriteNumber("goalForS", Math.Round(Math.Max(0L, _tick - _goalSince) * seconds, 1));
            w.WriteNumber("tick", _tick);
            w.WriteNumber("cl", _level);
            w.WriteNumber("x", Math.Round(_x, 1));
            w.WriteNumber("z", Math.Round(_z, 1));
            w.WriteNumber("realm", _realm);
            w.WriteNumber("netId", _netId);
            w.WriteBoolean("mounted", _mounted);
            w.WriteBoolean("home", _home);
            w.WriteNumber("goalChanges", _goalChanges);
            w.WriteNumber("reversals", _reversals);
            w.WriteNumber("stuck", _stucks);
            w.WriteNumber("legs", _legs);

            w.WriteStartObject("goalS");
            foreach (var goal in Enum.GetValues<Goal>())
            {
                var ticks = _goalTicks[(int)goal] + (goal == _goal ? Math.Max(0L, _tick - _goalSince) : 0L);
                if (ticks > 0)
                {
                    w.WriteNumber(goal.ToString(), Math.Round(ticks * seconds, 1));
                }
            }

            w.WriteEndObject();
            w.WriteStartObject("intents");
            foreach (var kind in Enum.GetValues<IntentKind>())
            {
                if (_intents[(int)kind] > 0)
                {
                    w.WriteNumber(kind.ToString(), _intents[(int)kind]);
                }
            }

            w.WriteEndObject();

            w.WriteStartArray("events");
            var first = Math.Max(SentEvents, _seq - EventCapacity) + 1;
            for (var s = first; s <= _seq; s++)
            {
                WriteEvent(w, in _events[(s - 1) % EventCapacity]);
            }

            w.WriteEndArray();
            w.WriteStartArray("trail");
            for (var s = Math.Max(SentTrail, _trailSeq - TrailCapacity) + 1; s <= _trailSeq; s++)
            {
                var p = _trail[(s - 1) % TrailCapacity];
                w.WriteStartObject();
                w.WriteNumber("s", p.Seq);
                w.WriteNumber("t", p.Tick);
                w.WriteNumber("x", Math.Round(p.X, 1));
                w.WriteNumber("z", Math.Round(p.Z, 1));
                w.WriteString("g", p.Goal.ToString());
                w.WriteNumber("realm", p.Realm);
                w.WriteBoolean("mounted", p.Mounted);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            return (_seq, _trailSeq);
        }
    }

    private static void WriteEvent(Utf8JsonWriter w, in TraceEvent e)
    {
        w.WriteStartObject();
        w.WriteNumber("s", e.Seq);
        w.WriteNumber("t", e.Tick);
        switch (e.Kind)
        {
            case TraceKind.Goal:
                w.WriteString("k", "goal");
                w.WriteString("from", e.From.ToString());
                w.WriteString("to", e.To.ToString());
                w.WriteString("exit", e.Exit.ToString());
                w.WriteString("enter", e.Enter.ToString());
                w.WriteNumber("subject", e.Subject);
                w.WriteNumber("m", Math.Round(e.Metres, 1));
                break;
            case TraceKind.Leg:
                w.WriteString("k", "leg");
                w.WriteString("leg", e.Leg.ToString());
                w.WriteNumber("n", e.Points);
                w.WriteNumber("m", Math.Round(e.Metres, 1));
                break;
            default:
                w.WriteString("k", "intent");
                w.WriteString("intent", e.Intent.ToString());
                w.WriteNumber("speed", e.Speed);
                if (!float.IsNaN(e.Heading))
                {
                    w.WriteNumber("heading", Math.Round(e.Heading, 2));
                }

                w.WriteBoolean("reversal", e.Reversal);
                w.WriteNumber("subject", e.Subject);
                break;
        }

        w.WriteEndObject();
    }

    private enum TraceKind : byte
    {
        Goal,
        Leg,
        Intent,
    }

    private struct TraceEvent
    {
        public long Seq;
        public long Tick;
        public TraceKind Kind;
        public Goal From;
        public Goal To;
        public GoalWhy Exit;
        public GoalWhy Enter;
        public LegWhy Leg;
        public IntentKind Intent;
        public byte Speed;
        public bool Reversal;
        public float Heading;
        public uint Subject;
        public float Metres;
        public int Points;
    }

    private readonly record struct TrailPoint(long Seq, long Tick, float X, float Z, Goal Goal, int Realm, bool Mounted);
}

/// <summary>
/// The direction a bot's navigation sends it, and its REVERSALS: a move whose heading is more than <see cref="ReversalRad"/> off the previous move's,
/// within a few seconds of it — "forward, stop, backward", the erratic bot Loïc saw (2026-10-26). A fight is not navigation: an attack's kiting heading
/// is not counted. Kept for every bot, so it is a value type and allocates nothing.
/// </summary>
public struct MoveWatch
{
    /// <summary>135°: past a turn, short of nothing but a change of mind.</summary>
    public const float ReversalRad = 3f * MathF.PI / 4f;

    private float _heading;
    private long _at;
    private bool _moving;

    /// <summary>Navigation reversals seen.</summary>
    public long Reversals;

    /// <summary>Notes one decision's intent.</summary>
    /// <param name="tick">The fleet's tick.</param>
    /// <param name="world">Where the body stands, for a <c>MoveTo</c>'s direction.</param>
    /// <param name="intent">What was sent.</param>
    /// <param name="windowTicks">How recent the previous move must be for a turn to count as a reversal.</param>
    /// <param name="heading">The intent's direction of motion, or NaN when it does not move the body.</param>
    /// <returns>Whether this intent reversed the previous move.</returns>
    public bool See(long tick, in Perceived world, Intent intent, long windowTicks, out float heading)
    {
        heading = float.NaN;
        var navigation = false;
        switch (intent.Kind)
        {
            case IntentKind.Move when intent.SpeedClass > 0:
                heading = intent.Heading;
                navigation = true;
                break;
            case IntentKind.MoveTo when Routing.Distance(world.X, world.Z, intent.X, intent.Z) > 0.5d:
                heading = Routing.HeadingTo(world.X, world.Z, intent.X, intent.Z);
                navigation = true;
                break;
            case IntentKind.Attack when intent.SpeedClass > 0:
                heading = intent.Heading;
                break;
        }

        if (!navigation)
        {
            return false;
        }

        var reversed = _moving && tick - _at <= windowTicks && Angle(heading, _heading) > ReversalRad;
        Reversals += reversed ? 1 : 0;
        _heading = heading;
        _at = tick;
        _moving = true;
        return reversed;
    }

    /// <summary>The unsigned angle between two headings, radians, in [0, π].</summary>
    public static float Angle(float a, float b)
    {
        var d = MathF.Abs(MathF.IEEERemainder(a - b, MathF.PI * 2f));
        return d;
    }
}
