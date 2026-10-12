using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// The engine's transaction sequence numbers are exhausted (#1208): a TSN is stored in 48 bits, and no transaction starts once the counter reaches
/// 2^47 minus a fixed margin. Not transient — no retry, eviction or reopen brings the counter down: the database must be migrated to a fresh one.
/// </summary>
[PublicAPI]
public sealed class TransactionSequenceExhaustedException : ResourceExhaustedException
{
    /// <summary>Creates a new <see cref="TransactionSequenceExhaustedException"/>.</summary>
    /// <param name="next">The next TSN the engine would draw.</param>
    /// <param name="limit">The TSN at which draws are refused.</param>
    public TransactionSequenceExhaustedException(long next, long limit)
        : base($"Transaction sequence numbers are exhausted: the next would be {next}, and none is drawn from {limit} on (a TSN is stored in 48 bits). "
               + "This does not heal: migrate the data to a fresh database.", "Data/TransactionChain/Tsn", ResourceType.TransactionPool, next, limit)
    {
    }

    /// <summary>Not transient: the counter never goes down.</summary>
    public override bool IsTransient => false;
}
