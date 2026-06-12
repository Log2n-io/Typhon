using JetBrains.Annotations;

namespace Typhon.Engine.Internals;

/// <summary>
/// The single seam every WAL emitter goes through (01 §3). One <see cref="Append"/> entry point appends a transaction's (or
/// fence's) record batch through the codec into the kept transport. Failure THROWS — never a sentinel (LOG-01) — so an
/// acknowledged commit can never have missing records.
/// </summary>
/// <remarks>
/// P1.1 subset of the design's interface: <c>AppendFence</c> is folded into <see cref="Append"/> via the builder's fence mode,
/// <c>Barrier</c> stays in <see cref="BulkLoadSession"/>'s flush+checkpoint choreography, and <c>GetSnapshot</c> (introspection,
/// M13) lands with checkpoint v2 (P1.3). The implementation composes <see cref="WalManager"/> in P1.1; <c>WalManager</c> is
/// dissolved into a SnapshotStore-owned transport in P1.2/P1.3.
/// </remarks>
[PublicAPI]
internal interface IDurabilityLog
{
    /// <summary>
    /// Appends one batch — all records claim contiguous ascending LSNs in builder (LOG-07) order, transparently split across
    /// chunks (02 §5). Returns the batch's highest LSN, or 0 when the batch is empty. Throws on back-pressure timeout or an
    /// over-large record (LOG-01).
    /// </summary>
    long Append(ref CommitBatchBuilder batch, ref WaitContext ctx);

    /// <summary>Requests an explicit flush of buffered WAL data (Deferred durability).</summary>
    void RequestFlush();

    /// <summary>Blocks until <paramref name="lsn"/> is durably written + fsynced.</summary>
    void WaitForDurable(long lsn, ref WaitContext ctx);

    /// <summary>Highest LSN durably written to stable media (LOG-05: never exceeds what reached disk).</summary>
    long DurableLsn { get; }

    /// <summary>Highest LSN claimed so far — replaces <c>CommitBuffer.NextLsn - 1</c> peeking at UoW flush (M7).</summary>
    long LastAppendedLsn { get; }
}
