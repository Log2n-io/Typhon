using System;
using System.Buffers.Binary;

namespace Typhon.Profiler;

/// <summary>
/// Frame envelope for the Tracy-style profiler's live TCP stream. Each frame: <c>[u8 type][u32 length][payload length bytes]</c>.
/// </summary>
/// <remarks>
/// The live stream carries the same content as the file format but delivered incrementally: <c>Init</c> sends the file header + metadata tables,
/// <c>Block</c> sends one compressed record block per consumer drain, <c>Shutdown</c> signals end-of-session.
/// </remarks>
public static class LiveStreamProtocol
{
    /// <summary>Size of the frame header in bytes: 1 (type) + 4 (length) = 5.</summary>
    public const int FrameHeaderSize = 5;

    /// <summary>Write a frame header into <paramref name="destination"/>.</summary>
    public static void WriteFrameHeader(Span<byte> destination, LiveFrameType type, int payloadLength)
    {
        if (destination.Length < FrameHeaderSize)
        {
            throw new ArgumentException($"Buffer must be at least {FrameHeaderSize} bytes", nameof(destination));
        }
        destination[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(destination[1..], payloadLength);
    }

    /// <summary>Read a frame header from <paramref name="source"/>.</summary>
    public static (LiveFrameType Type, int PayloadLength) ReadFrameHeader(ReadOnlySpan<byte> source)
    {
        if (source.Length < FrameHeaderSize)
        {
            throw new ArgumentException($"Buffer must be at least {FrameHeaderSize} bytes", nameof(source));
        }
        var type = (LiveFrameType)source[0];
        var length = BinaryPrimitives.ReadInt32LittleEndian(source[1..]);
        return (type, length);
    }
}

/// <summary>Frame kinds in the live TCP stream.</summary>
public enum LiveFrameType : byte
{
    /// <summary>Session init: file header + system / archetype / component type tables.</summary>
    Init = 1,

    /// <summary>One compressed block of typed records. Payload = <see cref="TraceBlockEncoder.BlockHeaderSize"/> + LZ4-compressed bytes.</summary>
    Block = 2,

    /// <summary>Session end.</summary>
    Shutdown = 3,

    /// <summary>
    /// File-table frame (#302, Phase 4 of profiler-source-attribution): interned source-file paths.
    /// Sent once during the init handshake before any Block frames; payload is identical to the
    /// FileTable section of <c>.typhon-trace</c> files (u32 entryCount, then per-entry: u16 fileId,
    /// u16 pathLen, UTF-8 path bytes). Absent when the engine has no source attribution to report.
    /// See claude/design/Profiler/10-profiler-source-attribution.md §4.7.
    /// </summary>
    FileTable = 4,

    /// <summary>
    /// SourceLocationManifest frame (#302, Phase 4): id → (fileId, line, kind, method) entries.
    /// Sent once during the init handshake immediately after the FileTable. Payload is identical to the
    /// SourceLocationManifest section of <c>.typhon-trace</c> files. No delta frames during the session —
    /// the table is fixed at compile time (per design §4.4).
    /// </summary>
    SourceLocationManifest = 5,
}
