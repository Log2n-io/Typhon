using System.Collections.Generic;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>One component slot whose <c>String1024</c> texts the cluster slot owns outright (SingleVersion or Transient), freed when the slot is
/// destroyed.</summary>
internal struct ClusterTextSlot
{
    /// <summary>Component slot index within the archetype.</summary>
    public int Slot;

    /// <summary>The owning table: its text fields and its text segment. A cluster slot carries no overhead, so its value-relative offsets are
    /// slot-relative.</summary>
    public ComponentTable Table;

    /// <summary>The component is Transient: its bytes live in the cluster's Transient segment and its texts in the table's Transient text segment.</summary>
    public bool Transient;
}

internal sealed unsafe partial class ArchetypeClusterState
{
    /// <summary>
    /// The SingleVersion and Transient components of this archetype that carry a <c>String1024</c> field: the cluster slot is the only owner of their texts,
    /// so a destroy frees them. A Versioned text is owned by the revisions naming it and freed by the revision cleanup. Null when no such component exists —
    /// zero cost on the destroy path.
    /// </summary>
    internal ClusterTextSlot[] TextSlots;

    /// <summary>Whether a Transient component of this archetype carries a text: a destroy then reads the slot's Transient bytes too.</summary>
    private bool _hasTransientTextSlot;

    /// <summary>
    /// Per slot, the table of a component that holds out-of-line data (a collection or a text), null otherwise: what a cluster-level whole-value store checks
    /// its handles against (<see cref="CollectionHandles.Keep"/>).
    /// </summary>
    internal ComponentTable[] OutOfLineTableBySlot;

    /// <summary>Builds <see cref="TextSlots"/> and <see cref="OutOfLineTableBySlot"/>.</summary>
    public void InitializeTexts(ComponentTable[] slotToTable)
    {
        OutOfLineTableBySlot = new ComponentTable[slotToTable.Length];
        List<ClusterTextSlot> slots = null;
        for (var slot = 0; slot < slotToTable.Length; slot++)
        {
            var table = slotToTable[slot];
            if (table != null && table.HasOutOfLineData)
            {
                OutOfLineTableBySlot[slot] = table;
            }

            if (table == null || !table.HasText || table.StorageMode == StorageMode.Versioned)
            {
                continue;
            }

            var transient = table.StorageMode == StorageMode.Transient;
            _hasTransientTextSlot |= transient;
            (slots ??= []).Add(new ClusterTextSlot { Slot = slot, Table = table, Transient = transient });
        }

        TextSlots = slots?.ToArray();
    }

    /// <summary>
    /// Frees the texts one slot holds, on a true destroy (never a migration, which moves the handles with the bytes), before the slot is cleared.
    /// <paramref name="persistentBase"/> / <paramref name="transientBase"/> are the cluster's bases in each segment; a null Transient base is resolved here.
    /// </summary>
    private void ReleaseSlotTexts(int clusterChunkId, int slotIndex, byte* persistentBase, byte* transientBase)
    {
        if (transientBase != null || !_hasTransientTextSlot)
        {
            ReleaseSlotTextsAt(slotIndex, persistentBase, transientBase);
            return;
        }

        var accessor = TransientSegment.CreateChunkAccessor();
        try
        {
            ReleaseSlotTextsAt(slotIndex, persistentBase, accessor.GetChunkAddress(clusterChunkId, true));
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private void ReleaseSlotTextsAt(int slotIndex, byte* persistentBase, byte* transientBase)
    {
        var layout = Layout;
        foreach (var ts in TextSlots)
        {
            var compBase = (ts.Transient ? transientBase : persistentBase) + layout.ComponentOffset(ts.Slot) + slotIndex * layout.ComponentSize(ts.Slot);
            foreach (var f in ts.Table.TextFields)
            {
                var chunkId = TextChunks.HandleAt(compBase, f.OffsetInComponentStorage);
                if (chunkId == 0)
                {
                    continue;
                }

                TextChunks.SetHandleAt(compBase, f.OffsetInComponentStorage, 0);
                if (ts.Transient)
                {
                    ts.Table.TransientTextSegment.FreeChunk(chunkId);
                }
                else
                {
                    ts.Table.TextSegment.FreeChunk(chunkId);
                }
            }
        }
    }
}
