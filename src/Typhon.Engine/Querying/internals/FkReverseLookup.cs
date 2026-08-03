using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Typhon.Engine.Internals;

/// <summary>
/// Receives each source entity found by an FK reverse lookup.
/// </summary>
/// <remarks>
/// A struct-generic callback rather than an enumerator, for two reasons the design records: the B+Tree's
/// <c>RangeMultipleEnumerator</c> is a <c>ref struct</c> so it cannot surface through <see cref="IEnumerable{T}"/>, and a hand-rolled enumerator would be
/// three-level (archetype × key × chunk) while owning per-archetype <c>ChunkAccessor</c> lifetimes across <c>MoveNext</c> boundaries. A <c>struct</c>
/// constraint devirtualises <see cref="Process"/> into the scan loop and allocates nothing. Same shape as
/// <c>RawValuePagedHashMap.IEntryAction&lt;T&gt;</c>, already used by these call sites.
/// </remarks>
internal interface IFkSourceAction
{
    /// <summary>Called once per source entity pointing at the scanned target. Returns <see langword="false"/> to stop the scan.</summary>
    bool Process(long sourcePK, ArchetypeMetadata meta);
}

/// <summary>
/// The archetypes that can hold the FK source component, split by which index home owns their field indexes.
/// </summary>
/// <remarks>
/// Resolved ONCE per query / per <c>NavigationView</c> — never per target PK. <c>NavigationView.ReverseLookupAndUpdate</c> runs once per target delta entry,
/// so anything rebuilt inside it is rebuilt per fan-out.
/// </remarks>
internal readonly struct FkCandidates
{
    /// <summary>Cluster-backed archetypes holding the source component, with the component's slot in each.</summary>
    public readonly (ArchetypeMetadata Meta, int ComponentSlot)[] ClusterArchetypes;

    /// <summary>True when at least one candidate archetype keeps its indexes on the ComponentTable, so phase 1 must run.</summary>
    public readonly bool HasNonCluster;

    public FkCandidates((ArchetypeMetadata Meta, int ComponentSlot)[] clusterArchetypes, bool hasNonCluster)
    {
        ClusterArchetypes = clusterArchetypes;
        HasNonCluster = hasNonCluster;
    }
}

/// <summary>
/// FK reverse lookup — "which source entities point at this target?" — across BOTH secondary-index homes.
/// </summary>
/// <remarks>
/// <para>
/// Secondary indexes live either on the <c>ComponentTable</c> (one tree per component type, shared by every archetype holding it, values are chunk ids) or on
/// the ARCHETYPE (one tree per archetype per field, values are packed <c>ClusterLocation</c>s). Which one applies is decided by the archetype's composition,
/// not the component's storage mode. Navigation read only the first, so for a cluster-backed source archetype it scanned an empty tree: a loud
/// <c>NotSupportedException</c> for a pure-SingleVersion source, and a SILENT empty result for a Versioned source in a mixed archetype (#662).
/// </para>
/// <para>
/// The cluster discriminator here — <c>IsClusterEligible &amp;&amp; ClusterState != null</c> — is deliberately the same expression the WRITE path uses to
/// decide where the index entry goes (<c>Transaction.ECS.cs</c>, <c>ctx.UseCluster</c>). Keying the read on the write's own condition is what stops the two
/// drifting apart again.
/// </para>
/// <para>
/// <b>Two phases, not one loop.</b> The ComponentTable index spans archetypes, so scanning it inside a per-archetype loop would rescan it K times and report
/// every hit K times. Phase 1 runs it once (and only when a non-cluster candidate exists), filtering per entity by routing id; phase 2 runs once per
/// cluster-backed candidate, filtering per archetype before it scans. Phase 1 disappears when the consolidation completes (#629 Phase 4).
/// </para>
/// </remarks>
internal static unsafe class FkReverseLookup
{
    /// <summary>
    /// Partitions the archetypes holding <paramref name="sourceTypeId"/> by index home. Call once per query / per view, never per target PK.
    /// </summary>
    internal static FkCandidates ResolveCandidates(DatabaseEngine dbe, int sourceTypeId)
    {
        ArgumentNullException.ThrowIfNull(dbe);

        List<(ArchetypeMetadata, int)> cluster = null;
        var hasNonCluster = false;

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (!meta.TryGetSlot(sourceTypeId, out var componentSlot))
            {
                continue;
            }

            if (!IsClusterHomed(dbe, meta))
            {
                hasNonCluster = true;
                continue;
            }

            (cluster ??= new List<(ArchetypeMetadata, int)>()).Add((meta, componentSlot));
        }

        return new FkCandidates(cluster?.ToArray() ?? [], hasNonCluster);
    }

    /// <summary>
    /// Partitions a SINGLE known archetype by index home, for callers that already know which archetype holds the sources.
    /// </summary>
    /// <remarks>
    /// Cascade delete resolves its child archetype from the <c>CascadeTarget</c> edge before it probes, so
    /// <see cref="ResolveCandidates"/>'s registry walk would scan every OTHER cluster archetype holding the component only for the action to discard the hits.
    /// The routing-id filter still belongs in the action: it is structurally true in phase 2 (the PK comes from THIS archetype's cluster) but phase 1 scans a
    /// tree that spans archetypes by construction, so there it remains load-bearing.
    /// </remarks>
    /// <param name="dbe">Owning engine — resolves the archetype's cluster state.</param>
    /// <param name="meta">The archetype known to hold the FK source component.</param>
    /// <param name="componentSlot">The source component's slot within <paramref name="meta"/>.</param>
    internal static FkCandidates ResolveCandidatesForArchetype(DatabaseEngine dbe, ArchetypeMetadata meta, int componentSlot)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        ArgumentNullException.ThrowIfNull(meta);

        return IsClusterHomed(dbe, meta) ? new FkCandidates([(meta, componentSlot)], false) : new FkCandidates([], true);
    }

    /// <summary>
    /// The one place that decides which home owns <paramref name="meta"/>'s field indexes.
    /// </summary>
    /// <remarks>
    /// Deliberately the same expression the WRITE path uses (<c>Transaction.ECS.cs</c>, <c>ctx.UseCluster</c>). Both partitioning entry points route through
    /// here rather than repeating the test: a second copy is how the read and the write drifted apart in the first place.
    /// </remarks>
    private static bool IsClusterHomed(DatabaseEngine dbe, ArchetypeMetadata meta)
        => meta.HasClusterIndexes && dbe._archetypeStates[meta.ArchetypeId]?.ClusterState?.IndexSlots != null;

    /// <summary>
    /// Invokes <paramref name="action"/> for every source entity whose FK field equals <paramref name="targetPK"/>, across both index homes.
    /// </summary>
    /// <param name="dbe">Owning engine — resolves per-archetype cluster state and routing ids.</param>
    /// <param name="sourceCT">Component table of the FK source component.</param>
    /// <param name="candidates">Archetypes holding the source component, pre-partitioned by index home. See <see cref="ResolveCandidates"/>.</param>
    /// <param name="fkFieldOrdinal">
    /// Position of the FK field among the source component's indexed fields. Indexes BOTH <c>IndexedFieldInfos[o]</c> and
    /// <c>IndexSlots[s].Fields[o]</c> — see <see cref="PipelineExecutor.FindFKIndexOrdinal"/> for why the two are positionally aligned.
    /// </param>
    /// <param name="targetPK">Primary key of the target entity whose referrers are wanted.</param>
    /// <param name="action">Receives each source entity found; returning <see langword="false"/> stops the scan.</param>
    internal static void ForEachSource<TAction>(DatabaseEngine dbe, ComponentTable sourceCT, in FkCandidates candidates, int fkFieldOrdinal, long targetPK,
        ref TAction action) where TAction : struct, IFkSourceAction
    {
        // Phase 1 — the shared ComponentTable tree, once for all non-cluster archetypes.
        if (candidates.HasNonCluster && !ScanComponentTable(dbe, sourceCT, fkFieldOrdinal, targetPK, ref action))
        {
            return;
        }

        // Phase 2 — one per-archetype tree per cluster-backed candidate.
        var clusterArchetypes = candidates.ClusterArchetypes;
        for (var i = 0; i < clusterArchetypes.Length; i++)
        {
            var (meta, componentSlot) = clusterArchetypes[i];
            if (!ScanClusterArchetype(dbe, meta, componentSlot, fkFieldOrdinal, targetPK, ref action))
            {
                return;
            }
        }
    }

    /// <summary>Phase 1: the per-ComponentTable FK tree. Values are chunk ids; <see cref="FkSourcePkResolver"/> maps each to the owning entity's PK.</summary>
    private static bool ScanComponentTable<TAction>(DatabaseEngine dbe, ComponentTable sourceCT, int fkFieldOrdinal, long targetPK, ref TAction action)
        where TAction : struct, IFkSourceAction
    {
        var fkIndex = sourceCT.IndexedFieldInfos[fkFieldOrdinal].Index as BTree<long, PersistentStore>;
        if (fkIndex == null)
        {
            return true;
        }

        var pkResolver = FkSourcePkResolver.Create(sourceCT);
        try
        {
            var enumerator = fkIndex.EnumerateRangeMultiple(targetPK, targetPK);
            try
            {
                while (enumerator.MoveNextKey())
                {
                    do
                    {
                        var values = enumerator.CurrentValues;
                        for (var j = 0; j < values.Length; j++)
                        {
                            var sourcePK = pkResolver.Resolve(values[j]);
                            // This tree spans archetypes, so the owning archetype is only known per entity — unlike phase 2, which knows it up front.
                            var meta = dbe.GetMetaByRouting(EntityId.FromRaw(sourcePK).ArchetypeId);
                            if (!action.Process(sourcePK, meta))
                            {
                                return false;
                            }
                        }
                    }
                    while (enumerator.NextChunk());
                }
            }
            finally
            {
                enumerator.Dispose();
            }
        }
        finally
        {
            pkResolver.Dispose();
        }

        return true;
    }

    /// <summary>
    /// Phase 2: one archetype's own FK tree. Values are packed <c>ClusterLocation</c>s (<c>clusterChunkId * 64 + slotIndex</c>), so the entity PK comes from
    /// the cluster's own entity-id array rather than from a chunk header.
    /// </summary>
    private static bool ScanClusterArchetype<TAction>(DatabaseEngine dbe, ArchetypeMetadata meta, int componentSlot, int fkFieldOrdinal, long targetPK,
        ref TAction action) where TAction : struct, IFkSourceAction
    {
        var clusterState = dbe._archetypeStates[meta.ArchetypeId]?.ClusterState;
        var ixSlotIdx = FindIndexSlot(clusterState, componentSlot);
        if (ixSlotIdx < 0)
        {
            return true;
        }

        ref var ixSlot = ref clusterState.IndexSlots[ixSlotIdx];
        if ((uint)fkFieldOrdinal >= (uint)ixSlot.Fields.Length)
        {
            // The ordinal is derived from the ComponentTable's indexed-field list; a mismatch here means the two orderings have diverged, which would
            // misroute every cluster FK lookup rather than fail. See PipelineExecutor.FindFKIndexOrdinal.
            Debug.Fail($"FK ordinal {fkFieldOrdinal} is out of range for archetype '{meta.Name}' slot {componentSlot} ({ixSlot.Fields.Length} indexed fields)");
            return true;
        }

        var fkIndex = ixSlot.Fields[fkFieldOrdinal].Index as BTree<long, PersistentStore>;
        if (fkIndex == null)
        {
            return true;
        }

        var layout = clusterState.Layout;
        var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {
            var enumerator = fkIndex.EnumerateRangeMultiple(targetPK, targetPK);
            try
            {
                while (enumerator.MoveNextKey())
                {
                    do
                    {
                        var values = enumerator.CurrentValues;
                        for (var j = 0; j < values.Length; j++)
                        {
                            var clusterLocation = values[j];
                            var clusterBase = clusterAccessor.GetChunkAddress(clusterLocation >> 6);
                            var sourcePK = *(long*)(clusterBase + layout.EntityIdsOffset + (clusterLocation & 0x3F) * 8);
                            if (!action.Process(sourcePK, meta))
                            {
                                return false;
                            }
                        }
                    }
                    while (enumerator.NextChunk());
                }
            }
            finally
            {
                enumerator.Dispose();
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }

        return true;
    }

    /// <summary>Locates the <c>IndexSlots</c> entry owning <paramref name="componentSlot"/>, or -1 when that component has no indexed field here.</summary>
    private static int FindIndexSlot(ArchetypeClusterState clusterState, int componentSlot)
    {
        var slots = clusterState.IndexSlots;
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i].Slot == componentSlot)
            {
                return i;
            }
        }

        return -1;
    }
}
