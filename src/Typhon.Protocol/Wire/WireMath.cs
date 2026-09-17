using System;
using System.Runtime.CompilerServices;

namespace Typhon.Protocol;

/// <summary>
/// The codec arithmetic of 03-wire-protocol § 12 (decisions W1–W10): quantization, normalized values, angles, smallest-three quaternions, half floats and
/// low-bit ticks, written so that C#, the TypeScript SDK and the engine produce bit-identical integer codes and bit-identical decoded doubles.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rules that make that true (W1).</b> All arithmetic is binary64: a float32 input is widened first (exact) and a result is narrowed only after
/// decoding. Ties round half away from zero — never <see cref="Math.Round(double)"/>'s default to-even, never <c>floor(x + 0.5)</c>. Values are clamped in
/// double before any integer conversion. Only <c>+ − × ÷</c>, <see cref="Math.Sqrt"/>, <see cref="Math.Floor(double)"/>, <see cref="Math.Abs(double)"/> and
/// comparisons appear; powers of two come from <see cref="Pow2"/>, built by doubling, never from <see cref="Math.Pow"/>. Nothing here may call
/// <see cref="Math.FusedMultiplyAdd"/> or a <c>MultiplyAddEstimate</c>: a fused result differs in the last bit from the TypeScript one.
/// </para>
/// <para>
/// Operators evaluate left to right, one IEEE operation each, exactly as the formulas in § 12 are written. Reordering a product "for speed" changes the last
/// bit and breaks the golden vectors — that is the point of having them.
/// </para>
/// </remarks>
public static class WireMath
{
    /// <summary>2π, as C#'s <see cref="Math.Tau"/> and TypeScript's <c>2 * Math.PI</c> both spell it.</summary>
    public const double Tau = 6.283185307179586;

    /// <summary>√½, identical to TypeScript's <c>Math.SQRT1_2</c>.</summary>
    public static readonly double Sqrt1Over2 = Math.Sqrt(0.5);

    private static readonly double[] Powers = BuildPowers();

    /// <summary>2ᵇ as an exact double, for b in [0, 64].</summary>
    /// <param name="b">The exponent.</param>
    /// <returns>2ᵇ.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Pow2(int b) => Powers[b];

    /// <summary>Rounds half away from zero (<c>rha</c>).</summary>
    /// <param name="x">The value.</param>
    /// <returns>The nearest integer, ties away from zero.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double RoundHalfAwayFromZero(double x) => Math.Round(x, MidpointRounding.AwayFromZero);

    // ---- quant, pos (W2, W3) ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>Encodes a <c>quant</c> value (or one <c>pos</c> axis) over [min, max) at <paramref name="bits"/> bits: step = (max − min) / 2ᵇ.</summary>
    /// <param name="v">The value; NaN, −∞ and anything below min encode as 0, anything at or above max − step/2 clamps to the top code.</param>
    /// <param name="min">The declared minimum (inclusive).</param>
    /// <param name="max">The declared maximum (exclusive).</param>
    /// <param name="bits">8, 16, 24 or 32.</param>
    /// <returns>The code, in [0, 2ᵇ − 1].</returns>
    public static uint EncodeQuant(double v, double min, double max, int bits)
    {
        var step = (max - min) / Pow2(bits);
        var x = (v - min) / step;
        if (!(x > 0))
        {
            return 0;
        }

        var top = Pow2(bits) - 1;
        var r = RoundHalfAwayFromZero(x);
        return (uint)(r > top ? top : r);
    }

    /// <summary>Decodes a <c>quant</c> code: <c>min + q × step</c>. Never clamps.</summary>
    /// <param name="q">The code.</param>
    /// <param name="min">The declared minimum.</param>
    /// <param name="max">The declared maximum.</param>
    /// <param name="bits">8, 16, 24 or 32.</param>
    /// <returns>The value, in [min, max − step].</returns>
    public static double DecodeQuant(uint q, double min, double max, int bits)
    {
        var step = (max - min) / Pow2(bits);
        return min + q * step;
    }

    /// <summary>Decodes a <c>quant</c> code with its step precomputed by <see cref="QuantStep"/>: the same operations, so the same bits.</summary>
    /// <param name="q">The code.</param>
    /// <param name="min">The declared minimum.</param>
    /// <param name="step">(max − min) / 2ᵇ.</param>
    /// <returns>The value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double DecodeQuantWithStep(uint q, double min, double step) => min + q * step;

    /// <summary>The quantization step of a <c>quant</c> or <c>pos</c> axis: (max − min) / 2ᵇ.</summary>
    /// <param name="min">The declared minimum.</param>
    /// <param name="max">The declared maximum.</param>
    /// <param name="bits">8, 16, 24 or 32.</param>
    /// <returns>The step.</returns>
    public static double QuantStep(double min, double max, int bits) => (max - min) / Pow2(bits);

    // ---- vec (W4) ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Encodes one <c>vec</c> component: <c>scale</c> is one step; signed, clamped symmetrically to ±(2ᵇ⁻¹ − 1).</summary>
    /// <param name="v">The value; NaN encodes as 0.</param>
    /// <param name="scale">The size of one step.</param>
    /// <param name="bits">8, 16, 24 or 32.</param>
    /// <returns>The signed code.</returns>
    public static int EncodeVec(double v, double scale, int bits)
    {
        if (double.IsNaN(v))
        {
            return 0;
        }

        return ClampSymmetric(RoundHalfAwayFromZero(v / scale), bits);
    }

    /// <summary>Decodes one <c>vec</c> component; the code −2ᵇ⁻¹ reads as −(2ᵇ⁻¹ − 1).</summary>
    /// <param name="q">The signed code.</param>
    /// <param name="scale">The size of one step.</param>
    /// <param name="bits">8, 16, 24 or 32.</param>
    /// <returns>The value.</returns>
    public static double DecodeVec(int q, double scale, int bits) => ClampLow(q, bits) * scale;

    // ---- vel (W5) ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Encodes one <c>vel</c> axis: displacement per tick in units of the axis' position step ÷ <paramref name="quantaDiv"/>, signed, symmetric clamp.
    /// </summary>
    /// <param name="d">Displacement per tick, in world units; NaN encodes as 0.</param>
    /// <param name="posStep">The linked position codec's step on this axis.</param>
    /// <param name="quantaDiv">The divisor, an integer ≥ 1.</param>
    /// <param name="bits">8, 16, 24 or 32.</param>
    /// <returns>The signed code.</returns>
    public static int EncodeVel(double d, double posStep, int quantaDiv, int bits)
    {
        var x = d / posStep * quantaDiv;
        if (double.IsNaN(x))
        {
            return 0;
        }

        return ClampSymmetric(RoundHalfAwayFromZero(x), bits);
    }

    /// <summary>Decodes one <c>vel</c> axis to world units per tick: <c>(q × posStep) / quantaDiv</c>.</summary>
    /// <param name="q">The signed code.</param>
    /// <param name="posStep">The linked position codec's step on this axis.</param>
    /// <param name="quantaDiv">The divisor.</param>
    /// <param name="bits">8, 16, 24 or 32.</param>
    /// <returns>Displacement per tick.</returns>
    public static double DecodeVel(int q, double posStep, int quantaDiv, int bits) => ClampLow(q, bits) * posStep / quantaDiv;

    // ---- unorm, snorm (W6) ------------------------------------------------------------------------------------------------------------------------

    /// <summary>Encodes a <c>unorm</c>: q = rhu(v × (2ᵇ − 1)), clamped to [0, 2ᵇ − 1].</summary>
    /// <param name="v">The value; NaN and anything ≤ 0 encode as 0.</param>
    /// <param name="bits">8, 16, 24 or 32.</param>
    /// <returns>The code.</returns>
    public static uint EncodeUnorm(double v, int bits)
    {
        var top = Pow2(bits) - 1;
        var x = v * top;
        if (!(x > 0))
        {
            return 0;
        }

        var r = RoundHalfAwayFromZero(x);
        return (uint)(r > top ? top : r);
    }

    /// <summary>Decodes a <c>unorm</c>: q / (2ᵇ − 1) — a division, not a multiplication by the reciprocal, which differs in the last bit.</summary>
    /// <param name="q">The code.</param>
    /// <param name="bits">8, 16, 24 or 32.</param>
    /// <returns>A value in [0, 1]; 0 and 1 exact.</returns>
    public static double DecodeUnorm(uint q, int bits) => q / (Pow2(bits) - 1);

    /// <summary>Encodes a <c>snorm</c>: q = rha(clamp(v, −1, 1) × (2ᵇ⁻¹ − 1)).</summary>
    /// <param name="v">The value; NaN encodes as 0.</param>
    /// <param name="bits">8, 16, 24 or 32.</param>
    /// <returns>The signed code.</returns>
    public static int EncodeSnorm(double v, int bits)
    {
        if (double.IsNaN(v))
        {
            return 0;
        }

        var c = v < -1 ? -1 : v > 1 ? 1 : v;
        return (int)RoundHalfAwayFromZero(c * (Pow2(bits - 1) - 1));
    }

    /// <summary>Decodes a <c>snorm</c>: max(q / (2ᵇ⁻¹ − 1), −1).</summary>
    /// <param name="q">The signed code.</param>
    /// <param name="bits">8, 16, 24 or 32.</param>
    /// <returns>A value in [−1, 1]; −1, 0 and 1 exact.</returns>
    public static double DecodeSnorm(int q, int bits)
    {
        var x = q / (Pow2(bits - 1) - 1);
        return x < -1 ? -1 : x;
    }

    // ---- angle (W7) -------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Encodes an <c>angle</c> as a two's-complement code over [−π, π).</summary>
    /// <param name="theta">Radians; non-finite values, and values whose code would exceed 2⁵³ in magnitude, encode as 0.</param>
    /// <param name="bits">8, 16, 24 or 32.</param>
    /// <returns>The signed code, in [−2ᵇ⁻¹, 2ᵇ⁻¹).</returns>
    public static int EncodeAngle(double theta, int bits)
    {
        if (!double.IsFinite(theta))
        {
            return 0;
        }

        var k = RoundHalfAwayFromZero(theta * Pow2(bits) / Tau);
        if (!(Math.Abs(k) <= 9007199254740992.0))
        {
            return 0;
        }

        var q = k - Pow2(bits) * Math.Floor((k + Pow2(bits - 1)) / Pow2(bits));
        return (int)q;
    }

    /// <summary>Decodes an <c>angle</c>: (q × 2π) / 2ᵇ.</summary>
    /// <param name="q">The signed code.</param>
    /// <param name="bits">8, 16, 24 or 32.</param>
    /// <returns>Radians in [−π, π).</returns>
    public static double DecodeAngle(int q, int bits) => q * Tau / Pow2(bits);

    // ---- quat3 (W8) -------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Encodes a rotation as smallest-three in 32 bits: index in bits 0–1, then three 10-bit signed components, ascending axis order.</summary>
    /// <param name="x">Quaternion x.</param>
    /// <param name="y">Quaternion y.</param>
    /// <param name="z">Quaternion z.</param>
    /// <param name="w">Quaternion w.</param>
    /// <returns>The 32-bit code.</returns>
    public static uint EncodeQuat3(double x, double y, double z, double w)
    {
        var n = Math.Sqrt(x * x + y * y + z * z + w * w);
        Span<double> c = stackalloc double[4];
        if (!(n > 0) || double.IsPositiveInfinity(n))
        {
            c[0] = 0;
            c[1] = 0;
            c[2] = 0;
            c[3] = 1;
        }
        else
        {
            c[0] = x / n;
            c[1] = y / n;
            c[2] = z / n;
            c[3] = w / n;
        }

        var largest = 0;
        for (var k = 1; k < 4; k++)
        {
            if (Math.Abs(c[k]) > Math.Abs(c[largest]))
            {
                largest = k;
            }
        }

        if (c[largest] < 0)
        {
            for (var k = 0; k < 4; k++)
            {
                c[k] = -c[k];
            }
        }

        var bits = (uint)largest;
        var shift = 2;
        for (var k = 0; k < 4; k++)
        {
            if (k == largest)
            {
                continue;
            }

            var r = RoundHalfAwayFromZero(c[k] / Sqrt1Over2 * 511);
            var e = (int)(r > 511 ? 511 : r < -511 ? -511 : r);
            bits |= ((uint)e & 0x3FF) << shift;
            shift += 10;
        }

        return bits;
    }

    /// <summary>Decodes a smallest-three rotation into <paramref name="xyzw"/>.</summary>
    /// <param name="bits">The 32-bit code.</param>
    /// <param name="xyzw">Receives x, y, z, w.</param>
    public static void DecodeQuat3(uint bits, Span<double> xyzw)
    {
        var largest = (int)(bits & 3);
        var sum = 0.0;
        var shift = 2;
        for (var k = 0; k < 4; k++)
        {
            if (k == largest)
            {
                continue;
            }

            var e = (int)((bits >> shift) & 0x3FF);
            e = (e << 22) >> 22;
            if (e < -511)
            {
                e = -511;
            }

            var v = e / 511.0 * Sqrt1Over2;
            xyzw[k] = v;
            sum += v * v;
            shift += 10;
        }

        var rest = 1 - sum;
        xyzw[largest] = Math.Sqrt(rest > 0 ? rest : 0);
    }

    // ---- f16 (W10) --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The one NaN a decoder produces: bits <c>0x7FF8000000000000</c>. JavaScript has a single observable NaN, so a decoded NaN that kept its sign or payload
    /// could never be bit-exact across languages. <see cref="double.NaN"/> is not it — its sign bit is set.
    /// </summary>
    public static readonly double CanonicalNaN = BitConverter.Int64BitsToDouble(0x7FF8000000000000);

    /// <summary>Replaces any NaN with <see cref="CanonicalNaN"/>.</summary>
    /// <param name="value">A decoded value.</param>
    /// <returns>The value, or the canonical NaN.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double CanonicalizeNaN(double value) => double.IsNaN(value) ? CanonicalNaN : value;

    /// <summary>Converts a double to IEEE half bits directly (ties to even, overflow to infinity), with NaN canonicalized to <c>0x7E00</c>.</summary>
    /// <param name="value">The value, in its source precision.</param>
    /// <returns>The half's bits.</returns>
    public static ushort EncodeHalf(double value) => double.IsNaN(value) ? (ushort)0x7E00 : BitConverter.HalfToUInt16Bits((Half)value);

    /// <summary>Decodes IEEE half bits, widened exactly.</summary>
    /// <param name="bits">The half's bits.</param>
    /// <returns>The value; every NaN pattern decodes as <see cref="CanonicalNaN"/>.</returns>
    public static double DecodeHalf(ushort bits) => CanonicalizeNaN((double)BitConverter.UInt16BitsToHalf(bits));

    /// <summary>The single-precision bits a value encodes to: narrowing ties to even, NaN canonicalized to <c>0x7FC00000</c>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The single's bits.</returns>
    public static uint EncodeSingle(double value) => double.IsNaN(value) ? 0x7FC00000u : BitConverter.SingleToUInt32Bits((float)value);

    // ---- tickLo (W9) ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Rebuilds an absolute tick from its low 16 bits against the frame's tick, assuming it is in the past and less than 2¹⁶ ticks old.</summary>
    /// <param name="low">The low 16 bits.</param>
    /// <param name="frameTick">The tick of the frame that carried it.</param>
    /// <returns>The absolute tick.</returns>
    public static uint DecodeTickLo(ushort low, uint frameTick) => frameTick - ((frameTick - low) & 0xFFFF);

    // ---- helpers ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The largest magnitude of a symmetric signed code: 2ᵇ⁻¹ − 1.</summary>
    /// <param name="bits">8, 16, 24 or 32.</param>
    /// <returns>The limit.</returns>
    public static int SymmetricLimit(int bits) => (int)(Pow2(bits - 1) - 1);

    private static int ClampSymmetric(double r, int bits)
    {
        var limit = Pow2(bits - 1) - 1;
        return (int)(r > limit ? limit : r < -limit ? -limit : r);
    }

    private static double ClampLow(int q, int bits)
    {
        var limit = Pow2(bits - 1) - 1;
        return q < -limit ? -limit : q;
    }

    private static double[] BuildPowers()
    {
        var powers = new double[65];
        powers[0] = 1;
        for (var i = 1; i < powers.Length; i++)
        {
            powers[i] = powers[i - 1] * 2;
        }

        return powers;
    }
}
