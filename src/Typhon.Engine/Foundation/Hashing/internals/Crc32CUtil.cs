using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace Typhon.Engine.Internals;

/// <summary>
/// Hardware-accelerated CRC32C (Castagnoli polynomial 0x1EDC6F41) computation. Uses SSE4.2 CRC32 instruction on x86/x64, ARM CRC32C instructions on ARM64.
/// Falls back to software lookup table on unsupported platforms.
/// </summary>
/// <remarks>
/// <para>
/// <b>Important:</b> <c>System.IO.Hashing.Crc32</c> computes IEEE 802.3 CRC-32 (polynomial <c>0x04C11DB7</c>), which is <b>NOT</b> CRC32C. The
/// Castagnoli polynomial required for database checksums is only available via the SSE4.2/ARM hardware intrinsics directly.
/// </para>
/// <para>
/// Performance: ~670 ns per 8 KiB page as a single sequential chain on SSE4.2 x64 (measured, Zen 4 @ 4.5 GHz). The CRC32 instruction has
/// 3-cycle latency but 1-cycle throughput, so a single chain is <b>latency-bound</b> at ~8 bytes / 3 cycles and leaves most of the unit idle.
/// Splitting the same 8 KiB into independent regions breaks the dependency and is materially faster — 16 × 512 B measures ~500 ns — which is
/// why <see cref="PageSectorFooter"/> costs less than the whole-page checksum it replaces rather than more.
/// </para>
/// <para>
/// Detection strength is also better at smaller block sizes: CRC-32C holds HD=6 only up to 2045-byte datawords, so an 8 KiB dataword is HD=4
/// (all &lt;= 3-bit errors caught) while a 512-byte one is HD=6 (all &lt;= 5-bit errors, plus every burst &lt;= 32 bits).
/// </para>
/// </remarks>
internal static class Crc32CUtil
{
    /// <summary>
    /// Compute CRC32C over a data span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compute(ReadOnlySpan<byte> data) => ComputePartial(0xFFFFFFFF, data) ^ 0xFFFFFFFF;

    /// <summary>Initial state of an incremental computation: <c>Finish(Append(Append(Start, a), b)) == Compute(a ++ b)</c>.</summary>
    public const uint Start = 0xFFFFFFFF;

    /// <summary>
    /// Advances an incremental CRC32C state over <paramref name="data"/>. The state is the raw register, never inverted between calls, so any
    /// split of a byte stream yields the same final value as one <see cref="Compute"/> over the whole of it.
    /// </summary>
    /// <param name="state"><see cref="Start"/>, or the value a previous <see cref="Append"/> returned.</param>
    /// <param name="data">The next bytes of the stream.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Append(uint state, ReadOnlySpan<byte> data) => ComputePartial(state, data);

    /// <summary>
    /// Advances an incremental state over <paramref name="count"/> zero bytes in O(log count): a zero byte is a linear map of the register, so
    /// <c>count</c> of them is that map's power, built by repeated squaring of the one-bit operator (zlib's <c>crc32_combine</c> construction).
    /// </summary>
    /// <param name="state"><see cref="Start"/>, or the value a previous <see cref="Append"/> returned.</param>
    /// <param name="count">How many zero bytes.</param>
    public static uint AppendZeros(uint state, long count)
    {
        if (count <= 0)
        {
            return state;
        }

        Span<uint> even = stackalloc uint[32];
        Span<uint> odd = stackalloc uint[32];

        // The operator for one zero BIT on the reflected register.
        odd[0] = 0x82F63B78u;
        var row = 1u;
        for (var n = 1; n < 32; n++)
        {
            odd[n] = row;
            row <<= 1;
        }

        Gf2MatrixSquare(even, odd);     // two zero bits
        Gf2MatrixSquare(odd, even);     // four zero bits

        // Each pass squares into the next power of two (first: one byte), applied when that bit of count is set.
        while (true)
        {
            Gf2MatrixSquare(even, odd);
            if ((count & 1) != 0)
            {
                state = Gf2MatrixTimes(even, state);
            }

            count >>= 1;
            if (count == 0)
            {
                return state;
            }

            Gf2MatrixSquare(odd, even);
            if ((count & 1) != 0)
            {
                state = Gf2MatrixTimes(odd, state);
            }

            count >>= 1;
            if (count == 0)
            {
                return state;
            }
        }
    }

    private static uint Gf2MatrixTimes(ReadOnlySpan<uint> matrix, uint vector)
    {
        var sum = 0u;
        for (var i = 0; vector != 0; i++, vector >>= 1)
        {
            if ((vector & 1) != 0)
            {
                sum ^= matrix[i];
            }
        }

        return sum;
    }

    private static void Gf2MatrixSquare(Span<uint> square, ReadOnlySpan<uint> matrix)
    {
        for (var n = 0; n < 32; n++)
        {
            square[n] = Gf2MatrixTimes(matrix, matrix[n]);
        }
    }

    /// <summary>Turns an incremental state into the CRC32C of every byte appended to it.</summary>
    /// <param name="state">The value the last <see cref="Append"/> returned.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Finish(uint state) => state ^ 0xFFFFFFFF;

    /// <summary>
    /// Compute CRC32C over a data span, skipping a region that is treated as zeros for CRC purposes.
    /// Used for self-referencing CRC fields where the CRC field itself must be excluded from the computation.
    /// </summary>
    /// <param name="data">The complete data span including the skip region.</param>
    /// <param name="skipOffset">Byte offset of the region to skip.</param>
    /// <param name="skipLength">Length of the region to skip in bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ComputeSkipping(ReadOnlySpan<byte> data, int skipOffset, int skipLength)
    {
        uint crc = 0xFFFFFFFF;

        if (skipOffset > 0)
        {
            crc = ComputePartial(crc, data[..skipOffset]);
        }

        // Skip region contributes zeros — advance CRC state by skipLength zero bytes
        crc = ComputePartialZeros(crc, skipLength);

        int afterSkip = skipOffset + skipLength;
        if (afterSkip < data.Length)
        {
            crc = ComputePartial(crc, data[afterSkip..]);
        }

        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Compute CRC32C over a data span, skipping <b>two</b> disjoint regions that are treated as zeros.
    /// </summary>
    /// <remarks>
    /// Needed by the per-sector page footer: sector 0 carries both the page's own checksum field and the sector array
    /// itself, and each is written after the value that covers it — so both must be excluded from the sector's own CRC.
    /// The two regions must not overlap and must be given in ascending offset order.
    /// </remarks>
    /// <param name="data">The complete data span including both skip regions.</param>
    /// <param name="skip0Offset">Byte offset of the first region to skip.</param>
    /// <param name="skip0Length">Length of the first skip region.</param>
    /// <param name="skip1Offset">Byte offset of the second region to skip; must be at or after the end of the first.</param>
    /// <param name="skip1Length">Length of the second skip region.</param>
    public static uint ComputeSkippingPair(ReadOnlySpan<byte> data, int skip0Offset, int skip0Length, int skip1Offset, int skip1Length)
    {
        Debug.Assert(skip1Offset >= skip0Offset + skip0Length, "Skip regions must be disjoint and ascending.");

        uint crc = 0xFFFFFFFF;

        if (skip0Offset > 0)
        {
            crc = ComputePartial(crc, data[..skip0Offset]);
        }

        crc = ComputePartialZeros(crc, skip0Length);

        var afterFirst = skip0Offset + skip0Length;
        if (skip1Offset > afterFirst)
        {
            crc = ComputePartial(crc, data[afterFirst..skip1Offset]);
        }

        crc = ComputePartialZeros(crc, skip1Length);

        var afterSecond = skip1Offset + skip1Length;
        if (afterSecond < data.Length)
        {
            crc = ComputePartial(crc, data[afterSecond..]);
        }

        return crc ^ 0xFFFFFFFF;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint ComputePartial(uint crc, ReadOnlySpan<byte> data)
    {
        if (Sse42.X64.IsSupported)
        {
            return ComputeSse42X64(crc, data);
        }

        if (Sse42.IsSupported)
        {
            return ComputeSse42X32(crc, data);
        }

        if (ArmCrc32.Arm64.IsSupported)
        {
            return ComputeArm64(crc, data);
        }

        return ComputeSoftware(crc, data);
    }

    /// <summary>
    /// Advances CRC state over <paramref name="count"/> zero bytes without needing a buffer.
    /// </summary>
    private static uint ComputePartialZeros(uint crc, int count)
    {
        // For small skip lengths (typical: 4 bytes for CRC field), process byte-by-byte with zero
        for (int i = 0; i < count; i++)
        {
            crc = (crc >> 8) ^ STable[(byte)(crc ^ 0)];
        }

        return crc;
    }

    /// <summary>
    /// SSE4.2 x64: Process 8 bytes per iteration via CRC32 r64, r/m64.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint ComputeSse42X64(uint crc, ReadOnlySpan<byte> data)
    {
        ulong crc64 = crc;
        ref byte ptr = ref MemoryMarshal.GetReference(data);
        int offset = 0;
        int aligned = data.Length & ~7;

        while (offset < aligned)
        {
            crc64 = Sse42.X64.Crc32(crc64, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref ptr, offset)));
            offset += 8;
        }

        uint crc32 = (uint)crc64;
        while (offset < data.Length)
        {
            crc32 = Sse42.Crc32(crc32, Unsafe.Add(ref ptr, offset));
            offset++;
        }

        return crc32;
    }

    /// <summary>
    /// SSE4.2 x86 (32-bit): Process 4 bytes per iteration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint ComputeSse42X32(uint crc, ReadOnlySpan<byte> data)
    {
        ref byte ptr = ref MemoryMarshal.GetReference(data);
        int offset = 0;
        int aligned = data.Length & ~3;

        while (offset < aligned)
        {
            crc = Sse42.Crc32(crc, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref ptr, offset)));
            offset += 4;
        }

        while (offset < data.Length)
        {
            crc = Sse42.Crc32(crc, Unsafe.Add(ref ptr, offset));
            offset++;
        }

        return crc;
    }

    /// <summary>
    /// ARM64: Process 8 bytes per iteration via CRC32CX instruction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint ComputeArm64(uint crc, ReadOnlySpan<byte> data)
    {
        ref byte ptr = ref MemoryMarshal.GetReference(data);
        int offset = 0;
        int aligned = data.Length & ~7;

        while (offset < aligned)
        {
            crc = ArmCrc32.Arm64.ComputeCrc32C(crc, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref ptr, offset)));
            offset += 8;
        }

        while (offset < data.Length)
        {
            crc = ArmCrc32.ComputeCrc32C(crc, Unsafe.Add(ref ptr, offset));
            offset++;
        }

        return crc;
    }

    /// <summary>
    /// Software fallback: byte-at-a-time with precomputed table.
    /// Castagnoli polynomial (bit-reversed): 0x82F63B78.
    /// </summary>
    private static uint ComputeSoftware(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ STable[(byte)(crc ^ b)];
        }

        return crc;
    }

    private static readonly uint[] STable = GenerateTable(0x82F63B78u);

    private static uint[] GenerateTable(uint polynomial)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint entry = i;
            for (int j = 0; j < 8; j++)
            {
                entry = (entry & 1) != 0 ? (entry >> 1) ^ polynomial : entry >> 1;
            }

            table[i] = entry;
        }

        return table;
    }
}
