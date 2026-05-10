using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// A transaction exceeded its overall deadline.
/// Tier 1 class — throw sites activated in Tier 2 when Execution Context is implemented.
/// </summary>
[PublicAPI]
public class TransactionTimeoutException : TyphonTimeoutException
{
    /// <summary>
    /// Creates a new <see cref="TransactionTimeoutException"/> for the specified transaction.
    /// </summary>
    /// <param name="transactionId">ID of the transaction that timed out.</param>
    /// <param name="waitDuration">How long the transaction ran before the timeout fired.</param>
    public TransactionTimeoutException(long transactionId, TimeSpan waitDuration)
        : base(TyphonErrorCode.TransactionTimeout, $"Transaction {transactionId} timed out after {waitDuration.TotalMilliseconds:F0}ms", waitDuration)
    {
        TransactionId = transactionId;
    }

    /// <summary>ID of the transaction that timed out.</summary>
    public long TransactionId { get; }
}
