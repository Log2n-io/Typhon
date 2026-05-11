using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// Builds wire-format delta messages from View deltas. For each published View, converts the ViewBase delta (Added/Removed/Modified entity sets)
/// into <see cref="ViewDeltaMessage"/> with component data.
/// </summary>
/// <remarks>
/// <para>Modified detection uses two sources:</para>
/// <list type="number">
/// <item>View ring buffer → entities with indexed field changes (already in ViewDelta.Modified)</item>
/// <item>PreviousTickDirtyBitmap → entities with any component changes (supplements ring buffer)</item>
/// </list>
/// </remarks>
internal sealed class DeltaBuilder
{
    // Reusable buffers to avoid per-tick allocation
    private readonly List<ComponentSnapshot> _snapshotBuffer = new(16);
    private readonly List<ComponentFieldUpdate> _updateBuffer = new(8);
    private readonly List<EntityUpdate> _modifiedUpdates = new(64);
    private readonly HashSet<long> _modifiedSupplementSet = new();

    /// <summary>
    /// Build a <see cref="ViewDeltaMessage"/> for a shared published View.
    /// </summary>
    internal ViewDeltaMessage? BuildSharedViewDelta(PublishedView publishedView, Transaction tx, DatabaseEngine engine)
    {
        var view = publishedView.SharedView;

        // Refresh the View (drains ring buffer, builds Added/Removed/Modified sets) — engine-internal, no user execution site
        view.RefreshFromScheduler(tx);
        return BuildFromRefreshedView(publishedView.PublishedId, view, tx, engine);
    }

    /// <summary>
    /// Build a <see cref="ViewDeltaMessage"/> from a View that has ALREADY been refreshed.
    /// Used when View refresh must happen before subscription transitions (so BeginSync captures fresh entity sets).
    /// </summary>
    internal ViewDeltaMessage? BuildFromRefreshedView(ushort publishedId, ViewBase view, Transaction tx, DatabaseEngine engine)
    {
        var delta = view.GetDelta();
        SupplementModifiedFromDirtyBitmap(view, engine);

        var hasChanges = delta.Added.Count > 0 || delta.Removed.Count > 0 || delta.Modified.Count > 0 || _modifiedSupplementSet.Count > 0;
        if (!hasChanges)
        {
            view.ClearDelta();
            _modifiedSupplementSet.Clear();
            return null;
        }

        var message = BuildMessage(publishedId, delta, tx, engine);
        view.ClearDelta();
        _modifiedSupplementSet.Clear();
        return message;
    }

    /// <summary>
    /// Build a <see cref="ViewDeltaMessage"/> for a per-client View.
    /// </summary>
    internal ViewDeltaMessage? BuildPerClientViewDelta(PublishedView publishedView, ViewBase clientView, Transaction tx, DatabaseEngine engine)
    {
        clientView.RefreshFromScheduler(tx);
        var delta = clientView.GetDelta();

        SupplementModifiedFromDirtyBitmap(clientView, engine);

        var hasChanges = delta.Added.Count > 0 || delta.Removed.Count > 0 || delta.Modified.Count > 0 || _modifiedSupplementSet.Count > 0;
        if (!hasChanges)
        {
            clientView.ClearDelta();
            _modifiedSupplementSet.Clear();
            return null;
        }

        var message = BuildMessage(publishedView.PublishedId, delta, tx, engine);
        clientView.ClearDelta();
        _modifiedSupplementSet.Clear();
        return message;
    }

    private ViewDeltaMessage BuildMessage(ushort viewId, ViewDelta delta, Transaction tx, DatabaseEngine engine)
    {
        // Build Added entities (full component snapshots)
        EntityDelta[] added = null;
        if (delta.Added.Count > 0)
        {
            added = new EntityDelta[delta.Added.Count];
            var idx = 0;
            foreach (var pk in delta.Added)
            {
                _snapshotBuffer.Clear();
                EntitySnapshotReader.ReadAllComponents(tx, EntityId.FromRaw(pk), _snapshotBuffer);

                var components = ArrayPool<ComponentSnapshot>.Shared.Rent(_snapshotBuffer.Count);
                _snapshotBuffer.CopyTo(components);
                var final = new ComponentSnapshot[_snapshotBuffer.Count];
                Array.Copy(components, final, _snapshotBuffer.Count);
                ArrayPool<ComponentSnapshot>.Shared.Return(components);

                added[idx++] = new EntityDelta
                {
                    Id = pk,
                    Components = final
                };
            }
        }

        // Build Removed entities (just IDs)
        long[] removed = null;
        if (delta.Removed.Count > 0)
        {
            removed = new long[delta.Removed.Count];
            var idx = 0;
            foreach (var pk in delta.Removed)
            {
                removed[idx++] = pk;
            }
        }

        // Build Modified entities (only dirty components)
        // Merge View ring buffer Modified set + DirtyBitmap supplement
        _modifiedUpdates.Clear();

        // From ring buffer Modified (indexed field changes)
        foreach (var pk in delta.Modified)
        {
            var update = BuildEntityUpdate(tx, pk);
            if (update.HasValue)
            {
                _modifiedUpdates.Add(update.Value);
            }
        }

        // From DirtyBitmap supplement (non-indexed field changes, not already in Modified)
        foreach (var pk in _modifiedSupplementSet)
        {
            if (!delta.Modified.Contains(pk))
            {
                var update = BuildEntityUpdate(tx, pk);
                if (update.HasValue)
                {
                    _modifiedUpdates.Add(update.Value);
                }
            }
        }

        EntityUpdate[] modified = _modifiedUpdates.Count > 0 ? _modifiedUpdates.ToArray() : null;

        return new ViewDeltaMessage
        {
            ViewId = viewId,
            Added = added,
            Removed = removed,
            Modified = modified
        };
    }

    /// <summary>
    /// Build an EntityUpdate for a Modified entity. Only includes components whose chunks are actually dirty in this tick's DirtyBitmap
    /// (per-component filtering). v1: sends full component bytes (FieldDirtyBits=~0UL).
    /// </summary>
    private EntityUpdate? BuildEntityUpdate(Transaction tx, long pk)
    {
        var entityId = EntityId.FromRaw(pk);
        var entity = tx.Open(entityId);
        if (!entity.IsValid)
        {
            return null;
        }

        _updateBuffer.Clear();
        var archetype = entity._archetype;
        var engineState = entity._engineState;

        for (byte slot = 0; slot < archetype.ComponentCount; slot++)
        {
            if (!entity.IsEnabled(slot))
            {
                continue;
            }

            var table = engineState.SlotToComponentTable[slot];
            var chunkId = entity.GetLocation(slot);

            // Per-component filtering: only include components whose chunk is dirty this tick
            if (table.StorageMode != Schema.Definition.StorageMode.Versioned)
            {
                var bitmap = table.PreviousTickDirtyBitmap;
                if (bitmap != null)
                {
                    var wordIdx = chunkId / 64;
                    var bitIdx = chunkId % 64;
                    if (wordIdx >= bitmap.Length || (bitmap[wordIdx] & (1L << bitIdx)) == 0)
                    {
                        continue; // This component's chunk was not dirty — skip
                    }
                }
            }
            // Versioned: no DirtyBitmap — conservatively include

            var data = EntitySnapshotReader.ReadComponent(tx, entityId, table);
            if (data.Length == 0)
            {
                continue;
            }

            _updateBuffer.Add(new ComponentFieldUpdate
            {
                ComponentId = (ushort)archetype._componentTypeIds[slot],
                FieldDirtyBits = ~0UL, // v1: all fields dirty
                FieldValues = data
            });
        }

        if (_updateBuffer.Count == 0)
        {
            return null;
        }

        return new EntityUpdate
        {
            Id = pk,
            ChangedComponents = _updateBuffer.ToArray()
        };
    }

    /// <summary>
    /// Supplement the View's Modified set with entities that have dirty components (from PreviousTickDirtyBitmap) but whose changes weren't captured by the
    /// ring buffer (non-indexed fields).
    /// All SV/Transient components store entityPK at chunk offset 0 (EntityPKOverheadSize == 8).
    /// </summary>
    private void SupplementModifiedFromDirtyBitmap(ViewBase view, DatabaseEngine engine)
    {
        _modifiedSupplementSet.Clear();

        var delta = view.GetDelta();

        foreach (var table in engine.GetAllComponentTables())
        {
            if (!table.PreviousTickHadDirtyEntities)
            {
                continue;
            }

            var bitmap = table.PreviousTickDirtyBitmap;
            if (bitmap == null)
            {
                continue;
            }

            // Only SV/Transient have DirtyBitmap and store entityPK at offset 0
            if (table.StorageMode == Schema.Definition.StorageMode.Versioned)
            {
                continue;
            }

            // Verify this table stores entityPK in overhead (should always be true for non-Versioned)
            if (table.Definition.EntityPKOverheadSize == 0)
            {
                continue;
            }

            // Use correct accessor based on storage mode
            if (table.StorageMode == Schema.Definition.StorageMode.Transient)
            {
                var accessor = table.TransientComponentSegment.CreateChunkAccessor();
                try
                {
                    ScanDirtyBitmap(bitmap, table, ref accessor, view, delta);
                }
                finally
                {
                    accessor.Dispose();
                }
            }
            else
            {
                var accessor = table.ComponentSegment.CreateChunkAccessor();
                try
                {
                    ScanDirtyBitmap(bitmap, table, ref accessor, view, delta);
                }
                finally
                {
                    accessor.Dispose();
                }
            }
        }
    }

    /// <summary>Iterate dirty bitmap, resolve entityPK, add to supplement set if entity is in View.</summary>
    private unsafe void ScanDirtyBitmap<TStore>(long[] bitmap, ComponentTable table, ref ChunkAccessor<TStore> accessor, ViewBase view, ViewDelta delta)
        where TStore : struct, IPageStore
    {
        for (var wordIdx = 0; wordIdx < bitmap.Length; wordIdx++)
        {
            var word = bitmap[wordIdx];
            while (word != 0)
            {
                var bit = BitOperations.TrailingZeroCount((ulong)word);
                var chunkId = wordIdx * 64 + bit;
                word &= word - 1;

                if (table.IsChunkDestroyed(chunkId))
                {
                    continue;
                }

                var chunkPtr = accessor.GetChunkAddress(chunkId);
                var entityPK = *(long*)chunkPtr;

                if (view.Contains(entityPK) && !delta.Added.Contains(entityPK))
                {
                    _modifiedSupplementSet.Add(entityPK);
                }
            }
        }
    }
}
