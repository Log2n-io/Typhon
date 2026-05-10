// unset

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Entry in the TAIL version-history buffer for AllowMultiple secondary indexes.
/// Encodes a chain-ID with an Active/Tombstone flag via the sign of <see cref="SignedChainId"/>, plus the TSN at which this state was established.
/// </summary>
/// <remarks>
/// <para>
/// Positive <see cref="SignedChainId"/> → Active (entity is indexed under this key at this TSN).
/// Negative <see cref="SignedChainId"/> → Tombstone (entity was removed from this key at this TSN).
/// </para>
/// <para>
/// Layout: 12 bytes (4 + 8), Pack=4 for natural alignment inside VSBS chunks.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct VersionedIndexEntry : IEquatable<VersionedIndexEntry>
{
    /// <summary>
    /// Sign-encoded chain ID. Positive = Active, Negative = Tombstone.
    /// <c>Math.Abs(SignedChainId)</c> yields the actual chain ID.
    /// </summary>
    public int SignedChainId;

    /// <summary>
    /// The TSN (Transaction Sequence Number) at which this entry was written.
    /// </summary>
    public long TSN;

    /// <summary>Creates an Active entry (entity is present under this key at this TSN).</summary>
    public static VersionedIndexEntry Active(int chainId, long tsn)
    {
        Debug.Assert(chainId > 0, "ChainId must be positive for sign-encoding to work");
        return new VersionedIndexEntry { SignedChainId = chainId, TSN = tsn };
    }

    /// <summary>Creates a Tombstone entry (entity was removed from this key at this TSN).</summary>
    public static VersionedIndexEntry Tombstone(int chainId, long tsn)
    {
        Debug.Assert(chainId > 0, "ChainId must be positive for sign-encoding to work");
        return new VersionedIndexEntry { SignedChainId = -chainId, TSN = tsn };
    }

    /// <summary>True if this entry represents an active mapping.</summary>
    public bool IsActive => SignedChainId > 0;

    /// <summary>True if this entry represents a removal.</summary>
    public bool IsTombstone => SignedChainId < 0;

    /// <summary>The chain ID (always positive, regardless of Active/Tombstone state).</summary>
    public int ChainId => Math.Abs(SignedChainId);

    public bool Equals(VersionedIndexEntry other) => SignedChainId == other.SignedChainId && TSN == other.TSN;
    public override bool Equals(object obj) => obj is VersionedIndexEntry other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(SignedChainId, TSN);
    public override string ToString() => $"{(IsActive ? "Active" : "Tombstone")}(ChainId={ChainId}, TSN={TSN})";
}

/// <summary>
/// Extra header appended after <see cref="VariableSizedBufferRootHeader"/> in AllowMultiple index HEAD buffers.
/// Links the HEAD buffer to its corresponding TAIL version-history buffer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IndexBufferExtraHeader
{
    /// <summary>
    /// Links this HEAD buffer to its corresponding TAIL version-history buffer (in TailVSBS).
    /// 0 = no TAIL buffer allocated yet (lazy allocation on first versioned write).
    /// </summary>
    public int TailBufferId;

    /// <summary>
    /// Gets a ref to the extra header at the given chunk address, offset past the standard root header.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe ref IndexBufferExtraHeader FromChunkAddress(byte* chunkAddr)
        => ref Unsafe.AsRef<IndexBufferExtraHeader>(chunkAddr + sizeof(VariableSizedBufferRootHeader));
}
