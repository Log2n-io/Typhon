using System;
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
        CheckConfig.Require(CheckConfig.Enabled, slot < _archetype.ComponentCount,
            $"Slot {slot} out of range for archetype with {_archetype.ComponentCount} components");
        CheckConfig.Require(CheckConfig.Enabled, (_enabledBits & (1 << slot)) != 0, $"Component at slot {slot} is disabled");

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
            // Commit-discipline read-your-own-writes: see this tx's staged value (point reads only; bulk spans read HEAD).
            if (_accessor.Discipline == DurabilityDiscipline.Commit)
            {
                byte* stagedPtr = _accessor.TryGetStagedPtr(typeof(T), (long)_id.RawValue);
                if (stagedPtr != null)
                {
                    return ref Unsafe.AsRef<T>(stagedPtr);
                }
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
        CheckConfig.Require(CheckConfig.Enabled, _writable, $"EntityRef opened as read-only — use OpenMut for writes");
        SystemAccessValidator.AssertWrite<T>();
        byte slot = _archetype.GetSlot(comp._componentTypeId);
        CheckConfig.Require(CheckConfig.Enabled, slot < _archetype.ComponentCount,
            $"Slot {slot} out of range for archetype with {_archetype.ComponentCount} components");
        CheckConfig.Require(CheckConfig.Enabled, (_enabledBits & (1 << slot)) != 0, $"Component at slot {slot} is disabled");

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
            byte* svHeadPtr = _clusterBase + _clusterLayout.ComponentOffset(slot) + _clusterSlotIndex * _clusterLayout.ComponentSize(slot);
            var svTable = _engineState.SlotToComponentTable[slot];

            // CM-02: a DefaultDiscipline=Commit component escalates the whole transaction to Commit on first touch.
            if (svTable.Discipline == DurabilityDiscipline.Commit)
            {
                _accessor.ResolveCommitDiscipline(svTable);
            }

            // Commit discipline (Variant A): stage the write — leave the cluster HEAD untouched, no dirty bit, no shadow capture (CM-01).
            // The exact B+Tree index is reconciled at commit (read old key from HEAD, new from the staged slot).
            if (_accessor.Discipline == DurabilityDiscipline.Commit)
            {
                return ref _accessor.StageClusterCommitWrite<T>(
                    svTable, comp._componentTypeId, (long)_id.RawValue, _clusterChunkId * 64 + _clusterSlotIndex, svHeadPtr);
            }

            // Shadow capture for per-archetype B+Tree index maintenance (first write per entity per tick)
            if (clusterState.IndexSlots != null)
            {
                int entityIndex = _clusterChunkId * 64 + _clusterSlotIndex;
                if (!clusterState.ClusterShadowBitmap.TestAndSet(entityIndex))
                {
                    ShadowClusterIndexedFields(clusterState);
                }
            }

            _accessor.NoteSvInPlaceWrite();   // CM-02: an in-place TickFence write happened — blocks late auto-escalation to Commit
            clusterState.SetDirty(_clusterChunkId, _clusterSlotIndex);
            return ref Unsafe.AsRef<T>(svHeadPtr);
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

            // CM-02: a DefaultDiscipline=Commit component escalates the whole tx to Commit before the (skipped) shadow capture below.
            if (table.StorageMode == StorageMode.SingleVersion && table.Discipline == DurabilityDiscipline.Commit)
            {
                _accessor.ResolveCommitDiscipline(table);
            }

            // Commit discipline stages and reconciles indexes at commit — skip the per-tick shadow capture (which feeds the fence-time Move).
            if (table.HasShadowableIndexes && _accessor.Discipline != DurabilityDiscipline.Commit)
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
        CheckConfig.Require(CheckConfig.Enabled, typeId >= 0, $"Component type {typeof(T).Name} not registered");
        byte slot = _archetype.GetSlot(typeId);
        CheckConfig.Require(CheckConfig.Enabled, (_enabledBits & (1 << slot)) != 0, $"Component {typeof(T).Name} at slot {slot} is disabled");

        if (_clusterBase != null)
        {
            // Versioned slots read from content chunk for MVCC correctness
            if ((_archetype.VersionedSlotMask & (1 << slot)) != 0)
            {
                int chunkId = _locations[slot];
                var table = _engineState.SlotToComponentTable[slot];
                return ref _accessor.ReadEcsComponentData<T>(table, chunkId);
            }
            // Commit-discipline read-your-own-writes: see this tx's staged value (point reads only; bulk spans read HEAD).
            if (_accessor.Discipline == DurabilityDiscipline.Commit)
            {
                byte* stagedPtr = _accessor.TryGetStagedPtr(typeof(T), (long)_id.RawValue);
                if (stagedPtr != null)
                {
                    return ref Unsafe.AsRef<T>(stagedPtr);
                }
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
        CheckConfig.Require(CheckConfig.Enabled, _writable, $"EntityRef opened as read-only — use OpenMut for writes");
        SystemAccessValidator.AssertWrite<T>();
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        CheckConfig.Require(CheckConfig.Enabled, typeId >= 0, $"Component type {typeof(T).Name} not registered");
        byte slot = _archetype.GetSlot(typeId);
        CheckConfig.Require(CheckConfig.Enabled, (_enabledBits & (1 << slot)) != 0, $"Component {typeof(T).Name} at slot {slot} is disabled");

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
            byte* svHeadPtr = _clusterBase + _clusterLayout.ComponentOffset(slot) + _clusterSlotIndex * _clusterLayout.ComponentSize(slot);
            var svTable = _engineState.SlotToComponentTable[slot];

            // CM-02: a DefaultDiscipline=Commit component escalates the whole transaction to Commit on first touch.
            if (svTable.Discipline == DurabilityDiscipline.Commit)
            {
                _accessor.ResolveCommitDiscipline(svTable);
            }

            // Commit discipline (Variant A): stage the write — HEAD untouched, no dirty/shadow (CM-01). Index reconciled at commit.
            if (_accessor.Discipline == DurabilityDiscipline.Commit)
            {
                return ref _accessor.StageClusterCommitWrite<T>(svTable, typeId, (long)_id.RawValue, _clusterChunkId * 64 + _clusterSlotIndex, svHeadPtr);
            }

            // Shadow capture for per-archetype B+Tree index maintenance (first write per entity per tick)
            if (clusterState.IndexSlots != null)
            {
                int entityIndex = _clusterChunkId * 64 + _clusterSlotIndex;
                if (!clusterState.ClusterShadowBitmap.TestAndSet(entityIndex))
                {
                    ShadowClusterIndexedFields(clusterState);
                }
            }

            _accessor.NoteSvInPlaceWrite();   // CM-02: an in-place TickFence write happened — blocks late auto-escalation to Commit
            clusterState.SetDirty(_clusterChunkId, _clusterSlotIndex);
            return ref Unsafe.AsRef<T>(svHeadPtr);
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

            // CM-02: a DefaultDiscipline=Commit component escalates the whole tx to Commit before the (skipped) shadow capture below.
            if (table.StorageMode == StorageMode.SingleVersion && table.Discipline == DurabilityDiscipline.Commit)
            {
                _accessor.ResolveCommitDiscipline(table);
            }

            // Commit discipline stages and reconciles indexes at commit — skip the per-tick shadow capture (which feeds the fence-time Move).
            if (table.HasShadowableIndexes && _accessor.Discipline != DurabilityDiscipline.Commit)
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

    // ═══════════════════════════════════════════════════════════════════════
    // Non-generic / runtime access — for tooling that decodes by field layout
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Number of component slots declared by this entity's archetype. Slots are addressable <c>[0, ComponentCount)</c>.</summary>
    public int ComponentCount => _archetype.ComponentCount;

    /// <summary>
    /// The registered name of the component at <paramref name="slot"/> — matches <c>ComponentTable.Definition.Name</c> (the join key for the schema layout).
    /// Pairs with <see cref="ReadRaw"/> for runtime, non-generic component decode.
    /// </summary>
    public string GetComponentName(int slot)
    {
        if ((uint)slot >= (uint)_archetype.ComponentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }
        return _engineState.SlotToComponentTable[slot].Definition.Name;
    }

    /// <summary>
    /// Read the raw storage bytes of the component at <paramref name="slot"/> — the non-generic counterpart to <see cref="Read{T}()"/> for tooling that decodes
    /// components by field layout at runtime (e.g. the Workbench Data Browser). The returned span points directly into mapped page / cluster memory (zero-copy)
    /// and is valid only while this <see cref="EntityRef"/> is alive. Its length is the component's storage size; field values are decoded by the caller using
    /// the component's field offsets. MVCC-correct: Versioned slots resolve to the content visible at the owning transaction's snapshot. Works regardless of the
    /// component's enabled state — query <see cref="IsEnabled(byte)"/> separately to render disabled components.
    /// <para>
    /// Prefer the typed <see cref="Read{T}()"/> / <see cref="TryRead{T}(out T)"/> whenever the component type is known at compile time — they return a typed
    /// (zero-copy) ref with no manual offset decoding. Reach for <see cref="ReadRaw"/> only when the component type is not available statically.
    /// </para>
    /// </summary>
    public ReadOnlySpan<byte> ReadRaw(int slot)
    {
        if ((uint)slot >= (uint)_archetype.ComponentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        var table = _engineState.SlotToComponentTable[slot];
        int size = table.Definition.ComponentStorageSize;

        if (_clusterBase != null)
        {
            // Transient slot: TransientStore cluster segment (mixed archetypes; for pure-T, _clusterBase IS the TS base so this branch is skipped).
            if (_transientClusterBase != null && (_archetype.TransientSlotMask & (1 << slot)) != 0)
            {
                byte* tp = _transientClusterBase + _clusterLayout.ComponentOffset(slot) + _clusterSlotIndex * _clusterLayout.ComponentSize(slot);
                return new ReadOnlySpan<byte>(tp, size);
            }
            // Versioned slot: read from the content chunk resolved by the revision-chain walk (MVCC-correct), not the cluster HEAD cache.
            if ((_archetype.VersionedSlotMask & (1 << slot)) != 0)
            {
                int vChunkId = _locations[slot];
                if (vChunkId == 0)
                {
                    return default;
                }
                byte* vp = _accessor.ReadEcsComponentDataRaw(table, _archetype._componentTypeIds[slot], _archetype._slotToComponentType[slot], vChunkId);
                return new ReadOnlySpan<byte>(vp, size);
            }
            // SV cluster slot: direct SoA pointer.
            byte* cp = _clusterBase + _clusterLayout.ComponentOffset(slot) + _clusterSlotIndex * _clusterLayout.ComponentSize(slot);
            return new ReadOnlySpan<byte>(cp, size);
        }

        int chunkId = _locations[slot];
        if (chunkId == 0)
        {
            return default;
        }
        byte* p = _accessor.ReadEcsComponentDataRaw(table, _archetype._componentTypeIds[slot], _archetype._slotToComponentType[slot], chunkId);
        return new ReadOnlySpan<byte>(p, size);
    }

    /// <summary>Disable a component by handle. Stages the change for commit.</summary>
    public void Disable<T>(Comp<T> comp) where T : unmanaged
    {
        CheckConfig.Require(CheckConfig.Enabled, _writable, $"EntityRef opened as read-only");
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
        CheckConfig.Require(CheckConfig.Enabled, _writable, $"EntityRef opened as read-only");
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
