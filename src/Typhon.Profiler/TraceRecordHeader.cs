using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Wire-format layout constants and encode/decode helpers for the <i>common header</i> (12 B, present on every record) and the <i>span header
/// extension</i> (25 B, present on every span record, optionally followed by a 16 B trace context).
/// </summary>
/// <remarks>
/// <para>
/// <b>Common header layout (12 B):</b>
/// <code>
/// offset 0..1    u16  Size             // total record size in bytes, including these 2 bytes. 0 = empty ring slot. 0xFFFF = wrap sentinel.
/// offset 2       u8   Kind             // TraceEventKind discriminant
/// offset 3       u8   ThreadSlot       // slot index 0..255 (from ThreadSlotRegistry)
/// offset 4..11   i64  StartTimestamp   // Stopwatch.GetTimestamp() at begin (span) or emit (instant)
/// </code>
/// </para>
/// <para>
/// <b>Span header extension (25 B), appended after the common header for <see cref="TraceEventKindExtensions.IsSpan"/> kinds:</b>
/// <code>
/// offset 12..19  i64  DurationTicks    // Stopwatch.GetTimestamp() delta at end; 0 allowed for open/crashed spans
/// offset 20..27  u64  SpanId           // this span's unique ID (0 disallowed for real spans)
/// offset 28..35  u64  ParentSpanId     // enclosing Typhon span's SpanId, or 0 for top-level
/// offset 36      u8   SpanFlags        // bit 0 = has trace context (next 16 B are TraceIdHi + TraceIdLo)
/// </code>
/// </para>
/// <para>
/// <b>Optional trace context (16 B):</b> only present when <see cref="SpanFlagsHasTraceContext"/> bit is set in <c>SpanFlags</c>:
/// <code>
/// offset 37..44  u64  TraceIdHi        // upper 8 B of Activity.TraceId
/// offset 45..52  u64  TraceIdLo        // lower 8 B of Activity.TraceId
/// </code>
/// </para>
/// <para>
/// All multi-byte integers are little-endian — <see cref="BinaryPrimitives"/> with explicit <c>LittleEndian</c> variants. No host-endian
/// reads/writes anywhere in the wire format so <c>.typhon-trace</c> files are portable across ARM/x64.
/// </para>
/// </remarks>
public static class TraceRecordHeader
{
    /// <summary>Common header size — 12 bytes.</summary>
    public const int CommonHeaderSize = 12;

    /// <summary>Span header extension size — 25 bytes, appended after the common header for span records.</summary>
    public const int SpanHeaderExtensionSize = 25;

    /// <summary>Optional trace context size — 16 bytes, appended after the span header extension if the flags bit is set.</summary>
    public const int TraceContextSize = 16;

    /// <summary>Span record header with no trace context (common + span extension) = 37 B.</summary>
    public const int MinSpanHeaderSize = CommonHeaderSize + SpanHeaderExtensionSize;

    /// <summary>Span record header with trace context (common + span extension + trace context) = 53 B.</summary>
    public const int MaxSpanHeaderSize = CommonHeaderSize + SpanHeaderExtensionSize + TraceContextSize;

    /// <summary>Reserved "empty slot" marker in the <c>Size</c> field — consumer treats this position as not-yet-committed.</summary>
    public const ushort EmptySlot = 0;

    /// <summary>Reserved "wrap to start" sentinel in the <c>Size</c> field — consumer resets its read pointer to the buffer start when it sees this.</summary>
    public const ushort WrapSentinel = 0xFFFF;

    /// <summary><c>SpanFlags</c> bit 0 — set when the record includes a 16-byte trace context after the span header extension.</summary>
    public const byte SpanFlagsHasTraceContext = 0x01;

    /// <summary>
    /// <c>SpanFlags</c> bit 1 — set when the record carries a 2-byte source-location id (the compile-time <c>SourceLocationGenerator</c>
    /// site index) immediately after the optional trace context. See <c>claude/design/Profiler/10-profiler-source-attribution.md</c>.
    /// </summary>
    public const byte SpanFlagsHasSourceLocation = 0x02;

    /// <summary>Optional source-location-id size — 2 bytes (u16 LE), appended after the (optional) trace context when the flag is set.</summary>
    public const int SourceLocationIdSize = 2;

    // ═══════════════════════════════════════════════════════════════════════
    // Encoding
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Write the 12-byte common header at the start of <paramref name="destination"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteCommonHeader(Span<byte> destination, ushort size, TraceEventKind kind, byte threadSlot, long startTimestamp)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, size);
        destination[2] = (byte)kind;
        destination[3] = threadSlot;
        BinaryPrimitives.WriteInt64LittleEndian(destination[4..], startTimestamp);
    }

    /// <summary>
    /// Write the 25-byte span header extension at <paramref name="destination"/> (caller passes the span starting at offset 12 of the record).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSpanHeaderExtension(
        Span<byte> destination,
        long durationTicks,
        ulong spanId,
        ulong parentSpanId,
        byte spanFlags)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, durationTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], spanId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], parentSpanId);
        destination[24] = spanFlags;
    }

    /// <summary>
    /// Write the optional 16-byte trace context at <paramref name="destination"/> (caller passes the span starting at offset 37 of the record).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteTraceContext(Span<byte> destination, ulong traceIdHi, ulong traceIdLo)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, traceIdHi);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], traceIdLo);
    }

    /// <summary>
    /// Write the optional 2-byte source-location id at <paramref name="destination"/>. Caller passes the span starting at the offset just
    /// past the optional trace context (37 with no trace context, 53 with). Set <see cref="SpanFlagsHasSourceLocation"/> in <c>SpanFlags</c>
    /// when calling this.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSourceLocationId(Span<byte> destination, ushort sourceLocationId) 
        => BinaryPrimitives.WriteUInt16LittleEndian(destination, sourceLocationId);

    // ═══════════════════════════════════════════════════════════════════════
    // Decoding
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parse the 12-byte common header at the start of <paramref name="source"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadCommonHeader(ReadOnlySpan<byte> source, out ushort size, out TraceEventKind kind, out byte threadSlot, out long startTimestamp)
    {
        size = BinaryPrimitives.ReadUInt16LittleEndian(source);
        kind = (TraceEventKind)source[2];
        threadSlot = source[3];
        startTimestamp = BinaryPrimitives.ReadInt64LittleEndian(source[4..]);
    }

    /// <summary>
    /// Parse the 25-byte span header extension at <paramref name="source"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadSpanHeaderExtension(
        ReadOnlySpan<byte> source,
        out long durationTicks,
        out ulong spanId,
        out ulong parentSpanId,
        out byte spanFlags)
    {
        durationTicks = BinaryPrimitives.ReadInt64LittleEndian(source);
        spanId = BinaryPrimitives.ReadUInt64LittleEndian(source[8..]);
        parentSpanId = BinaryPrimitives.ReadUInt64LittleEndian(source[16..]);
        spanFlags = source[24];
    }

    /// <summary>
    /// Parse the optional 16-byte trace context at <paramref name="source"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadTraceContext(ReadOnlySpan<byte> source, out ulong traceIdHi, out ulong traceIdLo)
    {
        traceIdHi = BinaryPrimitives.ReadUInt64LittleEndian(source);
        traceIdLo = BinaryPrimitives.ReadUInt64LittleEndian(source[8..]);
    }

    /// <summary>
    /// Parse the optional 2-byte source-location id at <paramref name="source"/>. Caller has already read <see cref="SpanFlagsHasSourceLocation"/>
    /// from <c>SpanFlags</c> and is positioning <paramref name="source"/> just past the optional trace context.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadSourceLocationId(ReadOnlySpan<byte> source)
        => BinaryPrimitives.ReadUInt16LittleEndian(source);

    // ═══════════════════════════════════════════════════════════════════════
    // Sizing
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Total header size for a span record: 37 B if <paramref name="hasTraceContext"/> is <c>false</c>, 53 B if <c>true</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SpanHeaderSize(bool hasTraceContext) => hasTraceContext ? MaxSpanHeaderSize : MinSpanHeaderSize;

    /// <summary>
    /// Total header size for a span record including the optional source-location id. Adds 2 bytes when <paramref name="hasSourceLocation"/>
    /// is <c>true</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SpanHeaderSize(bool hasTraceContext, bool hasSourceLocation)
        => SpanHeaderSize(hasTraceContext) + (hasSourceLocation ? SourceLocationIdSize : 0);

    /// <summary>
    /// Byte offset of the optional source-location id within a span record: 37 with no trace context, 53 with.
    /// Caller passes this to <see cref="WriteSourceLocationId"/> / <see cref="ReadSourceLocationId"/> after slicing
    /// the destination/source span at the offset.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SourceLocationIdOffset(bool hasTraceContext) => SpanHeaderSize(hasTraceContext);
}
