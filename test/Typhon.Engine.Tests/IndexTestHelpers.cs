using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Test-side access to the per-archetype index home.
/// </summary>
/// <remarks>
/// <para>
/// Indexes live on the archetype, not on the <see cref="ComponentTable"/> (#666). A test that reads
/// <c>table.IndexStats</c> or <c>table.IndexedFieldInfos[i].Index</c> is reading a tree with no entries in it, so its
/// assertions describe nothing — and pass or fail for reasons unrelated to what they claim to check.
/// </para>
/// <para>
/// The arrays returned here are parallel to <c>ComponentTable.IndexedFieldInfos</c>, so a call site usually only has to
/// swap the accessor and keep its indices.
/// </para>
/// </remarks>
static class IndexTestHelpers
{
    /// <summary>The per-archetype <see cref="IndexStatistics"/> array for the slot of <paramref name="table"/>'s component in <typeparamref name="TArchetype"/>.</summary>
    public static IndexStatistics[] ArchetypeIndexStats<TArchetype>(DatabaseEngine dbe, ComponentTable table)
        => ResolveSlot<TArchetype>(dbe, table).Stats;

    /// <summary>The per-archetype B+Tree for one indexed field — the counterpart of <c>IndexedFieldInfos[fieldIndex].Index</c>.</summary>
    public static IBTreeIndex ArchetypeIndex<TArchetype>(DatabaseEngine dbe, ComponentTable table, int fieldIndex)
        => ResolveSlot<TArchetype>(dbe, table).Fields[fieldIndex].Index;

    /// <summary>
    /// The cluster state of whichever archetype owns an index slot for <paramref name="table"/>'s component — resolved by search, not by name.
    /// </summary>
    /// <remarks>
    /// Preferred over the generic overloads at call sites that handle several component types: guessing the archetype from the component name breaks the
    /// moment a fixture introduces a third one, which is exactly how the String64 case surfaced.
    /// </remarks>
    public static ArchetypeClusterState OwningCluster(DatabaseEngine dbe, ComponentTable table)
    {
        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            var engineState = dbe._archetypeStates[meta.ArchetypeId];
            var clusterState = engineState?.ClusterState;
            if (clusterState?.IndexSlots == null)
            {
                continue;
            }

            for (var slot = 0; slot < meta.ComponentCount; slot++)
            {
                if (!ReferenceEquals(engineState.SlotToComponentTable[slot], table))
                {
                    continue;
                }

                for (var i = 0; i < clusterState.IndexSlots.Length; i++)
                {
                    if (clusterState.IndexSlots[i].Slot == slot && clusterState.IndexSlots[i].Fields != null)
                    {
                        return clusterState;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The B+Tree for one indexed field of <paramref name="table"/>, on whichever archetype owns an index slot for it — resolved by search, not by name.
    /// </summary>
    /// <remarks>
    /// For call sites that only have the <see cref="ComponentTable"/>. Prefer <see cref="ArchetypeIndex{TArchetype}"/> when the archetype is known: a search
    /// returns the FIRST owner, which is the wrong one the moment a second archetype holds the same component.
    /// </remarks>
    public static IBTreeIndex OwningIndex(DatabaseEngine dbe, ComponentTable table, int fieldIndex)
    {
        var clusterState = OwningCluster(dbe, table);
        if (clusterState == null)
        {
            return null;
        }

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            var engineState = dbe._archetypeStates[meta.ArchetypeId];
            if (!ReferenceEquals(engineState?.ClusterState, clusterState))
            {
                continue;
            }

            for (var slot = 0; slot < meta.ComponentCount; slot++)
            {
                if (!ReferenceEquals(engineState.SlotToComponentTable[slot], table))
                {
                    continue;
                }

                for (var i = 0; i < clusterState.IndexSlots.Length; i++)
                {
                    if (clusterState.IndexSlots[i].Slot == slot && clusterState.IndexSlots[i].Fields != null)
                    {
                        return clusterState.IndexSlots[i].Fields[fieldIndex].Index;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Per-archetype statistics for <paramref name="table"/>, or <see langword="null"/> when no archetype indexes it.</summary>
    public static IndexStatistics[] OwningStats(DatabaseEngine dbe, ComponentTable table)
    {
        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            var engineState = dbe._archetypeStates[meta.ArchetypeId];
            var clusterState = engineState?.ClusterState;
            if (clusterState?.IndexSlots == null)
            {
                continue;
            }

            for (var slot = 0; slot < meta.ComponentCount; slot++)
            {
                if (!ReferenceEquals(engineState.SlotToComponentTable[slot], table))
                {
                    continue;
                }

                for (var i = 0; i < clusterState.IndexSlots.Length; i++)
                {
                    if (clusterState.IndexSlots[i].Slot == slot && clusterState.IndexSlots[i].Fields != null)
                    {
                        return clusterState.IndexSlots[i].Stats;
                    }
                }
            }
        }

        return null;
    }

    private static ClusterIndexSlot<PersistentStore> ResolveSlot<TArchetype>(DatabaseEngine dbe, ComponentTable table)
    {
        var meta = ArchetypeRegistry.GetMetadata<TArchetype>();
        Assert.That(meta, Is.Not.Null, $"archetype {typeof(TArchetype).Name} is not registered");

        var engineState = dbe._archetypeStates[meta.ArchetypeId];
        var clusterState = engineState?.ClusterState;
        Assert.That(clusterState?.IndexSlots, Is.Not.Null,
            $"{typeof(TArchetype).Name} has no per-archetype index slots — it is either not cluster-backed or has no indexed field");

        // A component sits at a different slot in each archetype, and index slots are keyed on the archetype's own slot numbering, so the table identity is
        // the only reliable join.
        for (var slot = 0; slot < meta.ComponentCount; slot++)
        {
            if (!ReferenceEquals(engineState.SlotToComponentTable[slot], table))
            {
                continue;
            }

            for (var i = 0; i < clusterState.IndexSlots.Length; i++)
            {
                if (clusterState.IndexSlots[i].Slot == slot && clusterState.IndexSlots[i].Fields != null)
                {
                    return clusterState.IndexSlots[i];
                }
            }
        }

        Assert.Fail($"{typeof(TArchetype).Name} owns no index slot for the given component table");
        return default;
    }
}
