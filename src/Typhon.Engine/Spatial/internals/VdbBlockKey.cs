using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Packs a block's integer coordinates into the <see cref="long"/> key of the VDB grid's root map (#872 step 8, §3.1: "Block key packs integer block
/// coordinates at 21 bits per axis").
/// </summary>
/// <remarks>
/// <para>Three signed 21-bit fields, so each axis spans <c>[-1 048 576, 1 048 575]</c> blocks. At the default 16-cell block that is ±16.7 M cells per axis —
/// far past anything the current world-bounds clamp can produce, which is the point: the packing is what a later step needs in order to drop the clamp and
/// make the grid genuinely unbounded (§3.2). The clamp is still in force today, so negative block coordinates do not arise in practice; they are supported
/// and tested anyway (AC-8.3) because the encoding is the thing being fixed, not the caller that happens to use it.</para>
/// <para><b>Not Morton.</b> The root is a hash map, so key locality buys nothing — a hash destroys it either way. The design says "packs", and packing is
/// three shifts against an interleave's several.</para>
/// </remarks>
internal static class VdbBlockKey
{
    /// <summary>Bits per axis.</summary>
    private const int BitsPerAxis = 21;

    /// <summary>Most positive block coordinate on any axis.</summary>
    internal const int MaxCoord = (1 << (BitsPerAxis - 1)) - 1;

    /// <summary>Most negative block coordinate on any axis.</summary>
    internal const int MinCoord = -(1 << (BitsPerAxis - 1));

    private const long AxisMask = (1L << BitsPerAxis) - 1;

    /// <summary>Pack three block coordinates into one key. Throws when any axis is outside <see cref="MinCoord"/>..<see cref="MaxCoord"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Pack(int blockX, int blockY, int blockZ)
    {
        // Loud rather than silent: a truncated axis produces a key that collides with a real block somewhere else in the world, so the offending entity
        // would be filed into a cell belonging to a different region — an SQ-01 false negative for every query that does not happen to cover both.
        if (IsOutOfRange(blockX) || IsOutOfRange(blockY) || IsOutOfRange(blockZ))
        {
            ThrowOutOfRange(blockX, blockY, blockZ);
        }

        return ((blockX & AxisMask) << (2 * BitsPerAxis)) | ((blockY & AxisMask) << BitsPerAxis) | (blockZ & AxisMask);
    }

    /// <summary>Recover the three block coordinates a key was packed from.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (int x, int y, int z) Unpack(long key) => (SignExtend(key >> (2 * BitsPerAxis)), SignExtend(key >> BitsPerAxis), SignExtend(key));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsOutOfRange(int coord) => coord < MinCoord || coord > MaxCoord;

    /// <summary>Take the low <see cref="BitsPerAxis"/> bits and restore the sign bit — a left-then-arithmetic-right shift pair.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SignExtend(long field) => (int)((field & AxisMask) << (64 - BitsPerAxis) >> (64 - BitsPerAxis));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOutOfRange(int blockX, int blockY, int blockZ) =>
        throw new ArgumentOutOfRangeException(nameof(blockX),
            $"Block coordinates ({blockX}, {blockY}, {blockZ}) do not fit the VDB root key: each axis holds {BitsPerAxis} signed bits, "
            + $"i.e. [{MinCoord}, {MaxCoord}].");
}
