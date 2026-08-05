using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// The assertion half of the T-5 differential recovery oracle (design 03 §4.2). Drives a <see cref="RecoveryShadowModel"/> against a recovered engine on two axes:
/// the <b>primary</b> broad-scan/per-id axis (<see cref="AssertPrimaryAxis"/> — recovered state ≡ shadow) and the <b>index</b> axis (each secondary index's result set
/// ≡ the broad-scan set; built from <see cref="BroadScanEntityIds"/> + <see cref="IndexEntityIds{T,TKey}"/>). Kept separate from the shadow so the future crash sweep
/// (A1.2) can reuse the same assertions over many crash points.
/// </summary>
internal static class RecoveryOracle
{
    /// <summary>Assert the recovered engine reproduces the shadow exactly (values, enabled-bits, alive-set, no resurrection). Fails with the full structured diff.</summary>
    public static void AssertPrimaryAxis(DatabaseEngine recoveredDbe, RecoveryShadowModel shadow)
    {
        var diffs = shadow.Diff(recoveredDbe);
        Assert.That(
            diffs,
            Is.Empty,
            () => $"Differential oracle — primary (broad-scan) axis found {diffs.Count} mismatch(es):{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", diffs));
    }

    /// <summary>The set of entity ids a broad scan (no secondary index) reports for <paramref name="archetypeId"/> at the transaction's snapshot.</summary>
    public static HashSet<EntityId> BroadScanEntityIds(Transaction tx, ushort archetypeId) => new(tx.EnumerateArchetypeEntities(archetypeId));

    /// <summary>
    /// The set of entity ids a secondary index reports over the full key range. Compared against <see cref="BroadScanEntityIds"/> for the index axis: divergence means
    /// the index disagrees with the primary store (RB-01/RB-02 — e.g. recovery rebuilt the entity but not its index entry).
    /// </summary>
    public static HashSet<EntityId> IndexEntityIds<T, TKey>(DatabaseEngine dbe, Transaction tx, Expression<Func<T, TKey>> keySelector, TKey min, TKey max)
        where T : unmanaged
        where TKey : unmanaged
    {
        var set = new HashSet<EntityId>();
        var indexRef = dbe.GetIndexRef<T, TKey>(keySelector);
        using var e = tx.EnumerateIndex<T, TKey>(indexRef, min, max);
        foreach (var item in e)
        {
            set.Add(EntityId.FromRaw(item.EntityPK));
        }

        return set;
    }

    /// <summary>
    /// The index axis for a CLUSTER-BACKED archetype: the entity ids its own per-archetype B+Tree reports over the full key range, read straight from the tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IndexEntityIds{T,TKey}"/> cannot serve this case. It goes through <c>Transaction.EnumerateIndex</c>, which resolves the index off
    /// <c>ComponentTable.IndexedFieldInfos</c> — the per-ComponentTable home. A cluster-backed archetype keeps its indexes on the ARCHETYPE, so that tree is
    /// empty and the helper would report nothing while the data recovered perfectly: the exact silent-shortfall shape this oracle exists to catch, inverted
    /// into a false positive. Teaching <c>EnumerateIndex</c> about both homes needs the K-way merge decision and belongs to #666.
    /// </para>
    /// <para>
    /// Reads the tree rather than issuing a query for the same reason the rest of the cluster-index tests do: the planner takes the zone-map path at these
    /// entity counts and never touches the B+Tree, so a query would report the cluster DATA and pass straight over an index that was never rebuilt.
    /// </para>
    /// </remarks>
    /// <param name="dbe">The recovered engine.</param>
    /// <param name="meta">Metadata of the cluster-backed archetype under test.</param>
    /// <param name="indexSlotOrdinal">Position in <c>ClusterState.IndexSlots</c>.</param>
    /// <param name="fieldOrdinal">Position among that slot's indexed fields.</param>
    /// <param name="min">Inclusive low key.</param>
    /// <param name="max">Inclusive high key.</param>
    public static unsafe HashSet<EntityId> ClusterIndexEntityIds<TKey>(DatabaseEngine dbe, ArchetypeMetadata meta, int indexSlotOrdinal, int fieldOrdinal,
        TKey min, TKey max)
        where TKey : unmanaged
    {
        var set = new HashSet<EntityId>();
        var clusterState = dbe._archetypeStates[meta.ArchetypeId]?.ClusterState;
        Assert.That(clusterState?.IndexSlots, Is.Not.Null, "the archetype under test must be cluster-backed with per-archetype indexes");

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        ref var field = ref clusterState.IndexSlots[indexSlotOrdinal].Fields[fieldOrdinal];
        if (field.Index is not BTree<TKey, PersistentStore> tree)
        {
            Assert.Fail($"index key type mismatch: tree is '{field.Index.GetType().GenericTypeArguments[0].Name}', caller specified '{typeof(TKey).Name}'");
            return set;
        }

        var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {
            // The two index shapes need different enumerators — EnumerateRangeMultiple throws on a unique tree — but decode identically: the leaf value is a
            // packed ClusterLocation (clusterChunkId * 64 + slot) and the PK comes from the cluster's own entity-id array.
            if (field.AllowMultiple)
            {
                var multi = tree.EnumerateRangeMultiple(min, max);
                try
                {
                    while (multi.MoveNextKey())
                    {
                        do
                        {
                            var values = multi.CurrentValues;
                            for (var j = 0; j < values.Length; j++)
                            {
                                set.Add(DecodeClusterLocation(clusterState, ref clusterAccessor, values[j]));
                            }
                        }
                        while (multi.NextChunk());
                    }
                }
                finally
                {
                    multi.Dispose();
                }
            }
            else
            {
                using var unique = tree.EnumerateRange(min, max);
                while (unique.MoveNext())
                {
                    set.Add(DecodeClusterLocation(clusterState, ref clusterAccessor, unique.Current.Value));
                }
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }

        return set;
    }

    /// <summary>Resolves a packed <c>ClusterLocation</c> leaf value to the entity that occupies that cluster slot.</summary>
    private static unsafe EntityId DecodeClusterLocation(ArchetypeClusterState clusterState, ref ChunkAccessor<PersistentStore> clusterAccessor,
        int clusterLocation)
    {
        var clusterBase = clusterAccessor.GetChunkAddress(clusterLocation >> 6);
        return EntityId.FromRaw(*(long*)(clusterBase + clusterState.Layout.EntityIdsOffset + (clusterLocation & 0x3F) * 8));
    }
}
