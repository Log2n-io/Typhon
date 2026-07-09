// CS1591: this file declares public-accessibility types that live in the internal namespace (Phase 2b entanglement, see
// claude/research/PublicVsInternalApiClassification.md). They are excluded from the published API reference, so consumer-facing
// doc coverage is not enforced here.
#pragma warning disable 1591

using System;

// Keep this namespace, Typhon.Generators uses it to declare partial members of TraceEventDecoder
// ReSharper disable once CheckNamespace
namespace Typhon.Profiler.Events;

/// <summary>
/// Hand-glue partial-method implementations for the wire shapes that don't fit the generator's standard
/// template. As of 2026-05-10 this file holds only the catch-all fallback — every <c>[TraceEvent]</c> kind's
/// wire layout is now owned by the generator (slot-padding for shared slots is expressed via
/// <c>[Optional(WireSize=…)]</c>, and kind-conditional payload slots are now per-kind ref structs).
/// </summary>
public static partial class TraceEventDecoder
{
    // ─────────────────────────────────────────────────────────────────────
    // Fallback for kinds without a [TraceEvent] declaration
    // ─────────────────────────────────────────────────────────────────────
    //
    // Instant kinds (TickStart, TickEnd, PhaseStart/End, SystemReady/Skipped, etc.) don't have a
    // [TraceEvent]-decorated ref struct on the producer side — they're written through
    // InstantEventCodec directly. They surface as OtherTraceEventDto with the original kind preserved
    // numerically. Forward-compat new kinds (added after this consumer was built) follow the same path
    // so the dispatch stays graceful instead of dropping records.

    // ReSharper disable once MemberCanBePrivate.Global
    internal static partial TraceEventDto HandGlue_DecodeFallback(TraceEventKind kind, ReadOnlySpan<byte> source, int currentTick, long ticksPerUs)
    {
        // Query Definition Export kinds (#342, v9) — variable-length instant payloads with dedicated codecs.
        // Dispatched here so they surface as proper typed DTOs in the Workbench rather than OtherTraceEventDto.
        if (kind == TraceEventKind.QueryDefinitionDescribe)
        {
            return DecodeQueryDefinitionDescribe(source, currentTick, ticksPerUs);
        }
        if (kind == TraceEventKind.QueryArgs)
        {
            return DecodeQueryArgs(source, currentTick, ticksPerUs);
        }

        // Read the common header — every record carries it regardless of kind. After that we can't decode
        // payload generically (each kind has its own layout), so we surface what we have.
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);

        bool isSpan;
        try
        {
            isSpan = kind.IsSpan();
        }
        catch
        {
            // Unknown kind value (forward-compat) — IsSpan() may not classify it. Treat as instant.
            isSpan = false;
        }

        double? durationUs = null;
        if (isSpan && source.Length >= TraceRecordHeader.MinSpanHeaderSize)
        {
            TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
                out var durationTicks, out _, out _, out _);
            durationUs = durationTicks / (double)ticksPerUs;
        }

        return new OtherTraceEventDto
        {
            ThreadSlot = threadSlot,
            TickNumber = currentTick,
            TimestampUs = startTimestamp / (double)ticksPerUs,
            OriginalKind = (int)kind,
            IsSpan = isSpan,
            DurationUs = durationUs,
        };
    }

    // ═════════════════════════════════════════════════════════════════════
    // Query Definition Export decoders (#342, v9)
    // ═════════════════════════════════════════════════════════════════════

    private static QueryDefinitionDescribeEventDto DecodeQueryDefinitionDescribe(ReadOnlySpan<byte> source, int currentTick, long ticksPerUs)
    {
        // Copy span to ReadOnlyMemory<byte> for the codec API (Memory carries the blobs by reference). The source span lives only for this call — the DTO
        // arrays must own their data so they outlive the producer ring buffer.
        var sourceArray = source.ToArray();
        var data = QueryDefinitionDescribeEventCodec.Read(sourceArray);

        // Materialize evaluators[] and fieldDependencies[] as typed arrays for the DTO.
        var evaluators = new QueryFieldEvaluatorShapeDto[data.EvaluatorCount];
        var evBlob = data.EvaluatorBlob.Span;
        for (var i = 0; i < data.EvaluatorCount; i++)
        {
            var off = i * QueryDefinitionDescribeEventCodec.EvaluatorEntrySize;
            evaluators[i] = new QueryFieldEvaluatorShapeDto(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(evBlob[off..]), evBlob[off + 2]);
        }

        var fieldDeps = new ushort[data.FieldDependencyCount];
        var fdBlob = data.FieldDependenciesBlob.Span;
        for (var i = 0; i < data.FieldDependencyCount; i++)
        {
            fieldDeps[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(fdBlob[(i * 2)..]);
        }

        return new QueryDefinitionDescribeEventDto
        {
            ThreadSlot = data.ThreadSlot,
            TickNumber = currentTick,
            TimestampUs = data.Timestamp / (double)ticksPerUs,
            Kind = data.Kind,
            LocalId = data.LocalId,
            TargetComponentType = data.TargetComponentType,
            PrimaryIndexFieldIdx = data.PrimaryIndexFieldIdx,
            SortFieldIdx = data.SortFieldIdx,
            SortDescending = data.SortDescending != 0,
            DefinitionSourceFileId = data.DefinitionSourceFileId,
            DefinitionSourceLine = data.DefinitionSourceLine,
            DefinitionSourceMethodId = data.DefinitionSourceMethodId,
            Evaluators = evaluators,
            FieldDependencies = fieldDeps,
        };
    }

    private static QueryArgsEventDto DecodeQueryArgs(ReadOnlySpan<byte> source, int currentTick, long ticksPerUs)
    {
        var sourceArray = source.ToArray();
        var data = QueryArgsEventCodec.Read(sourceArray);

        var thresholds = new long[data.EvaluatorCount];
        var blob = data.ThresholdsBlob.Span;
        for (var i = 0; i < data.EvaluatorCount; i++)
        {
            thresholds[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(blob[(i * 8)..]);
        }

        return new QueryArgsEventDto
        {
            ThreadSlot = data.ThreadSlot,
            TickNumber = currentTick,
            TimestampUs = data.Timestamp / (double)ticksPerUs,
            Thresholds = thresholds,
        };
    }
}

/// <summary>Decoded DTO for a <see cref="TraceEventKind.QueryDefinitionDescribe"/> instant record (#342, v9).</summary>
public sealed record QueryDefinitionDescribeEventDto : TraceEventDto
{
    // ReSharper disable UnusedAutoPropertyAccessor.Global
    public override byte KindByte => (byte)TraceEventKind.QueryDefinitionDescribe;

    /// <summary>
    /// 0 = View, 1 = EcsQuery — discriminator for the (Kind, LocalId) identity pair. Serialized as <c>queryKind</c>: the camelCase of the CLR name (<c>kind</c>)
    /// collides with the polymorphic type-discriminator property the <c>TraceEventDto</c> hierarchy already uses, which makes System.Text.Json reject the whole
    /// hierarchy at metadata-resolution time.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("queryKind")]
    public byte Kind { get; init; }

    /// <summary>ViewId or EcsQueryId — the runtime-assigned identity within its Kind's namespace.</summary>
    public uint LocalId { get; init; }

    public ushort TargetComponentType { get; init; }
    public short PrimaryIndexFieldIdx { get; init; }
    public short SortFieldIdx { get; init; }
    public bool SortDescending { get; init; }

    /// <summary>ID into the trace's <c>QuerySourceStringTable</c> for the file path. 0 = unattributed.</summary>
    public ushort DefinitionSourceFileId { get; init; }
    public int DefinitionSourceLine { get; init; }
    /// <summary>ID into the trace's <c>QuerySourceStringTable</c> for the method name. 0 = unattributed.</summary>
    public ushort DefinitionSourceMethodId { get; init; }

    public QueryFieldEvaluatorShapeDto[] Evaluators { get; init; }
    public ushort[] FieldDependencies { get; init; }
    // ReSharper restore UnusedAutoPropertyAccessor.Global
}

/// <summary>Decoded DTO for a <see cref="TraceEventKind.QueryArgs"/> instant record (#342, v9).</summary>
public sealed record QueryArgsEventDto : TraceEventDto
{
    public override byte KindByte => (byte)TraceEventKind.QueryArgs;
    /// <summary>Widened i64 threshold constants, one per evaluator.</summary>
    public long[] Thresholds { get; init; }
}

/// <summary>Structural evaluator shape carried by <see cref="QueryDefinitionDescribeEventDto"/>. Excludes threshold (per-execution data).</summary>
public sealed record QueryFieldEvaluatorShapeDto(ushort FieldIdx, byte Op);
