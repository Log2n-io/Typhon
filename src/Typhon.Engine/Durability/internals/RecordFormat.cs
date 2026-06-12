using JetBrains.Annotations;
using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

// WAL v2 record format — logical-truth-only records. See claude/design/Durability/MinimalWal/02-wal-format.md.
// The log addresses (EntityId, ComponentTypeId) only — never pages, chunks, or bufferIds (LOG-06). One codec
// (RecordCodec) is the sole reader/writer of these bytes (LOG-02). All multi-byte fields little-endian; layouts
// are exact and binding — changing one is a format-version bump.

/// <summary>Discriminates the four WAL v2 record kinds (02 §3.0, RecordHeader.RecordKind).</summary>
[PublicAPI]
internal enum RecordKind : byte
{
    /// <summary>Unset / invalid.</summary>
    None = 0,

    /// <summary>Component value upsert (<see cref="SlotRecordBody"/>).</summary>
    Slot = 1,

    /// <summary>Entity lifecycle: spawn / destroy / set-enabled-bits (<see cref="LifecycleRecordBody"/>).</summary>
    Lifecycle = 2,

    /// <summary>Component-collection delta (<see cref="CollectionDeltaRecordBody"/>).</summary>
    CollectionDelta = 3,

    /// <summary>Bulk-load session manifest (<see cref="BulkManifestRecordBody"/>).</summary>
    BulkManifest = 4,
}

/// <summary>Record-header flag bits (02 §3.0, RecordHeader.Flags).</summary>
[Flags]
[PublicAPI]
internal enum RecordFlags : byte
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>First record of a transaction's batch.</summary>
    TxBegin = 1 << 0,

    /// <summary>Last record of a transaction's batch — the commit marker (LOG-04). A 1-record batch carries both.</summary>
    TxCommit = 1 << 1,

    /// <summary>Durability discipline: 0 = Versioned, set = Committed.</summary>
    Committed = 1 << 2,

    /// <summary>Fence record — individually committed; carries no Tx markers (LOG-04).</summary>
    FenceRecord = 1 << 3,
}

/// <summary>Slot-record operation (02 §3.1). v1 of the format emits only Upsert.</summary>
[PublicAPI]
internal enum SlotOp : byte
{
    /// <summary>Upsert the component's committed value.</summary>
    Upsert = 1,

    /// <summary>Reserved for a future detach/delete op.</summary>
    Delete = 2,
}

/// <summary>Lifecycle-record operation (02 §3.2).</summary>
[PublicAPI]
internal enum LifecycleOp : byte
{
    /// <summary>Spawn an entity into an archetype.</summary>
    Spawn = 1,

    /// <summary>Destroy an entity.</summary>
    Destroy = 2,

    /// <summary>Set the entity's enabled-bits to an absolute value.</summary>
    SetEnabledBits = 3,
}

/// <summary>Collection-delta operation (02 §3.3).</summary>
[PublicAPI]
internal enum CollectionOp : byte
{
    /// <summary>Clear all elements.</summary>
    Clear = 1,

    /// <summary>Append one element.</summary>
    Append = 2,

    /// <summary>Remove the element at Index.</summary>
    RemoveAt = 3,

    /// <summary>Replace the element at Index.</summary>
    UpdateAt = 4,

    /// <summary>Truncate/extend to Index elements.</summary>
    SetCount = 5,
}

/// <summary>
/// 24-byte common record prefix (02 §3.0). Followed by a kind-specific body of <see cref="BodyLength"/> bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[PublicAPI]
internal struct RecordHeader
{
    /// <summary>Log Sequence Number — assigned at Append from the claim's range, ascending per record.</summary>
    public long LSN;

    /// <summary>Committing transaction TSN; fence records carry the fence-cycle TSN snapshot.</summary>
    public long TSN;

    /// <summary>Diagnostic UoW epoch + grouping; NOT used for commit fate in v2 (LOG-04 uses markers).</summary>
    public ushort UowEpoch;

    /// <summary>Record kind (<see cref="RecordKind"/>).</summary>
    public byte RecordKind;

    /// <summary>Flags (<see cref="RecordFlags"/>).</summary>
    public byte Flags;

    /// <summary>Body length in bytes after this header; reader skips unknown kinds by this length (02 §4).</summary>
    public uint BodyLength;

    /// <summary>Size of the common header in bytes.</summary>
    public const int SizeInBytes = 24;
}

// ── Kind-specific body offset maps (fixed prefixes; variable tails follow) ──

/// <summary>SlotRecord body layout (Kind=1) — 14-byte fixed prefix + payload (02 §3.1).</summary>
[PublicAPI]
internal static class SlotRecordBody
{
    public const int EntityIdOffset = 0;        // long
    public const int ComponentTypeIdOffset = 8; // ushort
    public const int OpOffset = 10;             // byte
    public const int ReservedOffset = 11;       // byte = 0
    public const int PayloadLengthOffset = 12;  // ushort
    public const int FixedSize = 14;            // payload bytes follow
}

/// <summary>LifecycleRecord body layout (Kind=2) — 14 bytes (02 §3.2).</summary>
[PublicAPI]
internal static class LifecycleRecordBody
{
    public const int EntityIdOffset = 0;     // long
    public const int OpOffset = 8;           // byte
    public const int ReservedOffset = 9;     // byte = 0
    public const int ArchetypeIdOffset = 10; // ushort
    public const int EnabledBitsOffset = 12; // ushort
    public const int Size = 14;
}

/// <summary>CollectionDeltaRecord body layout (Kind=3) — 20-byte fixed prefix + element (02 §3.3).</summary>
[PublicAPI]
internal static class CollectionDeltaRecordBody
{
    public const int EntityIdOffset = 0;        // long
    public const int ComponentTypeIdOffset = 8; // ushort
    public const int FieldIdOffset = 10;        // ushort
    public const int OpOffset = 12;             // byte
    public const int ReservedOffset = 13;       // byte = 0
    public const int IndexOffset = 14;          // int
    public const int ElementLengthOffset = 18;  // ushort
    public const int FixedSize = 20;            // element bytes follow
}

/// <summary>BulkManifestRecord body layout (Kind=4) — 32 bytes (02 §3.4).</summary>
[PublicAPI]
internal static class BulkManifestRecordBody
{
    public const int BulkSessionIdOffset = 0;   // long
    public const int BulkBeginLsnOffset = 8;    // long (0 in the Begin record; End cross-references its Begin)
    public const int EntityCountOffset = 16;    // long
    public const int ComponentCountOffset = 24; // long
    public const int Size = 32;
}

/// <summary>
/// Decoded view over a single WAL v2 record (header + parsed body). Produced by <see cref="RecordCodec.TryReadRecord"/>.
/// A <c>ref struct</c> — the variable-length tail (<see cref="Payload"/>) aliases the source buffer with no copy.
/// </summary>
[PublicAPI]
internal ref struct RecordView
{
    // Header
    public long Lsn;
    public long Tsn;
    public ushort UowEpoch;
    public RecordKind Kind;
    public RecordFlags Flags;
    public uint BodyLength;

    // Common body fields (meaning depends on Kind)
    public long EntityId;
    public ushort ComponentTypeId;
    public ushort FieldId;
    public ushort ArchetypeId;
    public ushort EnabledBits;
    public byte Op;          // SlotOp / LifecycleOp / CollectionOp depending on Kind
    public int Index;

    // BulkManifest fields
    public long BulkSessionId;
    public long BulkBeginLsn;
    public long EntityCount;
    public long ComponentCount;

    /// <summary>Component payload (Slot) or collection element (CollectionDelta); empty otherwise. Aliases the source buffer.</summary>
    public ReadOnlySpan<byte> Payload;

    public readonly bool IsTxBegin => (Flags & RecordFlags.TxBegin) != 0;
    public readonly bool IsTxCommit => (Flags & RecordFlags.TxCommit) != 0;
    public readonly bool IsFence => (Flags & RecordFlags.FenceRecord) != 0;
    public readonly bool IsCommittedDiscipline => (Flags & RecordFlags.Committed) != 0;

    /// <summary>True when the reader skipped a record of an unrecognized kind (02 §4); only <see cref="BodyLength"/> is meaningful.</summary>
    public bool IsUnknownKind;
}
