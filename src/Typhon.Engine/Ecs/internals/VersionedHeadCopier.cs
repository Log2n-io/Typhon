using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Copies a Versioned component's HEAD — its chain's newest committed revision — into an entity's cluster slot, the per-entity half of the open's HEAD
/// rebuild: <see cref="ArchetypeClusterState.RebuildVersionedHeadFromChain"/>, which resolves each root from the EntityMap, and the crash open's EntityMap
/// rebuild, which has every root in hand as it walks the clusters and copies the HEADs there instead of in a second walk (#1665).
/// </summary>
/// <remarks>Holds one revision and one content accessor per Versioned slot for its lifetime. Single-threaded, at open.</remarks>
internal sealed unsafe class VersionedHeadCopier : IDisposable
{
    private readonly ArchetypeClusterInfo _layout;
    private readonly ComponentTable[] _tables;
    private readonly bool _enabledBitsTrusted;
    private readonly ushort _addedSlotMask;
    private readonly ChunkAccessor<PersistentStore>[] _compRevAccessors;
    private readonly ChunkAccessor<PersistentStore>[] _contentAccessors;

    /// <summary>The (entity, Versioned slot) pairs the copies gave up on, by reason (<see cref="VersionedHeadRebuildSkips"/>).</summary>
    public VersionedHeadRebuildSkips Skips;

    /// <param name="meta">The archetype's metadata.</param>
    /// <param name="layout">Its cluster layout.</param>
    /// <param name="tables">Its component table per slot.</param>
    /// <param name="enabledBitsTrusted">Whether the enabled bits a rootless slot is classified by can be trusted (see the rebuild's parameter).</param>
    /// <param name="addedSlotMask">The slots a component-set migration added in this open (see the rebuild's parameter).</param>
    public VersionedHeadCopier(ArchetypeMetadata meta, ArchetypeClusterInfo layout, ComponentTable[] tables, bool enabledBitsTrusted, ushort addedSlotMask)
    {
        _layout = layout;
        _tables = tables;
        _enabledBitsTrusted = enabledBitsTrusted;
        _addedSlotMask = addedSlotMask;
        _compRevAccessors = new ChunkAccessor<PersistentStore>[meta.ComponentCount];
        _contentAccessors = new ChunkAccessor<PersistentStore>[meta.ComponentCount];
        var slotToVi = layout.SlotToVersionedIndex;
        for (var slot = 0; slot < meta.ComponentCount; slot++)
        {
            if (slotToVi[slot] >= 0)
            {
                _compRevAccessors[slot] = tables[slot].CompRevTableSegment.CreateChunkAccessor();
                _contentAccessors[slot] = tables[slot].ComponentSegment.CreateChunkAccessor();
            }
        }
    }

    /// <summary>
    /// Copies the HEAD of the chain at <paramref name="root"/> into Versioned slot <paramref name="slot"/> of the entity at <paramref name="slotIndex"/> of
    /// the cluster at <paramref name="clusterBase"/>, or counts why it cannot. The cluster's page is the caller's to mark modified.
    /// </summary>
    /// <param name="slot">The component slot, a Versioned one.</param>
    /// <param name="root">The slot's chain root from the entity's record, 0 when it has none.</param>
    /// <param name="clusterBase">The entity's cluster.</param>
    /// <param name="slotIndex">The entity's slot in the cluster.</param>
    /// <param name="enabledBits">The entity's enabled mask, which classifies a slot with no root.</param>
    public void CopySlot(int slot, int root, byte* clusterBase, int slotIndex, ushort enabledBits)
    {
        if (root == 0)
        {
            // No chain root for this Versioned slot. The enabled bit says which of the two states this is, and they are not the same event: bit CLEAR is a
            // component the spawn never supplied — absent by design since #845, routine on any partially-spawned entity — while bit SET means the component
            // has a value whose pointer is gone. Only the latter is counted toward the warned Total; counting both would fire a Warning on every healthy
            // database and train operators to ignore the log #688 exists to produce.
            if ((_addedSlotMask & (1 << slot)) != 0)
            {
                // A slot this open's set migration added: absent on every entity it carried across, which is the design, not a lost root.
                Skips.AbsentByDesign++;
            }
            else if (!_enabledBitsTrusted)
            {
                Skips.RootlessUnclassifiable++;
            }
            else if ((enabledBits & (1 << slot)) != 0)
            {
                Skips.ChainRootLost++;
            }
            else
            {
                Skips.AbsentByDesign++;
            }

            return;
        }

        // Walk chain to find HEAD (latest committed entry)
        var chainResult = RevisionChainReader.WalkChain(ref _compRevAccessors[slot], root, long.MaxValue, cards: null);
        if (chainResult.IsFailure)
        {
            // A chain root exists and the walk failed. Never benign: the slot keeps its stale or zero value with no other signal.
            Skips.ChainWalkFailed++;
            return;
        }

        // Read HEAD value from content chunk and copy to cluster slot
        var table = _tables[slot];
        var headChunkId = chainResult.Value.CurCompContentChunkId;
        if ((uint)headChunkId >= (uint)table.ComponentSegment.ChunkCapacity)
        {
            Skips.HeadPastSegment++;
            return;
        }

        var srcAddr = _contentAccessors[slot].GetChunkAddress(headChunkId);
        var compSize = _layout.ComponentSize(slot);
        var dstSlot = clusterBase + _layout.ComponentOffset(slot) + slotIndex * compSize;
        Unsafe.CopyBlockUnaligned(dstSlot, srcAddr + table.ComponentOverhead, (uint)compSize);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        for (var slot = 0; slot < _compRevAccessors.Length; slot++)
        {
            if (_layout.SlotToVersionedIndex[slot] >= 0)
            {
                _compRevAccessors[slot].Dispose();
                _contentAccessors[slot].Dispose();
            }
        }
    }
}
