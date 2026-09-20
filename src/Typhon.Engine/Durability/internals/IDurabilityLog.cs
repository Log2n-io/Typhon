using JetBrains.Annotations;
using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// The single seam every WAL emitter goes through (01 §3). One <c>Append</c> entry point appends a transaction's (or
/// fence's) record batch through the codec into the kept transport. Failure THROWS — never a sentinel (LOG-01) — so an
/// acknowledged commit can never have missing records.
/// </summary>
/// <remarks>
/// P1.1 subset of the design's interface: <c>AppendFence</c> is folded into <c>Append</c> via the builder's fence mode,
/// <c>Barrier</c> stays in <see cref="BulkLoadSession"/>'s flush+checkpoint choreography, and <c>GetSnapshot</c> (introspection,
/// M13) lands with checkpoint v2 (P1.3). The implementation composes <see cref="WalManager"/> in P1.1; <c>WalManager</c> is
/// dissolved into a SnapshotStore-owned transport in P1.2/P1.3.
/// </remarks>
[PublicAPI]
internal interface IDurabilityLog
{
    /// <summary>
    /// Appends one batch — all records claim contiguous ascending LSNs in builder (LOG-07) order, transparently split across
    /// chunks (02 §5). Returns the batch's highest LSN, or 0 when the batch is empty. Throws on back-pressure timeout or an
    /// over-large record (LOG-01).
    /// </summary>
    long Append(ref CommitBatchBuilder batch, ref WaitContext ctx);

    /// <summary>
    /// <see cref="Append(ref CommitBatchBuilder, ref WaitContext)"/>, also storing the batch's first LSN into <paramref name="inFlightFloor"/> after
    /// claiming it and before publishing the frame (CK-13). A barrier that covers the batch has drained that frame, so it also sees the floor.
    /// </summary>
    long Append(ref CommitBatchBuilder batch, ref WaitContext ctx, ref long inFlightFloor);

    /// <summary>
    /// Appends a run of columnar tick-fence blocks (#559), copying each cluster's SoA columns straight into the WAL claim with
    /// no intermediate staging. Returns the highest LSN published, or 0 for an empty run. Throws on back-pressure timeout (LOG-01).
    /// </summary>
    /// <remarks>
    /// <paramref name="columnHandleRanges"/> carries the collection-handle byte ranges to zero out of the copied columns (LOG-06), packed by
    /// <see cref="RecordCodec.PackColumnHandleRange"/>. It is empty when no emitted column carries a <c>ComponentCollection</c> field.
    /// </remarks>
    long AppendFenceBlocks(
        ReadOnlySpan<RecordCodec.FenceBlockDescriptor> blocks,
        ushort archetypeId,
        long tsn,
        int entityKeysOffset,
        ReadOnlySpan<int> slotIndices,
        ReadOnlySpan<int> componentSizes,
        ReadOnlySpan<int> componentOffsets,
        int totalComponentSize,
        ReadOnlySpan<ulong> columnHandleRanges,
        ref WaitContext ctx);

    /// <summary>Requests an explicit flush of buffered WAL data (Deferred durability).</summary>
    void RequestFlush();

    /// <summary>Blocks until <paramref name="lsn"/> is durably written + fsynced.</summary>
    void WaitForDurable(long lsn, ref WaitContext ctx);

    /// <summary>Highest LSN durably written to stable media (LOG-05: never exceeds what reached disk).</summary>
    long DurableLsn { get; }

    /// <summary>Highest LSN claimed so far — replaces <c>CommitBuffer.NextLsn - 1</c> peeking at UoW flush (M7).</summary>
    /// <remarks>#937: this is the ALLOCATION frontier. It names LSNs that no frame owns whenever a claim is abandoned or times out,
    /// so it must not be used as a durability wait target — use <see cref="LastPublishedLsn"/> for that.</remarks>
    long LastAppendedLsn { get; }

    /// <summary>#937 — highest LSN a frame was actually PUBLISHED with. The sound target for "flush everything the WAL holds as of now".</summary>
    long LastPublishedLsn { get; }
}
