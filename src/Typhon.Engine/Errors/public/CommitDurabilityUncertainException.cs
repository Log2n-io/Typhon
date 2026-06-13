using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Thrown when a transaction's records were appended to the WAL and the transaction was published (its changes are visible in memory), but the
/// subsequent Immediate durability wait did not confirm the records reached stable storage (back-pressure timeout or a fatal WAL writer error).
/// </summary>
/// <remarks>
/// This is the AP-02 "point of no return" signal: the Append already happened and the transaction has been published, so it is logically committed and
/// MUST NOT be rolled back. Whether its records survive a crash depends on the WAL writer catching up — recovery will replay them iff they reached
/// stable storage. Callers should treat this as "commit accepted, durability unconfirmed": do not re-run the transaction, and surface the uncertainty to
/// the application. The original wait failure is preserved as <see cref="Exception.InnerException"/>. <see cref="HighLsn"/> is the batch's highest LSN —
/// poll <c>DurabilityLog.DurableLsn</c> against it to learn whether the records became durable.
/// </remarks>
[PublicAPI]
public class CommitDurabilityUncertainException : DurabilityException
{
    /// <summary>
    /// Creates a new <see cref="CommitDurabilityUncertainException"/>.
    /// </summary>
    /// <param name="highLsn">The highest LSN of the transaction's appended batch — compare against the durable watermark to learn the outcome.</param>
    /// <param name="innerException">The durability wait failure that left durability unconfirmed.</param>
    public CommitDurabilityUncertainException(long highLsn, Exception innerException)
        : base(TyphonErrorCode.CommitDurabilityUncertain,
            $"Transaction was appended (highLsn={highLsn}) and published, but the durability wait did not confirm: {innerException.Message}",
            innerException)
    {
        HighLsn = highLsn;
    }

    /// <summary>The highest LSN of the transaction's appended batch.</summary>
    public long HighLsn { get; }
}
