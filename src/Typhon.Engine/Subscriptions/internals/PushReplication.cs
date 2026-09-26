using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>One copy of a session's delivered window in the flat implementation: one row of up to 16 cells per <see cref="ushort"/>, inline.</summary>
[InlineArray(ReplicationGrid.MaxWindow)]
internal struct WindowRows
{
    private ushort _row;
}

/// <summary>What the push path remembers about one session: its visibility anchor and which cells around it it has been given.</summary>
internal struct PushSessionState
{
    public ushort Generation;
    public bool Bound;
    public bool Anchored;
    public bool NeedsReset;
    public double AnchorX;
    public double AnchorY;
    public double AnchorZ;
    public int OriginX;
    public int OriginY;
    public int OriginZ;

    /// <summary>The radius the committed known-set was built with; zero until the first frame, which takes the one it is gathered at.</summary>
    public double Radius;

    /// <summary>The flat implementation's delivered window, one row per <see cref="ushort"/>; the deep implementation's lives in a slab.</summary>
    public WindowRows D;

    /// <summary>The tick of the last committed frame: the push log replays every event after it.</summary>
    public uint LastTick;

    /// <summary>
    /// A World session: every cell whose key is below this one has been delivered — its whole known-set. <see cref="ulong.MaxValue"/> once the walk has
    /// passed the last occupied cell, so a cell occupied later is known through its events rather than delivered again.
    /// </summary>
    public ulong Cursor;
    public ulong PCursor;

    // Computed by the gather, applied only when the frame is published (SUB-03's discipline: a frame that was not sent changes nothing).
    public double PAnchorX;
    public double PAnchorY;
    public double PAnchorZ;
    public int POriginX;
    public int POriginY;
    public int POriginZ;
    public double PRadius;
    public WindowRows P;

    // The LOD level (09 § 10), committed with the frame like the anchor: Level is what every change held back so far was scheduled by, PLevel what this
    // frame's gather moved it to (towards the link's TargetLevel).
    public byte Level;
    public byte PLevel;

    /// <summary>After the level fell: the level whose periods a flush's history still spans, until <see cref="WideUntil"/> (<see cref="LodBands.AtLevel"/>).</summary>
    public byte WideLevel;
    public uint WideUntil;
}

/// <summary>
/// What the budget loop (09 § 10) remembers about a session's link: engine-wide, by session slot, so a realm switch — a door — keeps a congested session's
/// level and rate (R4.3, 12-realms § 1.5). The geometry a level applies to is the realm's (<see cref="PushSessionState"/>).
/// </summary>
internal struct PushLinkState
{
    /// <summary>The session generation this state belongs to; 0 before the session's first placement.</summary>
    public ushort Generation;

    /// <summary>Where the budget loop wants the session's LOD level: its own level plus the overload step.</summary>
    public byte TargetLevel;

    // The level the budget loop asked for and the steps (a ShrinkSteps-th of the radius each) it took off the radius at the last level, the bytes/s EWMA,
    // ticks spent over the budget and under its lower mark, and the last tick it was fed.
    public byte BudgetLevel;
    public byte Shrink;
    public float Rate;
    public ushort Over;
    public ushort Under;
    public uint PacedTick;
}

/// <summary>
/// Push replication (<c>claude/design/Subscriptions/research/push-model.md</c> § 4): the developer marks what changed, the engine encodes it
/// once and fans it out to the sessions around it, and a session's knowledge of an entity is a function of geometry rather than a per-session table.
/// </summary>
/// <remarks>
/// <para><b>The known-set is geometric.</b> For session <c>s</c> with anchor <c>a</c> and entity <c>e</c> last projected at <c>v</c>:
/// <c>known(s, e) ⟺ |a − v| ≤ R ∧ delivered(s, cell(v))</c>. Every event that moves <c>a</c>, moves <c>v</c> or delivers a cell emits exactly the enters
/// and leaves that keep the equality true, so nothing per (session, entity) is stored or probed.</para>
/// <para><b>Per tick:</b> the blocks step marks the pushed slots (the fence's structure words: spawns, destroys, <c>WriteSpatial</c>, and
/// <see cref="SubscriptionsCommands.Replicate{TArchetype}(in ClusterRef{TArchetype}, int)"/>) plus the slots still extrapolating; projection encodes them and
/// emits an event each; the push index sorts the events by cell; each push session's frame is gathered from the cells around it.</para>
/// <para><b>One model, two implementations</b> (<c>claude/design/Subscriptions/10-phase15-3d-groundwork.md</c> § 3.5, L6). This class is what is shared —
/// the blocks step, the validator, the occupancy, the counters — and the API; <see cref="PushReplication{TEvent}"/> is the geometry, instantiated with
/// <see cref="PushEvent"/> for a grid one cell deep and <see cref="PushEvent3"/> otherwise, chosen once by <see cref="Create"/>.</para>
/// </remarks>
internal abstract unsafe partial class PushReplication
{
    private protected readonly CompiledProjectionPlan[] _plans;
    private protected readonly ArchetypeReplicationState[] _states;
    private protected readonly int[] _pushIndices;

    /// <summary>The hub that collects this realm's blocks and routes them here (R4.1); set when the hub starts serving the realm.</summary>
    internal PushHub Hub;

    /// <summary>Per plan index: this realm's live entities of the archetype have all been pushed once — its bootstrap (R4.1: per realm).</summary>
    internal readonly bool[] Bootstrapped;

    /// <summary>Per plan index, this tick: every live entity of the archetype in this realm is pushed (bootstrap, a gap, or an undescribed fence).</summary>
    internal readonly bool[] EverythingThisTick;

    /// <summary>Whether this realm's blocks step follows a tick its track did not run for (<see cref="BeginRealmTick"/>).</summary>
    internal bool ResumedThisTick { get; private set; }

    // Per plan index: the cold-entry offset of the entity's last projected position.
    private protected readonly int[] _positionOffset;

    /// <summary>The cold-entry offset of a push archetype's last projected position.</summary>
    public int PositionOffset(int archetype) => _positionOffset[archetype];


    // Position decode per archetype: axes 0 and 1, and axis 2 where the grid is deep and the codec has it — in a flat grid, or for a 2D codec, the
    // geometric z is 0 (10 § 3.4).
    private readonly double[] _minX;
    private readonly double[] _minY;
    private readonly double[] _minZ;
    private readonly double[] _stepX;
    private readonly double[] _stepY;
    private readonly double[] _stepZ;
    private readonly int[] _axisBytes;

    /// <summary>Per plan index: the archetype's positions have a third axis in this grid — a 3D codec in a deep grid.</summary>
    private protected readonly bool[] _hasZ;

    /// <summary>Per plan index: a centimetre plus the coarsest quantization step, the margin a pruning proof on raw cluster bounds needs.</summary>
    private protected readonly double[] _pruneMargin;

    // Per archetype, what v̂'s lag behind the true position costs the cluster proofs (09 § 2, SUB-20): a cluster's box bounds true positions and v̂ lies up
    // to h_A from them, so the cell query pads by 1 m + h_A and the pruning margin by 1 cm + h_A.
    private protected readonly double[] _queryPad;
    private protected readonly double[] _skipMargin;

    /// <summary>
    /// The realm this replication serves (Realms R4.2): realm 0 until sessions are placed in realms (R4.3). A cluster of any other realm is never pushed,
    /// so nothing of it is ever known to a session here (SUB-28).
    /// </summary>
    public readonly ushort ServedRealm;

    /// <summary>The served realm's codecs: the frame each archetype's positions are quantized over and decoded with (SUB-30).</summary>
    public readonly RealmCodecs Codecs;

    /// <summary>The visibility radius: the largest a session takes, which sizes its window.</summary>
    public readonly double Radius;

    /// <summary>The replication cell side, declared (<see cref="SubscriptionsOptions.ReplicationCellM"/>).</summary>
    public readonly double CellSize;

    /// <summary>The delivered window's half width, in cells.</summary>
    public readonly int Half;

    /// <summary>The delivered window's width per axis, in cells (<c>2 · Half + 1</c>, at most 16).</summary>
    public readonly int Window;

    /// <summary>Whether this is the deep implementation (three axes).</summary>
    public abstract bool Deep { get; }

    private protected readonly double _gridMinX;
    private protected readonly double _gridMinY;
    private protected readonly double _gridMinZ;

    // The world's upper bounds: a decoded position is clamped into [grid min, world max] on every axis.
    private protected readonly double _worldMaxX;
    private protected readonly double _worldMaxY;
    private protected readonly double _worldMaxZ;
    private protected readonly int _gridW;
    private protected readonly int _gridH;
    private protected readonly int _gridD;


    /// <summary>
    /// Distance LOD (09 § 9–10): the fold's phase — the smallest period any session is gathered with, whose flush ticks are every band's — and its window,
    /// the history a flush covers. Both 0 when nothing is folded. The profiles' bands set a floor at <c>Start</c>; the sessions' LOD levels lower the phase
    /// and widen the window to the log's depth while any is above zero (<see cref="ResolveFar"/>), once per tick, before the fold.
    /// </summary>
    public int FarPhase { get; private set; }

    /// <inheritdoc cref="FarPhase"/>
    public int FarWindow { get; private set; }

    // The profiles' fold, from Start; the committed LOD levels' census, [level] = sessions; the last tick a level fell; the tick ResolveFar last ran.
    private int _declaredPhase;
    private int _declaredWindow;
    private readonly int[] _levelSessions = new int[MaxLevel + 1];
    private uint _lastLowered;
    private bool _lowered;
    private uint _farResolvedTick = uint.MaxValue;

    /// <summary>The highest LOD level (09 § 10): every period doubled three times, up to the log's depth.</summary>
    public const int MaxLevel = 3;

    /// <summary>The budget loop's EWMA time constant, in seconds.</summary>
    public const double RateTauSeconds = 0.5;

    /// <summary>How long a session's rate stays over its budget before its level rises, in seconds.</summary>
    public const double RaiseAfterSeconds = 1d;

    /// <summary>How long a session's rate stays under <see cref="LowerBelow"/> of its budget before its level falls, in seconds.</summary>
    public const double LowerAfterSeconds = 3d;

    /// <summary>The deadband's lower mark, as a fraction of the budget.</summary>
    public const double LowerBelow = 0.7;

    /// <summary>LOD levels raised and lowered by the budget loop — cumulative.</summary>
    public long LevelRaises;

    /// <inheritdoc cref="LevelRaises"/>
    public long LevelLowers;

    /// <summary>Sets the fold's phase and window from the profiles' bands (<see cref="SubscriptionProfiles.FarFold"/>). Before the first tick.</summary>
    /// <param name="phase">The smallest declared period, or 0.</param>
    /// <param name="window">The largest declared period, at most <see cref="LogDepth"/>, or 0.</param>
    public void ConfigureFar(int phase, int window)
    {
        if (phase > 1 && (window < phase || window > LogDepth))
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, $"a fold window is between its phase and the log's depth, {LogDepth}");
        }

        _declaredPhase = phase > 1 ? phase : 0;
        _declaredWindow = phase > 1 ? window : 0;
        FarPhase = _declaredPhase;
        FarWindow = _declaredWindow;
    }

    /// <summary>
    /// This tick's fold (09 § 10): the declared one, or — while a session's committed level is above zero — the phase of the lowest level's shortest period
    /// over the highest level's longest period; for <see cref="LogDepth"/> ticks after a level fell, the log's whole depth, which the widened windows need. Serial, before the fold; the census it reads is every
    /// commit up to the previous tick's, which is the level each session's gather this tick holds its changes to.
    /// </summary>
    private protected void ResolveFar()
    {
        if (_farResolvedTick == _tick)
        {
            return;
        }

        _farResolvedTick = _tick;
        var phase = _declaredPhase;
        var window = _declaredWindow;
        int lowest = 0, highest = 0;
        for (var level = 1; level <= MaxLevel; level++)
        {
            if (Volatile.Read(ref _levelSessions[level]) > 0)
            {
                lowest = lowest == 0 ? level : lowest;
                highest = level;
            }
        }

        if (lowest > 0)
        {
            // A level's shortest period is 2^level with no declared band, and longer with one: 2^lowest divides every period in use. Its longest is the
            // declared longest (or the implicit band's 1) doubled per level, up to the log's depth: the history no flush needs more of.
            phase = phase > 1 ? Math.Min(phase, 1 << lowest) : 1 << lowest;
            window = Math.Min(LogDepth, Math.Max(_declaredWindow, 1) << highest);
        }

        if (phase > 1 && Volatile.Read(ref _lowered) && _tick - Volatile.Read(ref _lastLowered) <= LogDepth)
        {
            window = LogDepth;
        }

        FarPhase = phase;
        FarWindow = window;
    }

    /// <summary>Whether any session's committed LOD level is above zero, by the census.</summary>
    public bool LevelsInUse => _levelSessions[1] + _levelSessions[2] + _levelSessions[3] != 0;

    /// <summary>How often the census is recounted, in ticks: a power of two.</summary>
    public const int RecountEvery = 64;

    /// <summary>
    /// Recounts the census from the bound sessions every <see cref="RecountEvery"/> ticks while it says levels are in use. The commits keep it by increments;
    /// what they miss is a session that closed, or whose slot was rebound, at a level — which only ever leaves the census too high, so the fold's phase too
    /// low and its window too wide: a superset, costlier, never wrong. The recount bounds that to <see cref="RecountEvery"/> ticks without reading every
    /// session's state serially every tick. Serial, in the frame prologue, before any commit of the tick.
    /// </summary>
    /// <param name="sessions">The tick's push sessions.</param>
    /// <param name="count">How many.</param>
    public void RecountLevels(SessionId[] sessions, int count)
    {
        if (!LevelsInUse || (_tick & (RecountEvery - 1)) != 0)
        {
            return;
        }

        Span<int> census = stackalloc int[MaxLevel + 1];
        for (var i = 0; i < count; i++)
        {
            var session = sessions[i];
            ref var st = ref _sessions[L(session)];
            if (st.Bound && st.Generation == session.Generation)
            {
                census[st.Level]++;
            }
        }

        for (var level = 0; level <= MaxLevel; level++)
        {
            _levelSessions[level] = census[level];
        }
    }

    /// <summary>A session's committed LOD level moves with its frame: the census follows, and a fall widens the windows for <see cref="LogDepth"/> ticks.</summary>
    private protected void CommitLevel(ref PushSessionState st)
    {
        if (st.PLevel == st.Level)
        {
            return;
        }

        Interlocked.Decrement(ref _levelSessions[st.Level]);
        Interlocked.Increment(ref _levelSessions[st.PLevel]);
        if (st.PLevel < st.Level)
        {
            // Every entity's next flush at the lower level falls within LogDepth ticks of its last one at the higher: until then, it spans the higher's.
            st.WideLevel = (byte)Math.Max(st.Level, Widened(in st, _tick) ? st.WideLevel : 0);
            st.WideUntil = _tick + LogDepth;
            Volatile.Write(ref _lastLowered, _tick);
            Volatile.Write(ref _lowered, true);
        }

        st.Level = st.PLevel;
    }

    /// <summary>Whether a session's windows are still widened by a fall, wrap-safe: <c>WideUntil</c> is at most <see cref="LogDepth"/> ticks ahead.</summary>
    private protected static bool Widened(in PushSessionState st, uint tick) => st.WideLevel > 0 && (int)(st.WideUntil - tick) >= 0;

    /// <summary>The LOD level a session's next frame is gathered at: its enter budget is <c>EnterBudgetPerFrame >> level</c> (09 § 10).</summary>
    /// <param name="session">The session.</param>
    /// <returns>The level, 0 for an unbound session.</returns>
    public int TargetLevelOf(SessionId session)
    {
        ref var link = ref LinkOf(session);
        return link.Generation == session.Generation ? link.TargetLevel : 0;
    }

    /// <summary>A session's link state (engine-wide, by session slot); the caller checks its generation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected ref PushLinkState LinkOf(SessionId session) => ref Hub.Links[session.Slot];

    /// <summary>Tests only: a session's committed LOD level.</summary>
    internal int LevelOf(SessionId session)
    {
        ref var st = ref _sessions[L(session)];
        return st.Bound && st.Generation == session.Generation ? st.Level : 0;
    }

    /// <summary>Tests only: a session's bytes/s EWMA.</summary>
    internal double RateOf(SessionId session)
    {
        ref var link = ref LinkOf(session);
        return link.Generation == session.Generation ? link.Rate : 0d;
    }

    /// <summary>
    /// The overload step (09 § 10): 1 while the runtime's tick multiplier is above 1, 0 otherwise — every Sphere session's level rises by it. Set by the frame
    /// prologue each tick, from the multiplier the tick started with; it falls back when the overload detector's own hold releases the multiplier.
    /// </summary>
    public int OverloadStep { get; set; }

    /// <summary>The last resort's step, as a fraction of the session's own radius: 1/16, so a step moves the rate by about 13 % (a disc's area).</summary>
    public const int ShrinkSteps = 16;

    /// <summary>The most steps the last resort takes: half the radius.</summary>
    public const int MaxShrink = ShrinkSteps / 2;

    /// <summary>Last-resort radius steps taken off and given back by the budget loop — cumulative.</summary>
    public long RadiusShrinks;

    /// <inheritdoc cref="RadiusShrinks"/>
    public long RadiusGrows;

    /// <summary>Tests only: the steps a session's radius is short of its own, as the budget loop left them.</summary>
    internal int ShrinkOf(SessionId session)
    {
        ref var link = ref LinkOf(session);
        return link.Generation == session.Generation ? link.Shrink : 0;
    }

    /// <summary>Tests only: a session's committed radius — its own, less any last-resort shrink.</summary>
    internal double RadiusOf(SessionId session)
    {
        ref var st = ref _sessions[L(session)];
        return st.Bound && st.Generation == session.Generation ? st.Radius : 0d;
    }

    /// <summary>Tests only: sets the steps taken off a session's radius, as the budget loop would at its last level.</summary>
    internal void SetShrink(SessionId session, int steps)
    {
        ref var link = ref LinkOf(session);
        if (link.Generation == session.Generation)
        {
            link.Shrink = (byte)Math.Clamp(steps, 0, MaxShrink);
        }
    }

    /// <summary>Tests only: the budget loop leaves every level where <see cref="SetTargetLevel"/> put it.</summary>
    internal bool LevelsPinned;

    /// <summary>Tests only: sets the level a session's next frames move to, as the budget loop would.</summary>
    internal void SetTargetLevel(SessionId session, int level)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)level, (uint)MaxLevel, nameof(level));
        ref var link = ref LinkOf(session);
        if (link.Generation == session.Generation)
        {
            link.TargetLevel = (byte)level;
        }
    }

    /// <summary>
    /// The budget loop (09 § 10), fed once per tick a session is served, with the bytes its frame published — zero for none. The level rises after the
    /// rate's EWMA has been over the budget for <see cref="RaiseAfterSeconds"/>, and falls after it has been under <see cref="LowerBelow"/> of it for
    /// <see cref="LowerAfterSeconds"/>; each move restarts both clocks. At the last level and still over, the radius loses a step instead — the last
    /// resort, never below <see cref="MaxLevel"/> — and under the lower mark it gets a step back before any level falls. A session with no budget goes back
    /// to level 0 and its own radius. The level the frames move to adds <see cref="OverloadStep"/>; it and the radius commit with them. O(1); only the
    /// session's own worker writes its state.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="bytes">What this tick's frame published.</param>
    /// <param name="budget">The session's budget in bytes per second; 0 for none.</param>
    /// <param name="tickSeconds">The live tick period.</param>
    public void Pace(SessionId session, int bytes, int budget, double tickSeconds)
    {
        ref var st = ref LinkOf(session);
        if (st.Generation != session.Generation || LevelsPinned)
        {
            return;
        }

        if (budget <= 0)
        {
            // No budget: level 0 (plus the overload step) at its own radius, and nothing written once there — a session with no budget pays one load.
            if (st.TargetLevel != OverloadStep || st.PacedTick != 0 || st.BudgetLevel != 0 || st.Shrink != 0)
            {
                st.BudgetLevel = 0;
                st.Shrink = 0;
                st.TargetLevel = (byte)OverloadStep;
                st.Rate = 0;
                st.Over = 0;
                st.Under = 0;
                st.PacedTick = 0;
            }

            return;
        }

        var ticks = st.PacedTick == 0 ? 1u : Math.Clamp(_tick - st.PacedTick, 1u, 64u);
        st.PacedTick = _tick;
        var seconds = ticks * tickSeconds;
        var alpha = 1d - Math.Exp(-seconds / RateTauSeconds);
        st.Rate += (float)(alpha * ((bytes / seconds) - st.Rate));

        st.Over = st.Rate > budget ? (ushort)Math.Min(st.Over + ticks, ushort.MaxValue) : (ushort)0;
        st.Under = st.Rate < budget * LowerBelow ? (ushort)Math.Min(st.Under + ticks, ushort.MaxValue) : (ushort)0;
        if (st.Over * tickSeconds >= RaiseAfterSeconds && (st.BudgetLevel < MaxLevel || st.Shrink < MaxShrink))
        {
            if (st.BudgetLevel < MaxLevel)
            {
                st.BudgetLevel++;
                Interlocked.Increment(ref LevelRaises);
            }
            else
            {
                // The last resort: a step off the radius, down to half of it.
                st.Shrink++;
                Interlocked.Increment(ref RadiusShrinks);
            }

            st.Over = 0;
            st.Under = 0;
        }
        else if (st.Under * tickSeconds >= LowerAfterSeconds && (st.Shrink > 0 || st.BudgetLevel > 0))
        {
            // The radius first: eviction is what the loop gives back soonest — but only when the rate, scaled to the grown disc's area (a ball's volume
            // in 3D), stays under the lower mark: a radius that grew straight back over the budget would shrink again a second later and pay a sweep
            // and a refill every cycle. The two radii's ratio depends on the step count alone, (S − s + 1) / (S − s).
            if (st.Shrink > 0)
            {
                var grown = Math.Pow((ShrinkSteps - st.Shrink + 1d) / (ShrinkSteps - st.Shrink), _gridD > 1 ? 3 : 2);
                if (st.Rate * grown < budget * LowerBelow)
                {
                    st.Shrink--;
                    Interlocked.Increment(ref RadiusGrows);
                    st.Over = 0;
                    st.Under = 0;
                }
            }
            else
            {
                st.BudgetLevel--;
                Interlocked.Increment(ref LevelLowers);
                st.Over = 0;
                st.Under = 0;
            }
        }

        st.TargetLevel = (byte)Math.Min(MaxLevel, st.BudgetLevel + OverloadStep);
    }

    public long UpdatesDeferred;

    /// <summary>Distance LOD: far flushes produced — events flagged in place plus flush entries appended — cumulative.</summary>
    public long FarFlushes;

    /// <summary>Distance LOD: updates the inner crescent sent — held entities the anchor's move brought within R/2 — cumulative.</summary>
    public long FarCrescentStates;

    /// <summary>Distance LOD: time spent in <see cref="EndFarFold"/> — serial, in the frame prologue — cumulative, in Stopwatch ticks.</summary>
    public long FarEndTicks;

    /// <summary>How many ticks of indexes the push log keeps: a session that missed fewer frames than this catches up from them, an older one resets.</summary>
    /// <remarks>Covers <see cref="SkipPolicy.MaxDegradeLevel"/> (one frame in four) with room for a few back-pressure skips on top.</remarks>
    public const int LogDepth = 8;

    // Counters for the log's catch-up.
    public long LogCatchUps;
    public long LogCatchUpTicks;
    public long LogTooOld;
    public long LogAmbiguous;

    // By realm-local slot (R4.3): slot 0 is a sentinel no session ever holds — never bound, never written — so a session not placed in this realm reads an
    // unbound state wherever it is looked up. The others are handed out by Join and given back by Release; the arrays grow ×2 from a few.
    private protected PushSessionState[] _sessions;
    private SessionId[] _slotOwners;
    private uint[] _slotSeen;
    private int[] _freeSlots;
    private int _freeCount;
    private int _slotHigh = 1;

    /// <summary>The realm-local slots a new replication starts with, the sentinel included; ×2 on demand.</summary>
    internal const int InitialSessionSlots = 8;

    /// <summary>The realm-local slot <paramref name="session"/> holds here, or 0 (the unbound sentinel) when it is not placed in this realm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected int L(SessionId session) => Hub.LocalSlot(session, ServedRealm);

    /// <summary>Tests only: the realm-local session slots allocated, the sentinel included.</summary>
    internal int SessionSlotCapacity => _sessions.Length;

    /// <summary>Tests only: the realm-local slots in use.</summary>
    internal int SessionsHere => _slotHigh - 1 - _freeCount;

    /// <summary>A realm-local slot for <paramref name="session"/>, reset: nothing of a previous occupant survives. Serial (the frame prologue).</summary>
    internal int Join(SessionId session)
    {
        int local;
        if (_freeCount > 0)
        {
            local = _freeSlots[--_freeCount];
        }
        else
        {
            local = _slotHigh++;
            if (local == _sessions.Length)
            {
                GrowSessions(_sessions.Length * 2);
            }
        }

        ResetSlot(local);
        _slotOwners[local] = session;
        return local;
    }

    /// <summary>Gives a realm-local slot back. Serial (the frame prologue).</summary>
    internal void Release(int local)
    {
        Debug.Assert(local > 0 && local < _slotHigh, "only a slot Join handed out is released");
        ResetSlot(local);
        _slotOwners[local] = default;
        _freeSlots[_freeCount++] = local;
    }

    /// <summary>Stamps a realm-local slot as in use this tick (<see cref="ReleaseUnseen"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Touch(int local, uint tick) => _slotSeen[local] = tick;

    /// <summary>
    /// Gives back every slot no session was placed in this tick — a session that closed, or lost its profile — and tells <paramref name="hub"/>. Serial,
    /// after the tick's placements.
    /// </summary>
    internal void ReleaseUnseen(uint tick, PushHub hub)
    {
        for (var local = 1; local < _slotHigh; local++)
        {
            if (_slotSeen[local] == tick || !_slotOwners[local].IsValid)
            {
                continue;
            }

            hub.Forget(_slotOwners[local], ServedRealm);
            Release(local);
        }
    }

    // A slot handed to a new session: its geometry, region and shadow belong to nobody (generation 0 is no session's).
    private protected virtual void ResetSlot(int local)
    {
        _sessions[local] = default;
        if (local < _regions.Length && _regions[local] != null)
        {
            _regions[local].Generation = 0;
        }

        if (Shadow)
        {
            _shadowGen[local] = 0;
            _shadow[local]?.Clear();
        }
    }

    private protected virtual void GrowSessions(int length)
    {
        Array.Resize(ref _sessions, length);
        Array.Resize(ref _slotOwners, length);
        Array.Resize(ref _slotSeen, length);
        Array.Resize(ref _freeSlots, length);
        if (_regions.Length > 0)
        {
            Array.Resize(ref _regions, length);
        }

        if (Shadow)
        {
            Array.Resize(ref _shadow, length);
            Array.Resize(ref _shadowGen, length);
        }
    }
    private protected uint _tick;

    /// <summary>The most rows a window can have: <see cref="ReplicationGrid.MaxWindow"/> in the flat implementation, 13² in the deep one (10 § 4.3).</summary>
    private protected const int MaxRows = 169;

    // Shared by both implementations, so declared here rather than once per closed generic type: an empty window, and an empty region hull.
    private protected static readonly ushort[] ZeroRows = new ushort[MaxRows];
    private protected static readonly ClientRegionCommand NoHull;

    // The last tick the blocks step ran for; zero before the first. A tick the track did not run for (no session, an aborted tick, a failed fence) still
    // ran the fence, which drained that tick's structure words: its pushes are gone, and only re-pushing every live entity recovers them.
    private uint _preparedTick;

    /// <summary>Blocks steps that followed a tick the track did not run for, and so re-pushed every live entity — cumulative.</summary>
    public long GapRepushes;
    private protected ArchetypeEncodePlan[] _encodePlans = [];

    /// <summary>The frame stage's encode plans, whose group tick slots decide what an update carries.</summary>
    public void AttachEncodePlans(ArchetypeEncodePlan[] plans) => _encodePlans = plans;


    // Counters, cumulative.
    public long Events;
    public long Enters;
    public long Leaves;
    public long Updates;
    public long CellsDelivered;
    public long Sweeps;
    public long SweepSlots;
    public long Resets;
    public long IndexTicks;

    /// <summary>The index's split, cumulative, in Stopwatch ticks: the runs' fill and sort (projection chunks), the merge (index chunks), the finish.</summary>
    public long SortTicks;
    public long MergeTicks;
    public long FinishTicks;
    public long GatherTicks;

    // -- The shadow oracle (TYPHON_PUSH_SHADOW=1) --
    //
    // What each client holds, rebuilt from the records the server actually PUBLISHED, and checked two ways: every record must be legal against it (no enter
    // of a held id, no state, segment or leave of an unheld one), and every few ticks it must equal the geometric known-set recomputed from the blocks.
    // Either failing is a divergence a client would carry for good. Off by default: it is a HashSet per session.
    public readonly bool Shadow;
    private protected HashSet<uint>[] _shadow = [];
    private protected ushort[] _shadowGen = [];
    public long ShadowIllegal;
    public long ShadowMissing;
    public long ShadowExtra;
    public long ShadowChecks;
    public long ShadowChecked;
    public long ExtraGone;
    public long ExtraOutside;
    public double ExtraOutsideDistSum;
    public long ExtraUndelivered;

    // Entries lost at the fence without a projection to see them go: a block released with live entries, a slot overwritten by a migration or by a parked
    // drain. Each one is an identity some client may hold, so each becomes a leave event.
    private protected readonly object _orphanLock = new();
    private protected int _orphanCount;
    public long OrphanRelease;
    public long OrphanMigrate;
    public long OrphanDrain;
    public long OrphanRealm;

    private protected readonly ReplicationOccupancy _occupancy = new();

    /// <summary>The replication grid's occupancy, maintained from the index (10 § 2.4).</summary>
    internal ReplicationOccupancy Occupancy => _occupancy;

    /// <summary>Cell deliveries and sweeps skipped because the cell held nothing — a cluster query each — cumulative.</summary>
    public long EmptyCellsSkipped;

    /// <summary>
    /// Cell delivery and sweep (10 § 8, L7): time in each, cumulative in Stopwatch ticks, and the entries each decoded against what it emitted — the
    /// measurement that decides whether batched delivery is worth designing. Collected only while <see cref="FrameAssembler.PhaseTimingEnabled"/> is set.
    /// </summary>
    public long DeliverTicks;
    public long DeliverDecoded;
    public long DeliverEntered;
    public long SweepTicks;
    public long SweepDecoded;

    /// <summary>Full recounts of the occupancy: after a tick the track did not run for or did not index, whose events it never saw — cumulative.</summary>
    public long OccupancyRecounts;

    /// <summary>Time spent in those recounts — serial, in the index's finish — cumulative, in Stopwatch ticks.</summary>
    public long RecountTicks;

    // Set by the blocks step when the occupancy missed a tick's changes; the finish then recounts instead of applying this tick's deltas.
    private protected bool _recountAtFinish;

    /// <summary>Index entries (primaries and secondaries) this tick, and occupied cells in the index — for the empty-world and cost tests.</summary>
    public int IndexEntries { get; private protected set; }

    /// <summary>The most key ranges one tick's merge was split into — whether the merge ever ran as concurrent chunks.</summary>
    public int MaxMergeChunks { get; private protected set; }

    /// <summary>Tests only (SUB-24's mutant): a mover's secondary no longer takes its entity out of the cell it left.</summary>
    internal bool OccupancyMutantForTest;

    /// <summary>Tests only (SUB-25's mutant): a mover's secondary is filed under the cell it entered instead of the one it left.</summary>
    internal bool IndexMutantForTest;

    /// <summary>Cells the index holds this tick: only those an event touched.</summary>
    public int IndexCells { get; private protected set; }

    /// <summary>
    /// PROTOTYPE — the index is merged in parallel (<c>TYPHON_PUSH_PARALLEL_INDEX=0</c> merges it serially in the frame prologue, for the A/B).
    /// </summary>
    public bool ParallelIndex = Environment.GetEnvironmentVariable("TYPHON_PUSH_PARALLEL_INDEX") != "0";

    private protected bool _countInProject;
    private protected uint _indexedTick = uint.MaxValue;

    /// <summary>Whether this tick's index is built, so the frame prologue does not build it again.</summary>
    public bool Indexed => _indexedTick == _tick;

    /// <summary>World frames that ended before the session's fill was complete — cumulative; what the fill's pacing costs in frames.</summary>
    public long WorldFillFrames;

    /// <summary>
    /// World gathers that needed the occupied cells' order on a tick it was not taken: each one delivered nothing that frame. Zero when sound.
    /// </summary>
    public long WorldOrderMissing;

    // Set by NoteWorldSession when some World session may fill this tick: only then is the occupancy's order kept up to date.
    private bool _worldOrderNeeded;
    private protected ulong[] _worldOrder = [];
    private protected int _worldOrderCount;
    private protected uint _worldOrderTick = uint.MaxValue;

    /// <summary>
    /// <b>Test seam, and a deliberate one.</b> Commits a skipped session as though its frame had been published — the one move SUB-03 forbids — so the
    /// rule's verifier can be shown to reject it. Nothing in production sets it.
    /// </summary>
    internal bool CommitOnSkipForTest;

    /// <summary>
    /// The implementation the grid's depth selects (L6): the flat one for a grid one cell deep, the deep one otherwise — or always the deep one with
    /// <c>forceDeep</c>, which only tests set, since on a flat grid both must agree.
    /// </summary>
    public static PushReplication Create(CompiledProjectionPlan[] plans, ArchetypeReplicationState[] states, bool[] isPush, bool[] automatic,
        ReplicationGrid grid, int maxSessions, bool shadow = false, bool forceDeep = false, ushort realm = 0, RealmCodecs codecs = null)
    {
        return grid == null || (grid.Flat && !forceDeep)
            ? new PushReplication<PushEvent>(plans, states, isPush, automatic, grid, maxSessions, shadow, realm, codecs)
            : new PushReplication<PushEvent3>(plans, states, isPush, automatic, grid, maxSessions, shadow, realm, codecs);
    }

    private protected PushReplication(CompiledProjectionPlan[] plans, ArchetypeReplicationState[] states, bool[] isPush, bool[] automatic,
        ReplicationGrid grid, int maxSessions, bool shadow, bool deep, ushort realm = 0, RealmCodecs codecs = null)
    {
        Shadow = shadow || Environment.GetEnvironmentVariable("TYPHON_PUSH_SHADOW") == "1";
        _plans = plans;
        _states = states;
        var count = 0;
        for (var a = 0; a < isPush.Length; a++)
        {
            count += isPush[a] ? 1 : 0;
        }

        _pushIndices = new int[count];
        count = 0;
        for (var a = 0; a < isPush.Length; a++)
        {
            if (isPush[a])
            {
                _pushIndices[count++] = a;
            }
        }

        ServedRealm = realm;
        Codecs = codecs ?? RealmCodecs.FromPlans(plans);
        Bootstrapped = new bool[plans.Length];
        EverythingThisTick = new bool[plans.Length];

        _minX = new double[plans.Length];
        _minY = new double[plans.Length];
        _minZ = new double[plans.Length];
        _stepX = new double[plans.Length];
        _stepY = new double[plans.Length];
        _stepZ = new double[plans.Length];
        _axisBytes = new int[plans.Length];
        _hasZ = new bool[plans.Length];
        _pruneMargin = new double[plans.Length];
        _queryPad = new double[plans.Length];
        _skipMargin = new double[plans.Length];
        _positionOffset = new int[plans.Length];

        foreach (var a in _pushIndices)
        {
            var position = plans[a].Position;
            var blockLayout = plans[a].BlockLayout;
            if (position == null || position.Dims < 2 || position.Dims > 3
                || (position.Moving ? blockLayout.PrevPositionBytes == 0 : blockLayout.EnterPositionBytes == 0))
            {
                throw new NotSupportedException(
                    $"Archetype '{plans[a].Name}' is observed by a profile and has no 2D or 3D position. Replication serves entities by where they are.");
            }

            // A 2D position lies on the plane z = 0, the spatial grid's own convention (10 § 3.4). In a deep grid whose Z range excludes 0 it would lie
            // outside every cell, and every cell-based proof would be unsound.
            if (grid != null && !grid.Flat && position.Dims == 2 && (grid.OriginZ > 0 || grid.OriginZ + (grid.DimZ * grid.CellM) <= 0))
            {
                throw new NotSupportedException(
                    $"Archetype '{plans[a].Name}' has a 2D position, which replication places on the plane z = 0, and the spatial world's Z range "
                    + $"[{grid.OriginZ}, {grid.OriginZ + (grid.DimZ * grid.CellM)}) does not contain it. Give the archetype a 3D position, or include z = 0 "
                    + "in the spatial world.");
            }

            // Where the position every geometric test reads lives: a mover's v̂ (its previous position when h_A = 0), or a static entity's enter cache.
            _positionOffset[a] = position.Moving ? blockLayout.VisibilityPositionOffsetInColdEntry : blockLayout.EnterPositionOffsetInColdEntry;
            var slack = position.Moving ? plans[a].VisibilitySlackM : 0d;
            _queryPad[a] = 1d + slack;

            var frame = Codecs.ByPlan[a];
            _minX[a] = frame.Min[0];
            _minY[a] = frame.Min[1];
            _stepX[a] = frame.Step[0];
            _stepY[a] = frame.Step[1];
            // In a flat grid every geometric z is 0 (10 § 3.4), whichever implementation serves it.
            _hasZ[a] = deep && position.Dims == 3 && grid != null && !grid.Flat;
            _pruneMargin[a] = 0.01 + Math.Max(_stepX[a], _stepY[a]);
            if (_hasZ[a])
            {
                _minZ[a] = frame.Min[2];
                _stepZ[a] = frame.Step[2];
                _pruneMargin[a] = Math.Max(_pruneMargin[a], 0.01 + _stepZ[a]);
            }

            _pruneMargin[a] += slack;

            // The cell delivery and sweep prune against v̂, which is quantized: the same centimetre, quantum and slack as the far sweep.
            _skipMargin[a] = _pruneMargin[a];

            _axisBytes[a] = frame.AxisBytes;
        }

        // Null only without a spatial grid, where every observed archetype was refused above for having no position.
        ArgumentNullException.ThrowIfNull(grid);
        Radius = grid.Radius;
        CellSize = grid.CellM;
        Half = grid.Half;
        Window = grid.Window;
        _gridMinX = grid.OriginX;
        _gridMinY = grid.OriginY;
        _gridMinZ = grid.OriginZ;
        _worldMaxX = grid.WorldMaxX;
        _worldMaxY = grid.WorldMaxY;
        _worldMaxZ = grid.WorldMaxZ;
        _gridW = grid.DimX;
        _gridH = grid.DimY;
        _gridD = grid.DimZ;

        _sessions = new PushSessionState[InitialSessionSlots];
        _slotOwners = new SessionId[InitialSessionSlots];
        _slotSeen = new uint[InitialSessionSlots];
        _freeSlots = new int[InitialSessionSlots];
        if (Shadow)
        {
            _shadow = new HashSet<uint>[_sessions.Length];
            _shadowGen = new ushort[_sessions.Length];
        }
    }

    // ── The flat cell key, for callers outside the geometry (tests, the occupancy's tests) ──

    /// <summary>Bits per axis of a cell key: the spatial VDB key's width (<see cref="ReplicationGrid.MaxAxisCells"/>).</summary>
    internal const int KeyAxisBits = 21;

    /// <summary>A flat grid's packed cell key, <c>(cy &lt;&lt; 21) | cx</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Key(int cx, int cy) => PushEvent.Key(cx, cy, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int KeyX(ulong key) => PushEvent.KeyX(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int KeyY(ulong key) => PushEvent.KeyY(key);

    // ══ Shadow oracle ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Shadow oracle: applies one published frame's records to the session's shadow of its client, counting every illegal record.</summary>
    public void ShadowApply(SessionId session, FrameWorkerScratch scratch, int archetypes, bool reset)
    {
        var slot = L(session);
        var set = _shadow[slot];
        if (set == null || _shadowGen[slot] != session.Generation)
        {
            set = _shadow[slot] = new HashSet<uint>();
            _shadowGen[slot] = session.Generation;
        }

        if (reset)
        {
            set.Clear();
        }

        var illegal = 0L;
        for (var a = 0; a < archetypes; a++)
        {
            foreach (ref readonly var r in scratch.List(a, FrameListKind.Enter))
            {
                illegal += set.Add(r.NetId) ? 0 : 1;
            }

            foreach (ref readonly var r in scratch.List(a, FrameListKind.Segment))
            {
                illegal += set.Contains(r.NetId) ? 0 : 1;
            }

            foreach (ref readonly var r in scratch.List(a, FrameListKind.State))
            {
                illegal += set.Contains(r.NetId) ? 0 : 1;
            }
        }

        for (var a = 0; a < archetypes; a++)
        {
            foreach (ref readonly var r in scratch.List(a, FrameListKind.Leave))
            {
                illegal += set.Remove(r.NetId) ? 0 : 1;
            }
        }

        if (illegal != 0)
        {
            Interlocked.Add(ref ShadowIllegal, illegal);
        }
    }

    private readonly List<(SessionId Session, ArchetypeSet Archetypes)> _shadowQueue = [];

    /// <summary>Shadow oracle: queues a session for the check at the start of the next blocks step.</summary>
    public void QueueShadowCheck(SessionId session, in ArchetypeSet archetypes) => _shadowQueue.Add((session, archetypes));

    public void RunQueuedShadowChecks()
    {
        foreach (var (session, archetypes) in _shadowQueue)
        {
            ShadowCheck(session, in archetypes);
        }

        _shadowQueue.Clear();
    }

    /// <summary>Shadow oracle, serial: compares a session's shadow with the geometric known-set recomputed from every block.</summary>
    public abstract void ShadowCheck(SessionId session, in ArchetypeSet archetypes);

    public long GoneInUnoccupiedSlot;
    public long GoneOccupiedButNotProjected;
    public long GoneNowhere;

    private protected void ClassifyGone(uint netId, in ArchetypeSet archetypes)
    {
        foreach (var a in _pushIndices)
        {
            if (!archetypes.Contains(a))
            {
                continue;
            }

            var state = _states[a];
            var cs = state.ClusterState;
            var active = 0;
            var ids = cs?.ReadActiveClusterList(out active);
            var layout = state.Layout;
            for (var i = 0; ids != null && i < active; i++)
            {
                if (!state.Directory.TryGetBlock(ids[i], out var block))
                {
                    continue;
                }

                var bytes = (byte*)block;
                for (var s = 0; s < layout.SlotCount; s++)
                {
                    if (((ReplicationHotEntry*)(bytes + layout.HotOffset + (s * layout.HotStride)))->NetId == netId)
                    {
                        if ((block->ProjectedOccupancy & (1UL << s)) == 0)
                        {
                            GoneInUnoccupiedSlot++;
                        }
                        else
                        {
                            GoneOccupiedButNotProjected++;
                        }

                        return;
                    }
                }
            }
        }

        GoneNowhere++;
    }

    /// <summary>Diagnostics: per push archetype, entries migrated, parked, parked-and-dropped, abandoned.</summary>
    public string MigrationSummary()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var a in _pushIndices)
        {
            var st = _states[a];
            sb.Append(_plans[a].Name).Append(": migrated ").Append(st.EntriesMigrated).Append(", parked ").Append(st.EntriesParked)
                .Append(", dropped ").Append(st.ParkedDropped).Append(", abandoned ").Append(st.MigrationsAbandoned).Append("; ");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The plan index, replication block and netId of the entity in <paramref name="clusters"/>' chunk <paramref name="chunk"/>, slot
    /// <paramref name="slot"/> — what a <c>SELF</c> reads its owner entry through (11 § 2.4); false as <see cref="TryEntityAt"/> is.
    /// </summary>
    public bool TryReplicaAt(ArchetypeClusterState clusters, int chunk, int slot, EntityId entity, out int archetype, out nint block, out uint netId)
    {
        archetype = -1;
        block = 0;
        netId = 0;
        for (var a = 0; a < _states.Length; a++)
        {
            var state = _states[a];
            if (state == null || !ReferenceEquals(state.ClusterState, clusters))
            {
                continue;
            }

            var header = BlockOf(a, chunk);
            if (header == null || (uint)slot >= 64)
            {
                return false;
            }

            var layout = state.Layout;
            var hot = (ReplicationHotEntry*)((byte*)header + layout.HotOffset + (slot * layout.HotStride));
            if (hot->Entity != entity || hot->NetId == NetIdAllocator.NoNetId)
            {
                return false;
            }

            archetype = a;
            block = (nint)header;
            netId = hot->NetId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The netId and v̂ of the entity in <paramref name="clusters"/>' chunk <paramref name="chunk"/>, slot <paramref name="slot"/> — false when its archetype
    /// is not replicated, its cluster has no block, or the slot holds no identity for it (09 § 11: an event's entity reference).
    /// </summary>
    public bool TryEntityAt(ArchetypeClusterState clusters, int chunk, int slot, EntityId entity, out uint netId, out float x, out float y, out float z) =>
        TryVisibilityAt(clusters, chunk, slot, entity, out _, out netId, out x, out y, out z);

    /// <summary><see cref="TryEntityAt"/>, with the entity's plan index — what an archetype-set test needs (SUB-26).</summary>
    public bool TryVisibilityAt(ArchetypeClusterState clusters, int chunk, int slot, EntityId entity, out int archetype, out uint netId, out float x,
        out float y, out float z)
    {
        x = y = z = 0f;
        if (!TryReplicaAt(clusters, chunk, slot, entity, out archetype, out var block, out netId))
        {
            return false;
        }

        var layout = _states[archetype].Layout;
        Decode(archetype, (byte*)block + layout.ColdOffset + (slot * layout.ColdStride) + PositionOffset(archetype), out x, out y, out z);
        return true;
    }

    /// <summary>This tick's departed entities, every replicated archetype's (09 § 11, Q7). Serial, in the frame prologue.</summary>
    public void CollectDeparted(System.Collections.Generic.Dictionary<long, NetIdLeaseSet.DepartedEntity> into)
    {
        foreach (var state in _states)
        {
            state?.NetIdLeases.CollectDeparted(into);
        }
    }

    /// <summary>The aggregate grids (09 § 8): per tile, per archetype, the entities whose v̂ lies in it. Empty when no profile declares an aggregate.</summary>
    public AggregateCounts[] Aggregates { get; private set; } = [];

    // By plan index: whether an aggregate grid or a near budget's counts count the archetype, so the merge notes its deltas.
    private protected bool[] _counted = [];

    /// <summary>Attaches the aggregate grids, before the first tick; every archetype they count must be one this replication serves.</summary>
    public void ConfigureAggregates(AggregateCounts[] grids)
    {
        Aggregates = grids;
        RefreshCounted();
    }

    private void RefreshCounted()
    {
        _counted = new bool[_plans.Length];
        foreach (var grid in Aggregates)
        {
            for (var a = 0; a < grid.Columns.Length && a < _counted.Length; a++)
            {
                _counted[a] |= grid.Columns[a] >= 0;
            }
        }

        foreach (var set in _nearSets)
        {
            for (var a = 0; a < _counted.Length; a++)
            {
                _counted[a] |= a < ArchetypeSet.Capacity && set.Contains(a);
            }
        }
    }

    // ── ClientRegion (09 § 7) ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A region window's width per axis in cells, <c>⌈maxEdgeM / c⌉ + 5</c>; zero when no profile declares a ClientRegion.</summary>
    public int RegionWindow { get; private set; }

    /// <summary>
    /// The near budgets' counts (09 § 7): per distinct archetype set a budgeted ClientRegion observes, per cell, the live entities of those archetypes whose
    /// v̂ lies there — maintained from the merge's deltas like the occupancy (SUB-24), recounted with it.
    /// </summary>
    public ReplicationOccupancy[] NearCounts { get; private set; } = [];

    private protected ArchetypeSet[] _nearSets = [];

    // Per session slot, a ClientRegion session's geometry; allocated at its first region gather, reused by the slot's later sessions.
    private protected RegionSession[] _regions = [];

    /// <summary>Region cells delivered, and taken back by a near budget — cumulative.</summary>
    public long RegionCellsDelivered;

    /// <inheritdoc cref="RegionCellsDelivered"/>
    public long RegionCellsUndelivered;

    /// <summary>Region changes that reset their session: more than half its delivered cells fell outside the new hull — cumulative.</summary>
    public long RegionResets;

    /// <summary>Enables ClientRegion sessions, before the first tick: their window width and the archetype sets the near budgets count.</summary>
    public void ConfigureRegions(int window, ArchetypeSet[] nearSets)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)window * window * (Deep ? window : 1), ReplicationGrid.MaxWindowCells, nameof(window));
        RegionWindow = window;
        _nearSets = nearSets;
        NearCounts = new ReplicationOccupancy[nearSets.Length];
        for (var i = 0; i < nearSets.Length; i++)
        {
            NearCounts[i] = new ReplicationOccupancy();
        }

        _regions = new RegionSession[_sessions.Length];
        RefreshCounted();
    }

    /// <summary>
    /// Builds a ClientRegion session's records (09 § 7): <c>known(s, e) ⟺ v̂ₑ ∈ H ∧ delivered(s, cell(v̂ₑ))</c> for its committed hull H. The hull is the
    /// session's anchor: a change sweeps the cells whose classification changed, and resets instead when more than half the delivered cells fall outside the
    /// new hull. With a near budget, cells are delivered nearest the hull's centroid first while the counted entities stay within it. Returns whether the
    /// frame must carry a RESET.
    /// </summary>
    public abstract bool GatherRegion(SessionId session, bool hasRegion, in ClientRegionCommand region, double maxEdgeM, int nearBudget, int nearCounts,
        double tickSeconds, bool forceReset, in ArchetypeSet archetypes, FrameWorkerScratch scratch, int enterBudget, ref long enters, ref long leaves,
        ref long updates, out bool complete);

    /// <summary>Tests only: a ClientRegion session's committed near-budget estimate — the counted entities of its delivered cells.</summary>
    internal int RegionHeldOf(SessionId session)
    {
        var r = (uint)L(session) < (uint)_regions.Length ? _regions[L(session)] : null;
        return r != null && r.Generation == session.Generation && r.Anchored ? r.Held : 0;
    }

    /// <summary>
    /// Whether a tile is in a ClientRegion session's aggregate region (09 § 8): it meets the session's pending hull, and some cell of it that the hull meets
    /// was not delivered — the hull minus what the near tier holds. After the session's gather.
    /// </summary>
    public abstract bool RegionAggregates(SessionId session, AggregateCounts counts, uint tile);

    /// <summary>Tests only: whether a ClientRegion session's committed window has the cell a point lies in.</summary>
    internal abstract bool RegionDelivers(SessionId session, double x, double y, double z);

    /// <summary>Tests only: the replication grid's cells per axis.</summary>
    internal (int X, int Y, int Z) GridCellsForTest => (_gridW, _gridH, _gridD);

    /// <summary>Tests only: a ClientRegion session's committed delivered cells.</summary>
    internal abstract int RegionDeliveredCells(SessionId session);

    /// <summary>The replication grid, as a debugging client is shown it (<c>DEBUG</c> <c>GRID</c>, 09 § 15).</summary>
    public DebugGrid DebugGrid => new(_gridMinX, _gridMinY, _gridMinZ, CellSize, _gridW, _gridH, Deep ? _gridD : 1);

    /// <summary>
    /// A debugging session's <c>PUSH_GEOMETRY</c> payload (09 § 15) into <paramref name="into"/>: its pending geometry — its anchor or hull and its delivered
    /// window as they are once this frame is published. After its gather. Returns the payload's length.
    /// </summary>
    public abstract int WriteDebugGeometry(SessionId session, PushShape shape, double slackM, int nearBudget, bool complete, Span<byte> into);

    /// <summary>A session's pending anchor, after its gather: where an aggregate's radius is centred.</summary>
    public Vector3D PendingAnchorOf(SessionId session)
    {
        ref var st = ref _sessions[L(session)];
        return st.Bound && st.Generation == session.Generation ? new Vector3D(st.PAnchorX, st.PAnchorY, st.PAnchorZ) : default;
    }

    /// <summary>The cell a point lies in, clamped to the grid.</summary>
    public abstract void CellOf(double x, double y, double z, out int cx, out int cy, out int cz);

    /// <summary>
    /// The v̂ an entity had before this tick, when this tick's projection moved it: its event, filed under the cell of its new v̂ (<paramref name="x"/>,
    /// <paramref name="y"/>, <paramref name="z"/>), carries the old one. <c>RouteToKnown</c> files both (09 § 11: was ∨ is). Serial, after the index.
    /// </summary>
    public abstract bool TryOldVisibility(uint netId, float x, float y, float z, out float oldX, out float oldY, out float oldZ);

    /// <summary>
    /// Whether a Sphere session sees a point: inside its committed sphere with the point's cell delivered, or its pending one — the known-set test (SUB-16)
    /// an event's geometric route asks (09 § 11) — and, with <paramref name="viewRadius"/>, within that of its viewpoint. After its gather.
    /// </summary>
    public abstract bool SeesPoint(SessionId session, float x, float y, float z, float viewRadius);

    /// <summary>Whether a World session has delivered the cell a point lies in, committed or pending.</summary>
    public abstract bool WorldSeesPoint(SessionId session, float x, float y, float z);

    /// <summary>
    /// Whether a session's COMMITTED geometry holds a point — what its client was last told (SUB-16), never the pending geometry of a frame not yet published:
    /// the test a command's entity reference is judged by (SUB-26). The point is the entity's v̂ (SUB-20).
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="shape">The shape of the session's observer.</param>
    /// <param name="x">The point.</param>
    /// <param name="y">The point.</param>
    /// <param name="z">The point.</param>
    /// <returns><see langword="true"/> when the session holds an entity at that point.</returns>
    public abstract bool HoldsCommitted(SessionId session, PushShape shape, float x, float y, float z);

    /// <summary>The cells a Sphere session's committed and pending spheres span: where its events' points can be.</summary>
    public abstract void SessionCellBox(SessionId session, out int minCx, out int maxCx, out int minCy, out int maxCy, out int minCz, out int maxCz);

    /// <summary>The tick of a session's last committed frame; 0 before its first.</summary>
    public uint LastTickOf(SessionId session)
    {
        ref var st = ref _sessions[L(session)];
        return st.Bound && st.Generation == session.Generation && st.Anchored ? st.LastTick : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected ReplicationBlockHeader* BlockOf(int archetype, int chunkId)
    {
        var table = _states[archetype].BlockByChunk;
        return (uint)chunkId < (uint)table.Length ? (ReplicationBlockHeader*)table[chunkId] : null;
    }

    // ══ Blocks step (serial): this realm's half; the collector is the hub's ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The start of this realm's blocks step (R4.1): its own gap detection. A tick the track did not run for, or ran without finishing its index, lost
    /// this realm's cell changes; the next finish recounts, and every live entity of the realm is re-pushed (<see cref="ResumedThisTick"/>).
    /// </summary>
    internal void BeginRealmTick(uint tick)
    {
        _tick = tick;
        var resumed = _preparedTick != 0 && tick != _preparedTick + 1;

        // A tick the track ran whose index was never finished (a stage fault between the projection and the frames) lost its cell changes as surely as a
        // tick it skipped. _indexedTick still names the last finished tick here: BeginMark resets it later in this step.
        var unindexed = _preparedTick != 0 && !resumed && _indexedTick != _preparedTick;
        _preparedTick = tick;

        // Either way the occupancy missed changes. It is recounted when this tick's index is finished, not now: the fence has yet to place this tick's
        // carried and parked entries, and only the projection makes the blocks' occupancy words describe them (SUB-24).
        _recountAtFinish |= resumed || unindexed;
        ResumedThisTick = resumed;
        if (resumed)
        {
            GapRepushes++;

            // The orphans queued across the gap are leaves nobody needs: a session connected before it misses a tick the log never held, so it resets, and
            // one connected since holds nothing. Dropped rather than kept, because with no session connected nothing else ever empties the list.
            _orphanCount = 0;
        }
    }

    /// <summary>
    /// Brings a replication the hub activated in the frame prologue (R4.4) to this tick: an empty index stands for it, and every live entity of the realm is
    /// pushed from the next tick on — a reactivated one's missed ticks are a gap, so its occupancy and counts are recounted as after any gap. Serial.
    /// </summary>
    internal void PrimeForTick(uint tick, int workers)
    {
        Array.Clear(Bootstrapped);
        BeginRealmTick(tick);
        BeginMark(workers, countInProject: false);
        BuildIndex();
    }

    /// <summary>The spatial grid this realm's entities of an archetype are indexed in, or null when the archetype has none in this realm.</summary>
    private protected SpatialGrid RealmGridOf(ArchetypeClusterState cs)
    {
        var byRealm = cs.RealmSpatial;
        return byRealm != null && ServedRealm < byRealm.Length ? Volatile.Read(ref byRealm[ServedRealm])?.Grid : null;
    }

    /// <summary>A replicated entity's v̂ (or enter position), decoded over this realm's frame: the point its sessions' geometry is tested against.</summary>
    internal void DecodeVisibility(int archetype, nint block, int slot, out float x, out float y, out float z)
    {
        var layout = _states[archetype].Layout;
        Decode(archetype, (byte*)block + layout.ColdOffset + (slot * layout.ColdStride) + PositionOffset(archetype), out x, out y, out z);
    }

    /// <summary>After the hub marked the push set: this realm's projection bookkeeping for the tick.</summary>
    // The hub's validator runs this tick: read once per tick here rather than through the hub per event.
    private protected bool _validating;

    internal void BeginMark(int workers, bool countInProject)
    {
        // The parallel index (BeginParallelIndex): the projection's chunks sort their own events as they finish.
        _countInProject = countInProject && ParallelIndex;
        _validating = Hub.ValidateClustersPerTick > 0;
        _indexedTick = uint.MaxValue;
        ResetWorkers(workers);
    }

    /// <summary>Sizes and empties the per-worker event lists for this tick's projection.</summary>
    private protected abstract void ResetWorkers(int workers);

    /// <summary>The codecs of <paramref name="realm"/> when this replication serves it, otherwise <see langword="null"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RealmCodecs CodecsFor(ushort realm) => realm == ServedRealm ? Codecs : null;

    // ══ Projection (parallel, one worker per block) ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Decodes a quantized position as the wire would: axes 0 and 1, and axis 2 where the archetype has one in this grid — a flat grid's, or a 2D codec's,
    /// geometric z is 0 (10 § 3.4).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Decode(int archetype, byte* quantized, out float x, out float y, out float z)
    {
        DecodePlane(archetype, quantized, out x, out y);
        z = 0f;
        if (_hasZ[archetype])
        {
            var bytes = _axisBytes[archetype];
            uint qz = 0;
            for (var i = 0; i < bytes; i++)
            {
                qz |= (uint)quantized[(2 * bytes) + i] << (8 * i);
            }

            z = (float)WireMath.DecodeQuantWithStep(qz, _minZ[archetype], _stepZ[archetype]);
        }
    }

    /// <summary>Axes 0 and 1 only: the flat implementation's whole decode, whose geometric z is 0 whatever the codec.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void DecodePlane(int archetype, byte* quantized, out float x, out float y)
    {
        var bytes = _axisBytes[archetype];
        uint qx = 0, qy = 0;
        for (var i = 0; i < bytes; i++)
        {
            qx |= (uint)quantized[i] << (8 * i);
            qy |= (uint)quantized[bytes + i] << (8 * i);
        }

        x = (float)WireMath.DecodeQuantWithStep(qx, _minX[archetype], _stepX[archetype]);
        y = (float)WireMath.DecodeQuantWithStep(qy, _minY[archetype], _stepY[archetype]);
    }

    /// <summary>Records one projected slot. Called by the worker that owns the block, while its hot entry is still in cache.</summary>
    public abstract void AddEvent(int worker, int archetype, ReplicationBlockHeader* block, int slot, ReplicationHotEntry* hot, uint netId, byte flags,
        float ox, float oy, float oz, float nx, float ny, float nz);

    /// <summary>Records an entry that is about to vanish at the fence, so every session holding it is told to drop it. Rare; locked.</summary>
    public abstract void Orphan(int archetype, ReplicationBlockHeader* block, byte* cold, in ReplicationBlockLayout layout, uint netId, int cause);

    // ══ The push index (design/Subscriptions/10 § 2.3) ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by a projection chunk once its blocks are done: sorts its worker's events by cell while they are still in this core's cache. The run is the
    /// worker's own, so no chunk writes anything another reads.
    /// </summary>
    public abstract void CountWorker(int worker);

    /// <summary>
    /// The parallel index's serial half, after every projection chunk has sorted its run: the fence's orphans as one more run, and the key-range
    /// splitters. Returns how many merge chunks follow, or 0 when the projection did not sort this tick and the frame prologue builds the index instead.
    /// </summary>
    public abstract int BeginParallelIndex();

    /// <summary>One merge chunk: every run's entries in its key range, merged into the tick's log slot.</summary>
    public abstract void PlaceWorker(int chunk);

    /// <summary>
    /// Builds this tick's index serially: what the frame prologue runs when no stage merged it (the collapsed shape, a deterministic projection, the
    /// parallel index switched off), and the finish when one did.
    /// </summary>
    public abstract void BuildIndex();

    /// <summary>
    /// The index's serial tail: the chunks' cell lists concatenated into the log slot — chunk order is key order — and the slot's probe-unit table.
    /// Idempotent per tick; a no-op until the merge ran.
    /// </summary>
    public abstract void FinishIndex();

    /// <summary>
    /// Recounts the occupancy from every block: the live, identified entries of every observed archetype, by the cell of their last pushed position —
    /// exactly what a cell delivery would enumerate.
    /// </summary>
    internal abstract void Recount(ReplicationOccupancy into);

    /// <summary>Recounts the aggregate grids from the blocks.</summary>
    internal abstract void RecountAggregates();

    /// <summary>Tests: the aggregate counts that differ from a fresh recount of the blocks.</summary>
    internal abstract int AggregateDifferencesForTest();

    /// <summary>Tests only: the current index's shape recomputed from its own events, after checking it; (-1, -1) when any check fails.</summary>
    internal abstract (int Cells, int Entries) IndexShapeForTest();

    /// <summary>Tests only: the cells where the maintained occupancy disagrees with a recount from the blocks. Zero when SUB-24 holds.</summary>
    internal int VerifyOccupancy()
    {
        var recount = new ReplicationOccupancy();
        Recount(recount);
        return _occupancy.Differences(recount);
    }

    // ══ Per-session gather (parallel over sessions) ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Whether a session slot is in a state that needs a RESET before anything else is sent.</summary>
    public bool NeedsReset(SessionId session)
    {
        ref var st = ref _sessions[L(session)];
        return !st.Bound || st.Generation != session.Generation ? false : st.NeedsReset;
    }

    /// <summary>
    /// A frame for the session was not published. Nothing changes: its committed anchor, cells and <see cref="PushSessionState.LastTick"/> stand, and the
    /// next frame replays the missed ticks from the push log — or resets, when they have left it.
    /// </summary>
    public void NoteNotPublished(SessionId session)
    {
        if (CommitOnSkipForTest)
        {
            Commit(session);
        }
    }

    /// <summary>The frame was published: the anchor and the delivered cells it described become the session's.</summary>
    public abstract void Commit(SessionId session);

    /// <summary>
    /// Builds one push session's records into <paramref name="scratch"/>. Returns whether the frame must carry a RESET (first frame after a lost one, a
    /// teleport, or a profile switch).
    /// </summary>
    public abstract bool Gather(SessionId session, bool placed, Vector3D viewpoint, double radius, in LodBands bands, bool forceReset,
        in ArchetypeSet archetypes, FrameWorkerScratch scratch, ArchetypeEncodePlan[] encodePlans, int enterBudget, ref long enters, ref long leaves,
        ref long updates, out bool complete);

    /// <summary>
    /// A World session: it holds every entity of its archetypes whose cell it has been delivered, and occupied cells are delivered in key order behind one
    /// cursor — so its whole known-set is <c>key(cell(v)) &lt; cursor</c>. Each frame delivers occupied cells onward under the enter budget, then carries
    /// the tick's events.
    /// </summary>
    public abstract bool GatherWorld(SessionId session, bool forceReset, in ArchetypeSet archetypes, FrameWorkerScratch scratch, int enterBudget, ref long enters,
        ref long leaves, ref long updates, out bool complete);

    /// <summary>
    /// Serial, in the frame prologue: notes a World session this tick, so its fill can walk the occupied cells in order. A session may fill when its fill is
    /// incomplete, or when its frame may reset — a reset asked for, or frames missed, which the log may not cover.
    /// </summary>
    public void NoteWorldSession(SessionId session, bool forceReset)
    {
        ref var st = ref _sessions[L(session)];

        // Frames missed that the log covers are caught up without a fill; only a gap it does not cover resets. A reset the catch-up decides for a reused
        // identity is not foreseen: that fill finds no order, delivers nothing, and the next tick's prologue takes the order for it.
        var gap = st.LastTick + 1 != _tick && (_tick - st.LastTick - 1 >= LogDepth || !LogHolds(st.LastTick + 1, _tick));
        _worldOrderNeeded |= forceReset || !st.Bound || st.Generation != session.Generation || st.NeedsReset || !st.Anchored
            || st.Cursor != ulong.MaxValue || gap;
    }

    /// <summary>Whether the push log holds every tick from <paramref name="first"/> to <paramref name="last"/>.</summary>
    private protected abstract bool LogHolds(uint first, uint last);

    /// <summary>
    /// Serial, in the frame prologue, after the index: the occupied cells in key order for this tick's World fills — taken only when some fill may run,
    /// so a runtime whose World sessions all hold the world pays nothing for it.
    /// </summary>
    public void PrepareWorldOrder()
    {
        _worldOrderCount = 0;
        if (!_worldOrderNeeded)
        {
            // No fill can find this tick's order, even a second call in one tick after one that took it.
            _worldOrderTick = uint.MaxValue;
            return;
        }

        _worldOrderNeeded = false;
        _worldOrderTick = _tick;

        // The occupancy's own array, read without a copy: it changes only at the next prologue's call, after every fill of this tick has read it.
        _worldOrder = _occupancy.OrderedKeys(out _worldOrderCount);
    }

    // ══ Distance LOD: the far flushes (parallel stage after the index) ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>Whether this tick's far flushes have been folded.</summary>
    public abstract bool FarFolded { get; }

    /// <summary>
    /// The far-flush fold's serial half: how many chunks of cells the stage runs, or 0 when the LOD is off, the index is not built yet (the frame
    /// prologue then folds serially) or the fold already ran this tick.
    /// </summary>
    public abstract int BeginFarFold(int workers);

    /// <summary>One chunk of the fold: a contiguous range of cells, walked in order across the window's slots.</summary>
    public abstract void FoldFarChunk(int chunk);

    /// <summary>
    /// The fold's serial tail, in the frame prologue: the chunks' flush entries concatenated, in chunk order — which is cell order — into the tick's log
    /// slot. Folds serially first when no stage did.
    /// </summary>
    public void EndFarFold()
    {
        var from = Stopwatch.GetTimestamp();
        try
        {
            EndFarFoldCore();
        }
        finally
        {
            FarEndTicks += Stopwatch.GetTimestamp() - from;
        }
    }

    private protected abstract void EndFarFoldCore();

    private protected static int LowerBound(ulong[] values, int from, int count, ulong key)
    {
        var lo = from;
        var hi = count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (values[mid] < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
