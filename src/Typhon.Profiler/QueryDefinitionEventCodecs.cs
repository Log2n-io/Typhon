using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

// ═══════════════════════════════════════════════════════════════════════════════════════
// Codecs for the v9 Query Definition Export instant events (#342, sub-issues #334/#335/#336).
//
// Both events are instant-shape (12-byte common header + variable payload, no span extension). Their payloads carry arrays — the [TraceEvent] source generator
// doesn't support variable-length payloads, so we hand-code the codecs here. The wire layout is owned by these codecs and mirrored in TraceEventDecoder.HandGlue.cs.
//
// See claude/design/Profiler/11-query-definition-export.md §4.5, §4.6.
// ═══════════════════════════════════════════════════════════════════════════════════════

/// <summary>Decoded form of a <see cref="TraceEventKind.QueryDefinitionDescribe"/> record.</summary>
public readonly struct QueryDefinitionDescribeData
{
    /// <summary>Typhon thread slot the definition was emitted on.</summary>
    public byte ThreadSlot { get; }

    /// <summary>Emit timestamp, in Stopwatch ticks.</summary>
    public long Timestamp { get; }

    /// <summary>Definition kind: 0 = View, 1 = EcsQuery.</summary>
    public byte Kind { get; }                       // 0 = View, 1 = EcsQuery

    /// <summary>Local identifier — a ViewId or EcsQueryId depending on <see cref="Kind"/>.</summary>
    public uint LocalId { get; }                    // ViewId or EcsQueryId

    /// <summary>Component type id the query targets.</summary>
    public ushort TargetComponentType { get; }

    /// <summary>Field index used for the primary index scan, or <c>-1</c> when there is no index scan.</summary>
    public short PrimaryIndexFieldIdx { get; }      // -1 = no index scan

    /// <summary>Field index the results are sorted on, or <c>-1</c> when unsorted.</summary>
    public short SortFieldIdx { get; }              // -1 = unsorted

    /// <summary>Sort direction: non-zero for descending, 0 for ascending.</summary>
    public byte SortDescending { get; }

    /// <summary>Interned file id of the query's definition site (indexes the trace's <c>FileTable</c>).</summary>
    public ushort DefinitionSourceFileId { get; }

    /// <summary>Source line of the query's definition site.</summary>
    public int DefinitionSourceLine { get; }

    /// <summary>Interned method id of the query's definition site.</summary>
    public ushort DefinitionSourceMethodId { get; }

    /// <summary>Per-evaluator shape: 4 bytes each (FieldIdx u16, Op u8, Reserved u8). Total bytes = <c>EvaluatorCount * 4</c>.</summary>
    public ReadOnlyMemory<byte> EvaluatorBlob { get; }

    /// <summary>Number of evaluator entries packed in <see cref="EvaluatorBlob"/>.</summary>
    public ushort EvaluatorCount { get; }

    /// <summary>Per-field-dep: 2 bytes each (u16). Total bytes = <c>FieldDependencyCount * 2</c>.</summary>
    public ReadOnlyMemory<byte> FieldDependenciesBlob { get; }

    /// <summary>Number of field-dependency entries packed in <see cref="FieldDependenciesBlob"/>.</summary>
    public ushort FieldDependencyCount { get; }

    /// <summary>Construct a decoded query-definition descriptor.</summary>
    /// <param name="threadSlot">Typhon thread slot.</param>
    /// <param name="timestamp">Emit timestamp, in Stopwatch ticks.</param>
    /// <param name="kind">0 = View, 1 = EcsQuery.</param>
    /// <param name="localId">ViewId or EcsQueryId, per <paramref name="kind"/>.</param>
    /// <param name="targetComponentType">Component type id the query targets.</param>
    /// <param name="primaryIndexFieldIdx">Primary index-scan field index, or <c>-1</c>.</param>
    /// <param name="sortFieldIdx">Sort field index, or <c>-1</c>.</param>
    /// <param name="sortDescending">Non-zero for descending sort.</param>
    /// <param name="definitionSourceFileId">Interned file id of the definition site.</param>
    /// <param name="definitionSourceLine">Source line of the definition site.</param>
    /// <param name="definitionSourceMethodId">Interned method id of the definition site.</param>
    /// <param name="evaluatorCount">Number of evaluator entries in <paramref name="evaluatorBlob"/>.</param>
    /// <param name="evaluatorBlob">Packed evaluator entries (4 bytes each).</param>
    /// <param name="fieldDependencyCount">Number of field-dependency entries in <paramref name="fieldDependenciesBlob"/>.</param>
    /// <param name="fieldDependenciesBlob">Packed field-dependency entries (2 bytes each).</param>
    public QueryDefinitionDescribeData(byte threadSlot, long timestamp, byte kind, uint localId, ushort targetComponentType, short primaryIndexFieldIdx,
        short sortFieldIdx, byte sortDescending, ushort definitionSourceFileId, int definitionSourceLine, ushort definitionSourceMethodId,
        ushort evaluatorCount, ReadOnlyMemory<byte> evaluatorBlob, ushort fieldDependencyCount, ReadOnlyMemory<byte> fieldDependenciesBlob)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        Kind = kind;
        LocalId = localId;
        TargetComponentType = targetComponentType;
        PrimaryIndexFieldIdx = primaryIndexFieldIdx;
        SortFieldIdx = sortFieldIdx;
        SortDescending = sortDescending;
        DefinitionSourceFileId = definitionSourceFileId;
        DefinitionSourceLine = definitionSourceLine;
        DefinitionSourceMethodId = definitionSourceMethodId;
        EvaluatorCount = evaluatorCount;
        EvaluatorBlob = evaluatorBlob;
        FieldDependencyCount = fieldDependencyCount;
        FieldDependenciesBlob = fieldDependenciesBlob;
    }
}

/// <summary>
/// Wire codec for <see cref="TraceEventKind.QueryDefinitionDescribe"/> (kind 247) — one-shot definition
/// descriptor emitted once per ViewId/EcsQueryId per profiling session. Wire format owned here.
/// </summary>
/// <remarks>
/// Layout after the 12-byte common header:
/// <code>
/// offset 12       u8   Kind                       (0=View, 1=EcsQuery)
/// offset 13..16   u32  LocalId
/// offset 17..18   u16  TargetComponentType
/// offset 19..20   i16  PrimaryIndexFieldIdx
/// offset 21..22   i16  SortFieldIdx
/// offset 23       u8   SortDescending
/// offset 24..25   u16  DefinitionSourceFileId
/// offset 26..29   i32  DefinitionSourceLine
/// offset 30..31   u16  DefinitionSourceMethodId
/// offset 32..33   u16  EvaluatorCount
/// offset 34+      repeated { u16 fieldIdx; u8 op; u8 reserved } × EvaluatorCount
/// offset N..N+1   u16  FieldDependencyCount
/// offset N+2..    repeated u16 fieldDependency  × FieldDependencyCount
/// </code>
/// </remarks>
public static class QueryDefinitionDescribeEventCodec
{
    /// <summary>Fixed-prefix size: 12 B common header + 22 B fixed fields + 2 B EvaluatorCount = 36 B.</summary>
    public const int FixedPrefixSize = TraceRecordHeader.CommonHeaderSize + 1 + 4 + 2 + 2 + 2 + 1 + 2 + 4 + 2 + 2;

    /// <summary>Bytes per evaluator entry: u16 fieldIdx + u8 op + u8 reserved.</summary>
    public const int EvaluatorEntrySize = 4;

    /// <summary>Bytes per field-dependency entry: u16.</summary>
    public const int FieldDependencyEntrySize = 2;

    /// <summary>Compute the exact on-wire size for the given evaluator + field-dependency counts.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(int evaluatorCount, int fieldDependencyCount) =>
        FixedPrefixSize + evaluatorCount * EvaluatorEntrySize + 2 /* FieldDependencyCount */ + fieldDependencyCount * FieldDependencyEntrySize;

    /// <summary>
    /// Encode a <see cref="TraceEventKind.QueryDefinitionDescribe"/> record. <paramref name="evaluators"/> is a packed blob of
    /// <see cref="EvaluatorEntrySize"/>-byte entries (FieldIdx u16, Op u8, Reserved u8). <paramref name="fieldDependencies"/> is a
    /// packed blob of u16 field indices.
    /// </summary>
    public static void Write(Span<byte> destination, byte threadSlot, long timestamp, byte kind, uint localId, ushort targetComponentType,
        short primaryIndexFieldIdx, short sortFieldIdx, byte sortDescending, ushort definitionSourceFileId, int definitionSourceLine,
        ushort definitionSourceMethodId, ReadOnlySpan<byte> evaluators, ReadOnlySpan<byte> fieldDependencies, out int bytesWritten)
    {
        if (evaluators.Length % EvaluatorEntrySize != 0)
        {
            throw new ArgumentException($"Evaluator blob length {evaluators.Length} is not a multiple of {EvaluatorEntrySize}", nameof(evaluators));
        }
        if (fieldDependencies.Length % FieldDependencyEntrySize != 0)
        {
            throw new ArgumentException($"FieldDependencies blob length {fieldDependencies.Length} is not a multiple of {FieldDependencyEntrySize}",
                nameof(fieldDependencies));
        }

        var evaluatorCount = evaluators.Length / EvaluatorEntrySize;
        var fieldDependencyCount = fieldDependencies.Length / FieldDependencyEntrySize;
        if (evaluatorCount > ushort.MaxValue)
        {
            throw new ArgumentException($"EvaluatorCount {evaluatorCount} exceeds {ushort.MaxValue}", nameof(evaluators));
        }
        if (fieldDependencyCount > ushort.MaxValue)
        {
            throw new ArgumentException($"FieldDependencyCount {fieldDependencyCount} exceeds {ushort.MaxValue}", nameof(fieldDependencies));
        }

        var size = ComputeSize(evaluatorCount, fieldDependencyCount);
        if (size > ushort.MaxValue)
        {
            throw new ArgumentException($"Record size {size} exceeds max record size {ushort.MaxValue}", nameof(evaluators));
        }

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.QueryDefinitionDescribe, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(p[1..], localId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[5..], targetComponentType);
        BinaryPrimitives.WriteInt16LittleEndian(p[7..], primaryIndexFieldIdx);
        BinaryPrimitives.WriteInt16LittleEndian(p[9..], sortFieldIdx);
        p[11] = sortDescending;
        BinaryPrimitives.WriteUInt16LittleEndian(p[12..], definitionSourceFileId);
        BinaryPrimitives.WriteInt32LittleEndian(p[14..], definitionSourceLine);
        BinaryPrimitives.WriteUInt16LittleEndian(p[18..], definitionSourceMethodId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[20..], (ushort)evaluatorCount);
        var offset = 22;
        evaluators.CopyTo(p[offset..]);
        offset += evaluators.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(p[offset..], (ushort)fieldDependencyCount);
        offset += 2;
        fieldDependencies.CopyTo(p[offset..]);

        bytesWritten = size;
    }

    /// <summary>Decode a <see cref="TraceEventKind.QueryDefinitionDescribe"/> record.</summary>
    public static QueryDefinitionDescribeData Read(ReadOnlyMemory<byte> source)
    {
        var span = source.Span;
        TraceRecordHeader.ReadCommonHeader(span, out var size, out var kindEnum, out var threadSlot, out var timestamp);
        if (kindEnum != TraceEventKind.QueryDefinitionDescribe)
        {
            throw new ArgumentException($"Expected QueryDefinitionDescribe, got {kindEnum}", nameof(source));
        }
        if (size > span.Length)
        {
            throw new ArgumentException($"Record size {size} exceeds source buffer {span.Length}", nameof(source));
        }

        var p = span[TraceRecordHeader.CommonHeaderSize..size];
        var kind = p[0];
        var localId = BinaryPrimitives.ReadUInt32LittleEndian(p[1..]);
        var targetComponentType = BinaryPrimitives.ReadUInt16LittleEndian(p[5..]);
        var primaryIndexFieldIdx = BinaryPrimitives.ReadInt16LittleEndian(p[7..]);
        var sortFieldIdx = BinaryPrimitives.ReadInt16LittleEndian(p[9..]);
        var sortDescending = p[11];
        var definitionSourceFileId = BinaryPrimitives.ReadUInt16LittleEndian(p[12..]);
        var definitionSourceLine = BinaryPrimitives.ReadInt32LittleEndian(p[14..]);
        var definitionSourceMethodId = BinaryPrimitives.ReadUInt16LittleEndian(p[18..]);
        var evaluatorCount = BinaryPrimitives.ReadUInt16LittleEndian(p[20..]);
        var offset = 22;
        var evaluatorBytes = evaluatorCount * EvaluatorEntrySize;
        var evaluatorBlob = source.Slice(TraceRecordHeader.CommonHeaderSize + offset, evaluatorBytes);
        offset += evaluatorBytes;
        var fieldDependencyCount = BinaryPrimitives.ReadUInt16LittleEndian(p[offset..]);
        offset += 2;
        var fieldDependencyBytes = fieldDependencyCount * FieldDependencyEntrySize;
        var fieldDependenciesBlob = source.Slice(TraceRecordHeader.CommonHeaderSize + offset, fieldDependencyBytes);

        return new QueryDefinitionDescribeData(threadSlot, timestamp, kind, localId, targetComponentType, primaryIndexFieldIdx, sortFieldIdx,
            sortDescending, definitionSourceFileId, definitionSourceLine, definitionSourceMethodId, evaluatorCount, evaluatorBlob,
            fieldDependencyCount, fieldDependenciesBlob);
    }
}

/// <summary>Decoded form of a <see cref="TraceEventKind.QueryArgs"/> record.</summary>
public readonly struct QueryArgsData
{
    /// <summary>Typhon thread slot the args were emitted on.</summary>
    public byte ThreadSlot { get; }

    /// <summary>Emit timestamp, in Stopwatch ticks.</summary>
    public long Timestamp { get; }

    /// <summary>Number of threshold constants packed in <see cref="ThresholdsBlob"/>.</summary>
    public ushort EvaluatorCount { get; }

    /// <summary>Packed array of i64 widened threshold constants — <c>EvaluatorCount * 8</c> bytes.</summary>
    public ReadOnlyMemory<byte> ThresholdsBlob { get; }

    /// <summary>Construct decoded per-execution query args.</summary>
    /// <param name="threadSlot">Typhon thread slot.</param>
    /// <param name="timestamp">Emit timestamp, in Stopwatch ticks.</param>
    /// <param name="evaluatorCount">Number of thresholds in <paramref name="thresholdsBlob"/>.</param>
    /// <param name="thresholdsBlob">Packed i64 threshold constants (8 bytes each).</param>
    public QueryArgsData(byte threadSlot, long timestamp, ushort evaluatorCount, ReadOnlyMemory<byte> thresholdsBlob)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        EvaluatorCount = evaluatorCount;
        ThresholdsBlob = thresholdsBlob;
    }

    /// <summary>Read the i-th threshold from the packed blob.</summary>
    public long GetThreshold(int index) => BinaryPrimitives.ReadInt64LittleEndian(ThresholdsBlob.Span[(index * 8)..]);
}

/// <summary>
/// Wire codec for <see cref="TraceEventKind.QueryArgs"/> (kind 248) — per-execution threshold arguments
/// emitted once per <see cref="TraceEventKind.QueryPlan"/> when the plan carries at least one evaluator.
/// </summary>
/// <remarks>
/// Layout after the 12-byte common header:
/// <code>
/// offset 12..13   u16  EvaluatorCount
/// offset 14+      repeated i64 threshold × EvaluatorCount
/// </code>
/// Skipped entirely when EvaluatorCount == 0 (pure index scan with no filter chain).
/// </remarks>
public static class QueryArgsEventCodec
{
    /// <summary>Fixed-prefix size: 12 B common header + 2 B EvaluatorCount = 14 B.</summary>
    public const int FixedPrefixSize = TraceRecordHeader.CommonHeaderSize + 2;

    /// <summary>Bytes per threshold entry (i64).</summary>
    public const int ThresholdSize = 8;

    /// <summary>Compute the exact on-wire size for the given number of threshold entries.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(int evaluatorCount) => FixedPrefixSize + evaluatorCount * ThresholdSize;

    /// <summary>
    /// Encode a <see cref="TraceEventKind.QueryArgs"/> record. <paramref name="thresholds"/> is a packed blob of
    /// i64 values — caller has already widened/reinterpreted from any source type (float, double, etc.).
    /// </summary>
    public static void Write(Span<byte> destination, byte threadSlot, long timestamp, ReadOnlySpan<byte> thresholds, out int bytesWritten)
    {
        if (thresholds.Length % ThresholdSize != 0)
        {
            throw new ArgumentException($"Thresholds blob length {thresholds.Length} is not a multiple of {ThresholdSize}", nameof(thresholds));
        }

        var evaluatorCount = thresholds.Length / ThresholdSize;
        if (evaluatorCount > ushort.MaxValue)
        {
            throw new ArgumentException($"EvaluatorCount {evaluatorCount} exceeds {ushort.MaxValue}", nameof(thresholds));
        }

        var size = ComputeSize(evaluatorCount);
        if (size > ushort.MaxValue)
        {
            throw new ArgumentException($"Record size {size} exceeds max record size {ushort.MaxValue}", nameof(thresholds));
        }

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.QueryArgs, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)evaluatorCount);
        thresholds.CopyTo(p[2..]);

        bytesWritten = size;
    }

    /// <summary>Decode a <see cref="TraceEventKind.QueryArgs"/> record.</summary>
    public static QueryArgsData Read(ReadOnlyMemory<byte> source)
    {
        var span = source.Span;
        TraceRecordHeader.ReadCommonHeader(span, out var size, out var kindEnum, out var threadSlot, out var timestamp);
        if (kindEnum != TraceEventKind.QueryArgs)
        {
            throw new ArgumentException($"Expected QueryArgs, got {kindEnum}", nameof(source));
        }
        if (size > span.Length)
        {
            throw new ArgumentException($"Record size {size} exceeds source buffer {span.Length}", nameof(source));
        }

        var p = span[TraceRecordHeader.CommonHeaderSize..size];
        var evaluatorCount = BinaryPrimitives.ReadUInt16LittleEndian(p);
        var thresholdsBlob = source.Slice(FixedPrefixSize, evaluatorCount * ThresholdSize);

        return new QueryArgsData(threadSlot, timestamp, evaluatorCount, thresholdsBlob);
    }
}
