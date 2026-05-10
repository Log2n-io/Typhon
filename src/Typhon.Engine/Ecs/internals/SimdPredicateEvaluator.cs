using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Typhon.Engine.Internals;

/// <summary>
/// SIMD-accelerated predicate evaluation on cluster SoA data.
/// Uses gather instructions to load field values from strided component arrays, then SIMD compare to produce a per-entity match bitmask.
/// Three-tier dispatch: AVX-512 (16 int/float per batch) → AVX2 (8 per batch) → scalar fallback.
/// </summary>
internal static unsafe class SimdPredicateEvaluator
{
    /// <summary>Returns true if the given KeyType can be evaluated via SIMD gather+compare.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSimdEligible(KeyType keyType) =>
        keyType is KeyType.Int or KeyType.UInt or KeyType.Float    // 32-bit gather
               or KeyType.Long or KeyType.ULong or KeyType.Double; // 64-bit gather

    /// <summary>
    /// Evaluate one <see cref="FieldEvaluator"/> against all slots in a cluster using SIMD.
    /// Returns a bitmask where bit <c>i</c> is set if entity at slot <c>i</c> satisfies the predicate.
    /// The caller must AND the result with occupancy bits to filter out empty slots.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ulong EvaluateCluster(ref FieldEvaluator eval, byte* compBase, int compSize, int clusterSize)
    {
        // Three-tier dispatch: AVX-512 (16/8 per batch) → AVX2 (8/4 per batch) → scalar
        if (Avx512F.IsSupported)
        {
            return eval.KeyType switch
            {
                KeyType.Int => EvaluateInt32_512(ref eval, compBase, compSize, clusterSize),
                KeyType.UInt => EvaluateUInt32_512(ref eval, compBase, compSize, clusterSize),
                KeyType.Float => EvaluateFloat32_512(ref eval, compBase, compSize, clusterSize),
                KeyType.Long => EvaluateInt64_512(ref eval, compBase, compSize, clusterSize),
                KeyType.ULong => EvaluateUInt64_512(ref eval, compBase, compSize, clusterSize),
                KeyType.Double => EvaluateFloat64_512(ref eval, compBase, compSize, clusterSize),
                _ => EvaluateScalarAll(ref eval, compBase, compSize, clusterSize)
            };
        }

        if (Avx2.IsSupported)
        {
            return eval.KeyType switch
            {
                KeyType.Int => EvaluateInt32(ref eval, compBase, compSize, clusterSize),
                KeyType.UInt => EvaluateUInt32(ref eval, compBase, compSize, clusterSize),
                KeyType.Float => EvaluateFloat32(ref eval, compBase, compSize, clusterSize),
                KeyType.Long => EvaluateInt64(ref eval, compBase, compSize, clusterSize),
                KeyType.ULong => EvaluateUInt64(ref eval, compBase, compSize, clusterSize),
                KeyType.Double => EvaluateFloat64(ref eval, compBase, compSize, clusterSize),
                _ => EvaluateScalarAll(ref eval, compBase, compSize, clusterSize)
            };
        }

        return EvaluateScalarAll(ref eval, compBase, compSize, clusterSize);
    }

    // ─── 32-bit types (8 elements per Vector256) ─────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong EvaluateInt32(ref FieldEvaluator eval, byte* compBase, int compSize, int clusterSize)
    {
        int threshold = (int)eval.Threshold;
        var thrVec = Vector256.Create(threshold);
        int fieldOffset = eval.FieldOffset;
        int* baseAddr = (int*)(compBase + fieldOffset);

        // Pre-compute stride index vector: byte offsets from baseAddr for slots 0..7
        var strideVec = Vector256.Create(0, compSize, compSize * 2, compSize * 3, compSize * 4, compSize * 5, compSize * 6, compSize * 7);

        ulong matchBits = 0;
        for (int batch = 0; batch < clusterSize; batch += 8)
        {
            var offsetVec = Vector256.Create(batch * compSize);
            var indices = Avx2.Add(strideVec, offsetVec);
            var gathered = Avx2.GatherVector256(baseAddr, indices, 1);

            uint mask = CompareMask32(gathered, thrVec, eval.CompareOp);

            // Handle partial last batch: if batch+8 > clusterSize, mask off extra lanes
            int remaining = clusterSize - batch;
            if (remaining < 8)
            {
                mask &= (1u << remaining) - 1;
            }

            matchBits |= (ulong)mask << batch;
        }

        return matchBits;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong EvaluateUInt32(ref FieldEvaluator eval, byte* compBase, int compSize, int clusterSize)
    {
        // Unsigned comparison via XOR with sign bit: converts unsigned ordering to signed ordering.
        // This matches ZoneMapArray.ReadFieldAsOrderedLong encoding for unsigned types.
        int threshold = (int)((uint)eval.Threshold ^ 0x80000000u);
        var thrVec = Vector256.Create(threshold);
        var signBit = Vector256.Create(unchecked((int)0x80000000u));
        int fieldOffset = eval.FieldOffset;
        int* baseAddr = (int*)(compBase + fieldOffset);

        var strideVec = Vector256.Create(0, compSize, compSize * 2, compSize * 3, compSize * 4, compSize * 5, compSize * 6, compSize * 7);

        ulong matchBits = 0;
        for (int batch = 0; batch < clusterSize; batch += 8)
        {
            var offsetVec = Vector256.Create(batch * compSize);
            var indices = Avx2.Add(strideVec, offsetVec);
            var gathered = Avx2.GatherVector256(baseAddr, indices, 1);

            // XOR with sign bit to convert unsigned→signed comparison
            var signedGathered = Avx2.Xor(gathered, signBit);

            uint mask = CompareMask32(signedGathered, thrVec, eval.CompareOp);

            int remaining = clusterSize - batch;
            if (remaining < 8)
            {
                mask &= (1u << remaining) - 1;
            }

            matchBits |= (ulong)mask << batch;
        }

        return matchBits;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong EvaluateFloat32(ref FieldEvaluator eval, byte* compBase, int compSize, int clusterSize)
    {
        int bits = (int)eval.Threshold;
        float threshold = Unsafe.As<int, float>(ref bits);
        var thrVec = Vector256.Create(threshold);
        int fieldOffset = eval.FieldOffset;
        float* baseAddr = (float*)(compBase + fieldOffset);

        var strideVec = Vector256.Create(0, compSize, compSize * 2, compSize * 3, compSize * 4, compSize * 5, compSize * 6, compSize * 7);

        ulong matchBits = 0;
        for (int batch = 0; batch < clusterSize; batch += 8)
        {
            var offsetVec = Vector256.Create(batch * compSize);
            var indices = Avx2.Add(strideVec, offsetVec);
            var gathered = Avx2.GatherVector256(baseAddr, indices, 1);

            uint mask = CompareFloatMask32(gathered, thrVec, eval.CompareOp);

            int remaining = clusterSize - batch;
            if (remaining < 8)
            {
                mask &= (1u << remaining) - 1;
            }

            matchBits |= (ulong)mask << batch;
        }

        return matchBits;
    }

    // ─── 64-bit types (4 elements per Vector256) ─────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong EvaluateInt64(ref FieldEvaluator eval, byte* compBase, int compSize, int clusterSize)
    {
        long threshold = eval.Threshold;
        var thrVec = Vector256.Create(threshold);
        int fieldOffset = eval.FieldOffset;
        long* baseAddr = (long*)(compBase + fieldOffset);

        // 64-bit gather uses Vector128<int> for indices (4 indices → 4 longs)
        var strideVec = Vector128.Create(0, compSize, compSize * 2, compSize * 3);

        ulong matchBits = 0;
        for (int batch = 0; batch < clusterSize; batch += 4)
        {
            var offsetVec = Vector128.Create(batch * compSize);
            var indices = Sse2.Add(strideVec, offsetVec);
            var gathered = Avx2.GatherVector256(baseAddr, indices, 1);

            uint mask = CompareMask64(gathered, thrVec, eval.CompareOp);

            int remaining = clusterSize - batch;
            if (remaining < 4)
            {
                mask &= (1u << remaining) - 1;
            }

            matchBits |= (ulong)mask << batch;
        }

        return matchBits;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong EvaluateUInt64(ref FieldEvaluator eval, byte* compBase, int compSize, int clusterSize)
    {
        // XOR with sign bit for unsigned→signed comparison (matches zone map encoding)
        long threshold = eval.Threshold ^ long.MinValue;
        var thrVec = Vector256.Create(threshold);
        var signBit = Vector256.Create(long.MinValue);
        int fieldOffset = eval.FieldOffset;
        long* baseAddr = (long*)(compBase + fieldOffset);

        var strideVec = Vector128.Create(0, compSize, compSize * 2, compSize * 3);

        ulong matchBits = 0;
        for (int batch = 0; batch < clusterSize; batch += 4)
        {
            var offsetVec = Vector128.Create(batch * compSize);
            var indices = Sse2.Add(strideVec, offsetVec);
            var gathered = Avx2.GatherVector256(baseAddr, indices, 1);

            // XOR with sign bit to convert unsigned→signed
            var signedGathered = Avx2.Xor(gathered, signBit);

            uint mask = CompareMask64(signedGathered, thrVec, eval.CompareOp);

            int remaining = clusterSize - batch;
            if (remaining < 4)
            {
                mask &= (1u << remaining) - 1;
            }

            matchBits |= (ulong)mask << batch;
        }

        return matchBits;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong EvaluateFloat64(ref FieldEvaluator eval, byte* compBase, int compSize, int clusterSize)
    {
        double threshold = Unsafe.As<long, double>(ref eval.Threshold);
        var thrVec = Vector256.Create(threshold);
        int fieldOffset = eval.FieldOffset;
        double* baseAddr = (double*)(compBase + fieldOffset);

        var strideVec = Vector128.Create(0, compSize, compSize * 2, compSize * 3);

        ulong matchBits = 0;
        for (int batch = 0; batch < clusterSize; batch += 4)
        {
            var offsetVec = Vector128.Create(batch * compSize);
            var indices = Sse2.Add(strideVec, offsetVec);
            var gathered = Avx2.GatherVector256(baseAddr, indices, 1);

            uint mask = CompareFloatMask64(gathered, thrVec, eval.CompareOp);

            int remaining = clusterSize - batch;
            if (remaining < 4)
            {
                mask &= (1u << remaining) - 1;
            }

            matchBits |= (ulong)mask << batch;
        }

        return matchBits;
    }

    // ─── Comparison helpers ──────────────────────────────────────────────

    /// <summary>Compare 8 int32 values against threshold, return 8-bit mask (1 bit per lane).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CompareMask32(Vector256<int> values, Vector256<int> threshold, CompareOp op)
    {
        Vector256<int> result;
        switch (op)
        {
            case CompareOp.Equal:
                result = Avx2.CompareEqual(values, threshold);
                break;
            case CompareOp.NotEqual:
                result = Avx2.Xor(Avx2.CompareEqual(values, threshold), Vector256.Create(-1));
                break;
            case CompareOp.GreaterThan:
                result = Avx2.CompareGreaterThan(values, threshold);
                break;
            case CompareOp.LessThan:
                // LT(a, b) = GT(b, a)
                result = Avx2.CompareGreaterThan(threshold, values);
                break;
            case CompareOp.GreaterThanOrEqual:
                // GTE = NOT LT = NOT GT(threshold, values)
                result = Avx2.Xor(Avx2.CompareGreaterThan(threshold, values), Vector256.Create(-1));
                break;
            case CompareOp.LessThanOrEqual:
                // LTE = NOT GT
                result = Avx2.Xor(Avx2.CompareGreaterThan(values, threshold), Vector256.Create(-1));
                break;
            default:
                return 0;
        }

        // ExtractMostSignificantBits on int32 lanes: returns 8 bits (one per 32-bit lane)
        return (uint)Avx.MoveMask(result.AsSingle());
    }

    /// <summary>Compare 4 int64 values against threshold, return 4-bit mask.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CompareMask64(Vector256<long> values, Vector256<long> threshold, CompareOp op)
    {
        Vector256<long> result;
        switch (op)
        {
            case CompareOp.Equal:
                result = Avx2.CompareEqual(values, threshold);
                break;
            case CompareOp.NotEqual:
                result = Avx2.Xor(Avx2.CompareEqual(values, threshold), Vector256.Create(-1L));
                break;
            case CompareOp.GreaterThan:
                result = Avx2.CompareGreaterThan(values, threshold);
                break;
            case CompareOp.LessThan:
                result = Avx2.CompareGreaterThan(threshold, values);
                break;
            case CompareOp.GreaterThanOrEqual:
                result = Avx2.Xor(Avx2.CompareGreaterThan(threshold, values), Vector256.Create(-1L));
                break;
            case CompareOp.LessThanOrEqual:
                result = Avx2.Xor(Avx2.CompareGreaterThan(values, threshold), Vector256.Create(-1L));
                break;
            default:
                return 0;
        }

        // MoveMask on double lanes: returns 4 bits (one per 64-bit lane)
        return (uint)Avx.MoveMask(result.AsDouble());
    }

    /// <summary>Compare 8 float32 values with IEEE 754 ordered comparison, return 8-bit mask.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CompareFloatMask32(Vector256<float> values, Vector256<float> threshold, CompareOp op)
    {
        // AVX float comparison predicates (ordered, non-signaling)
        Vector256<float> result = op switch
        {
            CompareOp.Equal => Avx.Compare(values, threshold, FloatComparisonMode.OrderedEqualNonSignaling),
            CompareOp.NotEqual => Avx.Compare(values, threshold, FloatComparisonMode.OrderedNotEqualNonSignaling),
            CompareOp.GreaterThan => Avx.Compare(values, threshold, FloatComparisonMode.OrderedGreaterThanNonSignaling),
            CompareOp.LessThan => Avx.Compare(values, threshold, FloatComparisonMode.OrderedLessThanNonSignaling),
            CompareOp.GreaterThanOrEqual => Avx.Compare(values, threshold, FloatComparisonMode.OrderedGreaterThanOrEqualNonSignaling),
            CompareOp.LessThanOrEqual => Avx.Compare(values, threshold, FloatComparisonMode.OrderedLessThanOrEqualNonSignaling),
            _ => Vector256<float>.Zero
        };

        return (uint)Avx.MoveMask(result);
    }

    /// <summary>Compare 4 float64 values with IEEE 754 ordered comparison, return 4-bit mask.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CompareFloatMask64(Vector256<double> values, Vector256<double> threshold, CompareOp op)
    {
        Vector256<double> result = op switch
        {
            CompareOp.Equal => Avx.Compare(values, threshold, FloatComparisonMode.OrderedEqualNonSignaling),
            CompareOp.NotEqual => Avx.Compare(values, threshold, FloatComparisonMode.OrderedNotEqualNonSignaling),
            CompareOp.GreaterThan => Avx.Compare(values, threshold, FloatComparisonMode.OrderedGreaterThanNonSignaling),
            CompareOp.LessThan => Avx.Compare(values, threshold, FloatComparisonMode.OrderedLessThanNonSignaling),
            CompareOp.GreaterThanOrEqual => Avx.Compare(values, threshold, FloatComparisonMode.OrderedGreaterThanOrEqualNonSignaling),
            CompareOp.LessThanOrEqual => Avx.Compare(values, threshold, FloatComparisonMode.OrderedLessThanOrEqualNonSignaling),
            _ => Vector256<double>.Zero
        };

        return (uint)Avx.MoveMask(result);
    }

    // ─── AVX-512: 32-bit types (16 elements via 2× AVX2 gather → Vector512 compare) ─────────────
    // .NET does not expose 512-bit gather intrinsics. Instead, we use two AVX2 256-bit gathers
    // and combine into Vector512 for the comparison step. The JIT emits native AVX-512 vpcmpd
    // for the comparison, giving us 16-wide mask output in a single instruction.
    // On Zen 4 (double-pumped 512-bit), throughput is identical to 2× AVX2 compare, but with
    // fewer loop iterations and a single 16-bit mask extraction.

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong EvaluateInt32_512(ref FieldEvaluator eval, byte* compBase, int compSize, int clusterSize)
    {
        int threshold = (int)eval.Threshold;
        var thrVec = Vector512.Create(threshold);
        int* baseAddr = (int*)(compBase + eval.FieldOffset);
        var stride256 = Vector256.Create(
            0, compSize, compSize * 2, compSize * 3, compSize * 4, compSize * 5, compSize * 6, compSize * 7);

        ulong matchBits = 0;
        for (int batch = 0; batch < clusterSize; batch += 16)
        {
            // Two 256-bit gathers → one 512-bit vector
            var offLo = Vector256.Create(batch * compSize);
            var offHi = Vector256.Create((batch + 8) * compSize);
            var lo = Avx2.GatherVector256(baseAddr, Avx2.Add(stride256, offLo), 1);
            var hi = Avx2.GatherVector256(baseAddr, Avx2.Add(stride256, offHi), 1);
            var gathered = Vector512.Create(lo, hi);

            var cmp = Avx512F.CompareGreaterThan(gathered, thrVec); // base comparison
            uint mask = CompareMask32_512(gathered, thrVec, cmp, eval.CompareOp);

            int remaining = clusterSize - batch;
            if (remaining < 16)
            {
                mask &= (1u << remaining) - 1;
            }
            matchBits |= (ulong)mask << batch;
        }
        return matchBits;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong EvaluateUInt32_512(ref FieldEvaluator eval, byte* compBase, int compSize, int clusterSize)
    {
        // AVX-512 has native unsigned compare — no XOR sign-bit trick needed
        uint threshold = (uint)eval.Threshold;
        var thrVec = Vector512.Create(threshold);
        int* baseAddr = (int*)(compBase + eval.FieldOffset);
        var stride256 = Vector256.Create(
            0, compSize, compSize * 2, compSize * 3, compSize * 4, compSize * 5, compSize * 6, compSize * 7);

        ulong matchBits = 0;
        for (int batch = 0; batch < clusterSize; batch += 16)
        {
            var offLo = Vector256.Create(batch * compSize);
            var offHi = Vector256.Create((batch + 8) * compSize);
            var lo = Avx2.GatherVector256(baseAddr, Avx2.Add(stride256, offLo), 1);
            var hi = Avx2.GatherVector256(baseAddr, Avx2.Add(stride256, offHi), 1);
            var gathered = Vector512.Create(lo, hi).AsUInt32();

            uint mask = CompareMaskUInt32_512(gathered, thrVec, eval.CompareOp);

            int remaining = clusterSize - batch;
            if (remaining < 16)
            {
                mask &= (1u << remaining) - 1;
            }
            matchBits |= (ulong)mask << batch;
        }
        return matchBits;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong EvaluateFloat32_512(ref FieldEvaluator eval, byte* compBase, int compSize, int clusterSize)
    {
        int bits = (int)eval.Threshold;
        float threshold = Unsafe.As<int, float>(ref bits);
        var thrVec = Vector512.Create(threshold);
        float* baseAddr = (float*)(compBase + eval.FieldOffset);
        var stride256 = Vector256.Create(
            0, compSize, compSize * 2, compSize * 3, compSize * 4, compSize * 5, compSize * 6, compSize * 7);

        ulong matchBits = 0;
        for (int batch = 0; batch < clusterSize; batch += 16)
        {
            var offLo = Vector256.Create(batch * compSize);
            var offHi = Vector256.Create((batch + 8) * compSize);
            var lo = Avx2.GatherVector256(baseAddr, Avx2.Add(stride256, offLo), 1);
            var hi = Avx2.GatherVector256(baseAddr, Avx2.Add(stride256, offHi), 1);
            var gathered = Vector512.Create(lo, hi);

            uint mask = CompareFloatMask32_512(gathered, thrVec, eval.CompareOp);

            int remaining = clusterSize - batch;
            if (remaining < 16)
            {
                mask &= (1u << remaining) - 1;
            }
            matchBits |= (ulong)mask << batch;
        }
        return matchBits;
    }

    // ─── AVX-512: 64-bit types (8 elements via 2× AVX2 gather → Vector512 compare) ───────────

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong EvaluateInt64_512(ref FieldEvaluator eval, byte* compBase, int compSize, int clusterSize)
    {
        long threshold = eval.Threshold;
        var thrVec = Vector512.Create(threshold);
        long* baseAddr = (long*)(compBase + eval.FieldOffset);
        var stride128 = Vector128.Create(0, compSize, compSize * 2, compSize * 3);

        ulong matchBits = 0;
        for (int batch = 0; batch < clusterSize; batch += 8)
        {
            var offLo = Vector128.Create(batch * compSize);
            var offHi = Vector128.Create((batch + 4) * compSize);
            var lo = Avx2.GatherVector256(baseAddr, Sse2.Add(stride128, offLo), 1);
            var hi = Avx2.GatherVector256(baseAddr, Sse2.Add(stride128, offHi), 1);
            var gathered = Vector512.Create(lo, hi);

            var cmp = Avx512F.CompareGreaterThan(gathered, thrVec);
            uint mask = CompareMask64_512(gathered, thrVec, cmp, eval.CompareOp);

            int remaining = clusterSize - batch;
            if (remaining < 8)
            {
                mask &= (1u << remaining) - 1;
            }
            matchBits |= (ulong)mask << batch;
        }
        return matchBits;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong EvaluateUInt64_512(ref FieldEvaluator eval, byte* compBase, int compSize, int clusterSize)
    {
        // AVX-512 has native unsigned compare
        ulong threshold = (ulong)eval.Threshold;
        var thrVec = Vector512.Create(threshold);
        long* baseAddr = (long*)(compBase + eval.FieldOffset);
        var stride128 = Vector128.Create(0, compSize, compSize * 2, compSize * 3);

        ulong matchBits = 0;
        for (int batch = 0; batch < clusterSize; batch += 8)
        {
            var offLo = Vector128.Create(batch * compSize);
            var offHi = Vector128.Create((batch + 4) * compSize);
            var lo = Avx2.GatherVector256(baseAddr, Sse2.Add(stride128, offLo), 1);
            var hi = Avx2.GatherVector256(baseAddr, Sse2.Add(stride128, offHi), 1);
            var gathered = Vector512.Create(lo, hi).AsUInt64();

            uint mask = CompareMaskUInt64_512(gathered, thrVec, eval.CompareOp);

            int remaining = clusterSize - batch;
            if (remaining < 8)
            {
                mask &= (1u << remaining) - 1;
            }
            matchBits |= (ulong)mask << batch;
        }
        return matchBits;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong EvaluateFloat64_512(ref FieldEvaluator eval, byte* compBase, int compSize, int clusterSize)
    {
        double threshold = Unsafe.As<long, double>(ref eval.Threshold);
        var thrVec = Vector512.Create(threshold);
        double* baseAddr = (double*)(compBase + eval.FieldOffset);
        var stride128 = Vector128.Create(0, compSize, compSize * 2, compSize * 3);

        ulong matchBits = 0;
        for (int batch = 0; batch < clusterSize; batch += 8)
        {
            var offLo = Vector128.Create(batch * compSize);
            var offHi = Vector128.Create((batch + 4) * compSize);
            var lo = Avx2.GatherVector256(baseAddr, Sse2.Add(stride128, offLo), 1);
            var hi = Avx2.GatherVector256(baseAddr, Sse2.Add(stride128, offHi), 1);
            var gathered = Vector512.Create(lo, hi);

            uint mask = CompareFloatMask64_512(gathered, thrVec, eval.CompareOp);

            int remaining = clusterSize - batch;
            if (remaining < 8)
            {
                mask &= (1u << remaining) - 1;
            }
            matchBits |= (ulong)mask << batch;
        }
        return matchBits;
    }

    // ─── AVX-512 comparison helpers ──────────────────────────────────────
    // Avx512F.CompareXxx returns Vector512 (all-ones/zeros per lane). ExtractMostSignificantBits gives the uint mask.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CompareMask32_512(Vector512<int> values, Vector512<int> threshold, Vector512<int> gtResult, CompareOp op)
    {
        Vector512<int> result = op switch
        {
            CompareOp.Equal => Avx512F.CompareEqual(values, threshold),
            CompareOp.NotEqual => Avx512F.CompareNotEqual(values, threshold),
            CompareOp.GreaterThan => gtResult,
            CompareOp.LessThan => Avx512F.CompareLessThan(values, threshold),
            CompareOp.GreaterThanOrEqual => Avx512F.CompareGreaterThanOrEqual(values, threshold),
            CompareOp.LessThanOrEqual => Avx512F.CompareLessThanOrEqual(values, threshold),
            _ => Vector512<int>.Zero
        };
        return (uint)result.ExtractMostSignificantBits();
    }

    /// <summary>Native unsigned compare — AVX-512 supports this directly, no XOR trick needed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CompareMaskUInt32_512(Vector512<uint> values, Vector512<uint> threshold, CompareOp op)
    {
        Vector512<uint> result = op switch
        {
            CompareOp.Equal => Avx512F.CompareEqual(values, threshold),
            CompareOp.NotEqual => Avx512F.CompareNotEqual(values, threshold),
            CompareOp.GreaterThan => Avx512F.CompareGreaterThan(values, threshold),
            CompareOp.LessThan => Avx512F.CompareLessThan(values, threshold),
            CompareOp.GreaterThanOrEqual => Avx512F.CompareGreaterThanOrEqual(values, threshold),
            CompareOp.LessThanOrEqual => Avx512F.CompareLessThanOrEqual(values, threshold),
            _ => Vector512<uint>.Zero
        };
        return (uint)result.ExtractMostSignificantBits();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CompareMask64_512(Vector512<long> values, Vector512<long> threshold, Vector512<long> gtResult, CompareOp op)
    {
        Vector512<long> result = op switch
        {
            CompareOp.Equal => Avx512F.CompareEqual(values, threshold),
            CompareOp.NotEqual => Avx512F.CompareNotEqual(values, threshold),
            CompareOp.GreaterThan => gtResult,
            CompareOp.LessThan => Avx512F.CompareLessThan(values, threshold),
            CompareOp.GreaterThanOrEqual => Avx512F.CompareGreaterThanOrEqual(values, threshold),
            CompareOp.LessThanOrEqual => Avx512F.CompareLessThanOrEqual(values, threshold),
            _ => Vector512<long>.Zero
        };
        return (uint)result.ExtractMostSignificantBits();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CompareMaskUInt64_512(Vector512<ulong> values, Vector512<ulong> threshold, CompareOp op)
    {
        Vector512<ulong> result = op switch
        {
            CompareOp.Equal => Avx512F.CompareEqual(values, threshold),
            CompareOp.NotEqual => Avx512F.CompareNotEqual(values, threshold),
            CompareOp.GreaterThan => Avx512F.CompareGreaterThan(values, threshold),
            CompareOp.LessThan => Avx512F.CompareLessThan(values, threshold),
            CompareOp.GreaterThanOrEqual => Avx512F.CompareGreaterThanOrEqual(values, threshold),
            CompareOp.LessThanOrEqual => Avx512F.CompareLessThanOrEqual(values, threshold),
            _ => Vector512<ulong>.Zero
        };
        return (uint)result.ExtractMostSignificantBits();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CompareFloatMask32_512(Vector512<float> values, Vector512<float> threshold, CompareOp op)
    {
        // Use Avx512F.Compare with FloatComparisonMode for ordered IEEE 754 semantics
        Vector512<float> result = op switch
        {
            CompareOp.Equal => Avx512F.Compare(values, threshold, FloatComparisonMode.OrderedEqualNonSignaling),
            CompareOp.NotEqual => Avx512F.Compare(values, threshold, FloatComparisonMode.OrderedNotEqualNonSignaling),
            CompareOp.GreaterThan => Avx512F.Compare(values, threshold, FloatComparisonMode.OrderedGreaterThanNonSignaling),
            CompareOp.LessThan => Avx512F.Compare(values, threshold, FloatComparisonMode.OrderedLessThanNonSignaling),
            CompareOp.GreaterThanOrEqual => Avx512F.Compare(values, threshold, FloatComparisonMode.OrderedGreaterThanOrEqualNonSignaling),
            CompareOp.LessThanOrEqual => Avx512F.Compare(values, threshold, FloatComparisonMode.OrderedLessThanOrEqualNonSignaling),
            _ => Vector512<float>.Zero
        };
        return (uint)result.ExtractMostSignificantBits();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CompareFloatMask64_512(Vector512<double> values, Vector512<double> threshold, CompareOp op)
    {
        Vector512<double> result = op switch
        {
            CompareOp.Equal => Avx512F.Compare(values, threshold, FloatComparisonMode.OrderedEqualNonSignaling),
            CompareOp.NotEqual => Avx512F.Compare(values, threshold, FloatComparisonMode.OrderedNotEqualNonSignaling),
            CompareOp.GreaterThan => Avx512F.Compare(values, threshold, FloatComparisonMode.OrderedGreaterThanNonSignaling),
            CompareOp.LessThan => Avx512F.Compare(values, threshold, FloatComparisonMode.OrderedLessThanNonSignaling),
            CompareOp.GreaterThanOrEqual => Avx512F.Compare(values, threshold, FloatComparisonMode.OrderedGreaterThanOrEqualNonSignaling),
            CompareOp.LessThanOrEqual => Avx512F.Compare(values, threshold, FloatComparisonMode.OrderedLessThanOrEqualNonSignaling),
            _ => Vector512<double>.Zero
        };
        return (uint)result.ExtractMostSignificantBits();
    }

    // ─── Scalar fallback ─────────────────────────────────────────────────

    /// <summary>
    /// Scalar evaluation of all slots in a cluster. Used when AVX2 is unavailable or the field type is not SIMD-eligible. Iterates ALL slots 0..clusterSize-1,
    /// returns match bitmask (caller ANDs with occupancy).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong EvaluateScalarAll(ref FieldEvaluator eval, byte* compBase, int compSize, int clusterSize)
    {
        ulong matchBits = 0;
        int fieldOffset = eval.FieldOffset;
        for (int i = 0; i < clusterSize; i++)
        {
            byte* fieldPtr = compBase + i * compSize + fieldOffset;
            if (FieldEvaluator.Evaluate(ref eval, fieldPtr))
            {
                matchBits |= 1UL << i;
            }
        }

        return matchBits;
    }
}
