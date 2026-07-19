using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Metadata for a registered archetype: identity, component slots, parent-child graph, and per-archetype entity storage.
/// Populated during static initialization, immutable after <see cref="ArchetypeRegistry.Freeze"/>.
/// </summary>
internal class ArchetypeMetadata
{
    /// <summary>Globally unique archetype ID from [Archetype] attribute. Embedded in EntityId.</summary>
    public ushort ArchetypeId;

    /// <summary>Schema revision from [Archetype(Revision)] attribute.</summary>
    public int Revision;

    /// <summary>Total component count (own + inherited). Max 16.</summary>
    public byte ComponentCount;

    /// <summary>Parent archetype ID. <see cref="NoParent"/> (0xFFFF) for root archetypes.</summary>
    public ushort ParentArchetypeId = NoParent;

    /// <summary>Sentinel value indicating no parent archetype (root). Outside the valid 12-bit ArchetypeId range.</summary>
    public const ushort NoParent = 0xFFFF;

    /// <summary>Direct children (mutable during registration, frozen after).</summary>
    public readonly List<ushort> ChildArchetypeIds = [];

    /// <summary>Self + all descendants (populated during Freeze).</summary>
    public ushort[] SubtreeArchetypeIds;

    /// <summary>CLR type of the archetype class.</summary>
    public Type ArchetypeType;

    /// <summary>
    /// Durable schema name — the stable per-DB identity persisted in <c>ArchetypeR1.Name</c> and matched on reopen to restore this archetype's routing id.
    /// From <c>[Archetype(Name = ...)]</c>; defaults to the CLR type's simple name. This — not the CLR type name — is the durability match key (#514 D4).
    /// </summary>
    public string Name;

    /// <summary>Former durable <see cref="Name"/> (from <c>[Archetype(PreviousName = ...)]</c>); matched on reopen after <see cref="Name"/> so a renamed archetype
    /// keeps its routing id. Null when never renamed.</summary>
    public string PreviousName;

    /// <summary>Optional human-readable label from <c>[Archetype(Alias = ...)]</c>; null when unset (callers fall back to the type name).</summary>
    public string Alias;

    // ═══════════════════════════════════════════════════════════════════════
    // Slot mapping
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>[slotIndex] → ComponentTypeId. Length == ComponentCount.</summary>
    internal int[] _componentTypeIds;

    /// <summary>ComponentTypeId → slotIndex (flat array, 0xFF = not present). Replaces Dictionary for O(1) array-indexed lookup.</summary>
    internal byte[] _typeIdToSlot;

    // ═══════════════════════════════════════════════════════════════════════
    // Schema-level component type mapping (immutable after static registration)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>[slotIndex] → CLR Type of the component at this slot. Length == ComponentCount.</summary>
    internal Type[] _slotToComponentType;

    /// <summary>Cached entity record size: 14 + ComponentCount * 4 bytes (legacy), or 19 bytes (cluster).</summary>
    internal int _entityRecordSize;

    // ═══════════════════════════════════════════════════════════════════════
    // Cluster storage (set during DatabaseEngine.InitializeArchetypes)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>True if this archetype uses cluster storage (all SV, no non-Dynamic spatial).</summary>
    internal bool IsClusterEligible;

    /// <summary>True if cluster-eligible AND at least one component has indexed fields.
    /// Gates per-archetype B+Tree creation and shadow capture in the cluster write path.</summary>
    internal bool HasClusterIndexes;

    /// <summary>True if cluster-eligible AND has a dynamic spatial-indexed component.
    /// Gates per-archetype R-Tree creation and cluster spatial maintenance.</summary>
    internal bool HasClusterSpatial;

    /// <summary>Precomputed cluster layout. Non-null only when <see cref="IsClusterEligible"/> is true.</summary>
    internal ArchetypeClusterInfo ClusterLayout;

    /// <summary>Bitmask of component slots that use Versioned storage mode. Bit N set = slot N is Versioned.</summary>
    internal ushort VersionedSlotMask;

    /// <summary>Number of Versioned component slots (PopCount of <see cref="VersionedSlotMask"/>).</summary>
    internal byte VersionedSlotCount;

    /// <summary>Bitmask of component slots that use Transient storage mode. Bit N set = slot N is Transient.</summary>
    internal ushort TransientSlotMask;

    /// <summary>Number of Transient component slots (PopCount of <see cref="TransientSlotMask"/>).</summary>
    internal byte TransientSlotCount;

    // ═══════════════════════════════════════════════════════════════════════
    // Cascade delete graph (populated during Freeze)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Children that should be cascade-deleted when an entity of this archetype is destroyed.
    /// Null or empty if no cascade targets. Populated during <see cref="ArchetypeRegistry.Freeze"/>.
    /// </summary>
    internal List<CascadeTarget> _cascadeTargets;

    /// <summary>Get the slot index for a component type ID. Throws if the component is not part of this archetype.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetSlot(int componentTypeId)
    {
        var arr = _typeIdToSlot;
        if ((uint)componentTypeId >= (uint)arr.Length || arr[componentTypeId] == 0xFF)
        {
            ThrowComponentNotInArchetype(componentTypeId);
        }
        return arr[componentTypeId];
    }

    /// <summary>Try to get the slot index for a component type ID. Returns false if not found.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetSlot(int componentTypeId, out byte slot)
    {
        var arr = _typeIdToSlot;
        if ((uint)componentTypeId < (uint)arr.Length)
        {
            slot = arr[componentTypeId];
            return slot != 0xFF;
        }
        slot = 0;
        return false;
    }

    /// <summary>Check whether this archetype has a component with the given type ID.</summary>
    public bool HasComponent(int componentTypeId)
    {
        var arr = _typeIdToSlot;
        return (uint)componentTypeId < (uint)arr.Length && arr[componentTypeId] != 0xFF;
    }

    /// <summary>Enumerate the CLR types of components in this archetype. Used by external tooling (Workbench Schema
    /// Inspector) to present the archetype's component set without reaching into internals.</summary>
    public IEnumerable<Type> GetComponentTypes()
    {
        var arr = _slotToComponentType;
        if (arr == null)
        {
            yield break;
        }

        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i] != null)
            {
                yield return arr[i];
            }
        }
    }

    /// <summary>True if this archetype uses cluster storage (fixed-size SoA chunks). False for legacy
    /// per-ComponentTable segment storage.</summary>
    public bool UsesClusterStorage => IsClusterEligible;

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void ThrowComponentNotInArchetype(int componentTypeId) =>
        throw new InvalidOperationException(
            $"Component type ID {componentTypeId} is not part of archetype '{ArchetypeType?.Name}' (Id={ArchetypeId}). " +
            $"Ensure you are using a Comp<T> handle declared on this archetype.");
}

/// <summary>
/// Per-engine archetype runtime state. Each <see cref="DatabaseEngine"/> instance owns its own array of these, indexed
/// by <see cref="ArchetypeMetadata.ArchetypeId"/>. This separates per-engine mutable data (entity storage, key counters, ComponentTable bindings) from the
/// globally-shared immutable schema in <see cref="ArchetypeMetadata"/>.
/// </summary>
internal class ArchetypeEngineState
{
    /// <summary>[slotIndex] → ComponentTable that stores this component type for THIS engine. Length == ComponentCount.</summary>
    public ComponentTable[] SlotToComponentTable;

    /// <summary>Per-archetype HashMap storing EntityRecords keyed by EntityKey (long). Backed by THIS engine's MMF.</summary>
    public RawValuePagedHashMap<long, PersistentStore> EntityMap;

    /// <summary>Monotonic entity key counter. Use Interlocked.Increment for thread-safe generation.</summary>
    public long NextEntityKey;

    /// <summary>Cluster storage state. Non-null for cluster-eligible archetypes.</summary>
    public ArchetypeClusterState ClusterState;
}

/// <summary>
/// Describes a cascade delete edge: when a parent entity is destroyed, find and destroy children in the specified child archetype via the FK index.
/// </summary>
internal struct CascadeTarget
{
    /// <summary>Archetype ID of the child that should be cascade-deleted.</summary>
    public ushort ChildArchetypeId;

    /// <summary>CLR Type of the child archetype (for logging).</summary>
    public Type ChildArchetypeType;

    /// <summary>Slot index of the component containing the FK field on the child archetype.</summary>
    public byte FkSlotIndex;

    /// <summary>Byte offset of the EntityLink&lt;T&gt; field within the component struct (from Marshal.OffsetOf).</summary>
    public int FkFieldOffset;
}
