using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Reasons for a <see cref="TraceEventKind.TransactionRollback"/>. Wire-stable; append-only (new entries get higher byte values).
/// </summary>
public enum TransactionRollbackReason : byte
{
    /// <summary>Caller invoked <c>Transaction.Rollback()</c> directly.</summary>
    Explicit = 0,

    /// <summary>Implicit rollback from <c>Dispose()</c> when the transaction was never committed.</summary>
    AutoOnDispose = 1,

    /// <summary>Rollback triggered by a concurrency-conflict resolver outcome.</summary>
    Conflict = 2,

    /// <summary>Rollback triggered by a transaction-level deadline / timeout.</summary>
    TimedOut = 3,
}

/// <summary>
/// Decoded form of a transaction span event (Commit, Rollback, CommitComponent, or Persist). Which optional fields are valid depends on the
/// kind — see the per-event ref structs for the contract.
/// </summary>
public readonly struct TransactionEventData
{
    public TraceEventKind Kind { get; }
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }

    /// <summary>Required — transaction sequence number.</summary>
    public long Tsn { get; }

    /// <summary>Required for <see cref="TraceEventKind.TransactionCommitComponent"/>; otherwise zero.</summary>
    public int ComponentTypeId { get; }

    public byte OptionalFieldMask { get; }

    /// <summary>Optional — number of component infos touched. Valid iff <see cref="HasComponentCount"/>.</summary>
    public int ComponentCount { get; }

    /// <summary>Optional — whether a concurrency conflict was detected (Commit only). Valid iff <see cref="HasConflictDetected"/>.</summary>
    public bool ConflictDetected { get; }

    /// <summary>Optional — WAL LSN assigned by serialization (Persist only). Valid iff <see cref="HasWalLsn"/>.</summary>
    public long WalLsn { get; }

    /// <summary>Phase 6: rollback reason (Rollback only). Valid iff <see cref="HasReason"/>.</summary>
    public TransactionRollbackReason Reason { get; }

    /// <summary>Phase 6: row count for CommitComponent (kind 22). Valid iff <see cref="HasRowCount"/>.</summary>
    public int RowCount { get; }

    /// <summary>Source-location id assigned by <c>SourceLocationGenerator</c> (#302). Zero when the wire record didn't carry source attribution.</summary>
    public ushort SourceLocationId { get; }

    public bool HasComponentCount => (OptionalFieldMask & TransactionEventCodec.OptComponentCount) != 0;
    public bool HasConflictDetected => (OptionalFieldMask & TransactionEventCodec.OptConflictDetected) != 0;
    public bool HasWalLsn => Kind == TraceEventKind.TransactionPersist && (OptionalFieldMask & TransactionEventCodec.OptWalLsn) != 0;
    public bool HasReason => (OptionalFieldMask & TransactionEventCodec.OptReason) != 0;
    public bool HasRowCount => (OptionalFieldMask & TransactionEventCodec.OptRowCount) != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public bool HasSourceLocation => SourceLocationId != 0;

    public TransactionEventData(
        TraceEventKind kind, byte threadSlot, long startTimestamp, long durationTicks,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        long tsn, int componentTypeId, byte optionalFieldMask,
        int componentCount, bool conflictDetected, long walLsn = 0,
        TransactionRollbackReason reason = TransactionRollbackReason.Explicit, int rowCount = 0,
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
        Tsn = tsn;
        ComponentTypeId = componentTypeId;
        OptionalFieldMask = optionalFieldMask;
        ComponentCount = componentCount;
        ConflictDetected = conflictDetected;
        WalLsn = walLsn;
        Reason = reason;
        RowCount = rowCount;
        SourceLocationId = sourceLocationId;
    }
}

/// <summary>
/// Shared codec for the four transaction span events. Wire layout differs slightly by kind — CommitComponent includes a <c>ComponentTypeId</c>
/// field in the required payload, Commit/Rollback do not, Persist has its own layout.
/// </summary>
/// <remarks>
/// <para>
/// <b>Payload layout for Commit/Rollback (after span header):</b>
/// <code>
/// [i64 tsn]          // 8 B — required
/// [u8  optMask]      // 1 B
/// [i32 componentCount]?  // 4 B — present iff optMask &amp; OptComponentCount
/// [u8  conflictDetected]? // 1 B — present iff optMask &amp; OptConflictDetected (Commit only)
/// </code>
/// </para>
/// <para>
/// <b>Payload layout for CommitComponent (after span header):</b>
/// <code>
/// [i64 tsn]              // 8 B — required
/// [i32 componentTypeId]  // 4 B — required
/// [u8  optMask]          // 1 B — always 0 (no optionals on this kind)
/// </code>
/// </para>
/// </remarks>
public static class TransactionEventCodec
{
    /// <summary>Optional-mask bit 0 — <c>ComponentCount</c> (Commit, Rollback).</summary>
    public const byte OptComponentCount = 0x01;

    /// <summary>Optional-mask bit 1 — <c>ConflictDetected</c> (Commit only).</summary>
    public const byte OptConflictDetected = 0x02;

    /// <summary>Phase 6 — Optional-mask bit 2 — <c>Reason</c> u8 (Rollback only). Wire-additive on kind 21.</summary>
    public const byte OptReason = 0x04;

    /// <summary>Phase 6 — Optional-mask bit 3 — <c>RowCount</c> i32 (CommitComponent only). Wire-additive on kind 22.</summary>
    public const byte OptRowCount = 0x08;

    /// <summary>Optional-mask bit 0 — <c>WalLsn</c> (Persist only).</summary>
    public const byte OptWalLsn = 0x01;

    private const int TsnSize = 8;
    private const int WalLsnSize = 8;
    private const int ComponentTypeIdSize = 4;
    private const int OptMaskSize = 1;
    private const int ComponentCountSize = 4;
    private const int ConflictDetectedSize = 1;
    private const int ReasonSize = 1;
    private const int RowCountSize = 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(TraceEventKind kind, bool hasTraceContext, byte optMask, ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var size = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation) + TsnSize + OptMaskSize;
        if (kind == TraceEventKind.TransactionCommitComponent)
        {
            size += ComponentTypeIdSize;
        }
        if ((optMask & OptComponentCount) != 0)
        {
            size += ComponentCountSize;
        }
        if ((optMask & OptConflictDetected) != 0)
        {
            size += ConflictDetectedSize;
        }
        if ((optMask & OptReason) != 0)
        {
            size += ReasonSize;
        }
        if ((optMask & OptRowCount) != 0)
        {
            size += RowCountSize;
        }
        return size;
    }

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
        long tsn,
        int componentTypeId,
        byte optMask,
        int componentCount,
        bool conflictDetected,
        out int bytesWritten,
        TransactionRollbackReason reason = TransactionRollbackReason.Explicit,
        int rowCount = 0,
        ushort sourceLocationId = 0)
    {
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var hasSourceLocation = sourceLocationId != 0;
        var size = ComputeSize(kind, hasTraceContext, optMask, sourceLocationId);

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, kind, threadSlot, startTimestamp);
        var spanFlags = (byte)0;
        if (hasTraceContext)
        {
            spanFlags |= TraceRecordHeader.SpanFlagsHasTraceContext;
        }

        if (hasSourceLocation)
        {
            spanFlags |= TraceRecordHeader.SpanFlagsHasSourceLocation;
        }

        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);

        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        var payload = destination[headerSize..];
        BinaryPrimitives.WriteInt64LittleEndian(payload, tsn);
        var cursor = TsnSize;

        if (kind == TraceEventKind.TransactionCommitComponent)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload[cursor..], componentTypeId);
            cursor += ComponentTypeIdSize;
        }

        payload[cursor] = optMask;
        cursor += OptMaskSize;

        if ((optMask & OptComponentCount) != 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload[cursor..], componentCount);
            cursor += ComponentCountSize;
        }
        if ((optMask & OptConflictDetected) != 0)
        {
            payload[cursor] = conflictDetected ? (byte)1 : (byte)0;
            cursor += ConflictDetectedSize;
        }
        if ((optMask & OptReason) != 0)
        {
            payload[cursor] = (byte)reason;
            cursor += ReasonSize;
        }
        if ((optMask & OptRowCount) != 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload[cursor..], rowCount);
            cursor += RowCountSize;
        }

        bytesWritten = size;
    }

    public static TransactionEventData Decode(ReadOnlySpan<byte> source)
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
        var tsn = BinaryPrimitives.ReadInt64LittleEndian(payload);
        var cursor = TsnSize;

        int componentTypeId = 0;
        if (kind == TraceEventKind.TransactionCommitComponent)
        {
            componentTypeId = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
            cursor += ComponentTypeIdSize;
        }

        var optMask = payload[cursor];
        cursor += OptMaskSize;

        int componentCount = 0;
        bool conflictDetected = false;
        TransactionRollbackReason reason = TransactionRollbackReason.Explicit;
        int rowCount = 0;
        if ((optMask & OptComponentCount) != 0)
        {
            componentCount = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
            cursor += ComponentCountSize;
        }
        if ((optMask & OptConflictDetected) != 0)
        {
            conflictDetected = payload[cursor] != 0;
            cursor += ConflictDetectedSize;
        }
        if ((optMask & OptReason) != 0)
        {
            reason = (TransactionRollbackReason)payload[cursor];
            cursor += ReasonSize;
        }
        if ((optMask & OptRowCount) != 0)
        {
            rowCount = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
            cursor += RowCountSize;
        }

        return new TransactionEventData(kind, threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            tsn, componentTypeId, optMask, componentCount, conflictDetected, 0, reason, rowCount, sourceLocationId);
    }

    // ── Persist-specific codec (kind 23) ──
    // Wire layout: [i64 tsn][u8 optMask][i64 walLsn?]

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputePersistSize(bool hasTraceContext, byte optMask, ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var size = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation) + TsnSize + OptMaskSize;
        if ((optMask & OptWalLsn) != 0)
        {
            size += WalLsnSize;
        }

        return size;
    }

    internal static void EncodePersist(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, long tsn, byte optMask, long walLsn, out int bytesWritten, ushort sourceLocationId = 0)
    {
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var hasSourceLocation = sourceLocationId != 0;
        var size = ComputePersistSize(hasTraceContext, optMask, sourceLocationId);

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.TransactionPersist, threadSlot, startTimestamp);
        var spanFlags = (byte)0;
        if (hasTraceContext)
        {
            spanFlags |= TraceRecordHeader.SpanFlagsHasTraceContext;
        }

        if (hasSourceLocation)
        {
            spanFlags |= TraceRecordHeader.SpanFlagsHasSourceLocation;
        }

        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);

        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        var payload = destination[headerSize..];
        BinaryPrimitives.WriteInt64LittleEndian(payload, tsn);
        var cursor = TsnSize;
        payload[cursor] = optMask;
        cursor += OptMaskSize;
        if ((optMask & OptWalLsn) != 0)
        {
            BinaryPrimitives.WriteInt64LittleEndian(payload[cursor..], walLsn);
            cursor += WalLsnSize;
        }

        bytesWritten = size;
    }

    public static TransactionEventData DecodePersist(ReadOnlySpan<byte> source)
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
        var tsn = BinaryPrimitives.ReadInt64LittleEndian(payload);
        var cursor = TsnSize;
        var optMask = payload[cursor];
        cursor += OptMaskSize;

        long walLsn = 0;
        if ((optMask & OptWalLsn) != 0)
        {
            walLsn = BinaryPrimitives.ReadInt64LittleEndian(payload[cursor..]);
            cursor += WalLsnSize;
        }

        return new TransactionEventData(kind, threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            tsn, componentTypeId: 0, optMask, componentCount: 0, conflictDetected: false, walLsn: walLsn, sourceLocationId: sourceLocationId);
    }
}

