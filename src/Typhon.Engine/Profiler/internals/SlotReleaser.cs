using System.Runtime.ConstrainedExecution;

namespace Typhon.Engine.Internals;

/// <summary>
/// Finalizer-based sentinel object that transitions a claimed <see cref="ThreadSlot"/> to <see cref="SlotState.Retiring"/> when its owning thread dies.
/// </summary>
/// <remarks>
/// <para>
/// One instance per producer thread is created when the thread claims its slot via <c>ThreadSlotRegistry.GetOrAssignSlot</c>, and it is rooted in a
/// <c>[ThreadStatic]</c> field so the GC cannot collect it while the thread is alive. When the thread terminates, the GC eventually runs the finalizer,
/// which flips the slot's state from <see cref="SlotState.Active"/> to <see cref="SlotState.Retiring"/>.
/// </para>
/// <para>
/// The profiler consumer thread sees the retiring state on its next drain pass, drains the trailing events (the buffer is still intact — just no
/// longer being written to), and then CAS-flips the slot to <see cref="SlotState.Free"/> so a new thread can reclaim it.
/// </para>
/// <para>
/// <b>Why <see cref="CriticalFinalizerObject"/>?</b> Guarantees the finalizer runs even during process shutdown or app-domain unload, mirroring the
/// pattern used by <c>EpochSlotHandle</c> in <c>EpochThreadRegistry</c>. Without this guarantee, a slot claimed by a thread that exits during shutdown
/// could remain stuck in <see cref="SlotState.Active"/> and leak until the whole process dies.
/// </para>
/// </remarks>
internal sealed class SlotReleaser : CriticalFinalizerObject
{
    private readonly int _slotIndex;

    internal SlotReleaser(int slotIndex)
    {
        _slotIndex = slotIndex;
    }

    ~SlotReleaser()
    {
        ThreadSlotRegistry.MarkRetiring(_slotIndex);
    }
}
