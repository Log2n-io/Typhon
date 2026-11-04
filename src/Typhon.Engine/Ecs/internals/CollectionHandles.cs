using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// The rule every whole-value store of a component applies to its out-of-line handles — <c>ComponentCollection</c> buffers (#1362) and <c>String1024</c> text
/// chunks (#1094): a store copies bytes and moves no ownership, so it never changes a collection or a text — that is
/// <see cref="EntityRefMut.CreateComponentCollectionAccessor{T, TElem}"/>'s and <see cref="EntityRefMut.SetText{T}(Comp{T}, ref T, ref
/// Typhon.Schema.Definition.String1024, System.ReadOnlySpan{char})"/>'s alone.
/// </summary>
/// <remarks>
/// Changing one was a leak or a use-after-free: an empty handle orphaned the stored buffer; a stale one — a copy read before an accessor moved the collection —
/// named a buffer whose reference belongs to an older revision, freed under this one once that revision was collected. A text chunk has the same two failures,
/// and one more: two components naming one chunk, each freeing it.
/// </remarks>
internal static unsafe class CollectionHandles
{
    /// <summary>
    /// Gives <paramref name="value"/> the out-of-line handles <paramref name="stored"/> holds, field by field: an empty handle keeps the stored one, an
    /// equal one
    /// is the stored one, any other is refused — unless <paramref name="adoptIntoEmpty"/> and the stored component holds no buffer there, where the value
    /// supplies
    /// the collection as a spawn does. A text is never adopted: it is set through <c>SetText</c> once the component is stored.
    /// </summary>
    /// <param name="table">The component's table: its collection and text fields.</param>
    /// <param name="value">The value about to be stored, on the stack.</param>
    /// <param name="stored">The stored component, in engine-owned memory.</param>
    /// <param name="adoptIntoEmpty">The store supplies a component that had no value (<c>Enable(comp, value)</c>).</param>
    /// <exception cref="System.InvalidOperationException">The value names a buffer or a text chunk the stored component does not.</exception>
    internal static void Keep(ComponentTable table, byte* value, byte* stored, bool adoptIntoEmpty)
    {
        foreach (var f in table.CollectionFields)
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
                ThrowHelper.ThrowInvalidOp($"A store of '{table.Name}' carries a collection the entity does not hold (buffer {mine}, stored {storedHandle}): "
                    + "a copy read before the collection changed, or one built elsewhere. Change a collection through "
                    + "EntityRefMut.CreateComponentCollectionAccessor — Add, Replace, Clear.");
            }

            Unsafe.WriteUnaligned(handle, storedHandle);
        }

        foreach (var f in table.TextFields)
        {
            var handle = value + f.OffsetInComponentStorage;
            var mine = Unsafe.ReadUnaligned<int>(handle);
            var storedHandle = Unsafe.ReadUnaligned<int>(stored + f.OffsetInComponentStorage);
            if (mine == storedHandle)
            {
                continue;
            }

            if (mine != 0)
            {
                ThrowHelper.ThrowInvalidOp($"A store of '{table.Name}' carries a text the entity does not hold (chunk {mine}, stored {storedHandle}): a copy "
                    + "read before the text changed, or one taken from another entity. Change a text through EntityRefMut.SetText / ClearText.");
            }

            Unsafe.WriteUnaligned(handle, storedHandle);
        }
    }

    /// <summary>
    /// The spawn's form of the rule: a value supplied to a spawn may carry collections it built (adopted), but no text — the entity owns no chunk yet, and a
    /// handle in the value is one taken from elsewhere. Set the text after the spawn.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">The value carries a text handle.</exception>
    internal static void RefuseSpawnedText(ComponentTable table, System.ReadOnlySpan<byte> value)
    {
        foreach (var f in table.TextFields)
        {
            var mine = value.Length >= f.OffsetInComponentStorage + sizeof(int)
                ? System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(value[f.OffsetInComponentStorage..])
                : 0;
            if (mine != 0)
            {
                ThrowHelper.ThrowInvalidOp($"A spawn of '{table.Name}' carries text chunk {mine}, which is another entity's: spawn with no text and set it "
                    + "through EntityRefMut.SetText.");
            }
        }
    }
}
