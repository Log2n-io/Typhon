namespace Typhon.Engine.Internals;

/// <summary>
/// Lifecycle state of a <see cref="ThreadSlot"/> in <see cref="ThreadSlotRegistry"/>.
/// </summary>
/// <remarks>
/// The state is stored as an <c>int</c> in the slot for use with <see cref="System.Threading.Interlocked.CompareExchange(ref int, int, int)"/>
/// — C# has no byte overload of CAS. Transitions:
/// <list type="bullet">
///   <item><see cref="Free"/> → <see cref="Active"/>: producer claims an unused slot via CAS in <c>ThreadSlotRegistry.GetOrAssignSlot</c></item>
///   <item><see cref="Active"/> → <see cref="Retiring"/>: the owning thread's <c>SlotReleaser</c> finalizer runs on thread exit</item>
///   <item><see cref="Retiring"/> → <see cref="Free"/>: the profiler consumer thread drains the slot's trailing events, then releases it</item>
/// </list>
/// </remarks>
internal enum SlotState
{
    /// <summary>Slot is unused and may be claimed.</summary>
    Free = 0,

    /// <summary>Slot is owned by a live producer thread; its buffer is being written to.</summary>
    Active = 1,

    /// <summary>Slot's owner thread has exited; the consumer will drain trailing events and release it on the next pass.</summary>
    Retiring = 2
}
