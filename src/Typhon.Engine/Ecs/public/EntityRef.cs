using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Read-only, zero-copy entity handle. A ref struct that copies the EntityRecord from the per-archetype LinearHash and provides typed component reads
/// via cached Location ChunkIds.
/// </summary>
/// <remarks>
/// <para>Created by <c>Open</c> / <c>TryOpen</c> on any accessor. It has no write member: writes go through <see cref="EntityRefMut"/>, returned by
/// <c>OpenMut</c> / <c>TryOpenMut</c>, which converts implicitly to <see cref="EntityRef"/> (#997). Must not outlive the creating accessor.</para>
/// <para>Reads delegate to the EntityAccessor for chunk accessor management.</para>
/// </remarks>
[PublicAPI]
public unsafe ref struct EntityRef
{
    internal readonly EntityId _id;
    internal readonly ArchetypeMetadata _archetype;
    internal readonly ArchetypeEngineState _engineState;
    internal readonly EntityAccessor _accessor;
    internal ushort _enabledBits;

    /// <summary>
    /// True when this ref was built from a <c>SpawnEntry</c> of the OPEN transaction — the entity exists only in spawn-staging chunks and is not in the
    /// EntityMap or the cluster yet, so it has no HEAD and no other transaction can observe it. The Commit-discipline write path keys off this to write
    /// in place instead of staging (#713); see <see cref="EntityAccessor.WriteEcsComponentData{T}"/>.
    /// </summary>
    internal bool _isOwnSpawn;
    private fixed int _locations[16];

    /// <summary>
    /// Per-slot revision-chain ROOT chunk ids for Versioned slots (0 = not resolved via point-open). Set by <c>Transaction.ResolveEntity</c> so the
    /// first <see cref="EntityRefMut.Write{T}(Comp{T})"/> can re-resolve the <c>CompRevInfo</c> with a direct (fast-path) chain walk instead of a PK-index
    /// lookup —
    /// read-only resolves no longer populate <c>ComponentInfo.SingleCache</c> (deferred-insert: the cache holds only written/spawned entries).
    /// </summary>
    private fixed int _chainRoots[16];

    /// <summary>
    /// Versioned slots whose <c>_locations</c> entry has NOT been resolved yet — bit <c>i</c> set means slot <c>i</c> currently carries only its revision-chain
    /// root in <c>_chainRoots</c>, and the walk that turns it into an MVCC-visible content chunk is deferred to the first read of that slot.
    /// <para>
    /// Resolving eagerly costs a chain walk per Versioned slot on <i>every</i> open, whether or not the caller ever touches that component. Measured on the
    /// SWG sample: <c>MoveSystem</c> writes only <c>Transform</c> (SingleVersion) yet walked <c>Wallet</c>'s chain 4,000,200 times over 200 ticks — 733 ms of
    /// self-time plus the CompRev pages it faulted in, for data the system never reads.
    /// </para>
    /// </summary>
    private ushort _versionedPending;

    // ── Cluster storage fields (non-null when entity uses cluster storage) ──
    internal byte* _clusterBase;                    // Pointer to primary cluster chunk data; null = legacy path
    internal byte* _transientClusterBase;           // Pointer to TransientStore cluster base; null = no Transient segment (or pure-T where _clusterBase is TS)
    internal byte _clusterSlotIndex;                // Slot within cluster (0..63)
    internal int _clusterChunkId;                   // Cluster chunk ID (for dirty tracking: entityIndex = chunkId * 64 + slot)
    internal ArchetypeClusterInfo _clusterLayout;   // Layout info for offset computation

    /// <summary>
    /// Marks this entity's slot in its cluster's per-tick structure word — the push set replication projects after the fence (ADR-067). The write-by-id
    /// counterpart of <c>ClusterRef.NotePushed</c>: a command's effect on the entity it names is reached by id, not by walking its cluster.
    /// </summary>
    internal readonly void NotePushed() => _engineState?.ClusterState?.NoteStructureSlots(_clusterChunkId, 1UL << _clusterSlotIndex);

    internal EntityRef(EntityId id, ArchetypeMetadata archetype, ArchetypeEngineState engineState, EntityAccessor accessor, ushort enabledBits)
    {
        _id = id;
        _archetype = archetype;
        _engineState = engineState;
        _accessor = accessor;
        _enabledBits = enabledBits;
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
    internal readonly int GetLocation(int slot) => _locations[slot];

    /// <summary>Override the chunkId at a specific slot. Used by ResolveEntity for MVCC revision chain resolution.</summary>
    internal void SetLocation(int slot, int chunkId) => _locations[slot] = chunkId;

    /// <summary>The revision-chain root chunk id recorded for a Versioned slot (0 = not resolved via point-open).</summary>
    internal readonly int ChainRoot(int slot) => _chainRoots[slot];

    /// <summary>
    /// Record a Versioned copy-on-write: <paramref name="slot"/> now reads <paramref name="newChunkId"/>, and any deferred chain walk for it is dropped —
    /// the COW result supersedes it, and a later read must not re-resolve to the old revision. The one place that keeps the location and the pending
    /// mask in step (see <c>_versionedPending</c>).
    /// </summary>
    internal void ApplyCopyOnWrite(int slot, int newChunkId)
    {
        _locations[slot] = newChunkId;
        _versionedPending &= (ushort)~(1 << slot);
    }

    /// <summary>True when <paramref name="slot"/> is a Versioned component this entity has no revision chain for — genuinely absent (#845).</summary>
    /// <remarks>
    /// Three signals, because three different resolvers build an <see cref="EntityRef"/> and they populate different fields.
    /// <list type="bullet">
    /// <item><c>_chainRoots[slot]</c> is the record's <c>CompRevFirstChunkId</c> and is the authoritative absence signal — but it is never populated on an
    /// own-spawn ref, whose slot state lives in <c>_locations</c> (the SpawnEntry's VerLoc).</item>
    /// <item><c>_locations[slot]</c> alone is 0 for a FAILED chain walk and for every slot still unresolved on the deferred path, neither of which is
    /// absence.</item>
    /// <item><c>_clusterLayout</c> cannot be consulted at all: it is null on an own-spawn ref, which is what made the first version of this guard throw a
    /// NullReferenceException on the ordinary disable/enable round trip.</item>
    /// </list>
    /// <c>VersionedSlotMask</c> is zeroed for non-cluster archetypes, so the whole predicate short-circuits to false there.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly bool IsVersionedSlotAbsent(byte slot) =>
        (_archetype.VersionedSlotMask & (1 << slot)) != 0 && _chainRoots[slot] == 0 && _locations[slot] == 0;

    /// <summary>Record the revision-chain root chunk id for a Versioned slot (see <c>_chainRoots</c>).</summary>
    internal void SetChainRoot(int slot, int chainRootChunkId) => _chainRoots[slot] = chainRootChunkId;

    /// <summary>
    /// Mark a Versioned slot as carrying only its chain root, deferring the revision-chain walk to the first read (see <c>_versionedPending</c>).
    /// The caller must have populated <see cref="SetChainRoot"/> for the same slot first.
    /// </summary>
    internal void MarkVersionedPending(int slot) => _versionedPending |= (ushort)(1 << slot);

    /// <summary>True while any Versioned slot still waits for its deferred chain walk (tests: proves the memo landed on this handle, not a copy).</summary>
    internal readonly bool HasPendingVersionedSlots => _versionedPending != 0;

    /// <summary>
    /// Resolve <paramref name="slot"/>'s deferred revision chain if it is still pending. Inlined so the common case (nothing pending, or already resolved)
    /// is a single mask test; the walk itself lives in a non-inlined slow path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void EnsureVersionedResolved(int slot)
    {
        if ((_versionedPending & (1 << slot)) != 0)
        {
            ResolveVersionedSlot(slot);
        }
    }

    /// <summary>Walk the deferred chain for <paramref name="slot"/> and memoize the visible content chunk. Runs at most once per slot per EntityRef.</summary>
    /// <remarks>
    /// <c>readonly</c>, and it still writes: the memo is a cache, invisible to callers, so the read members stay <c>readonly</c> and a call through a
    /// read-only receiver (an <c>in</c> parameter, a <c>foreach</c> variable) costs no defensive copy of this ~200-byte handle. The write goes through
    /// <c>Unsafe.AsRef(in this)</c> — the receiver itself, never a copy, because a <c>readonly</c> member is exactly what stops the compiler from making
    /// one. Safe because a ref struct lives on one stack and is used by one thread.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void ResolveVersionedSlot(int slot)
    {
        ref var self = ref Unsafe.AsRef(in this);
        // Walk first, then clear: a walk that throws (an expired snapshot) must leave the slot pending, so a caught retry walks — and throws — again
        // instead of reading location 0 as if it had been resolved.
        self._locations[slot] = _accessor.ResolveVersionedContentChunk(_archetype, slot, _chainRoots[slot]);
        self._versionedPending &= (ushort)~(1 << slot);
    }

    /// <summary>Copy locations from a managed byte array.</summary>
    internal void CopyLocationsFrom(byte[] recordBytes, int componentCount)
    {
        // Read through a span, not a pinned pointer: recordBytes is managed.
        for (int i = 0; i < componentCount; i++)
        {
            _locations[i] = System.Runtime.InteropServices.MemoryMarshal.Read<int>(recordBytes.AsSpan(EntityRecordAccessor.HeaderSize + i * sizeof(int)));
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
    public readonly EntityId Id => _id;

    /// <summary>The archetype ID of this entity.</summary>
    public readonly ushort ArchetypeId => _id.ArchetypeId;

    /// <summary>True if this EntityRef refers to a valid entity.</summary>
    public readonly bool IsValid => !_id.IsNull;

    // ═══════════════════════════════════════════════════════════════════════
    // Component access — by handle (O(1), preferred)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Read a component by handle. Zero-copy — returns a ref into the chunk page (or cluster slot).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref readonly T Read<T>(Comp<T> comp) where T : unmanaged
    {
        byte slot = _archetype.GetSlot(comp._componentTypeId);
        // Hot path: guaranteed-fold inline guard (not CheckConfig.Require) — the interpolated-string handler
        // materializes a 32-byte struct per call even when off (~9ns); `CheckConfig.Enabled &&` folds to nothing (#422 AC#6).
        if (CheckConfig.Enabled && slot >= _archetype.ComponentCount)
        {
            ThrowHelper.ThrowInvalidOp($"Slot {slot} out of range for archetype with {_archetype.ComponentCount} components");
        }
        if (CheckConfig.Enabled && (_enabledBits & (1 << slot)) == 0)
        {
            ThrowHelper.ThrowInvalidOp($"Component at slot {slot} is disabled");
        }

        if (_clusterBase != null)
        {
            // Transient slots read from TransientStore cluster segment (mixed archetypes only; for pure-T, _clusterBase IS the TS base)
            if (_transientClusterBase != null && (_archetype.TransientSlotMask & (1 << slot)) != 0)
            {
                return ref Unsafe.AsRef<T>(_transientClusterBase + _clusterLayout.ComponentOffset(slot) + _clusterSlotIndex * _clusterLayout.ComponentSize(slot));
            }
            // Versioned slots read from the content chunk, not the cluster slot — the cluster slot is the HEAD cache, used by bulk iteration only, while
            // MVCC-correct reads must see the revision visible at this TSN. The chain walk that finds it is deferred to here (see _versionedPending).
            if ((_archetype.VersionedSlotMask & (1 << slot)) != 0)
            {
                EnsureVersionedResolved(slot);
                int chunkId = _locations[slot];
                var table = _engineState.SlotToComponentTable[slot];
                return ref _accessor.ReadEcsComponentData<T>(table, chunkId, (long)_id.RawValue, _isOwnSpawn);
            }
            // Commit-discipline read-your-own-writes: see this tx's staged value (point reads only; bulk spans read HEAD).
            if (_accessor.Discipline == CommitDiscipline.Commit)
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
        return ref _accessor.ReadEcsComponentData<T>(table2, chunkId2, (long)_id.RawValue, _isOwnSpawn);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Component access — by type (slot lookup, slower)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Read a component by type. Resolves slot via archetype metadata.</summary>
    public readonly ref readonly T Read<T>() where T : unmanaged
    {
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        if (CheckConfig.Enabled && typeId < 0)
        {
            ThrowHelper.ThrowInvalidOp($"Component type {typeof(T).Name} not registered");
        }
        byte slot = _archetype.GetSlot(typeId);
        if (CheckConfig.Enabled && (_enabledBits & (1 << slot)) == 0)
        {
            ThrowHelper.ThrowInvalidOp($"Component {typeof(T).Name} at slot {slot} is disabled");
        }

        if (_clusterBase != null)
        {
            // Versioned slots read from content chunk for MVCC correctness
            if ((_archetype.VersionedSlotMask & (1 << slot)) != 0)
            {
                EnsureVersionedResolved(slot);
                int chunkId = _locations[slot];
                var table = _engineState.SlotToComponentTable[slot];
                return ref _accessor.ReadEcsComponentData<T>(table, chunkId, (long)_id.RawValue, _isOwnSpawn);
            }
            // Commit-discipline read-your-own-writes: see this tx's staged value (point reads only; bulk spans read HEAD).
            if (_accessor.Discipline == CommitDiscipline.Commit)
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
        return ref _accessor.ReadEcsComponentData<T>(table2, chunkId2, (long)_id.RawValue, _isOwnSpawn);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Enabled state
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Check if a component at the given slot is enabled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsEnabled(byte slotIndex) => (_enabledBits & (1 << slotIndex)) != 0;

    /// <summary>Check if a component is enabled by handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsEnabled<T>(Comp<T> comp) where T : unmanaged
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
    public readonly bool TryRead<T>(out T value) where T : unmanaged
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
                EnsureVersionedResolved(slot);
                int chunkId = _locations[slot];
                var table = _engineState.SlotToComponentTable[slot];
                value = _accessor.ReadEcsComponentData<T>(table, chunkId, (long)_id.RawValue, _isOwnSpawn);
                return true;
            }

            value = Unsafe.AsRef<T>(_clusterBase + _clusterLayout.ComponentOffset(slot) + _clusterSlotIndex * _clusterLayout.ComponentSize(slot));
            return true;
        }

        int chunkId2 = _locations[slot];
        var table2 = _engineState.SlotToComponentTable[slot];
        value = _accessor.ReadEcsComponentData<T>(table2, chunkId2, (long)_id.RawValue, _isOwnSpawn);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Non-generic / runtime access — for tooling that decodes by field layout
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Number of component slots declared by this entity's archetype. Slots are addressable <c>[0, ComponentCount)</c>.</summary>
    public readonly int ComponentCount => _archetype.ComponentCount;

    /// <summary>
    /// The registered name of the component at <paramref name="slot"/> — matches <c>ComponentTable.Definition.Name</c> (the join key for the schema layout).
    /// Pairs with <see cref="ReadRaw"/> for runtime, non-generic component decode.
    /// </summary>
    public readonly string GetComponentName(int slot)
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
    public readonly ReadOnlySpan<byte> ReadRaw(int slot)
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
                EnsureVersionedResolved(slot);
                int vChunkId = _locations[slot];
                if (vChunkId == 0)
                {
                    return default;
                }
                byte* vp = _accessor.ReadEcsComponentDataRaw(table, _archetype._componentTypeIds[slot], _archetype._slotToComponentType[slot], vChunkId,
                    _isOwnSpawn);
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
        byte* p = _accessor.ReadEcsComponentDataRaw(table, _archetype._componentTypeIds[slot], _archetype._slotToComponentType[slot], chunkId, _isOwnSpawn);
        return new ReadOnlySpan<byte>(p, size);
    }

}
