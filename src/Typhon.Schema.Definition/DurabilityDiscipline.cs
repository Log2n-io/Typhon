using JetBrains.Annotations;

namespace Typhon.Schema.Definition;

/// <summary>
/// Runtime durability discipline for a <see cref="StorageMode.SingleVersion"/>-layout component — selected per transaction, orthogonal to the design-time
/// <see cref="StorageMode"/> (which fixes the cluster layout) and to the per-UoW <c>DurabilityMode</c> (which fixes flush timing).
/// </summary>
/// <remarks>
/// <para>
/// The discipline is a transaction-time knob on the existing per-UoW durability axis; it does NOT change a component's storage layout and is NOT a new
/// <see cref="StorageMode"/> value. It only applies to the <see cref="StorageMode.SingleVersion"/> layout — <see cref="StorageMode.Versioned"/> is always
/// commit-scoped and <see cref="StorageMode.Transient"/> is never durable.
/// </para>
/// <para>
/// See <c>claude/design/Ecs/committed-storage-mode.md</c> (the authoritative feature spec).
/// </para>
/// </remarks>
[PublicAPI]
public enum DurabilityDiscipline : byte
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
