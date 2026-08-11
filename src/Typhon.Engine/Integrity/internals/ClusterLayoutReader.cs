using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// Re-derives an archetype's cluster layout — slot count, and where each component's data block starts — from the
/// manifest alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Correcting <c>09 §1.1</c> again.</b> That section listed "cluster size and component count" as recovered by
/// <c>ArchetypeR1</c>. Only the second is true: the row carries <c>ComponentCount</c> and no <c>ClusterSize</c>. The
/// generalisation was the same shape as the original error it was correcting — one fact checked, a neighbouring one
/// assumed.
/// </para>
/// <para>
/// It does not need to be persisted, because it is a <b>pure function of things that are</b>.
/// <c>ArchetypeClusterInfo.SelectClusterSize</c> takes only a fixed header size and a per-entity size, and both come
/// out of the manifest: the header from <c>ComponentCount</c>, the per-entity size from the component sizes in
/// <c>ComponentR1</c> plus four bytes for every <c>AllowMultiple</c> indexed field in <c>FieldR1</c>. So this calls the
/// engine's own selector with reconstructed arguments rather than reimplementing its scoring loop — the arguments are
/// what has to be recovered; the algorithm is already here.
/// </para>
/// <para>
/// <b>Why the slot count matters more than it looks.</b> It sets where the entity-key array ends, and reading past it
/// yields component bytes that decode as plausible entity keys — that produced three <c>CLU-01</c> findings on a
/// healthy database, with ids like <c>12592</c>, which is ASCII <c>"01"</c> out of a <c>String64</c>. The occupancy
/// word gives a sound lower bound without any of this; what the derivation adds is the ability to address component
/// data at all, which is what <c>CLU-03</c> and <c>IDX-03/04</c> need.
/// </para>
/// </remarks>
internal sealed class ClusterLayoutReader
{
    private ClusterLayoutReader(int clusterSize, int stride, IReadOnlyList<int> componentOffsets,
        IReadOnlyList<int> componentSizes, IReadOnlyList<string> componentNames)
    {
        ClusterSize = clusterSize;
        Stride = stride;
        ComponentOffsets = componentOffsets;
        ComponentSizes = componentSizes;
        ComponentNames = componentNames;
    }

    /// <summary>Entity slots per cluster, as the engine's selector chooses it.</summary>
    public int ClusterSize { get; }

    /// <summary>The cluster stride this layout implies, for corroboration against the persisted one.</summary>
    public int Stride { get; }

    /// <summary>Byte offset of each component's data block from the cluster base, in slot order.</summary>
    public IReadOnlyList<int> ComponentOffsets { get; }

    /// <summary>Per-entity size of each component's data, in slot order.</summary>
    public IReadOnlyList<int> ComponentSizes { get; }

    /// <summary>Component schema names in slot order.</summary>
    public IReadOnlyList<string> ComponentNames { get; }

    /// <summary>Byte offset of the entity-key array.</summary>
    public int EntityKeysOffset => 8 + (8 * ComponentNames.Count);

    /// <summary>Byte offset of one entity's data for one component slot.</summary>
    /// <param name="componentSlot">The component's slot in the archetype.</param>
    /// <param name="entitySlot">The entity's slot in the cluster.</param>
    public int ComponentDataOffset(int componentSlot, int entitySlot)
        => ComponentOffsets[componentSlot] + (entitySlot * ComponentSizes[componentSlot]);

    /// <summary>
    /// Derives the layout for an archetype, or <c>null</c> when the manifest does not describe it completely enough.
    /// </summary>
    /// <param name="manifest">The schema manifest.</param>
    /// <param name="archetype">The archetype to lay out.</param>
    public static ClusterLayoutReader TryDerive(SchemaCatalogReader manifest, ArchetypeView archetype)
    {
        if (archetype.ComponentNames.Count == 0 || archetype.ComponentNames.Count != archetype.ComponentCount)
        {
            return null;   // CLU-04 reports the disagreement; deriving through it would build on the wrong one
        }

        var sizes = new int[archetype.ComponentNames.Count];
        var multipleIndexedFields = 0;

        for (var slot = 0; slot < archetype.ComponentNames.Count; slot++)
        {
            if (!manifest.Components.TryGetValue(archetype.ComponentNames[slot], out var component) || component.Size <= 0)
            {
                return null;
            }

            sizes[slot] = component.Size;

            foreach (var field in component.Fields)
            {
                if (field.HasIndex && field.IndexAllowMultiple)
                {
                    multipleIndexedFields++;
                }
            }
        }

        var fixedHeader = 8 + (8 * sizes.Length);
        var perEntitySize = 8 + (multipleIndexedFields * sizeof(int));
        foreach (var size in sizes)
        {
            perEntitySize += size;
        }

        int clusterSize;
        try
        {
            clusterSize = ArchetypeClusterInfo.SelectClusterSize(fixedHeader, perEntitySize);
        }
        catch (InvalidOperationException)
        {
            return null;   // components too large to cluster; the archetype has no cluster storage to read
        }

        var offsets = new int[sizes.Length];
        var offset = fixedHeader + (8 * clusterSize);
        for (var slot = 0; slot < sizes.Length; slot++)
        {
            offsets[slot] = offset;
            offset += sizes[slot] * clusterSize;
        }

        var stride = ArchetypeClusterInfo.AlignStride(offset + (multipleIndexedFields * clusterSize * sizeof(int)));
        return new ClusterLayoutReader(clusterSize, stride, offsets, sizes, archetype.ComponentNames);
    }
}
