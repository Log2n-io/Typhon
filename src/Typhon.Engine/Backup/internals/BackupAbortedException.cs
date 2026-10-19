using System;

namespace Typhon.Engine.Internals;

/// <summary>Why an online backup capture was abandoned.</summary>
internal enum BackupAbortReason : byte
{
    /// <summary>No reason recorded: the capture has not aborted.</summary>
    None = 0,

    /// <summary>A data-file writer could not preserve a page within <c>CbwStallTimeout</c> (BKP-12): the backup could not keep up.</summary>
    CbwStall = 1,

    /// <summary>The freeze could not drain the data-file writes in flight within its budget.</summary>
    GateTimeout = 2,

    /// <summary>The caller cancelled the capture.</summary>
    Cancelled = 3,

    /// <summary>The engine began to shut down.</summary>
    EngineStopping = 4,

    /// <summary>The backup store failed (its exception is the inner one).</summary>
    SinkFailed = 5,

    /// <summary>The WAL did not become durable up to the freeze's published LSN within the budget, or failed.</summary>
    WalFlushTimeout = 6,

    /// <summary>The engine cannot take a consistent point: an open not complete, recovery running, a stopped commit pipeline or checkpointer.</summary>
    EngineFault = 7,

    /// <summary>A WAL segment the tail needs could not be read or did not hold a valid header.</summary>
    TailUnreadable = 8,

    /// <summary>
    /// The write gate is switched off for this process (<c>TYPHON_DATAFILE_GATE=0</c>): a freeze cannot exclude in-flight writes. Offline capture
    /// still works.
    /// </summary>
    TrackingDisabled = 9,
}

/// <summary>
/// An online backup capture was abandoned. Nothing was published; the pages it would have covered stay marked for the next backup (BKP-07).
/// </summary>
internal sealed class BackupAbortedException : TyphonException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Why the capture was abandoned.</param>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public BackupAbortedException(BackupAbortReason reason, string message, Exception innerException = null)
        : base(TyphonErrorCode.BackupAborted, $"Backup aborted ({reason}): {message}", innerException)
    {
        Reason = reason;
    }

    /// <summary>Why the capture was abandoned.</summary>
    public BackupAbortReason Reason { get; }

    /// <inheritdoc />
    public override bool IsTransient => Reason is BackupAbortReason.CbwStall or BackupAbortReason.GateTimeout or BackupAbortReason.WalFlushTimeout;
}
