using System;
using System.Collections.Generic;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// Reads all enabled components of an entity as raw byte arrays for serialization.
/// Uses the Transaction's MVCC-resolved EntityRef for correct versioned component visibility.
/// </summary>
internal static unsafe class EntitySnapshotReader
{
    /// <summary>
    /// Read all enabled components of an entity, returning a <see cref="ComponentSnapshot"/> for each.
    /// </summary>
    /// <param name="tx">Read-only Transaction for MVCC-correct component access.</param>
    /// <param name="entityId">The entity to read.</param>
    /// <param name="snapshots">Output list to append snapshots to (avoids allocation per call).</param>
    internal static void ReadAllComponents(Transaction tx, EntityId entityId, List<ComponentSnapshot> snapshots)
    {
        var entity = tx.Open(entityId);
        if (!entity.IsValid)
        {
            return;
        }

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

            var data = ReadComponentRawBytes(table, chunkId);
            if (data == null)
            {
                continue;
            }

            snapshots.Add(new ComponentSnapshot
            {
                ComponentId = (ushort)archetype._componentTypeIds[slot],
                Data = data
            });
        }
    }

    /// <summary>
    /// Read the raw bytes of a specific component on an entity.
    /// </summary>
    /// <param name="tx">Read-only Transaction.</param>
    /// <param name="entityId">The entity.</param>
    /// <param name="table">The ComponentTable for the component type.</param>
    /// <returns>Raw component bytes, or empty if entity is invalid or component disabled.</returns>
    internal static byte[] ReadComponent(Transaction tx, EntityId entityId, ComponentTable table)
    {
        var entity = tx.Open(entityId);
        if (!entity.IsValid)
        {
            return [];
        }

        var typeId = ArchetypeRegistry.GetComponentTypeId(table.Definition.POCOType);
        if (typeId < 0 || !entity._archetype.TryGetSlot(typeId, out var slot) || !entity.IsEnabled(slot))
        {
            return [];
        }

        var chunkId = entity.GetLocation(slot);
        return ReadComponentRawBytes(table, chunkId) ?? [];
    }

    /// <summary>
    /// Read raw component bytes from a chunk address. Returns a copied byte[] so the caller is not coupled to the ChunkAccessor's lifetime. Returns null if
    /// the component has zero size.
    /// </summary>
    private static byte[] ReadComponentRawBytes(ComponentTable table, int chunkId)
    {
        var componentSize = table.ComponentStorageSize;
        if (componentSize == 0)
        {
            return null;
        }

        var overhead = table.ComponentOverhead;
        var result = new byte[componentSize];

        if (table.StorageMode == Schema.Definition.StorageMode.Transient)
        {
            var accessor = table.TransientComponentSegment.CreateChunkAccessor();
            try
            {
                var ptr = accessor.GetChunkAddress(chunkId);
                new ReadOnlySpan<byte>(ptr + overhead, componentSize).CopyTo(result);
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
                var ptr = accessor.GetChunkAddress(chunkId);
                new ReadOnlySpan<byte>(ptr + overhead, componentSize).CopyTo(result);
            }
            finally
            {
                accessor.Dispose();
            }
        }

        return result;
    }
}
