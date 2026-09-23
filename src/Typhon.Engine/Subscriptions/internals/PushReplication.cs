using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// PROTOTYPE — one projected slot of a push archetype this tick, with where it was and where it is. The unit the frame stage fans out.
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
}

/// <summary>PROTOTYPE — what the push path remembers about one session: its visibility anchor and which cells around it it has been given.</summary>
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

    // Computed by the gather, applied only when the frame is published (SUB-03's discipline: a frame that was not sent changes nothing).
    public double PAnchorX;
    public double PAnchorY;
    public int POriginX;
    public int POriginY;
    public ulong P0, P1, P2, P3;
}

/// <summary>
/// PROTOTYPE — push replication (<c>claude/design/Subscriptions/research/push-model.md</c> § 4): the developer marks what changed, the engine encodes it
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
    private readonly bool[] _isPush;
    private readonly int[] _pushIndices;
    private readonly bool[] _bootstrapped;

    // Position decode per archetype (2D: axes 0 and 1).
    private readonly double[] _minX;
    private readonly double[] _minY;
    private readonly double[] _stepX;
    private readonly double[] _stepY;
    private readonly int[] _axisBytes;

    /// <summary>The visibility radius.</summary>
    public readonly double Radius;

    /// <summary>The replication cell side: R / 3.</summary>
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
    private int[][] _pushChunks;
    private ulong[][] _pushMasks;
    private int[] _pushCount;

    // Per archetype, by chunk id: slots to push again next tick — still extrapolating, or denied an identity.
    private long[][] _repush;

    // Per worker, this tick's events, and per worker per cell how many primaries and secondaries it filed (so the index needs no counting pass).
    private PushEvent[][] _events = [];
    private int[] _eventCount = [];
    private int[][] _primaryCounts = [];
    private int[][] _secondaryCounts = [];

    // The index: events bucketed by cell, primaries first, then the secondaries (leave-only views of a mover filed under the cell it left).
    private PushEvent[] _indexed = [];
    private int[] _cellStart;
    private int[] _cellPrimaryEnd;
    private int[] _cellFill;

    private PushSessionState[] _sessions;
    private uint _tick;
    private ArchetypeEncodePlan[] _encodePlans = [];

    /// <summary>The frame stage's encode plans, whose group tick slots decide what an update carries.</summary>
    public void AttachEncodePlans(ArchetypeEncodePlan[] plans) => _encodePlans = plans;

    // Per archetype, this tick's push set's blocks, parallel to _pushChunks — looked up once in PrepareBlocks.
    private nint[][] _pushBlocks;

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
    public readonly bool Shadow = Environment.GetEnvironmentVariable("TYPHON_PUSH_SHADOW") == "1";
    private HashSet<uint>[] _shadow = [];
    private ushort[] _shadowGen = [];
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
        Decode(archetype, cold + layout.PrevPositionOffsetInColdEntry, out var x, out var y);
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

    public PushReplication(CompiledProjectionPlan[] plans, ArchetypeReplicationState[] states, bool[] isPush, double radius, int maxSessions)
    {
        _plans = plans;
        _states = states;
        _isPush = isPush;
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
        _minX = new double[plans.Length];
        _minY = new double[plans.Length];
        _stepX = new double[plans.Length];
        _stepY = new double[plans.Length];
        _axisBytes = new int[plans.Length];
        _pushChunks = new int[plans.Length][];
        _pushBlocks = new nint[plans.Length][];
        _pushMasks = new ulong[plans.Length][];
        _pushCount = new int[plans.Length];
        _repush = new long[plans.Length][];

        var gMinX = double.MaxValue;
        var gMinY = double.MaxValue;
        var gMaxX = double.MinValue;
        var gMaxY = double.MinValue;
        foreach (var a in _pushIndices)
        {
            var position = plans[a].Position;
            if (position == null || !position.Moving || position.Dims != 2 || plans[a].BlockLayout.PrevPositionBytes == 0)
            {
                throw new NotSupportedException(
                    $"Archetype '{plans[a].Name}' is push-served; the push prototype supports 2D moving positions only.");
            }

            var pos = position.Pos;
            _minX[a] = pos.Min[0];
            _minY[a] = pos.Min[1];
            _stepX[a] = WireMath.QuantStep(pos.Min[0], pos.Max[0], pos.Bits);
            _stepY[a] = WireMath.QuantStep(pos.Min[1], pos.Max[1], pos.Bits);
            _axisBytes[a] = pos.Bits / 8;
            gMinX = Math.Min(gMinX, pos.Min[0]);
            gMinY = Math.Min(gMinY, pos.Min[1]);
            gMaxX = Math.Max(gMaxX, pos.Max[0]);
            gMaxY = Math.Max(gMaxY, pos.Max[1]);
            _pushChunks[a] = new int[64];
            _pushBlocks[a] = new nint[64];
            _pushMasks[a] = new ulong[64];
            _repush[a] = [];
        }

        Radius = radius;
        CellSize = radius / 3d;
        AnchorSlack = CellSize / 16d;
        // Two cells of margin past the radius: the anchor moves at most one cell before a move is treated as a teleport, so a cell can leave the window
        // only when every point of it is past R from both the old anchor and the new one — no known entity is ever dropped with its cell.
        Half = (int)Math.Ceiling(radius / CellSize) + 2;
        Window = (2 * Half) + 1;
        if (Window > 16)
        {
            throw new NotSupportedException("Push window wider than 16 cells.");
        }

        _gridMinX = gMinX;
        _gridMinY = gMinY;
        _gridW = Math.Max(1, (int)Math.Ceiling((gMaxX - gMinX) / CellSize) + 1);
        _gridH = Math.Max(1, (int)Math.Ceiling((gMaxY - gMinY) / CellSize) + 1);
        if ((long)_gridW * _gridH > 16_000_000)
        {
            throw new NotSupportedException($"Push grid of {_gridW} x {_gridH} cells is too large for the prototype's dense index.");
        }

        _cellStart = new int[(_gridW * _gridH) + 1];
        _cellPrimaryEnd = new int[_gridW * _gridH];
        _cellFill = new int[_gridW * _gridH];
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

                    Decode(a, bytes + layout.ColdOffset + (s * layout.ColdStride) + layout.PrevPositionOffsetInColdEntry, out var px, out var py);
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

    /// <summary>Whether plan <paramref name="archetype"/> is push-served.</summary>
    public bool IsPush(int archetype) => (uint)archetype < (uint)_isPush.Length && _isPush[archetype];

    // ══ Blocks step (serial) ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Before the parked drain: drops last tick's push marks, collects this tick's push set, and gives every cluster in it a block — so an entity that
    /// migrated into a cluster with no block is drained into one this tick rather than dropped.
    /// </summary>
    public void PrepareBlocks(uint tick)
    {
        var from = Stopwatch.GetTimestamp();
        _tick = tick;
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
            var everything = !_bootstrapped[a] || (Volatile.Read(ref cs.StructureTick) == tick && cs.StructureCoversAll);
            if (everything)
            {
                // First tick (or a tick the fence could not describe): every live entity is pushed, which is what gives every entity of a push archetype an
                // identity and an encoded state before any session asks — the geometric known-set assumes a described entity for every position.
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
    public void MarkPushed(int workers)
    {
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
            Array.Resize(ref _primaryCounts, workers);
            Array.Resize(ref _secondaryCounts, workers);
        }

        var cells = _gridW * _gridH;
        for (var w = 0; w < _events.Length; w++)
        {
            _events[w] ??= new PushEvent[1024];
            _eventCount[w] = 0;
            if (_primaryCounts[w] == null)
            {
                _primaryCounts[w] = new int[cells];
                _secondaryCounts[w] = new int[cells];
            }
            else
            {
                Array.Clear(_primaryCounts[w]);
                Array.Clear(_secondaryCounts[w]);
            }
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
        if ((flags & (both | PushEvent.Segment)) == both && groups == 0 && ox == nx && oy == ny)
        {
            // Un-stamp it: the sweep and the cell delivery skip an entity "pushed this tick" because the push step owns it — and with no event, nothing
            // would own it. An anchor that moves this tick must still see it.
            var layout = _states[archetype].Layout;
            *(uint*)((byte*)block + layout.ColdOffset + (slot * layout.ColdStride) + layout.LastWatchedTickOffsetInColdEntry) = _tick - 1;
            return;
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
        _primaryCounts[worker][e.Cell]++;
        if ((flags & both) == both && (e.OldCx != e.NewCx || e.OldCy != e.NewCy))
        {
            _secondaryCounts[worker][(e.OldCy * _gridW) + e.OldCx]++;
        }

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

    /// <summary>Buckets this tick's events by cell, primaries then secondaries within each cell, from counts the projecting workers already kept.</summary>
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
                _primaryCounts[0][_orphans[i].Cell]++;
            }

            _orphanCount = 0;
        }

        var cells = _gridW * _gridH;
        var workers = _events.Length;
        if (_workerOffsets.Length < workers)
        {
            Array.Resize(ref _workerOffsets, workers);
            Array.Resize(ref _workerSecondaryOffsets, workers);
        }

        // Prefix over (cell, worker): each worker's primaries for a cell, then each worker's secondaries for it. The per-worker running offsets are the
        // counts arrays themselves, rewritten in place.
        var running = 0;
        for (var c = 0; c < cells; c++)
        {
            _cellStart[c] = running;
            for (var w = 0; w < workers; w++)
            {
                var k = _primaryCounts[w][c];
                _primaryCounts[w][c] = running;
                running += k;
            }

            _cellPrimaryEnd[c] = running;
            for (var w = 0; w < workers; w++)
            {
                var k = _secondaryCounts[w][c];
                _secondaryCounts[w][c] = running;
                running += k;
            }
        }

        _cellStart[cells] = running;
        Events += running;
        if (_indexed.Length < running)
        {
            _indexed = new PushEvent[Math.Max(running, _indexed.Length * 2)];
        }

        const byte both = PushEvent.HasNew | PushEvent.HasOld;
        for (var w = 0; w < workers; w++)
        {
            var list = _events[w];
            var n = _eventCount[w];
            var primary = _primaryCounts[w];
            var secondary = _secondaryCounts[w];
            for (var i = 0; i < n; i++)
            {
                ref var e = ref list[i];
                _indexed[primary[e.Cell]++] = e;
                if ((e.Flags & both) == both && (e.OldCx != e.NewCx || e.OldCy != e.NewCy))
                {
                    // A secondary keeps the PRIMARY cell in Cell, so a session can tell whether it already met this event there.
                    _indexed[secondary[(e.OldCy * _gridW) + e.OldCx]++] = e;
                }
            }
        }

        IndexTicks += Stopwatch.GetTimestamp() - from;
    }

    private int[] _workerOffsets = [];
    private int[] _workerSecondaryOffsets = [];

    // ══ Per-session gather (parallel over sessions) ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Whether a session slot is in a state that needs a RESET before anything else is sent.</summary>
    public bool NeedsReset(SessionId session)
    {
        ref var st = ref _sessions[session.Slot];
        return !st.Bound || st.Generation != session.Generation ? false : st.NeedsReset;
    }

    /// <summary>A frame for the session was not published: what it would have said is lost, so the next one starts from a RESET.</summary>
    public void NoteNotPublished(SessionId session)
    {
        ref var st = ref _sessions[session.Slot];
        if (st.Bound && st.Generation == session.Generation && st.Anchored)
        {
            st.NeedsReset = true;
        }
    }

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
        st.Anchored = true;
        st.NeedsReset = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Bit(ulong d0, ulong d1, ulong d2, ulong d3, int i) =>
        i < 0 ? false : ((i >> 6) switch { 0 => d0, 1 => d1, 2 => d2, _ => d3 } >> (i & 63) & 1UL) != 0;

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
                    entered += DeliverCell(cx, cy, nx, ny, r2, archetypeMask, scratch, tick);
                    Interlocked.Increment(ref CellsDelivered);
                }
            }
        }

        // The budget did not bind, so every cell the disc reaches is delivered: the client holds its whole view.
        complete = entered < enterBudget;
        enters += entered;

        // ── 2. The push events around both anchors ─────────────────────────────────────────────────────────────────────────────────────────────────
        var minCx = CellX(Math.Min(ax, nx) - Radius);
        var maxCx = CellX(Math.Max(ax, nx) + Radius);
        var minCy = CellY(Math.Min(ay, ny) - Radius);
        var maxCy = CellY(Math.Max(ay, ny) + Radius);
        for (var cy = minCy; cy <= maxCy; cy++)
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
                        EmitUpdate(in e, scratch, ref updates);
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
                            EmitUpdate(in e, scratch, ref updates);
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

                    SweepCell(cx, cy, ax, ay, nx, ny, r2, archetypeMask, scratch, tick, ref enters, ref leaves);
                }
            }
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
    private int DeliverCell(int cx, int cy, double nx, double ny, double r2, ulong archetypeMask, FrameWorkerScratch scratch, uint tick)
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
                    if (*(uint*)(cold + layout.LastWatchedTickOffsetInColdEntry) == tick)
                    {
                        continue;
                    }

                    Decode(a, cold + layout.PrevPositionOffsetInColdEntry, out var px, out var py);
                    if (CellX(px) != cx || CellY(py) != cy || !Within(nx, ny, px, py, r2))
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
        ref long enters, ref long leaves)
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
                    if (*(uint*)(cold + layout.LastWatchedTickOffsetInColdEntry) == tick)
                    {
                        continue;
                    }

                    Decode(a, cold + layout.PrevPositionOffsetInColdEntry, out var px, out var py);
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
