using JetBrains.Annotations;
using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Represents a claimed region in the WAL commit buffer for writing WAL records. Returned by <see cref="WalCommitBuffer.TryClaim"/> — the producer writes
/// record data into <see cref="DataSpan"/>, then calls <see cref="WalCommitBuffer.Publish"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a <c>ref struct</c> because it contains a <see cref="Span{T}"/> pointing directly into the native buffer memory. It cannot escape to the
/// heap, which is exactly what we want — the claimed region is only valid during the current stack frame.
/// </para>
/// <para>
/// If the producer encounters an error during serialization, it must call <see cref="WalCommitBuffer.AbandonClaim"/> to release the claimed space.
/// Failing to do so will leak the inflight count and block buffer swaps.
/// </para>
/// </remarks>
[PublicAPI]
internal ref struct WalClaim
{
    /// <summary>
    /// Writable region for WAL record data (after the frame header). The producer writes serialized WAL records into this span.
    /// </summary>
    public Span<byte> DataSpan;

    /// <summary>
    /// Byte offset of this claim's frame header within the buffer. Internal — used by <see cref="WalCommitBuffer.Publish"/>
    /// and <see cref="WalCommitBuffer.AbandonClaim"/>.
    /// </summary>
    internal int FrameOffset;

    /// <summary>
    /// Total frame size including frame header, 8-byte aligned. Internal — used by <see cref="WalCommitBuffer.Publish"/> for the FrameLength write.
    /// </summary>
    internal int TotalFrameSize;

    /// <summary>
    /// Number of WAL records the producer will write into this claim.
    /// </summary>
    public int RecordCount;

    /// <summary>
    /// First assigned LSN for the records in this claim. LSNs are contiguous: FirstLSN, FirstLSN+1, ..., FirstLSN+RecordCount-1.
    /// </summary>
    public long FirstLSN;

    /// <summary>
    /// Index of the buffer (0 or 1) this claim belongs to. Internal — used for stale-claim detection after a buffer swap.
    /// </summary>
    internal int BufferIndex;

    /// <summary>
    /// True if this claim was successfully allocated and is ready for writing. False if <see cref="WalCommitBuffer.TryClaim"/> failed or the claim was abandoned.
    /// </summary>
    public bool IsValid;
}
