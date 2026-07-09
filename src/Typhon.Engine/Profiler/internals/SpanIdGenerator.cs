using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Generates unique 64-bit <see cref="TraceSpanHeader.SpanId"/> values for the producer hot path with zero atomic operations and zero
/// shared cache lines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scheme:</b> <c>SpanId = ((ulong)slotIdx &lt;&lt; 56) | (ulong)counter</c>. Top 8 bits are the slot index (0..255), bottom 56 bits are a per-slot
/// monotonic counter stored in <see cref="ThreadSlot.SpanCounter"/>. The producer increments the counter with a plain <c>++</c> — single writer
/// (the slot's owning thread), so no <see cref="System.Threading.Interlocked"/> is needed and no cache line is shared with other cores.
/// </para>
/// <para>
/// <b>Why this is collision-free:</b>
/// <list type="bullet">
///   <item>Distinct slot indices give distinct top-8-bit prefixes — concurrent threads on different slots never collide.</item>
///   <item>The counter is per-slot and <b>NEVER</b> reset across reclaims (see <c>ThreadSlot.SpanCounter</c> XML doc + <c>ThreadSlotRegistry.AssignClaim</c>).
///         When thread A dies after emitting N spans on slot 5, the counter sits at N. When thread B reclaims slot 5, its first emit advances it to N+1.
///         The (slot, counter) pair is unique for the life of the process.</item>
/// </list>
/// </para>
/// <para>
/// <b>Range:</b> 56-bit counter ≈ 7.2 × 10^16 ≈ 2280 years at 1 million spans per second per slot. Effectively unbounded.
/// </para>
/// <para>
/// <b>Cost on the hot path:</b> one cache-hot load of <c>slot.SpanCounter</c>, one add, one store. ~1–2 ns total. No memory barriers, no atomic ops.
/// </para>
/// </remarks>
internal static class SpanIdGenerator
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong NextId(int slotIdx, ThreadSlot slot)
    {
        var counter = ++slot.SpanCounter;
        return ((ulong)slotIdx << 56) | (ulong)counter;
    }
}
