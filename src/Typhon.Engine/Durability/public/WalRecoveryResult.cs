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

    /// <summary>Number of FPI records applied for torn-page repair.</summary>
    public int FpiRecordsApplied;

    /// <summary>Number of TickFence chunks processed during recovery.</summary>
    public int TickFenceChunksProcessed;

    /// <summary>Number of individual component entries overwritten from TickFence data.</summary>
    public int TickFenceEntriesReplayed;

    /// <summary>LSN of the last valid record found during scan.</summary>
    public long LastValidLSN;

    /// <summary>Total elapsed time for the recovery operation in microseconds.</summary>
    public long ElapsedMicroseconds;
}
