using JetBrains.Annotations;
using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// <see cref="IDurabilityLog"/> over the kept WAL transport (01 §3). Measures the batch with the codec, claims contiguous LSNs
/// from the <see cref="WalCommitBuffer"/>, writes the records, and publishes — the one path WAL records reach the log by.
/// </summary>
[PublicAPI]
internal sealed class DurabilityLog : IDurabilityLog
{
    private readonly WalManager _wal;

    public DurabilityLog(WalManager wal)
    {
        ArgumentNullException.ThrowIfNull(wal);
        _wal = wal;
    }

    public long DurableLsn => _wal.DurableLsn;

    public long LastAppendedLsn => _wal.CommitBuffer.NextLsn - 1;

    public void RequestFlush() => _wal.RequestFlush();

    public void WaitForDurable(long lsn, ref WaitContext ctx) => _wal.WaitForDurable(lsn, ref ctx);

    /// <summary>
    /// Appends a run of columnar tick-fence blocks (#559). Unlike <see cref="Append(ref CommitBatchBuilder, ref WaitContext)"/>
    /// this path never stages payload: the size is a pure function of the descriptors, so it measures, claims, and has the codec
    /// copy each cluster's SoA columns straight into the claim. Returns the highest LSN published, or 0 for an empty run.
    /// </summary>
    public long AppendFenceBlocks(
        ReadOnlySpan<RecordCodec.FenceBlockDescriptor> blocks,
        ushort archetypeId,
        long tsn,
        int entityKeysOffset,
        ReadOnlySpan<int> slotIndices,
        ReadOnlySpan<int> componentSizes,
        ReadOnlySpan<int> componentOffsets,
        int totalComponentSize,
        ReadOnlySpan<ulong> columnHandleRanges,
        ref WaitContext ctx)
    {
        if (blocks.Length == 0)
        {
            return 0;
        }

        var size = RecordCodec.MeasureFenceBlocks(blocks, slotIndices.Length, totalComponentSize, out _);

        // TryClaim throws WalBackPressureTimeoutException / WalClaimTooLargeException on failure (LOG-01) — never a sentinel.
        var claim = _wal.CommitBuffer.TryClaim(size, blocks.Length, ref ctx);
        try
        {
            var written = RecordCodec.WriteFenceBlocks(
                claim.DataSpan, blocks, archetypeId, claim.FirstLSN, tsn,
                entityKeysOffset, slotIndices, componentSizes, componentOffsets, totalComponentSize, columnHandleRanges);

            // Zero the frame-alignment slack after the last chunk, as Append does: stale bytes from a prior claim would
            // otherwise be misread as a chunk header during recovery.
            if (written < claim.DataSpan.Length)
            {
                claim.DataSpan[written..].Clear();
            }

            _wal.CommitBuffer.Publish(ref claim);
            return claim.FirstLSN + blocks.Length - 1;
        }
        catch
        {
            _wal.CommitBuffer.AbandonClaim(ref claim);
            throw;
        }
    }

    public long Append(ref CommitBatchBuilder batch, ref WaitContext ctx)
    {
        if (batch.IsEmpty)
        {
            return 0;
        }

        var size = RecordCodec.Measure(in batch, out var recordCount, out _);

        // TryClaim throws WalBackPressureTimeoutException / WalClaimTooLargeException on failure (LOG-01) — never a sentinel.
        var claim = _wal.CommitBuffer.TryClaim(size, recordCount, ref ctx);
        try
        {
            var written = RecordCodec.Write(claim.DataSpan, in batch, claim.FirstLSN);

            // Zero the 0–7 bytes of frame-alignment slack after the last chunk: TryClaim only zeroes the frame header, so stale
            // bytes from a prior claim could otherwise be misread as a chunk header during recovery.
            if (written < claim.DataSpan.Length)
            {
                claim.DataSpan[written..].Clear();
            }

            _wal.CommitBuffer.Publish(ref claim);
            return claim.FirstLSN + recordCount - 1;
        }
        catch
        {
            _wal.CommitBuffer.AbandonClaim(ref claim);
            throw;
        }
    }
}
