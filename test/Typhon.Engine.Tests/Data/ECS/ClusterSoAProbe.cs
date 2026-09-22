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
/// goes through here (#847). The entity is located by the EntityId the cluster stores in its own slot, not by the record's location.
/// </remarks>
internal static unsafe class ClusterSoAProbe
{
    public static bool IsEnabled(DatabaseEngine dbe, ushort archetypeId, EntityId id, int componentSlot) => Access(dbe, archetypeId, id, componentSlot, null);

    public static void SetEnabled(DatabaseEngine dbe, ushort archetypeId, EntityId id, int componentSlot, bool enabled) =>
        Access(dbe, archetypeId, id, componentSlot, enabled);

    private static bool Access(DatabaseEngine dbe, ushort archetypeId, EntityId id, int componentSlot, bool? write)
    {
        var cs = dbe._archetypeStates[archetypeId].ClusterState;
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var clusterBase = accessor.GetChunkAddress(cs.ActiveClusterIds[i], write.HasValue);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (*(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8) != (long)id.RawValue)
                    {
                        continue;
                    }

                    ref var word = ref *(ulong*)(clusterBase + cs.Layout.EnabledBitsOffset(componentSlot));
                    if (write.HasValue)
                    {
                        word = write.Value ? word | (1UL << slot) : word & ~(1UL << slot);
                    }

                    return (word & (1UL << slot)) != 0;
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        Assert.Fail($"entity {id} occupies no cluster slot of archetype {archetypeId}");
        return false;
    }
}
