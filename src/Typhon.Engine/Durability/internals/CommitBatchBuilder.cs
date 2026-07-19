using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

// CommitBatchBuilder + its pooled arena. See claude/design/Durability/MinimalWal/01-architecture.md §4.
// The builder is created per commit; the arena is pooled per transaction (reset, not realloc). Entity ids are raw longs on this seam (48-bit key + 16-bit
// per-DB routing id) and component identity is the per-archetype SLOT — the Durability layer stays free of ECS types (the caller resolves the slot), and the
// codec/builder become a closed, schema-free unit the property tests can exercise without an engine.

/// <summary>One staged batch entry. Payload/handle bytes live in the <see cref="CommitBatchArena"/> via offsets.</summary>
internal struct BatchEntry
{
    public RecordKind Kind;
    public byte Op;                 // SlotOp / LifecycleOp / CollectionOp
    public byte Bucket;             // LOG-07 emission order: 0=Spawn, 1=Slot/CollectionDelta, 2=Destroy/SetEnabledBits, 3=BulkManifest
    public long EntityId;
    public ushort SlotIndex;        // Slot/CollectionDelta: per-archetype component slot (0..15) — durable identity, resolved via EntityId's routing id
    public ushort FieldId;
    public ushort ArchetypeId;
    public ushort EnabledBits;
    public int Index;

    public int PayloadOffset;       // into arena payload buffer (Slot payload / CollectionDelta element)
    public int PayloadLength;
    public int HandleRangeOffset;   // into arena handle-range buffer (Slot only)
    public int HandleRangeCount;

    // BulkManifest fields
    public long BulkSessionId;
    public long BulkBeginLsn;
    public long EntityCount;
    public long ComponentCount;
}

/// <summary>
/// Pooled backing store for a <see cref="CommitBatchBuilder"/> — a bump-allocated payload arena (grow-by-doubling,
/// reset at tx dispose), a parallel handle-range arena, and the call-order entry list. One instance per transaction,
/// reused across commits via <see cref="Reset"/> (capacity retained, no per-commit allocation after warm-up).
/// </summary>
[PublicAPI]
internal sealed class CommitBatchArena
{
    private const int InitialPayloadCapacity = 64 * 1024;

    private byte[] _payload = new byte[InitialPayloadCapacity];
    private int _payloadUsed;
    private uint[] _handleRanges = new uint[64];
    private int _handleRangesUsed;
    private readonly List<BatchEntry> _entries = [];

    internal List<BatchEntry> Entries => _entries;

    /// <summary>Resets the arena for reuse by the next commit. Retains buffer capacity.</summary>
    internal void Reset()
    {
        _payloadUsed = 0;
        _handleRangesUsed = 0;
        _entries.Clear();
    }

    /// <summary>Copies a payload span into the arena; returns its offset.</summary>
    internal int AppendPayload(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return _payloadUsed;
        }

        EnsurePayloadCapacity(_payloadUsed + data.Length);
        var offset = _payloadUsed;
        data.CopyTo(_payload.AsSpan(offset));
        _payloadUsed += data.Length;
        return offset;
    }

    /// <summary>Copies packed handle ranges (each = offset&lt;&lt;16 | length) into the arena; returns their offset.</summary>
    internal int AppendHandleRanges(ReadOnlySpan<uint> ranges)
    {
        if (ranges.Length == 0)
        {
            return _handleRangesUsed;
        }

        EnsureHandleRangeCapacity(_handleRangesUsed + ranges.Length);
        var offset = _handleRangesUsed;
        ranges.CopyTo(_handleRanges.AsSpan(offset));
        _handleRangesUsed += ranges.Length;
        return offset;
    }

    internal ReadOnlySpan<byte> Payload(int offset, int length) => _payload.AsSpan(offset, length);

    internal ReadOnlySpan<uint> HandleRanges(int offset, int count) => _handleRanges.AsSpan(offset, count);

    private void EnsurePayloadCapacity(int needed)
    {
        if (needed <= _payload.Length)
        {
            return;
        }

        var newCapacity = _payload.Length;
        while (newCapacity < needed)
        {
            newCapacity *= 2;
        }

        Array.Resize(ref _payload, newCapacity);
    }

    private void EnsureHandleRangeCapacity(int needed)
    {
        if (needed <= _handleRanges.Length)
        {
            return;
        }

        var newCapacity = _handleRanges.Length;
        while (newCapacity < needed)
        {
            newCapacity *= 2;
        }

        Array.Resize(ref _handleRanges, newCapacity);
    }
}

/// <summary>
/// Builds one transaction's WAL v2 record batch (01 §4). Entries are bucketed by category as they are added, so the
/// codec always emits them in LOG-07 order (Spawn → Slot/CollectionDelta → Destroy/SetEnabledBits → BulkManifest) —
/// a mis-ordered batch is unconstructible by API shape (A1.17). Created per commit over a pooled <see cref="CommitBatchArena"/>.
/// </summary>
[PublicAPI]
internal ref struct CommitBatchBuilder
{
    private readonly CommitBatchArena _arena;

    /// <summary>Committing transaction TSN (also the fence-cycle TSN for fence batches).</summary>
    public readonly long Tsn;

    /// <summary>Diagnostic UoW epoch stamped on every record.</summary>
    public readonly ushort UowEpoch;

    /// <summary>When set, the batch is a fence batch — records carry <see cref="RecordFlags.FenceRecord"/>, no Tx markers.</summary>
    public readonly bool FenceMode;

    /// <summary>When set, records carry <see cref="RecordFlags.Committed"/> (Committed discipline).</summary>
    public readonly bool CommittedDiscipline;

    public CommitBatchBuilder(CommitBatchArena arena, long tsn, ushort uowEpoch, bool fenceMode = false, bool committedDiscipline = false)
    {
        ArgumentNullException.ThrowIfNull(arena);
        _arena = arena;
        Tsn = tsn;
        UowEpoch = uowEpoch;
        FenceMode = fenceMode;
        CommittedDiscipline = committedDiscipline;
    }

    /// <summary>Number of records the batch will emit.</summary>
    public readonly int EntryCount => _arena.Entries.Count;

    /// <summary>True when nothing has been added — Append is skipped entirely (01 §5, M1 guard).</summary>
    public readonly bool IsEmpty => _arena.Entries.Count == 0;

    internal readonly CommitBatchArena Arena => _arena;

    public readonly void AddSpawn(long entityId, ushort archetypeId, ushort enabledBits) =>
        _arena.Entries.Add(new BatchEntry
        {
            Kind = RecordKind.Lifecycle, Op = (byte)LifecycleOp.Spawn, Bucket = 0,
            EntityId = entityId, ArchetypeId = archetypeId, EnabledBits = enabledBits,
        });

    public readonly void AddDestroy(long entityId) =>
        _arena.Entries.Add(new BatchEntry
        {
            Kind = RecordKind.Lifecycle, Op = (byte)LifecycleOp.Destroy, Bucket = 2, EntityId = entityId,
        });

    public readonly void AddEnabledBits(long entityId, ushort absoluteBits) =>
        _arena.Entries.Add(new BatchEntry
        {
            Kind = RecordKind.Lifecycle, Op = (byte)LifecycleOp.SetEnabledBits, Bucket = 2,
            EntityId = entityId, EnabledBits = absoluteBits,
        });

    public readonly void AddSlot(long entityId, ushort slotIndex, ReadOnlySpan<byte> payload, ReadOnlySpan<uint> handleRanges = default)
    {
        var payloadOffset = _arena.AppendPayload(payload);
        var handleRangeOffset = _arena.AppendHandleRanges(handleRanges);
        _arena.Entries.Add(new BatchEntry
        {
            Kind = RecordKind.Slot, Op = (byte)SlotOp.Upsert, Bucket = 1,
            EntityId = entityId, SlotIndex = slotIndex,
            PayloadOffset = payloadOffset, PayloadLength = payload.Length,
            HandleRangeOffset = handleRangeOffset, HandleRangeCount = handleRanges.Length,
        });
    }

    public readonly void AddCollectionDelta(long entityId, ushort slotIndex, ushort fieldId, CollectionOp op, int index, ReadOnlySpan<byte> element)
    {
        var payloadOffset = _arena.AppendPayload(element);
        _arena.Entries.Add(new BatchEntry
        {
            Kind = RecordKind.CollectionDelta, Op = (byte)op, Bucket = 1,
            EntityId = entityId, SlotIndex = slotIndex, FieldId = fieldId, Index = index,
            PayloadOffset = payloadOffset, PayloadLength = element.Length,
        });
    }

    public readonly void AddBulkManifest(long sessionId, long bulkBeginLsn, long entityCount, long componentCount) =>
        _arena.Entries.Add(new BatchEntry
        {
            Kind = RecordKind.BulkManifest, Op = 0, Bucket = 3,
            BulkSessionId = sessionId, BulkBeginLsn = bulkBeginLsn, EntityCount = entityCount, ComponentCount = componentCount,
        });
}
