using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-worker-slot hot state for <see cref="Typhon.Engine.EntityCommandBuffer"/> (#1099) — the scalars a producer touches on every queued command,
/// clustered onto one cache line so concurrent workers never share one.
/// </summary>
/// <remarks>
/// <para>
/// Same arrangement and the same reasons as <see cref="EventQueueSegmentState"/>: rule MD-03 wants each independently-mutated element on its own
/// ≥64-byte line and prefers one padded struct over parallel padded arrays, and the buffers cannot live here because a type with explicit layout
/// cannot be generic — not that this one is, but keeping the shape identical means the two can be read against each other.
/// </para>
/// <para>
/// <b>No atomics.</b> Every field is written only by the worker that owns the slot, and read by the apply only after the producing systems' DAG
/// completion barrier, which is a full fence. The two counters are deliberately exact rather than sampled: a reported zero has to mean nothing was lost,
/// which is the whole basis of the tolerate-and-count overflow verdict.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct EntityCommandSegmentState
{
    /// <summary>Commands accepted into this slot's header buffer. Cleared by <c>Reset</c>.</summary>
    [FieldOffset(0)]
    public int Count;

    /// <summary>Values written into this slot's payload pool. Cleared by <c>Reset</c>.</summary>
    [FieldOffset(4)]
    public int PayloadCount;

    /// <summary>Commands dropped because this slot hit its ceiling — header pool, payload pool, or key-block generations. Cleared by <c>Reset</c>.</summary>
    [FieldOffset(8)]
    public uint Overflow;

    /// <summary>
    /// Commands refused at the call because they were not valid — unregistered archetype, a realm that cannot hold the entity, too many values, or a
    /// chunk index outside the key-block stride. Distinct from <see cref="Overflow"/>: overflow is the engine running out of room, a rejection is the
    /// caller asking for something impossible. Cleared by <c>Reset</c>.
    /// </summary>
    [FieldOffset(12)]
    public uint Rejected;

    /// <summary>Entities this slot's accepted spawns will create — the sum of every accepted header's <c>Count</c>. Cleared by <c>Reset</c>.</summary>
    [FieldOffset(16)]
    public int SpawnedEntities;

    /// <summary>
    /// High-water <see cref="Count"/> for this slot this tick. Cleared by <c>Reset</c>.
    /// </summary>
    /// <remarks>
    /// Equal to <see cref="Count"/> as long as nothing drains mid-tick, which nothing does today — the apply runs once, at the fence. It is tracked
    /// separately anyway because <see cref="EventQueue{T}"/> learned this the expensive way: its peak was a sum of per-slot maxima observed at unrelated
    /// instants, and its partial-drain path never folded at all, under-reporting by 125x. Recording the slot's own high water keeps the figure meaningful
    /// if a mid-tick drain is ever added, and costs one compare on a branch that already wrote two fields.
    /// </remarks>
    [FieldOffset(20)]
    public int PeakDepth;
}
