using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <see cref="NarrowphaseAabb2F"/>'s block kernels against the scalar loop they stand in for — <c>AabbClusterEnumerator.Drain</c> itself, over the same
/// column — entity for entity: on random worlds, and on the inputs where a SIMD rewrite usually drifts: NaN and infinite bounds, inverted boxes, boxes
/// touching the query exactly, NaN and infinite query bounds, coordinates at 2^36, denormals, a partial last block.
/// </summary>
/// <remarks>
/// The enumerator cannot be driven with most of these: it rejects a non-finite query box before its walk, and a spawn rejects a non-finite position. So the
/// real loop is run here directly, on a column laid out as a cluster whose spatial field starts at its base.
/// </remarks>
[TestFixture]
unsafe class NarrowphaseAabb2FTests
{
    private const string Marker = "disagrees with the scalar loop";

    private delegate ulong MaskFn(float* column, ulong bits, int blocks, in QueryGeometry query);

    /// <summary>A sink recording which slots the loop accepted.</summary>
    private struct MaskSink : AabbClusterEnumerator.IHitSink
    {
        public ulong Mask;

        public bool Hit(byte* clusterBase, int chunkId, int slot, int idsOffset, double minX, double minY, double minZ, double maxX, double maxY,
            double maxZ, double distSq)
        {
            Mask |= 1UL << slot;
            return true;
        }
    }

    /// <summary>The loop's decision: <c>AabbClusterEnumerator.Drain&lt;Aabb2FReader, _&gt;</c> over the slots of <paramref name="bits"/> in the first
    /// <paramref name="blocks"/> blocks — the slots the kernel reads.</summary>
    private static ulong LoopMatch(float* column, ulong bits, int blocks, in QueryGeometry query)
    {
        var layout = new ClusterFieldLayout(SpatialFieldType.AABB2F, fieldsOffset: 0, stride: NarrowphaseAabb2F.Stride, idsOffset: 0, aabb2FBlocks: 0);
        var inBlocks = blocks >= 4 ? ulong.MaxValue : (1UL << (blocks * NarrowphaseAabb2F.BlockSize)) - 1;
        var sink = new MaskSink();
        AabbClusterEnumerator.Drain<AabbClusterEnumerator.Aabb2FReader, MaskSink>(in layout, in query, (byte*)column, 0, bits & inBlocks, ref sink);
        return sink.Mask;
    }

    /// <summary>The same decision in the POSITIVE form (<c>minX &lt;= qMaxX</c> ...). It differs from the loop only on a NaN query bound — which is why the
    /// kernels are written as skip conditions, and what the mutant below leans on.</summary>
    private static bool PositiveFormMatch(AABB2F b, in QueryGeometry q)
    {
        if (SpatialGeometry.IsDegenerate(b))
        {
            return false;
        }

        double minX = b.MinX, minY = b.MinY, maxX = b.MaxX, maxY = b.MaxY;
        if (!(maxX >= q.MinX && minX <= q.MaxX && maxY >= q.MinY && minY <= q.MaxY))
        {
            return false;
        }

        if (q.RadiusSq > 0d)
        {
            var dx = double.MaxNative(0d, double.MaxNative(minX - q.CenterX, q.CenterX - maxX));
            var dy = double.MaxNative(0d, double.MaxNative(minY - q.CenterY, q.CenterY - maxY));
            if ((dx * dx) + (dy * dy) > q.RadiusSq)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The same decision with the query narrowed to f32 — the narrowing SQ-06 forbids. It differs from the loop where one f32 step is coarser than
    /// the gap between a bound and the query: at 2^36, one step is 8 192.</summary>
    private static bool NarrowedToF32Match(AABB2F b, in QueryGeometry q)
    {
        if (SpatialGeometry.IsDegenerate(b))
        {
            return false;
        }

        float qMinX = (float)q.MinX, qMinY = (float)q.MinY, qMaxX = (float)q.MaxX, qMaxY = (float)q.MaxY;
        if (b.MaxX < qMinX || b.MinX > qMaxX || b.MaxY < qMinY || b.MinY > qMaxY)
        {
            return false;
        }

        if (q.RadiusSq > 0d)
        {
            float cx = (float)q.CenterX, cy = (float)q.CenterY;
            var dx = float.MaxNative(0f, float.MaxNative(b.MinX - cx, cx - b.MaxX));
            var dy = float.MaxNative(0f, float.MaxNative(b.MinY - cy, cy - b.MaxY));
            if ((dx * dx) + (dy * dy) > (float)q.RadiusSq)
            {
                return false;
            }
        }

        return true;
    }

    private delegate bool Predicate(AABB2F b, in QueryGeometry q);

    private static MaskFn FromPredicate(Predicate match) =>
        (float* column, ulong bits, int blocks, in QueryGeometry query) =>
        {
            ulong mask = 0UL;
            for (var s = 0; s < blocks * NarrowphaseAabb2F.BlockSize; s++)
            {
                if ((bits & (1UL << s)) != 0UL && match(*(AABB2F*)(column + (s * 4)), in query))
                {
                    mask |= 1UL << s;
                }
            }

            return mask;
        };

    // ── Inputs ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly float[] AwkwardFloats =
    [
        0f, -0f, 1f, -1f, 5.5f, -7.25f, 1e-40f, float.Epsilon, float.MaxValue, -float.MaxValue, float.PositiveInfinity, float.NegativeInfinity, float.NaN,
        68_719_476_736f, -68_719_476_736f, 68_719_484_928f,
    ];

    private static readonly double[] AwkwardDoubles =
    [
        0d, -0d, 1d, -1d, 5.5d, -7.25d, double.PositiveInfinity, double.NegativeInfinity, double.NaN, 68_719_476_736d, -68_719_476_736d,
        Math.BitIncrement(68_719_476_736d), Math.BitDecrement(68_719_476_736d), 1e300, -1e300, 1e-310, -1e-310,
    ];

    private static float Awkward(Random rng) => AwkwardFloats[rng.Next(AwkwardFloats.Length)];

    private static double AwkwardD(Random rng) => AwkwardDoubles[rng.Next(AwkwardDoubles.Length)];

    /// <summary>
    /// One randomised case: a column of 64 entities around a centre, a quarter of them awkward, then a query whose bounds are drawn half the time from the
    /// entities' own coordinates so boxes touch it exactly.
    /// </summary>
    private static void FillCase(Random rng, float* column, out ulong bits, out int blocks, out QueryGeometry query)
    {
        var scale = rng.Next(4) switch { 0 => 1d, 1 => 1_000d, 2 => 68_719_476_736d, _ => 1e-3 };
        var centreX = (rng.NextDouble() - 0.5) * scale;
        var centreY = (rng.NextDouble() - 0.5) * scale;
        for (var s = 0; s < 64; s++)
        {
            var b = (AABB2F*)(column + (s * 4));
            if (rng.Next(4) == 0)
            {
                *b = new AABB2F { MinX = Awkward(rng), MinY = Awkward(rng), MaxX = Awkward(rng), MaxY = Awkward(rng) };
                continue;
            }

            var x = (float)(centreX + ((rng.NextDouble() - 0.5) * scale * 0.5));
            var y = (float)(centreY + ((rng.NextDouble() - 0.5) * scale * 0.5));
            var h = rng.Next(3) == 0 ? 0f : (float)(rng.NextDouble() * scale * 0.05);
            *b = new AABB2F { MinX = x - h, MinY = y - h, MaxX = x + h, MaxY = y + h };
        }

        bits = rng.Next(5) switch { 0 => ulong.MaxValue, 1 => 0UL, 2 => 1UL << rng.Next(64), _ => (ulong)rng.NextInt64() ^ ((ulong)rng.NextInt64() << 1) };
        blocks = 1 + rng.Next(4);

        double Edge(bool xAxis, double fallback)
        {
            switch (rng.Next(6))
            {
                case 0:
                    return AwkwardD(rng);
                case 1:
                case 2:
                    var e = (AABB2F*)(column + (rng.Next(64) * 4));
                    return xAxis ? (rng.Next(2) == 0 ? e->MinX : e->MaxX) : (rng.Next(2) == 0 ? e->MaxY : e->MinY);
                default:
                    return fallback;
            }
        }

        var half = rng.NextDouble() * scale * 0.4;
        var qMinX = Edge(true, centreX - half);
        var qMaxX = Edge(true, centreX + half);
        var qMinY = Edge(false, centreY - half);
        var qMaxY = Edge(false, centreY + half);
        var cx = rng.Next(8) == 0 ? AwkwardD(rng) : centreX;
        var cy = rng.Next(8) == 0 ? AwkwardD(rng) : centreY;
        var radiusSq = rng.Next(3) switch { 0 => 0d, 1 => half * half, _ => rng.Next(4) == 0 ? AwkwardD(rng) : half * half * rng.NextDouble() };
        query = new QueryGeometry(qMinX, qMinY, double.NegativeInfinity, qMaxX, qMaxY, double.PositiveInfinity, radiusSq, cx, cy, 0d);
    }

    /// <summary>Run <paramref name="cases"/> random cases through <paramref name="candidate"/> and the scalar loop; fail on the first disagreement.</summary>
    private static void AssertAgrees(string name, MaskFn candidate, int cases, int seed)
    {
        var column = (float*)NativeMemory.AlignedAlloc(64 * 16, 64);
        try
        {
            var rng = new Random(seed);
            for (var i = 0; i < cases; i++)
            {
                FillCase(rng, column, out var bits, out var blocks, out var query);
                var expected = LoopMatch(column, bits, blocks, in query);
                var actual = candidate(column, bits, blocks, in query);
                if (actual != expected)
                {
                    var diff = actual ^ expected;
                    var s = System.Numerics.BitOperations.TrailingZeroCount(diff);
                    var e = *(AABB2F*)(column + (s * 4));
                    Assert.Fail($"{name} {Marker} at case {i}, slot {s} (bit set by the loop: {(expected >> s) & 1}): entity [{e.MinX}, {e.MinY}]..[{e.MaxX}, "
                        + $"{e.MaxY}], query [{query.MinX}, {query.MinY}]..[{query.MaxX}, {query.MaxY}], centre ({query.CenterX}, {query.CenterY}), "
                        + $"r² {query.RadiusSq}, blocks {blocks}");
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(column);
        }
    }

    // ── Tests ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("SQ-03")]
    [VerifiesRule("SQ-06")]
    public void Avx512Kernel_AnswersWhatTheScalarLoopAnswers()
    {
        Assume.That(Avx512F.IsSupported, "no AVX-512 on this machine");
        AssertAgrees("AVX-512", NarrowphaseAabb2F.MatchAvx512, cases: 40_000, seed: 1);
    }

    [Test]
    [VerifiesRule("SQ-03")]
    [VerifiesRule("SQ-06")]
    public void Avx2Kernel_AnswersWhatTheScalarLoopAnswers()
    {
        Assume.That(Avx2.IsSupported, "no AVX2 on this machine");
        AssertAgrees("AVX2", NarrowphaseAabb2F.MatchAvx2, cases: 40_000, seed: 2);
    }

    /// <summary>The positive form agrees with the loop everywhere except on a NaN query bound; the cases above must include enough of those to tell.</summary>
    [Test]
    [RuleMutant("SQ-03")]
    public void APositiveFormPredicate_IsCaughtByTheComparison() =>
        RuleMutants.AssertDetects("SQ-03", Marker, () => AssertAgrees("positive form", FromPredicate(PositiveFormMatch), cases: 40_000, seed: 1));

    /// <summary>A kernel that narrowed the query to f32 agrees with the loop almost everywhere; the cases above must hold enough 2^36 edges to tell.</summary>
    [Test]
    [RuleMutant("SQ-06")]
    public void AQueryNarrowedToF32_IsCaughtByTheComparison() =>
        RuleMutants.AssertDetects("SQ-06", Marker, () => AssertAgrees("f32 query", FromPredicate(NarrowedToF32Match), cases: 40_000, seed: 1));

    [Test]
    public void Unshuffle_PutsEveryLaneBackInItsSlot()
    {
        ReadOnlySpan<int> laneToSlot = [0, 2, 4, 6, 1, 3, 5, 7];
        for (var laneMask = 0u; laneMask < 256u; laneMask++)
        {
            var expected = 0u;
            for (var lane = 0; lane < 8; lane++)
            {
                if ((laneMask & (1u << lane)) != 0u)
                {
                    expected |= 1u << laneToSlot[lane];
                }
            }

            Assert.That(NarrowphaseAabb2F.Unshuffle(laneMask), Is.EqualTo(expected), $"lane mask {laneMask:X2}");
        }
    }
}
