using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// The rule every whole-value store of a component applies to its <c>ComponentCollection</c> handles (#1362): a store copies bytes and counts no reference,
/// so it never changes a collection — that is <see cref="EntityRefMut.CreateComponentCollectionAccessor{T, TElem}"/>'s alone.
/// </summary>
/// <remarks>
/// Changing one was a leak or a use-after-free: an empty handle orphaned the stored buffer; a stale one — a copy read before an accessor moved the collection —
/// named a buffer whose reference belongs to an older revision, freed under this one once that revision was collected.
/// </remarks>
internal static unsafe class CollectionHandles
{
    /// <summary>
    /// Gives <paramref name="value"/> the collection handles <paramref name="stored"/> holds, field by field: an empty handle keeps the stored collection, an
    /// equal one is the stored collection, any other is refused — unless <paramref name="adoptIntoEmpty"/> and the stored component holds no buffer there,
    /// where the value supplies the collection as a spawn does.
    /// </summary>
    /// <param name="fields">The component's collection fields.</param>
    /// <param name="value">The value about to be stored, on the stack.</param>
    /// <param name="stored">The stored component, in engine-owned memory.</param>
    /// <param name="adoptIntoEmpty">The store supplies a component that had no value (<c>Enable(comp, value)</c>).</param>
    /// <param name="componentName">For the error.</param>
    /// <exception cref="System.InvalidOperationException">The value names a collection buffer the stored component does not.</exception>
    internal static void Keep(ComponentTable.CollectionFieldInfo[] fields, byte* value, byte* stored, bool adoptIntoEmpty, string componentName)
    {
        foreach (var f in fields)
        {
            var handle = value + f.OffsetInComponentStorage;
            var mine = Unsafe.ReadUnaligned<int>(handle);
            var storedHandle = Unsafe.ReadUnaligned<int>(stored + f.OffsetInComponentStorage);
            if (mine == storedHandle || (adoptIntoEmpty && storedHandle == 0))
            {
                continue;
            }

            if (mine != 0)
            {
                ThrowHelper.ThrowInvalidOp($"A store of '{componentName}' carries a collection the entity does not hold (buffer {mine}, stored {storedHandle}): "
                    + "a copy read before the collection changed, or one built elsewhere. Change a collection through "
                    + "EntityRefMut.CreateComponentCollectionAccessor — Add, Replace, Clear.");
            }

            Unsafe.WriteUnaligned(handle, storedHandle);
        }
    }
}
