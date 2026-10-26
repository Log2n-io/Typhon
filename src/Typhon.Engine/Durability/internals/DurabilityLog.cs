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

    // LsnsAppendedBy, one counter per WalSource. One Interlocked.Add per published batch, not per record.
    private readonly long[] _lsnsBySource = new long[4];

    /// <inheritdoc/>
    public long LsnsAppendedBy(WalSource source) => Interlocked.Read(ref _lsnsBySource[(int)source]);

    public DurabilityLog(WalManager wal)
    {
        ArgumentNullException.ThrowIfNull(wal);
        _wal = wal;
    }

    /// <summary>Test seam (CK-13): invoked inside <see cref="Append(ref CommitBatchBuilder, ref WaitContext, ref long, CommitCardTable, int)"/> after the floor is stored and
    /// before the frame is published, so a fixture can fail an append after its claim. Null in production: one null check per append.</summary>
    internal Action AfterFloorProbe { get; set; }

    /// <summary>Test seam (#1269): invoked inside <see cref="Append(ref CommitBatchBuilder, ref WaitContext, ref long, CommitCardTable, int)"/> and <see cref="AppendFenceBlocks"/>
    /// after the first stop check and before the claim — where an append may wait out WAL back-pressure. Null in production: one null check per
    /// append.</summary>
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

    /// <summary>
    /// The engine's world clock (#1495, TICK-01): every append writes its value on the records' trailer, so recovery can date the last durable write.
    /// Null — a log no engine wired — writes 0.
    /// </summary>
    internal WorldClock Clock { get; set; }

    private long _maxAppendedWorldTick;

    /// <summary>
    /// The highest world clock any append has written, by acquire. The checkpoint reads it BEFORE its barrier flush: every record that carried it is then
    /// durable, so the value it persists is never ahead of the data a crash keeps.
    /// </summary>
    public long MaxAppendedWorldTick => Volatile.Read(ref _maxAppendedWorldTick);

    // The clock for this append, recorded as appended. A read first and a CAS only when it rises — the value moves once a tick, every commit of the tick
    // shares it, so the line stays read-mostly.
    private long WorldTickForAppend()
    {
        var worldTick = Clock?.Next ?? 0;
        var current = Volatile.Read(ref _maxAppendedWorldTick);
        while (worldTick > current)
        {
            var seen = Interlocked.CompareExchange(ref _maxAppendedWorldTick, worldTick, current);
            if (seen == current)
            {
                break;
            }

            current = seen;
        }

        return worldTick;
    }

    public long LastAppendedLsn => _wal.CommitBuffer.NextLsn - 1;

    public void RequestFlush() => _wal.RequestFlush();

    public void WaitForDurable(long lsn, ref WaitContext ctx) => _wal.WaitForDurable(lsn, ref ctx);

    public long LastPublishedLsn => _wal.LastPublishedLsn;

    /// <summary>
    /// Appends a run of columnar tick-fence blocks (#559). Unlike <see cref="Append(ref CommitBatchBuilder, ref WaitContext, WalSource)"/>
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
        BeforeClaimProbe?.Invoke();

        // TryClaim throws WalBackPressureTimeoutException / WalClaimTooLargeException on failure (LOG-01) — never a sentinel.
        var claim = _wal.CommitBuffer.TryClaim(size, blocks.Length, ref ctx);
        try
        {
            // Again once claimed: the claim may have waited out back-pressure while another commit stopped the engine (#1269). Abandoned below.
            ThrowIfStopped();
            var written = RecordCodec.WriteFenceBlocks(
                claim.DataSpan, blocks, archetypeId, claim.FirstLSN, tsn, WorldTickForAppend(),
                entityKeysOffset, slotIndices, componentSizes, componentOffsets, totalComponentSize, columnHandleRanges);

            // Zero the frame-alignment slack after the last chunk, as Append does: stale bytes from a prior claim would
            // otherwise be misread as a chunk header during recovery.
            if (written < claim.DataSpan.Length)
            {
                claim.DataSpan[written..].Clear();
            }

            _wal.CommitBuffer.Publish(ref claim);
            Interlocked.Add(ref _lsnsBySource[(int)WalSource.ClusterFence], blocks.Length);
            return claim.FirstLSN + blocks.Length - 1;
        }
        catch
        {
            _wal.CommitBuffer.AbandonClaim(ref claim);
            throw;
        }
    }

    public long Append(ref CommitBatchBuilder batch, ref WaitContext ctx, WalSource source = WalSource.TableFence)
    {
        long unused = 0;
        return Append(ref batch, ref ctx, ref unused, null, -1, source);
    }

    /// <summary>
    /// Appends a transaction's batch. With <paramref name="cards"/>, the commit point is here (VIS-03): once the claim holds and the engine is still
    /// committing, the committer's card <paramref name="cardSlot"/> is registered, its stamp drawn and stored, and the stamp rides on TxCommit
    /// (<see cref="CommitBatchBuilder.CommitStamp"/>). Nothing between that and the frame's publish can fail. Without, the batch carries no stamp (fence and
    /// bulk-load batches, whose effects are never staged).
    /// </summary>
    public long Append(ref CommitBatchBuilder batch, ref WaitContext ctx, ref long inFlightFloor, CommitCardTable cards, int cardSlot) =>
        Append(ref batch, ref ctx, ref inFlightFloor, cards, cardSlot, WalSource.Transaction);

    private long Append(ref CommitBatchBuilder batch, ref WaitContext ctx, ref long inFlightFloor, CommitCardTable cards, int cardSlot, WalSource source)
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

            // TICK-01: raised once the claim holds, so the checkpoint, which samples the max before the allocation frontier, finds this claim under the
            // frontier it reads (CheckpointManager.RunCycle).
            batch.WorldTick = WorldTickForAppend();
            AfterFloorProbe?.Invoke();

            // ── The commit point (VIS-03 steps 1–3): registered, stamped. Below, only the copy into the claimed span and its publish. ──
            if (cards != null)
            {
                batch.CommitStamp = cards.Register(cardSlot, batch.Tsn);
            }

            var written = RecordCodec.Write(claim.DataSpan, in batch, claim.FirstLSN);

            // Zero the 0–7 bytes of frame-alignment slack after the last chunk: TryClaim only zeroes the frame header, so stale
            // bytes from a prior claim could otherwise be misread as a chunk header during recovery.
            if (written < claim.DataSpan.Length)
            {
                claim.DataSpan[written..].Clear();
            }

            _wal.CommitBuffer.Publish(ref claim);
            Interlocked.Add(ref _lsnsBySource[(int)source], recordCount);
            return claim.FirstLSN + recordCount - 1;
        }
        catch
        {
            _wal.CommitBuffer.AbandonClaim(ref claim);
            throw;
        }
    }
}
