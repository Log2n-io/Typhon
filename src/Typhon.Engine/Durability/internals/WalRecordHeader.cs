using JetBrains.Annotations;
using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Bit flags for WAL record metadata.
/// </summary>
[Flags]
[PublicAPI]
internal enum WalRecordFlags : byte
{
    /// <summary>No flags set.</summary>
    None = 0,

    /// <summary>This record is the first in a Unit of Work.</summary>
    UowBegin = 1 << 0,

    /// <summary>This record is the last in a Unit of Work (commit marker).</summary>
    UowCommit = 1 << 1,
}

/// <summary>
/// Operation type for a WAL record.
/// </summary>
[PublicAPI]
internal enum WalOperationType : byte
{
    /// <summary>Component creation.</summary>
    Create = 1,

    /// <summary>Component update (before/after image).</summary>
    Update = 2,

    /// <summary>Component deletion (before image only).</summary>
    Delete = 3,
}

/// <summary>
/// 32-byte WAL record header — the body of a <see cref="WalChunkType.Transaction"/> chunk.
/// Written after the <see cref="WalChunkHeader"/> and before component payload data.
/// </summary>
/// <remarks>
/// <para>
/// CRC and PrevCRC are no longer part of this struct — they live in <see cref="WalChunkHeader"/> and <see cref="WalChunkFooter"/>, managed by the WAL
/// writer thread.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[PublicAPI]
internal struct WalRecordHeader
{
    /// <summary>Log Sequence Number — monotonically increasing, globally unique.</summary>
    public long LSN;

    /// <summary>MVCC transaction timestamp for snapshot isolation.</summary>
    public long TransactionTSN;

    /// <summary>Unit of Work registry link — identifies the UoW this record belongs to.</summary>
    public ushort UowEpoch;

    /// <summary>Component table ID — identifies which component type was modified.</summary>
    public ushort ComponentTypeId;

    /// <summary>Primary key (entity ID) of the modified entity.</summary>
    public long EntityId;

    /// <summary>Number of component data bytes following this header.</summary>
    public ushort PayloadLength;

    /// <summary>Type of operation (Create, Update, Delete).</summary>
    public byte OperationType;

    /// <summary>Flags providing additional record metadata.</summary>
    public byte Flags;

    /// <summary>Expected size of this struct in bytes.</summary>
    public const int SizeInBytes = 32;
}
