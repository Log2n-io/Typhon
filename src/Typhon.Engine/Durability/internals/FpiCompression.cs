using System;
using K4os.Compression.LZ4;

namespace Typhon.Engine.Internals;

/// <summary>
/// LZ4 compression helpers for Full-Page Image (FPI) payloads. Uses <see cref="LZ4Level.L00_FAST"/> for minimal latency.
/// </summary>
internal static class FpiCompression
{
    /// <summary>No compression applied.</summary>
    public const byte AlgoNone = 0;

    /// <summary>LZ4 fast compression.</summary>
    public const byte AlgoLZ4 = 1;

    /// <summary>Returns the maximum compressed output size for a given input size.</summary>
    public static int MaxCompressedSize(int inputSize) => LZ4Codec.MaximumOutputSize(inputSize);

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="target"/> using LZ4 fast mode.
    /// </summary>
    /// <returns>Compressed byte count, or -1 if the data is incompressible (compressed &gt;= source length).</returns>
    public static int Compress(ReadOnlySpan<byte> source, Span<byte> target)
    {
        var compressedSize = LZ4Codec.Encode(source, target, LZ4Level.L00_FAST);
        if (compressedSize <= 0 || compressedSize >= source.Length)
        {
            return -1; // Incompressible
        }

        return compressedSize;
    }

    /// <summary>
    /// Decompresses <paramref name="source"/> into <paramref name="target"/>.
    /// </summary>
    /// <returns>Decompressed byte count, or -1 on failure.</returns>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> target)
    {
        var decompressedSize = LZ4Codec.Decode(source, target);
        return decompressedSize <= 0 ? -1 : decompressedSize;
    }
}
