// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.TransactionCommit"/>. Required: TSN. Optional: component count, conflict-detected flag.
/// </summary>
/// <summary>
/// Generator-emitted encoder. Wire layout: <c>[i64 tsn][u8 optMask][i32 componentCount?][u8 conflictDetected?]</c> —
/// matches the prior <c>TransactionEventCodec.Encode</c>-emitted bytes 1:1 for the TransactionCommit kind.
/// </summary>
[TraceEvent(TraceEventKind.TransactionCommit, EmitEncoder = true)]
internal ref partial struct TransactionCommitEvent
{
    [BeginParam]
    public long Tsn;

    [Optional(MaskValue = 0x01)]
    private int _componentCount;
    [Optional(MaskValue = 0x02)]
    private bool _conflictDetected;
}

/// <summary>
/// Generator-emitted encoder. Wire layout: <c>[i64 tsn][u8 optMask][i32 componentCount?][u8 reason?]</c> — matches
/// the prior <c>TransactionEventCodec.Encode</c>-emitted bytes 1:1 for the TransactionRollback kind.
/// </summary>
[TraceEvent(TraceEventKind.TransactionRollback, EmitEncoder = true)]
internal ref partial struct TransactionRollbackEvent
{
    [BeginParam]
    public long Tsn;

    [Optional(MaskValue = 0x01)]
    private int _componentCount;

    /// <summary>Phase 6 (D3): rollback reason byte. Setting any value flips the OptReason mask bit so the producer always emits the trailing byte.</summary>
    [Optional(MaskValue = 0x04)]
    private TransactionRollbackReason _reason;
}

/// <summary>
/// Generator-emitted encoder. Wire layout: <c>[i64 tsn][i32 componentTypeId][u8 optMask][i32 rowCount?]</c> — same
/// bytes the prior hand-written <c>TransactionEventCodec.Encode</c> produced when called with
/// <see cref="TraceEventKind.TransactionCommitComponent"/>. The legacy codec gated the i32 ComponentTypeId slot on
/// the kind discriminator; the generator emits it unconditionally because this struct is the only producer of the kind.
/// </summary>
[TraceEvent(TraceEventKind.TransactionCommitComponent, EmitEncoder = true)]
internal ref partial struct TransactionCommitComponentEvent
{
    [BeginParam]
    public long Tsn;
    [BeginParam]
    public int ComponentTypeId;

    /// <summary>Phase 6: number of rows mutated within this component-type's commit. Setting any value flips the OptRowCount mask bit.</summary>
    [Optional(MaskValue = 0x08)]
    private int _rowCount;
}

/// <summary>
/// WAL serialization inside Transaction.Commit. Required: TSN. Optional: walLsn (set after SerializeToWal returns).
/// Wire payload for Persist: <c>[i64 tsn][i64 walLsn][u8 optMask]</c> — walLsn is in the componentTypeId slot (reused as i64)
/// but to keep things clean we use a separate encode path.
/// </summary>
[TraceEvent(TraceEventKind.TransactionPersist, FactoryName = "BeginTransactionPersist", EmitEncoder = true)]
internal ref partial struct TransactionPersistEvent
{
    [BeginParam]
    public long Tsn;

    [Optional(MaskValue = 0x01)]
    private long _walLsn;
}

