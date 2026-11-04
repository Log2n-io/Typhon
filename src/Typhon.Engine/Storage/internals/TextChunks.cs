using System;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// The storage primitive behind <see cref="String1024"/> (#1094): one text per 1 024-byte chunk of a <see cref="ChunkBasedSegment{TStore}"/>, UTF-8,
/// NUL-terminated,
/// the rest of the chunk zeroed. A chunk id of 0 is no text: chunk 0 of every chunk-based segment is the reserved null sentinel.
/// </summary>
/// <remarks>
/// <para>Fixed size, so a text is one chunk: allocation and free are the segment's own O(1) bitmap operations, there is no chaining and no reference count.
/// Who owns a chunk, and when it may be freed, is decided by the callers (rule TXT-01): exactly one live row owns it, or a contiguous run of revisions of
/// one Versioned entity shares it.</para>
/// <para>The tail past the terminator is zeroed on every write, so a chunk's bytes depend only on its text: a page image, a backup or a dump never carries a
/// longer text that was there before.</para>
/// </remarks>
internal static unsafe class TextChunks
{
    /// <summary>Chunk stride: <see cref="String1024.MaxUtf8Bytes"/> bytes of text plus the terminator.</summary>
    internal const int Stride = String1024.MaxUtf8Bytes + 1;

    /// <summary>
    /// Encodes <paramref name="text"/> to UTF-8 into <paramref name="destination"/> (at least <see cref="String1024.MaxUtf8Bytes"/> bytes), cut at a whole
    /// code point when it does not fit. Returns the byte count.
    /// </summary>
    internal static int Encode(ReadOnlySpan<char> text, Span<byte> destination)
    {
        // DestinationTooSmall stops at a scalar boundary, which is the truncation wanted; invalid surrogates become U+FFFD.
        Utf8.FromUtf16(text, destination[..String1024.MaxUtf8Bytes], out _, out var written, replaceInvalidSequences: true, isFinalBlock: true);
        return written;
    }

    /// <summary>
    /// The length <paramref name="utf8"/> keeps once cut to <see cref="String1024.MaxUtf8Bytes"/> on a code-point boundary, and before any NUL (a NUL ends the
    /// text as stored).
    /// </summary>
    internal static int FitUtf8(ReadOnlySpan<byte> utf8)
    {
        var nul = utf8.IndexOf((byte)0);
        var length = nul >= 0 ? nul : utf8.Length;
        if (length <= String1024.MaxUtf8Bytes)
        {
            return length;
        }

        // Back off continuation bytes (10xxxxxx) so the cut lands on the start of a code point, which is then excluded.
        length = String1024.MaxUtf8Bytes;
        while (length > 0 && (utf8[length] & 0xC0) == 0x80)
        {
            length--;
        }

        return length;
    }

    /// <summary>Writes <paramref name="utf8"/> (already fitted, see <see cref="FitUtf8"/>) into chunk <paramref name="chunkId"/>, terminator and zeroed
    /// tail included.</summary>
    internal static void Write<TStore>(ref ChunkAccessor<TStore> accessor, int chunkId, ReadOnlySpan<byte> utf8) where TStore : struct, IPageStore
    {
        var chunk = accessor.GetChunkAsSpan(chunkId, true);
        utf8.CopyTo(chunk);
        chunk[utf8.Length..].Clear();
    }

    /// <summary>The text length, in UTF-8 bytes, of chunk <paramref name="chunkId"/>; 0 for chunk 0.</summary>
    internal static int Length<TStore>(ref ChunkAccessor<TStore> accessor, int chunkId) where TStore : struct, IPageStore
    {
        if (chunkId == 0)
        {
            return 0;
        }

        var chunk = accessor.GetChunkAsReadOnlySpan(chunkId)[..String1024.MaxUtf8Bytes];
        var nul = chunk.IndexOf((byte)0);
        return nul >= 0 ? nul : chunk.Length;
    }

    /// <summary>The UTF-8 bytes of chunk <paramref name="chunkId"/>, valid while <paramref name="accessor"/> holds its page; empty for chunk 0.</summary>
    internal static ReadOnlySpan<byte> Bytes<TStore>(ref ChunkAccessor<TStore> accessor, int chunkId) where TStore : struct, IPageStore =>
        chunkId == 0 ? default : accessor.GetChunkAsReadOnlySpan(chunkId)[..Length(ref accessor, chunkId)];

    /// <summary>Copies the text of chunk <paramref name="chunkId"/> into <paramref name="destination"/>; returns the bytes copied (at most its
    /// length).</summary>
    internal static int Read<TStore>(ref ChunkAccessor<TStore> accessor, int chunkId, Span<byte> destination) where TStore : struct, IPageStore
    {
        var bytes = Bytes(ref accessor, chunkId);
        var n = Math.Min(bytes.Length, destination.Length);
        bytes[..n].CopyTo(destination);
        return n;
    }

    /// <summary>The text of chunk <paramref name="chunkId"/> as a string; empty for chunk 0.</summary>
    internal static string ReadString<TStore>(ref ChunkAccessor<TStore> accessor, int chunkId) where TStore : struct, IPageStore
    {
        var bytes = Bytes(ref accessor, chunkId);
        return bytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Allocates a chunk and writes <paramref name="utf8"/> into it through <paramref name="accessor"/>, the accessor that keeps the new chunk's page dirty
    /// (the CP-04 window of <see cref="ChunkBasedSegment{TStore}.AllocateChunk(bool, ChangeSet)"/> does not exist on this path).
    /// </summary>
    internal static int Allocate<TStore>(ChunkBasedSegment<TStore> segment, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet, ReadOnlySpan<byte> utf8)
        where TStore : struct, IPageStore
    {
        var chunkId = segment.AllocateChunk(changeSet, ref accessor);
        Write(ref accessor, chunkId, utf8);
        return chunkId;
    }

    /// <summary>Reads a handle out of a component's value bytes at <paramref name="offset"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int HandleAt(byte* value, int offset) => Unsafe.ReadUnaligned<int>(value + offset);

    /// <summary>Writes a handle into a component's value bytes at <paramref name="offset"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetHandleAt(byte* value, int offset, int chunkId) => Unsafe.WriteUnaligned(value + offset, chunkId);
}
