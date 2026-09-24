using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The geometry of push replication (<c>claude/design/Subscriptions/10-phase15-3d-groundwork.md</c> § 3–§ 5, L6), written once and compiled per
/// implementation: <typeparamref name="TEvent"/> is <see cref="PushEvent"/> for a grid one cell deep — two axes, row-ordered keys, the 48-byte event — and
/// <see cref="PushEvent3"/> otherwise — three axes, tile-ordered keys, the 64-byte event. Every test of the third axis is behind
/// <c>TEvent.Deep</c>, a constant per instantiation, so the flat implementation carries none of it.
/// </summary>
/// <remarks>
/// <para><b>The flat implementation is the depth-1 case of the deep one's model</b> (10 § 3.4): its geometric z is 0 for anchors, entities, cells and
/// cluster bounds, so every z term would be +0.0 and every test would equal its 2D form bit for bit; it simply does not compute them.</para>
/// <para><b>Windows are rows of bits</b> (10 § 4.2): <c>W · W_z</c> rows of <c>W</c> cells, one <see cref="ushort"/> each — inline in the session state in the
/// flat implementation (<see cref="PushSessionState.D"/>, <see cref="PushSessionState.P"/>), in a slab indexed by session slot in the deep one. Moving
/// a window by a cell is a shift of every row.</para>
/// </remarks>
internal sealed unsafe class PushReplication<TEvent> : PushReplication where TEvent : unmanaged, IPushEvent<TEvent>
{
    /// <summary>The most rows a window can have: <see cref="ReplicationGrid.MaxWindow"/> in the flat implementation, 13² in the deep one (10 § 4.3).</summary>
    private const int MaxRows = 169;

    private static readonly ushort[] ZeroRows = new ushort[MaxRows];

    // Rows per window copy, and the deep implementation's slab: per session slot, the committed rows then the pending ones.
    private readonly int _windowRows;
    private readonly ushort[] _slab = [];

    // The z cell the plane z = 0 lies in: the only one a 2D-position archetype can occupy in a deep grid.
    private readonly int _zeroCz;

    // The compressed coordinate bits of the sort key (cells in the flat implementation, tiles in the deep one), and the sort key's width.
    private readonly int _bitsX;
    private readonly int _bitsY;
    private readonly int _sortBits;

    // Per worker, this tick's events.
    private TEvent[][] _events = [];
    private int[] _eventCount = [];

    // The index: events bucketed by cell, primaries first, then the secondaries (leave-only views of a mover filed under the cell it left). It is the current
    // tick's slot of the push log.
    private TEvent[] _indexed = [];

    private TEvent[] _orphans = new TEvent[64];

    // Distance LOD's per-tick fold (BeginFarFold): per chunk of cells, the flush entries it appended, in cell order, as a compact CSR.
    private uint _farFoldTick = uint.MaxValue;
    private uint _farEndTick = uint.MaxValue;
    private int _farChunkCount;
    private TEvent[][] _farOut = [];
    private int[] _farOutCount = [];
    private ulong[][] _farOutCells = [];
    private int[][] _farOutStarts = [];
    private int[] _farOutCellCount = [];
    private long[] _farOutFlagged = [];
    private ulong[] _farBounds = [];

    private readonly TickLog[] _log = CreateLog();

    public override bool Deep => TEvent.Deep;

    public PushReplication(CompiledProjectionPlan[] plans, ArchetypeReplicationState[] states, bool[] isPush, bool[] automatic, ReplicationGrid grid,
        int maxSessions, bool shadow)
        : base(plans, states, isPush, automatic, grid, maxSessions, shadow, TEvent.Deep)
    {
        if (TEvent.Deep)
        {
            // 13 cells per axis at most (10 § 4.3), which the grid refuses beyond — reachable only by a flat grid forced onto this implementation.
            if (Window * Window > MaxRows)
            {
                throw new NotSupportedException($"The deep implementation serves windows of at most 13 cells per axis; this grid's is {Window}.");
            }

            // Tiles of 4³ cells per axis; the six in-tile bits below them, the secondary bit below those.
            _bitsX = BitsFor((_gridW + 3) >> 2);
            _bitsY = BitsFor((_gridH + 3) >> 2);
            _sortBits = _bitsX + _bitsY + BitsFor((_gridD + 3) >> 2) + 6 + 1;
            _windowRows = Window * Window;
            _slab = new ushort[_sessions.Length * 2 * _windowRows];
            _zeroCz = Math.Clamp((int)Math.Floor((0d - _gridMinZ) / CellSize), 0, _gridD - 1);
        }
        else
        {
            _bitsX = BitsFor(_gridW);
            _bitsY = BitsFor(_gridH);
            _sortBits = _bitsX + _bitsY + 1;
            _windowRows = Window;
        }
    }

    private static int BitsFor(int count) => count <= 1 ? 1 : 32 - BitOperations.LeadingZeroCount((uint)(count - 1));

    // ══ Cells ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CellX(double x) => Math.Clamp((int)Math.Floor((x - _gridMinX) / CellSize), 0, _gridW - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CellY(double y) => Math.Clamp((int)Math.Floor((y - _gridMinY) / CellSize), 0, _gridH - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CellZ(double z) => TEvent.Deep ? Math.Clamp((int)Math.Floor((z - _gridMinZ) / CellSize), 0, _gridD - 1) : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong CellKey(double x, double y, double z) => TEvent.Key(CellX(x), CellY(y), CellZ(z));

    /// <summary>A last projected position, decoded: the flat implementation reads two axes and nothing else.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DecodeAt(int archetype, byte* quantized, out float x, out float y, out float z)
    {
        if (TEvent.Deep)
        {
            Decode(archetype, quantized, out x, out y, out z);
            return;
        }

        DecodePlane(archetype, quantized, out x, out y);
        z = 0f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong PrimaryKey(ref TEvent e) => (e.Flags & PushEvent.HasNew) != 0 ? e.NewKey : e.OldKey;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool InGrid(int cx, int cy, int cz) => (uint)cx < (uint)_gridW && (uint)cy < (uint)_gridH && (!TEvent.Deep || (uint)cz < (uint)_gridD);

    /// <summary>
    /// A sphere of interest: an anchor and a radius, with the squares every test reads — and the session's distance bands (09 § 9) as squared
    /// boundaries at this radius. Band 0 is inside the first boundary, sent every tick; band i is beyond boundary i, sent every <c>Ni</c> ticks.
    /// </summary>
    private readonly struct Ball
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;
        public readonly double R;
        public readonly double R2;

        public readonly int BandCount;
        public readonly double B1, B2, B3;
        public readonly int N1, N2, N3;
        public readonly int W1, W2, W3;

        public Ball(double x, double y, double z, double r) : this(x, y, z, r, default)
        {
        }

        public Ball(double x, double y, double z, double r, in LodBands bands)
        {
            X = x;
            Y = y;
            Z = z;
            R = r;
            R2 = r * r;
            BandCount = bands.Count;
            B1 = bands.F1 * bands.F1 * R2;
            B2 = bands.F2 * bands.F2 * R2;
            B3 = bands.F3 * bands.F3 * R2;
            N1 = bands.N1;
            N2 = bands.N2;
            N3 = bands.N3;
            W1 = bands.W1;
            W2 = bands.W2;
            W3 = bands.W3;
        }

        /// <summary>The innermost boundary squared: inside it every entity is near. <see cref="R2"/> without bands.</summary>
        public double Inner2 => BandCount > 0 ? B1 : R2;

        /// <summary>The outermost boundary squared: beyond it every entity is in the last band. <see cref="R2"/> without bands.</summary>
        public double Outer2 => BandCount switch
        {
            0 => R2,
            1 => B1,
            2 => B2,
            _ => B3,
        };

        /// <summary>The band of a point: 0 near, 1 … <see cref="BandCount"/> beyond each boundary.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int BandOf(double px, double py, double pz)
        {
            if (BandCount == 0)
            {
                return 0;
            }

            var dx = px - X;
            var dy = py - Y;
            var dz = pz - Z;
            var d2 = (dx * dx) + (dy * dy) + (dz * dz);
            return d2 <= B1 ? 0 : BandCount == 1 || d2 <= B2 ? 1 : BandCount == 2 || d2 <= B3 ? 2 : 3;
        }

        /// <summary>A band's period, in ticks; 1 for the near band.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int EveryOf(int band) => band switch
        {
            0 => 1,
            1 => N1,
            2 => N2,
            _ => N3,
        };

        /// <summary>A band's window — the history its flush carries — in ticks: its period, or longer for a while after the session's level fell.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WindowOf(int band) => band switch
        {
            0 => 1,
            1 => W1,
            2 => W2,
            _ => W3,
        };
    }

    /// <summary>The squared distance from a sphere's centre to the nearest point of a cell (a flat grid's cells have no depth, 10 § 3.4).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double CellMin2(in Ball b, int cx, int cy, int cz)
    {
        var x0 = _gridMinX + (cx * CellSize);
        var y0 = _gridMinY + (cy * CellSize);
        var dx = Math.Max(Math.Max(x0 - b.X, 0d), b.X - (x0 + CellSize));
        var dy = Math.Max(Math.Max(y0 - b.Y, 0d), b.Y - (y0 + CellSize));
        var d = (dx * dx) + (dy * dy);
        if (TEvent.Deep)
        {
            var z0 = _gridMinZ + (cz * CellSize);
            var dz = Math.Max(Math.Max(z0 - b.Z, 0d), b.Z - (z0 + CellSize));
            d += dz * dz;
        }

        return d;
    }

    /// <summary>The squared distance from a sphere's centre to the farthest corner of a cell.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double CellMax2(in Ball b, int cx, int cy, int cz)
    {
        var x0 = _gridMinX + (cx * CellSize);
        var y0 = _gridMinY + (cy * CellSize);
        var dx = Math.Max(b.X - x0, x0 + CellSize - b.X);
        var dy = Math.Max(b.Y - y0, y0 + CellSize - b.Y);
        var d = (dx * dx) + (dy * dy);
        if (TEvent.Deep)
        {
            var z0 = _gridMinZ + (cz * CellSize);
            var dz = Math.Max(b.Z - z0, z0 + CellSize - b.Z);
            d += dz * dz;
        }

        return d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double BoxMin2(in Ball b, double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var dx = Math.Max(Math.Max(x0 - b.X, 0d), b.X - x1);
        var dy = Math.Max(Math.Max(y0 - b.Y, 0d), b.Y - y1);
        var d = (dx * dx) + (dy * dy);
        if (TEvent.Deep)
        {
            var dz = Math.Max(Math.Max(z0 - b.Z, 0d), b.Z - z1);
            d += dz * dz;
        }

        return d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double BoxMax2(in Ball b, double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var dx = Math.Max(b.X - x0, x1 - b.X);
        var dy = Math.Max(b.Y - y0, y1 - b.Y);
        var d = (dx * dx) + (dy * dy);
        if (TEvent.Deep)
        {
            var dz = Math.Max(b.Z - z0, z1 - b.Z);
            d += dz * dz;
        }

        return d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Within(in Ball b, float x, float y, float z) => Within(b.X, b.Y, b.Z, x, y, z, b.R2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Within(double ax, double ay, double az, float x, float y, float z, double r2)
    {
        var dx = x - ax;
        var dy = y - ay;
        var d = (dx * dx) + (dy * dy);
        if (TEvent.Deep)
        {
            var dz = z - az;
            d += dz * dz;
        }

        return d <= r2;
    }

    // ══ Windows ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The row and bit of absolute cell (<paramref name="cx"/>, <paramref name="cy"/>, <paramref name="cz"/>) in a window at the given origin.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Local(int ox, int oy, int oz, int cx, int cy, int cz, out int row, out int bit)
    {
        var lx = cx - ox;
        var ly = cy - oy;
        bit = lx;
        if (TEvent.Deep)
        {
            var lz = cz - oz;
            row = (lz * Window) + ly;
            return (uint)lx < (uint)Window && (uint)ly < (uint)Window && (uint)lz < (uint)Window;
        }

        row = ly;
        return (uint)lx < (uint)Window && (uint)ly < (uint)Window;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Has(ReadOnlySpan<ushort> rows, int row, int bit) => ((rows[row] >> bit) & 1) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Set(Span<ushort> rows, int row, int bit) => rows[row] |= (ushort)(1 << bit);

    /// <summary>Whether the cell of <paramref name="key"/> is delivered in a window at the given origin.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Held(ReadOnlySpan<ushort> rows, int ox, int oy, int oz, ulong key) =>
        Local(ox, oy, oz, TEvent.KeyX(key), TEvent.KeyY(key), TEvent.KeyZ(key), out var row, out var bit) && Has(rows, row, bit);

    private Span<ushort> Committed(ref PushSessionState st, int slot) =>
        TEvent.Deep ? new Span<ushort>(_slab, slot * 2 * _windowRows, _windowRows) : ((Span<ushort>)st.D)[.._windowRows];

    private Span<ushort> Pending(ref PushSessionState st, int slot) =>
        TEvent.Deep ? new Span<ushort>(_slab, (slot * 2 * _windowRows) + _windowRows, _windowRows) : ((Span<ushort>)st.P)[.._windowRows];

    /// <summary>The old window's cells the new window still covers, into <paramref name="into"/> (cleared first): a shift of every row.</summary>
    private void ShiftWindow(ReadOnlySpan<ushort> old, int oox, int ooy, int ooz, int nox, int noy, int noz, Span<ushort> into)
    {
        into.Clear();
        var dx = nox - oox;
        var dy = noy - ooy;
        var dz = TEvent.Deep ? noz - ooz : 0;
        if (dx <= -Window || dx >= Window || dy <= -Window || dy >= Window || dz <= -Window || dz >= Window)
        {
            return;
        }

        var mask = (1 << Window) - 1;
        var planes = TEvent.Deep ? Window : 1;
        for (var lz = 0; lz < planes; lz++)
        {
            var olz = lz + dz;
            if ((uint)olz >= (uint)planes)
            {
                continue;
            }

            for (var ly = 0; ly < Window; ly++)
            {
                var oly = ly + dy;
                if ((uint)oly >= (uint)Window)
                {
                    continue;
                }

                // New cell lx is old cell lx + dx.
                int row = old[(olz * Window) + oly];
                into[(lz * Window) + ly] = (ushort)((dx >= 0 ? row >> dx : row << -dx) & mask);
            }
        }
    }

    /// <summary>A slot's first gather for a session: nothing of a previous occupant survives, the deep implementation's slab rows included.</summary>
    private void Bind(ref PushSessionState st, SessionId session)
    {
        st = default;
        // The slot's previous session left the census with the recount (RecountLevels), which sees only bound sessions: nothing to take out here.
        st.Bound = true;
        st.Generation = session.Generation;
        if (TEvent.Deep)
        {
            Array.Clear(_slab, session.Slot * 2 * _windowRows, 2 * _windowRows);
        }
    }

    private protected override bool LogHolds(uint first, uint last) => LogCovers(first, last);

    public override void CellOf(double x, double y, double z, out int cx, out int cy, out int cz)
    {
        cx = CellX(x);
        cy = CellY(y);
        cz = CellZ(z);
    }

    public override bool SeesPoint(SessionId session, float x, float y, float z, float viewRadius)
    {
        ref var st = ref _sessions[session.Slot];
        if (!st.Bound || st.Generation != session.Generation)
        {
            return false;
        }

        var pz = TEvent.Deep ? z : 0f;
        if (viewRadius > 0 && !Within(st.PAnchorX, st.PAnchorY, st.PAnchorZ, x, y, pz, (double)viewRadius * viewRadius))
        {
            return false;
        }

        var key = CellKey(x, y, pz);
        if (st.Anchored && st.Radius > 0 && Within(st.AnchorX, st.AnchorY, st.AnchorZ, x, y, pz, st.Radius * st.Radius)
            && Held(Committed(ref st, session.Slot), st.OriginX, st.OriginY, st.OriginZ, key))
        {
            return true;
        }

        return st.PRadius > 0 && Within(st.PAnchorX, st.PAnchorY, st.PAnchorZ, x, y, pz, st.PRadius * st.PRadius)
               && Held(Pending(ref st, session.Slot), st.POriginX, st.POriginY, st.POriginZ, key);
    }

    public override bool WorldSeesPoint(SessionId session, float x, float y, float z)
    {
        ref var st = ref _sessions[session.Slot];
        if (!st.Bound || st.Generation != session.Generation)
        {
            return false;
        }

        var key = CellKey(x, y, TEvent.Deep ? z : 0f);
        return key < st.Cursor || key < st.PCursor;
    }

    public override void SessionCellBox(SessionId session, out int minCx, out int maxCx, out int minCy, out int maxCy, out int minCz, out int maxCz)
    {
        ref var st = ref _sessions[session.Slot];
        var r = st.PRadius;
        double x0 = st.PAnchorX - r, x1 = st.PAnchorX + r, y0 = st.PAnchorY - r, y1 = st.PAnchorY + r, z0 = st.PAnchorZ - r, z1 = st.PAnchorZ + r;
        if (st.Anchored && st.Radius > 0)
        {
            var c = st.Radius;
            x0 = Math.Min(x0, st.AnchorX - c);
            x1 = Math.Max(x1, st.AnchorX + c);
            y0 = Math.Min(y0, st.AnchorY - c);
            y1 = Math.Max(y1, st.AnchorY + c);
            z0 = Math.Min(z0, st.AnchorZ - c);
            z1 = Math.Max(z1, st.AnchorZ + c);
        }

        minCx = CellX(x0);
        maxCx = CellX(x1);
        minCy = CellY(y0);
        maxCy = CellY(y1);
        minCz = CellZ(z0);
        maxCz = CellZ(z1);
    }

    public override void Commit(SessionId session)
    {
        ref var st = ref _sessions[session.Slot];
        st.AnchorX = st.PAnchorX;
        st.AnchorY = st.PAnchorY;
        st.AnchorZ = st.PAnchorZ;
        st.OriginX = st.POriginX;
        st.OriginY = st.POriginY;
        st.OriginZ = st.POriginZ;
        st.Radius = st.PRadius;
        CommitLevel(ref st);
        if (TEvent.Deep)
        {
            Pending(ref st, session.Slot).CopyTo(Committed(ref st, session.Slot));
        }
        else
        {
            st.D = st.P;
        }

        st.Cursor = st.PCursor;
        st.Anchored = true;
        st.NeedsReset = false;
        st.LastTick = _tick;
    }

    // ══ The push log, which is also the index ════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One tick of the push log, which is also that tick's index: the events in cell order, the occupied cells as an ascending CSR of keys, and a
    /// probe-unit table from each occupied unit (a row, or a tile) to its first cell.
    /// </summary>
    private sealed class TickLog
    {
        public uint Tick;
        public bool Valid;
        public TEvent[] Events = [];
        public ulong[] Cells = [];
        public int[] Starts = [];
        public int[] PrimaryEnds = [];
        public int CellCount;

        // Open addressing, unit + 1 as the key (0 is empty), sized to twice the occupied units: a probe per unit a session reads, an empty one costs one.
        private ulong[] _unitKeys = new ulong[16];
        private int[] _unitFirst = new int[16];
        private int _unitMask = 15;

        // Distance LOD: this tick's far flushes for entities whose latest event is an earlier tick, by cell (compact, ascending). An entity whose latest
        // event IS this tick carries its flush on that event instead.
        public TEvent[] Flush = [];
        public ulong[] FlushCells = [];
        public int[] FlushStarts = [];
        public int FlushCellCount;

        /// <summary>Rebuilds the unit table from <see cref="Cells"/>: O(occupied cells), clearing only the table it uses.</summary>
        public void BuildUnits()
        {
            var units = 0;
            var previous = ulong.MaxValue;
            for (var k = 0; k < CellCount; k++)
            {
                var unit = TEvent.Unit(Cells[k]);
                if (unit != previous)
                {
                    units++;
                    previous = unit;
                }
            }

            var size = 16;
            while (size < units * 2)
            {
                size <<= 1;
            }

            if (_unitKeys.Length < size)
            {
                _unitKeys = new ulong[size];
                _unitFirst = new int[size];
            }

            Array.Clear(_unitKeys, 0, size);
            _unitMask = size - 1;
            previous = ulong.MaxValue;
            for (var k = 0; k < CellCount; k++)
            {
                var unit = TEvent.Unit(Cells[k]);
                if (unit == previous)
                {
                    continue;
                }

                previous = unit;
                var h = Hash(unit) & _unitMask;
                while (_unitKeys[h] != 0)
                {
                    h = (h + 1) & _unitMask;
                }

                _unitKeys[h] = unit + 1;
                _unitFirst[h] = k;
            }
        }

        /// <summary>The index in <see cref="Cells"/> of the unit's first occupied cell, or -1 when the unit has none.</summary>
        public int UnitStart(ulong unit)
        {
            var h = Hash(unit) & _unitMask;
            while (true)
            {
                var k = _unitKeys[h];
                if (k == unit + 1)
                {
                    return _unitFirst[h];
                }

                if (k == 0)
                {
                    return -1;
                }

                h = (h + 1) & _unitMask;
            }
        }

        private static int Hash(ulong unit) => (int)((unit * 0x9E3779B97F4A7C15UL) >> 40);
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

    /// <summary>The flat implementation's probe: the first occupied cell of row <paramref name="cy"/> at or after column <paramref name="minCx"/>.</summary>
    private static int FirstCellInRow(TickLog slot, int cy, int minCx)
    {
        var k = slot.UnitStart(TEvent.UnitOf(0, cy, 0));
        if (k < 0)
        {
            return slot.CellCount;
        }

        // A row holds few cells: a short scan finds the first in the box, and only a long row falls back to the binary search.
        var target = TEvent.Key(minCx, cy, 0);
        var cells = slot.Cells;
        var scanEnd = Math.Min(slot.CellCount, k + 8);
        while (k < scanEnd && cells[k] < target)
        {
            k++;
        }

        return k < scanEnd || k == slot.CellCount ? k : LowerBound(cells, k, slot.CellCount, target);
    }

    /// <summary>
    /// The occupied cells of an ascending cell list that lie in a box of cells, probe unit by probe unit (10 § 2.3): a row at a time in the flat
    /// implementation, a tile at a time in the deep one. With a log slot, a unit is found by one probe of its table; without (a flush list), by a binary
    /// search.
    /// </summary>
    private ref struct BoxWalk
    {
        private readonly ulong[] _cells;
        private readonly int _count;
        private readonly TickLog _table;
        private readonly int _minX;
        private readonly int _maxX;
        private readonly int _minY;
        private readonly int _maxY;
        private readonly int _minZ;
        private readonly int _maxZ;
        private readonly int _uxHi;
        private readonly int _uyHi;
        private readonly int _uzHi;
        private readonly int _uxLo;
        private readonly int _uyLo;
        private int _ux;
        private int _uy;
        private int _uz;
        private int _k;
        private ulong _unit;
        private ulong _last;
        private bool _open;

        public BoxWalk(ulong[] cells, int count, TickLog table, int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
        {
            _cells = cells;
            _count = count;
            _table = table;
            _minX = minX;
            _maxX = maxX;
            _minY = minY;
            _maxY = maxY;
            _minZ = minZ;
            _maxZ = maxZ;
            if (TEvent.Deep)
            {
                _uxLo = minX >> 2;
                _uxHi = maxX >> 2;
                _uyLo = minY >> 2;
                _uyHi = maxY >> 2;
                _uz = minZ >> 2;
                _uzHi = maxZ >> 2;
            }
            else
            {
                _uxLo = _uxHi = 0;
                _uyLo = minY;
                _uyHi = maxY;
                _uz = _uzHi = 0;
            }

            _ux = _uxLo;
            _uy = _uyLo;
            _k = 0;
            _unit = 0;
            _last = 0;
            _open = false;
        }

        /// <summary>The next occupied cell in the box, as its index in the list; <see langword="false"/> once the box is exhausted.</summary>
        public bool Next(out int k)
        {
            while (true)
            {
                if (_open)
                {
                    while (_k < _count)
                    {
                        var cell = _cells[_k];
                        if (TEvent.Deep)
                        {
                            if (TEvent.Unit(cell) != _unit)
                            {
                                break;
                            }

                            var cx = TEvent.KeyX(cell);
                            var cy = TEvent.KeyY(cell);
                            var cz = TEvent.KeyZ(cell);
                            if (cx < _minX || cx > _maxX || cy < _minY || cy > _maxY || cz < _minZ || cz > _maxZ)
                            {
                                _k++;
                                continue;
                            }
                        }
                        else if (cell > _last)
                        {
                            break;
                        }

                        k = _k++;
                        return true;
                    }

                    _open = false;
                    if (!Advance())
                    {
                        k = -1;
                        return false;
                    }
                }

                if (_uz > _uzHi)
                {
                    k = -1;
                    return false;
                }

                Open();
            }
        }

        // Opens the current unit: its first cell in the box, and where it ends.
        private void Open()
        {
            _open = true;
            if (TEvent.Deep)
            {
                _unit = TEvent.UnitOf(_ux, _uy, _uz);
                if (_table != null)
                {
                    var start = _table.UnitStart(_unit);
                    _k = start < 0 ? _count : start;
                }
                else
                {
                    _k = LowerBound(_cells, 0, _count, TEvent.UnitFirstKey(_unit));
                }

                return;
            }

            _last = TEvent.Key(_maxX, _uy, 0);
            _k = _table != null ? FirstCellInRow(_table, _uy, _minX) : LowerBound(_cells, 0, _count, TEvent.Key(_minX, _uy, 0));
        }

        // The next unit, x fastest; false past the last.
        private bool Advance()
        {
            if (++_ux <= _uxHi)
            {
                return true;
            }

            _ux = _uxLo;
            if (++_uy <= _uyHi)
            {
                return true;
            }

            _uy = _uyLo;
            return ++_uz <= _uzHi;
        }
    }

    // ══ Projection ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private protected override void ResetWorkers(int workers)
    {
        if (_events.Length < workers)
        {
            Array.Resize(ref _events, workers);
            Array.Resize(ref _eventCount, workers);
        }

        for (var w = 0; w < _events.Length; w++)
        {
            _events[w] ??= new TEvent[1024];
            _eventCount[w] = 0;
        }

        EnsureRuns(_events.Length + 1);
    }

    public override void AddEvent(int worker, int archetype, ReplicationBlockHeader* block, int slot, ReplicationHotEntry* hot, uint netId, byte flags,
        float ox, float oy, float oz, float nx, float ny, float nz)
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
        if (!arrived && (flags & (both | PushEvent.Segment)) == both && groups == 0 && ox == nx && oy == ny && (!TEvent.Deep || oz == nz))
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
        e.OldZ = oz;
        e.NewX = nx;
        e.NewY = ny;
        e.NewZ = nz;
        e.Groups = (byte)groups;
        e.OldKey = CellKey(ox, oy, oz);
        e.NewKey = CellKey(nx, ny, nz);
        _eventCount[worker] = n + 1;
    }

    public override void Orphan(int archetype, ReplicationBlockHeader* block, byte* cold, in ReplicationBlockLayout layout, uint netId, int cause)
    {
        DecodeAt(archetype, cold + _positionOffset[archetype], out var x, out var y, out var z);
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
            e.OldZ = z;
            e.OldKey = CellKey(x, y, z);
            switch (cause)
            {
                case 0: OrphanRelease++; break;
                case 1: OrphanMigrate++; break;
                default: OrphanDrain++; break;
            }
        }
    }

    // ══ The push index (design/Subscriptions/10 § 2.3) ═══════════════════════════════════════════════════════════════════════════════════════════════
    //
    // Each projection chunk sorts its own events by cell as it finishes (SortRun), the PushIndex stage merges the sorted runs by key range (MergeChunk),
    // and a serial finish concatenates the chunks' cell lists and builds the unit table (FinishIndex). Every step costs O(events + occupied cells): no
    // array the size of the grid is touched, cleared or walked. The result is the tick's slot of the push log, so the log and the index are one thing.

    // The sort key of an index entry: the cell key compressed to the grid's used bits (order-preserving), shifted left once, with the low bit set for a
    // secondary — every cell's primaries sort before its secondaries by construction. Fewer bits, fewer radix passes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong SortKey(ulong key, uint secondary) => (TEvent.Compress(key, _bitsX, _bitsY) << 1) | secondary;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong KeyOfSortKey(ulong sortKey) => TEvent.Expand(sortKey >> 1, _bitsX, _bitsY);

    // Per run (a worker's events, then the fence's orphans last): the entries' sort keys and event indices, sorted, and the buffers the sort ping-pongs.
    private ulong[][] _runKey = [];
    private int[][] _runIdx = [];
    private ulong[][] _runKeyTmp = [];
    private int[][] _runIdxTmp = [];
    private int[] _runLen = [];
    private uint[] _runSortedTick = [];

    // The fence's orphans as the last run, copied out of the orphan list when the merge is prepared.
    private TEvent[] _orphanEvents = new TEvent[64];
    private int _orphanRunCount;

    // The merge: key-range splitters in sort-key space, and per chunk its cells in order.
    private int _mergeChunks;
    private uint _mergedTick = uint.MaxValue;
    private ulong[] _split = [];
    private ulong[][] _chunkCells = [];
    private int[][] _chunkStarts = [];
    private int[][] _chunkPrimaryEnds = [];
    private int[] _chunkCellCount = [];
    private ulong[] _samples = [];

    // Per chunk, the cells whose occupancy changed this tick and by how much, applied by the serial finish.
    private ulong[][] _chunkDeltaKeys = [];
    private int[][] _chunkDeltas = [];
    private int[] _chunkDeltaCount = [];

    public override void CountWorker(int worker)
    {
        if (!_countInProject || (uint)worker >= (uint)_events.Length)
        {
            return;
        }

        SortRun(worker, _events[worker], _eventCount[worker]);
    }

    public override int BeginParallelIndex()
    {
        if (!_countInProject || _events.Length == 0)
        {
            return 0;
        }

        var from = Stopwatch.GetTimestamp();
        PrepareMerge(_events.Length);
        IndexTicks += Stopwatch.GetTimestamp() - from;
        return _mergeChunks;
    }

    public override void PlaceWorker(int chunk)
    {
        if ((uint)chunk >= (uint)_mergeChunks || _mergedTick != _tick)
        {
            return;
        }

        var from = Stopwatch.GetTimestamp();
        MergeChunk(chunk);
        var spent = Stopwatch.GetTimestamp() - from;
        Interlocked.Add(ref IndexTicks, spent);
        Interlocked.Add(ref MergeTicks, spent);
    }

    public override void BuildIndex()
    {
        if (_mergedTick != _tick)
        {
            var from = Stopwatch.GetTimestamp();
            PrepareMerge(1);
            for (var c = 0; c < _mergeChunks; c++)
            {
                MergeChunk(c);
            }

            IndexTicks += Stopwatch.GetTimestamp() - from;
        }

        FinishIndex();
    }

    public override void FinishIndex()
    {
        if (_mergedTick != _tick || _indexedTick == _tick)
        {
            return;
        }

        var from = Stopwatch.GetTimestamp();
        var slot = _log[_tick % LogDepth];
        var cells = 0;
        for (var c = 0; c < _mergeChunks; c++)
        {
            cells += _chunkCellCount[c];
        }

        if (slot.Cells.Length < cells)
        {
            var grown = Math.Max(Math.Max(64, cells), slot.Cells.Length * 2);
            slot.Cells = new ulong[grown];
            slot.PrimaryEnds = new int[grown];
        }

        if (slot.Starts.Length < cells + 1)
        {
            slot.Starts = new int[Math.Max(cells + 1, slot.Starts.Length * 2)];
        }

        var at = 0;
        for (var c = 0; c < _mergeChunks; c++)
        {
            var n = _chunkCellCount[c];
            Array.Copy(_chunkCells[c], 0, slot.Cells, at, n);
            Array.Copy(_chunkStarts[c], 0, slot.Starts, at, n);
            Array.Copy(_chunkPrimaryEnds[c], 0, slot.PrimaryEnds, at, n);
            at += n;
        }

        slot.Starts[cells] = IndexEntries;
        slot.CellCount = cells;
        slot.BuildUnits();
        if (_recountAtFinish)
        {
            // After the projection every block's occupancy word and cold position describe this tick, arrivals and drained entries included.
            var recountFrom = Stopwatch.GetTimestamp();
            Recount(_occupancy);
            _recountAtFinish = false;
            OccupancyRecounts++;
            RecountTicks += Stopwatch.GetTimestamp() - recountFrom;
        }
        else
        {
            // Serial, and O(cells whose count changed): 10 § 2.4 applies them per merge chunk, which a concurrent map would need; not worth it at this size.
            for (var c = 0; c < _mergeChunks; c++)
            {
                var keys = _chunkDeltaKeys[c];
                var deltas = _chunkDeltas[c];
                for (var i = 0; i < _chunkDeltaCount[c]; i++)
                {
                    _occupancy.Add(keys[i], deltas[i]);
                }
            }
        }

        slot.Tick = _tick;
        slot.Valid = true;
        slot.FlushCellCount = 0;
        _indexed = slot.Events;
        IndexCells = cells;
        _indexedTick = _tick;
        IndexTicks += Stopwatch.GetTimestamp() - from;
        FinishTicks += Stopwatch.GetTimestamp() - from;
    }

    /// <summary>Fills a run from a list of events: one entry under each event's primary cell, and one more under the cell a mover left.</summary>
    private void FillRun(int run, TEvent[] events, int count)
    {
        const byte both = PushEvent.HasNew | PushEvent.HasOld;
        if (_runKey[run] == null || _runKey[run].Length < 2 * count)
        {
            // A power of two, so a load that creeps up reallocates a logarithmic number of times rather than on every new maximum (SUB-07).
            var size = (int)Math.Max(2048u, BitOperations.RoundUpToPowerOf2((uint)(2 * count)));
            _runKey[run] = new ulong[size];
            _runIdx[run] = new int[size];
            _runKeyTmp[run] = new ulong[size];
            _runIdxTmp[run] = new int[size];
        }

        var keys = _runKey[run];
        var idx = _runIdx[run];
        var n = 0;
        for (var i = 0; i < count; i++)
        {
            ref var e = ref events[i];
            keys[n] = SortKey(PrimaryKey(ref e), 0);
            idx[n++] = i;
            if ((e.Flags & both) == both && e.OldKey != e.NewKey)
            {
                keys[n] = SortKey(IndexMutantForTest ? e.NewKey : e.OldKey, 1);
                idx[n++] = i;
            }
        }

        _runLen[run] = n;
    }

    /// <summary>Fills and sorts one run, stably, so equal keys keep the order their events were recorded in.</summary>
    private void SortRun(int run, TEvent[] events, int count)
    {
        var from = Stopwatch.GetTimestamp();
        FillRun(run, events, count);
        var n = _runLen[run];
        if (n > 0 && RadixSort(_runKey[run], _runIdx[run], _runKeyTmp[run], _runIdxTmp[run], n, _sortBits))
        {
            (_runKey[run], _runKeyTmp[run]) = (_runKeyTmp[run], _runKey[run]);
            (_runIdx[run], _runIdxTmp[run]) = (_runIdxTmp[run], _runIdx[run]);
        }

        _runSortedTick[run] = _tick;
        Interlocked.Add(ref SortTicks, Stopwatch.GetTimestamp() - from);
    }

    /// <summary>
    /// A stable LSD radix sort of <paramref name="n"/> entries on the low <paramref name="bits"/> of their keys, in as few passes of at most 11 bits as the
    /// width allows. Returns whether the result is in the temporary buffers. Short runs use an insertion sort, which is also stable.
    /// </summary>
    private static bool RadixSort(ulong[] keys, int[] idx, ulong[] keysTmp, int[] idxTmp, int n, int bits)
    {
        if (n <= 48)
        {
            for (var i = 1; i < n; i++)
            {
                var key = keys[i];
                var id = idx[i];
                var j = i - 1;
                while (j >= 0 && keys[j] > key)
                {
                    keys[j + 1] = keys[j];
                    idx[j + 1] = idx[j];
                    j--;
                }

                keys[j + 1] = key;
                idx[j + 1] = id;
            }

            return false;
        }

        var passes = (bits + 10) / 11;
        var digit = (bits + passes - 1) / passes;
        var buckets = 1 << digit;
        var mask = (ulong)(buckets - 1);
        Span<int> histogram = stackalloc int[buckets];
        var srcK = keys;
        var srcI = idx;
        var dstK = keysTmp;
        var dstI = idxTmp;
        for (var p = 0; p < passes; p++)
        {
            var shift = p * digit;
            histogram.Clear();
            for (var i = 0; i < n; i++)
            {
                histogram[(int)((srcK[i] >> shift) & mask)]++;
            }

            var running = 0;
            for (var b = 0; b < buckets; b++)
            {
                var c = histogram[b];
                histogram[b] = running;
                running += c;
            }

            for (var i = 0; i < n; i++)
            {
                var key = srcK[i];
                var at = histogram[(int)((key >> shift) & mask)]++;
                dstK[at] = key;
                dstI[at] = srcI[i];
            }

            (srcK, dstK) = (dstK, srcK);
            (srcI, dstI) = (dstI, srcI);
        }

        return (passes & 1) != 0;
    }

    /// <summary>
    /// The merge's serial half: the orphans' run, every run not yet sorted (the serial path), and <paramref name="chunks"/> key-range splitters sampled
    /// from the runs, on cell boundaries so a cell's primaries and secondaries land in one chunk.
    /// </summary>
    private void PrepareMerge(int chunks)
    {
        var workers = _events.Length;
        var runs = workers + 1;
        EnsureRuns(runs);

        // The fence's orphans are the last run: leaves like any other, filed under the cell the entity was last described in.
        if (_orphanEvents.Length < _orphanCount)
        {
            _orphanEvents = new TEvent[Math.Max(_orphanCount, _orphanEvents.Length * 2)];
        }

        Array.Copy(_orphans, _orphanEvents, _orphanCount);
        _orphanRunCount = _orphanCount;
        SortRun(workers, _orphanEvents, _orphanCount);
        _orphanCount = 0;

        var total = 0;
        for (var r = 0; r < workers; r++)
        {
            if (_runSortedTick[r] != _tick)
            {
                SortRun(r, _events[r], _eventCount[r]);
            }

            total += _runLen[r];
        }

        total += _runLen[workers];
        IndexEntries = total;
        var events = _orphanRunCount;
        for (var r = 0; r < workers; r++)
        {
            events += _eventCount[r];
        }

        Events += events;
        var slot = _log[_tick % LogDepth];
        if (slot.Events.Length < total)
        {
            slot.Events = new TEvent[Math.Max(total, slot.Events.Length * 2)];
        }

        // A few hundred entries per chunk at least: below that a chunk costs more to dispatch than to merge.
        var k = Math.Clamp(Math.Min(chunks, total / 512), 1, Math.Max(1, chunks));
        if (_split.Length < k + 1)
        {
            _split = new ulong[k + 1];
        }

        _split[0] = 0;
        _split[k] = ulong.MaxValue;
        if (k > 1)
        {
            // About eight samples per chunk, taken from each run in proportion to its length, sorted: their quantiles split the entries into ranges of
            // about equal size, and the serial cost follows the chunk count rather than the worker count.
            var target = 8 * k;
            if (_samples.Length < target + runs)
            {
                _samples = new ulong[target + runs];
            }

            var s = 0;
            for (var r = 0; r < runs; r++)
            {
                var len = _runLen[r];
                var m = len == 0 ? 0 : Math.Max(1, (int)((long)len * target / total));
                for (var j = 0; j < m; j++)
                {
                    _samples[s++] = _runKey[r][(int)((long)len * j / m)] & ~1UL;
                }
            }

            Array.Sort(_samples, 0, s);
            for (var i = 1; i < k; i++)
            {
                _split[i] = Math.Max(_split[i - 1], _samples[(int)((long)s * i / k)]);
            }
        }

        if (_chunkCells.Length < k)
        {
            Array.Resize(ref _chunkCells, k);
            Array.Resize(ref _chunkStarts, k);
            Array.Resize(ref _chunkPrimaryEnds, k);
            Array.Resize(ref _chunkCellCount, k);
            Array.Resize(ref _chunkDeltaKeys, k);
            Array.Resize(ref _chunkDeltas, k);
            Array.Resize(ref _chunkDeltaCount, k);
        }

        if (_mergeKeyA.Length < k)
        {
            Array.Resize(ref _mergeKeyA, k);
            Array.Resize(ref _mergeSrcA, k);
            Array.Resize(ref _mergeKeyB, k);
            Array.Resize(ref _mergeSrcB, k);
        }

        for (var c = 0; c < k; c++)
        {
            _chunkCells[c] ??= new ulong[64];
            _chunkStarts[c] ??= new int[64];
            _chunkPrimaryEnds[c] ??= new int[64];
            _chunkDeltaKeys[c] ??= new ulong[64];
            _chunkDeltas[c] ??= new int[64];
            _chunkCellCount[c] = 0;
            _chunkDeltaCount[c] = 0;
        }

        _mergeChunks = k;
        MaxMergeChunks = Math.Max(MaxMergeChunks, k);
        _mergedTick = _tick;
    }

    private void NoteDelta(int chunk, ulong key, int delta, ref int count)
    {
        if (delta == 0)
        {
            return;
        }

        if (count == _chunkDeltaKeys[chunk].Length)
        {
            Array.Resize(ref _chunkDeltaKeys[chunk], count * 2);
            Array.Resize(ref _chunkDeltas[chunk], count * 2);
        }

        _chunkDeltaKeys[chunk][count] = key;
        _chunkDeltas[chunk][count++] = delta;
    }

    internal override void Recount(ReplicationOccupancy into)
    {
        into.Clear();
        foreach (var a in _pushIndices)
        {
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
                    var slot = BitOperations.TrailingZeroCount(occ);
                    occ &= occ - 1;
                    var hot = (ReplicationHotEntry*)(bytes + layout.HotOffset + (slot * layout.HotStride));
                    if (hot->NetId == NetIdAllocator.NoNetId)
                    {
                        continue;
                    }

                    DecodeAt(a, bytes + layout.ColdOffset + (slot * layout.ColdStride) + _positionOffset[a], out var px, out var py, out var pz);
                    into.Add(CellKey(px, py, pz), 1);
                }
            }
        }
    }

    internal override (int Cells, int Entries) IndexShapeForTest()
    {
        const byte both = PushEvent.HasNew | PushEvent.HasOld;
        var slot = _log[_tick % LogDepth];
        if (!Indexed || slot.Tick != _tick)
        {
            return (-1, -1);
        }

        // The index as a multiset of (identity, cell, secondary), against the same multiset built from the runs' raw events: an event dropped, copied twice
        // or misfiled fails here even when the index is consistent with itself.
        var expected = new Dictionary<(uint, ulong, bool), int>();
        for (var w = 0; w <= _events.Length; w++)
        {
            var list = w < _events.Length ? _events[w] : _orphanEvents;
            var count = w < _events.Length ? _eventCount[w] : _orphanRunCount;
            for (var i = 0; i < count; i++)
            {
                ref var e = ref list[i];
                Bump(expected, (e.NetId, PrimaryKey(ref e), false), 1);
                if ((e.Flags & both) == both && e.OldKey != e.NewKey)
                {
                    Bump(expected, (e.NetId, e.OldKey, true), 1);
                }
            }
        }

        var keys = new HashSet<ulong>();
        var entries = 0;
        for (var k = 0; k < slot.CellCount; k++)
        {
            var cell = slot.Cells[k];
            var unit = TEvent.Unit(cell);
            if ((k > 0 && cell <= slot.Cells[k - 1]) || ((k == 0 || TEvent.Unit(slot.Cells[k - 1]) != unit) && slot.UnitStart(unit) != k))
            {
                return (-1, -1);
            }

            for (var i = slot.Starts[k]; i < slot.PrimaryEnds[k]; i++)
            {
                ref var e = ref slot.Events[i];
                if (PrimaryKey(ref e) != cell)
                {
                    return (-1, -1);
                }

                keys.Add(cell);
                entries++;
                Bump(expected, (e.NetId, cell, false), -1);
                if ((e.Flags & both) == both && e.OldKey != e.NewKey)
                {
                    keys.Add(e.OldKey);
                    entries++;
                }
            }

            for (var i = slot.PrimaryEnds[k]; i < slot.Starts[k + 1]; i++)
            {
                ref var e = ref slot.Events[i];
                if ((e.Flags & both) != both || e.OldKey == e.NewKey || e.OldKey != cell)
                {
                    return (-1, -1);
                }

                Bump(expected, (e.NetId, cell, true), -1);
            }
        }

        foreach (var count in expected.Values)
        {
            if (count != 0)
            {
                return (-1, -1);
            }
        }

        return (keys.Count, entries);

        static void Bump(Dictionary<(uint, ulong, bool), int> map, (uint, ulong, bool) key, int by) =>
            map[key] = map.GetValueOrDefault(key) + by;
    }

    private void EnsureRuns(int runs)
    {
        if (_runKey.Length >= runs)
        {
            return;
        }

        Array.Resize(ref _runKey, runs);
        Array.Resize(ref _runIdx, runs);
        Array.Resize(ref _runKeyTmp, runs);
        Array.Resize(ref _runIdxTmp, runs);
        Array.Resize(ref _runLen, runs);
        Array.Resize(ref _runSortedTick, runs);
        for (var r = 0; r < runs; r++)
        {
            _runSortedTick[r] = uint.MaxValue;
        }
    }

    // Per chunk, the merge's ping-pong buffers: an entry's sort key and its source, (run << 32) | event index.
    private ulong[][] _mergeKeyA = [];
    private long[][] _mergeSrcA = [];
    private ulong[][] _mergeKeyB = [];
    private long[][] _mergeSrcB = [];

    /// <summary>
    /// One key range: each run's slice found by binary search — the chunk's output offset is the count of entries below its range, so no chunk waits for
    /// another — then the slices merged pairwise, adjacent runs first, so the order is (key, run, index) and a function of the runs alone. Every event is
    /// copied into the log slot in that order while the range's cells and their occupancy deltas are listed.
    /// </summary>
    private void MergeChunk(int chunk)
    {
        var workers = _events.Length;
        var runs = workers + 1;
        var lo = _split[chunk];
        var hi = _split[chunk + 1];
        Span<int> from = stackalloc int[runs];
        Span<int> to = stackalloc int[runs];
        var output = 0;
        var total = 0;
        for (var r = 0; r < runs; r++)
        {
            var len = _runLen[r];
            from[r] = chunk == 0 ? 0 : LowerBound(_runKey[r], 0, len, lo);
            to[r] = chunk == _mergeChunks - 1 ? len : LowerBound(_runKey[r], 0, len, hi);
            output += from[r];
            total += to[r] - from[r];
        }

        EnsureMergeBuffers(chunk, total);
        var keys = _mergeKeyA[chunk];
        var srcs = _mergeSrcA[chunk];

        // The slices, concatenated in run order; each is already sorted.
        Span<int> bounds = stackalloc int[runs + 1];
        var at = 0;
        var segments = 0;
        for (var r = 0; r < runs; r++)
        {
            if (from[r] == to[r])
            {
                continue;
            }

            bounds[segments++] = at;
            Array.Copy(_runKey[r], from[r], keys, at, to[r] - from[r]);
            var runIdx = _runIdx[r];
            for (var i = from[r]; i < to[r]; i++)
            {
                srcs[at++] = ((long)r << 32) | (uint)runIdx[i];
            }
        }

        bounds[segments] = at;

        // Adjacent segments merged pairwise until one is left: a linear pass per halving. A tie takes the left, lower-run side first.
        var keysOut = _mergeKeyB[chunk];
        var srcsOut = _mergeSrcB[chunk];
        while (segments > 1)
        {
            var merged = 0;
            for (var sgm = 0; sgm < segments; sgm += 2)
            {
                var a0 = bounds[sgm];
                if (sgm + 1 == segments)
                {
                    var tail = bounds[sgm + 1] - a0;
                    Array.Copy(keys, a0, keysOut, a0, tail);
                    Array.Copy(srcs, a0, srcsOut, a0, tail);
                    bounds[merged++] = a0;
                    continue;
                }

                var a1 = bounds[sgm + 1];
                var b1 = bounds[sgm + 2];
                int i = a0, j = a1, o = a0;
                while (i < a1 && j < b1)
                {
                    if (keys[j] < keys[i])
                    {
                        keysOut[o] = keys[j];
                        srcsOut[o++] = srcs[j++];
                    }
                    else
                    {
                        keysOut[o] = keys[i];
                        srcsOut[o++] = srcs[i++];
                    }
                }

                Array.Copy(keys, i, keysOut, o, a1 - i);
                Array.Copy(srcs, i, srcsOut, o, a1 - i);
                o += a1 - i;
                Array.Copy(keys, j, keysOut, o, b1 - j);
                Array.Copy(srcs, j, srcsOut, o, b1 - j);
                bounds[merged++] = a0;
            }

            bounds[merged] = at;
            segments = merged;
            (keys, keysOut) = (keysOut, keys);
            (srcs, srcsOut) = (srcsOut, srcs);
        }

        var slotEvents = _log[_tick % LogDepth].Events;
        var cells = _chunkCells[chunk];
        var starts = _chunkStarts[chunk];
        var primaryEnds = _chunkPrimaryEnds[chunk];
        var cellCount = 0;
        var current = ulong.MaxValue;
        var primaryEnd = -1;
        var delta = 0;
        var deltaCount = 0;
        const byte both = PushEvent.HasNew | PushEvent.HasOld;
        for (var n = 0; n < at; n++)
        {
            var sortKey = keys[n];
            var src = srcs[n];
            var run = (int)(src >> 32);
            var source = run < workers ? _events[run] : _orphanEvents;
            var cell = sortKey >> 1;
            if (cell != current)
            {
                if (current != ulong.MaxValue)
                {
                    primaryEnds[cellCount - 1] = primaryEnd < 0 ? output : primaryEnd;
                    NoteDelta(chunk, cells[cellCount - 1], delta, ref deltaCount);
                }

                delta = 0;
                if (cellCount == cells.Length)
                {
                    Array.Resize(ref _chunkCells[chunk], cellCount * 2);
                    Array.Resize(ref _chunkStarts[chunk], cellCount * 2);
                    Array.Resize(ref _chunkPrimaryEnds[chunk], cellCount * 2);
                    cells = _chunkCells[chunk];
                    starts = _chunkStarts[chunk];
                    primaryEnds = _chunkPrimaryEnds[chunk];
                }

                cells[cellCount] = KeyOfSortKey(sortKey);
                starts[cellCount] = output;
                cellCount++;
                current = cell;
                primaryEnd = -1;
            }

            ref var e = ref source[(int)(uint)src];
            if ((sortKey & 1) != 0)
            {
                // A secondary: the cell a mover left.
                primaryEnd = primaryEnd < 0 ? output : primaryEnd;
                delta -= OccupancyMutantForTest ? 0 : 1;
            }
            else if ((e.Flags & PushEvent.HasNew) == 0)
            {
                // Leave-only: the entity is gone from the cell it was last described in.
                delta--;
            }
            else if ((e.Flags & both) != both || e.OldKey != e.NewKey)
            {
                // New here: a first description, or a mover that changed cell.
                delta++;
            }

            slotEvents[output++] = e;
        }

        if (current != ulong.MaxValue)
        {
            primaryEnds[cellCount - 1] = primaryEnd < 0 ? output : primaryEnd;
            NoteDelta(chunk, cells[cellCount - 1], delta, ref deltaCount);
        }

        _chunkCellCount[chunk] = cellCount;
        _chunkDeltaCount[chunk] = deltaCount;
    }

    // Called by the chunk that owns the slot; the outer arrays are sized by PrepareMerge, serially, before any chunk runs.
    private void EnsureMergeBuffers(int chunk, int total)
    {
        if (_mergeKeyA[chunk] == null || _mergeKeyA[chunk].Length < total)
        {
            var size = Math.Max(1024, total + (total >> 1));
            _mergeKeyA[chunk] = new ulong[size];
            _mergeSrcA[chunk] = new long[size];
            _mergeKeyB[chunk] = new ulong[size];
            _mergeSrcB[chunk] = new long[size];
        }
    }

    // ══ Per-session gather (parallel over sessions) ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whether a cluster's box proves no entity in it can matter: for a delivery, the box is wholly outside the new sphere; for a sweep, it is wholly
    /// inside both spheres or wholly outside both. The margin — a centimetre plus the archetype's slack — keeps the proof sound against the quantized v̂
    /// the tests use: the box bounds true positions, and v̂ lies up to h_A from them (SUB-20).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SkipCluster(double bx0, double by0, double bz0, double bx1, double by1, double bz1, in Ball a, in Ball n, bool sweeping,
        double margin)
    {
        if (double.IsInfinity(bx0) || double.IsInfinity(bx1) || (TEvent.Deep && (double.IsInfinity(bz0) || double.IsInfinity(bz1))))
        {
            return false;
        }

        var outsideNew = BoxMin2(n, bx0, by0, bz0, bx1, by1, bz1) > (n.R + margin) * (n.R + margin);
        if (!sweeping)
        {
            return outsideNew;
        }

        var outsideOld = BoxMin2(a, bx0, by0, bz0, bx1, by1, bz1) > (a.R + margin) * (a.R + margin);
        if (outsideNew && outsideOld)
        {
            return true;
        }

        // A margin as wide as the radius proves nothing inside: (R − margin)² would be positive again and "prove" a box at the anchor.
        var innerN = n.R - margin;
        var innerA = a.R - margin;
        return innerN > 0 && innerA > 0
               && BoxMax2(n, bx0, by0, bz0, bx1, by1, bz1) <= innerN * innerN
               && BoxMax2(a, bx0, by0, bz0, bx1, by1, bz1) <= innerA * innerA;
    }

    public override bool Gather(SessionId session, bool placed, Vector3D viewpoint, double radius, in LodBands bands, bool forceReset,
        in ArchetypeSet archetypes, FrameWorkerScratch scratch, ArchetypeEncodePlan[] encodePlans, int enterBudget, ref long enters, ref long leaves,
        ref long updates, out bool complete)
    {
        complete = true;
        var from = Stopwatch.GetTimestamp();
        var slotIndex = session.Slot;
        ref var st = ref _sessions[slotIndex];
        if (!st.Bound || st.Generation != session.Generation)
        {
            Bind(ref st, session);
        }

        var reset = st.NeedsReset || forceReset;
        var tick = _tick;

        // The radius this frame moves to — the profile's R′, or the session's SetRadius — and the one the committed known-set was built with (09 § 4,
        // 10 § 5): a change is a shell sweep, not a reset. The window is sized for the largest any session can take.
        Debug.Assert(radius <= Radius, "SetRadius and the profiles bound a session's radius by the window's; the prologue handed a wider one");
        var rNew = radius > 0 && radius <= Radius ? radius : Radius;

        // The budget's last resort (09 § 10): sixteenths of the radius off it, down to half. A shrink is a radius change like SetRadius's — a shell
        // sweep of leaves — and commits with the frame.
        if (st.Shrink > 0)
        {
            rNew *= 1d - (Math.Min((int)st.Shrink, MaxShrink) / (double)ShrinkSteps);
        }

        var rOld = st.Radius > 0 ? st.Radius : rNew;

        // The anchor slack is the session's own: R′ / 48, capped at half a cell so a slack move never skips one (09 § 4).
        var anchorSlack = Math.Min(rNew / 48d, CellSize / 2d);

        // Missed frames: replayed from the push log while every missed tick is still in it, reset otherwise (SUB-03: skip = union).
        var gap = st.Anchored && !reset && placed ? (int)(tick - st.LastTick - 1) : 0;

        // Distance LOD at the session's level (09 § 9–10): the committed level's bands are what every change held back so far was scheduled by — with a
        // wider window for a while after it fell — and the frame moves to the budget loop's target, committed with it. An update to an entity far from the
        // session before and after is sent only on the entity's far flush (BeginFarFold).
        var aBands = bands.AtLevel(st.Level, Widened(in st, tick) ? st.WideLevel : 0);
        var nBands = bands.AtLevel(st.TargetLevel, 0);
        st.PLevel = st.TargetLevel;
        var lod = (aBands.Count > 0 || nBands.Count > 0) && placed;

        // A level moves periods, not boundaries — except a bandless profile's implicit band, which appears at level 1 and goes at 0: only then can an
        // entity be inward without the anchor moving.
        var rebanded = lod && aBands.Count != nBands.Count;
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
            else if (!CollectLog(ref st, slotIndex, viewpoint, rOld, rNew, in archetypes, log))
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

        if (!placed)
        {
            // Nowhere: nothing is described and nothing changes. The pending state is the current one.
            st.PAnchorX = st.AnchorX;
            st.PAnchorY = st.AnchorY;
            st.PAnchorZ = st.AnchorZ;
            st.POriginX = st.OriginX;
            st.POriginY = st.OriginY;
            st.POriginZ = st.OriginZ;
            st.PRadius = st.Radius;
            st.PLevel = st.Level;
            Committed(ref st, slotIndex).CopyTo(Pending(ref st, slotIndex));
            return reset && st.Anchored;
        }

        var vz = TEvent.Deep ? viewpoint.Z : 0d;
        double ax, ay, az;
        int oOriginX, oOriginY, oOriginZ;
        ReadOnlySpan<ushort> o;
        if (reset || !st.Anchored)
        {
            ax = viewpoint.X;
            ay = viewpoint.Y;
            az = vz;
            o = new ReadOnlySpan<ushort>(ZeroRows, 0, _windowRows);
            oOriginX = CellX(ax) - Half;
            oOriginY = CellY(ay) - Half;
            oOriginZ = CellZ(az) - Half;
            reset = st.Anchored || reset;
            rOld = rNew;
        }
        else
        {
            ax = st.AnchorX;
            ay = st.AnchorY;
            az = st.AnchorZ;
            oOriginX = st.OriginX;
            oOriginY = st.OriginY;
            oOriginZ = st.OriginZ;
            o = Committed(ref st, slotIndex);
        }

        // ── The anchor ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        var nx = ax;
        var ny = ay;
        var nz = az;
        var dvx = viewpoint.X - ax;
        var dvy = viewpoint.Y - ay;
        var drift2 = (dvx * dvx) + (dvy * dvy);
        if (TEvent.Deep)
        {
            var dvz = vz - az;
            drift2 += dvz * dvz;
        }

        if (drift2 > anchorSlack * anchorSlack)
        {
            if (drift2 > CellSize * CellSize)
            {
                // A teleport: everything the client holds is wrong. Start over.
                ax = nx = viewpoint.X;
                ay = ny = viewpoint.Y;
                az = nz = vz;
                o = new ReadOnlySpan<ushort>(ZeroRows, 0, _windowRows);
                oOriginX = CellX(ax) - Half;
                oOriginY = CellY(ay) - Half;
                oOriginZ = CellZ(az) - Half;
                reset = true;
                rOld = rNew;
            }
            else
            {
                nx = viewpoint.X;
                ny = viewpoint.Y;
                nz = vz;
            }
        }

        var moved = nx != ax || ny != ay || (TEvent.Deep && nz != az);
        var resized = rOld != rNew;
        var nOriginX = CellX(nx) - Half;
        var nOriginY = CellY(ny) - Half;
        var nOriginZ = CellZ(nz) - Half;
        var aBall = new Ball(ax, ay, az, rOld, in aBands);
        var nBall = new Ball(nx, ny, nz, rNew, in nBands);

        // The new window starts as the old one's cells that it still covers.
        Span<ushort> d = stackalloc ushort[TEvent.Deep ? MaxRows : ReplicationGrid.MaxWindow];
        d = d[.._windowRows];
        ShiftWindow(o, oOriginX, oOriginY, oOriginZ, nOriginX, nOriginY, nOriginZ, d);

        // ── 1. Deliver cells nearest first, under the enter budget ─────────────────────────────────────────────────────────────────────────────────
        // Rings of Chebyshev distance around the anchor's cell, z outside y outside x (10 § 5); a flat grid's z range is {0}, so the order is 2D's.
        Span<ushort> fresh = stackalloc ushort[TEvent.Deep ? MaxRows : ReplicationGrid.MaxWindow];
        fresh = fresh[.._windowRows];
        fresh.Clear();
        var entered = 0;
        var anchorCx = CellX(nx);
        var anchorCy = CellY(ny);
        var anchorCz = CellZ(nz);
        for (var ring = 0; ring <= Half && entered < enterBudget; ring++)
        {
            var zSpan = TEvent.Deep ? ring : 0;
            for (var dz = -zSpan; dz <= zSpan && entered < enterBudget; dz++)
            {
                var cz = anchorCz + dz;
                if (TEvent.Deep && (uint)cz >= (uint)_gridD)
                {
                    continue;
                }

                // On the ring's top and bottom planes every cell is at distance `ring`; between them only the square's border is.
                var plane = (TEvent.Deep ? Math.Abs(dz) : 0) == ring;
                for (var dy = -ring; dy <= ring && entered < enterBudget; dy++)
                {
                    var fullRow = plane || Math.Abs(dy) == ring;
                    var step = fullRow ? 1 : 2 * ring;
                    for (var dx = -ring; dx <= ring && entered < enterBudget; dx += step)
                    {
                        var cx = anchorCx + dx;
                        var cy = anchorCy + dy;
                        if ((uint)cx >= (uint)_gridW || (uint)cy >= (uint)_gridH)
                        {
                            continue;
                        }

                        if (!Local(nOriginX, nOriginY, nOriginZ, cx, cy, cz, out var row, out var bit) || Has(d, row, bit))
                        {
                            continue;
                        }

                        if (CellMin2(nBall, cx, cy, cz) > nBall.R2)
                        {
                            continue;
                        }

                        Set(d, row, bit);
                        Set(fresh, row, bit);
                        entered += DeliverCell(cx, cy, cz, in nBall, in archetypes, scratch, tick, gap);
                        Interlocked.Increment(ref CellsDelivered);
                    }
                }
            }
        }

        // The budget did not bind, so every cell the sphere reaches is delivered: the client holds its whole view.
        complete = entered < enterBudget;
        enters += entered;

        // ── 2. The push events around both anchors ─────────────────────────────────────────────────────────────────────────────────────────────────
        if (gap > 0)
        {
            EmitLog(log, in aBall, oOriginX, oOriginY, oOriginZ, o, in nBall, nOriginX, nOriginY, nOriginZ, d, scratch, ref enters, ref leaves,
                ref updates, lod);
        }

        var minCx = CellX(Math.Min(ax - rOld, nx - rNew));
        var maxCx = CellX(Math.Max(ax + rOld, nx + rNew));
        var minCy = CellY(Math.Min(ay - rOld, ny - rNew));
        var maxCy = CellY(Math.Max(ay + rOld, ny + rNew));
        var minCz = CellZ(Math.Min(az - rOld, nz - rNew));
        var maxCz = CellZ(Math.Max(az + rOld, nz + rNew));
        var index = _log[tick % LogDepth];
        if (gap == 0)
        {
            // Only the occupied cells of the box: one probe per probe unit, then the unit's cells in the box.
            var walk = new BoxWalk(index.Cells, index.CellCount, index, minCx, maxCx, minCy, maxCy, minCz, maxCz);
            while (walk.Next(out var k))
            {
                var cell = index.Cells[k];
                var cx = TEvent.KeyX(cell);
                var cy = TEvent.KeyY(cell);
                var cz = TEvent.KeyZ(cell);
                var b = index.Starts[k];
                var end = index.Starts[k + 1];
                var pe = index.PrimaryEnds[k];

                // A cell wholly inside both spheres and delivered in both windows: every event that neither entered nor left it is an update, with no test.
                var interior = CellMax2(aBall, cx, cy, cz) <= aBall.R2 && CellMax2(nBall, cx, cy, cz) <= nBall.R2
                    && Local(oOriginX, oOriginY, oOriginZ, cx, cy, cz, out var orow, out var obit) && Has(o, orow, obit)
                    && Local(nOriginX, nOriginY, nOriginZ, cx, cy, cz, out var nrow, out var nbit) && Has(d, nrow, nbit);
                for (var i = b; i < end; i++)
                {
                    ref var e = ref _indexed[i];
                    if (!archetypes.Contains(e.Archetype))
                    {
                        continue;
                    }

                    if (interior && i < pe && (e.Flags & (PushEvent.HasOld | PushEvent.HasNew)) == (PushEvent.HasOld | PushEvent.HasNew) && e.OldKey == cell)
                    {
                        EmitUpdateLod(ref e, e.OldX, e.OldY, e.OldZ, e.Groups, (e.Flags & PushEvent.Segment) != 0, e.FlushGroups, e.Flags, in aBall,
                            in nBall, lod, scratch, ref updates);
                        continue;
                    }

                    if (i >= pe)
                    {
                        // A secondary: handled at its primary cell when that cell is in range.
                        var primary = PrimaryKey(ref e);
                        var pcx = TEvent.KeyX(primary);
                        var pcy = TEvent.KeyY(primary);
                        var pcz = TEvent.KeyZ(primary);
                        if (pcx >= minCx && pcx <= maxCx && pcy >= minCy && pcy <= maxCy && (!TEvent.Deep || (pcz >= minCz && pcz <= maxCz)))
                        {
                            continue;
                        }
                    }

                    var was = (e.Flags & PushEvent.HasOld) != 0 && Within(aBall, e.OldX, e.OldY, e.OldZ) && Held(o, oOriginX, oOriginY, oOriginZ, e.OldKey);
                    var isIn = (e.Flags & PushEvent.HasNew) != 0 && Within(nBall, e.NewX, e.NewY, e.NewZ)
                        && Held(d, nOriginX, nOriginY, nOriginZ, e.NewKey);
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
                            EmitUpdateLod(ref e, e.OldX, e.OldY, e.OldZ, e.Groups, (e.Flags & PushEvent.Segment) != 0, e.FlushGroups, e.Flags, in aBall,
                                in nBall, lod, scratch, ref updates);
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

        // ── 3. The crescent (a shell in 3D) the anchor's move or the radius change uncovered or left — or, for the inner one, a band that went ────────
        if (moved || resized || rebanded)
        {
            Interlocked.Increment(ref Sweeps);
            var planes = TEvent.Deep ? Window : 1;
            for (var lz = 0; lz < planes; lz++)
            {
                for (var ly = 0; ly < Window; ly++)
                {
                    var row = (lz * Window) + ly;
                    var bits = (uint)(d[row] & ~fresh[row]);
                    while (bits != 0)
                    {
                        var lx = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        var cx = nOriginX + lx;
                        var cy = nOriginY + ly;
                        var cz = TEvent.Deep ? nOriginZ + lz : 0;
                        if (!InGrid(cx, cy, cz))
                        {
                            continue;
                        }

                        var inBoth = CellMax2(aBall, cx, cy, cz) <= aBall.R2 && CellMax2(nBall, cx, cy, cz) <= nBall.R2;
                        var outBoth = CellMin2(aBall, cx, cy, cz) > aBall.R2 && CellMin2(nBall, cx, cy, cz) > nBall.R2;
                        if (inBoth || outBoth)
                        {
                            continue;
                        }

                        SweepCell(cx, cy, cz, in aBall, in nBall, in archetypes, scratch, tick, gap, ref enters, ref leaves);
                    }
                }
            }

            // Distance LOD: the inner crescent. A held entity the anchor's move brought inward across a band's boundary may have changes it was never
            // sent — they wait for a flush of its old band, which its new band does not share — so it gets them now. One with an event since the last
            // frame is the event's. A cell wholly in the last band now, or wholly near before, holds no such entity.
            for (var lz = 0; lod && lz < planes; lz++)
            {
                for (var ly = 0; ly < Window; ly++)
                {
                    var row = (lz * Window) + ly;
                    var bits = (uint)(d[row] & ~fresh[row]);
                    while (bits != 0)
                    {
                        var lx = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        var cx = nOriginX + lx;
                        var cy = nOriginY + ly;
                        var cz = TEvent.Deep ? nOriginZ + lz : 0;
                        if (!InGrid(cx, cy, cz))
                        {
                            continue;
                        }

                        if (CellMin2(nBall, cx, cy, cz) > nBall.Outer2 || CellMax2(aBall, cx, cy, cz) <= aBall.Inner2)
                        {
                            continue;
                        }

                        FarSweepCell(cx, cy, cz, in aBall, oOriginX, oOriginY, oOriginZ, o, in nBall, in archetypes, scratch, tick, gap, ref updates);
                    }
                }
            }
        }

        // ── 4. Distance LOD: this tick's far flushes of entities whose latest event is older, sent to the sessions that hold them far ─────────────
        // After missed frames the log's replay folded them, with the events.
        if (lod && gap == 0)
        {
            FlushEntries(in aBall, oOriginX, oOriginY, oOriginZ, o, in nBall, nOriginX, nOriginY, nOriginZ, d, in archetypes, scratch, ref updates);
        }

        if (scratch.Deferred != 0)
        {
            Interlocked.Add(ref UpdatesDeferred, scratch.Deferred);
            scratch.Deferred = 0;
        }

        FoldEmptyCells(scratch);

        st.PAnchorX = nx;
        st.PAnchorY = ny;
        st.PAnchorZ = nz;
        st.POriginX = nOriginX;
        st.POriginY = nOriginY;
        st.POriginZ = nOriginZ;
        st.PRadius = rNew;
        d.CopyTo(Pending(ref st, slotIndex));
        Interlocked.Add(ref GatherTicks, Stopwatch.GetTimestamp() - from);
        return reset;
    }

    // ══ World sessions ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>How many occupied cells a World session's delivery may visit in one frame: bounds a frame's cost (10 § 2.4, 1.5.3).</summary>
    private const int WorldCellsPerFrame = 4096;

    public override bool GatherWorld(SessionId session, bool forceReset, in ArchetypeSet archetypes, FrameWorkerScratch scratch, int enterBudget, ref long enters,
        ref long leaves, ref long updates, out bool complete)
    {
        var from = Stopwatch.GetTimestamp();
        ref var st = ref _sessions[session.Slot];
        if (!st.Bound || st.Generation != session.Generation)
        {
            Bind(ref st, session);
        }

        // No LOD for a World session: a session switched from a Sphere at a level goes back to 0 with this frame, and leaves the census.
        st.TargetLevel = 0;
        st.PLevel = 0;

        var tick = _tick;
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
            else if (!CollectLogWorld(st.LastTick + 1, st.Cursor, in archetypes, log))
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
        var oldCursor = reset || !st.Anchored ? 0UL : st.Cursor;

        // ── 1. Deliver occupied cells onward, under the enter budget ──
        // An empty cell holds nobody to enter, so only occupied ones are visited; past the last of them the whole world is delivered.
        var cursor = oldCursor;
        var entered = 0;
        if (cursor != ulong.MaxValue && _worldOrderTick != tick)
        {
            // NoteWorldSession did not foresee this fill. Nothing is delivered and the cursor stays where it is — never "complete" over an order nobody
            // took — and the next prologue sees the fill and takes it.
            Interlocked.Increment(ref WorldOrderMissing);
        }
        else if (cursor != ulong.MaxValue)
        {
            var k = LowerBound(_worldOrder, 0, _worldOrderCount, cursor);
            var visited = 0;
            var nowhere = new Ball(0d, 0d, 0d, 0d);
            for (; k < _worldOrderCount && entered < enterBudget && visited < WorldCellsPerFrame; k++, visited++)
            {
                var key = _worldOrder[k];
                entered += DeliverCell(TEvent.KeyX(key), TEvent.KeyY(key), TEvent.KeyZ(key), in nowhere, in archetypes, scratch, tick, gap,
                    everywhere: true);
                cursor = key + 1;
            }

            if (k == _worldOrderCount)
            {
                cursor = ulong.MaxValue;
            }
        }

        complete = cursor == ulong.MaxValue;
        if (!complete)
        {
            Interlocked.Increment(ref WorldFillFrames);
        }

        enters += entered;

        // ── 2. The events: this tick's, or every missed tick's folded ──
        if (gap > 0)
        {
            for (var i = 0; i < log.Count; i++)
            {
                ref var entry = ref log.Entries[i];
                ref var e = ref entry.Last;
                var was = (entry.FirstFlags & PushEvent.HasOld) != 0 && entry.OldKey < oldCursor;
                var isIn = (e.Flags & PushEvent.HasNew) != 0 && e.NewKey < cursor;
                EmitWorld(ref e, was, isIn, entry.Groups, entry.Segment, scratch, ref enters, ref leaves, ref updates);
            }
        }
        else
        {
            var slot = _log[tick % LogDepth];
            for (var k = 0; slot.Valid && slot.Tick == tick && k < slot.CellCount; k++)
            {
                for (var i = slot.Starts[k]; i < slot.PrimaryEnds[k]; i++)
                {
                    ref var e = ref slot.Events[i];
                    if (!archetypes.Contains(e.Archetype))
                    {
                        continue;
                    }

                    var was = (e.Flags & PushEvent.HasOld) != 0 && e.OldKey < oldCursor;
                    var isIn = (e.Flags & PushEvent.HasNew) != 0 && e.NewKey < cursor;
                    EmitWorld(ref e, was, isIn, e.Groups, (e.Flags & PushEvent.Segment) != 0, scratch, ref enters, ref leaves, ref updates);
                }
            }
        }

        st.PCursor = cursor;
        FoldEmptyCells(scratch);
        Interlocked.Add(ref GatherTicks, Stopwatch.GetTimestamp() - from);
        return flagged;
    }

    // Once per gather, not once per cell: a World frame can skip thousands of cells, and every gather worker shares the counter's line.
    private void FoldEmptyCells(FrameWorkerScratch scratch)
    {
        if (scratch.EmptyCellsSkipped != 0)
        {
            Interlocked.Add(ref EmptyCellsSkipped, scratch.EmptyCellsSkipped);
            scratch.EmptyCellsSkipped = 0;
        }
    }

    private static void EmitWorld(ref TEvent e, bool was, bool isIn, int groups, bool segment, FrameWorkerScratch scratch, ref long enters,
        ref long leaves, ref long updates)
    {
        if (isIn && !was)
        {
            scratch.Add(e.Archetype, FrameListKind.Enter, new FrameRecord { NetId = e.NetId, Block = e.Block, Slot = e.Slot, Archetype = e.Archetype });
            enters++;
        }
        else if (isIn)
        {
            EmitRecord(e.NetId, e.Block, e.Slot, e.Archetype, groups, segment, scratch, ref updates);
        }
        else if (was)
        {
            scratch.Add(e.Archetype, FrameListKind.Leave, new FrameRecord { NetId = e.NetId, Archetype = e.Archetype });
            leaves++;
        }
    }

    /// <summary>The World form of <see cref="CollectLog"/>: every cell, primaries only (each event once per tick).</summary>
    private bool CollectLogWorld(uint first, ulong cursor, in ArchetypeSet archetypes, LogTable table)
    {
        for (var t = first; t != _tick + 1; t++)
        {
            var slot = _log[t % LogDepth];
            for (var k = 0; k < slot.CellCount; k++)
            {
                for (var i = slot.Starts[k]; i < slot.PrimaryEnds[k]; i++)
                {
                    ref var e = ref slot.Events[i];
                    if (!archetypes.Contains(e.Archetype))
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
                        entry.OldZ = e.OldZ;
                        entry.OldKey = e.OldKey;
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
            if (entry.Replaced && (entry.FirstFlags & PushEvent.HasOld) != 0 && entry.OldKey < cursor && (entry.Last.Flags & PushEvent.HasNew) != 0)
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
        public TEvent Last;
        public ulong OldKey;
        public float OldX;
        public float OldY;
        public float OldZ;
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
    /// Folds every event the session missed, in tick order, over the cells either sphere can reach. Returns <see langword="false"/> when an identity it
    /// held was reused inside the window and would be both left and entered in one frame (SUB-06), which only a RESET can say.
    /// </summary>
    private bool CollectLog(ref PushSessionState st, int slotIndex, Vector3D viewpoint, double rOld, double rNew, in ArchetypeSet archetypes, LogTable table)
    {
        var ax = st.AnchorX;
        var ay = st.AnchorY;
        var az = st.AnchorZ;
        var vz = TEvent.Deep ? viewpoint.Z : 0d;
        var minCx = CellX(Math.Min(ax - rOld, viewpoint.X - rNew));
        var maxCx = CellX(Math.Max(ax + rOld, viewpoint.X + rNew));
        var minCy = CellY(Math.Min(ay - rOld, viewpoint.Y - rNew));
        var maxCy = CellY(Math.Max(ay + rOld, viewpoint.Y + rNew));
        var minCz = CellZ(Math.Min(az - rOld, vz - rNew));
        var maxCz = CellZ(Math.Max(az + rOld, vz + rNew));
        for (var t = st.LastTick + 1; t != _tick + 1; t++)
        {
            var slot = _log[t % LogDepth];
            var walk = new BoxWalk(slot.Cells, slot.CellCount, slot, minCx, maxCx, minCy, maxCy, minCz, maxCz);
            while (walk.Next(out var k))
            {
                for (var i = slot.Starts[k]; i < slot.Starts[k + 1]; i++)
                {
                    ref var e = ref slot.Events[i];
                    if (!archetypes.Contains(e.Archetype))
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
                        entry.OldZ = e.OldZ;
                        entry.OldKey = e.OldKey;
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

            // The tick's far flushes of entities whose latest event is older: their position is that event's, and nothing else changed since.
            if (FarPhase > 1)
            {
                var flushes = new BoxWalk(slot.FlushCells, slot.FlushCellCount, null, minCx, maxCx, minCy, maxCy, minCz, maxCz);
                while (flushes.Next(out var k))
                {
                    for (var i = slot.FlushStarts[k]; i < slot.FlushStarts[k + 1]; i++)
                    {
                        ref var f = ref slot.Flush[i];
                        if (!archetypes.Contains(f.Archetype))
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
                            entry.OldZ = f.OldZ;
                            entry.OldKey = f.OldKey;
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
        var committed = Committed(ref st, slotIndex);
        var held = new Ball(ax, ay, az, rOld);
        var reach = (rNew + CellSize) * (rNew + CellSize);
        for (var i = 0; i < table.Count; i++)
        {
            ref var entry = ref table.Entries[i];
            if (entry.Replaced && WasKnown(ref st, committed, in held, ref entry) && (entry.Last.Flags & PushEvent.HasNew) != 0
                && Within(viewpoint.X, viewpoint.Y, vz, entry.Last.NewX, entry.Last.NewY, entry.Last.NewZ, reach))
            {
                return false;
            }
        }

        return true;
    }

    private bool WasKnown(ref PushSessionState st, ReadOnlySpan<ushort> committed, in Ball held, ref LogEntry entry) =>
        (entry.FirstFlags & PushEvent.HasOld) != 0 && Within(held, entry.OldX, entry.OldY, entry.OldZ)
        && Held(committed, st.OriginX, st.OriginY, st.OriginZ, entry.OldKey);

    /// <summary>The folded events against the committed sphere and window (was) and the new ones (is).</summary>
    private void EmitLog(LogTable table, in Ball a, int oOriginX, int oOriginY, int oOriginZ, ReadOnlySpan<ushort> o, in Ball n, int nOriginX, int nOriginY,
        int nOriginZ, ReadOnlySpan<ushort> d, FrameWorkerScratch scratch, ref long enters, ref long leaves, ref long updates, bool lod)
    {
        for (var i = 0; i < table.Count; i++)
        {
            ref var entry = ref table.Entries[i];
            ref var e = ref entry.Last;
            var was = (entry.FirstFlags & PushEvent.HasOld) != 0 && Within(a, entry.OldX, entry.OldY, entry.OldZ)
                && Held(o, oOriginX, oOriginY, oOriginZ, entry.OldKey);
            var isIn = (e.Flags & PushEvent.HasNew) != 0 && Within(n, e.NewX, e.NewY, e.NewZ) && Held(d, nOriginX, nOriginY, nOriginZ, e.NewKey);

            // Only far flushes and no event: the entity neither moved nor changed since, and a flush entry stamps nothing — so an enter or a leave is the cell
            // delivery's or the sweep's, and a crossing inward — between any two bands — is the inner crescent's. It speaks only to a session holding it in a
            // band before and after, not nearer now.
            if (!entry.Real)
            {
                var bandBefore = a.BandOf(entry.OldX, entry.OldY, entry.OldZ);
                if (lod && was && isIn && bandBefore > 0 && n.BandOf(e.NewX, e.NewY, e.NewZ) >= bandBefore)
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
                EmitUpdateLod(ref e, entry.OldX, entry.OldY, entry.OldZ, entry.Groups, entry.Segment, entry.FlushGroups, flushFlags, in a, in n, lod,
                    scratch, ref updates, live: false);
            }
            else if (was)
            {
                scratch.Add(e.Archetype, FrameListKind.Leave, new FrameRecord { NetId = e.NetId, Archetype = e.Archetype });
                leaves++;
            }
        }
    }

    /// <summary>
    /// An update to an entity the session holds before and after (09 § 9): sent; withheld (in a band before, not inward since, and not its flush tick for
    /// that band); its flush for the band — the groups stamped in the band's last N ticks; or widened to the whole state (it came inward across a boundary,
    /// so changes withheld in its old band may be missing). Of <c>flags</c> only <see cref="PushEvent.FarFlush"/> and <see cref="PushEvent.FlushSegment"/>
    /// are read. <paramref name="live"/> is this tick's event; a catch-up's union has no single flush tick, so it sends every flushed group.
    /// </summary>
    private void EmitUpdateLod(ref TEvent e, float oldX, float oldY, float oldZ, int groups, bool segment, int flushGroups, byte flags, in Ball a, in Ball n,
        bool lod, FrameWorkerScratch scratch, ref long updates, bool live = true)
    {
        if (lod)
        {
            var bandNow = n.BandOf(e.NewX, e.NewY, e.NewZ);
            var bandBefore = a.BandOf(oldX, oldY, oldZ);
            if (bandNow < bandBefore)
            {
                var plan = _encodePlans[e.Archetype];
                groups = (1 << plan.GroupCount) - 1;
                segment = plan.Moving;
            }
            else if (bandBefore > 0)
            {
                // Outward or in place: the old band's period, the more frequent of the two, is the schedule every change so far was held to; its window is
                // the history the flush carries.
                var every = a.EveryOf(bandBefore);
                if ((flags & PushEvent.FarFlush) == 0 || (live && ((e.NetId % (uint)every) + (_tick % (uint)every)) % (uint)every != 0))
                {
                    scratch.Deferred++;
                    return;
                }

                groups = flushGroups;
                segment = (flags & PushEvent.FlushSegment) != 0;
                if (live)
                {
                    FlushSince(ref e, WindowFloor(_tick, a.WindowOf(bandBefore)), ref groups, ref segment);

                    // Nothing stamped in the window — an arrival that changed no byte, flagged by an older change: there is no record to send.
                    if (groups == 0 && !segment)
                    {
                        return;
                    }
                }
            }
        }

        EmitRecord(e.NetId, e.Block, e.Slot, e.Archetype, groups, segment, scratch, ref updates);
    }

    /// <summary>The tick a window of <paramref name="ticks"/> ending at <paramref name="tick"/> starts after, wrap-safe: 0 in the run's first ticks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint WindowFloor(uint tick, int ticks) => tick > (uint)ticks ? tick - (uint)ticks : 0u;

    /// <summary>
    /// A flush's groups narrowed to a band's period: the fold computed them over the widest band's window, and a band of period N sends only what was
    /// stamped in its last N ticks — the rest went out in its previous flushes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FlushSince(ref TEvent e, uint since, ref int groups, ref bool segment)
    {
        if (e.Block == 0 || FarWindow <= 1)
        {
            return;
        }

        var plan = _encodePlans[e.Archetype];
        var layout = _states[e.Archetype].Layout;
        var hot = (ReplicationHotEntry*)((byte*)e.Block + layout.HotOffset + (e.Slot * layout.HotStride));
        for (var g = 0; g < plan.GroupCount; g++)
        {
            if ((groups & (1 << g)) != 0 && hot->GroupTicks[plan.GroupTickSlot[g]] <= since)
            {
                groups &= ~(1 << g);
            }
        }

        segment &= plan.Moving && hot->GroupTicks[plan.MotionTickSlot] > since;
    }

    /// <summary>Distance LOD: this tick's flush entries in the sphere, to a session that held the entity before this frame and holds it in a band now.</summary>
    private void FlushEntries(in Ball a, int oOriginX, int oOriginY, int oOriginZ, ReadOnlySpan<ushort> o, in Ball n, int nOriginX, int nOriginY,
        int nOriginZ, ReadOnlySpan<ushort> d, in ArchetypeSet archetypes, FrameWorkerScratch scratch, ref long updates)
    {
        var slot = _log[_tick % LogDepth];
        if (slot.FlushCellCount == 0 || slot.Tick != _tick)
        {
            return;
        }

        var walk = new BoxWalk(slot.FlushCells, slot.FlushCellCount, null, CellX(n.X - n.R), CellX(n.X + n.R), CellY(n.Y - n.R), CellY(n.Y + n.R),
            CellZ(n.Z - n.R), CellZ(n.Z + n.R));
        while (walk.Next(out var k))
        {
            var cell = slot.FlushCells[k];
            var cx = TEvent.KeyX(cell);
            var cy = TEvent.KeyY(cell);
            var cz = TEvent.KeyZ(cell);
            if (CellMax2(n, cx, cy, cz) <= n.Inner2 || CellMin2(n, cx, cy, cz) > n.R2)
            {
                continue;
            }

            for (var i = slot.FlushStarts[k]; i < slot.FlushStarts[k + 1]; i++)
            {
                ref var f = ref slot.Flush[i];
                if (!archetypes.Contains(f.Archetype) || !Within(n, f.NewX, f.NewY, f.NewZ) || !Held(d, nOriginX, nOriginY, nOriginZ, f.NewKey)
                    || !Within(a, f.NewX, f.NewY, f.NewZ) || !Held(o, oOriginX, oOriginY, oOriginZ, f.NewKey))
                {
                    continue;
                }

                // The entity did not move; the anchor may have. Inward is the inner crescent's; otherwise the old band's period schedules the flush — the
                // first band's for an entity that was near, which got everything until now.
                // A session with no band before — its level just rose from none — was sent every change: nothing is held back.
                var bandNow = n.BandOf(f.NewX, f.NewY, f.NewZ);
                var bandBefore = a.BandOf(f.NewX, f.NewY, f.NewZ);
                if (bandNow == 0 || bandNow < bandBefore || a.BandCount == 0)
                {
                    continue;
                }

                var band = Math.Max(bandBefore, 1);
                var every = a.EveryOf(band);
                if (((f.NetId % (uint)every) + (_tick % (uint)every)) % (uint)every != 0)
                {
                    continue;
                }

                var groups = (int)f.FlushGroups;
                var segment = (f.Flags & PushEvent.FlushSegment) != 0;
                FlushSince(ref f, WindowFloor(_tick, a.WindowOf(band)), ref groups, ref segment);
                if (groups != 0 || segment)
                {
                    EmitRecord(f.NetId, f.Block, f.Slot, f.Archetype, groups, segment, scratch, ref updates);
                }
            }
        }
    }

    /// <summary>
    /// Distance LOD: a cell of the inner crescent — the held entities the anchor's move brought inward across a band's boundary, with no event since the
    /// session's last frame, get what changed in their old band's last N ticks: older changes went out in that band's flushes, and newer ones wait for one
    /// the new band does not share.
    /// </summary>
    private void FarSweepCell(int cx, int cy, int cz, in Ball a, int oOriginX, int oOriginY, int oOriginZ, ReadOnlySpan<ushort> o, in Ball n,
        in ArchetypeSet archetypes, FrameWorkerScratch scratch, uint tick, int gap, ref long updates)
    {
        if (!Local(oOriginX, oOriginY, oOriginZ, cx, cy, cz, out var row, out var bit) || !Has(o, row, bit) || _occupancy.Get(TEvent.Key(cx, cy, cz)) == 0)
        {
            return;
        }

        // A box wholly in the last band now, or wholly near before, holds nobody who came inward.
        var nearN = Math.Sqrt(n.Outer2);
        var nearA = Math.Sqrt(a.Inner2);
        foreach (var arch in _pushIndices)
        {
            if (!archetypes.Contains(arch))
            {
                continue;
            }

            var state = _states[arch];
            var cs = state.ClusterState;
            if (cs == null || (TEvent.Deep && !_hasZ[arch] && cz != _zeroCz))
            {
                continue;
            }

            var plan = _encodePlans[arch];
            var layout = state.Layout;
            var hasZ = TEvent.Deep && _hasZ[arch];

            // The box is built from raw positions and the test below from decoded ones: a margin of a quantization step keeps the pruning sound.
            var margin = _pruneMargin[arch];
            double bz0 = 0, bz1 = 0;
            QueryBox(cx, cy, cz, hasZ, _queryPad[arch], out var qx0, out var qy0, out var qz0, out var qx1, out var qy1, out var qz1);
            using var e = cs.QueryAabb(cs.Grid, qx0, qy0, qz0, qx1, qy1, qz1);
            while (hasZ
                       ? e.MoveNextClusterUnopened(out var chunkId, out var bx0, out var by0, out bz0, out var bx1, out var by1, out bz1)
                       : e.MoveNextClusterUnopened(out chunkId, out bx0, out by0, out bx1, out by1))
            {
                ClampToWorld(ref bx0, ref by0, ref bz0, ref bx1, ref by1, ref bz1, hasZ);

                // A box wholly past the outermost boundary of the new anchor, or wholly within the innermost of the old one, holds nobody who came inward.
                if (!double.IsInfinity(bx0) && !double.IsInfinity(bx1) && (!hasZ || (!double.IsInfinity(bz0) && !double.IsInfinity(bz1)))
                    && (BoxMin2(n, bx0, by0, bz0, bx1, by1, bz1) > (nearN + margin) * (nearN + margin)
                        || (nearA > margin && BoxMax2(a, bx0, by0, bz0, bx1, by1, bz1) <= (nearA - margin) * (nearA - margin))))
                {
                    continue;
                }

                var block = BlockOf(arch, chunkId);
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

                    DecodeAt(arch, cold + _positionOffset[arch], out var px, out var py, out var pz);
                    if (CellX(px) != cx || CellY(py) != cy || (TEvent.Deep && CellZ(pz) != cz) || !Within(a, px, py, pz))
                    {
                        continue;
                    }

                    var bandBefore = a.BandOf(px, py, pz);
                    if (n.BandOf(px, py, pz) >= bandBefore)
                    {
                        continue;
                    }

                    // From the last frame the session received, not from this tick: after missed frames the catch-up leaves an inward entity's flushes of
                    // the gap to this sweep, so what the old band held back reaches back to its window before the gap.
                    var lo = WindowFloor(tick, a.WindowOf(bandBefore) + gap);

                    var groups = 0;
                    for (var g = 0; g < plan.GroupCount; g++)
                    {
                        if (hot->GroupTicks[plan.GroupTickSlot[g]] > lo)
                        {
                            groups |= 1 << g;
                        }
                    }

                    Interlocked.Increment(ref FarCrescentStates);
                    EmitRecord(hot->NetId, (nint)block, (byte)slot, (ushort)arch, groups, plan.Moving && hot->GroupTicks[plan.MotionTickSlot] > lo,
                        scratch, ref updates);
                }
            }
        }
    }

    // ══ Distance LOD: the far flushes (parallel stage after the index) ═══════════════════════════════════════════════════════════════════════════════

    public override bool FarFolded => _farFoldTick == _tick;

    /// <remarks>
    /// An entity's far flush is its phase tick, <c>(netId + tick) % N == 0</c>, and carries the groups it changed in the last N ticks — read from the
    /// group stamps of its hot entry, which every change sets and which the previous phase flush, N ticks ago, covered up to. The candidates are the
    /// primaries of the last N log slots; the one that is the entity's latest event (its cold stamp names that tick) speaks for it. A change always makes
    /// an event, so an entity with anything to flush is always among them.
    /// </remarks>
    public override int BeginFarFold(int workers)
    {
        ResolveFar();
        if (FarPhase <= 1 || !Indexed || _farFoldTick == _tick)
        {
            return 0;
        }

        var k = Math.Max(1, Math.Min(workers, _log[_tick % LogDepth].CellCount));
        if (_farBounds.Length < k + 1)
        {
            _farBounds = new ulong[k + 1];
        }

        // Cell ranges of about equal event counts, from this tick's compact index: a population is clustered, so equal cell counts are not equal work.
        var slot0 = _log[_tick % LogDepth];
        var totalEvents = slot0.CellCount == 0 ? 0 : slot0.Starts[slot0.CellCount];
        _farBounds[0] = 0;
        var at = 0;
        for (var i = 1; i < k; i++)
        {
            var target = (int)((long)totalEvents * i / k);
            while (at < slot0.CellCount && slot0.Starts[at] < target)
            {
                at++;
            }

            _farBounds[i] = at < slot0.CellCount ? Math.Max(_farBounds[i - 1], slot0.Cells[at]) : ulong.MaxValue;
        }

        _farBounds[k] = ulong.MaxValue;
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
            _farOut[i] ??= new TEvent[256];
            _farOutCells[i] ??= new ulong[64];
            _farOutStarts[i] ??= new int[65];
            _farOutCount[i] = 0;
            _farOutCellCount[i] = 0;
            _farOutFlagged[i] = 0;
        }

        _farChunkCount = k;
        _farFoldTick = _tick;
        return k;
    }

    /// <remarks>A chunk owns its cells' events, which is what lets it flag this tick's in place.</remarks>
    public override void FoldFarChunk(int chunk)
    {
        if ((uint)chunk >= (uint)_farChunkCount)
        {
            return;
        }

        var c0 = _farBounds[chunk];
        var c1 = _farBounds[chunk + 1];

        // The smallest period flags candidates — every band's flush ticks are among its own — and the largest is the history a flush must cover.
        var p = FarPhase;
        var n = FarWindow;
        var t = _tick;

        // The window's floor, wrap-safe for the run's first ticks; and the phase, wrap-safe for a tick counter or net id past 2^32.
        var lo = t > (uint)n ? t - (uint)n : 0u;
        var tickPhase = t % (uint)p;
        Span<int> cursor = stackalloc int[LogDepth];
        for (var age = 0; age < n; age++)
        {
            var slot = _log[(t - (uint)age) % LogDepth];
            cursor[age] = slot.Valid && slot.Tick == t - (uint)age ? LowerBound(slot.Cells, 0, slot.CellCount, c0) : int.MaxValue;
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
            var cell = ulong.MaxValue;
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
                    if ((e.Flags & PushEvent.HasNew) == 0 || ((e.NetId % (uint)p) + tickPhase) % (uint)p != 0)
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
                    f.OldZ = e.NewZ;
                    f.OldKey = e.NewKey;
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

    private protected override void EndFarFoldCore()
    {
        if (_farEndTick == _tick)
        {
            return;
        }

        ResolveFar();

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
            slot.Flush = new TEvent[Math.Max(total, slot.Flush.Length * 2)];
        }

        if (slot.FlushCells.Length < totalCells)
        {
            slot.FlushCells = new ulong[Math.Max(totalCells, slot.FlushCells.Length * 2)];
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

    /// <summary>How far an edge cell's query reaches past the world on X and Y: 10⁹ km, beyond anything a position can mean.</summary>
    private const double OpenEdgeM = 1e12;

    /// <summary>
    /// A cell's cluster query box: the cell and a metre around it, open to infinity on any side where the cell is the grid's last — an entity beyond the
    /// world's bounds decodes into that cell, and a box that stopped at the edge would never find it. A flat grid's, and a 2D archetype's, Z is unbounded.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void QueryBox(int cx, int cy, int cz, bool hasZ, double pad, out double x0, out double y0, out double z0, out double x1, out double y1,
        out double z1)
    {
        var cellX = _gridMinX + (cx * CellSize);
        var cellY = _gridMinY + (cy * CellSize);
        // A far finite bound, not an infinity: the spatial query takes an unbounded Z ("every Z") but refuses a non-finite X or Y, and widens the box by
        // a step below its low side, which would take -double.MaxValue to -Infinity.
        x0 = cx == 0 ? -OpenEdgeM : cellX - pad;
        x1 = cx == _gridW - 1 ? OpenEdgeM : cellX + CellSize + pad;
        y0 = cy == 0 ? -OpenEdgeM : cellY - pad;
        y1 = cy == _gridH - 1 ? OpenEdgeM : cellY + CellSize + pad;
        z0 = double.NegativeInfinity;
        z1 = double.PositiveInfinity;
        if (TEvent.Deep && hasZ)
        {
            var cellZ = _gridMinZ + (cz * CellSize);
            z0 = cz == 0 ? double.NegativeInfinity : cellZ - pad;
            z1 = cz == _gridD - 1 ? double.PositiveInfinity : cellZ + CellSize + pad;
        }
    }

    /// <summary>
    /// A cluster's raw bounds brought into the world: every position is quantized, and so decoded, inside it, so a cluster lying beyond an edge holds
    /// entities the tests see on that edge. Pruning on the raw box would prove them out of reach when their decoded positions are in it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClampToWorld(ref double x0, ref double y0, ref double z0, ref double x1, ref double y1, ref double z1, bool hasZ)
    {
        x0 = Math.Clamp(x0, _gridMinX, _worldMaxX);
        x1 = Math.Clamp(x1, _gridMinX, _worldMaxX);
        y0 = Math.Clamp(y0, _gridMinY, _worldMaxY);
        y1 = Math.Clamp(y1, _gridMinY, _worldMaxY);
        if (TEvent.Deep && hasZ)
        {
            z0 = Math.Clamp(z0, _gridMinZ, _worldMaxZ);
            z1 = Math.Clamp(z1, _gridMinZ, _worldMaxZ);
        }
    }

    // ══ Records, cell delivery and sweep ════════════════════════════════════════════════════════════════════════════════════════════════════════════

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

    /// <summary>Enters every entity of the cell inside the sphere that was not pushed this tick (a pushed one is the push step's).</summary>
    private int DeliverCell(int cx, int cy, int cz, in Ball n, in ArchetypeSet archetypes, FrameWorkerScratch scratch, uint tick, int gap, bool everywhere = false)
    {
        // Nobody's last pushed position is in the cell: its cluster query could find nothing to enter.
        if (_occupancy.Get(TEvent.Key(cx, cy, cz)) == 0)
        {
            scratch.EmptyCellsSkipped++;
            return 0;
        }

        var from = FrameAssembler.PhaseTimingEnabled ? Stopwatch.GetTimestamp() : 0L;
        var decoded = 0;
        var entered = 0;
        const bool sweeping = false;
        foreach (var a in _pushIndices)
        {
            if (!archetypes.Contains(a))
            {
                continue;
            }

            var state = _states[a];
            var cs = state.ClusterState;
            if (cs == null || (TEvent.Deep && !_hasZ[a] && cz != _zeroCz))
            {
                continue;
            }

            var layout = state.Layout;
            var hasZ = TEvent.Deep && _hasZ[a];
            double bz0 = 0, bz1 = 0;
            QueryBox(cx, cy, cz, hasZ, _queryPad[a], out var qx0, out var qy0, out var qz0, out var qx1, out var qy1, out var qz1);
            using var e = cs.QueryAabb(cs.Grid, qx0, qy0, qz0, qx1, qy1, qz1);
            while (hasZ
                       ? e.MoveNextClusterUnopened(out var chunkId, out var bx0, out var by0, out bz0, out var bx1, out var by1, out bz1)
                       : e.MoveNextClusterUnopened(out chunkId, out bx0, out by0, out bx1, out by1))
            {
                ClampToWorld(ref bx0, ref by0, ref bz0, ref bx1, ref by1, ref bz1, hasZ);
                if (!everywhere && SkipCluster(bx0, by0, bz0, bx1, by1, bz1, in n, in n, sweeping, _skipMargin[a]))
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

                    decoded++;
                    DecodeAt(a, cold + _positionOffset[a], out var px, out var py, out var pz);
                    if (CellX(px) != cx || CellY(py) != cy || (TEvent.Deep && CellZ(pz) != cz) || (!everywhere && !Within(n, px, py, pz)))
                    {
                        continue;
                    }

                    scratch.Add(a, FrameListKind.Enter, new FrameRecord { NetId = hot->NetId, Block = (nint)block, Slot = (byte)slot, Archetype = (ushort)a });
                    entered++;
                }
            }
        }

        if (from != 0L)
        {
            Interlocked.Add(ref DeliverTicks, Stopwatch.GetTimestamp() - from);
            Interlocked.Add(ref DeliverDecoded, decoded);
            Interlocked.Add(ref DeliverEntered, entered);
        }

        return entered;
    }

    /// <summary>Emits the enters and leaves the anchor's move caused among one delivered cell's entities that were not pushed this tick.</summary>
    private void SweepCell(int cx, int cy, int cz, in Ball a, in Ball n, in ArchetypeSet archetypes, FrameWorkerScratch scratch, uint tick, int gap, ref long enters,
        ref long leaves)
    {
        if (_occupancy.Get(TEvent.Key(cx, cy, cz)) == 0)
        {
            scratch.EmptyCellsSkipped++;
            return;
        }

        var from = FrameAssembler.PhaseTimingEnabled ? Stopwatch.GetTimestamp() : 0L;
        var decoded = 0;
        var visited = 0;
        const bool sweeping = true;
        foreach (var arch in _pushIndices)
        {
            if (!archetypes.Contains(arch))
            {
                continue;
            }

            var state = _states[arch];
            var cs = state.ClusterState;
            if (cs == null || (TEvent.Deep && !_hasZ[arch] && cz != _zeroCz))
            {
                continue;
            }

            var layout = state.Layout;
            var hasZ = TEvent.Deep && _hasZ[arch];
            double bz0 = 0, bz1 = 0;
            QueryBox(cx, cy, cz, hasZ, _queryPad[arch], out var qx0, out var qy0, out var qz0, out var qx1, out var qy1, out var qz1);
            using var e = cs.QueryAabb(cs.Grid, qx0, qy0, qz0, qx1, qy1, qz1);
            while (hasZ
                       ? e.MoveNextClusterUnopened(out var chunkId, out var bx0, out var by0, out bz0, out var bx1, out var by1, out bz1)
                       : e.MoveNextClusterUnopened(out chunkId, out bx0, out by0, out bx1, out by1))
            {
                ClampToWorld(ref bx0, ref by0, ref bz0, ref bx1, ref by1, ref bz1, hasZ);
                if (SkipCluster(bx0, by0, bz0, bx1, by1, bz1, in a, in n, sweeping, _skipMargin[arch]))
                {
                    continue;
                }

                var block = BlockOf(arch, chunkId);
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

                    decoded++;
                    DecodeAt(arch, cold + _positionOffset[arch], out var px, out var py, out var pz);
                    if (CellX(px) != cx || CellY(py) != cy || (TEvent.Deep && CellZ(pz) != cz))
                    {
                        continue;
                    }

                    visited++;
                    var was = Within(a, px, py, pz);
                    var isIn = Within(n, px, py, pz);
                    if (isIn && !was)
                    {
                        scratch.Add(arch, FrameListKind.Enter,
                            new FrameRecord { NetId = hot->NetId, Block = (nint)block, Slot = (byte)slot, Archetype = (ushort)arch });
                        enters++;
                    }
                    else if (was && !isIn)
                    {
                        scratch.Add(arch, FrameListKind.Leave, new FrameRecord { NetId = hot->NetId, Archetype = (ushort)arch });
                        leaves++;
                    }
                }
            }
        }

        Interlocked.Add(ref SweepSlots, visited);
        if (from != 0L)
        {
            Interlocked.Add(ref SweepDecoded, decoded);
            Interlocked.Add(ref SweepTicks, Stopwatch.GetTimestamp() - from);
        }
    }

    // ══ Shadow oracle ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    public override void ShadowCheck(SessionId session, in ArchetypeSet archetypes)
    {
        var slotIndex = session.Slot;
        ref var st = ref _sessions[slotIndex];
        var set = _shadow[slotIndex];
        if (set == null || _shadowGen[slotIndex] != session.Generation || !st.Anchored || st.NeedsReset)
        {
            return;
        }

        var held = new Ball(st.AnchorX, st.AnchorY, st.AnchorZ, st.Radius > 0 ? st.Radius : Radius);
        var rows = Committed(ref st, slotIndex);
        var expected = 0;
        var missing = 0L;
        var where = new Dictionary<uint, (float X, float Y, float Z, bool Delivered)>();
        foreach (var a in _pushIndices)
        {
            if (!archetypes.Contains(a))
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

                    DecodeAt(a, bytes + layout.ColdOffset + (s * layout.ColdStride) + _positionOffset[a], out var px, out var py, out var pz);
                    var delivered = Held(rows, st.OriginX, st.OriginY, st.OriginZ, CellKey(px, py, pz));
                    where[hot->NetId] = (px, py, pz, delivered);
                    var known = Within(held, px, py, pz) && delivered;
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
                    ClassifyGone(id, in archetypes);
                }
                else if (!Within(held, w.X, w.Y, w.Z))
                {
                    ExtraOutside++;
                    var dx = w.X - held.X;
                    var dy = w.Y - held.Y;
                    var dz = TEvent.Deep ? w.Z - held.Z : 0d;
                    ExtraOutsideDistSum += Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) - held.R;
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
}
