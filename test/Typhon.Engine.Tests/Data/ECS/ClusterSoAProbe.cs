using System.Numerics;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// Reads or overwrites one entity's bit in its cluster's per-component SoA <c>EnabledBits</c> word, bypassing the EntityMap record.
/// </summary>
/// <remarks>
/// A cluster entity holds its enabled state twice: the record's 16-bit mask, which point reads (<c>Open</c> / <c>IsEnabled</c>) consume, and one bit per
/// component in the cluster, which bulk iteration and the crash rebuild consume. A point read cannot observe the second copy, so a test that has to see it
/// goes through here (#847). The entity is located by the EntityId the cluster stores in its own slot, not by the record's location. The cluster metadata
/// lives in the PersistentStore segment, or in the TransientStore one for a pure-Transient archetype, and both are walked the same way.
/// </remarks>
internal static unsafe class ClusterSoAProbe
{
    private enum Op { Read, Set, Clear, Locate }

    public static bool IsEnabled(DatabaseEngine dbe, ushort archetypeId, EntityId id, int componentSlot) =>
        Access(dbe, archetypeId, id, componentSlot, Op.Read, out _);

    public static void SetEnabled(DatabaseEngine dbe, ushort archetypeId, EntityId id, int componentSlot, bool enabled) =>
        Access(dbe, archetypeId, id, componentSlot, enabled ? Op.Set : Op.Clear, out _);

    /// <summary>The (cluster chunk id, slot index) the entity occupies, found through the ids the clusters store.</summary>
    public static (int ChunkId, int Slot) Locate(DatabaseEngine dbe, ushort archetypeId, EntityId id)
    {
        Access(dbe, archetypeId, id, 0, Op.Locate, out var location);
        return location;
    }

    /// <summary>Sets or clears one bit at a raw (cluster, slot) location, occupied or not — for reproducing a stray bit on a freed slot.</summary>
    public static void SetBitAt(DatabaseEngine dbe, ushort archetypeId, (int ChunkId, int Slot) location, int componentSlot, bool enabled)
    {
        var cs = dbe._archetypeStates[archetypeId].ClusterState;
        Assert.That(cs.ClusterSegment, Is.Not.Null, "SetBitAt addresses the PersistentStore cluster segment");
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            ref var word = ref *(ulong*)(accessor.GetChunkAddress(location.ChunkId, true) + cs.Layout.EnabledBitsOffset(componentSlot));
            word = enabled ? word | (1UL << location.Slot) : word & ~(1UL << location.Slot);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>The <c>EnabledBits</c> of the entity's raw EntityMap record — tombstones included, no MVCC resolution.</summary>
    public static ushort RecordEnabledBits(DatabaseEngine dbe, ushort archetypeId, EntityId id)
    {
        var state = dbe._archetypeStates[archetypeId];
        var buffer = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = state.EntityMap.Segment.CreateChunkAccessor();
        try
        {
            if (!state.EntityMap.TryGet(id.EntityKey, buffer, ref accessor))
            {
                Assert.Fail($"entity {id} has no EntityMap record");
            }

            return EntityRecordAccessor.GetHeader(buffer).EnabledBits;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private static bool Access(DatabaseEngine dbe, ushort archetypeId, EntityId id, int componentSlot, Op op, out (int ChunkId, int Slot) location)
    {
        var cs = dbe._archetypeStates[archetypeId].ClusterState;
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var found = cs.ClusterSegment != null
            ? Walk(cs.ClusterSegment, cs, id, componentSlot, op, out var result, out location)
            : Walk(cs.TransientSegment, cs, id, componentSlot, op, out result, out location);
        if (!found)
        {
            Assert.Fail($"entity {id} occupies no cluster slot of archetype {archetypeId}");
        }

        return result;
    }

    private static bool Walk<TStore>(ChunkBasedSegment<TStore> segment, ArchetypeClusterState cs, EntityId id, int componentSlot, Op op, out bool result,
        out (int ChunkId, int Slot) location) where TStore : struct, IPageStore
    {
        var accessor = segment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var chunkId = cs.ActiveClusterIds[i];
                var clusterBase = accessor.GetChunkAddress(chunkId, op is Op.Set or Op.Clear);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (*(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8) != (long)id.RawValue)
                    {
                        continue;
                    }

                    location = (chunkId, slot);
                    ref var word = ref *(ulong*)(clusterBase + cs.Layout.EnabledBitsOffset(componentSlot));
                    if (op == Op.Set)
                    {
                        word |= 1UL << slot;
                    }
                    else if (op == Op.Clear)
                    {
                        word &= ~(1UL << slot);
                    }

                    result = (word & (1UL << slot)) != 0;
                    return true;
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        result = false;
        location = (-1, -1);
        return false;
    }
}
