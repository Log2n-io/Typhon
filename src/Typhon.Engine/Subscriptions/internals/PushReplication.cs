using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// One projected slot of a push archetype this tick, with where it was and where it is. The unit the frame stage fans out.
/// </summary>
/// <remarks>
/// Positions are the DECODED quantized positions — exactly what the wire carries — so every path that tests a distance (the push step, the sweep, a cell
/// delivery) tests the same number and the geometric known-set is exact rather than approximately consistent.
/// </remarks>
internal struct PushEvent
{
    public const byte HasOld = 1;
    public const byte HasNew = 2;
    public const byte Segment = 4;

    /// <summary>Set by the projection on an arrival by migration: never dropped as a no-op, so the entity's latest event names the slot it is in now.</summary>
    public const byte Arrived = 8;

    /// <summary>
    /// Distance LOD: this is the entity's far flush — its phase tick — and <see cref="FlushGroups"/> (with <see cref="FlushSegment"/>) is the union of what
    /// changed since its previous one. A session that holds the entity far sends that union; any other treats the event as it would without the flag.
    /// </summary>
    public const byte FarFlush = 16;

    /// <summary>Distance LOD: the far flush carries the motion segment.</summary>
    public const byte FlushSegment = 32;

    public nint Block;
    public float OldX;
    public float OldY;
    public float NewX;
    public float NewY;
    public uint NetId;

    /// <summary>The primary cell: the new position's, or the old one's for a leave-only event.</summary>
    public int Cell;

    // Cell coordinates, computed once by the projecting worker so no session re-derives them.
    public short OldCx;
    public short OldCy;
    public short NewCx;
    public short NewCy;
    public ushort Archetype;
    public byte Slot;
    public byte Flags;

    /// <summary>The change groups this tick stamped — what an update to a session that already holds the entity carries.</summary>
    public byte Groups;

    /// <summary>Distance LOD: with <see cref="FarFlush"/>, the groups changed since the entity's previous far flush.</summary>
    public byte FlushGroups;
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
    public int OriginX;
    public int OriginY;
    public ulong D0, D1, D2, D3;

    /// <summary>The tick of the last committed frame: the push log replays every event after it.</summary>
    public uint LastTick;

    /// <summary>A World session: the cells below this index, in grid order, have been delivered. Its whole known-set.</summary>
    public int Cursor;
    public int PCursor;

    // Computed by the gather, applied only when the frame is published (SUB-03's discipline: a frame that was not sent changes nothing).
    public double PAnchorX;
    public double PAnchorY;
    public int POriginX;
    public int POriginY;
    public ulong P0, P1, P2, P3;
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
/// emits a <see cref="PushEvent"/> each; the frame prologue buckets the events by cell; each push session's frame is gathered from the cells around it.</para>
/// </remarks>
internal sealed unsafe class PushReplication
{
    private readonly CompiledProjectionPlan[] _plans;
    private readonly ArchetypeReplicationState[] _states;
    private readonly int[] _pushIndices;
    private readonly bool[] _bootstrapped;

    // Per plan index: the cold-entry offset of the entity's last projected position.
    private readonly int[] _positionOffset;

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

    // Position decode per archetype (2D: axes 0 and 1).
    private readonly double[] _minX;
    private readonly double[] _minY;
    private readonly double[] _stepX;
    private readonly double[] _stepY;
    private readonly int[] _axisBytes;

    /// <summary>The visibility radius.</summary>
    public readonly double Radius;

    /// <summary>The replication cell side, declared (<see cref="SubscriptionsOptions.ReplicationCellM"/>).</summary>
    public readonly double CellSize;

    /// <summary>How far a viewpoint may drift from its anchor before the anchor moves.</summary>
    public readonly double AnchorSlack;

    /// <summary>The delivered window's half width, in cells.</summary>
    public readonly int Half;

    /// <summary>The delivered window's width, in cells (<c>2 · Half + 1</c>, at most 16).</summary>
    public readonly int Window;

    private readonly double _gridMinX;
    private readonly double _gridMinY;
    private readonly int _gridW;
    private readonly int _gridH;

    // Per archetype, this tick's push set as (chunk, mask) pairs.
    private readonly int[][] _pushChunks;
    private readonly ulong[][] _pushMasks;
    private readonly int[] _pushCount;

    // Per archetype, by chunk id: slots to push again next tick — still extrapolating, or denied an identity.
    private readonly long[][] _repush;

    // Per worker, this tick's events.
    private PushEvent[][] _events = [];
    private int[] _eventCount = [];

    // The index: events bucketed by cell, primaries first, then the secondaries (leave-only views of a mover filed under the cell it left). It is the current
    // tick's slot of the push log.
    private PushEvent[] _indexed = [];

    /// <summary>
    /// PROTOTYPE — distance LOD (<c>TYPHON_PUSH_FAR_EVERY=N</c>, 0 = off): an update to an entity beyond half the radius, that was beyond it before too, is
    /// deferred and sent every N ticks as the union since the last time, folded from the push log. Enters and leaves are never deferred, and an entity
    /// crossing inward gets its whole state.
    /// </summary>
    public int FarEvery
    {
        get => _farEvery;

        // At most the log's depth: an entity's far flush is found among the events of the last N ticks, and the log holds LogDepth of them. Set before
        // the first tick — the phase of every entity is taken against it.
        set => _farEvery = Math.Clamp(value, 0, LogDepth);
    }

    private int _farEvery = Math.Clamp(int.TryParse(Environment.GetEnvironmentVariable("TYPHON_PUSH_FAR_EVERY"), out var farEvery) ? farEvery : 0, 0, 8);

    public long UpdatesDeferred;

    /// <summary>Distance LOD: far flushes produced — events flagged in place plus flush entries appended — cumulative.</summary>
    public long FarFlushes;

    /// <summary>Distance LOD: updates the inner crescent sent — held entities the anchor's move brought within R/2 — cumulative.</summary>
    public long FarCrescentStates;

    /// <summary>Distance LOD: time spent in <see cref="EndFarFold"/> — serial, in the frame prologue — cumulative, in Stopwatch ticks.</summary>
    public long FarEndTicks;

    // Distance LOD's per-tick fold (BeginFarFold): per chunk of cells, the flush entries it appended, in cell order, as a compact CSR.
    private uint _farFoldTick = uint.MaxValue;
    private uint _farEndTick = uint.MaxValue;
    private int _farChunkCount;
    private PushEvent[][] _farOut = [];
    private int[] _farOutCount = [];
    private int[][] _farOutCells = [];
    private int[][] _farOutStarts = [];
    private int[] _farOutCellCount = [];
    private long[] _farOutFlagged = [];
    private int[] _farBounds = [];

    /// <summary>How many ticks of indexes the push log keeps: a session that missed fewer frames than this catches up from them, an older one resets.</summary>
    /// <remarks>Covers <see cref="SkipPolicy.MaxDegradeLevel"/> (one frame in four) with room for a few back-pressure skips on top.</remarks>
    public const int LogDepth = 8;

    private readonly TickLog[] _log = CreateLog();

    /// <summary>One tick of the push log: that tick's events in cell order, and the non-empty cells as a compact, ascending CSR.</summary>
    private sealed class TickLog
    {
        public uint Tick;
        public bool Valid;
        public PushEvent[] Events = [];
        public int[] Cells = [];
        public int[] Starts = [];
        public int[] PrimaryEnds = [];
        public int CellCount;

        // Distance LOD: this tick's far flushes for entities whose latest event is an earlier tick, by cell (compact, ascending). An entity whose latest
        // event IS this tick carries its flush on that event instead.
        public PushEvent[] Flush = [];
        public int[] FlushCells = [];
        public int[] FlushStarts = [];
        public int FlushCellCount;
    }

    private static TickLog[] CreateLog()
    {
        var log = new TickLog[LogDepth];
        for (var i = 0; i < log.Length; i++)
        {
            log[i] = new TickLog();
        }

        return log;
    }

    // Counters for the log's catch-up.
    public long LogCatchUps;
    public long LogCatchUpTicks;
    public long LogTooOld;
    public long LogAmbiguous;
    private readonly int[] _cellStart;
    private readonly int[] _cellPrimaryEnd;
    private readonly int[] _cellFill;
    private readonly int[] _cellSecondaryFill;

    private PushSessionState[] _sessions;
    private uint _tick;

    // The last tick the blocks step ran for; zero before the first. A tick the track did not run for (no session, an aborted tick, a failed fence) still
    // ran the fence, which drained that tick's structure words: its pushes are gone, and only re-pushing every live entity recovers them.
    private uint _preparedTick;

    /// <summary>Blocks steps that followed a tick the track did not run for, and so re-pushed every live entity — cumulative.</summary>
    public long GapRepushes;
    private ArchetypeEncodePlan[] _encodePlans = [];

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
    public long PrepareTicks;
    public long GatherTicks;

    // -- The shadow oracle (TYPHON_PUSH_SHADOW=1) --
    //
    // What each client holds, rebuilt from the records the server actually PUBLISHED, and checked two ways: every record must be legal against it (no enter
    // of a held id, no state, segment or leave of an unheld one), and every few ticks it must equal the geometric known-set recomputed from the blocks.
    // Either failing is a divergence a client would carry for good. Off by default: it is a HashSet per session.
    public readonly bool Shadow;
    private readonly HashSet<uint>[] _shadow = [];
    private readonly ushort[] _shadowGen = [];
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
    private readonly object _orphanLock = new();
    private PushEvent[] _orphans = new PushEvent[64];
    private int _orphanCount;
    public long OrphanRelease;
    public long OrphanMigrate;
    public long OrphanDrain;

    /// <summary>Records an entry that is about to vanish at the fence, so every session holding it is told to drop it. Rare; locked.</summary>
    public void Orphan(int archetype, ReplicationBlockHeader* block, byte* cold, in ReplicationBlockLayout layout, uint netId, int cause)
    {
        Decode(archetype, cold + _positionOffset[archetype], out var x, out var y);
        lock (_orphanLock)
        {
            if (_orphanCount == _orphans.Length)
            {
                Array.Resize(ref _orphans, _orphanCount * 2);
            }

            ref var e = ref _orphans[_orphanCount++];
            e = default;
            e.Block = (nint)block;
            e.NetId = netId;
            e.Archetype = (ushort)archetype;
            e.Flags = PushEvent.HasOld;
            e.OldX = x;
            e.OldY = y;
            e.OldCx = (short)CellX(x);
            e.OldCy = (short)CellY(y);
            e.Cell = (e.OldCy * _gridW) + e.OldCx;
            switch (cause)
            {
                case 0: OrphanRelease++; break;
                case 1: OrphanMigrate++; break;
                default: OrphanDrain++; break;
            }
        }
    }

    public PushReplication(CompiledProjectionPlan[] plans, ArchetypeReplicationState[] states, bool[] isPush, bool[] automatic, ReplicationGrid grid,
        int maxSessions, bool shadow = false)
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
        _stepX = new double[plans.Length];
        _stepY = new double[plans.Length];
        _axisBytes = new int[plans.Length];
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
            if (position == null || position.Dims != 2 || (position.Moving ? blockLayout.PrevPositionBytes == 0 : blockLayout.EnterPositionBytes == 0))
            {
                throw new NotSupportedException(
                    $"Archetype '{plans[a].Name}' is observed by a profile and has no 2D position. Replication serves entities by where they are, and the "
                    + "push index supports 2D positions only.");
            }

            // Where the entity's last projected position lives: a mover's previous-position copy, or a static entity's enter cache (it never moves).
            _positionOffset[a] = position.Moving ? blockLayout.PrevPositionOffsetInColdEntry : blockLayout.EnterPositionOffsetInColdEntry;

            var pos = position.Pos;
            _minX[a] = pos.Min[0];
            _minY[a] = pos.Min[1];
            _stepX[a] = WireMath.QuantStep(pos.Min[0], pos.Max[0], pos.Bits);
            _stepY[a] = WireMath.QuantStep(pos.Min[1], pos.Max[1], pos.Bits);
            _axisBytes[a] = pos.Bits / 8;
            _pushChunks[a] = new int[64];
            _pushBlocks[a] = new nint[64];
            _pushMasks[a] = new ulong[64];
            _repush[a] = [];
        }

        Radius = grid.Radius;
        CellSize = grid.CellM;
        AnchorSlack = grid.AnchorSlack;
        Half = grid.Half;
        Window = grid.Window;
        _gridMinX = grid.OriginX;
        _gridMinY = grid.OriginY;
        _gridW = grid.DimX;
        _gridH = grid.DimY;

        // The dense index's own limit, gone with it (10 § 12, 1.5.1): it clears and walks four int arrays of this many cells every tick.
        if ((long)_gridW * _gridH > 16_000_000)
        {
            throw new NotSupportedException(
                $"Replication grid of {_gridW} x {_gridH} cells is too large for the dense push index: raise SubscriptionsOptions.ReplicationCellM.");
        }

        _cellStart = new int[(_gridW * _gridH) + 1];
        _cellPrimaryEnd = new int[_gridW * _gridH];
        _cellFill = new int[_gridW * _gridH];
        _cellSecondaryFill = new int[_gridW * _gridH];
        _sessions = new PushSessionState[Math.Max(1, maxSessions)];
        if (Shadow)
        {
            _shadow = new HashSet<uint>[_sessions.Length];
            _shadowGen = new ushort[_sessions.Length];
        }
    }

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

    private readonly List<(SessionId Session, ulong Mask)> _shadowQueue = [];

    /// <summary>Shadow oracle: queues a session for the check at the start of the next blocks step.</summary>
    public void QueueShadowCheck(SessionId session, ulong mask) => _shadowQueue.Add((session, mask));

    public void RunQueuedShadowChecks()
    {
        foreach (var (session, mask) in _shadowQueue)
        {
            ShadowCheck(session, mask);
        }

        _shadowQueue.Clear();
    }

    public long GoneInUnoccupiedSlot;
    public long GoneOccupiedButNotProjected;
    public long GoneNowhere;

    private void ClassifyGone(uint netId, ulong archetypeMask)
    {
        foreach (var a in _pushIndices)
        {
            if ((archetypeMask & (1UL << a)) == 0)
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

    /// <summary>Shadow oracle, serial: compares a session's shadow with the geometric known-set recomputed from every block.</summary>
    public void ShadowCheck(SessionId session, ulong archetypeMask)
    {
        var slot = session.Slot;
        ref var st = ref _sessions[slot];
        var set = _shadow[slot];
        if (set == null || _shadowGen[slot] != session.Generation || !st.Anchored || st.NeedsReset)
        {
            return;
        }

        var r2 = Radius * Radius;
        var expected = 0;
        var missing = 0L;
        var where = new Dictionary<uint, (float X, float Y, bool Delivered)>();
        foreach (var a in _pushIndices)
        {
            if ((archetypeMask & (1UL << a)) == 0)
            {
                continue;
            }

            var state = _states[a];
            var cs = state.ClusterState;
            if (cs == null)
            {
                continue;
            }

            var ids = cs.ReadActiveClusterList(out var active);
            var layout = state.Layout;
            for (var i = 0; ids != null && i < active; i++)
            {
                if (!state.Directory.TryGetBlock(ids[i], out var block))
                {
                    continue;
                }

                var bytes = (byte*)block;
                var occ = block->ProjectedOccupancy;
                while (occ != 0)
                {
                    var s = BitOperations.TrailingZeroCount(occ);
                    occ &= occ - 1;
                    var hot = (ReplicationHotEntry*)(bytes + layout.HotOffset + (s * layout.HotStride));
                    if (hot->NetId == NetIdAllocator.NoNetId)
                    {
                        continue;
                    }

                    Decode(a, bytes + layout.ColdOffset + (s * layout.ColdStride) + _positionOffset[a], out var px, out var py);
                    var delivered = Bit(st.D0, st.D1, st.D2, st.D3, WindowIndex(st.OriginX, st.OriginY, CellX(px), CellY(py)));
                    where[hot->NetId] = (px, py, delivered);
                    var known = Within(st.AnchorX, st.AnchorY, px, py, r2) && delivered;
                    if (!known)
                    {
                        continue;
                    }

                    expected++;
                    if (!set.Contains(hot->NetId))
                    {
                        missing++;
                    }
                }
            }
        }

        ShadowMissing += missing;
        ShadowExtra += set.Count - (expected - missing);
        if (set.Count - (expected - missing) > 0)
        {
            foreach (var id in set)
            {
                if (!where.TryGetValue(id, out var w))
                {
                    ExtraGone++;
                    ClassifyGone(id, archetypeMask);
                }
                else if (!Within(st.AnchorX, st.AnchorY, w.X, w.Y, r2))
                {
                    ExtraOutside++;
                    var dx = w.X - st.AnchorX;
                    var dy = w.Y - st.AnchorY;
                    ExtraOutsideDistSum += Math.Sqrt((dx * dx) + (dy * dy)) - Radius;
                }
                else if (!w.Delivered)
                {
                    ExtraUndelivered++;
                }
            }
        }
        ShadowChecks++;
        ShadowChecked += expected;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReplicationBlockHeader* BlockOf(int archetype, int chunkId)
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
        _preparedTick = tick;
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
    private void NoteIfForgotten(int archetype, ReplicationBlockHeader* block, int slot, byte flags, int groups)
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
        // The parallel index (BeginParallelIndex): the projection's chunks count their events into the shared per-cell counts as they finish, so those
        // start from zero here, before any chunk runs.
        _countInProject = countInProject && ParallelIndex;
        _indexedTick = uint.MaxValue;
        if (_countInProject)
        {
            Array.Clear(_cellStart);
            Array.Clear(_cellPrimaryEnd);
        }

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

        if (_events.Length < workers)
        {
            Array.Resize(ref _events, workers);
            Array.Resize(ref _eventCount, workers);
        }

        for (var w = 0; w < _events.Length; w++)
        {
            _events[w] ??= new PushEvent[1024];
            _eventCount[w] = 0;
        }
    }

    // ══ Projection (parallel, one worker per block) ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Decodes a 2D quantized position as the wire would.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Decode(int archetype, byte* quantized, out float x, out float y)
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
    public void AddEvent(int worker, int archetype, ReplicationBlockHeader* block, int slot, ReplicationHotEntry* hot, uint netId, byte flags, float ox,
        float oy, float nx, float ny)
    {
        var groups = 0;
        if (hot != null && (uint)archetype < (uint)_encodePlans.Length)
        {
            var plan = _encodePlans[archetype];
            var tick = _tick;
            if (plan.Moving && hot->GroupTicks[plan.MotionTickSlot] >= tick)
            {
                flags |= PushEvent.Segment;
            }

            for (var g = 0; g < plan.GroupCount; g++)
            {
                if (hot->GroupTicks[plan.GroupTickSlot[g]] >= tick)
                {
                    groups |= 1 << g;
                }
            }
        }

        // Pushed, re-encoded, and nothing moved or changed: no session can learn anything from it. Dropped here, in parallel, rather than filtered by
        // every session that reaches its cell.
        const byte both = PushEvent.HasOld | PushEvent.HasNew;
        var arrived = (flags & PushEvent.Arrived) != 0;
        flags &= unchecked((byte)~PushEvent.Arrived);
        if (!arrived && (flags & (both | PushEvent.Segment)) == both && groups == 0 && ox == nx && oy == ny)
        {
            // No event, so the entity's stamp stays the tick of its last one: the sweep and the cell delivery must still own it.
            return;
        }

        if (ValidateClustersPerTick > 0 && hot != null)
        {
            NoteIfForgotten(archetype, block, slot, flags, groups);
        }

        if (hot != null)
        {
            // The push step owns this entity from this tick on; the sweep, the cell delivery and the log's catch-up skip it by this stamp.
            var layout = _states[archetype].Layout;
            *(uint*)((byte*)block + layout.ColdOffset + (slot * layout.ColdStride) + layout.LastEventTickOffsetInColdEntry) = _tick;
        }

        var list = _events[worker];
        var n = _eventCount[worker];
        if (n == list.Length)
        {
            Array.Resize(ref _events[worker], n * 2);
            list = _events[worker];
        }

        ref var e = ref list[n];
        e.Block = (nint)block;
        e.Slot = (byte)slot;
        e.NetId = netId;
        e.Archetype = (ushort)archetype;
        e.Flags = flags;
        e.OldX = ox;
        e.OldY = oy;
        e.NewX = nx;
        e.NewY = ny;
        e.Groups = (byte)groups;
        e.OldCx = (short)CellX(ox);
        e.OldCy = (short)CellY(oy);
        e.NewCx = (short)CellX(nx);
        e.NewCy = (short)CellY(ny);
        e.Cell = (flags & PushEvent.HasNew) != 0 ? (e.NewCy * _gridW) + e.NewCx : (e.OldCy * _gridW) + e.OldCx;
        _eventCount[worker] = n + 1;
    }

    /// <summary>Marks slots of a cluster to be pushed again next tick. Called by the worker that owns the block — one writer per chunk.</summary>
    public void Repush(int archetype, int chunkId, ulong slots)
    {
        var r = _repush[archetype];
        if ((uint)chunkId < (uint)r.Length)
        {
            r[chunkId] |= (long)slots;
        }
    }

    // ══ Frame prologue (serial) ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CellX(double x) => Math.Clamp((int)Math.Floor((x - _gridMinX) / CellSize), 0, _gridW - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CellY(double y) => Math.Clamp((int)Math.Floor((y - _gridMinY) / CellSize), 0, _gridH - 1);

    /// <summary>Buckets this tick's events by cell, primaries then secondaries within each cell: a counting sort over the events.</summary>
    public void BuildIndex()
    {
        var from = Stopwatch.GetTimestamp();

        // The fence's orphans ride worker 0's list: they are leaves like any other, filed under the cell the entity was last described in.
        if (_orphanCount > 0 && _events.Length > 0)
        {
            for (var i = 0; i < _orphanCount; i++)
            {
                var n = _eventCount[0];
                if (n == _events[0].Length)
                {
                    Array.Resize(ref _events[0], n * 2);
                }

                _events[0][n] = _orphans[i];
                _eventCount[0] = n + 1;
            }

            _orphanCount = 0;
        }

        var cells = _gridW * _gridH;
        var workers = _events.Length;
        const byte both = PushEvent.HasNew | PushEvent.HasOld;

        // Count: the primary counts in _cellStart, the secondary ones in _cellPrimaryEnd, both turned into offsets below. Proportional to the events and
        // the grid, never to the worker count.
        Array.Clear(_cellStart);
        Array.Clear(_cellPrimaryEnd);
        for (var w = 0; w < workers; w++)
        {
            var list = _events[w];
            var n = _eventCount[w];
            for (var i = 0; i < n; i++)
            {
                ref readonly var e = ref list[i];
                _cellStart[e.Cell]++;
                if ((e.Flags & both) == both && (e.OldCx != e.NewCx || e.OldCy != e.NewCy))
                {
                    _cellPrimaryEnd[(e.OldCy * _gridW) + e.OldCx]++;
                }
            }
        }

        var running = 0;
        for (var c = 0; c < cells; c++)
        {
            var primaries = _cellStart[c];
            var secondaries = _cellPrimaryEnd[c];
            _cellStart[c] = running;
            _cellFill[c] = running;
            running += primaries;
            _cellPrimaryEnd[c] = running;
            _cellSecondaryFill[c] = running;
            running += secondaries;
        }

        _cellStart[cells] = running;
        Events += running;
        var slot = _log[_tick % LogDepth];
        if (slot.Events.Length < running)
        {
            slot.Events = new PushEvent[Math.Max(running, slot.Events.Length * 2)];
        }

        _indexed = slot.Events;
        for (var w = 0; w < workers; w++)
        {
            var list = _events[w];
            var n = _eventCount[w];
            for (var i = 0; i < n; i++)
            {
                ref readonly var e = ref list[i];
                _indexed[_cellFill[e.Cell]++] = e;
                if ((e.Flags & both) == both && (e.OldCx != e.NewCx || e.OldCy != e.NewCy))
                {
                    // A secondary keeps the PRIMARY cell in Cell, so a session can tell whether it already met this event there.
                    _indexed[_cellSecondaryFill[(e.OldCy * _gridW) + e.OldCx]++] = e;
                }
            }
        }

        // The log's compact form of this tick: the non-empty cells, ascending, with their ranges. The dense arrays above are rebuilt every tick.
        var nonEmpty = 0;
        for (var c = 0; c < cells; c++)
        {
            if (_cellStart[c + 1] != _cellStart[c])
            {
                if (nonEmpty == slot.Cells.Length)
                {
                    var grown = Math.Max(64, nonEmpty * 2);
                    Array.Resize(ref slot.Cells, grown);
                    Array.Resize(ref slot.Starts, grown + 1);
                    Array.Resize(ref slot.PrimaryEnds, grown);
                }

                slot.Cells[nonEmpty] = c;
                slot.Starts[nonEmpty] = _cellStart[c];
                slot.PrimaryEnds[nonEmpty] = _cellPrimaryEnd[c];
                nonEmpty++;
            }
        }

        if (slot.Starts.Length < nonEmpty + 1)
        {
            Array.Resize(ref slot.Starts, nonEmpty + 1);
        }

        slot.Starts[nonEmpty] = running;
        slot.CellCount = nonEmpty;
        slot.Tick = _tick;
        slot.Valid = true;
        slot.FlushCellCount = 0;
        _indexedTick = _tick;

        IndexTicks += Stopwatch.GetTimestamp() - from;
    }

    /// <summary>
    /// PROTOTYPE — the index is built in parallel (<c>TYPHON_PUSH_PARALLEL_INDEX=0</c> keeps <see cref="BuildIndex"/>, serial in the frame prologue, for the
    /// A/B): counted by the projection's chunks, offset here, placed by one chunk per worker list.
    /// </summary>
    public bool ParallelIndex = Environment.GetEnvironmentVariable("TYPHON_PUSH_PARALLEL_INDEX") != "0";

    private bool _countInProject;
    private uint _indexedTick = uint.MaxValue;

    /// <summary>Whether this tick's index is built, so the frame prologue does not build it again.</summary>
    public bool Indexed => _indexedTick == _tick;

    /// <summary>
    /// Called by a projection chunk once its blocks are done: counts its worker's events into the shared per-cell counts — primaries under their cell,
    /// secondaries under the cell a mover left. Atomic, because every chunk counts into the same cells; the events are still in this core's cache.
    /// </summary>
    public void CountWorker(int worker)
    {
        if (!_countInProject || (uint)worker >= (uint)_events.Length)
        {
            return;
        }

        const byte both = PushEvent.HasNew | PushEvent.HasOld;
        var list = _events[worker];
        var n = _eventCount[worker];
        for (var i = 0; i < n; i++)
        {
            ref readonly var e = ref list[i];
            Interlocked.Increment(ref _cellStart[e.Cell]);
            if ((e.Flags & both) == both && (e.OldCx != e.NewCx || e.OldCy != e.NewCy))
            {
                Interlocked.Increment(ref _cellPrimaryEnd[(e.OldCy * _gridW) + e.OldCx]);
            }
        }
    }

    /// <summary>
    /// The parallel index's serial half, after every projection chunk has counted: the fence's orphans, the offsets and the log's compact cell list. Returns
    /// how many placement chunks follow — one per worker list — or 0 when the projection did not count this tick and the frame prologue builds it instead.
    /// </summary>
    public int BeginParallelIndex()
    {
        if (!_countInProject || _events.Length == 0)
        {
            return 0;
        }

        var from = Stopwatch.GetTimestamp();
        const byte both = PushEvent.HasNew | PushEvent.HasOld;

        // The fence's orphans ride worker 0's list, counted here: nothing else is running.
        for (var i = 0; i < _orphanCount; i++)
        {
            var n = _eventCount[0];
            if (n == _events[0].Length)
            {
                Array.Resize(ref _events[0], n * 2);
            }

            ref readonly var e = ref _orphans[i];
            _events[0][n] = e;
            _eventCount[0] = n + 1;
            _cellStart[e.Cell]++;
            if ((e.Flags & both) == both && (e.OldCx != e.NewCx || e.OldCy != e.NewCy))
            {
                _cellPrimaryEnd[(e.OldCy * _gridW) + e.OldCx]++;
            }
        }

        _orphanCount = 0;
        var cells = _gridW * _gridH;
        var running = 0;
        var slot = _log[_tick % LogDepth];
        var nonEmpty = 0;
        for (var c = 0; c < cells; c++)
        {
            var primaries = _cellStart[c];
            var secondaries = _cellPrimaryEnd[c];
            _cellStart[c] = running;
            _cellFill[c] = running;
            running += primaries;
            _cellPrimaryEnd[c] = running;
            _cellSecondaryFill[c] = running;
            running += secondaries;
            if (primaries + secondaries != 0)
            {
                if (nonEmpty == slot.Cells.Length)
                {
                    var grown = Math.Max(64, nonEmpty * 2);
                    Array.Resize(ref slot.Cells, grown);
                    Array.Resize(ref slot.Starts, grown + 1);
                    Array.Resize(ref slot.PrimaryEnds, grown);
                }

                slot.Cells[nonEmpty] = c;
                slot.Starts[nonEmpty] = _cellStart[c];
                slot.PrimaryEnds[nonEmpty] = _cellPrimaryEnd[c];
                nonEmpty++;
            }
        }

        _cellStart[cells] = running;
        Events += running;
        if (slot.Events.Length < running)
        {
            slot.Events = new PushEvent[Math.Max(running, slot.Events.Length * 2)];
        }

        if (slot.Starts.Length < nonEmpty + 1)
        {
            Array.Resize(ref slot.Starts, nonEmpty + 1);
        }

        slot.Starts[nonEmpty] = running;
        slot.CellCount = nonEmpty;
        slot.Tick = _tick;
        slot.Valid = true;
        slot.FlushCellCount = 0;
        _indexed = slot.Events;
        _indexedTick = _tick;
        IndexTicks += Stopwatch.GetTimestamp() - from;
        return _events.Length;
    }

    /// <summary>
    /// The parallel index's placement, for one worker's list: each event into its cell's next free position, claimed atomically since other lists place
    /// into the same cells. The order inside a cell follows the race, which nothing reads: a cell's events are folded or tested one by one.
    /// </summary>
    public void PlaceWorker(int worker)
    {
        if ((uint)worker >= (uint)_events.Length)
        {
            return;
        }

        var from = Stopwatch.GetTimestamp();
        const byte both = PushEvent.HasNew | PushEvent.HasOld;
        var list = _events[worker];
        var n = _eventCount[worker];
        var indexed = _indexed;
        for (var i = 0; i < n; i++)
        {
            ref readonly var e = ref list[i];
            indexed[Interlocked.Increment(ref _cellFill[e.Cell]) - 1] = e;
            if ((e.Flags & both) == both && (e.OldCx != e.NewCx || e.OldCy != e.NewCy))
            {
                // A secondary keeps the PRIMARY cell in Cell, so a session can tell whether it already met this event there.
                indexed[Interlocked.Increment(ref _cellSecondaryFill[(e.OldCy * _gridW) + e.OldCx]) - 1] = e;
            }
        }

        Interlocked.Add(ref IndexTicks, Stopwatch.GetTimestamp() - from);
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

    /// <summary>
    /// <b>Test seam, and a deliberate one.</b> Commits a skipped session as though its frame had been published — the one move SUB-03 forbids — so the
    /// rule's verifier can be shown to reject it. Nothing in production sets it.
    /// </summary>
    internal bool CommitOnSkipForTest;

    /// <summary>The frame was published: the anchor and the delivered cells it described become the session's.</summary>
    public void Commit(SessionId session)
    {
        ref var st = ref _sessions[session.Slot];
        st.AnchorX = st.PAnchorX;
        st.AnchorY = st.PAnchorY;
        st.OriginX = st.POriginX;
        st.OriginY = st.POriginY;
        st.D0 = st.P0;
        st.D1 = st.P1;
        st.D2 = st.P2;
        st.D3 = st.P3;
        st.Cursor = st.PCursor;
        st.Anchored = true;
        st.NeedsReset = false;
        st.LastTick = _tick;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Bit(ulong d0, ulong d1, ulong d2, ulong d3, int i) =>
        i >= 0 && ((i >> 6) switch { 0 => d0, 1 => d1, 2 => d2, _ => d3 } >> (i & 63) & 1UL) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetBit(ref ulong d0, ref ulong d1, ref ulong d2, ref ulong d3, int i)
    {
        switch (i >> 6)
        {
            case 0: d0 |= 1UL << (i & 63); break;
            case 1: d1 |= 1UL << (i & 63); break;
            case 2: d2 |= 1UL << (i & 63); break;
            default: d3 |= 1UL << (i & 63); break;
        }
    }

    /// <summary>The window index of absolute cell (<paramref name="cx"/>, <paramref name="cy"/>) for a window at the given origin, or -1 outside it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int WindowIndex(int originX, int originY, int cx, int cy)
    {
        var lx = cx - originX;
        var ly = cy - originY;
        return (uint)lx < (uint)Window && (uint)ly < (uint)Window ? (ly * Window) + lx : -1;
    }

    private static double RectMin2(double sx, double sy, double x0, double y0, double c)
    {
        var dx = Math.Max(Math.Max(x0 - sx, 0d), sx - (x0 + c));
        var dy = Math.Max(Math.Max(y0 - sy, 0d), sy - (y0 + c));
        return (dx * dx) + (dy * dy);
    }

    private static double RectMax2(double sx, double sy, double x0, double y0, double c)
    {
        var dx = Math.Max(sx - x0, x0 + c - sx);
        var dy = Math.Max(sy - y0, y0 + c - sy);
        return (dx * dx) + (dy * dy);
    }

    /// <summary>
    /// Whether a cluster's box proves no entity in it can matter: for a delivery, the box is wholly outside the new disc; for a sweep, it is wholly
    /// inside both discs or wholly outside both. A centimetre of margin keeps the proof sound against the quantized positions the tests use.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SkipCluster(double bx0, double by0, double bx1, double by1, double ax, double ay, double nx, double ny, bool sweeping)
    {
        if (double.IsInfinity(bx0) || double.IsInfinity(bx1))
        {
            return false;
        }

        var outer = (Radius + 0.01) * (Radius + 0.01);
        var inner = (Radius - 0.01) * (Radius - 0.01);
        var outsideNew = BoxMin2(nx, ny, bx0, by0, bx1, by1) > outer;
        if (!sweeping)
        {
            return outsideNew;
        }

        var outsideOld = BoxMin2(ax, ay, bx0, by0, bx1, by1) > outer;
        if (outsideNew && outsideOld)
        {
            return true;
        }

        return BoxMax2(nx, ny, bx0, by0, bx1, by1) <= inner && BoxMax2(ax, ay, bx0, by0, bx1, by1) <= inner;
    }

    private static double BoxMin2(double sx, double sy, double x0, double y0, double x1, double y1)
    {
        var dx = Math.Max(Math.Max(x0 - sx, 0d), sx - x1);
        var dy = Math.Max(Math.Max(y0 - sy, 0d), sy - y1);
        return (dx * dx) + (dy * dy);
    }

    private static double BoxMax2(double sx, double sy, double x0, double y0, double x1, double y1)
    {
        var dx = Math.Max(sx - x0, x1 - sx);
        var dy = Math.Max(sy - y0, y1 - sy);
        return (dx * dx) + (dy * dy);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Within(double ax, double ay, float x, float y, double r2)
    {
        var dx = x - ax;
        var dy = y - ay;
        return (dx * dx) + (dy * dy) <= r2;
    }

    /// <summary>
    /// Builds one push session's records into <paramref name="scratch"/>. Returns whether the frame must carry a RESET (first frame after a lost one, a
    /// teleport, or a profile switch).
    /// </summary>
    public bool Gather(SessionId session, bool placed, Vector3D viewpoint, bool forceReset, ulong archetypeMask, FrameWorkerScratch scratch,
        ArchetypeEncodePlan[] encodePlans, int enterBudget, ref long enters, ref long leaves, ref long updates, out bool complete)
    {
        complete = true;
        var from = Stopwatch.GetTimestamp();
        ref var st = ref _sessions[session.Slot];
        if (!st.Bound || st.Generation != session.Generation)
        {
            st = default;
            st.Bound = true;
            st.Generation = session.Generation;
        }

        var reset = st.NeedsReset || forceReset;
        var r2 = Radius * Radius;
        var tick = _tick;

        // Missed frames: replayed from the push log while every missed tick is still in it, reset otherwise (SUB-03: skip = union).
        var gap = st.Anchored && !reset && placed ? (int)(tick - st.LastTick - 1) : 0;

        // Distance LOD: an update to an entity far from the session before and after is sent only on the entity's far flush (BeginFarFold).
        var lod = FarEvery > 1 && placed;
        var farR2 = Radius * Radius * 0.25;
        var log = Log ??= new LogTable();
        log.Clear();
        if (gap > 0)
        {
            if (gap >= LogDepth || !LogCovers(st.LastTick + 1, tick))
            {
                Interlocked.Increment(ref LogTooOld);
                reset = true;
                gap = 0;
            }
            else if (!CollectLog(ref st, viewpoint, archetypeMask, log, r2))
            {
                Interlocked.Increment(ref LogAmbiguous);
                reset = true;
                gap = 0;
                log.Clear();
            }
            else
            {
                Interlocked.Increment(ref LogCatchUps);
                Interlocked.Add(ref LogCatchUpTicks, gap);
            }
        }

        double ax, ay;
        int oOriginX, oOriginY;
        ulong o0, o1, o2, o3;
        if (!placed)
        {
            // Nowhere: nothing is described and nothing changes. The pending state is the current one.
            st.PAnchorX = st.AnchorX;
            st.PAnchorY = st.AnchorY;
            st.POriginX = st.OriginX;
            st.POriginY = st.OriginY;
            st.P0 = st.D0;
            st.P1 = st.D1;
            st.P2 = st.D2;
            st.P3 = st.D3;
            return reset && st.Anchored;
        }

        if (reset || !st.Anchored)
        {
            ax = viewpoint.X;
            ay = viewpoint.Y;
            o0 = o1 = o2 = o3 = 0;
            oOriginX = CellX(ax) - Half;
            oOriginY = CellY(ay) - Half;
            reset = st.Anchored || reset;
        }
        else
        {
            ax = st.AnchorX;
            ay = st.AnchorY;
            oOriginX = st.OriginX;
            oOriginY = st.OriginY;
            o0 = st.D0;
            o1 = st.D1;
            o2 = st.D2;
            o3 = st.D3;
        }

        // ── The anchor ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        var nx = ax;
        var ny = ay;
        var dvx = viewpoint.X - ax;
        var dvy = viewpoint.Y - ay;
        var drift2 = (dvx * dvx) + (dvy * dvy);
        if (drift2 > AnchorSlack * AnchorSlack)
        {
            if (drift2 > CellSize * CellSize)
            {
                // A teleport: everything the client holds is wrong. Start over.
                ax = nx = viewpoint.X;
                ay = ny = viewpoint.Y;
                o0 = o1 = o2 = o3 = 0;
                oOriginX = CellX(ax) - Half;
                oOriginY = CellY(ay) - Half;
                reset = true;
            }
            else
            {
                nx = viewpoint.X;
                ny = viewpoint.Y;
            }
        }

        var moved = nx != ax || ny != ay;
        var nOriginX = CellX(nx) - Half;
        var nOriginY = CellY(ny) - Half;

        // The new window starts as the old one's cells that it still covers.
        ulong d0 = 0, d1 = 0, d2 = 0, d3 = 0;
        for (var ly = 0; ly < Window; ly++)
        {
            for (var lx = 0; lx < Window; lx++)
            {
                var oi = WindowIndex(oOriginX, oOriginY, nOriginX + lx, nOriginY + ly);
                if (Bit(o0, o1, o2, o3, oi))
                {
                    SetBit(ref d0, ref d1, ref d2, ref d3, (ly * Window) + lx);
                }
            }
        }

        // ── 1. Deliver cells nearest first, under the enter budget ─────────────────────────────────────────────────────────────────────────────────
        ulong n0 = 0, n1 = 0, n2 = 0, n3 = 0;
        var entered = 0;
        var anchorCx = CellX(nx);
        var anchorCy = CellY(ny);
        for (var ring = 0; ring <= Half && entered < enterBudget; ring++)
        {
            for (var dy = -ring; dy <= ring && entered < enterBudget; dy++)
            {
                for (var dx = -ring; dx <= ring && entered < enterBudget; dx++)
                {
                    if (Math.Abs(dx) != ring && Math.Abs(dy) != ring)
                    {
                        continue;
                    }

                    var cx = anchorCx + dx;
                    var cy = anchorCy + dy;
                    if ((uint)cx >= (uint)_gridW || (uint)cy >= (uint)_gridH)
                    {
                        continue;
                    }

                    var wi = WindowIndex(nOriginX, nOriginY, cx, cy);
                    if (wi < 0 || Bit(d0, d1, d2, d3, wi))
                    {
                        continue;
                    }

                    var x0 = _gridMinX + (cx * CellSize);
                    var y0 = _gridMinY + (cy * CellSize);
                    if (RectMin2(nx, ny, x0, y0, CellSize) > r2)
                    {
                        continue;
                    }

                    SetBit(ref d0, ref d1, ref d2, ref d3, wi);
                    SetBit(ref n0, ref n1, ref n2, ref n3, wi);
                    entered += DeliverCell(cx, cy, nx, ny, r2, archetypeMask, scratch, tick, gap);
                    Interlocked.Increment(ref CellsDelivered);
                }
            }
        }

        // The budget did not bind, so every cell the disc reaches is delivered: the client holds its whole view.
        complete = entered < enterBudget;
        enters += entered;

        // ── 2. The push events around both anchors ─────────────────────────────────────────────────────────────────────────────────────────────────
        if (gap > 0)
        {
            EmitLog(log, ax, ay, oOriginX, oOriginY, o0, o1, o2, o3, nx, ny, nOriginX, nOriginY, d0, d1, d2, d3, r2, scratch, ref enters, ref leaves,
                ref updates, lod, farR2);
        }

        var minCx = CellX(Math.Min(ax, nx) - Radius);
        var maxCx = CellX(Math.Max(ax, nx) + Radius);
        var minCy = CellY(Math.Min(ay, ny) - Radius);
        var maxCy = CellY(Math.Max(ay, ny) + Radius);
        for (var cy = minCy; gap == 0 && cy <= maxCy; cy++)
        {
            for (var cx = minCx; cx <= maxCx; cx++)
            {
                var cell = (cy * _gridW) + cx;
                var b = _cellStart[cell];
                var end = _cellStart[cell + 1];
                if (b == end)
                {
                    continue;
                }

                var pe = _cellPrimaryEnd[cell];

                // A cell wholly inside both discs and delivered in both windows: every event that neither entered nor left it is an update, with no test.
                var x0 = _gridMinX + (cx * CellSize);
                var y0 = _gridMinY + (cy * CellSize);
                var interior = RectMax2(ax, ay, x0, y0, CellSize) <= r2 && RectMax2(nx, ny, x0, y0, CellSize) <= r2
                    && Bit(o0, o1, o2, o3, WindowIndex(oOriginX, oOriginY, cx, cy)) && Bit(d0, d1, d2, d3, WindowIndex(nOriginX, nOriginY, cx, cy));
                for (var i = b; i < end; i++)
                {
                    ref readonly var e = ref _indexed[i];
                    if ((archetypeMask & (1UL << e.Archetype)) == 0)
                    {
                        continue;
                    }

                    if (interior && i < pe && (e.Flags & (PushEvent.HasOld | PushEvent.HasNew)) == (PushEvent.HasOld | PushEvent.HasNew)
                        && e.OldCx == cx && e.OldCy == cy)
                    {
                        EmitUpdateLod(in e, e.OldX, e.OldY, e.Groups, (e.Flags & PushEvent.Segment) != 0, e.FlushGroups, e.Flags, ax, ay, nx, ny, lod,
                            farR2, scratch, ref updates);
                        continue;
                    }

                    if (i >= pe)
                    {
                        // A secondary: handled at its primary cell when that cell is in range.
                        var pcx = e.Cell % _gridW;
                        var pcy = e.Cell / _gridW;
                        if (pcx >= minCx && pcx <= maxCx && pcy >= minCy && pcy <= maxCy)
                        {
                            continue;
                        }
                    }

                    var was = (e.Flags & PushEvent.HasOld) != 0 && Within(ax, ay, e.OldX, e.OldY, r2)
                        && Bit(o0, o1, o2, o3, WindowIndex(oOriginX, oOriginY, e.OldCx, e.OldCy));
                    var isIn = (e.Flags & PushEvent.HasNew) != 0 && Within(nx, ny, e.NewX, e.NewY, r2)
                        && Bit(d0, d1, d2, d3, WindowIndex(nOriginX, nOriginY, e.NewCx, e.NewCy));
                    if (isIn)
                    {
                        if (!was)
                        {
                            scratch.Add(e.Archetype, FrameListKind.Enter,
                                new FrameRecord { NetId = e.NetId, Block = e.Block, Slot = e.Slot, Archetype = e.Archetype });
                            enters++;
                        }
                        else
                        {
                            EmitUpdateLod(in e, e.OldX, e.OldY, e.Groups, (e.Flags & PushEvent.Segment) != 0, e.FlushGroups, e.Flags, ax, ay, nx, ny,
                                lod, farR2, scratch, ref updates);
                        }
                    }
                    else if (was)
                    {
                        scratch.Add(e.Archetype, FrameListKind.Leave, new FrameRecord { NetId = e.NetId, Archetype = e.Archetype });
                        leaves++;
                    }
                }
            }
        }

        // ── 3. The crescent the anchor's move uncovered or left ────────────────────────────────────────────────────────────────────────────────────
        if (moved)
        {
            Interlocked.Increment(ref Sweeps);
            for (var ly = 0; ly < Window; ly++)
            {
                for (var lx = 0; lx < Window; lx++)
                {
                    var wi = (ly * Window) + lx;
                    if (!Bit(d0, d1, d2, d3, wi) || Bit(n0, n1, n2, n3, wi))
                    {
                        continue;
                    }

                    var cx = nOriginX + lx;
                    var cy = nOriginY + ly;
                    if ((uint)cx >= (uint)_gridW || (uint)cy >= (uint)_gridH)
                    {
                        continue;
                    }

                    var x0 = _gridMinX + (cx * CellSize);
                    var y0 = _gridMinY + (cy * CellSize);
                    var inBoth = RectMax2(ax, ay, x0, y0, CellSize) <= r2 && RectMax2(nx, ny, x0, y0, CellSize) <= r2;
                    var outBoth = RectMin2(ax, ay, x0, y0, CellSize) > r2 && RectMin2(nx, ny, x0, y0, CellSize) > r2;
                    if (inBoth || outBoth)
                    {
                        continue;
                    }

                    SweepCell(cx, cy, ax, ay, nx, ny, r2, archetypeMask, scratch, tick, gap, ref enters, ref leaves);
                }
            }

            // Distance LOD: the inner crescent. A held entity the anchor's move brought inside R/2 may have far changes it was never sent — they wait for
            // its far flush, which a near session ignores — so it gets its whole state now. One with an event since the last frame is the event's.
            for (var ly = 0; lod && ly < Window; ly++)
            {
                for (var lx = 0; lx < Window; lx++)
                {
                    var wi = (ly * Window) + lx;
                    if (!Bit(d0, d1, d2, d3, wi) || Bit(n0, n1, n2, n3, wi))
                    {
                        continue;
                    }

                    var cx = nOriginX + lx;
                    var cy = nOriginY + ly;
                    if ((uint)cx >= (uint)_gridW || (uint)cy >= (uint)_gridH)
                    {
                        continue;
                    }

                    var x0 = _gridMinX + (cx * CellSize);
                    var y0 = _gridMinY + (cy * CellSize);
                    if (RectMin2(nx, ny, x0, y0, CellSize) > farR2 || RectMax2(ax, ay, x0, y0, CellSize) <= farR2)
                    {
                        continue;
                    }

                    FarSweepCell(cx, cy, ax, ay, oOriginX, oOriginY, o0, o1, o2, o3, nx, ny, r2, farR2, archetypeMask, scratch, tick, gap, ref updates);
                }
            }
        }

        // ── 4. Distance LOD: this tick's far flushes of entities whose latest event is older, sent to the sessions that hold them far ─────────────
        // After missed frames the log's replay folded them, with the events.
        if (lod && gap == 0)
        {
            FlushEntries(ax, ay, oOriginX, oOriginY, o0, o1, o2, o3, nx, ny, nOriginX, nOriginY, d0, d1, d2, d3, r2, farR2, archetypeMask, scratch,
                ref updates);
        }

        if (scratch.Deferred != 0)
        {
            Interlocked.Add(ref UpdatesDeferred, scratch.Deferred);
            scratch.Deferred = 0;
        }

        st.PAnchorX = nx;
        st.PAnchorY = ny;
        st.POriginX = nOriginX;
        st.POriginY = nOriginY;
        st.P0 = d0;
        st.P1 = d1;
        st.P2 = d2;
        st.P3 = d3;
        Interlocked.Add(ref GatherTicks, Stopwatch.GetTimestamp() - from);
        return reset;
    }

    // ══ World sessions ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>How many grid cells a World session's delivery may visit in one frame, empty or not: bounds the first frame's cost.</summary>
    private const int WorldCellsPerFrame = 4096;

    /// <summary>
    /// A World session: it holds every entity of its archetypes whose cell it has been delivered, and cells are delivered in grid order behind one cursor
    /// — so its whole known-set is <c>cell(v) &lt; cursor</c>. Each frame delivers cells onward under the enter budget, then carries the tick's events.
    /// </summary>
    public bool GatherWorld(SessionId session, bool forceReset, ulong archetypeMask, FrameWorkerScratch scratch, int enterBudget, ref long enters,
        ref long leaves, ref long updates, out bool complete)
    {
        var from = Stopwatch.GetTimestamp();
        ref var st = ref _sessions[session.Slot];
        if (!st.Bound || st.Generation != session.Generation)
        {
            st = default;
            st.Bound = true;
            st.Generation = session.Generation;
        }

        var tick = _tick;
        var cells = _gridW * _gridH;
        var reset = st.NeedsReset || forceReset;
        var gap = st.Anchored && !reset ? (int)(tick - st.LastTick - 1) : 0;
        var log = Log ??= new LogTable();
        log.Clear();
        if (gap > 0)
        {
            if (gap >= LogDepth || !LogCovers(st.LastTick + 1, tick))
            {
                Interlocked.Increment(ref LogTooOld);
                reset = true;
                gap = 0;
            }
            else if (!CollectLogWorld(st.LastTick + 1, st.Cursor, archetypeMask, log))
            {
                Interlocked.Increment(ref LogAmbiguous);
                reset = true;
                gap = 0;
                log.Clear();
            }
            else
            {
                Interlocked.Increment(ref LogCatchUps);
                Interlocked.Add(ref LogCatchUpTicks, gap);
            }
        }

        var flagged = reset && st.Anchored;
        var oldCursor = reset || !st.Anchored ? 0 : st.Cursor;

        // ── 1. Deliver cells onward, under the enter budget ──
        var cursor = oldCursor;
        var entered = 0;
        var visited = 0;
        while (cursor < cells && entered < enterBudget && visited < WorldCellsPerFrame)
        {
            entered += DeliverCell(cursor % _gridW, cursor / _gridW, 0d, 0d, double.PositiveInfinity, archetypeMask, scratch, tick, gap, everywhere: true);
            cursor++;
            visited++;
        }

        complete = cursor >= cells;
        enters += entered;

        // ── 2. The events: this tick's, or every missed tick's folded ──
        if (gap > 0)
        {
            for (var i = 0; i < log.Count; i++)
            {
                ref var entry = ref log.Entries[i];
                ref readonly var e = ref entry.Last;
                var was = (entry.FirstFlags & PushEvent.HasOld) != 0 && ((entry.OldCy * _gridW) + entry.OldCx) < oldCursor;
                var isIn = (e.Flags & PushEvent.HasNew) != 0 && ((e.NewCy * _gridW) + e.NewCx) < cursor;
                EmitWorld(in e, was, isIn, entry.Groups, entry.Segment, scratch, ref enters, ref leaves, ref updates);
            }
        }
        else
        {
            var slot = _log[tick % LogDepth];
            for (var k = 0; slot.Valid && slot.Tick == tick && k < slot.CellCount; k++)
            {
                for (var i = slot.Starts[k]; i < slot.PrimaryEnds[k]; i++)
                {
                    ref readonly var e = ref slot.Events[i];
                    if ((archetypeMask & (1UL << e.Archetype)) == 0)
                    {
                        continue;
                    }

                    var was = (e.Flags & PushEvent.HasOld) != 0 && ((e.OldCy * _gridW) + e.OldCx) < oldCursor;
                    var isIn = (e.Flags & PushEvent.HasNew) != 0 && ((e.NewCy * _gridW) + e.NewCx) < cursor;
                    EmitWorld(in e, was, isIn, e.Groups, (e.Flags & PushEvent.Segment) != 0, scratch, ref enters, ref leaves, ref updates);
                }
            }
        }

        st.PCursor = cursor;
        Interlocked.Add(ref GatherTicks, Stopwatch.GetTimestamp() - from);
        return flagged;
    }

    private static void EmitWorld(in PushEvent e, bool was, bool isIn, int groups, bool segment, FrameWorkerScratch scratch, ref long enters,
        ref long leaves, ref long updates)
    {
        if (isIn && !was)
        {
            scratch.Add(e.Archetype, FrameListKind.Enter, new FrameRecord { NetId = e.NetId, Block = e.Block, Slot = e.Slot, Archetype = e.Archetype });
            enters++;
        }
        else if (isIn)
        {
            var merged = e;
            merged.Groups = (byte)groups;
            merged.Flags = segment ? (byte)(e.Flags | PushEvent.Segment) : (byte)(e.Flags & ~PushEvent.Segment);
            EmitUpdate(in merged, scratch, ref updates);
        }
        else if (was)
        {
            scratch.Add(e.Archetype, FrameListKind.Leave, new FrameRecord { NetId = e.NetId, Archetype = e.Archetype });
            leaves++;
        }
    }

    /// <summary>The World form of <see cref="CollectLog"/>: every cell, primaries only (each event once per tick).</summary>
    private bool CollectLogWorld(uint first, int cursor, ulong archetypeMask, LogTable table)
    {
        for (var t = first; t != _tick + 1; t++)
        {
            var slot = _log[t % LogDepth];
            for (var k = 0; k < slot.CellCount; k++)
            {
                for (var i = slot.Starts[k]; i < slot.PrimaryEnds[k]; i++)
                {
                    ref readonly var e = ref slot.Events[i];
                    if ((archetypeMask & (1UL << e.Archetype)) == 0)
                    {
                        continue;
                    }

                    ref var entry = ref table.Find(e.NetId, out var found);
                    if (!found)
                    {
                        entry = default;
                        entry.FirstFlags = e.Flags;
                        entry.OldX = e.OldX;
                        entry.OldY = e.OldY;
                        entry.OldCx = e.OldCx;
                        entry.OldCy = e.OldCy;
                    }
                    else if ((entry.Last.Flags & PushEvent.HasNew) == 0)
                    {
                        entry.Replaced = true;
                    }

                    entry.Last = e;
                    entry.LastTick = t;
                    entry.Groups |= e.Groups;
                    entry.Segment |= (e.Flags & PushEvent.Segment) != 0;
                }
            }
        }

        for (var i = 0; i < table.Count; i++)
        {
            ref var entry = ref table.Entries[i];
            if (entry.Replaced && (entry.FirstFlags & PushEvent.HasOld) != 0 && ((entry.OldCy * _gridW) + entry.OldCx) < cursor
                && (entry.Last.Flags & PushEvent.HasNew) != 0)
            {
                return false;
            }
        }

        return true;
    }

    // ══ The push log's catch-up ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [ThreadStatic]
    private static LogTable Log;

    /// <summary>
    /// Per worker: the missed ticks' events, folded per identity — the first event's old position, the last one's new, and the union of what changed.
    /// </summary>
    private sealed class LogTable
    {
        public uint[] Keys = new uint[256];
        public int[] Index = new int[256];
        public LogEntry[] Entries = new LogEntry[128];
        public int Count;

        public void Clear()
        {
            if (Count > 0)
            {
                Array.Clear(Keys);
                Count = 0;
            }
        }

        public ref LogEntry Find(uint netId, out bool found)
        {
            if (Count * 2 >= Keys.Length)
            {
                Grow();
            }

            var mask = Keys.Length - 1;
            var h = (int)((netId * 0x9E3779B1u) >> 8) & mask;
            while (true)
            {
                var k = Keys[h];
                if (k == 0)
                {
                    Keys[h] = netId + 1;
                    Index[h] = Count;
                    if (Count == Entries.Length)
                    {
                        Array.Resize(ref Entries, Count * 2);
                    }

                    found = false;
                    return ref Entries[Count++];
                }

                if (k == netId + 1)
                {
                    found = true;
                    return ref Entries[Index[h]];
                }

                h = (h + 1) & mask;
            }
        }

        private void Grow()
        {
            var keys = new uint[Keys.Length * 2];
            var index = new int[keys.Length];
            var mask = keys.Length - 1;
            for (var i = 0; i < Keys.Length; i++)
            {
                if (Keys[i] == 0)
                {
                    continue;
                }

                var h = (int)(((Keys[i] - 1) * 0x9E3779B1u) >> 8) & mask;
                while (keys[h] != 0)
                {
                    h = (h + 1) & mask;
                }

                keys[h] = Keys[i];
                index[h] = Index[i];
            }

            Keys = keys;
            Index = index;
        }
    }

    private struct LogEntry
    {
        public PushEvent Last;
        public float OldX;
        public float OldY;
        public short OldCx;
        public short OldCy;
        public uint LastTick;
        public byte FirstFlags;
        public byte Groups;
        public bool Segment;
        public bool Replaced;

        // Distance LOD: the far flushes folded in (flagged events and flush entries), and whether any real event was.
        public byte FlushGroups;
        public bool FlushSegment;
        public bool Flushed;
        public bool Real;
    }

    /// <summary>Whether the log holds every tick from <paramref name="first"/> to <paramref name="last"/>.</summary>
    private bool LogCovers(uint first, uint last)
    {
        for (var t = first; t != last + 1; t++)
        {
            var slot = _log[t % LogDepth];
            if (!slot.Valid || slot.Tick != t)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Folds every event the session missed, in tick order, over the cells either disc can reach. Returns <see langword="false"/> when an identity it held
    /// was reused inside the window and would be both left and entered in one frame (SUB-06), which only a RESET can say.
    /// </summary>
    private bool CollectLog(ref PushSessionState st, Vector3D viewpoint, ulong archetypeMask, LogTable table, double r2)
    {
        var ax = st.AnchorX;
        var ay = st.AnchorY;
        var minCx = CellX(Math.Min(ax, viewpoint.X) - Radius);
        var maxCx = CellX(Math.Max(ax, viewpoint.X) + Radius);
        var minCy = CellY(Math.Min(ay, viewpoint.Y) - Radius);
        var maxCy = CellY(Math.Max(ay, viewpoint.Y) + Radius);
        for (var t = st.LastTick + 1; t != _tick + 1; t++)
        {
            var slot = _log[t % LogDepth];
            for (var cy = minCy; cy <= maxCy; cy++)
            {
                var lo = (cy * _gridW) + minCx;
                var hi = (cy * _gridW) + maxCx;
                var k = LowerBound(slot.Cells, slot.CellCount, lo);
                for (; k < slot.CellCount && slot.Cells[k] <= hi; k++)
                {
                    for (var i = slot.Starts[k]; i < slot.Starts[k + 1]; i++)
                    {
                        ref readonly var e = ref slot.Events[i];
                        if ((archetypeMask & (1UL << e.Archetype)) == 0)
                        {
                            continue;
                        }

                        ref var entry = ref table.Find(e.NetId, out var found);
                        if (!found)
                        {
                            entry = default;
                            entry.FirstFlags = e.Flags;
                            entry.OldX = e.OldX;
                            entry.OldY = e.OldY;
                            entry.OldCx = e.OldCx;
                            entry.OldCy = e.OldCy;
                        }
                        else if (entry.LastTick == t)
                        {
                            // The same event, met again — as a secondary, or as the primary after its secondary. A secondary is a copy taken before the
                            // fold flagged the primary, so a far flush is only ever on the primary: take it from whichever copy carries it.
                            if ((e.Flags & PushEvent.FarFlush) != 0)
                            {
                                entry.FlushGroups |= e.FlushGroups;
                                entry.FlushSegment |= (e.Flags & PushEvent.FlushSegment) != 0;
                                entry.Flushed = true;
                            }

                            continue;
                        }
                        else if ((entry.Last.Flags & PushEvent.HasNew) == 0)
                        {
                            // An event after the identity was released: it names somebody else now.
                            entry.Replaced = true;
                        }

                        entry.Last = e;
                        entry.LastTick = t;
                        entry.Groups |= e.Groups;
                        entry.Segment |= (e.Flags & PushEvent.Segment) != 0;
                        entry.Real = true;
                        if ((e.Flags & PushEvent.FarFlush) != 0)
                        {
                            entry.FlushGroups |= e.FlushGroups;
                            entry.FlushSegment |= (e.Flags & PushEvent.FlushSegment) != 0;
                            entry.Flushed = true;
                        }
                    }
                }
            }

            // The tick's far flushes of entities whose latest event is older: their position is that event's, and nothing else changed since.
            for (var cy = minCy; FarEvery > 1 && cy <= maxCy; cy++)
            {
                var lo = (cy * _gridW) + minCx;
                var hi = (cy * _gridW) + maxCx;
                var k = LowerBound(slot.FlushCells, slot.FlushCellCount, lo);
                for (; k < slot.FlushCellCount && slot.FlushCells[k] <= hi; k++)
                {
                    for (var i = slot.FlushStarts[k]; i < slot.FlushStarts[k + 1]; i++)
                    {
                        ref readonly var f = ref slot.Flush[i];
                        if ((archetypeMask & (1UL << f.Archetype)) == 0)
                        {
                            continue;
                        }

                        ref var entry = ref table.Find(f.NetId, out var found);
                        if (!found)
                        {
                            entry = default;
                            entry.FirstFlags = f.Flags;
                            entry.OldX = f.OldX;
                            entry.OldY = f.OldY;
                            entry.OldCx = f.OldCx;
                            entry.OldCy = f.OldCy;
                        }
                        else if ((entry.Last.Flags & PushEvent.HasNew) == 0)
                        {
                            entry.Replaced = true;
                        }

                        entry.Last = f;
                        entry.LastTick = t;
                        entry.FlushGroups |= f.FlushGroups;
                        entry.FlushSegment |= (f.Flags & PushEvent.FlushSegment) != 0;
                        entry.Flushed = true;
                    }
                }
            }
        }

        // An identity the session held that now names another entity it would enter: leave and enter in one frame, which only a RESET can carry. The
        // enter half is tested against the viewpoint, a superset of what the gather will deliver, so the refusal is conservative.
        for (var i = 0; i < table.Count; i++)
        {
            ref var entry = ref table.Entries[i];
            if (entry.Replaced && WasKnown(ref st, in entry, r2) && (entry.Last.Flags & PushEvent.HasNew) != 0
                && Within(viewpoint.X, viewpoint.Y, entry.Last.NewX, entry.Last.NewY, (Radius + CellSize) * (Radius + CellSize)))
            {
                return false;
            }
        }

        return true;
    }

    private bool WasKnown(ref PushSessionState st, in LogEntry entry, double r2) =>
        (entry.FirstFlags & PushEvent.HasOld) != 0 && Within(st.AnchorX, st.AnchorY, entry.OldX, entry.OldY, r2)
        && Bit(st.D0, st.D1, st.D2, st.D3, WindowIndex(st.OriginX, st.OriginY, entry.OldCx, entry.OldCy));

    /// <summary>The folded events against the committed disc and window (was) and the new ones (is).</summary>
    private void EmitLog(LogTable table, double ax, double ay, int oOriginX, int oOriginY, ulong o0, ulong o1, ulong o2, ulong o3, double nx, double ny,
        int nOriginX, int nOriginY, ulong d0, ulong d1, ulong d2, ulong d3, double r2, FrameWorkerScratch scratch, ref long enters, ref long leaves,
        ref long updates, bool lod, double farR2)
    {
        for (var i = 0; i < table.Count; i++)
        {
            ref var entry = ref table.Entries[i];
            ref readonly var e = ref entry.Last;
            var was = (entry.FirstFlags & PushEvent.HasOld) != 0 && Within(ax, ay, entry.OldX, entry.OldY, r2)
                && Bit(o0, o1, o2, o3, WindowIndex(oOriginX, oOriginY, entry.OldCx, entry.OldCy));
            var isIn = (e.Flags & PushEvent.HasNew) != 0 && Within(nx, ny, e.NewX, e.NewY, r2)
                && Bit(d0, d1, d2, d3, WindowIndex(nOriginX, nOriginY, e.NewCx, e.NewCy));

            // Only far flushes and no event: the entity neither moved nor changed since, and a flush entry stamps nothing — so an enter or a leave is the cell
            // delivery's or the sweep's, and a crossing inward is the inner crescent's. It speaks only to a session holding it far before and after.
            if (!entry.Real)
            {
                if (lod && was && isIn && !Within(nx, ny, e.NewX, e.NewY, farR2) && !Within(ax, ay, entry.OldX, entry.OldY, farR2))
                {
                    EmitRecord(e.NetId, e.Block, e.Slot, e.Archetype, entry.FlushGroups, entry.FlushSegment, scratch, ref updates);
                }

                continue;
            }

            if (isIn && !was)
            {
                scratch.Add(e.Archetype, FrameListKind.Enter, new FrameRecord { NetId = e.NetId, Block = e.Block, Slot = e.Slot, Archetype = e.Archetype });
                enters++;
            }
            else if (isIn)
            {
                var flushFlags = entry.Flushed ? (byte)(PushEvent.FarFlush | (entry.FlushSegment ? PushEvent.FlushSegment : 0)) : (byte)0;
                EmitUpdateLod(in e, entry.OldX, entry.OldY, entry.Groups, entry.Segment, entry.FlushGroups, flushFlags, ax, ay, nx, ny, lod, farR2,
                    scratch, ref updates);
            }
            else if (was)
            {
                scratch.Add(e.Archetype, FrameListKind.Leave, new FrameRecord { NetId = e.NetId, Archetype = e.Archetype });
                leaves++;
            }
        }
    }

    /// <summary>
    /// An update to an entity the session holds before and after: sent, withheld (far then and far now, not its far flush), the far flush's union, or
    /// widened to the whole state (it crossed inward, so changes withheld while it was far may be missing). Of <c>flags</c> only
    /// <see cref="PushEvent.FarFlush"/> and <see cref="PushEvent.FlushSegment"/> are read.
    /// </summary>
    private void EmitUpdateLod(in PushEvent e, float oldX, float oldY, int groups, bool segment, int flushGroups, byte flags, double ax, double ay, double nx,
        double ny, bool lod, double farR2, FrameWorkerScratch scratch, ref long updates)
    {
        if (lod)
        {
            var farNow = !Within(nx, ny, e.NewX, e.NewY, farR2);
            var farBefore = !Within(ax, ay, oldX, oldY, farR2);
            if (farNow && farBefore)
            {
                if ((flags & PushEvent.FarFlush) == 0)
                {
                    scratch.Deferred++;
                    return;
                }

                groups = flushGroups;
                segment = (flags & PushEvent.FlushSegment) != 0;
            }
            else if (farBefore)
            {
                var plan = _encodePlans[e.Archetype];
                groups = (1 << plan.GroupCount) - 1;
                segment = plan.Moving;
            }
        }

        EmitRecord(e.NetId, e.Block, e.Slot, e.Archetype, groups, segment, scratch, ref updates);
    }

    /// <summary>Distance LOD: this tick's flush entries in the disc, to a session that held the entity before this frame and holds it far now.</summary>
    private void FlushEntries(double ax, double ay, int oOriginX, int oOriginY, ulong o0, ulong o1, ulong o2, ulong o3, double nx, double ny, int nOriginX,
        int nOriginY, ulong d0, ulong d1, ulong d2, ulong d3, double r2, double farR2, ulong archetypeMask, FrameWorkerScratch scratch, ref long updates)
    {
        var slot = _log[_tick % LogDepth];
        if (slot.FlushCellCount == 0 || slot.Tick != _tick)
        {
            return;
        }

        var minCx = CellX(nx - Radius);
        var maxCx = CellX(nx + Radius);
        var minCy = CellY(ny - Radius);
        var maxCy = CellY(ny + Radius);
        for (var cy = minCy; cy <= maxCy; cy++)
        {
            var k = LowerBound(slot.FlushCells, slot.FlushCellCount, (cy * _gridW) + minCx);
            for (; k < slot.FlushCellCount && slot.FlushCells[k] <= (cy * _gridW) + maxCx; k++)
            {
                var cell = slot.FlushCells[k];
                var x0 = _gridMinX + ((cell % _gridW) * CellSize);
                var y0 = _gridMinY + ((cell / _gridW) * CellSize);
                if (RectMax2(nx, ny, x0, y0, CellSize) <= farR2 || RectMin2(nx, ny, x0, y0, CellSize) > r2)
                {
                    continue;
                }

                for (var i = slot.FlushStarts[k]; i < slot.FlushStarts[k + 1]; i++)
                {
                    ref readonly var f = ref slot.Flush[i];
                    if ((archetypeMask & (1UL << f.Archetype)) == 0 || Within(nx, ny, f.NewX, f.NewY, farR2) || !Within(nx, ny, f.NewX, f.NewY, r2)
                        || !Bit(d0, d1, d2, d3, WindowIndex(nOriginX, nOriginY, f.NewCx, f.NewCy)) || !Within(ax, ay, f.NewX, f.NewY, r2)
                        || !Bit(o0, o1, o2, o3, WindowIndex(oOriginX, oOriginY, f.NewCx, f.NewCy)))
                    {
                        continue;
                    }

                    EmitRecord(f.NetId, f.Block, f.Slot, f.Archetype, f.FlushGroups, (f.Flags & PushEvent.FlushSegment) != 0, scratch, ref updates);
                }
            }
        }
    }

    /// <summary>
    /// Distance LOD: a cell of the inner crescent — the held entities the anchor's move brought from beyond R/2 to within it, with no event since the
    /// session's last frame, get what changed in the last N ticks: older changes went out in the far flush before, and newer ones wait for one a near
    /// session ignores.
    /// </summary>
    private void FarSweepCell(int cx, int cy, double ax, double ay, int oOriginX, int oOriginY, ulong o0, ulong o1, ulong o2, ulong o3, double nx, double ny,
        double r2, double farR2, ulong archetypeMask, FrameWorkerScratch scratch, uint tick, int gap, ref long updates)
    {
        if (!Bit(o0, o1, o2, o3, WindowIndex(oOriginX, oOriginY, cx, cy)))
        {
            return;
        }

        var x0 = _gridMinX + (cx * CellSize);
        var y0 = _gridMinY + (cy * CellSize);
        var near = Math.Sqrt(farR2);
        foreach (var a in _pushIndices)
        {
            if ((archetypeMask & (1UL << a)) == 0)
            {
                continue;
            }

            var state = _states[a];
            var cs = state.ClusterState;
            if (cs == null)
            {
                continue;
            }

            var plan = _encodePlans[a];
            var layout = state.Layout;
            var lo = tick > (uint)FarEvery ? tick - (uint)FarEvery : 0u;

            // The box is built from raw positions and the test below from decoded ones: a margin of a quantization step keeps the pruning sound.
            var margin = 0.01 + Math.Max(_stepX[a], _stepY[a]);
            using var e = cs.QueryAabb(cs.Grid, x0 - 1d, y0 - 1d, double.NegativeInfinity, x0 + CellSize + 1d, y0 + CellSize + 1d, double.PositiveInfinity);
            while (e.MoveNextClusterUnopened(out var chunkId, out var bx0, out var by0, out var bx1, out var by1))
            {
                // A box wholly beyond R/2 of the new anchor, or wholly within R/2 of the old one, holds nobody who crossed inward.
                if (!double.IsInfinity(bx0) && !double.IsInfinity(bx1)
                    && (BoxMin2(nx, ny, bx0, by0, bx1, by1) > (near + margin) * (near + margin)
                        || BoxMax2(ax, ay, bx0, by0, bx1, by1) <= (near - margin) * (near - margin)))
                {
                    continue;
                }

                var block = BlockOf(a, chunkId);
                if (block == null)
                {
                    continue;
                }

                var bytes = (byte*)block;
                var occ = block->ProjectedOccupancy;
                while (occ != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occ);
                    occ &= occ - 1;
                    var hot = (ReplicationHotEntry*)(bytes + layout.HotOffset + (slot * layout.HotStride));
                    if (hot->NetId == NetIdAllocator.NoNetId)
                    {
                        continue;
                    }

                    var cold = bytes + layout.ColdOffset + (slot * layout.ColdStride);
                    if (tick - *(uint*)(cold + layout.LastEventTickOffsetInColdEntry) <= (uint)gap)
                    {
                        continue;
                    }

                    Decode(a, cold + _positionOffset[a], out var px, out var py);
                    if (CellX(px) != cx || CellY(py) != cy || !Within(nx, ny, px, py, farR2) || Within(ax, ay, px, py, farR2)
                        || !Within(ax, ay, px, py, r2))
                    {
                        continue;
                    }

                    var groups = 0;
                    for (var g = 0; g < plan.GroupCount; g++)
                    {
                        if (hot->GroupTicks[plan.GroupTickSlot[g]] > lo)
                        {
                            groups |= 1 << g;
                        }
                    }

                    FarCrescentStates++;
                    EmitRecord(hot->NetId, (nint)block, (byte)slot, (ushort)a, groups, plan.Moving && hot->GroupTicks[plan.MotionTickSlot] > lo, scratch,
                        ref updates);
                }
            }
        }
    }

    // ══ Distance LOD: the far flushes (parallel stage after the index) ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>Whether this tick's far flushes have been folded.</summary>
    public bool FarFolded => _farFoldTick == _tick;

    /// <summary>
    /// The far-flush fold's serial half: how many chunks of cells the stage runs, or 0 when the LOD is off, the index is not built yet (the frame
    /// prologue then folds serially) or the fold already ran this tick.
    /// </summary>
    /// <remarks>
    /// An entity's far flush is its phase tick, <c>(netId + tick) % N == 0</c>, and carries the groups it changed in the last N ticks — read from the
    /// group stamps of its hot entry, which every change sets and which the previous phase flush, N ticks ago, covered up to. The candidates are the
    /// primaries of the last N log slots; the one that is the entity's latest event (its cold stamp names that tick) speaks for it. A change always makes
    /// an event, so an entity with anything to flush is always among them.
    /// </remarks>
    public int BeginFarFold(int workers)
    {
        if (FarEvery <= 1 || !Indexed || _farFoldTick == _tick)
        {
            return 0;
        }

        var k = Math.Max(1, Math.Min(workers, _gridH));
        if (_farBounds.Length < k + 1)
        {
            _farBounds = new int[k + 1];
        }

        // Cell ranges of about equal event counts, from this tick's compact index: a population is clustered, so equal cell counts are not equal work.
        var slot0 = _log[_tick % LogDepth];
        var totalEvents = slot0.CellCount == 0 ? 0 : slot0.Starts[slot0.CellCount];
        var cells = _gridW * _gridH;
        _farBounds[0] = 0;
        var at = 0;
        for (var i = 1; i < k; i++)
        {
            var target = (int)((long)totalEvents * i / k);
            while (at < slot0.CellCount && slot0.Starts[at] < target)
            {
                at++;
            }

            _farBounds[i] = at < slot0.CellCount ? Math.Max(_farBounds[i - 1], slot0.Cells[at]) : cells;
        }

        _farBounds[k] = cells;
        if (_farOut.Length < k)
        {
            Array.Resize(ref _farOut, k);
            Array.Resize(ref _farOutCount, k);
            Array.Resize(ref _farOutCells, k);
            Array.Resize(ref _farOutStarts, k);
            Array.Resize(ref _farOutCellCount, k);
            Array.Resize(ref _farOutFlagged, k);
        }

        for (var i = 0; i < k; i++)
        {
            _farOut[i] ??= new PushEvent[256];
            _farOutCells[i] ??= new int[64];
            _farOutStarts[i] ??= new int[65];
            _farOutCount[i] = 0;
            _farOutCellCount[i] = 0;
            _farOutFlagged[i] = 0;
        }

        _farChunkCount = k;
        _farFoldTick = _tick;
        return k;
    }

    /// <summary>
    /// One chunk of the fold: a contiguous range of cells, walked in order across the window's slots, so its output is already grouped by cell. A chunk
    /// owns its cells' events, which is what lets it flag this tick's in place.
    /// </summary>
    public void FoldFarChunk(int chunk)
    {
        if ((uint)chunk >= (uint)_farChunkCount)
        {
            return;
        }

        var c0 = _farBounds[chunk];
        var c1 = _farBounds[chunk + 1];
        var n = FarEvery;
        var t = _tick;

        // The window's floor, wrap-safe for the run's first ticks; and the phase, wrap-safe for a tick counter or net id past 2^32 when N is no power of two.
        var lo = t > (uint)n ? t - (uint)n : 0u;
        var tickPhase = t % (uint)n;
        Span<int> cursor = stackalloc int[LogDepth];
        for (var age = 0; age < n; age++)
        {
            var slot = _log[(t - (uint)age) % LogDepth];
            cursor[age] = slot.Valid && slot.Tick == t - (uint)age ? LowerBound(slot.Cells, slot.CellCount, c0) : int.MaxValue;
        }

        var output = _farOut[chunk];
        var count = 0;
        var cellsOut = _farOutCells[chunk];
        var startsOut = _farOutStarts[chunk];
        var cellCount = 0;
        var flagged = 0L;
        while (true)
        {
            // The next cell any slot has events in.
            var cell = int.MaxValue;
            for (var age = 0; age < n; age++)
            {
                var slot = _log[(t - (uint)age) % LogDepth];
                if (cursor[age] < slot.CellCount && slot.Cells[cursor[age]] < cell)
                {
                    cell = slot.Cells[cursor[age]];
                }
            }

            if (cell >= c1)
            {
                break;
            }

            var before = count;
            for (var age = 0; age < n; age++)
            {
                var slot = _log[(t - (uint)age) % LogDepth];
                if (cursor[age] >= slot.CellCount || slot.Cells[cursor[age]] != cell)
                {
                    continue;
                }

                var k = cursor[age]++;
                var events = slot.Events;
                for (var i = slot.Starts[k]; i < slot.PrimaryEnds[k]; i++)
                {
                    ref var e = ref events[i];
                    if ((e.Flags & PushEvent.HasNew) == 0 || ((e.NetId % (uint)n) + tickPhase) % (uint)n != 0)
                    {
                        continue;
                    }

                    var block = (ReplicationBlockHeader*)e.Block;
                    if (block == null || block->ChunkId < 0)
                    {
                        continue;
                    }

                    var layout = _states[e.Archetype].Layout;
                    var bytes = (byte*)block;
                    var hot = (ReplicationHotEntry*)(bytes + layout.HotOffset + (e.Slot * layout.HotStride));
                    // This tick's event is the entity's latest by definition; an older one only if nothing came after it (its stamp) and its slot still
                    // holds it (identity reuse).
                    if (age != 0 && (hot->NetId != e.NetId
                        || *(uint*)(bytes + layout.ColdOffset + (e.Slot * layout.ColdStride) + layout.LastEventTickOffsetInColdEntry) != slot.Tick))
                    {
                        continue;
                    }

                    var plan = _encodePlans[e.Archetype];
                    var groups = 0;
                    for (var g = 0; g < plan.GroupCount; g++)
                    {
                        if (hot->GroupTicks[plan.GroupTickSlot[g]] > lo)
                        {
                            groups |= 1 << g;
                        }
                    }

                    var segment = plan.Moving && hot->GroupTicks[plan.MotionTickSlot] > lo;
                    if (groups == 0 && !segment)
                    {
                        continue;
                    }

                    if (age == 0)
                    {
                        e.FlushGroups = (byte)groups;
                        e.Flags |= (byte)(PushEvent.FarFlush | (segment ? PushEvent.FlushSegment : 0));
                        flagged++;
                        continue;
                    }

                    if (count == output.Length)
                    {
                        Array.Resize(ref _farOut[chunk], count * 2);
                        output = _farOut[chunk];
                    }

                    ref var f = ref output[count++];
                    f = e;
                    f.OldX = e.NewX;
                    f.OldY = e.NewY;
                    f.OldCx = e.NewCx;
                    f.OldCy = e.NewCy;
                    f.Cell = (e.NewCy * _gridW) + e.NewCx;
                    f.Groups = 0;
                    f.FlushGroups = (byte)groups;
                    f.Flags = (byte)(PushEvent.HasOld | PushEvent.HasNew | PushEvent.FarFlush | (segment ? PushEvent.FlushSegment : 0));
                }
            }

            if (count != before)
            {
                if (cellCount + 1 >= startsOut.Length)
                {
                    Array.Resize(ref _farOutCells[chunk], (cellCount + 1) * 2);
                    Array.Resize(ref _farOutStarts[chunk], ((cellCount + 1) * 2) + 1);
                    cellsOut = _farOutCells[chunk];
                    startsOut = _farOutStarts[chunk];
                }

                cellsOut[cellCount] = cell;
                startsOut[cellCount] = before;
                cellCount++;
            }
        }

        _farOutCount[chunk] = count;
        _farOutCellCount[chunk] = cellCount;
        _farOutFlagged[chunk] = flagged;
    }

    /// <summary>
    /// The fold's serial tail, in the frame prologue: the chunks' flush entries concatenated, in chunk order — which is cell order — into the tick's log
    /// slot. Folds serially first when no stage did (the LOD on a tick whose index the prologue built).
    /// </summary>
    public void EndFarFold()
    {
        if (_farEndTick == _tick)
        {
            return;
        }

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

    private void EndFarFoldCore()
    {

        if (!FarFolded)
        {
            var chunks = BeginFarFold(1);
            for (var c = 0; c < chunks; c++)
            {
                FoldFarChunk(c);
            }
        }

        _farEndTick = _tick;
        var slot = _log[_tick % LogDepth];
        slot.FlushCellCount = 0;
        if (!FarFolded || slot.Tick != _tick)
        {
            return;
        }

        var total = 0;
        var totalCells = 0;
        for (var c = 0; c < _farChunkCount; c++)
        {
            total += _farOutCount[c];
            totalCells += _farOutCellCount[c];
            FarFlushes += _farOutFlagged[c];
        }

        FarFlushes += total;
        if (slot.Flush.Length < total)
        {
            slot.Flush = new PushEvent[Math.Max(total, slot.Flush.Length * 2)];
        }

        if (slot.FlushCells.Length < totalCells)
        {
            slot.FlushCells = new int[Math.Max(totalCells, slot.FlushCells.Length * 2)];
        }

        if (slot.FlushStarts.Length < totalCells + 1)
        {
            slot.FlushStarts = new int[Math.Max(totalCells + 1, slot.FlushStarts.Length * 2)];
        }

        var at = 0;
        var cellAt = 0;
        for (var c = 0; c < _farChunkCount; c++)
        {
            Array.Copy(_farOut[c], 0, slot.Flush, at, _farOutCount[c]);
            for (var k = 0; k < _farOutCellCount[c]; k++)
            {
                slot.FlushCells[cellAt] = _farOutCells[c][k];
                slot.FlushStarts[cellAt] = at + _farOutStarts[c][k];
                cellAt++;
            }

            at += _farOutCount[c];
        }

        slot.FlushStarts[cellAt] = at;
        slot.FlushCellCount = cellAt;
    }

    private static int LowerBound(int[] values, int count, int key)
    {
        var lo = 0;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitRecord(uint netId, nint block, byte slot, ushort archetype, int groups, bool segment, FrameWorkerScratch scratch, ref long updates)
    {
        if (segment)
        {
            scratch.Add(archetype, FrameListKind.Segment, new FrameRecord { NetId = netId, Block = block, Slot = slot, Archetype = archetype });
            updates++;
        }

        if (groups != 0)
        {
            scratch.Add(archetype, FrameListKind.State,
                new FrameRecord { NetId = netId, Block = block, Slot = slot, GroupMask = (byte)groups, Archetype = archetype });
            updates++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitUpdate(in PushEvent e, FrameWorkerScratch scratch, ref long updates)
    {
        if ((e.Flags & PushEvent.Segment) != 0)
        {
            scratch.Add(e.Archetype, FrameListKind.Segment, new FrameRecord { NetId = e.NetId, Block = e.Block, Slot = e.Slot, Archetype = e.Archetype });
            updates++;
        }

        if (e.Groups != 0)
        {
            scratch.Add(e.Archetype, FrameListKind.State,
                new FrameRecord { NetId = e.NetId, Block = e.Block, Slot = e.Slot, GroupMask = e.Groups, Archetype = e.Archetype });
            updates++;
        }
    }

    /// <summary>Enters every entity of the cell inside the disc that was not pushed this tick (a pushed one is the push step's).</summary>
    private int DeliverCell(int cx, int cy, double nx, double ny, double r2, ulong archetypeMask, FrameWorkerScratch scratch, uint tick, int gap,
        bool everywhere = false)
    {
        var entered = 0;
        var ax = nx;
        var ay = ny;
        const bool sweeping = false;
        var x0 = _gridMinX + (cx * CellSize);
        var y0 = _gridMinY + (cy * CellSize);
        foreach (var a in _pushIndices)
        {
            if ((archetypeMask & (1UL << a)) == 0)
            {
                continue;
            }

            var state = _states[a];
            var cs = state.ClusterState;
            if (cs == null)
            {
                continue;
            }

            var layout = state.Layout;
            using var e = cs.QueryAabb(cs.Grid, x0 - 1d, y0 - 1d, double.NegativeInfinity, x0 + CellSize + 1d, y0 + CellSize + 1d, double.PositiveInfinity);
            while (e.MoveNextClusterUnopened(out var chunkId, out var bx0, out var by0, out var bx1, out var by1))
            {
                if (!everywhere && SkipCluster(bx0, by0, bx1, by1, ax, ay, nx, ny, sweeping))
                {
                    continue;
                }

                var block = BlockOf(a, chunkId);
                if (block == null)
                {
                    continue;
                }

                var bytes = (byte*)block;
                var occ = block->ProjectedOccupancy;
                while (occ != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occ);
                    occ &= occ - 1;
                    var hot = (ReplicationHotEntry*)(bytes + layout.HotOffset + (slot * layout.HotStride));
                    if (hot->NetId == NetIdAllocator.NoNetId)
                    {
                        continue;
                    }

                    var cold = bytes + layout.ColdOffset + (slot * layout.ColdStride);
                    // Owned by the push step (or, after missed frames, by the log's replay): it had an event since the session's last frame.
                    if (tick - *(uint*)(cold + layout.LastEventTickOffsetInColdEntry) <= (uint)gap)
                    {
                        continue;
                    }

                    Decode(a, cold + _positionOffset[a], out var px, out var py);
                    if (CellX(px) != cx || CellY(py) != cy || (!everywhere && !Within(nx, ny, px, py, r2)))
                    {
                        continue;
                    }

                    scratch.Add(a, FrameListKind.Enter, new FrameRecord { NetId = hot->NetId, Block = (nint)block, Slot = (byte)slot, Archetype = (ushort)a });
                    entered++;
                }
            }
        }

        return entered;
    }

    /// <summary>Emits the enters and leaves the anchor's move caused among one delivered cell's entities that were not pushed this tick.</summary>
    private void SweepCell(int cx, int cy, double ax, double ay, double nx, double ny, double r2, ulong archetypeMask, FrameWorkerScratch scratch, uint tick,
        int gap, ref long enters, ref long leaves)
    {
        var x0 = _gridMinX + (cx * CellSize);
        var y0 = _gridMinY + (cy * CellSize);
        var visited = 0;
        const bool sweeping = true;
        foreach (var a in _pushIndices)
        {
            if ((archetypeMask & (1UL << a)) == 0)
            {
                continue;
            }

            var state = _states[a];
            var cs = state.ClusterState;
            if (cs == null)
            {
                continue;
            }

            var layout = state.Layout;
            using var e = cs.QueryAabb(cs.Grid, x0 - 1d, y0 - 1d, double.NegativeInfinity, x0 + CellSize + 1d, y0 + CellSize + 1d, double.PositiveInfinity);
            while (e.MoveNextClusterUnopened(out var chunkId, out var bx0, out var by0, out var bx1, out var by1))
            {
                if (SkipCluster(bx0, by0, bx1, by1, ax, ay, nx, ny, sweeping))
                {
                    continue;
                }

                var block = BlockOf(a, chunkId);
                if (block == null)
                {
                    continue;
                }

                var bytes = (byte*)block;
                var occ = block->ProjectedOccupancy;
                while (occ != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occ);
                    occ &= occ - 1;
                    var hot = (ReplicationHotEntry*)(bytes + layout.HotOffset + (slot * layout.HotStride));
                    if (hot->NetId == NetIdAllocator.NoNetId)
                    {
                        continue;
                    }

                    var cold = bytes + layout.ColdOffset + (slot * layout.ColdStride);
                    // Owned by the push step (or, after missed frames, by the log's replay): it had an event since the session's last frame.
                    if (tick - *(uint*)(cold + layout.LastEventTickOffsetInColdEntry) <= (uint)gap)
                    {
                        continue;
                    }

                    Decode(a, cold + _positionOffset[a], out var px, out var py);
                    if (CellX(px) != cx || CellY(py) != cy)
                    {
                        continue;
                    }

                    visited++;
                    var was = Within(ax, ay, px, py, r2);
                    var isIn = Within(nx, ny, px, py, r2);
                    if (isIn && !was)
                    {
                        scratch.Add(a, FrameListKind.Enter, new FrameRecord { NetId = hot->NetId, Block = (nint)block, Slot = (byte)slot, Archetype = (ushort)a });
                        enters++;
                    }
                    else if (was && !isIn)
                    {
                        scratch.Add(a, FrameListKind.Leave, new FrameRecord { NetId = hot->NetId, Archetype = (ushort)a });
                        leaves++;
                    }
                }
            }
        }

        Interlocked.Add(ref SweepSlots, visited);
    }
}
