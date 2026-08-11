using System;
using System.Buffers.Binary;

namespace Typhon.Engine.Internals;

/// <summary>
/// Emits a component's <c>ComponentCollection</c> content into a commit batch as <c>Clear</c> + <c>Append × N</c> (#389, Option B).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why full content rather than per-operation deltas.</b> The frozen D3 design assumed deltas journalled at mutation time. That is unimplementable against
/// the API as it exists: <c>ComponentCollectionAccessor</c> holds none of the entity id, the slot or the field id, is never given the <c>Transaction</c>, and
/// the <c>ref</c> it binds may legally point at a stack local <i>before the entity exists</i> — which is the documented idiom for filling a collection before
/// <c>Spawn</c>. At commit, by contrast, every one of those facts is already in hand at the <c>AddSlot</c> line.
/// </para>
/// <para>
/// Full content also dissolves the other unimplementable piece. <c>03-recovery.md</c> §5's fold calls <c>EnsureBase</c> to read a collection's pre-window
/// state, but once the handle is zeroed for LOG-06 there is no way to reach that buffer: a collection is reachable only through the row's inline
/// <c>_bufferId</c>, there is no reverse index, and the buffer root carries no owner back-pointer. Because every emission is complete, the fold always has
/// <c>baseDiscarded</c> set and <c>EnsureBase</c> is never called.
/// </para>
/// <para>
/// <b>This emission is mandatory, not opportunistic.</b> Since LOG-06 zeroes the handle in every Slot payload, applying a Slot record sets the recovered row's
/// handle to zero. If the fold has nothing for that (entity, slot, field), the collection ends up empty — including for content that was already checkpointed
/// and would previously have survived untouched. So every emitter that writes a Slot record for a collection-bearing table must call this, or it trades a
/// dangling reference for silent loss.
/// </para>
/// <para>
/// <b>Cost, accepted for v1.</b> Whole-collection emission on every commit that touches a collection-bearing component is a hot-path cost. It is taken
/// deliberately as correct-by-construction and is optimisable later behind a per-field dirty flag — get it correct, measure, then decide.
/// </para>
/// </remarks>
internal static class CollectionContentEmitter
{
    /// <summary>Per-thread staging for one collection's elements. The fence calls this concurrently across distinct tables, so it cannot be shared.</summary>
    [ThreadStatic]
    private static byte[] _scratch;

    /// <summary>
    /// Appends the full content of every collection field of <paramref name="table"/> to <paramref name="batch"/>, reading each buffer id out of
    /// <paramref name="valueBytes"/> — the component's value bytes, i.e. exactly the span the accompanying Slot record carries.
    /// </summary>
    /// <returns>
    /// The exact wire size of the records appended. The tick fence needs this: it packs batches against a byte cap and claims ring space up front, so an
    /// under-count would produce a claim too small for what is then written.
    /// </returns>
    internal static int Emit(ref CommitBatchBuilder batch, ComponentTable table, long entityId, ushort slot, ReadOnlySpan<byte> valueBytes)
    {
        var wireBytes = 0;
        foreach (var f in table.CollectionFields)
        {
            if (valueBytes.Length < f.OffsetInComponentStorage + f.HandleSize)
            {
                continue;   // a payload shorter than the schema says — nothing to read; the Slot record itself carries the diagnostic value
            }

            var bufferId = BinaryPrimitives.ReadInt32LittleEndian(valueBytes[f.OffsetInComponentStorage..]);

            // A null handle needs no record. The Slot payload's zeroed handle already says "no buffer", and apply writes exactly that, so an emitted Clear
            // would restate what the row already encodes. Every NON-null handle does emit, including one whose buffer happens to be empty, because there the
            // row and the fold can disagree and the fold has to win.
            if (bufferId == 0)
            {
                continue;
            }

            var vsbs = f.Vsbs;
            var accessor = vsbs.Segment.CreateChunkAccessor();
            try
            {
                var count = vsbs.GetElementCount(bufferId, ref accessor);
                batch.AddCollectionDelta(entityId, slot, f.FieldId, CollectionOp.Clear, 0, default);
                wireBytes += RecordWireSize(0);
                if (count == 0)
                {
                    continue;
                }

                var elementSize = vsbs.ElementSize;
                var needed = count * elementSize;
                if (_scratch == null || _scratch.Length < needed)
                {
                    _scratch = new byte[Math.Max(needed, 1024)];
                }

                var elements = _scratch.AsSpan(0, needed);
                vsbs.ReadAllElementsRaw(bufferId, elements, ref accessor);

                for (var i = 0; i < count; i++)
                {
                    batch.AddCollectionDelta(entityId, slot, f.FieldId, CollectionOp.Append, 0, elements.Slice(i * elementSize, elementSize));
                }

                wireBytes += count * RecordWireSize(elementSize);
            }
            finally
            {
                // Read-only: no CommitChanges. Emitting must not dirty a page — the WAL step observes state, it does not produce it.
                accessor.Dispose();
            }
        }

        return wireBytes;
    }

    /// <summary>Wire size of one CollectionDelta record carrying an element of <paramref name="elementLength"/> bytes.</summary>
    private static int RecordWireSize(int elementLength) => RecordHeader.SizeInBytes + CollectionDeltaRecordBody.FixedSize + elementLength;
}
