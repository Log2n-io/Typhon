using K4os.Compression.LZ4;
using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// The block codec of raw WAL bytes in the store: a pack's tail blocks (<see cref="PackWriter"/>, <see cref="PackReader"/>) and an archived segment's
/// (<see cref="ArchivedSegmentWriter"/>, <see cref="ArchivedSegmentReader"/>). A block is at most <see cref="PackFormat.TailBlockSize"/> raw bytes, stored
/// as an LZ4 block, or raw when LZ4 cannot shrink it; the stored length tells which (equal to the raw length = raw).
/// </summary>
internal static class TailBlockCodec
{
    /// <summary>The payload room one block needs: LZ4's worst case for a full block.</summary>
    public static int MaxStoredLength(int rawLength) => LZ4Codec.MaximumOutputSize(rawLength);

    /// <summary>Stores <paramref name="raw"/> into <paramref name="payload"/> (at least <see cref="MaxStoredLength"/>); returns the stored length.</summary>
    public static int Encode(ReadOnlySpan<byte> raw, Span<byte> payload)
    {
        var stored = LZ4Codec.Encode(raw, payload);
        if (stored <= 0 || stored >= raw.Length)
        {
            raw.CopyTo(payload);
            stored = raw.Length;
        }

        return stored;
    }

    /// <summary>
    /// Restores a block's <paramref name="raw"/> bytes (exactly its raw length) from its stored <paramref name="payload"/>; <c>false</c> when the payload does
    /// not decode to that length. The destination is cleared before an LZ4 decode (<see cref="PackReader.DecodeLz4"/>: a damaged frame can decode to the
    /// full length with an output that depends on what the destination held).
    /// </summary>
    public static bool Decode(ReadOnlySpan<byte> payload, Span<byte> raw)
    {
        if (payload.Length == raw.Length)
        {
            payload.CopyTo(raw);
            return true;
        }

        return PackReader.DecodeLz4(payload, raw) == raw.Length;
    }
}
