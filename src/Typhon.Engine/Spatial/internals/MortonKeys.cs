using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Typhon.Engine.Internals;

/// <summary>
/// Compile-time spatial grid configuration. Currently only the cell-key encoding strategy.
/// </summary>
/// <remarks>
/// <para>Using a <c>const bool</c> lets the JIT eliminate the non-selected branch entirely — Morton vs row-major cell keys is a zero-cost decision at runtime.</para>
/// </remarks>
internal static class SpatialConfig
{
    /// <summary>
    /// When true, cell keys use Morton (Z-order) interleaving via BMI2 PDEP/PEXT (with runtime fallback to row-major on non-BMI2 CPUs). When false, always row-major.
    /// </summary>
    public const bool UseMortonCellKeys = true;
}

/// <summary>
/// 2D Morton (Z-order) encode/decode with BMI2 PDEP/PEXT fast path and a row-major fallback.
/// </summary>
/// <remarks>
/// <para>Morton ordering places spatially adjacent cells at nearby array indices, which improves cache locality when iterating neighbouring cells
/// (multi-cell queries, checkerboard dispatch). The BMI2 fast path costs ~1 cycle per encode/decode on Zen/Haswell+ CPUs.</para>
/// <para>Inputs are clamped to 16 bits per axis (max 65 535). This is enough for a 65k x 65k cell grid — far larger than any realistic world size at typical
/// 100-unit cells.</para>
/// </remarks>
internal static class MortonKeys
{
    private const uint MaskEvenBits = 0x5555_5555u;  // 0101_0101... — positions for X
    private const uint MaskOddBits  = 0xAAAA_AAAAu;  // 1010_1010... — positions for Y

    /// <summary>
    /// Interleave the low 16 bits of <paramref name="x"/> and <paramref name="y"/> into a 32-bit Morton key.
    /// X bits go to even positions, Y bits go to odd positions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Encode2D(int x, int y)
    {
        if (Bmi2.IsSupported)
        {
            uint xBits = Bmi2.ParallelBitDeposit((uint)x, MaskEvenBits);
            uint yBits = Bmi2.ParallelBitDeposit((uint)y, MaskOddBits);
            return (int)(xBits | yBits);
        }

        return EncodeScalar2D(x, y);
    }

    /// <summary>
    /// Decode a 2D Morton key back into <c>(x, y)</c> cell coordinates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int x, int y) Decode2D(int key)
    {
        if (Bmi2.IsSupported)
        {
            int x = (int)Bmi2.ParallelBitExtract((uint)key, MaskEvenBits);
            int y = (int)Bmi2.ParallelBitExtract((uint)key, MaskOddBits);
            return (x, y);
        }

        return DecodeScalar2D(key);
    }

    /// <summary>
    /// Scalar Morton encode fallback — expands 16 bits to 32 bits by shifting and masking.
    /// Used on CPUs without BMI2 (pre-Haswell Intel, pre-Zen AMD).
    /// </summary>
    internal static int EncodeScalar2D(int x, int y)
    {
        return (int)(Part1By1((uint)x) | (Part1By1((uint)y) << 1));
    }

    /// <summary>
    /// Scalar Morton decode fallback.
    /// </summary>
    internal static (int x, int y) DecodeScalar2D(int key)
    {
        uint k = (uint)key;
        return ((int)Compact1By1(k), (int)Compact1By1(k >> 1));
    }

    // Expand 16 bits to 32 bits with zeros between each pair: abcdefgh -> 0a0b0c0d0e0f0g0h
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Part1By1(uint v)
    {
        v &= 0x0000_FFFFu;
        v = (v ^ (v << 8)) & 0x00FF_00FFu;
        v = (v ^ (v << 4)) & 0x0F0F_0F0Fu;
        v = (v ^ (v << 2)) & 0x3333_3333u;
        v = (v ^ (v << 1)) & 0x5555_5555u;
        return v;
    }

    // Compact 32 bits to 16 bits by removing odd-position bits: 0a0b0c0d0e0f0g0h -> abcdefgh
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Compact1By1(uint v)
    {
        v &= 0x5555_5555u;
        v = (v ^ (v >> 1)) & 0x3333_3333u;
        v = (v ^ (v >> 2)) & 0x0F0F_0F0Fu;
        v = (v ^ (v >> 4)) & 0x00FF_00FFu;
        v = (v ^ (v >> 8)) & 0x0000_FFFFu;
        return v;
    }
}
