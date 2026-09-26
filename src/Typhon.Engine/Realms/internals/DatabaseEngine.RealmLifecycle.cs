using System;
using System.Collections.Generic;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>
/// Realms D5 (RT-7): realms registered and unregistered while the engine runs — dungeon instances, player houses.
/// </summary>
/// <remarks>
/// <para><b>Register</b> builds the realm's grid, grows every archetype's per-realm table to hold its id, marks which archetypes it cannot hold, and only then
/// publishes it — an entity can enter it only after it is complete. Its catalog row is written synchronously (RLM-01).</para>
/// <para><b>Unregister</b> marks the realm <see cref="RealmRunState.Closing"/>: entries are refused from then on (spawn, teleport, a raw key write — the
/// last reverted at the fence, D-2); the entities already in it stay until the application destroys them (<see cref="RealmRegistry.DestroyContents"/>).
/// The first fence that finds it holding no cluster removes it. The catalog row is marked closing synchronously at the call, so a crash at any point
/// reopens the realm as Closing: its surviving entities are filed, WAL replay applies the destroys, and an open that finds it empty after recovery
/// retires the row — only then may the id be registered again (RLM-06).</para>
/// </remarks>
public partial class DatabaseEngine
{
    private readonly Lock _realmLifecycleLock = new();

    // Ids closing or removed this session: not reusable until an open retires their catalog row after proving them empty (RLM-06).
    private HashSet<ushort> _unavailableRealmIds;

    // Catalog rows of retired realms (State = Retired), by id: a later registration of the id reuses its row with a bumped generation.
    private Dictionary<ushort, (int ChunkId, RealmR1 Row)> _retiredRealmRows;

    // Catalog realms that were closing at the last shutdown or crash: registered as usual at open, then resolved once recovery has run.
    private List<ushort> _closingRealmsAtOpen;

    /// <summary>Registers realm <paramref name="id"/> on a running engine. See the class remarks.</summary>
    internal void RegisterRealmAtRuntime(RealmId id, RealmConfig config)
    {
        lock (_realmLifecycleLock)
        {
            var table = _realms ?? throw new InvalidOperationException("No realm table: configure realms before InitializeArchetypes.");
            if (id.Value >= table.MaxRealms)
            {
                throw new ArgumentOutOfRangeException(nameof(id), id.Value, $"Realm {id.Value} is out of range: this engine holds {table.MaxRealms} realm(s).");
            }

            if (id == RealmId.Default)
            {
                throw new InvalidOperationException("Realm 0 is registered at open (ConfigureSpatialGrid or Realms.Register before InitializeArchetypes).");
            }

            if (table.IsRegistered(id.Value))
            {
                throw new InvalidOperationException($"Realm {id.Value} is already registered.");
            }

            if (_unavailableRealmIds != null && _unavailableRealmIds.Contains(id.Value))
            {
                throw new InvalidOperationException(
                    $"Realm {id.Value} was unregistered this session: its id is reusable only after an open retires it (RLM-06) — a crash before the "
                    + "destroys are checkpointed would otherwise replay the old realm's entities into the new one.");
            }

            config.Validate(id);
            var grid = new SpatialGrid(config.Grid);

            // Every archetype's per-realm table holds the id BEFORE the realm is published: a reader that finds the realm finds room for its state.
            foreach (var state in _archetypeStates)
            {
                state?.ClusterState?.EnsureRealmSpatialCapacity(id.Value + 1);
            }

            // Durable first, published second (review #4): an entity can enter the realm only once its catalog row is on disk, so a crash can never
            // leave committed entities in a realm the next open does not know (RLM-01). A failed write publishes nothing.
            PersistRealmCatalogRows([(id.Value, config.Grid)]);
            table.Register(id, grid, config, MarkRealmCompatibility);
        }
    }

    /// <summary>Unregisters realm <paramref name="id"/>: Closing now, removed at the first fence that finds it empty. See the class remarks.</summary>
    internal void UnregisterRealm(RealmId id)
    {
        lock (_realmLifecycleLock)
        {
            var table = _realms ?? throw new InvalidOperationException("No realm table.");
            var realm = table.Get(id.Value);
            if (realm.Closing)
            {
                return;
            }

            if (id == RealmId.Default || ReferenceEquals(table.Primary, realm))
            {
                throw new InvalidOperationException(
                    $"Realm {id.Value} is the primary realm: its configuration carries every archetype's maintenance budgets, so it cannot be unregistered.");
            }

            // The durable half first: a crash after this line reopens the realm as Closing.
            if (_persistedRealms != null && _persistedRealms.TryGetValue(id.Value, out var persisted))
            {
                var row = persisted.Row;
                row.State = RealmR1.StateClosing;
                WriteRealmCatalogRow(persisted.ChunkId, ref row);
                _persistedRealms[id.Value] = (persisted.ChunkId, row);
            }

            (_unavailableRealmIds ??= []).Add(id.Value);
            table.MarkClosing(id.Value);
        }
    }

    /// <summary>
    /// Destroys every entity in realm <paramref name="id"/> through <paramref name="tx"/> — the ordinary destroy path (EntityMap, indexes, WAL, cascades).
    /// Returns the number destroyed. The realm's clusters are freed when the transaction commits and the fence runs.
    /// </summary>
    internal unsafe int DestroyRealmContents(RealmId id, Transaction tx)
    {
        ArgumentNullException.ThrowIfNull(tx);
        _ = (_realms ?? throw new InvalidOperationException("No realm table.")).Get(id.Value);
        var ids = new List<EntityId>();
        using (EpochGuard.Enter(EpochManager))
        {
            foreach (var state in _archetypeStates)
            {
                var cs = state?.ClusterState;
                var realmMap = cs?.ClusterRealmMap;
                if (realmMap == null || cs.ClusterSegment == null)
                {
                    continue;
                }

                var accessor = cs.ClusterSegment.CreateChunkAccessor();
                try
                {
                    var active = cs.ReadActiveClusterList(out var count);
                    for (var i = 0; i < count; i++)
                    {
                        var chunkId = active[i];
                        if (chunkId >= realmMap.Length || realmMap[chunkId] != id.Value)
                        {
                            continue;
                        }

                        var clusterBase = accessor.GetChunkAddress(chunkId);
                        for (var bits = *(ulong*)clusterBase; bits != 0; bits &= bits - 1)
                        {
                            var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                            ids.Add(EntityId.FromRaw(*(long*)(clusterBase + cs.Layout.EntityIdsOffset + (slot * 8))));
                        }
                    }
                }
                finally
                {
                    accessor.Dispose();
                }
            }
        }

        // Not concurrently with a fence (the fence moves slots): call it from a system or between ticks. An id read from a slot the fence was moving is
        // checked alive before it is destroyed.
        var destroyed = 0;
        foreach (var entity in ids)
        {
            if (tx.IsAlive(entity))
            {
                tx.Destroy(entity);
                destroyed++;
            }
        }

        return destroyed;
    }

    /// <summary>
    /// Removes every Closing realm that holds no cluster: its per-archetype state and its grid go, and its id leaves the table. Fence-serial (EW-01), at
    /// the end of every fence — one comparison when nothing is closing.
    /// </summary>
    internal void RemoveEmptyClosingRealms()
    {
        var table = _realms;
        if (table == null || table.ClosingCount == 0)
        {
            return;
        }

        List<ushort> empty = null;
        foreach (var realm in table.Registered)
        {
            if (realm.Closing && !RealmHoldsClusters(realm.Id.Value))
            {
                (empty ??= []).Add(realm.Id.Value);
            }
        }

        if (empty == null)
        {
            return;
        }

        lock (_realmLifecycleLock)
        {
            foreach (var id in empty)
            {
                foreach (var state in _archetypeStates)
                {
                    state?.ClusterState?.DropRealmSpatial(id);
                }

                table.Remove(id);
            }
        }
    }

    /// <summary>
    /// After recovery at open, resolve every realm that was Closing when the engine last stopped (RLM-06). Closing is never carried across an open:
    /// <list type="bullet">
    /// <item>something still names it — a cluster in it, or ANY entity's [RealmKey] (WAL replay places a replayed spawn by plain claim, possibly in
    /// another realm's cluster, and the first fence moves it home by its key) — or the application registered it at this open: it is live again, its
    /// row back to Live, and the application may unregister it anew;</item>
    /// <item>otherwise it is retired: its row marked Retired, its id free (a registration reuses the row at the next generation).</item>
    /// </list>
    /// Keeping it Closing instead would refuse the key of every replayed entity as an entry and the fence would revert it into the cluster's realm — the
    /// entity resurrected elsewhere (review #4).
    /// </summary>
    private void ResolveClosingRealmsAtOpen(IReadOnlyDictionary<ushort, RealmConfig> registeredAtOpen)
    {
        if (_closingRealmsAtOpen == null || _realms == null)
        {
            return;
        }

        foreach (var id in _closingRealmsAtOpen)
        {
            if (!_realms.IsRegistered(id))
            {
                continue;
            }

            var (chunkId, row) = _persistedRealms[id];
            if ((registeredAtOpen != null && registeredAtOpen.ContainsKey(id)) || RealmHoldsClusters(id) || AnySlotNamesRealm(id))
            {
                row.State = RealmR1.StateLive;
                WriteRealmCatalogRow(chunkId, ref row);
                _persistedRealms[id] = (chunkId, row);
                continue;
            }

            row.State = RealmR1.StateRetired;
            WriteRealmCatalogRow(chunkId, ref row);
            _persistedRealms.Remove(id);
            (_retiredRealmRows ??= [])[id] = (chunkId, row);
            foreach (var state in _archetypeStates)
            {
                state?.ClusterState?.DropRealmSpatial(id);
            }

            _realms.Remove(id);
        }

        _closingRealmsAtOpen = null;
    }

    /// <summary>True when any occupied slot of any realm-keyed archetype names realm <paramref name="id"/> in its key. Open-time only: one pass over the
    /// clusters of every realm-keyed archetype.</summary>
    private unsafe bool AnySlotNamesRealm(ushort id)
    {
        using var epoch = EpochGuard.Enter(EpochManager);
        foreach (var state in _archetypeStates)
        {
            var cs = state?.ClusterState;
            if (cs == null || !cs.SpatialSlot.HasRealmKey || cs.ClusterSegment == null)
            {
                continue;
            }

            var accessor = cs.ClusterSegment.CreateChunkAccessor();
            try
            {
                var active = cs.ReadActiveClusterList(out var count);
                for (var i = 0; i < count; i++)
                {
                    var clusterBase = accessor.GetChunkAddress(active[i]);
                    for (var bits = *(ulong*)clusterBase; bits != 0; bits &= bits - 1)
                    {
                        if (*cs.RealmKeyAt(clusterBase, System.Numerics.BitOperations.TrailingZeroCount(bits)) == id)
                        {
                            return true;
                        }
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }

        return false;
    }

    /// <summary>True when any archetype has an active cluster in realm <paramref name="id"/>.</summary>
    private bool RealmHoldsClusters(ushort id)
    {
        foreach (var state in _archetypeStates)
        {
            var cs = state?.ClusterState;
            var realmMap = cs?.ClusterRealmMap;
            if (realmMap == null)
            {
                continue;
            }

            var active = cs.ReadActiveClusterList(out var count);
            for (var i = 0; i < count; i++)
            {
                var chunkId = active[i];
                if (chunkId < realmMap.Length && realmMap[chunkId] == id)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Records, on a realm about to be published at run time, the realm-keyed archetypes whose spatial field cannot address its world.</summary>
    private void MarkRealmCompatibility(Realm realm)
    {
        foreach (var state in _archetypeStates)
        {
            var cs = state?.ClusterState;
            if (cs == null || !cs.SpatialSlot.HasSpatialIndex || !cs.SpatialSlot.HasRealmKey)
            {
                continue;
            }

            var fieldType = cs.SpatialSlot.FieldInfo.FieldType;
            if (SpatialGrid.IsWorldExtentAddressable(fieldType, in realm.GridConfig))
            {
                continue;
            }

            var name = ArchetypeRegistry.GetMetadata((ushort)cs.ArchetypeId)?.ArchetypeType?.Name ?? cs.ArchetypeId.ToString();
            try
            {
                SpatialGrid.ValidateWorldExtentForFieldType(fieldType, in realm.GridConfig, name);
            }
            catch (InvalidOperationException e)
            {
                realm.MarkIncompatible(cs.ArchetypeId, e.Message);
            }
        }
    }

    /// <summary>Rewrites one catalog row in place, synchronously: the pages are written and synced before this returns.</summary>
    private void WriteRealmCatalogRow(int chunkId, ref RealmR1 row)
    {
        var cs = MMF.CreateChangeSet();
        SystemCrud.Update(_realmsTable, chunkId, ref row, EpochManager, cs);
        cs.SaveChanges();
    }
}
