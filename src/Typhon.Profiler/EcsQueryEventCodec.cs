using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Phase 7 (D2): variant byte for the consolidated <see cref="TraceEventKind.EcsQueryExecute"/> wire format.
/// New producers always emit kind 32 with this byte set so old separate kinds (33/34) can be retired.
/// </summary>
public enum EcsQueryVariant : byte
{
    Execute = 0,
    Count   = 1,
    Any     = 2,
}

/// <summary>
/// Decoded form of an ECS query span event (Execute / Count / Any share this shape). Used by the reader, viewer DTO, and tests.
/// </summary>
/// <remarks>
/// <b>Optional field semantics:</b> a field is present if its corresponding bit in <see cref="OptionalFieldMask"/> is set. The properties return the
/// raw value regardless, but callers that want to distinguish "not set" from "set to zero" must check the mask.
/// </remarks>
public readonly struct EcsQueryEventData
{
    public TraceEventKind Kind { get; }
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }

    /// <summary>Required field — archetype type ID (<c>ushort</c> index into <c>ArchetypeRegistry</c>).</summary>
    public ushort ArchetypeTypeId { get; }

    /// <summary>Bitmask describing which optional fields are present. Bit positions match <c>EcsQueryEventOptBit</c> constants.</summary>
    public byte OptionalFieldMask { get; }

    /// <summary>Optional — number of matching entities. Valid iff <see cref="HasResultCount"/> is <c>true</c>.</summary>
    public int ResultCount { get; }

    /// <summary>Optional — scan strategy used. Valid iff <see cref="HasScanMode"/> is <c>true</c>.</summary>
    public EcsQueryScanMode ScanMode { get; }

    /// <summary>Optional — for <see cref="TraceEventKind.EcsQueryAny"/>, whether a match was found. Valid iff <see cref="HasFound"/> is <c>true</c>.</summary>
    public bool Found { get; }

    /// <summary>Phase 7 (D2): variant byte for consolidated kind 32 producers. Valid iff <see cref="HasVariant"/> is <c>true</c>.</summary>
    public EcsQueryVariant Variant { get; }

    /// <summary>Source-location id assigned by <c>SourceLocationGenerator</c> (#302). Zero when the wire record didn't carry source attribution.</summary>
    public ushort SourceLocationId { get; }

    public bool HasResultCount => (OptionalFieldMask & EcsQueryEventCodec.OptResultCount) != 0;
    public bool HasScanMode => (OptionalFieldMask & EcsQueryEventCodec.OptScanMode) != 0;
    public bool HasFound => (OptionalFieldMask & EcsQueryEventCodec.OptFound) != 0;
    public bool HasVariant => (OptionalFieldMask & EcsQueryEventCodec.OptVariant) != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public bool HasSourceLocation => SourceLocationId != 0;

    public EcsQueryEventData(
        TraceEventKind kind, byte threadSlot, long startTimestamp, long durationTicks,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        ushort archetypeTypeId, byte optionalFieldMask,
        int resultCount, EcsQueryScanMode scanMode, bool found,
        EcsQueryVariant variant = EcsQueryVariant.Execute,
        ushort sourceLocationId = 0)
    {
        Kind = kind;
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        ArchetypeTypeId = archetypeTypeId;
        OptionalFieldMask = optionalFieldMask;
        ResultCount = resultCount;
        ScanMode = scanMode;
        Found = found;
        Variant = variant;
        SourceLocationId = sourceLocationId;
    }
}

/// <summary>
/// Shared wire-format codec for the three ECS query event kinds (Execute, Count, Any). All three have identical payload layout — only the
/// <see cref="TraceEventKind"/> byte in the common header differs, and Any uses the <see cref="OptFound"/> bit slot in place of
/// <see cref="OptResultCount"/>.
/// </summary>
/// <remarks>
/// <b>Payload layout after span header:</b>
/// <code>
/// [u16 archetypeTypeId]      // required — 2 B
/// [u8  optMask]              // 1 B — bits enumerated below
/// [i32 resultCount]?         // 4 B — present iff optMask &amp; OptResultCount (OR optMask &amp; OptFound for Any, which encodes in the same 4 B slot)
/// [u8  scanMode]?            // 1 B — present iff optMask &amp; OptScanMode
/// </code>
/// The <c>Found</c> field on <see cref="EcsQueryAnyEvent"/> is encoded as 0/1 in the same 4-byte slot as <c>ResultCount</c> to keep the wire
/// format identical across the three kinds. This simplifies the decoder — one layout, three event kinds.
/// </remarks>
public static class EcsQueryEventCodec
{
    /// <summary>Optional-mask bit 0 — <c>ResultCount</c> (Execute, Count).</summary>
    public const byte OptResultCount = 0x01;

    /// <summary>Optional-mask bit 1 — <c>ScanMode</c> (all three kinds).</summary>
    public const byte OptScanMode = 0x02;

    /// <summary>Optional-mask bit 2 — <c>Found</c> (Any). Encoded in the same 4 B slot as <see cref="OptResultCount"/>.</summary>
    public const byte OptFound = 0x04;

    /// <summary>Phase 7 (D2) — Optional-mask bit 3 — <c>Variant</c> u8 (consolidated kind 32 producers).</summary>
    public const byte OptVariant = 0x08;

    /// <summary>Size of the <c>ResultCount</c>/<c>Found</c> slot if present.</summary>
    private const int ResultCountSize = 4;

    /// <summary>Size of the <c>ScanMode</c> slot if present.</summary>
    private const int ScanModeSize = 1;

    /// <summary>Phase 7 — size of the <c>Variant</c> u8 slot if present.</summary>
    private const int VariantSize = 1;

    /// <summary>Required payload size — <c>ArchetypeTypeId</c> (u16) + <c>optMask</c> (u8) = 3 B.</summary>
    private const int RequiredPayloadSize = 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext, byte optMask, ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var size = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation) + RequiredPayloadSize;
        // ResultCount and Found share the same 4 B slot — only one of them is ever set for a given kind.
        if ((optMask & (OptResultCount | OptFound)) != 0)
        {
            size += ResultCountSize;
        }
        if ((optMask & OptScanMode) != 0)
        {
            size += ScanModeSize;
        }
        if ((optMask & OptVariant) != 0)
        {
            size += VariantSize;
        }
        return size;
    }

    /// <summary>
    /// Internal encoder shared by the three ECS query event kinds. Writes the span header, required payload, and any optional fields whose mask
    /// bit is set. When <paramref name="sourceLocationId"/> is non-zero, sets <see cref="TraceRecordHeader.SpanFlagsHasSourceLocation"/> and
    /// writes 2 extra bytes after the optional trace context (#302).
    /// </summary>
    internal static void Encode(
        Span<byte> destination,
        long endTimestamp,
        TraceEventKind kind,
        byte threadSlot,
        long startTimestamp,
        ulong spanId,
        ulong parentSpanId,
        ulong traceIdHi,
        ulong traceIdLo,
        ushort archetypeTypeId,
        byte optMask,
        int resultCount,
        EcsQueryScanMode scanMode,
        bool found,
        out int bytesWritten,
        EcsQueryVariant variant = EcsQueryVariant.Execute,
        ushort sourceLocationId = 0)
    {
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var hasSourceLocation = sourceLocationId != 0;
        var size = ComputeSize(hasTraceContext, optMask, sourceLocationId);

        // ── Common header + span extension ──
        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, kind, threadSlot, startTimestamp);
        var spanFlags = (byte)0;
        if (hasTraceContext) spanFlags |= TraceRecordHeader.SpanFlagsHasTraceContext;
        if (hasSourceLocation) spanFlags |= TraceRecordHeader.SpanFlagsHasSourceLocation;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            durationTicks: endTimestamp - startTimestamp,
            spanId: spanId,
            parentSpanId: parentSpanId,
            spanFlags: spanFlags);

        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        // ── Required payload ──
        var payload = destination[headerSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, archetypeTypeId);
        payload[2] = optMask;
        var cursor = RequiredPayloadSize;

        // ── Optional payload (in canonical order) ──
        // ResultCount and Found share the same 4 B slot since a given kind never sets both.
        if ((optMask & (OptResultCount | OptFound)) != 0)
        {
            var slotValue = (optMask & OptFound) != 0 ? (found ? 1 : 0) : resultCount;
            BinaryPrimitives.WriteInt32LittleEndian(payload[cursor..], slotValue);
            cursor += ResultCountSize;
        }
        if ((optMask & OptScanMode) != 0)
        {
            payload[cursor] = (byte)scanMode;
            cursor += ScanModeSize;
        }
        if ((optMask & OptVariant) != 0)
        {
            payload[cursor] = (byte)variant;
            cursor += VariantSize;
        }

        bytesWritten = size;
    }

    /// <summary>Decode an ECS query event record from <paramref name="source"/>. Works for all three kinds (Execute, Count, Any).</summary>
    public static EcsQueryEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;

        ulong traceIdHi = 0, traceIdLo = 0;
        if (hasTraceContext)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        var payload = source[headerSize..];
        var archetypeTypeId = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var optMask = payload[2];
        var cursor = RequiredPayloadSize;

        int resultCount = 0;
        EcsQueryScanMode scanMode = EcsQueryScanMode.Empty;
        bool found = false;

        if ((optMask & (OptResultCount | OptFound)) != 0)
        {
            var slotValue = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
            if ((optMask & OptFound) != 0)
            {
                found = slotValue != 0;
            }
            else
            {
                resultCount = slotValue;
            }
            cursor += ResultCountSize;
        }
        if ((optMask & OptScanMode) != 0)
        {
            scanMode = (EcsQueryScanMode)payload[cursor];
            cursor += ScanModeSize;
        }

        var variant = EcsQueryVariant.Execute;
        if ((optMask & OptVariant) != 0)
        {
            variant = (EcsQueryVariant)payload[cursor];
            cursor += VariantSize;
        }

        return new EcsQueryEventData(kind, threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            archetypeTypeId, optMask, resultCount, scanMode, found, variant, sourceLocationId);
    }
}

