using System;
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
/// Performance: ~1.3us per 8KB page (sequential) on SSE4.2 x64. The CRC32 instruction has 3-cycle latency but 1-cycle throughput,
/// yielding ~8 bytes/3 cycles at ~4 GHz.
/// </para>
/// </remarks>
internal static class WalCrc
{
    /// <summary>
    /// Compute CRC32C over a data span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compute(ReadOnlySpan<byte> data) => ComputePartial(0xFFFFFFFF, data) ^ 0xFFFFFFFF;

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
