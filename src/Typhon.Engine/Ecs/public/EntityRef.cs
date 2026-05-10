using System.Diagnostics;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Zero-copy entity accessor. A ref struct (~96 bytes) that copies the EntityRecord from the per-archetype LinearHash and provides typed component access
/// via cached Location ChunkIds.
/// </summary>
/// <remarks>
/// <para>Created by <see cref="EntityAccessor.Open"/> or <see cref="EntityAccessor.OpenMut"/>. Must not outlive the creating accessor.</para>
/// <para>Read/Write operations delegate to the EntityAccessor for chunk accessor management.</para>
/// </remarks>
[PublicAPI]
public unsafe ref struct EntityRef
{
    internal readonly EntityId _id;
    internal readonly ArchetypeMetadata _archetype;
    internal readonly ArchetypeEngineState _engineState;
    internal readonly EntityAccessor _accessor;
    internal ushort _enabledBits;
    internal readonly bool _writable;
    private fixed int _locations[16];

    // ── Cluster storage fields (non-null when entity uses cluster storage) ──
    internal byte* _clusterBase;                    // Pointer to primary cluster chunk data; null = legacy path
    internal byte* _transientClusterBase;           // Pointer to TransientStore cluster base; null = no Transient segment (or pure-T where _clusterBase is TS)
    internal byte _clusterSlotIndex;                // Slot within cluster (0..63)
    internal int _clusterChunkId;                   // Cluster chunk ID (for dirty tracking: entityIndex = chunkId * 64 + slot)
    internal ArchetypeClusterInfo _clusterLayout;   // Layout info for offset computation

    internal EntityRef(EntityId id, ArchetypeMetadata archetype, ArchetypeEngineState engineState, EntityAccessor accessor, ushort enabledBits, bool writable)
    {
        _id = id;
        _archetype = archetype;
        _engineState = engineState;
        _accessor = accessor;
        _enabledBits = enabledBits;
        _writable = writable;
    }

    /// <summary>Copy locations from a raw EntityRecord byte pointer into this ref struct.</summary>
    internal void CopyLocationsFrom(byte* recordPtr, int componentCount)
    {
        for (int i = 0; i < componentCount; i++)
        {
            _locations[i] = EntityRecordAccessor.GetLocation(recordPtr, i);
        }
    }

    /// <summary>Read the chunkId at a specific slot.</summary>
    internal int GetLocation(int slot) => _locations[slot];

    /// <summary>Override the chunkId at a specific slot. Used by ResolveEntity for MVCC revision chain resolution.</summary>
    internal void SetLocation(int slot, int chunkId) => _locations[slot] = chunkId;

    /// <summary>Copy locations from a managed byte array.</summary>
    internal void CopyLocationsFrom(byte[] recordBytes, int componentCount)
    {
        fixed (byte* ptr = recordBytes)
        {
            CopyLocationsFrom(ptr, componentCount);
        }
    }

    /// <summary>Copy locations from an inline EntityLocations struct (zero-allocation foreach path).</summary>
    internal void CopyLocationsFrom(in EntityLocations locs, int componentCount)
    {
        for (int i = 0; i < componentCount; i++)
        {
            _locations[i] = locs.Values[i];
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>The entity's unique identifier.</summary>
    public EntityId Id => _id;

    /// <summary>The archetype ID of this entity.</summary>
    public ushort ArchetypeId => _id.ArchetypeId;

    /// <summary>True if this EntityRef refers to a valid entity.</summary>
    public bool IsValid => !_id.IsNull;

    /// <summary>True if this EntityRef allows writes.</summary>
    public bool IsWritable => _writable;

    // ═══════════════════════════════════════════════════════════════════════
    // Component access — by handle (O(1), preferred)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Read a component by handle. Zero-copy — returns a ref into the chunk page (or cluster slot).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T Read<T>(Comp<T> comp) where T : unmanaged
    {
        byte slot = _archetype.GetSlot(comp._componentTypeId);
        Debug.Assert(slot < _archetype.ComponentCount, $"Slot {slot} out of range for archetype with {_archetype.ComponentCount} components");
        Debug.Assert((_enabledBits & (1 << slot)) != 0, $"Component at slot {slot} is disabled");

        if (_clusterBase != null)
        {
            // Transient slots read from TransientStore cluster segment (mixed archetypes only; for pure-T, _clusterBase IS the TS base)
            if (_transientClusterBase != null && (_archetype.TransientSlotMask & (1 << slot)) != 0)
            {
                return ref Unsafe.AsRef<T>(_transientClusterBase + _clusterLayout.ComponentOffset(slot) + _clusterSlotIndex * _clusterLayout.ComponentSize(slot));
            }
            // Versioned slots read from content chunk (_locations populated by chain walk), not cluster slot.
            // Cluster slot is the HEAD cache — used by bulk iteration only. MVCC-correct reads use content chunk.
            if ((_archetype.VersionedSlotMask & (1 << slot)) != 0)
            {
                int chunkId = _locations[slot];
                var table = _engineState.SlotToComponentTable[slot];
                return ref _accessor.ReadEcsComponentData<T>(table, chunkId);
            }
            return ref Unsafe.AsRef<T>(_clusterBase + _clusterLayout.ComponentOffset(slot) + _clusterSlotIndex * _clusterLayout.ComponentSize(slot));
        }

        int chunkId2 = _locations[slot];
        var table2 = _engineState.SlotToComponentTable[slot];
        return ref _accessor.ReadEcsComponentData<T>(table2, chunkId2);
    }

    /// <summary>Write a component by handle. Returns a mutable ref into the chunk page (or cluster slot).
    /// For Versioned: copy-on-write (allocates new chunk, preserves old for concurrent readers).
    /// For SingleVersion with indexes: shadows old field values on first write per tick for deferred index maintenance.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Write<T>(Comp<T> comp) where T : unmanaged
    {
        Debug.Assert(_writable, "EntityRef opened as read-only — use OpenMut for writes");
        SystemAccessValidator.AssertWrite<T>();
        byte slot = _archetype.GetSlot(comp._componentTypeId);
        Debug.Assert(slot < _archetype.ComponentCount);
        Debug.Assert((_enabledBits & (1 << slot)) != 0, $"Component at slot {slot} is disabled");

        if (_clusterBase != null)
        {
            // Versioned cluster: COW path (same as legacy Versioned — cluster slot updated at commit)
            if ((_archetype.VersionedSlotMask & (1 << slot)) != 0)
            {
                var table = _engineState.SlotToComponentTable[slot];
                var (newChunkId, rawPtr) = _accessor.EcsVersionedCopyOnWrite(typeof(T), _id, table);
                _locations[slot] = newChunkId;
                return ref Unsafe.AsRef<T>((byte*)rawPtr + table.ComponentOverhead);
            }

            var clusterState = _engineState.ClusterState;

            // Transient cluster: in-place write to TransientStore segment (no COW, no revision chain)
            if (_transientClusterBase != null && (_archetype.TransientSlotMask & (1 << slot)) != 0)
            {
                // Shadow capture for SV indexed fields (first write per entity per tick — captures SV fields, skips T and V)
                if (clusterState.IndexSlots != null)
                {
                    int entityIndex = _clusterChunkId * 64 + _clusterSlotIndex;
                    if (!clusterState.ClusterShadowBitmap.TestAndSet(entityIndex))
                    {
                        ShadowClusterIndexedFields(clusterState);
                    }
                }
                clusterState.SetDirty(_clusterChunkId, _clusterSlotIndex);
                return ref Unsafe.AsRef<T>(_transientClusterBase + _clusterLayout.ComponentOffset(slot) + _clusterSlotIndex * _clusterLayout.ComponentSize(slot));
            }

            // SV cluster fast path: direct pointer arithmetic into SoA array.
            // Page was already marked dirty at resolve time (OpenMut → GetChunkAddress(dirty:true)).
            // Shadow capture for per-archetype B+Tree index maintenance (first write per entity per tick)
            if (clusterState.IndexSlots != null)
            {
                int entityIndex = _clusterChunkId * 64 + _clusterSlotIndex;
                if (!clusterState.ClusterShadowBitmap.TestAndSet(entityIndex))
                {
                    ShadowClusterIndexedFields(clusterState);
                }
            }

            clusterState.SetDirty(_clusterChunkId, _clusterSlotIndex);
            return ref Unsafe.AsRef<T>(_clusterBase + _clusterLayout.ComponentOffset(slot) + _clusterSlotIndex * _clusterLayout.ComponentSize(slot));
        }

        {
            int chunkId = _locations[slot];
            var table = _engineState.SlotToComponentTable[slot];

            if (table.StorageMode == StorageMode.Versioned)
            {
                var (newChunkId, rawPtr) = _accessor.EcsVersionedCopyOnWrite(typeof(T), _id, table);
                _locations[slot] = newChunkId;
                return ref Unsafe.AsRef<T>((byte*)rawPtr + table.ComponentOverhead);
            }

            if (table.HasShadowableIndexes)
            {
                _accessor.ShadowIndexedFields<T>(table, chunkId, _id);
            }

            return ref _accessor.WriteEcsComponentData<T>(table, chunkId);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Component access — by type (slot lookup, slower)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Read a component by type. Resolves slot via archetype metadata.</summary>
    public ref readonly T Read<T>() where T : unmanaged
    {
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        Debug.Assert(typeId >= 0, $"Component type {typeof(T).Name} not registered");
        byte slot = _archetype.GetSlot(typeId);
        Debug.Assert((_enabledBits & (1 << slot)) != 0, $"Component {typeof(T).Name} at slot {slot} is disabled");

        if (_clusterBase != null)
        {
            // Versioned slots read from content chunk for MVCC correctness
            if ((_archetype.VersionedSlotMask & (1 << slot)) != 0)
            {
                int chunkId = _locations[slot];
                var table = _engineState.SlotToComponentTable[slot];
                return ref _accessor.ReadEcsComponentData<T>(table, chunkId);
            }
            return ref Unsafe.AsRef<T>(_clusterBase + _clusterLayout.ComponentOffset(slot) + _clusterSlotIndex * _clusterLayout.ComponentSize(slot));
        }

        int chunkId2 = _locations[slot];
        var table2 = _engineState.SlotToComponentTable[slot];
        return ref _accessor.ReadEcsComponentData<T>(table2, chunkId2);
    }

    /// <summary>Write a component by type. Resolves slot via archetype metadata.
    /// For Versioned: copy-on-write (allocates new chunk, preserves old for concurrent readers).
    /// For SingleVersion with indexes: shadows old field values on first write per tick for deferred index maintenance.</summary>
    public ref T Write<T>() where T : unmanaged
    {
        Debug.Assert(_writable, "EntityRef opened as read-only — use OpenMut for writes");
        SystemAccessValidator.AssertWrite<T>();
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        Debug.Assert(typeId >= 0, $"Component type {typeof(T).Name} not registered");
        byte slot = _archetype.GetSlot(typeId);
        Debug.Assert((_enabledBits & (1 << slot)) != 0, $"Component {typeof(T).Name} at slot {slot} is disabled");

        if (_clusterBase != null)
        {
            // Versioned cluster: COW path
            if ((_archetype.VersionedSlotMask & (1 << slot)) != 0)
            {
                var table = _engineState.SlotToComponentTable[slot];
                var (newChunkId, rawPtr) = _accessor.EcsVersionedCopyOnWrite(typeof(T), _id, table);
                _locations[slot] = newChunkId;
                return ref Unsafe.AsRef<T>((byte*)rawPtr + table.ComponentOverhead);
            }

            // SV cluster fast path
            var clusterState = _engineState.ClusterState;

            // Shadow capture for per-archetype B+Tree index maintenance (first write per entity per tick)
            if (clusterState.IndexSlots != null)
            {
                int entityIndex = _clusterChunkId * 64 + _clusterSlotIndex;
                if (!clusterState.ClusterShadowBitmap.TestAndSet(entityIndex))
                {
                    ShadowClusterIndexedFields(clusterState);
                }
            }

            clusterState.SetDirty(_clusterChunkId, _clusterSlotIndex);
            return ref Unsafe.AsRef<T>(_clusterBase + _clusterLayout.ComponentOffset(slot) + _clusterSlotIndex * _clusterLayout.ComponentSize(slot));
        }

        {
            int chunkId = _locations[slot];
            var table = _engineState.SlotToComponentTable[slot];

            if (table.StorageMode == StorageMode.Versioned)
            {
                var (newChunkId, rawPtr) = _accessor.EcsVersionedCopyOnWrite(typeof(T), _id, table);
                _locations[slot] = newChunkId;
                return ref Unsafe.AsRef<T>((byte*)rawPtr + table.ComponentOverhead);
            }

            if (table.HasShadowableIndexes)
            {
                _accessor.ShadowIndexedFields<T>(table, chunkId, _id);
            }

            return ref _accessor.WriteEcsComponentData<T>(table, chunkId);
        }
    }

    /// <summary>
    /// Capture old indexed field values from cluster SoA for all indexed components.
    /// Called once per entity per tick, before the first write mutation.
    /// </summary>
    private void ShadowClusterIndexedFields(ArchetypeClusterState clusterState)
    {
        long pk = (long)_id.RawValue;
        int entityIndex = _clusterChunkId * 64 + _clusterSlotIndex;
        var slots = clusterState.IndexSlots;
        // Skip Versioned slots (indexes updated at commit time) and Transient slots (indexes maintained per-ComponentTable)
        ushort skipMask = (ushort)(_archetype.VersionedSlotMask | _archetype.TransientSlotMask);

        for (int s = 0; s < slots.Length; s++)
        {
            ref var ixSlot = ref slots[s];

            if ((skipMask & (1 << ixSlot.Slot)) != 0)
            {
                continue;
            }

            int compSize = _clusterLayout.ComponentSize(ixSlot.Slot);
            byte* compBase = _clusterBase + _clusterLayout.ComponentOffset(ixSlot.Slot) + _clusterSlotIndex * compSize;

            for (int f = 0; f < ixSlot.Fields.Length; f++)
            {
                ref var field = ref ixSlot.Fields[f];
                var oldKey = KeyBytes8.FromPointer(compBase + field.FieldOffset, field.FieldSize);
                ixSlot.ShadowBuffers[f].Append(entityIndex, pk, oldKey);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Enable/Disable
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Check if a component at the given slot is enabled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEnabled(byte slotIndex) => (_enabledBits & (1 << slotIndex)) != 0;

    /// <summary>Check if a component is enabled by handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEnabled<T>(Comp<T> comp) where T : unmanaged
    {
        byte slot = _archetype.GetSlot(comp._componentTypeId);
        return (_enabledBits & (1 << slot)) != 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Optional component access — TryRead
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempt to read a component by type. Returns false if the archetype doesn't declare the component or it's disabled.
    /// Returns a copy (not ref) since out parameters can't be ref readonly.
    /// For zero-copy, use <c>if (entity.IsEnabled(comp)) { ref readonly var v = ref entity.Read(comp); }</c>.
    /// </summary>
    public bool TryRead<T>(out T value) where T : unmanaged
    {
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        if (typeId < 0 || !_archetype.TryGetSlot(typeId, out byte slot))
        {
            value = default;
            return false;
        }
        if ((_enabledBits & (1 << slot)) == 0)
        {
            value = default;
            return false;
        }

        if (_clusterBase != null)
        {
            // Transient slots read from TransientStore cluster segment
            if (_transientClusterBase != null && (_archetype.TransientSlotMask & (1 << slot)) != 0)
            {
                value = Unsafe.AsRef<T>(_transientClusterBase + _clusterLayout.ComponentOffset(slot) + _clusterSlotIndex * _clusterLayout.ComponentSize(slot));
                return true;
            }
            // Versioned slots read from content chunk (_locations populated by chain walk), not cluster slot.
            if ((_archetype.VersionedSlotMask & (1 << slot)) != 0)
            {
                int chunkId = _locations[slot];
                var table = _engineState.SlotToComponentTable[slot];
                value = _accessor.ReadEcsComponentData<T>(table, chunkId);
                return true;
            }

            value = Unsafe.AsRef<T>(_clusterBase + _clusterLayout.ComponentOffset(slot) + _clusterSlotIndex * _clusterLayout.ComponentSize(slot));
            return true;
        }

        int chunkId2 = _locations[slot];
        var table2 = _engineState.SlotToComponentTable[slot];
        value = _accessor.ReadEcsComponentData<T>(table2, chunkId2);
        return true;
    }

    /// <summary>Disable a component by handle. Stages the change for commit.</summary>
    public void Disable<T>(Comp<T> comp) where T : unmanaged
    {
        Debug.Assert(_writable, "EntityRef opened as read-only");
        byte slot = _archetype.GetSlot(comp._componentTypeId);
        _enabledBits &= (ushort)~(1 << slot);
        _accessor.StageEnableDisable(_id, _enabledBits);

        // Update cluster EnabledBits so cluster iteration sees the change immediately
        if (_clusterBase != null)
        {
            ref ulong clusterBits = ref *(ulong*)(_clusterBase + _clusterLayout.EnabledBitsOffset(slot));
            clusterBits &= ~(1UL << _clusterSlotIndex);
        }
    }

    /// <summary>Enable a component by handle. Stages the change for commit.</summary>
    public void Enable<T>(Comp<T> comp) where T : unmanaged
    {
        Debug.Assert(_writable, "EntityRef opened as read-only");
        byte slot = _archetype.GetSlot(comp._componentTypeId);
        _enabledBits |= (ushort)(1 << slot);
        _accessor.StageEnableDisable(_id, _enabledBits);

        // Update cluster EnabledBits so cluster iteration sees the change immediately
        if (_clusterBase != null)
        {
            ref ulong clusterBits = ref *(ulong*)(_clusterBase + _clusterLayout.EnabledBitsOffset(slot));
            clusterBits |= 1UL << _clusterSlotIndex;
        }
    }
}
