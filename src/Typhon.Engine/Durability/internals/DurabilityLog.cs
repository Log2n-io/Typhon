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
