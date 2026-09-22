using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Typhon.Engine.Internals;

/// <summary>
/// One interest member against one cluster: which of the cluster's entities lie within the member's leave radius, and which of those within its enter
/// radius — read straight from the cluster's column in the shared snapshot (design 23, phase 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it replaces.</b> The cell's broad phase used to read every entity of every boundary cluster, test it against the cell's enlarged disc and copy
/// the survivors into a per-cell candidate buffer, which each member then filtered: two passes over the same entities and 50 MB a tick of arena writes at
/// d06/1 000. The member's own test implies the cell's — a member is at most a half-diagonal from the cell's centre and the broad radius is its radius
/// plus that half-diagonal — so the copy bought nothing but a second read.
/// </para>
/// <para>
/// <b>The arithmetic is <c>HitArena.FilterCandidatesInto</c>'s, word for word.</b> The degenerate rule (a NaN or an inverted axis is skipped), the query box
/// at the leave radius and the closest-point distance through <c>MaxNative</c> in the same operand order, all on f32 bounds widened to f64, which is exact.
/// Written as miss conditions, because their positive forms differ on NaN. No FMA: RyuJIT contracts neither form.
/// </para>
/// <para>
/// <b>Column layout</b>: four runs of 64 floats — MinX, MinY, MaxX, MaxY — so a 16-entity block is one 64-byte load per bound and needs no transpose.
/// Read through a <c>ref</c>, never a pointer: the snapshot's columns are a managed array.
/// </para>
/// </remarks>
internal static class InterestBandKernel
{
    /// <summary>Floats per cluster column: four bounds of 64 slots.</summary>
    internal const int ColumnFloats = 256;

    /// <summary>
    /// The member's far mask (within <paramref name="leaveRadius"/>) over the occupied slots of one cluster column, and its near mask (within the enter
    /// radius).
    /// </summary>
    /// <param name="columns">The first float of the cluster's column: MinX[64], MinY[64], MaxX[64], MaxY[64].</param>
    /// <param name="occupancy">The cluster's occupied slots; a 16-slot block with none is not read.</param>
    /// <param name="cx">The member's viewpoint X.</param>
    /// <param name="cy">The member's viewpoint Y.</param>
    /// <param name="leaveRadius">The radius a held entity may stay inside; the query box is this far from the viewpoint.</param>
    /// <param name="enterSq">The squared radius a new entity must be inside; never larger than the leave radius squared.</param>
    /// <param name="near">The subset of the result within the enter radius.</param>
    /// <returns>The slots within the leave radius.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Match(ref float columns, ulong occupancy, double cx, double cy, double leaveRadius, double enterSq, out ulong near)
    {
        if (Vector512.IsHardwareAccelerated)
        {
            return Match512(ref columns, occupancy, cx, cy, leaveRadius, enterSq, out near);
        }

        if (Vector256.IsHardwareAccelerated)
        {
            return Match256(ref columns, occupancy, cx, cy, leaveRadius, enterSq, out near);
        }

        return MatchScalar(ref columns, occupancy, cx, cy, leaveRadius, enterSq, out near);
    }

    /// <summary><see cref="Match"/> on 512-bit vectors: sixteen entities per block in four loads, the f64 tests eight at a time.</summary>
    internal static ulong Match512(ref float columns, ulong occupancy, double cx, double cy, double leaveRadius, double enterSq, out ulong near)
    {
        var vcx = Vector512.Create(cx);
        var vcy = Vector512.Create(cy);
        var vLeave2 = Vector512.Create(leaveRadius * leaveRadius);
        var vEnter2 = Vector512.Create(enterSq);
        var vqMinX = Vector512.Create(cx - leaveRadius);
        var vqMaxX = Vector512.Create(cx + leaveRadius);
        var vqMinY = Vector512.Create(cy - leaveRadius);
        var vqMaxY = Vector512.Create(cy + leaveRadius);

        var far = 0UL;
        var nearAll = 0UL;
        for (var g = 0; g < 4; g++)
        {
            var occupied = (uint)(occupancy >> (g * 16)) & 0xFFFFu;
            if (occupied == 0u)
            {
                continue;
            }

            var o = (nuint)(g * 16);
            var minX = Vector512.LoadUnsafe(ref columns, o);
            var minY = Vector512.LoadUnsafe(ref columns, o + 64);
            var maxX = Vector512.LoadUnsafe(ref columns, o + 128);
            var maxY = Vector512.LoadUnsafe(ref columns, o + 192);

            // !(min <= max) on either axis — a NaN or an inverted box — is skipped, as the broad phase skipped it.
            var valid = (uint)(Vector512.LessThanOrEqual(minX, maxX) & Vector512.LessThanOrEqual(minY, maxY)).ExtractMostSignificantBits() & occupied;

            var lo = Half512(Vector512.WidenLower(minX), Vector512.WidenLower(minY), Vector512.WidenLower(maxX), Vector512.WidenLower(maxY), vqMinX, vqMinY,
                vqMaxX, vqMaxY, vcx, vcy, vLeave2, vEnter2, out var nearLo);
            var hi = Half512(Vector512.WidenUpper(minX), Vector512.WidenUpper(minY), Vector512.WidenUpper(maxX), Vector512.WidenUpper(maxY), vqMinX, vqMinY,
                vqMaxX, vqMaxY, vcx, vcy, vLeave2, vEnter2, out var nearHi);
            far |= (ulong)((lo | (hi << 8)) & valid) << (g * 16);
            nearAll |= (ulong)((nearLo | (nearHi << 8)) & valid) << (g * 16);
        }

        near = nearAll;
        return far;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Half512(Vector512<double> minX, Vector512<double> minY, Vector512<double> maxX, Vector512<double> maxY, Vector512<double> qMinX,
        Vector512<double> qMinY, Vector512<double> qMaxX, Vector512<double> qMaxY, Vector512<double> cx, Vector512<double> cy, Vector512<double> leave2,
        Vector512<double> enter2, out uint near)
    {
        var miss = Vector512.LessThan(maxX, qMinX) | Vector512.GreaterThan(minX, qMaxX) | Vector512.LessThan(maxY, qMinY) | Vector512.GreaterThan(minY, qMaxY);
        var dx = Vector512.MaxNative(Vector512<double>.Zero, Vector512.MaxNative(minX - cx, cx - maxX));
        var dy = Vector512.MaxNative(Vector512<double>.Zero, Vector512.MaxNative(minY - cy, cy - maxY));
        var distSq = (dx * dx) + (dy * dy);
        miss |= Vector512.GreaterThan(distSq, leave2);
        near = ~(uint)(miss | Vector512.GreaterThan(distSq, enter2)).ExtractMostSignificantBits() & 0xFFu;
        return ~(uint)miss.ExtractMostSignificantBits() & 0xFFu;
    }

    /// <summary><see cref="Match"/> on 256-bit vectors: eight entities per load, the f64 tests four at a time.</summary>
    internal static ulong Match256(ref float columns, ulong occupancy, double cx, double cy, double leaveRadius, double enterSq, out ulong near)
    {
        var vcx = Vector256.Create(cx);
        var vcy = Vector256.Create(cy);
        var vLeave2 = Vector256.Create(leaveRadius * leaveRadius);
        var vEnter2 = Vector256.Create(enterSq);
        var vqMinX = Vector256.Create(cx - leaveRadius);
        var vqMaxX = Vector256.Create(cx + leaveRadius);
        var vqMinY = Vector256.Create(cy - leaveRadius);
        var vqMaxY = Vector256.Create(cy + leaveRadius);

        var far = 0UL;
        var nearAll = 0UL;
        for (var g = 0; g < 8; g++)
        {
            var occupied = (uint)(occupancy >> (g * 8)) & 0xFFu;
            if (occupied == 0u)
            {
                continue;
            }

            var o = (nuint)(g * 8);
            var minX = Vector256.LoadUnsafe(ref columns, o);
            var minY = Vector256.LoadUnsafe(ref columns, o + 64);
            var maxX = Vector256.LoadUnsafe(ref columns, o + 128);
            var maxY = Vector256.LoadUnsafe(ref columns, o + 192);

            var valid = (uint)(Vector256.LessThanOrEqual(minX, maxX) & Vector256.LessThanOrEqual(minY, maxY)).ExtractMostSignificantBits() & occupied;

            var lo = Half256(Vector256.WidenLower(minX), Vector256.WidenLower(minY), Vector256.WidenLower(maxX), Vector256.WidenLower(maxY), vqMinX, vqMinY,
                vqMaxX, vqMaxY, vcx, vcy, vLeave2, vEnter2, out var nearLo);
            var hi = Half256(Vector256.WidenUpper(minX), Vector256.WidenUpper(minY), Vector256.WidenUpper(maxX), Vector256.WidenUpper(maxY), vqMinX, vqMinY,
                vqMaxX, vqMaxY, vcx, vcy, vLeave2, vEnter2, out var nearHi);
            far |= (ulong)((lo | (hi << 4)) & valid) << (g * 8);
            nearAll |= (ulong)((nearLo | (nearHi << 4)) & valid) << (g * 8);
        }

        near = nearAll;
        return far;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Half256(Vector256<double> minX, Vector256<double> minY, Vector256<double> maxX, Vector256<double> maxY, Vector256<double> qMinX,
        Vector256<double> qMinY, Vector256<double> qMaxX, Vector256<double> qMaxY, Vector256<double> cx, Vector256<double> cy, Vector256<double> leave2,
        Vector256<double> enter2, out uint near)
    {
        var miss = Vector256.LessThan(maxX, qMinX) | Vector256.GreaterThan(minX, qMaxX) | Vector256.LessThan(maxY, qMinY) | Vector256.GreaterThan(minY, qMaxY);
        var dx = Vector256.MaxNative(Vector256<double>.Zero, Vector256.MaxNative(minX - cx, cx - maxX));
        var dy = Vector256.MaxNative(Vector256<double>.Zero, Vector256.MaxNative(minY - cy, cy - maxY));
        var distSq = (dx * dx) + (dy * dy);
        miss |= Vector256.GreaterThan(distSq, leave2);
        near = ~(uint)(miss | Vector256.GreaterThan(distSq, enter2)).ExtractMostSignificantBits() & 0xFu;
        return ~(uint)miss.ExtractMostSignificantBits() & 0xFu;
    }

    /// <summary><see cref="Match"/> one entity at a time: the reference the vector forms are held to, and the path where no vector unit applies.</summary>
    internal static ulong MatchScalar(ref float columns, ulong occupancy, double cx, double cy, double leaveRadius, double enterSq, out ulong near)
    {
        var leaveSq = leaveRadius * leaveRadius;
        var qMinX = cx - leaveRadius;
        var qMaxX = cx + leaveRadius;
        var qMinY = cy - leaveRadius;
        var qMaxY = cy + leaveRadius;
        var far = 0UL;
        var nearAll = 0UL;
        var bits = occupancy;
        while (bits != 0UL)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            double minX = Unsafe.Add(ref columns, slot);
            double minY = Unsafe.Add(ref columns, 64 + slot);
            double maxX = Unsafe.Add(ref columns, 128 + slot);
            double maxY = Unsafe.Add(ref columns, 192 + slot);
            if (!(minX <= maxX) || !(minY <= maxY))
            {
                continue;
            }

            if (maxX < qMinX || minX > qMaxX || maxY < qMinY || minY > qMaxY)
            {
                continue;
            }

            var dx = double.MaxNative(0d, double.MaxNative(minX - cx, cx - maxX));
            var dy = double.MaxNative(0d, double.MaxNative(minY - cy, cy - maxY));
            var distSq = (dx * dx) + (dy * dy);
            if (distSq > leaveSq)
            {
                continue;
            }

            far |= 1UL << slot;
            if (distSq <= enterSq)
            {
                nearAll |= 1UL << slot;
            }
        }

        near = nearAll;
        return far;
    }
}
