using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Thrown when a transaction's records were appended to the WAL — the point of no return (AP-02) — and something failed before its publish completed:
/// the transaction is committed, but only part of it, or none of it, is visible in memory.
/// </summary>
/// <remarks>
/// The transaction reports <see cref="Transaction.TransactionState.Committed"/> and is never rolled back: its records are in the log. The engine stops
/// committing (<see cref="DatabaseEngine.CommitPipelineFailure"/>), keeps its checkpoint below these records, and closes as a crash, so the next open
/// replays them and the commit comes back whole. Reopen the database; do not re-run the transaction. The original failure is the
/// <see cref="Exception.InnerException"/>; <see cref="HighLsn"/> is the batch's highest LSN (0 when it appended nothing), and <see cref="Durable"/> says
/// whether its records were confirmed on stable storage before the exception was raised.
/// </remarks>
[PublicAPI]
public class CommitPublishIncompleteException : DurabilityException
{
    /// <summary>Creates a new <see cref="CommitPublishIncompleteException"/>.</summary>
    /// <param name="highLsn">The highest LSN of the transaction's appended batch; 0 when it appended nothing.</param>
    /// <param name="durable">Whether the batch was confirmed on stable storage.</param>
    /// <param name="innerException">The failure that interrupted the publish.</param>
    public CommitPublishIncompleteException(long highLsn, bool durable, Exception innerException)
        : base(TyphonErrorCode.CommitPublishIncomplete, MessageFor(highLsn, durable, innerException), innerException)
    {
        HighLsn = highLsn;
        Durable = durable;
    }

    private static string MessageFor(long highLsn, bool durable, Exception innerException) =>
        highLsn > 0
            ? $"Transaction was appended (highLsn={highLsn}, durable={durable}) but its publish did not complete: {innerException.Message}. The engine "
              + "stopped committing; reopen the database to recover the transaction from the log. Do not run it again."
            : $"Transaction's publish did not complete: {innerException.Message}. It logged no record (its changes are transient), so a reopen does not "
              + "bring it back; the engine stopped committing over its partial publish, and must be reopened.";

    /// <summary>The highest LSN of the transaction's appended batch; 0 when it appended nothing.</summary>
    public long HighLsn { get; }

    /// <summary>Whether the batch was confirmed on stable storage before this exception was raised.</summary>
    public bool Durable { get; }
}
