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

    // The LOD level (09 § 10), committed with the frame like the anchor: Level is what every change held back so far was scheduled by, TargetLevel is
    // where the budget loop wants the session, PLevel what this frame's gather moved it to.
    public byte Level;
    public byte PLevel;
    public byte TargetLevel;

    /// <summary>After the level fell: the level whose periods a flush's history still spans, until <see cref="WideUntil"/> (<see cref="LodBands.AtLevel"/>).</summary>
    public byte WideLevel;
    public uint WideUntil;

    // The budget loop: the level it asked for and the steps (a ShrinkSteps-th of the radius each) it took off the radius at the last level (09 § 10; TargetLevel adds the overload step),
    // the bytes/s EWMA, ticks spent over the budget and under its lower mark, and the last tick it was fed.
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
internal abstract unsafe class PushReplication
{
    private protected readonly CompiledProjectionPlan[] _plans;
    private protected readonly ArchetypeReplicationState[] _states;
    private protected readonly int[] _pushIndices;
    private readonly bool[] _bootstrapped;

    // Per plan index: the cold-entry offset of the entity's last projected position.
    private protected readonly int[] _positionOffset;

    /// <summary>The cold-entry offset of a push archetype's last projected position.</summary>
    public int PositionOffset(int archetype) => _positionOffset[archetype];

    // Per plan index: the engine, not the application, detects this archetype's changes. Every live entity is pushed every tick and the byte compare
    // keeps only those that changed.
    private readonly bool[] _automatic;

    // ── The forgotten-push validator (explicit detection) ──
    //
    // A few clusters of each explicit archetype are projected whole every tick, round-robin. A slot among them that the application did not push and
    // that still produces an event changed without a push: counted by group, and sent anyway, so the validator heals what it finds.

    /// <summary>How many clusters per explicit archetype the validator projects whole each tick; zero turns it off.</summary>
    public int ValidateClustersPerTick = int.TryParse(Environment.GetEnvironmentVariable("TYPHON_PUSH_VALIDATE"), out var v) ? v : 0;

    private readonly int[] _validateCursor;
    private readonly Dictionary<int, ulong>[] _validating;

    /// <summary>Slots the validator found changed without a push, and of those, how many had moved (a segment) — cumulative.</summary>
    public long ForgottenPushes;
    public long ForgottenMotion;
    public long ValidatedSlots;
    private readonly long[][] _forgottenGroups;

    /// <summary>Per plan index and change group, how many forgotten pushes changed it.</summary>
    public long ForgottenGroups(int archetype, int group) =>
        (uint)archetype < (uint)_forgottenGroups.Length && _forgottenGroups[archetype] != null && (uint)group < (uint)_forgottenGroups[archetype].Length
            ? Interlocked.Read(ref _forgottenGroups[archetype][group]) : 0;

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

    // Per archetype, this tick's push set as (chunk, mask) pairs.
    private readonly int[][] _pushChunks;
    private readonly ulong[][] _pushMasks;
    private readonly int[] _pushCount;

    // Per archetype, by chunk id: slots to push again next tick — still extrapolating, or denied an identity.
    private readonly long[][] _repush;

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
            ref var st = ref _sessions[session.Slot];
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
        ref var st = ref _sessions[session.Slot];
        return st.Bound && st.Generation == session.Generation ? st.TargetLevel : 0;
    }

    /// <summary>Tests only: a session's committed LOD level.</summary>
    internal int LevelOf(SessionId session)
    {
        ref var st = ref _sessions[session.Slot];
        return st.Bound && st.Generation == session.Generation ? st.Level : 0;
    }

    /// <summary>Tests only: a session's bytes/s EWMA.</summary>
    internal double RateOf(SessionId session)
    {
        ref var st = ref _sessions[session.Slot];
        return st.Bound && st.Generation == session.Generation ? st.Rate : 0d;
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
        ref var st = ref _sessions[session.Slot];
        return st.Bound && st.Generation == session.Generation ? st.Shrink : 0;
    }

    /// <summary>Tests only: a session's committed radius — its own, less any last-resort shrink.</summary>
    internal double RadiusOf(SessionId session)
    {
        ref var st = ref _sessions[session.Slot];
        return st.Bound && st.Generation == session.Generation ? st.Radius : 0d;
    }

    /// <summary>Tests only: sets the steps taken off a session's radius, as the budget loop would at its last level.</summary>
    internal void SetShrink(SessionId session, int steps)
    {
        ref var st = ref _sessions[session.Slot];
        if (st.Bound && st.Generation == session.Generation)
        {
            st.Shrink = (byte)Math.Clamp(steps, 0, MaxShrink);
        }
    }

    /// <summary>Tests only: the budget loop leaves every level where <see cref="SetTargetLevel"/> put it.</summary>
    internal bool LevelsPinned;

    /// <summary>Tests only: sets the level a session's next frames move to, as the budget loop would.</summary>
    internal void SetTargetLevel(SessionId session, int level)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)level, (uint)MaxLevel, nameof(level));
        ref var st = ref _sessions[session.Slot];
        if (st.Bound && st.Generation == session.Generation)
        {
            st.TargetLevel = (byte)level;
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
        ref var st = ref _sessions[session.Slot];
        if (!st.Bound || st.Generation != session.Generation || LevelsPinned)
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

    private protected readonly PushSessionState[] _sessions;
    private protected uint _tick;

    // The last tick the blocks step ran for; zero before the first. A tick the track did not run for (no session, an aborted tick, a failed fence) still
    // ran the fence, which drained that tick's structure words: its pushes are gone, and only re-pushing every live entity recovers them.
    private uint _preparedTick;

    /// <summary>Blocks steps that followed a tick the track did not run for, and so re-pushed every live entity — cumulative.</summary>
    public long GapRepushes;
    private protected ArchetypeEncodePlan[] _encodePlans = [];

    /// <summary>The frame stage's encode plans, whose group tick slots decide what an update carries.</summary>
    public void AttachEncodePlans(ArchetypeEncodePlan[] plans) => _encodePlans = plans;

    // Per archetype, this tick's push set's blocks, parallel to _pushChunks — looked up once in PrepareBlocks.
    private readonly nint[][] _pushBlocks;

    // Counters, cumulative.
    public long Events;
    public long SlotsPushed;
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
    public long PrepareTicks;
    public long GatherTicks;

    // -- The shadow oracle (TYPHON_PUSH_SHADOW=1) --
    //
    // What each client holds, rebuilt from the records the server actually PUBLISHED, and checked two ways: every record must be legal against it (no enter
    // of a held id, no state, segment or leave of an unheld one), and every few ticks it must equal the geometric known-set recomputed from the blocks.
    // Either failing is a divergence a client would carry for good. Off by default: it is a HashSet per session.
    public readonly bool Shadow;
    private protected readonly HashSet<uint>[] _shadow = [];
    private protected readonly ushort[] _shadowGen = [];
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
        ReplicationGrid grid, int maxSessions, bool shadow = false, bool forceDeep = false)
    {
        return grid == null || (grid.Flat && !forceDeep)
            ? new PushReplication<PushEvent>(plans, states, isPush, automatic, grid, maxSessions, shadow)
            : new PushReplication<PushEvent3>(plans, states, isPush, automatic, grid, maxSessions, shadow);
    }

    private protected PushReplication(CompiledProjectionPlan[] plans, ArchetypeReplicationState[] states, bool[] isPush, bool[] automatic,
        ReplicationGrid grid, int maxSessions, bool shadow, bool deep)
    {
        Shadow = shadow || Environment.GetEnvironmentVariable("TYPHON_PUSH_SHADOW") == "1";
        _plans = plans;
        _states = states;
        _automatic = automatic;
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

        _bootstrapped = new bool[plans.Length];
        _validateCursor = new int[plans.Length];
        _validating = new Dictionary<int, ulong>[plans.Length];
        _forgottenGroups = new long[plans.Length][];
        for (var a = 0; a < plans.Length; a++)
        {
            _validating[a] = [];
            _forgottenGroups[a] = new long[8];
        }

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
        _pushChunks = new int[plans.Length][];
        _pushBlocks = new nint[plans.Length][];
        _pushMasks = new ulong[plans.Length][];
        _pushCount = new int[plans.Length];
        _repush = new long[plans.Length][];

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

            var pos = position.Pos;
            _minX[a] = pos.Min[0];
            _minY[a] = pos.Min[1];
            _stepX[a] = WireMath.QuantStep(pos.Min[0], pos.Max[0], pos.Bits);
            _stepY[a] = WireMath.QuantStep(pos.Min[1], pos.Max[1], pos.Bits);
            // In a flat grid every geometric z is 0 (10 § 3.4), whichever implementation serves it.
            _hasZ[a] = deep && position.Dims == 3 && grid != null && !grid.Flat;
            _pruneMargin[a] = 0.01 + Math.Max(_stepX[a], _stepY[a]);
            if (_hasZ[a])
            {
                _minZ[a] = pos.Min[2];
                _stepZ[a] = WireMath.QuantStep(pos.Min[2], pos.Max[2], pos.Bits);
                _pruneMargin[a] = Math.Max(_pruneMargin[a], 0.01 + _stepZ[a]);
            }

            _pruneMargin[a] += slack;

            // The cell delivery and sweep prune against v̂, which is quantized: the same centimetre, quantum and slack as the far sweep.
            _skipMargin[a] = _pruneMargin[a];

            _axisBytes[a] = pos.Bits / 8;
            _pushChunks[a] = new int[64];
            _pushBlocks[a] = new nint[64];
            _pushMasks[a] = new ulong[64];
            _repush[a] = [];
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

        _sessions = new PushSessionState[Math.Max(1, maxSessions)];
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
        var slot = session.Slot;
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
    /// The netId and v̂ of the entity in <paramref name="clusters"/>' chunk <paramref name="chunk"/>, slot <paramref name="slot"/> — false when its archetype
    /// is not replicated, its cluster has no block, or the slot holds no identity for it (09 § 11: an event's entity reference).
    /// </summary>
    public bool TryEntityAt(ArchetypeClusterState clusters, int chunk, int slot, EntityId entity, out uint netId, out float x, out float y, out float z)
    {
        netId = 0;
        x = y = z = 0f;
        for (var a = 0; a < _states.Length; a++)
        {
            var state = _states[a];
            if (state == null || !ReferenceEquals(state.ClusterState, clusters))
            {
                continue;
            }

            var block = BlockOf(a, chunk);
            if (block == null || (uint)slot >= 64)
            {
                return false;
            }

            var layout = state.Layout;
            var hot = (ReplicationHotEntry*)((byte*)block + layout.HotOffset + (slot * layout.HotStride));
            if (hot->Entity != entity || hot->NetId == NetIdAllocator.NoNetId)
            {
                return false;
            }

            netId = hot->NetId;
            Decode(a, (byte*)block + layout.ColdOffset + (slot * layout.ColdStride) + PositionOffset(a), out x, out y, out z);
            return true;
        }

        return false;
    }

    /// <summary>This tick's departed entities, every replicated archetype's (09 § 11, Q7). Serial, in the frame prologue.</summary>
    public void CollectDeparted(System.Collections.Generic.Dictionary<long, NetIdLeaseSet.DepartedEntity> into)
    {
        foreach (var state in _states)
        {
            state?.NetIdLeases.CollectDeparted(into);
        }
    }

    /// <summary>The cell a point lies in, clamped to the grid.</summary>
    public abstract void CellOf(double x, double y, double z, out int cx, out int cy, out int cz);

    /// <summary>
    /// Whether a Sphere session sees a point: inside its committed sphere with the point's cell delivered, or its pending one — the known-set test (SUB-16)
    /// an event's geometric route asks (09 § 11) — and, with <paramref name="viewRadius"/>, within that of its viewpoint. After its gather.
    /// </summary>
    public abstract bool SeesPoint(SessionId session, float x, float y, float z, float viewRadius);

    /// <summary>Whether a World session has delivered the cell a point lies in, committed or pending.</summary>
    public abstract bool WorldSeesPoint(SessionId session, float x, float y, float z);

    /// <summary>The cells a Sphere session's committed and pending spheres span: where its events' points can be.</summary>
    public abstract void SessionCellBox(SessionId session, out int minCx, out int maxCx, out int minCy, out int maxCy, out int minCz, out int maxCz);

    /// <summary>The tick of a session's last committed frame; 0 before its first.</summary>
    public uint LastTickOf(SessionId session)
    {
        ref var st = ref _sessions[session.Slot];
        return st.Bound && st.Generation == session.Generation && st.Anchored ? st.LastTick : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected ReplicationBlockHeader* BlockOf(int archetype, int chunkId)
    {
        var table = _states[archetype].BlockByChunk;
        return (uint)chunkId < (uint)table.Length ? (ReplicationBlockHeader*)table[chunkId] : null;
    }

    // ══ Blocks step (serial) ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Before the parked drain: drops last tick's push marks, collects this tick's push set, and gives every cluster in it a block — so an entity that
    /// migrated into a cluster with no block is drained into one this tick rather than dropped.
    /// </summary>
    public void PrepareBlocks(uint tick)
    {
        var from = Stopwatch.GetTimestamp();
        _tick = tick;
        var resumed = _preparedTick != 0 && tick != _preparedTick + 1;

        // A tick the track ran whose index was never finished (a stage fault between the projection and the frames) lost its cell changes as surely as a
        // tick it skipped. _indexedTick still names the last finished tick here: MarkPushed resets it later in this step.
        var unindexed = _preparedTick != 0 && !resumed && _indexedTick != _preparedTick;
        _preparedTick = tick;

        // Either way the occupancy missed changes. It is recounted when this tick's index is finished, not now: the fence has yet to place this tick's
        // carried and parked entries, and only the projection makes the blocks' occupancy words describe them (SUB-24).
        _recountAtFinish |= resumed || unindexed;
        if (resumed)
        {
            GapRepushes++;

            // The orphans queued across the gap are leaves nobody needs: a session connected before it misses a tick the log never held, so it resets, and
            // one connected since holds nothing. Dropped rather than kept, because with no session connected nothing else ever empties the list.
            _orphanCount = 0;
        }

        foreach (var a in _pushIndices)
        {
            var state = _states[a];
            var list = state.WatchedBlocks;
            for (var i = 0; i < list.Count; i++)
            {
                list[i]->WatchedMask = 0;
            }

            _pushCount[a] = 0;
            var cs = state.ClusterState;
            if (cs == null)
            {
                continue;
            }

            var capacity = cs.ClusterAabbs?.Length ?? 0;
            if (_repush[a].Length < capacity)
            {
                Array.Resize(ref _repush[a], capacity);
            }

            var slotMask = state.Layout.SlotCount >= 64 ? ulong.MaxValue : (1UL << state.Layout.SlotCount) - 1;
            var everything = _automatic[a] || !_bootstrapped[a] || resumed || (Volatile.Read(ref cs.StructureTick) == tick && cs.StructureCoversAll);
            if (everything)
            {
                // First tick, a tick after a gap, or a tick the fence could not describe: every live entity is pushed, which is what gives every entity of a
                // push archetype an identity and an encoded state before any session asks — the geometric known-set assumes a described entity for every
                // position. Every slot of an active cluster is visited, so a slot emptied during a gap gives its identity back too.
                var ids = cs.ReadActiveClusterList(out var active);
                for (var i = 0; ids != null && i < active; i++)
                {
                    AddPush(a, ids[i], slotMask);
                }

                _bootstrapped[a] = true;
            }
            else if (Volatile.Read(ref cs.StructureTick) == tick)
            {
                var words = cs.StructureWords;
                for (var c = 0; c < words.Length; c++)
                {
                    var w = (ulong)words[c];
                    if (w != 0UL)
                    {
                        AddPush(a, c, w & slotMask);
                    }
                }
            }

            // The validator: a few whole clusters, noting which of their slots nobody pushed. Before the repush list is consumed, which it reads.
            _validating[a].Clear();
            if (!everything && ValidateClustersPerTick > 0)
            {
                Validate(a, cs, tick, slotMask);
            }

            // Slots still extrapolating or denied an identity last tick: the engine's own pushes. A client dead-reckons a mover until told it stopped, so a
            // mover that stops without a write must still be visited, and an entity with no identity is invisible until it gets one.
            var repush = _repush[a];
            for (var c = 0; c < repush.Length; c++)
            {
                var w = (ulong)repush[c];
                if (w != 0UL)
                {
                    AddPush(a, c, w & slotMask);
                    repush[c] = 0;
                }
            }

            // Blocks for every cluster pushed into. Serial by contract (pool and directory are single-threaded here).
            if (_pushBlocks[a].Length < _pushChunks[a].Length)
            {
                Array.Resize(ref _pushBlocks[a], _pushChunks[a].Length);
            }

            for (var i = 0; i < _pushCount[a]; i++)
            {
                var chunk = _pushChunks[a][i];
                var block = BlockOf(a, chunk);
                if (block == null && !state.TryAttachBlock(chunk, out block))
                {
                    block = null;
                }

                _pushBlocks[a][i] = (nint)block;
            }
        }

        PrepareTicks += Stopwatch.GetTimestamp() - from;
    }

    /// <summary>Adds the validator's clusters for this tick to the push set, remembering which of their slots were not pushed otherwise.</summary>
    private void Validate(int a, ArchetypeClusterState cs, uint tick, ulong slotMask)
    {
        var ids = cs.ReadActiveClusterList(out var active);
        if (ids == null || active == 0)
        {
            return;
        }

        var words = Volatile.Read(ref cs.StructureTick) == tick ? cs.StructureWords : default;
        var repush = _repush[a];
        var count = Math.Min(ValidateClustersPerTick, active);
        for (var i = 0; i < count; i++)
        {
            var cursor = _validateCursor[a]++ % active;
            var chunk = ids[cursor];
            var pushed = ((uint)chunk < (uint)words.Length ? (ulong)words[chunk] : 0UL) | ((uint)chunk < (uint)repush.Length ? (ulong)repush[chunk] : 0UL);
            var unpushed = slotMask & ~pushed;
            if (unpushed == 0UL || !_validating[a].TryAdd(chunk, unpushed))
            {
                continue;
            }

            AddPush(a, chunk, unpushed);
            ValidatedSlots += BitOperations.PopCount(unpushed);
        }
    }

    /// <summary>Called by a projecting worker for an event: counts it as a forgotten push when only the validator asked for the slot.</summary>
    private protected void NoteIfForgotten(int archetype, ReplicationBlockHeader* block, int slot, byte flags, int groups)
    {
        var validating = _validating[archetype];
        if (validating.Count == 0 || !validating.TryGetValue(block->ChunkId, out var mask) || (mask & (1UL << slot)) == 0)
        {
            return;
        }

        Interlocked.Increment(ref ForgottenPushes);
        if ((flags & PushEvent.Segment) != 0)
        {
            Interlocked.Increment(ref ForgottenMotion);
        }

        var perGroup = _forgottenGroups[archetype];
        for (var g = 0; g < perGroup.Length; g++)
        {
            if ((groups & (1 << g)) != 0)
            {
                Interlocked.Increment(ref perGroup[g]);
            }
        }
    }

    private void AddPush(int a, int chunk, ulong mask)
    {
        if (mask == 0UL || chunk < 0)
        {
            return;
        }

        var n = _pushCount[a];
        if (n == _pushChunks[a].Length)
        {
            Array.Resize(ref _pushChunks[a], n * 2);
            Array.Resize(ref _pushMasks[a], n * 2);
        }

        // Adjacent duplicates (the repush list following the structure words) are merged; others are merged by the block's mask OR below.
        _pushChunks[a][n] = chunk;
        _pushMasks[a][n] = mask;
        _pushCount[a] = n + 1;
    }

    /// <summary>After the watched lists were reset: marks the push set and lists each block once, so the projection pass visits exactly it.</summary>
    public void MarkPushed(int workers, bool countInProject = false)
    {
        // The parallel index (BeginParallelIndex): the projection's chunks sort their own events as they finish.
        _countInProject = countInProject && ParallelIndex;
        _indexedTick = uint.MaxValue;

        foreach (var a in _pushIndices)
        {
            var state = _states[a];
            var list = state.WatchedBlocks;
            for (var i = 0; i < _pushCount[a]; i++)
            {
                var block = (ReplicationBlockHeader*)_pushBlocks[a][i];
                if (block == null)
                {
                    continue;
                }

                var before = block->WatchedMask;
                block->WatchedMask = before | _pushMasks[a][i];
                if (before == 0UL)
                {
                    list.Add(block);
                }

                SlotsPushed += BitOperations.PopCount(_pushMasks[a][i]);
            }
        }

        ResetWorkers(workers);
    }

    /// <summary>Sizes and empties the per-worker event lists for this tick's projection.</summary>
    private protected abstract void ResetWorkers(int workers);

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

    /// <summary>Marks slots of a cluster to be pushed again next tick. Called by the worker that owns the block — one writer per chunk.</summary>
    public void Repush(int archetype, int chunkId, ulong slots)
    {
        var r = _repush[archetype];
        if ((uint)chunkId < (uint)r.Length)
        {
            r[chunkId] |= (long)slots;
        }
    }

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
        ref var st = ref _sessions[session.Slot];
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
        ref var st = ref _sessions[session.Slot];

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
