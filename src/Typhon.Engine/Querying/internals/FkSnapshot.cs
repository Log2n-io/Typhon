using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// An FK reverse lookup answered at a transaction's snapshot (#1247, VIS-13): "which sources' foreign key named this target AT THE SNAPSHOT?". The FK trees
/// hold the newest committed values, and a destroy takes its entries out at its publish; a navigation over them returned sources spawned after the snapshot,
/// lost ones destroyed after it and followed foreign keys retargeted after it. Built once per query or per view refresh — never per target — over every
/// candidate source archetype, it carries, per archetype, the entities a change newer than the snapshot destroyed or re-keyed, read at the snapshot
/// (<see cref="IndexSnapshotSide{TKey}"/>), and the gate a tree candidate passes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Against the trees' reads.</b> Each target's tree entries are read before they are decided (<see cref="Sync"/>): the archetype's
/// <see cref="ArchetypeClusterState.KeyChangeCount"/>, read when this was built, is read again after each target's tree read, and a destroy or key move
/// counted since is read at the snapshot before any candidate of that target is admitted. A lookup per exact key needs no floor: an entity re-keyed after
/// one target was answered is dropped from the trees for every later target, and found at its snapshot key by its own.
/// </para>
/// <para>
/// Its absence (<see langword="null"/>) is the HEAD lookup the cascade makes: a destroy reads the children committed now, not those its snapshot held.
/// </para>
/// </remarks>
internal sealed unsafe class FkSnapshot : IDisposable
{
    internal sealed class Entry
    {
        public Transaction.IndexArchetypeSource Source;
        public IndexSnapshotSide<long> Side;
        public long Mark;
        public ChunkAccessor<PersistentStore> Map;
    }

#pragma warning disable TYPHON005 // The snapshot borrows the query's transaction, it does not own it
    private readonly Transaction _tx;
#pragma warning restore TYPHON005
    private readonly bool _includeOwn;
    private readonly Entry[] _entries;

    /// <param name="tx">The transaction whose snapshot answers.</param>
    /// <param name="candidates">The source archetypes, as <see cref="FkReverseLookup.ResolveCandidates"/> partitioned them; entries align with them.</param>
    /// <param name="fkFieldOrdinal">The FK field's position among the source component's indexed fields.</param>
    /// <param name="includeOwn">The transaction's own spawns and destroys count (a query); a view's refresh reads committed state only.</param>
    internal FkSnapshot(Transaction tx, in FkCandidates candidates, int fkFieldOrdinal, bool includeOwn)
    {
        _tx = tx;
        _includeOwn = includeOwn;
        TSN = tx.TSN;
        Cards = tx.DBE.CommitCards;
        var archetypes = candidates.ClusterArchetypes;
        _entries = new Entry[archetypes.Length];
        try
        {
            for (var i = 0; i < archetypes.Length; i++)
            {
                var (meta, componentSlot) = archetypes[i];
                if (meta.VersionedSlotMask == 0)
                {
                    continue;   // no isolation promised: the HEAD lookup answers
                }

                var es = tx.DBE._archetypeStates[meta.ArchetypeId];
                var cs = es?.ClusterState;
                var ixSlot = FkReverseLookup.FindIndexSlot(cs, componentSlot);
                if (ixSlot < 0 || (uint)fkFieldOrdinal >= (uint)cs.IndexSlots[ixSlot].Fields.Length)
                {
                    continue;
                }

                var layout = cs.Layout;
                var entry = new Entry
                {
                    Source = new Transaction.IndexArchetypeSource
                    {
                        Meta = meta,
                        CompSlot = componentSlot,
                        ClusterState = cs,
                        EngineState = es,
                        RecordSize = meta._entityRecordSize,
                        CompOffset = layout.ComponentOffset(componentSlot),
                        CompSize = layout.ComponentSize(componentSlot),
                        FieldOffset = cs.IndexSlots[ixSlot].Fields[fkFieldOrdinal].FieldOffset,
                        VersionedIndex = layout.SlotToVersionedIndex != null ? layout.SlotToVersionedIndex[componentSlot] : -1,
                    },
                    Map = es.EntityMap.Segment.CreateChunkAccessor(),
                };
                _entries[i] = entry;
                entry.Side = IndexSnapshotSide<long>.Create(tx, [entry.Source], long.MinValue, long.MaxValue, out entry.Mark, includeOwn: includeOwn);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The entry of the candidate archetype at <paramref name="index"/>, or null: that archetype answers at HEAD.</summary>
    internal Entry this[int index] => _entries[index];

    internal long TSN { get; }

    internal CommitCardTable Cards { get; }

    /// <summary>
    /// After a target's tree entries were read, before any is decided: a destroy or key move counted since the last look is read at the snapshot (a side
    /// built on the first one).
    /// </summary>
    internal void Sync(Entry entry)
    {
        if (entry.Side != null)
        {
            if (entry.Side.NeedsRefresh())
            {
                entry.Side.Refresh(false, 0);
            }

            return;
        }

        if (entry.Source.ClusterState.KeyChangeCount == entry.Mark)
        {
            return;
        }

        entry.Side = IndexSnapshotSide<long>.Create(_tx, [entry.Source], long.MinValue, long.MaxValue, out entry.Mark, includeOwn: _includeOwn);
    }

    /// <summary>
    /// Whether a tree candidate is a source at the snapshot: not one the side reads, alive at the snapshot — a hidden slot decided by its record, which must
    /// name it; an occupied one by the cluster's summary, read after the occupancy word, then its record.
    /// </summary>
    internal bool Admits(Entry entry, byte* clusterBase, int clusterChunkId, int slot, long sourcePK, bool occupied)
    {
        if (entry.Side != null && entry.Side.IsDropped(sourcePK))
        {
            return false;
        }

        var src = entry.Source;
        var record = stackalloc byte[src.RecordSize];
        if (!occupied)
        {
            return (src.ClusterState.ClaimedBits(clusterChunkId) & (1UL << slot)) != 0
                   && SnapshotSlot.IsReservedSpawnVisible(sourcePK, clusterChunkId, slot, src.EngineState, record, TSN, Cards, ref entry.Map);
        }

        return src.ClusterState.IsClusterFullyVisibleAt(clusterChunkId, TSN)
               || SnapshotSlot.IsVisibleAtSnapshot(sourcePK, true, src.EngineState, record, src.RecordSize, TSN, Cards, ref entry.Map);
    }

#pragma warning disable TYPHON006 // _tx is borrowed: its owner disposes it
    public void Dispose()
#pragma warning restore TYPHON006
    {
        foreach (var e in _entries)
        {
            if (e == null)
            {
                continue;
            }

            e.Map.Dispose();
            e.Side?.Release();
        }
    }
}
