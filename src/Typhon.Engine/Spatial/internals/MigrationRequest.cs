using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// A queued migration request: move the entity currently living at
/// <c>(SourceClusterChunkId, SourceSlotIndex)</c> into a cluster attached to <see cref="DestCellKey"/>.
/// </summary>
/// <remarks>
/// <para>Populated during cell-crossing detection inside <c>DatabaseEngine.DetectClusterMigrations</c>,
/// drained by <c>ArchetypeClusterState.ExecuteMigrations</c> at the tick fence.</para>
/// <para>Packed to 12 bytes so the per-archetype queue stays cache-friendly — a 1K-entry queue fits in 12KB
/// (under 3 L1 lines per 16 entries). Issue #229 Phase 3.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct MigrationRequest
{
    /// <summary>Cluster chunk id of the entity's current (pre-migration) slot.</summary>
    public readonly int SourceClusterChunkId;

    /// <summary>Slot index within <see cref="SourceClusterChunkId"/>.</summary>
    public readonly int SourceSlotIndex;

    /// <summary>Target cell key the entity should land in after migration. Resolved to a concrete
    /// destination cluster by <c>ClaimSlotInCell</c> during execution.</summary>
    public readonly int DestCellKey;

    public MigrationRequest(int sourceClusterChunkId, int sourceSlotIndex, int destCellKey)
    {
        SourceClusterChunkId = sourceClusterChunkId;
        SourceSlotIndex = sourceSlotIndex;
        DestCellKey = destCellKey;
    }
}
