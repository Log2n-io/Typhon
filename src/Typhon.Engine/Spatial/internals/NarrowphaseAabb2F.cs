using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Typhon.Engine.Internals;

/// <summary>
/// The AABB2F narrowphase over whole 16-entity blocks: which entities of a cluster's AABB2F column match the query — exactly the entities
/// <c>AabbClusterEnumerator</c>'s scalar loop accepts, one bit each.
/// </summary>
/// <remarks>
/// <para><b>The scalar loop's predicates, at its width (SQ-06).</b> Sixteen entities are four 64-byte loads, transposed in registers into MinX / MinY / MaxX
/// / MaxY lanes — eight two-source permutes on AVX-512, unpacks and shuffles on AVX2. The degenerate test runs on those f32 lanes as
/// <c>SpatialGeometry.IsDegenerate</c> does. Each lane is then widened to f64, which is exact, and the overlap and radius tests are the loop's skip
/// conditions word for word: <c>maxX &lt; qMinX</c>, <c>minX &gt; qMaxX</c>, the clamp distance through <c>MaxNative</c> in the loop's operand order, and
/// <c>distSq &gt; radiusSq</c>. Written as "miss" conditions rather than their positive forms because the two differ on a NaN query bound. No FMA:
/// RyuJIT contracts neither the scalar nor the vector <c>dx * dx + dy * dy</c>.</para>
/// <para><b>Held to the loop itself, not to a copy of it.</b> <c>NarrowphaseAabb2FTests</c> runs each kernel and <c>AabbClusterEnumerator.Drain</c> over
/// the same column, on inputs seeded with NaN, infinite, inverted and 2^36 bounds.</para>
/// <para><b>Storage untouched.</b> The column stays AoS in the cluster; nothing about the page, the WAL or the cluster format changes.</para>
/// <para>Measured before it was built (E-D, engine-perf report 2026-09-13 §4.3): 6.83 → 0.97 ns per tested entity on AVX-512, identical hit counts.
/// No arm64 kernel yet: there <see cref="Best"/> is <see cref="Kernel.None"/> and the caller keeps its scalar loop.</para>
/// </remarks>
internal static unsafe class NarrowphaseAabb2F
{
    internal enum Kernel : byte
    {
        None,
        Avx2,
        Avx512,
    }

    /// <summary>The widest kernel this machine runs. A static readonly the JIT folds, so <see cref="Match"/>'s dispatch costs nothing once tiered.</summary>
    internal static readonly Kernel Best =
        Avx512F.IsSupported && Vector512.IsHardwareAccelerated ? Kernel.Avx512 : Avx2.IsSupported ? Kernel.Avx2 : Kernel.None;

    /// <summary>Bytes per entity the kernels require: one AABB2F and nothing else in the component, so a block is four contiguous 64-byte loads.</summary>
    internal const int Stride = 16;

    /// <summary>Entities per block.</summary>
    internal const int BlockSize = 16;

    /// <summary>
    /// Hit mask over the first <paramref name="blocks"/> 16-entity blocks of the AABB2F column at <paramref name="column"/>, restricted to
    /// <paramref name="bits"/>. A block with no bit in <paramref name="bits"/> is not read. The query's Z range is not read: a 2D query's is ±Infinity, and the
    /// caller leaves an inverted one to the loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Match(float* column, ulong bits, int blocks, in QueryGeometry query) => Best switch
    {
        Kernel.Avx512 => MatchAvx512(column, bits, blocks, in query),
        Kernel.Avx2 => MatchAvx2(column, bits, blocks, in query),
        _ => ThrowNoKernel(),
    };

    /// <summary><see cref="Match"/> on AVX-512: sixteen entities per block in one transpose, the f64 tests eight at a time.</summary>
    internal static ulong MatchAvx512(float* column, ulong bits, int blocks, in QueryGeometry query)
    {
        var vqMinX = Vector512.Create(query.MinX);
        var vqMinY = Vector512.Create(query.MinY);
        var vqMaxX = Vector512.Create(query.MaxX);
        var vqMaxY = Vector512.Create(query.MaxY);
        var vcx = Vector512.Create(query.CenterX);
        var vcy = Vector512.Create(query.CenterY);
        var vr2 = Vector512.Create(query.RadiusSq);
        var radius = query.RadiusSq > 0d;

        // Four entities per 64-byte load, four floats each. idxLo / idxHi gather the min / max pairs of eight entities out of two loads, idxA / idxB then
        // join the halves of two such gathers.
        var idxLo = Vector512.Create(0, 4, 8, 12, 16, 20, 24, 28, 1, 5, 9, 13, 17, 21, 25, 29);     // minX e0-7 | minY e0-7
        var idxHi = Vector512.Create(2, 6, 10, 14, 18, 22, 26, 30, 3, 7, 11, 15, 19, 23, 27, 31);    // maxX e0-7 | maxY e0-7
        var idxA = Vector512.Create(0, 1, 2, 3, 4, 5, 6, 7, 16, 17, 18, 19, 20, 21, 22, 23);          // first halves of both
        var idxB = Vector512.Create(8, 9, 10, 11, 12, 13, 14, 15, 24, 25, 26, 27, 28, 29, 30, 31);    // second halves of both

        ulong hits = 0UL;
        for (var g = 0; g < blocks; g++)
        {
            var occupied = (uint)(bits >> (g * BlockSize)) & 0xFFFFu;
            if (occupied == 0u)
            {
                continue;
            }

            var p = column + (g * BlockSize * 4);
            var v0 = Vector512.Load(p);
            var v1 = Vector512.Load(p + 16);
            var v2 = Vector512.Load(p + 32);
            var v3 = Vector512.Load(p + 48);
            var lo01 = Avx512F.PermuteVar16x32x2(v0, idxLo, v1);
            var hi01 = Avx512F.PermuteVar16x32x2(v0, idxHi, v1);
            var lo23 = Avx512F.PermuteVar16x32x2(v2, idxLo, v3);
            var hi23 = Avx512F.PermuteVar16x32x2(v2, idxHi, v3);
            var minX = Avx512F.PermuteVar16x32x2(lo01, idxA, lo23);
            var minY = Avx512F.PermuteVar16x32x2(lo01, idxB, lo23);
            var maxX = Avx512F.PermuteVar16x32x2(hi01, idxA, hi23);
            var maxY = Avx512F.PermuteVar16x32x2(hi01, idxB, hi23);

            // SpatialGeometry.IsDegenerate(AABB2F), sixteen at once: any NaN, or an inverted axis.
            var degenerate = Avx512F.CompareUnordered(minX, maxX) | Avx512F.CompareUnordered(minY, maxY) | Vector512.GreaterThan(minX, maxX)
                             | Vector512.GreaterThan(minY, maxY);
            var valid = ~(uint)degenerate.ExtractMostSignificantBits() & 0xFFFFu;

            var matched = Half512(Vector512.WidenLower(minX), Vector512.WidenLower(minY), Vector512.WidenLower(maxX), Vector512.WidenLower(maxY),
                              vqMinX, vqMinY, vqMaxX, vqMaxY, vcx, vcy, vr2, radius)
                          | (Half512(Vector512.WidenUpper(minX), Vector512.WidenUpper(minY), Vector512.WidenUpper(maxX), Vector512.WidenUpper(maxY),
                              vqMinX, vqMinY, vqMaxX, vqMaxY, vcx, vcy, vr2, radius) << 8);
            hits |= (ulong)(matched & valid & occupied) << (g * BlockSize);
        }

        return hits;
    }

    /// <summary>The loop's overlap and radius tests for eight widened entities; bit i set when entity i is NOT rejected.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Half512(Vector512<double> minX, Vector512<double> minY, Vector512<double> maxX, Vector512<double> maxY, Vector512<double> qMinX,
        Vector512<double> qMinY, Vector512<double> qMaxX, Vector512<double> qMaxY, Vector512<double> cx, Vector512<double> cy, Vector512<double> radiusSq,
        bool radius)
    {
        var miss = Vector512.LessThan(maxX, qMinX) | Vector512.GreaterThan(minX, qMaxX) | Vector512.LessThan(maxY, qMinY) | Vector512.GreaterThan(minY, qMaxY);
        if (radius)
        {
            var dx = Vector512.MaxNative(Vector512<double>.Zero, Vector512.MaxNative(minX - cx, cx - maxX));
            var dy = Vector512.MaxNative(Vector512<double>.Zero, Vector512.MaxNative(minY - cy, cy - maxY));
            miss |= Vector512.GreaterThan((dx * dx) + (dy * dy), radiusSq);
        }

        return ~(uint)miss.ExtractMostSignificantBits() & 0xFFu;
    }

    /// <summary>
    /// <see cref="Match"/> on AVX2: two eight-entity halves per block. The in-lane transpose leaves the entities in the order 0 2 4 6 1 3 5 7, so each
    /// half's mask is put back in slot order at the end (<see cref="Unshuffle"/>) rather than paying four more permutes per half.
    /// </summary>
    internal static ulong MatchAvx2(float* column, ulong bits, int blocks, in QueryGeometry query)
    {
        var vqMinX = Vector256.Create(query.MinX);
        var vqMinY = Vector256.Create(query.MinY);
        var vqMaxX = Vector256.Create(query.MaxX);
        var vqMaxY = Vector256.Create(query.MaxY);
        var vcx = Vector256.Create(query.CenterX);
        var vcy = Vector256.Create(query.CenterY);
        var vr2 = Vector256.Create(query.RadiusSq);
        var radius = query.RadiusSq > 0d;

        ulong hits = 0UL;
        for (var g = 0; g < blocks * 2; g++)
        {
            var occupied = (uint)(bits >> (g * 8)) & 0xFFu;
            if (occupied == 0u)
            {
                continue;
            }

            var p = column + (g * 32);
            var v0 = Vector256.Load(p);          // e0 | e1
            var v1 = Vector256.Load(p + 8);      // e2 | e3
            var v2 = Vector256.Load(p + 16);     // e4 | e5
            var v3 = Vector256.Load(p + 24);     // e6 | e7
            var t0 = Avx.UnpackLow(v0, v1);      // e0.minX e2.minX e0.minY e2.minY | e1.minX e3.minX e1.minY e3.minY
            var t1 = Avx.UnpackHigh(v0, v1);     // the same for max
            var t2 = Avx.UnpackLow(v2, v3);
            var t3 = Avx.UnpackHigh(v2, v3);
            var minX = Avx.Shuffle(t0, t2, 0x44);    // e0 e2 e4 e6 | e1 e3 e5 e7
            var minY = Avx.Shuffle(t0, t2, 0xEE);
            var maxX = Avx.Shuffle(t1, t3, 0x44);
            var maxY = Avx.Shuffle(t1, t3, 0xEE);

            var degenerate = Avx.CompareUnordered(minX, maxX) | Avx.CompareUnordered(minY, maxY) | Vector256.GreaterThan(minX, maxX)
                             | Vector256.GreaterThan(minY, maxY);
            var valid = ~(uint)degenerate.ExtractMostSignificantBits() & 0xFFu;

            var matched = Half256(Vector256.WidenLower(minX), Vector256.WidenLower(minY), Vector256.WidenLower(maxX), Vector256.WidenLower(maxY),
                              vqMinX, vqMinY, vqMaxX, vqMaxY, vcx, vcy, vr2, radius)
                          | (Half256(Vector256.WidenUpper(minX), Vector256.WidenUpper(minY), Vector256.WidenUpper(maxX), Vector256.WidenUpper(maxY),
                              vqMinX, vqMinY, vqMaxX, vqMaxY, vcx, vcy, vr2, radius) << 4);
            hits |= (ulong)(Unshuffle(matched & valid) & occupied) << (g * 8);
        }

        return hits;
    }

    /// <summary>The loop's overlap and radius tests for four widened entities; bit i set when entity i is NOT rejected.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Half256(Vector256<double> minX, Vector256<double> minY, Vector256<double> maxX, Vector256<double> maxY, Vector256<double> qMinX,
        Vector256<double> qMinY, Vector256<double> qMaxX, Vector256<double> qMaxY, Vector256<double> cx, Vector256<double> cy, Vector256<double> radiusSq,
        bool radius)
    {
        var miss = Vector256.LessThan(maxX, qMinX) | Vector256.GreaterThan(minX, qMaxX) | Vector256.LessThan(maxY, qMinY) | Vector256.GreaterThan(minY, qMaxY);
        if (radius)
        {
            var dx = Vector256.MaxNative(Vector256<double>.Zero, Vector256.MaxNative(minX - cx, cx - maxX));
            var dy = Vector256.MaxNative(Vector256<double>.Zero, Vector256.MaxNative(minY - cy, cy - maxY));
            miss |= Vector256.GreaterThan((dx * dx) + (dy * dy), radiusSq);
        }

        return ~(uint)miss.ExtractMostSignificantBits() & 0xFu;
    }

    /// <summary>An eight-bit mask in lane order 0 2 4 6 1 3 5 7, back in slot order: the low nibble to the even bits, the high one to the odd bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Unshuffle(uint laneOrder)
    {
        var even = laneOrder & 0xFu;
        var odd = (laneOrder >> 4) & 0xFu;
        even = (even | (even << 2)) & 0x33u;
        even = (even | (even << 1)) & 0x55u;
        odd = (odd | (odd << 2)) & 0x33u;
        odd = (odd | (odd << 1)) & 0x55u;
        return even | (odd << 1);
    }

    /// <summary>A caller reached <see cref="Match"/> on a machine with no kernel, past the <see cref="Best"/> gate every caller must take.</summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong ThrowNoKernel() =>
        throw new PlatformNotSupportedException("NarrowphaseAabb2F.Match has no kernel on this machine; callers must check NarrowphaseAabb2F.Best first.");
}
