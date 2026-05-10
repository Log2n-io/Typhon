// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.TransactionCommit"/>. Required: TSN. Optional: component count, conflict-detected flag.
/// </summary>
[TraceEvent(TraceEventKind.TransactionCommit, Codec = typeof(TransactionEventCodec))]
internal ref partial struct TransactionCommitEvent
{
    [BeginParam]
    public long Tsn;

    [Optional]
    private int _componentCount;
    [Optional]
    private bool _conflictDetected;
    public readonly int ComputeSize()
        => TransactionEventCodec.ComputeSize(TraceEventKind.TransactionCommit, Header.TraceIdHi != 0 || Header.TraceIdLo != 0, _optMask, Header.SourceLocationId);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => TransactionEventCodec.Encode(destination, endTimestamp, TraceEventKind.TransactionCommit, Header.ThreadSlot, Header.StartTimestamp,
            Header.SpanId, Header.ParentSpanId, Header.TraceIdHi, Header.TraceIdLo, Tsn, 0, _optMask, _componentCount, _conflictDetected, out bytesWritten,
            sourceLocationId: Header.SourceLocationId);
}

[TraceEvent(TraceEventKind.TransactionRollback, Codec = typeof(TransactionEventCodec))]
internal ref partial struct TransactionRollbackEvent
{
    [BeginParam]
    public long Tsn;

    [Optional]
    private int _componentCount;

    /// <summary>Phase 6 (D3): rollback reason byte. Setting any value flips the OptReason mask bit so the producer always emits the trailing byte.</summary>
    [Optional]
    private TransactionRollbackReason _reason;

    public readonly int ComputeSize()
        => TransactionEventCodec.ComputeSize(TraceEventKind.TransactionRollback, Header.TraceIdHi != 0 || Header.TraceIdLo != 0, _optMask, Header.SourceLocationId);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => TransactionEventCodec.Encode(destination, endTimestamp, TraceEventKind.TransactionRollback, Header.ThreadSlot, Header.StartTimestamp,
            Header.SpanId, Header.ParentSpanId, Header.TraceIdHi, Header.TraceIdLo, Tsn, 0, _optMask, _componentCount, false, out bytesWritten, _reason,
            sourceLocationId: Header.SourceLocationId);
}

[TraceEvent(TraceEventKind.TransactionCommitComponent, Codec = typeof(TransactionEventCodec))]
internal ref partial struct TransactionCommitComponentEvent
{
    [BeginParam]
    public long Tsn;
    [BeginParam]
    public int ComponentTypeId;

    /// <summary>Phase 6: number of rows mutated within this component-type's commit. Setting any value flips the OptRowCount mask bit.</summary>
    [Optional]
    private int _rowCount;

    public readonly int ComputeSize()
        => TransactionEventCodec.ComputeSize(TraceEventKind.TransactionCommitComponent, Header.TraceIdHi != 0 || Header.TraceIdLo != 0, _optMask, Header.SourceLocationId);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => TransactionEventCodec.Encode(destination, endTimestamp, TraceEventKind.TransactionCommitComponent, Header.ThreadSlot, Header.StartTimestamp,
            Header.SpanId, Header.ParentSpanId, Header.TraceIdHi, Header.TraceIdLo, Tsn, ComponentTypeId, _optMask, componentCount: 0, conflictDetected: false,
            out bytesWritten, rowCount: _rowCount, sourceLocationId: Header.SourceLocationId);
}

/// <summary>
/// WAL serialization inside Transaction.Commit. Required: TSN. Optional: walLsn (set after SerializeToWal returns).
/// Wire payload for Persist: <c>[i64 tsn][i64 walLsn][u8 optMask]</c> — walLsn is in the componentTypeId slot (reused as i64)
/// but to keep things clean we use a separate encode path.
/// </summary>
[TraceEvent(TraceEventKind.TransactionRollback, Codec = typeof(TransactionEventCodec), FactoryName = "BeginTransactionPersist")]
internal ref partial struct TransactionPersistEvent
{
    [BeginParam]
    public long Tsn;

    [Optional]
    private long _walLsn;
    public readonly int ComputeSize()
        => TransactionEventCodec.ComputePersistSize(Header.TraceIdHi != 0 || Header.TraceIdLo != 0, _optMask, Header.SourceLocationId);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => TransactionEventCodec.EncodePersist(destination, endTimestamp, Header.ThreadSlot, Header.StartTimestamp,
            Header.SpanId, Header.ParentSpanId, Header.TraceIdHi, Header.TraceIdLo, Tsn, _optMask, _walLsn, out bytesWritten,
            sourceLocationId: Header.SourceLocationId);
}

