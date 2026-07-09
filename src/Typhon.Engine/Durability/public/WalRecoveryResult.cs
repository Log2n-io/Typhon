using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Result of a WAL crash recovery operation.
/// </summary>
[PublicAPI]
public struct WalRecoveryResult
{
    /// <summary>Number of WAL segment files scanned.</summary>
    public int SegmentsScanned;

    /// <summary>Total number of valid records scanned across all segments.</summary>
    public int RecordsScanned;

    /// <summary>Number of UoWs promoted from Pending to WalDurable (had commit markers in WAL).</summary>
    public int UowsPromoted;

    /// <summary>Number of UoWs voided (Pending with no commit marker in WAL).</summary>
    public int UowsVoided;

    /// <summary>Number of records replayed during recovery.</summary>
    public int RecordsReplayed;

    /// <summary>
    /// Always <c>0</c>. Full-page-image (FPI) torn-page repair was retired; torn-page protection is now the recovery rebuild net (scrub + index rebuild +
    /// suspect-mode). The field is retained for result-shape compatibility and is never incremented.
    /// </summary>
    public int FpiRecordsApplied;

    /// <summary>Number of TickFence chunks processed during recovery.</summary>
    public int TickFenceChunksProcessed;

    /// <summary>Number of individual component entries overwritten from TickFence data.</summary>
    public int TickFenceEntriesReplayed;

    /// <summary>
    /// Number of <see cref="WalChunkType.BulkBegin"/> chunks observed during scan. Each pairs with a <see cref="BulkEndCount"/> increment for a fully-durable
    /// bulk; mismatches indicate incomplete bulks whose visibility correctness is provided by the standard UowRegistry void path (UR-03).
    /// </summary>
    public int BulkBeginCount;

    /// <summary>
    /// Number of <see cref="WalChunkType.BulkEnd"/> chunks observed during scan. See <see cref="BulkBeginCount"/>.
    /// </summary>
    public int BulkEndCount;

    /// <summary>LSN of the last valid record found during scan.</summary>
    public long LastValidLSN;

    /// <summary>Total elapsed time for the recovery operation in microseconds.</summary>
    public long ElapsedMicroseconds;
}
