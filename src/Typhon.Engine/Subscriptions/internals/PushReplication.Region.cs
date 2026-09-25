using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// A ClientRegion session's geometry (09 § 7), beside its <see cref="PushSessionState"/>: the committed hull and the delivered window, and the pending ones
/// the gather computed, which become the committed ones only when the frame is published (SUB-03).
/// </summary>
/// <remarks>
/// A window is <c>W</c> rows of <c>W</c> cells in a flat grid and <c>W²</c> rows in a deep one, one <see cref="ulong"/> each: a region window is up to 53
/// cells wide (<c>⌈maxEdgeM / c⌉ + 5</c>, 09 § 7), past the Sphere window's 16-bit rows.
/// </remarks>
internal sealed class RegionSession
{
    public ushort Generation;

    /// <summary>The tick of the last region gather: <see cref="PushReplication.Commit"/> commits the pending geometry only when it is this tick's.</summary>
    public uint PendingTick = uint.MaxValue;

    /// <summary>Whether the committed geometry is a region's: false until the first region frame is published, and after a frame of another shape.</summary>
    public bool Anchored;

    /// <summary>The committed hull; <see cref="ClientRegionCommand.PlaneCount"/> zero for none, which contains nothing.</summary>
    public ClientRegionCommand Hull;

    public ClientRegionCommand PHull;

    public int OriginX;
    public int OriginY;
    public int OriginZ;
    public int POriginX;
    public int POriginY;
    public int POriginZ;

    /// <summary>The committed delivered window; <see cref="P"/> the pending one.</summary>
    public ulong[] D;

    public ulong[] P;

    /// <summary>The pending hull's cell box, clipped to the window.</summary>
    public int PMinCx;
    public int PMaxCx;
    public int PMinCy;
    public int PMaxCy;
    public int PMinCz;
    public int PMaxCz;

    /// <summary>Whether every cell the hull meets and the near budget admits was delivered, as of the committed frame.</summary>
    public bool Complete;

    public bool PComplete;

    /// <summary>The near budget (09 § 7): the counted entities of the delivered cells, an upper bound on what the session holds.</summary>
    public int Held;

    public int PHeld;

    /// <summary>The near budget's deadband: the tick the estimate fell under 0.9 × budget and has stayed there since; 0 while it is not under.</summary>
    public uint UnderSince;

    public uint PUnderSince;
}

internal sealed unsafe partial class PushReplication<TEvent>
{
    /// <summary>The most rows a region window has: 53 in a flat grid (53² ≤ 2 809), 14² in a deep one (14³ ≤ 2 809).</summary>
    private const int MaxRegionRows = 196;

    /// <summary>The most cells a region window covers: the window bound (10 § 4.3).</summary>
    private const int MaxRegionCells = ReplicationGrid.MaxWindowCells;

    /// <summary>A cell box is classified against a hull padded by this much, so float rounding at a plane cannot prove a point in or out wrongly.</summary>
    private const double ClassifyPadM = 0.01;

    private int RegionRows => TEvent.Deep ? RegionWindow * RegionWindow : RegionWindow;

    /// <summary>The slot's region geometry for this session, created at its first region gather and emptied for a new session of the slot.</summary>
    private RegionSession RegionFor(SessionId session)
    {
        var r = _regions[session.Slot];
        if (r == null)
        {
            r = new RegionSession { D = new ulong[RegionRows], P = new ulong[RegionRows], Generation = session.Generation };
            _regions[session.Slot] = r;
            return r;
        }

        if (r.Generation != session.Generation)
        {
            r.Generation = session.Generation;
            r.Anchored = false;
            r.PendingTick = uint.MaxValue;
            r.Hull.PlaneCount = 0;
            r.PHull.PlaneCount = 0;
            Array.Clear(r.D);
            Array.Clear(r.P);
            r.Complete = r.PComplete = false;
            r.Held = r.PHeld = 0;
            r.UnderSince = r.PUnderSince = 0;
        }

        return r;
    }

    /// <summary>The slot's region geometry when this session was last gathered as a region this tick — the only sessions a region test applies to.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RegionSession RegionGatheredNow(SessionId session)
    {
        if (_regions.Length == 0)
        {
            return null;
        }

        var r = _regions[session.Slot];
        return r != null && r.Generation == session.Generation && r.PendingTick == _tick ? r : null;
    }

    /// <summary>Whether the committed geometry of the session's slot is a region's (a session that changes shape resets, 09 § 7).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CommittedAsRegion(SessionId session)
    {
        if (_regions.Length == 0)
        {
            return false;
        }

        var r = _regions[session.Slot];
        return r != null && r.Generation == session.Generation && r.Anchored;
    }

    // ══ Region windows ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RegionLocal(int ox, int oy, int oz, int cx, int cy, int cz, out int row, out int bit)
    {
        var w = RegionWindow;
        var lx = cx - ox;
        var ly = cy - oy;
        bit = lx;
        if (TEvent.Deep)
        {
            var lz = cz - oz;
            row = (lz * w) + ly;
            return (uint)lx < (uint)w && (uint)ly < (uint)w && (uint)lz < (uint)w;
        }

        row = ly;
        return (uint)lx < (uint)w && (uint)ly < (uint)w;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RegionHeld(ReadOnlySpan<ulong> rows, int ox, int oy, int oz, ulong key) =>
        RegionLocal(ox, oy, oz, TEvent.KeyX(key), TEvent.KeyY(key), TEvent.KeyZ(key), out var row, out var bit) && ((rows[row] >> bit) & 1UL) != 0;

    /// <summary>The old window's cells the new one still covers, into <paramref name="into"/> (cleared first).</summary>
    private void ShiftRegion(ReadOnlySpan<ulong> old, int oox, int ooy, int ooz, int nox, int noy, int noz, Span<ulong> into)
    {
        into.Clear();
        var w = RegionWindow;
        var dx = nox - oox;
        var dy = noy - ooy;
        var dz = TEvent.Deep ? noz - ooz : 0;
        if (dx <= -w || dx >= w || dy <= -w || dy >= w || dz <= -w || dz >= w)
        {
            return;
        }

        var mask = w == 64 ? ulong.MaxValue : (1UL << w) - 1;
        var planes = TEvent.Deep ? w : 1;
        for (var lz = 0; lz < planes; lz++)
        {
            var olz = lz + dz;
            if ((uint)olz >= (uint)planes)
            {
                continue;
            }

            for (var ly = 0; ly < w; ly++)
            {
                var oly = ly + dy;
                if ((uint)oly >= (uint)w)
                {
                    continue;
                }

                var row = old[(olz * w) + oly];
                into[(lz * w) + ly] = (dx >= 0 ? row >> dx : row << -dx) & mask;
            }
        }
    }

    /// <summary>How a cell lies against a hull, the cell padded by <see cref="ClassifyPadM"/>; a flat grid's cells lie on z = 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RegionOverlap ClassifyCell(in ClientRegionCommand hull, int cx, int cy, int cz)
    {
        var x0 = _gridMinX + (cx * CellSize);
        var y0 = _gridMinY + (cy * CellSize);
        double z0 = 0, z1 = 0;
        if (TEvent.Deep)
        {
            z0 = _gridMinZ + (cz * CellSize) - ClassifyPadM;
            z1 = z0 + CellSize + (2 * ClassifyPadM);
        }

        return hull.Classify(x0 - ClassifyPadM, y0 - ClassifyPadM, z0, x0 + CellSize + ClassifyPadM, y0 + CellSize + ClassifyPadM, z1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool InHull(in ClientRegionCommand hull, float x, float y, float z) => hull.Contains(x, y, TEvent.Deep ? z : 0d);

    /// <summary>A hull's cell box: the cells its vertices' bounding box spans, and its focus — the vertices' centroid, where the near budget starts.</summary>
    private void HullCells(in ClientRegionCommand hull, out int minCx, out int maxCx, out int minCy, out int maxCy, out int minCz, out int maxCz,
        out double fx, out double fy, out double fz)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        double sx = 0, sy = 0, sz = 0;
        for (var i = 0; i < hull.VertexCount; i++)
        {
            ref readonly var v = ref hull.Vertices[i];
            x0 = Math.Min(x0, v.X);
            x1 = Math.Max(x1, v.X);
            y0 = Math.Min(y0, v.Y);
            y1 = Math.Max(y1, v.Y);
            z0 = Math.Min(z0, v.Z);
            z1 = Math.Max(z1, v.Z);
            sx += v.X;
            sy += v.Y;
            sz += v.Z;
        }

        var n = Math.Max(1, hull.VertexCount);
        fx = sx / n;
        fy = sy / n;
        fz = TEvent.Deep ? sz / n : 0d;
        minCx = CellX(x0);
        maxCx = CellX(x1);
        minCy = CellY(y0);
        maxCy = CellY(y1);
        minCz = CellZ(z0);
        maxCz = CellZ(z1);
    }

    /// <summary>Whether two hulls have the same half-spaces: an unchanged region moves nothing.</summary>
    private static bool SameHull(in ClientRegionCommand a, in ClientRegionCommand b) =>
        a.PlaneCount == b.PlaneCount
        && MemoryMarshal.AsBytes(((ReadOnlySpan<RegionPlane>)a.Planes)[..a.PlaneCount])
            .SequenceEqual(MemoryMarshal.AsBytes(((ReadOnlySpan<RegionPlane>)b.Planes)[..b.PlaneCount]));

    // ══ The region gather ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    public override bool GatherRegion(SessionId session, bool hasRegion, in ClientRegionCommand region, double maxEdgeM, int nearBudget, int nearCounts,
        double tickSeconds, bool forceReset, in ArchetypeSet archetypes, FrameWorkerScratch scratch, int enterBudget, ref long enters, ref long leaves,
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

        var r = RegionFor(session);
        var tick = _tick;
        r.PendingTick = tick;

        // No LOD for a region (09 § 9 bands are a Sphere's): a session switched from a Sphere at a level goes back to 0 with this frame.
        st.TargetLevel = 0;
        st.PLevel = 0;

        // A session whose committed geometry is another shape's holds what that shape named: only a RESET says what it holds now.
        var reset = st.NeedsReset || forceReset || (st.Anchored && !r.Anchored);
        var rows = RegionRows;
        var w = RegionWindow;

        if (!hasRegion)
        {
            // No region yet: nothing is held. A session that held something under another shape is told so.
            r.PHull.PlaneCount = 0;
            r.PHull.VertexCount = 0;
            Array.Clear(r.P);
            r.POriginX = r.POriginY = r.POriginZ = 0;
            r.PComplete = true;
            r.PHeld = 0;
            r.PUnderSince = 0;
            st.PAnchorX = st.AnchorX;
            st.PAnchorY = st.AnchorY;
            st.PAnchorZ = st.AnchorZ;
            Interlocked.Add(ref GatherTicks, Stopwatch.GetTimestamp() - from);
            return st.Anchored && (reset || r.Hull.PlaneCount > 0);
        }

        // ── The new hull: the session's latest region, clamped again when its profile accepts less than ingress did ────────────────────────────────
        ref var nh = ref r.PHull;
        nh = region;
        if (maxEdgeM > 0 && Extent(in nh) > maxEdgeM * (1 + 1e-9))
        {
            // A region taken while the session's profile accepted more, or before it had a region observer at all.
            nh.ClampToMaxEdge(maxEdgeM);
            if (!nh.BuildPlanes())
            {
                nh.PlaneCount = 0;
            }
        }

        HullCells(in nh, out var nMinCx, out var nMaxCx, out var nMinCy, out var nMaxCy, out var nMinCz, out var nMaxCz, out var fx, out var fy,
            out var fz);
        var nOriginX = nMinCx - 2;
        var nOriginY = nMinCy - 2;
        var nOriginZ = TEvent.Deep ? nMinCz - 2 : 0;

        // The window is sized for the widest hull a profile accepts, so this holds; a hull wider than that is cut to the window rather than overrun it.
        nMaxCx = Math.Min(nMaxCx, nOriginX + w - 1);
        nMaxCy = Math.Min(nMaxCy, nOriginY + w - 1);
        nMaxCz = TEvent.Deep ? Math.Min(nMaxCz, nOriginZ + w - 1) : 0;

        // ── Missed frames: replayed from the push log while every missed tick is still in it, reset otherwise (SUB-03) ─────────────────────────────
        var gap = st.Anchored && r.Anchored && !reset ? (int)(tick - st.LastTick - 1) : 0;
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
            else if (!CollectLogRegion(r, in nh, nMinCx, nMaxCx, nMinCy, nMaxCy, nMinCz, nMaxCz, st.LastTick + 1, in archetypes, log))
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

        // ── The old geometry: the committed hull and window, or nothing ────────────────────────────────────────────────────────────────────────────
        var anchored = st.Anchored && r.Anchored && !reset;
        if (!anchored)
        {
            reset = st.Anchored || reset;
        }

        ref readonly var oh = ref anchored ? ref r.Hull : ref NoHull;
        var oOriginX = anchored ? r.OriginX : nOriginX;
        var oOriginY = anchored ? r.OriginY : nOriginY;
        var oOriginZ = anchored ? r.OriginZ : nOriginZ;
        Span<ulong> none = stackalloc ulong[MaxRegionRows];
        none = none[..rows];
        none.Clear();
        ReadOnlySpan<ulong> o = none;
        if (anchored)
        {
            o = r.D;
        }

        var changed = !anchored || !SameHull(in oh, in nh);

        Span<ulong> d = stackalloc ulong[MaxRegionRows];
        d = d[..rows];
        ShiftRegion(o, oOriginX, oOriginY, oOriginZ, nOriginX, nOriginY, nOriginZ, d);

        // Cells the old window delivered that this frame takes back (the new hull leaves them, or the near budget does), in the new window's coordinates;
        // cells the new window no longer covers are taken back too, found from the old window.
        Span<ulong> gone = stackalloc ulong[MaxRegionRows];
        gone = gone[..rows];
        gone.Clear();

        // ── 1. The hull moved: its delivered cells classified against the new one — and a reset when most of them fall outside it ────────────────
        Span<byte> cls = stackalloc byte[MaxRegionCells];
        if (changed && anchored)
        {
            var total = 0;
            var outside = 0;
            var planes = TEvent.Deep ? w : 1;
            for (var lz = 0; lz < planes; lz++)
            {
                for (var ly = 0; ly < w; ly++)
                {
                    var orow = (lz * w) + ly;
                    var bits = o[orow];
                    total += BitOperations.PopCount(bits);
                    while (bits != 0)
                    {
                        var lx = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        var cx = oOriginX + lx;
                        var cy = oOriginY + ly;
                        var cz = TEvent.Deep ? oOriginZ + lz : 0;
                        if (!RegionLocal(nOriginX, nOriginY, nOriginZ, cx, cy, cz, out var nrow, out var nbit))
                        {
                            outside++;
                            continue;
                        }

                        // A cell the padding keeps Straddling outside the hull's cell box holds no hull point: taken back like an Outside one, so the
                        // delivered cells stay within the box every walk covers.
                        var inBox = cx >= nMinCx && cx <= nMaxCx && cy >= nMinCy && cy <= nMaxCy && (!TEvent.Deep || (cz >= nMinCz && cz <= nMaxCz));
                        var c = inBox ? ClassifyCell(in nh, cx, cy, cz) : RegionOverlap.Outside;
                        cls[(nrow * w) + nbit] = (byte)c;
                        if (c == RegionOverlap.Outside)
                        {
                            outside++;
                            d[nrow] &= ~(1UL << nbit);
                            gone[nrow] |= 1UL << nbit;
                        }
                    }
                }
            }

            if (outside * 2 > total)
            {
                // A jump: leaves for most of what the client holds cost more than a clear. Start over, as a first frame would.
                Interlocked.Increment(ref RegionResets);
                anchored = false;
                reset = true;
                oh = ref NoHull;
                o = none;
                oOriginX = nOriginX;
                oOriginY = nOriginY;
                oOriginZ = nOriginZ;
                d.Clear();
                gone.Clear();
                gap = 0;
                log.Clear();
            }
        }

        // ── 2. The near budget (09 § 7, SUB-23): the counted entities of the delivered cells; past 1.1 n, the farthest cells go ─────────────────────
        var counts = nearBudget > 0 && (uint)nearCounts < (uint)NearCounts.Length ? NearCounts[nearCounts] : null;
        var held = 0;
        var undelivered = 0;
        var focusCx = CellX(fx);
        var focusCy = CellY(fy);
        var focusCz = CellZ(fz);
        var maxRing = Math.Max(Math.Max(Math.Abs(focusCx - nOriginX), Math.Abs(nOriginX + w - 1 - focusCx)),
            Math.Max(Math.Abs(focusCy - nOriginY), Math.Abs(nOriginY + w - 1 - focusCy)));
        if (TEvent.Deep)
        {
            maxRing = Math.Max(maxRing, Math.Max(Math.Abs(focusCz - nOriginZ), Math.Abs(nOriginZ + w - 1 - focusCz)));
        }

        if (counts != null)
        {
            // A new hull moves the focus: the cells to hold are the new ring order's prefix within the budget, so the cells past it are taken back first —
            // or a pan would keep the cells around the old focus and never reach the new one's.
            if (changed && anchored)
            {
                TakeBackPastThePrefix(d, gone, in nh, nOriginX, nOriginY, nOriginZ, nMinCx, nMaxCx, nMinCy, nMaxCy, nMinCz, nMaxCz, focusCx, focusCy,
                    focusCz, maxRing, counts, nearBudget, ref undelivered);
            }

            held = HeldCount(d, nOriginX, nOriginY, nOriginZ, counts);
            if (held * 10L > nearBudget * 11L)
            {
                // Farthest first, ring by ring from the focus, until the estimate is back within the budget.
                for (var ring = maxRing; ring >= 0 && held > nearBudget; ring--)
                {
                    var ringWalk = new RingCells(focusCx, focusCy, focusCz, ring, nOriginX, nOriginX + w - 1, nOriginY, nOriginY + w - 1, nOriginZ,
                        nOriginZ + w - 1);
                    while (held > nearBudget && ringWalk.Next(out var cx, out var cy, out var cz))
                    {
                        if (!RegionLocal(nOriginX, nOriginY, nOriginZ, cx, cy, cz, out var nrow, out var nbit) || ((d[nrow] >> nbit) & 1UL) == 0)
                        {
                            continue;
                        }

                        d[nrow] &= ~(1UL << nbit);
                        gone[nrow] |= 1UL << nbit;
                        held -= counts.Get(TEvent.Key(cx, cy, cz));
                        undelivered++;
                    }
                }
            }
        }

        // ── 3. The cells the old window delivered: taken back (leaves), or swept where the hull's move may have moved an entity in or out ────────────
        if (anchored && (changed || HasAny(gone)))
        {
            var planes = TEvent.Deep ? w : 1;
            for (var lz = 0; lz < planes; lz++)
            {
                for (var ly = 0; ly < w; ly++)
                {
                    var bits = o[(lz * w) + ly];
                    while (bits != 0)
                    {
                        var lx = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        var cx = oOriginX + lx;
                        var cy = oOriginY + ly;
                        var cz = TEvent.Deep ? oOriginZ + lz : 0;
                        if (!InGrid(cx, cy, cz))
                        {
                            continue;
                        }

                        if (!RegionLocal(nOriginX, nOriginY, nOriginZ, cx, cy, cz, out var nrow, out var nbit) || ((d[nrow] >> nbit) & 1UL) == 0)
                        {
                            SweepRegionCell(cx, cy, cz, in oh, in NoHull, in archetypes, scratch, tick, gap, ref enters, ref leaves);
                            continue;
                        }

                        if (!changed)
                        {
                            continue;
                        }

                        var cNew = (RegionOverlap)cls[(nrow * w) + nbit];
                        if (cNew == RegionOverlap.Inside && ClassifyCell(in oh, cx, cy, cz) == RegionOverlap.Inside)
                        {
                            continue;
                        }

                        SweepRegionCell(cx, cy, cz, in oh, in nh, in archetypes, scratch, tick, gap, ref enters, ref leaves);
                    }
                }
            }
        }

        // ── 4. Deliver the cells the new hull meets, nearest the focus first, under the enter budget and the near budget ────────────────────────────
        // Walked when the hull moved or the last frame left cells undelivered; with a near budget, also once the estimate has stayed under 0.9 n for a
        // second — the deadband that keeps a static scene from delivering and taking back the same cell (F5).
        var under = counts != null && held * 10L < nearBudget * 9L;
        var underSince = under ? (r.UnderSince != 0 && anchored ? r.UnderSince : tick) : 0u;
        var walk = changed || !r.Complete || !anchored
                   || (under && (tick - underSince) * tickSeconds >= 1d);
        var entered = 0;
        var delivered = 0;
        var enterStop = false;
        if (walk)
        {
            var budgetStop = false;
            for (var ring = 0; ring <= maxRing && !enterStop && !budgetStop; ring++)
            {
                var ringWalk = new RingCells(focusCx, focusCy, focusCz, ring, nOriginX, nOriginX + w - 1, nOriginY, nOriginY + w - 1, nOriginZ,
                    nOriginZ + w - 1);
                while (ringWalk.Next(out var cx, out var cy, out var cz))
                {
                    if (!InGrid(cx, cy, cz) || cx < nMinCx || cx > nMaxCx || cy < nMinCy || cy > nMaxCy || (TEvent.Deep && (cz < nMinCz || cz > nMaxCz)))
                    {
                        continue;
                    }

                    if (!RegionLocal(nOriginX, nOriginY, nOriginZ, cx, cy, cz, out var nrow, out var nbit) || ((d[nrow] >> nbit) & 1UL) != 0)
                    {
                        continue;
                    }

                    var overlap = ClassifyCell(in nh, cx, cy, cz);
                    if (overlap == RegionOverlap.Outside)
                    {
                        continue;
                    }

                    // Taken back this frame: never delivered again in it — and under a near budget, the first cell past the prefix, where delivery stops.
                    if (((gone[nrow] >> nbit) & 1UL) != 0)
                    {
                        if (counts != null)
                        {
                            budgetStop = true;
                            break;
                        }

                        continue;
                    }

                    if (entered >= enterBudget)
                    {
                        enterStop = true;
                        break;
                    }

                    var key = TEvent.Key(cx, cy, cz);
                    var cellCount = counts?.Get(key) ?? 0;
                    if (counts != null && held + cellCount > nearBudget)
                    {
                        budgetStop = true;
                        break;
                    }

                    d[nrow] |= 1UL << nbit;
                    held += cellCount;
                    entered += DeliverRegionCell(cx, cy, cz, in nh, overlap == RegionOverlap.Inside, in archetypes, scratch, tick, gap);
                    delivered++;
                }
            }

            // Delivered what the budget admits: the deadband's clock starts over.
            underSince = counts != null && held * 10L < nearBudget * 9L ? tick : 0u;
        }

        complete = !enterStop;
        enters += entered;

        // ── 5. The push events over both hulls' cells, or the missed ticks' folded ──────────────────────────────────────────────────────────────────
        HullCells(in oh, out var oMinCx, out var oMaxCx, out var oMinCy, out var oMaxCy, out var oMinCz, out var oMaxCz, out _, out _, out _);
        var twoBoxes = anchored && (oMinCx != nMinCx || oMaxCx != nMaxCx || oMinCy != nMinCy || oMaxCy != nMaxCy
                                    || (TEvent.Deep && (oMinCz != nMinCz || oMaxCz != nMaxCz)));
        if (gap > 0)
        {
            EmitLogRegion(log, in oh, oOriginX, oOriginY, oOriginZ, o, in nh, nOriginX, nOriginY, nOriginZ, d, scratch, ref enters, ref leaves,
                ref updates);
        }
        else
        {
            var index = _log[tick % LogDepth];
            for (var pass = 0; pass < (twoBoxes ? 2 : 1); pass++)
            {
                var walkBox = pass == 0
                    ? new BoxWalk(index.Cells, index.CellCount, index, nMinCx, nMaxCx, nMinCy, nMaxCy, nMinCz, nMaxCz)
                    : new BoxWalk(index.Cells, index.CellCount, index, oMinCx, oMaxCx, oMinCy, oMaxCy, oMinCz, oMaxCz);
                while (walkBox.Next(out var k))
                {
                    var cell = index.Cells[k];
                    var cx = TEvent.KeyX(cell);
                    var cy = TEvent.KeyY(cell);
                    var cz = TEvent.KeyZ(cell);
                    var inNew = cx >= nMinCx && cx <= nMaxCx && cy >= nMinCy && cy <= nMaxCy && (!TEvent.Deep || (cz >= nMinCz && cz <= nMaxCz));
                    if (pass == 1 && inNew)
                    {
                        continue;
                    }

                    var b = index.Starts[k];
                    var end = index.Starts[k + 1];
                    var pe = index.PrimaryEnds[k];

                    // A cell wholly inside both hulls and delivered in both windows: every event that neither entered nor left it is an update.
                    var interior = anchored && RegionHeld(o, oOriginX, oOriginY, oOriginZ, cell) && RegionHeld(d, nOriginX, nOriginY, nOriginZ, cell)
                                   && ClassifyCell(in nh, cx, cy, cz) == RegionOverlap.Inside && ClassifyCell(in oh, cx, cy, cz) == RegionOverlap.Inside;
                    for (var i = b; i < end; i++)
                    {
                        ref var e = ref _indexed[i];
                        if (!archetypes.Contains(e.Archetype))
                        {
                            continue;
                        }

                        if (interior && i < pe && (e.Flags & (PushEvent.HasOld | PushEvent.HasNew)) == (PushEvent.HasOld | PushEvent.HasNew)
                            && e.OldKey == cell)
                        {
                            EmitRecord(e.NetId, e.Block, e.Slot, e.Archetype, e.Groups, (e.Flags & PushEvent.Segment) != 0, scratch, ref updates);
                            continue;
                        }

                        if (i >= pe && InBoxes(PrimaryKey(ref e), nMinCx, nMaxCx, nMinCy, nMaxCy, nMinCz, nMaxCz, twoBoxes, oMinCx, oMaxCx, oMinCy, oMaxCy,
                                oMinCz, oMaxCz))
                        {
                            // A secondary: handled at its primary cell, which a pass reaches.
                            continue;
                        }

                        var was = (e.Flags & PushEvent.HasOld) != 0 && InHull(in oh, e.OldX, e.OldY, e.OldZ)
                                  && RegionHeld(o, oOriginX, oOriginY, oOriginZ, e.OldKey);
                        var isIn = (e.Flags & PushEvent.HasNew) != 0 && InHull(in nh, e.NewX, e.NewY, e.NewZ)
                                   && RegionHeld(d, nOriginX, nOriginY, nOriginZ, e.NewKey);
                        EmitWorld(ref e, was, isIn, e.Groups, (e.Flags & PushEvent.Segment) != 0, scratch, ref enters, ref leaves, ref updates);
                    }
                }
            }
        }

        FoldEmptyCells(scratch);
        if (delivered + undelivered != 0)
        {
            Interlocked.Add(ref RegionCellsDelivered, delivered);
            Interlocked.Add(ref RegionCellsUndelivered, undelivered);
        }

        r.POriginX = nOriginX;
        r.POriginY = nOriginY;
        r.POriginZ = nOriginZ;
        (r.PMinCx, r.PMaxCx, r.PMinCy, r.PMaxCy, r.PMinCz, r.PMaxCz) = (nMinCx, nMaxCx, nMinCy, nMaxCy, nMinCz, nMaxCz);
        d.CopyTo(r.P);
        r.PComplete = complete;
        r.PHeld = held;
        r.PUnderSince = underSince;

        // The focus stands for the anchor: an aggregate's radius and an event's view radius are centred on it.
        st.PAnchorX = fx;
        st.PAnchorY = fy;
        st.PAnchorZ = fz;
        Interlocked.Add(ref GatherTicks, Stopwatch.GetTimestamp() - from);
        return reset;
    }

    /// <summary>A hull's widest extent on any axis, what <see cref="ClientRegionCommand.ClampToMaxEdge"/> bounds.</summary>
    private static double Extent(in ClientRegionCommand hull)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        for (var i = 0; i < hull.VertexCount; i++)
        {
            ref readonly var v = ref hull.Vertices[i];
            x0 = Math.Min(x0, v.X);
            x1 = Math.Max(x1, v.X);
            y0 = Math.Min(y0, v.Y);
            y1 = Math.Max(y1, v.Y);
            z0 = Math.Min(z0, v.Z);
            z1 = Math.Max(z1, v.Z);
        }

        return hull.VertexCount == 0 ? 0d : Math.Max(Math.Max(x1 - x0, y1 - y0), z1 - z0);
    }

    /// <summary>
    /// The near budget after a hull change (09 § 7): the delivered cells past the new focus's ring-order prefix within the budget — counting every cell the
    /// hull meets, delivered or not — are taken back, so the delivery that follows reaches the prefix.
    /// </summary>
    private void TakeBackPastThePrefix(Span<ulong> d, Span<ulong> gone, in ClientRegionCommand nh, int ox, int oy, int oz, int minCx, int maxCx, int minCy,
        int maxCy, int minCz, int maxCz, int focusCx, int focusCy, int focusCz, int maxRing, ReplicationOccupancy counts, int budget, ref int undelivered)
    {
        var total = 0;
        var past = false;
        for (var ring = 0; ring <= maxRing; ring++)
        {
            var cells = new RingCells(focusCx, focusCy, focusCz, ring, ox, ox + RegionWindow - 1, oy, oy + RegionWindow - 1, oz, oz + RegionWindow - 1);
            while (cells.Next(out var cx, out var cy, out var cz))
            {
                if (cx < minCx || cx > maxCx || cy < minCy || cy > maxCy || (TEvent.Deep && (cz < minCz || cz > maxCz)) || !InGrid(cx, cy, cz)
                    || !RegionLocal(ox, oy, oz, cx, cy, cz, out var row, out var bit))
                {
                    continue;
                }

                var isDelivered = ((d[row] >> bit) & 1UL) != 0;
                if (!past)
                {
                    if (!isDelivered && ClassifyCell(in nh, cx, cy, cz) == RegionOverlap.Outside)
                    {
                        continue;
                    }

                    var count = counts.Get(TEvent.Key(cx, cy, cz));
                    if (total + count <= budget)
                    {
                        total += count;
                        continue;
                    }

                    past = true;
                }

                if (isDelivered)
                {
                    d[row] &= ~(1UL << bit);
                    gone[row] |= 1UL << bit;
                    undelivered++;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasAny(ReadOnlySpan<ulong> rows) => rows.IndexOfAnyExcept(0UL) >= 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool InBoxes(ulong key, int nMinCx, int nMaxCx, int nMinCy, int nMaxCy, int nMinCz, int nMaxCz, bool two, int oMinCx, int oMaxCx,
        int oMinCy, int oMaxCy, int oMinCz, int oMaxCz)
    {
        var cx = TEvent.KeyX(key);
        var cy = TEvent.KeyY(key);
        var cz = TEvent.KeyZ(key);
        return (cx >= nMinCx && cx <= nMaxCx && cy >= nMinCy && cy <= nMaxCy && (!TEvent.Deep || (cz >= nMinCz && cz <= nMaxCz)))
               || (two && cx >= oMinCx && cx <= oMaxCx && cy >= oMinCy && cy <= oMaxCy && (!TEvent.Deep || (cz >= oMinCz && cz <= oMaxCz)));
    }

    /// <summary>The near budget's estimate: the counted entities of a window's delivered cells — an upper bound, a straddling cell counting all of its own.</summary>
    private int HeldCount(ReadOnlySpan<ulong> rows, int ox, int oy, int oz, ReplicationOccupancy counts)
    {
        var w = RegionWindow;
        var held = 0;
        var planes = TEvent.Deep ? w : 1;
        for (var lz = 0; lz < planes; lz++)
        {
            for (var ly = 0; ly < w; ly++)
            {
                var bits = rows[(lz * w) + ly];
                while (bits != 0)
                {
                    var lx = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    held += counts.Get(TEvent.Key(ox + lx, oy + ly, TEvent.Deep ? oz + lz : 0));
                }
            }
        }

        return held;
    }

    /// <summary>
    /// The cells at Chebyshev distance <c>ring</c> from a centre cell — a square's border in a flat grid, a cube's shell in a deep one — clipped to a box of
    /// cells (the window), in one fixed order: z, then y, then x ascending. Every walk over one frame's rings uses the same box, so they agree on the order.
    /// </summary>
    private ref struct RingCells
    {
        private readonly int _cx;
        private readonly int _cy;
        private readonly int _cz;
        private readonly int _ring;
        private readonly int _dxLo;
        private readonly int _dxHi;
        private readonly int _dyLo;
        private readonly int _dyHi;
        private readonly int _dzLo;
        private readonly int _dzHi;
        private int _dz;
        private int _dy;
        private int _dx;
        private bool _started;

        public RingCells(int cx, int cy, int cz, int ring, int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
        {
            _cx = cx;
            _cy = cy;
            _cz = cz;
            _ring = ring;
            _dxLo = Math.Max(-ring, minX - cx);
            _dxHi = Math.Min(ring, maxX - cx);
            _dyLo = Math.Max(-ring, minY - cy);
            _dyHi = Math.Min(ring, maxY - cy);
            _dzLo = TEvent.Deep ? Math.Max(-ring, minZ - cz) : 0;
            _dzHi = TEvent.Deep ? Math.Min(ring, maxZ - cz) : 0;
            _dz = _dzLo;
            _dy = _dyLo;
            _dx = 0;
            _started = false;
        }

        // On the ring's top and bottom planes, and its first and last rows, every cell is on it; between them only the two ends are.
        private readonly bool Full => (TEvent.Deep && Math.Abs(_dz) == _ring) || Math.Abs(_dy) == _ring;

        private readonly int FirstDx()
        {
            if (Full)
            {
                return _dxLo <= _dxHi ? _dxLo : int.MaxValue;
            }

            if (_dxLo == -_ring && -_ring <= _dxHi)
            {
                return -_ring;
            }

            return _dxHi == _ring && _ring >= _dxLo ? _ring : int.MaxValue;
        }

        private readonly int NextDx(int dx)
        {
            if (Full)
            {
                return dx + 1 <= _dxHi ? dx + 1 : int.MaxValue;
            }

            return dx == -_ring && _ring != 0 && _dxHi == _ring && _ring >= _dxLo ? _ring : int.MaxValue;
        }

        public bool Next(out int cx, out int cy, out int cz)
        {
            cx = cy = cz = 0;
            if (_dyLo > _dyHi || _dzLo > _dzHi)
            {
                return false;
            }

            _dx = _started ? NextDx(_dx) : FirstDx();
            _started = true;
            while (_dx == int.MaxValue)
            {
                if (++_dy > _dyHi)
                {
                    _dy = _dyLo;
                    if (++_dz > _dzHi)
                    {
                        _dz = _dzHi + 1;
                        return false;
                    }
                }

                _dx = FirstDx();
            }

            cx = _cx + _dx;
            cy = _cy + _dy;
            cz = _cz + _dz;
            return true;
        }
    }

    // ══ Region cells ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whether a cluster's box proves no entity in it can matter: for a delivery, it is wholly outside the new hull; for a sweep, wholly outside both or
    /// wholly inside both. The box is padded by the margin (a centimetre, the quantum and the archetype's slack, SUB-20) before it is classified.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SkipRegionCluster(double bx0, double by0, double bz0, double bx1, double by1, double bz1, in ClientRegionCommand a,
        in ClientRegionCommand n, bool sweeping, double margin, bool hasZ)
    {
        if (double.IsInfinity(bx0) || double.IsInfinity(bx1) || (hasZ && (double.IsInfinity(bz0) || double.IsInfinity(bz1))))
        {
            return false;
        }

        double z0 = 0, z1 = 0;
        if (hasZ)
        {
            z0 = bz0 - margin;
            z1 = bz1 + margin;
        }

        var cn = n.Classify(bx0 - margin, by0 - margin, z0, bx1 + margin, by1 + margin, z1);
        if (!sweeping)
        {
            return cn == RegionOverlap.Outside;
        }

        var ca = a.Classify(bx0 - margin, by0 - margin, z0, bx1 + margin, by1 + margin, z1);
        return (cn == RegionOverlap.Outside && ca == RegionOverlap.Outside) || (cn == RegionOverlap.Inside && ca == RegionOverlap.Inside);
    }

    /// <summary>
    /// Enters every entity of the cell inside the hull that had no event since the session's last frame (the events are the push step's). A cell wholly
    /// inside the hull needs no per-entity test: the classification is the broadphase (09 § 7).
    /// </summary>
    private int DeliverRegionCell(int cx, int cy, int cz, in ClientRegionCommand n, bool inside, in ArchetypeSet archetypes, FrameWorkerScratch scratch,
        uint tick, int gap)
    {
        if (_occupancy.Get(TEvent.Key(cx, cy, cz)) == 0)
        {
            scratch.EmptyCellsSkipped++;
            return 0;
        }

        var entered = 0;
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
            // Realm 0 until replication gets its realm (Realms F1 / R4.3): a session sees one realm, and today there is one.
            using var e = cs.QueryAabb(cs.Realm0Spatial.Grid, qx0, qy0, qz0, qx1, qy1, qz1);
            while (hasZ
                       ? e.MoveNextClusterUnopened(out var chunkId, out var bx0, out var by0, out bz0, out var bx1, out var by1, out bz1)
                       : e.MoveNextClusterUnopened(out chunkId, out bx0, out by0, out bx1, out by1))
            {
                ClampToWorld(ref bx0, ref by0, ref bz0, ref bx1, ref by1, ref bz1, hasZ);
                if (!inside && SkipRegionCluster(bx0, by0, bz0, bx1, by1, bz1, in n, in n, false, _skipMargin[a], hasZ))
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

                    DecodeAt(a, cold + _positionOffset[a], out var px, out var py, out var pz);
                    if (CellX(px) != cx || CellY(py) != cy || (TEvent.Deep && CellZ(pz) != cz) || (!inside && !InHull(in n, px, py, pz)))
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

    /// <summary>
    /// The enters and leaves among one cell's entities with no event since the session's last frame, when the hull moved from <paramref name="a"/> to
    /// <paramref name="n"/> — or, with <see cref="PushReplication.NoHull"/> as <paramref name="n"/>, the leaves of a cell the session no longer has.
    /// </summary>
    private void SweepRegionCell(int cx, int cy, int cz, in ClientRegionCommand a, in ClientRegionCommand n, in ArchetypeSet archetypes,
        FrameWorkerScratch scratch, uint tick, int gap, ref long enters, ref long leaves)
    {
        if (_occupancy.Get(TEvent.Key(cx, cy, cz)) == 0)
        {
            scratch.EmptyCellsSkipped++;
            return;
        }

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
            // Realm 0 until replication gets its realm (Realms F1 / R4.3): a session sees one realm, and today there is one.
            using var e = cs.QueryAabb(cs.Realm0Spatial.Grid, qx0, qy0, qz0, qx1, qy1, qz1);
            while (hasZ
                       ? e.MoveNextClusterUnopened(out var chunkId, out var bx0, out var by0, out bz0, out var bx1, out var by1, out bz1)
                       : e.MoveNextClusterUnopened(out chunkId, out bx0, out by0, out bx1, out by1))
            {
                ClampToWorld(ref bx0, ref by0, ref bz0, ref bx1, ref by1, ref bz1, hasZ);
                if (SkipRegionCluster(bx0, by0, bz0, bx1, by1, bz1, in a, in n, true, _skipMargin[arch], hasZ))
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
                    if (CellX(px) != cx || CellY(py) != cy || (TEvent.Deep && CellZ(pz) != cz))
                    {
                        continue;
                    }

                    var was = InHull(in a, px, py, pz);
                    var isIn = InHull(in n, px, py, pz);
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
    }

    // ══ The region's log catch-up ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Folds every event the session missed over both hulls' cells, as <see cref="CollectLog"/> does over both spheres'. Returns <see langword="false"/> when
    /// an identity it held was reused inside the window and would be both left and entered in one frame (SUB-06).
    /// </summary>
    private bool CollectLogRegion(RegionSession r, in ClientRegionCommand nh, int nMinCx, int nMaxCx, int nMinCy, int nMaxCy, int nMinCz, int nMaxCz,
        uint first, in ArchetypeSet archetypes, LogTable table)
    {
        HullCells(in r.Hull, out var oMinCx, out var oMaxCx, out var oMinCy, out var oMaxCy, out var oMinCz, out var oMaxCz, out _, out _, out _);
        var two = oMinCx != nMinCx || oMaxCx != nMaxCx || oMinCy != nMinCy || oMaxCy != nMaxCy || (TEvent.Deep && (oMinCz != nMinCz || oMaxCz != nMaxCz));
        for (var t = first; t != _tick + 1; t++)
        {
            var slot = _log[t % LogDepth];
            for (var pass = 0; pass < (two ? 2 : 1); pass++)
            {
                var walk = pass == 0
                    ? new BoxWalk(slot.Cells, slot.CellCount, slot, nMinCx, nMaxCx, nMinCy, nMaxCy, nMinCz, nMaxCz)
                    : new BoxWalk(slot.Cells, slot.CellCount, slot, oMinCx, oMaxCx, oMinCy, oMaxCy, oMinCz, oMaxCz);
                while (walk.Next(out var k))
                {
                    var cell = slot.Cells[k];
                    if (pass == 1 && InBoxes(cell, nMinCx, nMaxCx, nMinCy, nMaxCy, nMinCz, nMaxCz, false, 0, 0, 0, 0, 0, 0))
                    {
                        continue;
                    }

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
                            // The same event met again, as a secondary or as the primary after its secondary.
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
                    }
                }
            }
        }

        // An identity the session held that now names another entity it would enter: leave and enter in one frame, which only a RESET can carry. The enter
        // half is tested against the new hull alone, a superset of what the gather enters (hull and delivered cell), so the refusal is conservative.
        for (var i = 0; i < table.Count; i++)
        {
            ref var entry = ref table.Entries[i];
            if (!entry.Replaced || (entry.FirstFlags & PushEvent.HasOld) == 0 || (entry.Last.Flags & PushEvent.HasNew) == 0)
            {
                continue;
            }

            var wasKnown = InHull(in r.Hull, entry.OldX, entry.OldY, entry.OldZ) && RegionHeld(r.D, r.OriginX, r.OriginY, r.OriginZ, entry.OldKey);
            if (wasKnown && InHull(in nh, entry.Last.NewX, entry.Last.NewY, entry.Last.NewZ))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The folded events against the committed hull and window (was) and the new ones (is).</summary>
    private void EmitLogRegion(LogTable table, in ClientRegionCommand a, int oOriginX, int oOriginY, int oOriginZ, ReadOnlySpan<ulong> o,
        in ClientRegionCommand n, int nOriginX, int nOriginY, int nOriginZ, ReadOnlySpan<ulong> d, FrameWorkerScratch scratch, ref long enters,
        ref long leaves, ref long updates)
    {
        for (var i = 0; i < table.Count; i++)
        {
            ref var entry = ref table.Entries[i];
            ref var e = ref entry.Last;
            var was = (entry.FirstFlags & PushEvent.HasOld) != 0 && InHull(in a, entry.OldX, entry.OldY, entry.OldZ)
                      && RegionHeld(o, oOriginX, oOriginY, oOriginZ, entry.OldKey);
            var isIn = (e.Flags & PushEvent.HasNew) != 0 && InHull(in n, e.NewX, e.NewY, e.NewZ) && RegionHeld(d, nOriginX, nOriginY, nOriginZ, e.NewKey);
            EmitWorld(ref e, was, isIn, entry.Groups, entry.Segment, scratch, ref enters, ref leaves, ref updates);
        }
    }

    // ══ Region geometry for the events, the commit and the oracle ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Commits a region gather of this tick with its frame; a frame of another shape leaves the slot's region geometry uncommitted.</summary>
    private void CommitRegion(SessionId session)
    {
        if (_regions.Length == 0)
        {
            return;
        }

        var r = _regions[session.Slot];
        if (r == null || r.Generation != session.Generation)
        {
            return;
        }

        if (r.PendingTick != _tick)
        {
            r.Anchored = false;
            return;
        }

        r.Hull = r.PHull;
        r.OriginX = r.POriginX;
        r.OriginY = r.POriginY;
        r.OriginZ = r.POriginZ;
        r.P.CopyTo(r.D, 0);
        r.Complete = r.PComplete;
        r.Held = r.PHeld;
        r.UnderSince = r.PUnderSince;
        r.Anchored = true;
    }

    /// <summary>Whether a region session's committed hull and delivered window hold a point (SUB-26): what its client was last told.</summary>
    private bool RegionHoldsCommitted(SessionId session, float x, float y, float pz, ulong key)
    {
        if ((uint)session.Slot >= (uint)_regions.Length)
        {
            return false;
        }

        var r = _regions[session.Slot];
        return r != null && r.Generation == session.Generation && r.Anchored && InHull(in r.Hull, x, y, pz)
               && RegionHeld(r.D, r.OriginX, r.OriginY, r.OriginZ, key);
    }

    /// <summary>Whether a region session sees a point: in its committed hull with the cell delivered, or in its pending one (SUB-16, 09 § 11).</summary>
    private bool RegionSeesPoint(RegionSession r, ref PushSessionState st, float x, float y, float z, float viewRadius)
    {
        var pz = TEvent.Deep ? z : 0f;
        if (viewRadius > 0 && !Within(st.PAnchorX, st.PAnchorY, st.PAnchorZ, x, y, pz, (double)viewRadius * viewRadius))
        {
            return false;
        }

        var key = CellKey(x, y, pz);
        return (r.Anchored && InHull(in r.Hull, x, y, pz) && RegionHeld(r.D, r.OriginX, r.OriginY, r.OriginZ, key))
               || (InHull(in r.PHull, x, y, pz) && RegionHeld(r.P, r.POriginX, r.POriginY, r.POriginZ, key));
    }

    private void RegionCellBox(RegionSession r, out int minCx, out int maxCx, out int minCy, out int maxCy, out int minCz, out int maxCz)
    {
        HullCells(in r.PHull, out minCx, out maxCx, out minCy, out maxCy, out minCz, out maxCz, out _, out _, out _);
        if (r.PHull.PlaneCount == 0)
        {
            (minCx, minCy, minCz, maxCx, maxCy, maxCz) = (0, 0, 0, -1, -1, -1);
        }

        if (r.Anchored && r.Hull.PlaneCount > 0)
        {
            HullCells(in r.Hull, out var x0, out var x1, out var y0, out var y1, out var z0, out var z1, out _, out _, out _);
            if (maxCx < minCx)
            {
                (minCx, maxCx, minCy, maxCy, minCz, maxCz) = (x0, x1, y0, y1, z0, z1);
            }
            else
            {
                (minCx, maxCx) = (Math.Min(minCx, x0), Math.Max(maxCx, x1));
                (minCy, maxCy) = (Math.Min(minCy, y0), Math.Max(maxCy, y1));
                (minCz, maxCz) = (Math.Min(minCz, z0), Math.Max(maxCz, z1));
            }
        }
    }

    /// <summary>A region's <c>PUSH_GEOMETRY</c> body: the pending hull's vertices, the near budget's estimate and the pending window.</summary>
    private void WriteDebugRegion(SessionId session, PushGeometryFlags flags, int nearBudget, ref WireWriter w)
    {
        var r = (uint)session.Slot < (uint)_regions.Length ? _regions[session.Slot] : null;
        if (r == null || r.Generation != session.Generation)
        {
            PushGeometry.WriteRegion(ref w, flags, TEvent.Deep ? 3 : 2, [], 0, nearBudget);
            PushGeometry.WriteWindow(ref w, 0, 0, 0, 0, []);
            return;
        }

        ref readonly var hull = ref r.PHull;
        var count = hull.PlaneCount > 0 ? hull.VertexCount : 0;
        Span<double> vertices = stackalloc double[3 * BuiltInCommands.MaxRegionVertices];
        for (var i = 0; i < count; i++)
        {
            vertices[3 * i] = hull.Vertices[i].X;
            vertices[(3 * i) + 1] = hull.Vertices[i].Y;
            vertices[(3 * i) + 2] = hull.Vertices[i].Z;
        }

        PushGeometry.WriteRegion(ref w, flags, hull.Dims == 3 ? 3 : 2, vertices[..(3 * count)], r.PHeld, nearBudget);
        PushGeometry.WriteWindow(ref w, r.POriginX, r.POriginY, r.POriginZ, RegionWindow, r.P);
    }

    public override bool RegionAggregates(SessionId session, AggregateCounts counts, uint tile)
    {
        var r = RegionGatheredNow(session);
        if (r == null || r.PHull.PlaneCount == 0)
        {
            return false;
        }

        var tx = (int)(tile % (uint)counts.DimX);
        var rest = tile / (uint)counts.DimX;
        var ty = (int)(rest % (uint)counts.DimY);
        var tz = (int)(rest / (uint)counts.DimY);

        // A tile is k cells per axis over the replication grid's origin: one whose cells miss the hull's cell box misses the hull.
        var k = Math.Max(1, (int)Math.Round(counts.TileM / CellSize));
        if ((tx * k) + k - 1 < r.PMinCx || tx * k > r.PMaxCx || (ty * k) + k - 1 < r.PMinCy || ty * k > r.PMaxCy
            || (TEvent.Deep && ((tz * k) + k - 1 < r.PMinCz || tz * k > r.PMaxCz)))
        {
            return false;
        }

        var x0 = counts.OriginX + (tx * counts.TileM);
        var y0 = counts.OriginY + (ty * counts.TileM);
        double z0 = 0, z1 = 0;

        // A deep grid's tile has a z extent even when the aggregate grid is one tile deep (a world no taller than a tile): its z cells are the tile's.
        if (TEvent.Deep)
        {
            z0 = counts.OriginZ + (tz * counts.TileM) - ClassifyPadM;
            z1 = z0 + counts.TileM + (2 * ClassifyPadM);
        }

        if (r.PHull.Classify(x0 - ClassifyPadM, y0 - ClassifyPadM, z0, x0 + counts.TileM + ClassifyPadM, y0 + counts.TileM + ClassifyPadM, z1)
            == RegionOverlap.Outside)
        {
            return false;
        }

        // The tile's cells (a tile is a whole number of cells over the same origin): one the hull meets and the near tier was not delivered keeps it.
        var planes = TEvent.Deep ? k : 1;
        for (var dz = 0; dz < planes; dz++)
        {
            for (var dy = 0; dy < k; dy++)
            {
                for (var dx = 0; dx < k; dx++)
                {
                    var cx = (tx * k) + dx;
                    var cy = (ty * k) + dy;
                    var cz = TEvent.Deep ? (tz * k) + dz : 0;
                    // Past the hull's cell box a cell meets nothing, whatever the half-spaces say near a corner — the gather's delivered cells stay inside it.
                    if (!InGrid(cx, cy, cz) || cx < r.PMinCx || cx > r.PMaxCx || cy < r.PMinCy || cy > r.PMaxCy
                        || (TEvent.Deep && (cz < r.PMinCz || cz > r.PMaxCz)) || ClassifyCell(in r.PHull, cx, cy, cz) == RegionOverlap.Outside)
                    {
                        continue;
                    }

                    if (!RegionHeld(r.P, r.POriginX, r.POriginY, r.POriginZ, TEvent.Key(cx, cy, cz)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    internal override bool RegionDelivers(SessionId session, double x, double y, double z)
    {
        var r = _regions.Length == 0 ? null : _regions[session.Slot];
        return r != null && r.Generation == session.Generation && r.Anchored && RegionHeld(r.D, r.OriginX, r.OriginY, r.OriginZ, CellKey(x, y, z));
    }

    internal override int RegionDeliveredCells(SessionId session)
    {
        var r = _regions.Length == 0 ? null : _regions[session.Slot];
        if (r == null || r.Generation != session.Generation || !r.Anchored)
        {
            return 0;
        }

        var cells = 0;
        foreach (var row in r.D)
        {
            cells += BitOperations.PopCount(row);
        }

        return cells;
    }

    /// <summary>Shadow oracle, serial: a region session's shadow against its committed hull and window, recomputed from every block.</summary>
    private void ShadowCheckRegion(RegionSession r, HashSet<uint> set, in ArchetypeSet archetypes)
    {
        var expected = 0;
        var missing = 0L;
        var known = new HashSet<uint>();
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
                    if (!InHull(in r.Hull, px, py, pz) || !RegionHeld(r.D, r.OriginX, r.OriginY, r.OriginZ, CellKey(px, py, pz)))
                    {
                        continue;
                    }

                    expected++;
                    known.Add(hot->NetId);
                    if (!set.Contains(hot->NetId))
                    {
                        missing++;
                    }
                }
            }
        }

        var extra = 0L;
        foreach (var id in set)
        {
            extra += known.Contains(id) ? 0 : 1;
        }

        ShadowMissing += missing;
        ShadowExtra += extra;
        ShadowChecks++;
        ShadowChecked += expected;
    }
}
