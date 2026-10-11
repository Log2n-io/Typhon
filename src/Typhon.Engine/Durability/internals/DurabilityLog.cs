using JetBrains.Annotations;
using System;
using System.Threading;

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

    /// <summary>Test seam (CK-13): invoked inside <see cref="Append(ref CommitBatchBuilder, ref WaitContext, ref long)"/> after the floor is stored and
    /// before the frame is published, so a fixture can fail an append after its claim. Null in production: one null check per append.</summary>
    internal Action AfterFloorProbe { get; set; }

    /// <summary>Test seam (#1269): invoked inside <see cref="Append(ref CommitBatchBuilder, ref WaitContext, ref long)"/> after its first stop check and
    /// before its claim — where a commit may wait out WAL back-pressure. Null in production: one null check per append.</summary>
    internal Action BeforeClaimProbe { get; set; }

    /// <summary>
    /// Stops every later append (AP-03): set by <see cref="DatabaseEngine.LatchCommitPipelineFailure"/> when a commit's publish failed after its records
    /// were appended. Memory then holds a partial publish, and nothing more is logged over it — a commit, a fence, a bulk manifest alike. The callers check
    /// the engine's latch first (a fence returns, a commit rolls back); this is the backstop for any that does not, and for a latch set between the two.
    /// </summary>
    internal void Stop(Exception failure) => Interlocked.CompareExchange(ref _stoppedBy, failure, null);

    private Exception _stoppedBy;

    private void ThrowIfStopped()
    {
        if (Volatile.Read(ref _stoppedBy) is { } stopped)
        {
            throw new DurabilityException(TyphonErrorCode.CommitPipelineStopped,
                "The engine stopped committing after a commit failed between its append and the end of its publish; nothing more is appended. Reopen the "
                + "database to recover it.", stopped);
        }
    }

    public long DurableLsn => _wal.DurableLsn;

    public long LastAppendedLsn => _wal.CommitBuffer.NextLsn - 1;

    public void RequestFlush() => _wal.RequestFlush();

    public void WaitForDurable(long lsn, ref WaitContext ctx) => _wal.WaitForDurable(lsn, ref ctx);

    public long LastPublishedLsn => _wal.LastPublishedLsn;

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

        ThrowIfStopped();
        var size = RecordCodec.MeasureFenceBlocks(blocks, slotIndices.Length, totalComponentSize, out _);

        // TryClaim throws WalBackPressureTimeoutException / WalClaimTooLargeException on failure (LOG-01) — never a sentinel.
        var claim = _wal.CommitBuffer.TryClaim(size, blocks.Length, ref ctx);
        try
        {
            // Again once claimed: the claim may have waited out back-pressure while another commit stopped the engine (#1269). Abandoned below.
            ThrowIfStopped();
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
        long unused = 0;
        return Append(ref batch, ref ctx, ref unused);
    }

    public long Append(ref CommitBatchBuilder batch, ref WaitContext ctx, ref long inFlightFloor)
    {
        if (batch.IsEmpty)
        {
            return 0;
        }

        ThrowIfStopped();

        var size = RecordCodec.Measure(in batch, out var recordCount, out _);
        BeforeClaimProbe?.Invoke();

        // TryClaim throws WalBackPressureTimeoutException / WalClaimTooLargeException on failure (LOG-01) — never a sentinel.
        var claim = _wal.CommitBuffer.TryClaim(size, recordCount, ref ctx);
        try
        {
            // Again once claimed: the claim may have waited out back-pressure while another commit stopped the engine, and appending now would log
            // over the partial publish the stop is for (AP-03, #1269). The claim is abandoned below, as a failed write's is.
            ThrowIfStopped();

            // CK-13: before the frame is published. The writer drains frames in LSN order and stops at an unpublished one (WP-06), so a checkpoint
            // barrier at or past this batch has seen the frame's release, and with it this store.
            Volatile.Write(ref inFlightFloor, claim.FirstLSN);
            AfterFloorProbe?.Invoke();

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
