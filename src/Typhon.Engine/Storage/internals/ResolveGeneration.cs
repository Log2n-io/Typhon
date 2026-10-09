namespace Typhon.Engine.Internals;

/// <summary>
/// A counter an accessor bumps whenever it lets go of a page it holds: a slot evicted to make room, or the accessor disposed (#1199).
/// </summary>
/// <remarks>
/// <para>
/// A handle that cached a pointer into a page resolved through such an accessor (<see cref="EntityRef"/>'s cluster base) records the counter's value
/// at resolve. While the value is unchanged, every page the accessor resolved since is still held by its slot (<c>SlotRefCount</c>), so the cached
/// pointer is valid whatever the epoch does. Once it moves, the handle re-resolves from the cluster id it kept instead of trusting the pointer.
/// </para>
/// <para>
/// One counter per <see cref="EntityAccessor"/>, shared by every accessor that resolves handles for it (its cluster caches and each
/// <see cref="ArchetypeAccessor{TArch}"/> built on it). Touched only by the accessor's owning thread, so a plain field.
/// </para>
/// </remarks>
internal sealed class ResolveGeneration
{
    public int Value;
}
