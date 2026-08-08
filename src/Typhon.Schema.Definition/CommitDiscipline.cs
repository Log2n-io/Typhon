using JetBrains.Annotations;

namespace Typhon.Schema.Definition;

/// <summary>
/// Whether a write to a <see cref="StorageMode.SingleVersion"/>-layout component participates in the transaction or bypasses it and rides the tick fence —
/// selected per transaction, orthogonal to the design-time <see cref="StorageMode"/> (which fixes the cluster layout) and to the per-UoW
/// <c>DurabilityMode</c> (which fixes flush timing).
/// </summary>
/// <remarks>
/// <para>
/// Three ACID properties move together with this one knob, and only one of them is durability:
/// </para>
/// <list type="table">
///   <listheader><term/><description>TickFence / Commit</description></listheader>
///   <item><term>Isolation</term><description>none, visible immediately / read-committed</description></item>
///   <item><term><c>Rollback</c> reverts the write</term><description>no / <b>yes</b>, O(1) — the staging buffer is discarded</description></item>
///   <item><term>Durability</term><description>≤ 1 tick of loss / zero loss at commit</description></item>
/// </list>
/// <para>
/// It was called <c>DurabilityDiscipline</c> until #648, named after its neighbour <c>DurabilityMode</c> rather than after its function. That name is why
/// two of the guide pages told users a <see cref="StorageMode.SingleVersion"/> write can never be rolled back: the rollback behaviour is not something a
/// reader looks for on an enum whose name says durability. Staging was not chosen in order to provide rollback — Variant B (in-place plus undo log) was
/// rejected as crash-unsafe (rule CM-01) — but that is the implementer's motivation, not the caller's contract, and a public enum should name what the
/// caller gets.
/// </para>
/// <para>
/// The VALUE names deliberately did not change with it. <c>CommitDiscipline.Commit</c> reads tautologically, and <c>Fence</c>/<c>Transactional</c> were
/// considered, but the value is what appears in every call site and doc sample: a second source-breaking rename buys a nicer word, not a correction.
/// </para>
/// <para>
/// The discipline is a transaction-time knob; it does NOT change a component's storage layout and is NOT a new <see cref="StorageMode"/> value. It only
/// applies to the <see cref="StorageMode.SingleVersion"/> layout — <see cref="StorageMode.Versioned"/> is always commit-scoped and
/// <see cref="StorageMode.Transient"/> is never durable.
/// </para>
/// <para>
/// See <c>claude/design/Ecs/committed-storage-mode.md</c> (the authoritative feature spec) and ADR-057.
/// </para>
/// </remarks>
[PublicAPI]
public enum CommitDiscipline : byte
{
    /// <summary>
    /// Default. In-place writes, last-writer-wins, durability batched at the tick fence (≤1-tick loss). Maximum throughput for high-frequency, loss-tolerant
    /// data (position, velocity, health).
    /// </summary>
    TickFence = 0,

    /// <summary>
    /// Writes are staged per transaction and made atomic + zero-loss durable at <c>Transaction.Commit</c> via a logical-redo WAL record, then published in
    /// place — read-committed isolation, O(1) rollback, no revision chain.
    /// For writes that must not be lost and must be all-or-nothing (teleport, item pickup) without paying for MVCC.
    /// </summary>
    Commit = 1,
}
