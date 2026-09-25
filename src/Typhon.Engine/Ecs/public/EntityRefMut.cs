using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Writable entity handle: everything <see cref="EntityRef"/> reads, plus <see cref="Write{T}(Comp{T})"/>, <see cref="Enable{T}(Comp{T})"/> and
/// <see cref="Disable{T}"/>. Returned by <c>OpenMut</c> / <c>TryOpenMut</c> on every accessor; <c>Open</c> / <c>TryOpen</c> return the read-only
/// <see cref="EntityRef"/>, which has no write member at all — writing through a read-only open does not compile (#997).
/// </summary>
/// <remarks>
/// <para>Holds one <see cref="EntityRef"/> and no other state: read members forward to it, write members work on its fields. A writable resolve fetches
/// the entity's cluster page with <c>GetChunkAddress(…, dirty: true)</c> and has run the accessor's mutation prep (<c>EnsureMutable</c> + <c>InProgress</c>
/// for a <see cref="Transaction"/>) — the preconditions the write paths below rely on.</para>
/// <para>Converts implicitly to <see cref="EntityRef"/>, so read-only helpers take an <see cref="EntityRef"/>. The conversion copies: a Versioned slot
/// resolved on the copy is not memoized back here, and a copy taken before a Versioned <see cref="Write{T}(Comp{T})"/> keeps reading the pre-write
/// revision.</para>
/// <para>Must not outlive the creating accessor.</para>
/// </remarks>
[PublicAPI]
public unsafe ref struct EntityRefMut
{
    // The ONLY field, and it must stay that way: resolvers reinterpret a resolved EntityRef as an EntityRefMut with Unsafe.BitCast rather than wrap it,
    // because `new EntityRefMut(resolve(...))` makes the JIT copy the ~200-byte handle out of a temporary — 64-byte loads issued right after the
    // resolver's narrow stores, which defeats store forwarding: +10 ns per ArchetypeAccessor.OpenMut, measured (#997).
    internal EntityRef _ref;

    /// <summary>Read-only view of the same entity. A copy — see the type remarks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator EntityRef(in EntityRefMut entity) => entity._ref;

    // ═══════════════════════════════════════════════════════════════════════
    // Read side — forwards to EntityRef
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc cref="EntityRef.Id"/>
    public readonly EntityId Id => _ref._id;

    /// <inheritdoc cref="EntityRef.ArchetypeId"/>
    public readonly ushort ArchetypeId => _ref._id.ArchetypeId;

    /// <inheritdoc cref="EntityRef.IsValid"/>
    public readonly bool IsValid => !_ref._id.IsNull;

    /// <inheritdoc cref="EntityRef.ComponentCount"/>
    public readonly int ComponentCount => _ref.ComponentCount;

    /// <inheritdoc cref="EntityRef.GetComponentName"/>
    public readonly string GetComponentName(int slot) => _ref.GetComponentName(slot);

    /// <inheritdoc cref="EntityRef.Read{T}(Comp{T})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref readonly T Read<T>(Comp<T> comp) where T : unmanaged => ref _ref.Read(comp);

    /// <inheritdoc cref="EntityRef.Read{T}()"/>
    public readonly ref readonly T Read<T>() where T : unmanaged => ref _ref.Read<T>();

    /// <inheritdoc cref="EntityRef.TryRead{T}(out T)"/>
    public readonly bool TryRead<T>(out T value) where T : unmanaged => _ref.TryRead(out value);

    /// <inheritdoc cref="EntityRef.IsEnabled(byte)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsEnabled(byte slotIndex) => _ref.IsEnabled(slotIndex);

    /// <inheritdoc cref="EntityRef.IsEnabled{T}(Comp{T})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsEnabled<T>(Comp<T> comp) where T : unmanaged => _ref.IsEnabled(comp);

    /// <inheritdoc cref="EntityRef.ReadRaw"/>
    public readonly ReadOnlySpan<byte> ReadRaw(int slot) => _ref.ReadRaw(slot);

    /// <summary>Marks this entity's slot in its cluster's per-tick structure word — see <c>EntityRef.NotePushed</c>.</summary>
    internal readonly void NotePushed() => _ref.NotePushed();

    // ═══════════════════════════════════════════════════════════════════════
    // Write side
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Write a component by handle. Returns a mutable ref into the chunk page (or cluster slot).
    /// For Versioned: copy-on-write (allocates new chunk, preserves old for concurrent readers).
    /// For SingleVersion with indexes: shadows old field values on first write per tick for deferred index maintenance.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Write<T>(Comp<T> comp) where T : unmanaged
    {
        SystemAccessValidator.AssertWrite<T>();
        byte slot = _ref._archetype.GetSlot(comp._componentTypeId);
        // Hot path: guaranteed-fold inline guard (not CheckConfig.Require) — the interpolated-string handler
        // materializes a 32-byte struct per call even when off (~9ns); `CheckConfig.Enabled &&` folds to nothing (#422 AC#6).
        if (CheckConfig.Enabled && slot >= _ref._archetype.ComponentCount)
        {
            ThrowHelper.ThrowInvalidOp($"Slot {slot} out of range for archetype with {_ref._archetype.ComponentCount} components");
        }
        if (CheckConfig.Enabled && (_ref._enabledBits & (1 << slot)) == 0)
        {
            ThrowHelper.ThrowInvalidOp($"Component at slot {slot} is disabled");
        }

        if (_ref._clusterBase != null)
        {
            // Versioned cluster: COW path (same as legacy Versioned — cluster slot updated at commit)
            if ((_ref._archetype.VersionedSlotMask & (1 << slot)) != 0)
            {
                var table = _ref._engineState.SlotToComponentTable[slot];
                var (newChunkId, rawPtr) = _ref._accessor.EcsVersionedCopyOnWrite(typeof(T), _ref._id, table, _ref.ChainRoot(slot));
                _ref.ApplyCopyOnWrite(slot, newChunkId);

                return ref Unsafe.AsRef<T>((byte*)rawPtr + table.ComponentOverhead);
            }

            var clusterState = _ref._engineState.ClusterState;

            // Transient cluster: in-place write to TransientStore segment (no COW, no revision chain)
            if (_ref._transientClusterBase != null && (_ref._archetype.TransientSlotMask & (1 << slot)) != 0)
            {
                // Shadow capture for SV indexed fields (first write per entity per tick — captures SV fields, skips T and V)
                if (clusterState.IndexSlots != null)
                {
                    int entityIndex = _ref._clusterChunkId * 64 + _ref._clusterSlotIndex;
                    if (!clusterState.ClusterShadowBitmap.TestAndSet(entityIndex))
                    {
                        ShadowClusterIndexedFields(clusterState);
                    }
                }
                clusterState.SetDirty(_ref._clusterChunkId, _ref._clusterSlotIndex, slot);
                return ref Unsafe.AsRef<T>(
                    _ref._transientClusterBase + _ref._clusterLayout.ComponentOffset(slot) + _ref._clusterSlotIndex * _ref._clusterLayout.ComponentSize(slot));
            }

            // SV cluster fast path: direct pointer arithmetic into SoA array.
            // Page was already marked dirty at resolve time (OpenMut → GetChunkAddress(dirty:true)).
            byte* svHeadPtr = _ref._clusterBase + _ref._clusterLayout.ComponentOffset(slot) + _ref._clusterSlotIndex * _ref._clusterLayout.ComponentSize(slot);
            var svTable = _ref._engineState.SlotToComponentTable[slot];

            // CM-02: a DefaultDiscipline=Commit component escalates the whole transaction to Commit on first touch.
            if (svTable.Discipline == CommitDiscipline.Commit)
            {
                _ref._accessor.ResolveCommitDiscipline(svTable);
            }

            // Commit discipline (Variant A): stage the write — leave the cluster HEAD untouched, no dirty bit, no shadow capture (CM-01).
            // The exact B+Tree index is reconciled at commit (read old key from HEAD, new from the staged slot).
            if (_ref._accessor.Discipline == CommitDiscipline.Commit)
            {
                return ref _ref._accessor.StageClusterCommitWrite<T>(
                    svTable, comp._componentTypeId, (long)_ref._id.RawValue, _ref._clusterChunkId * 64 + _ref._clusterSlotIndex, svHeadPtr);
            }

            // Shadow capture for per-archetype B+Tree index maintenance (first write per entity per tick)
            if (clusterState.IndexSlots != null)
            {
                int entityIndex = _ref._clusterChunkId * 64 + _ref._clusterSlotIndex;
                if (!clusterState.ClusterShadowBitmap.TestAndSet(entityIndex))
                {
                    ShadowClusterIndexedFields(clusterState);
                }
            }

            _ref._accessor.NoteSvInPlaceWrite();   // CM-02: an in-place TickFence write happened — blocks late auto-escalation to Commit
            clusterState.SetDirty(_ref._clusterChunkId, _ref._clusterSlotIndex, slot);
            return ref Unsafe.AsRef<T>(svHeadPtr);
        }

        {
            int chunkId = _ref.GetLocation(slot);
            var table = _ref._engineState.SlotToComponentTable[slot];

            if (table.StorageMode == StorageMode.Versioned)
            {
                var (newChunkId, rawPtr) = _ref._accessor.EcsVersionedCopyOnWrite(typeof(T), _ref._id, table, _ref.ChainRoot(slot));
                _ref.ApplyCopyOnWrite(slot, newChunkId);

                return ref Unsafe.AsRef<T>((byte*)rawPtr + table.ComponentOverhead);
            }

            // CM-02: a DefaultDiscipline=Commit component escalates the whole tx to Commit before the (skipped) shadow capture below.
            if (table.StorageMode == StorageMode.SingleVersion && table.Discipline == CommitDiscipline.Commit)
            {
                _ref._accessor.ResolveCommitDiscipline(table);
            }

            // Commit discipline stages and reconciles indexes at commit — skip the per-tick shadow capture (which feeds the fence-time Move).
            // An own-spawn is skipped for two independent reasons. It has no OLD key to shadow: FinalizeSpawns inserts this entity's index entries fresh from
            // the final staged bytes, so there is no Move for the fence to perform — the same argument DIRTY-01 makes about the dirty bit. And since #839 the
            // location of an unpublished non-Versioned slot is a spawn-arena handle, which ShadowIndexedFields would dereference against the ComponentSegment,
            // reading an unrelated chunk's bytes as the "old key" and recording the handle as a chunk id for fence-time index maintenance.
            if (table.HasShadowableIndexes && _ref._accessor.Discipline != CommitDiscipline.Commit && !_ref._isOwnSpawn)
            {
                _ref._accessor.ShadowIndexedFields<T>(table, chunkId, _ref._id);
            }

            return ref _ref._accessor.WriteEcsComponentData<T>(table, chunkId, (long)_ref._id.RawValue, _ref._isOwnSpawn);
        }
    }

    /// <summary>Write a component by type. Resolves slot via archetype metadata.
    /// For Versioned: copy-on-write (allocates new chunk, preserves old for concurrent readers).
    /// For SingleVersion with indexes: shadows old field values on first write per tick for deferred index maintenance.</summary>
    public ref T Write<T>() where T : unmanaged
    {
        SystemAccessValidator.AssertWrite<T>();
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        if (CheckConfig.Enabled && typeId < 0)
        {
            ThrowHelper.ThrowInvalidOp($"Component type {typeof(T).Name} not registered");
        }
        byte slot = _ref._archetype.GetSlot(typeId);
        if (CheckConfig.Enabled && (_ref._enabledBits & (1 << slot)) == 0)
        {
            ThrowHelper.ThrowInvalidOp($"Component {typeof(T).Name} at slot {slot} is disabled");
        }

        if (_ref._clusterBase != null)
        {
            // Versioned cluster: COW path
            if ((_ref._archetype.VersionedSlotMask & (1 << slot)) != 0)
            {
                var table = _ref._engineState.SlotToComponentTable[slot];
                var (newChunkId, rawPtr) = _ref._accessor.EcsVersionedCopyOnWrite(typeof(T), _ref._id, table, _ref.ChainRoot(slot));
                _ref.ApplyCopyOnWrite(slot, newChunkId);

                return ref Unsafe.AsRef<T>((byte*)rawPtr + table.ComponentOverhead);
            }

            // SV cluster fast path
            var clusterState = _ref._engineState.ClusterState;
            byte* svHeadPtr = _ref._clusterBase + _ref._clusterLayout.ComponentOffset(slot) + _ref._clusterSlotIndex * _ref._clusterLayout.ComponentSize(slot);
            var svTable = _ref._engineState.SlotToComponentTable[slot];

            // CM-02: a DefaultDiscipline=Commit component escalates the whole transaction to Commit on first touch.
            if (svTable.Discipline == CommitDiscipline.Commit)
            {
                _ref._accessor.ResolveCommitDiscipline(svTable);
            }

            // Commit discipline (Variant A): stage the write — HEAD untouched, no dirty/shadow (CM-01). Index reconciled at commit.
            if (_ref._accessor.Discipline == CommitDiscipline.Commit)
            {
                return ref _ref._accessor.StageClusterCommitWrite<T>(
                    svTable, typeId, (long)_ref._id.RawValue, _ref._clusterChunkId * 64 + _ref._clusterSlotIndex, svHeadPtr);
            }

            // Shadow capture for per-archetype B+Tree index maintenance (first write per entity per tick)
            if (clusterState.IndexSlots != null)
            {
                int entityIndex = _ref._clusterChunkId * 64 + _ref._clusterSlotIndex;
                if (!clusterState.ClusterShadowBitmap.TestAndSet(entityIndex))
                {
                    ShadowClusterIndexedFields(clusterState);
                }
            }

            _ref._accessor.NoteSvInPlaceWrite();   // CM-02: an in-place TickFence write happened — blocks late auto-escalation to Commit
            clusterState.SetDirty(_ref._clusterChunkId, _ref._clusterSlotIndex, slot);
            return ref Unsafe.AsRef<T>(svHeadPtr);
        }

        {
            int chunkId = _ref.GetLocation(slot);
            var table = _ref._engineState.SlotToComponentTable[slot];

            if (table.StorageMode == StorageMode.Versioned)
            {
                var (newChunkId, rawPtr) = _ref._accessor.EcsVersionedCopyOnWrite(typeof(T), _ref._id, table, _ref.ChainRoot(slot));
                _ref.ApplyCopyOnWrite(slot, newChunkId);

                return ref Unsafe.AsRef<T>((byte*)rawPtr + table.ComponentOverhead);
            }

            // CM-02: a DefaultDiscipline=Commit component escalates the whole tx to Commit before the (skipped) shadow capture below.
            if (table.StorageMode == StorageMode.SingleVersion && table.Discipline == CommitDiscipline.Commit)
            {
                _ref._accessor.ResolveCommitDiscipline(table);
            }

            // Commit discipline stages and reconciles indexes at commit — skip the per-tick shadow capture (which feeds the fence-time Move).
            // An own-spawn is skipped for two independent reasons. It has no OLD key to shadow: FinalizeSpawns inserts this entity's index entries fresh from
            // the final staged bytes, so there is no Move for the fence to perform — the same argument DIRTY-01 makes about the dirty bit. And since #839 the
            // location of an unpublished non-Versioned slot is a spawn-arena handle, which ShadowIndexedFields would dereference against the ComponentSegment,
            // reading an unrelated chunk's bytes as the "old key" and recording the handle as a chunk id for fence-time index maintenance.
            if (table.HasShadowableIndexes && _ref._accessor.Discipline != CommitDiscipline.Commit && !_ref._isOwnSpawn)
            {
                _ref._accessor.ShadowIndexedFields<T>(table, chunkId, _ref._id);
            }

            return ref _ref._accessor.WriteEcsComponentData<T>(table, chunkId, (long)_ref._id.RawValue, _ref._isOwnSpawn);
        }
    }

    /// <summary>
    /// Capture old indexed field values from cluster SoA for all indexed components.
    /// Called once per entity per tick, before the first write mutation.
    /// </summary>
    /// <remarks>
    /// Walks both index homes since #655. A slot's bytes live in the segment matching its storage mode, so the base is chosen per home rather than taken from
    /// <c>_clusterBase</c> for all of them — a Transient slot's key read off a MIXED archetype's cluster base would capture whatever the SoA holds at that
    /// offset, which is another component's data. That is silent: the shadow entry looks well-formed and the drain moves the index off a key the entity never
    /// had.
    /// </remarks>
    private void ShadowClusterIndexedFields(ArchetypeClusterState clusterState)
    {
        int entityIndex = _ref._clusterChunkId * 64 + _ref._clusterSlotIndex;

        // Skip every slot whose index is maintained at COMMIT rather than at the fence — Versioned always, and SingleVersion too when this transaction runs
        // under Commit discipline (its writes are staged and reconciled by PublishStagedCommitWrites). Capturing a commit-maintained slot is not merely
        // wasted work: the fence would then apply a SECOND Move for it, from a pre-write OldKey the commit publish has already moved away from, which
        // corrupts the entry rather than refreshing it (#711, the SvPlusTransient+Commit half).
        //
        // This mask MUST stay the exact complement of the one the destroy path removes inline — see Transaction.FlushEcsPendingOperations. The two
        // disagreeing is what #711 was.
        var skipMask = (ushort)~_ref._archetype.FenceMaintainedSlotsUnder(_ref._accessor.Discipline);
        CaptureIndexedSlots(clusterState.IndexSlots, _ref._clusterBase, entityIndex, skipMask);

        // A PURE-Transient archetype has no second base — _clusterBase already IS the Transient one (see the field comment on _transientClusterBase), so it
        // stays null here. Passing it straight through hit CaptureIndexedSlots' null guard and captured NOTHING, leaving the tree on the pre-mutation key for
        // the entity's whole lifetime: spawn indexed correctly, every later in-place write was invisible to the index.
        byte* transientBase = _ref._transientClusterBase != null ? _ref._transientClusterBase : _ref._clusterBase;
        CaptureIndexedSlots(clusterState.TransientIndexSlots, transientBase, entityIndex, skipMask);
    }

    /// <summary>
    /// Appends every indexed field of every non-skipped slot in <paramref name="slots"/> to its shadow buffer, reading from
    /// <paramref name="segmentBase"/>.
    /// </summary>
    private void CaptureIndexedSlots<TStore>(ClusterIndexSlot<TStore>[] slots, byte* segmentBase, int entityIndex, ushort skipMask)
        where TStore : struct, IPageStore
    {
        if (slots == null || segmentBase == null)
        {
            return;
        }

        for (int s = 0; s < slots.Length; s++)
        {
            ref var ixSlot = ref slots[s];

            if ((skipMask & (1 << ixSlot.Slot)) != 0)
            {
                continue;
            }

            int compSize = _ref._clusterLayout.ComponentSize(ixSlot.Slot);
            byte* compBase = segmentBase + _ref._clusterLayout.ComponentOffset(ixSlot.Slot) + _ref._clusterSlotIndex * compSize;

            for (int f = 0; f < ixSlot.Fields.Length; f++)
            {
                ref var field = ref ixSlot.Fields[f];
                var oldKey = KeyBytes8.FromPointer(compBase + field.FieldOffset, field.FieldSize);
                ixSlot.ShadowBuffers[f].Append(entityIndex, _ref._id, oldKey);
            }
        }
    }

    /// <summary>Disable a component by handle. Stages the change for commit.</summary>
    public void Disable<T>(Comp<T> comp) where T : unmanaged
    {
        byte slot = _ref._archetype.GetSlot(comp._componentTypeId);
        _ref._enabledBits &= (ushort)~(1 << slot);

        // Staged only. The cluster's EnabledBits copy is written at commit by FlushPendingEnableDisable, never from here: a write at staging reached every
        // concurrent bulk scan before commit and survived a rollback (#998, rule ENABLE-01).
        _ref._accessor.StageEnableDisable(_ref._id, _ref._enabledBits);
    }

    /// <summary>
    /// Supplies a value for a component and enables it, in one step.
    /// </summary>
    /// <remarks>
    /// The complement to <see cref="Enable{T}(Comp{T})"/>, not a replacement for it. The no-value overload re-enables a component that already has one —
    /// disabling preserves the payload, so a disable/enable round trip needs no value and must not demand one. This overload exists for the case that
    /// overload refuses: a component the spawn never supplied, which has no value to enable. Write is gated by the same EnabledBits as Read, so a caller
    /// cannot set the value first; enabling and supplying have to happen together (#845).
    /// </remarks>
    public void Enable<T>(Comp<T> comp, in T value) where T : unmanaged
    {
        byte slot = _ref._archetype.GetSlot(comp._componentTypeId);
        if (CheckConfig.Enabled && slot >= _ref._archetype.ComponentCount)
        {
            ThrowHelper.ThrowInvalidOp($"Component slot {slot} is out of range for archetype {_ref._archetype.ArchetypeId}");
        }

        var needsContent = _ref.IsVersionedSlotAbsent(slot);

        // A never-supplied Versioned slot has no chain, so Write's copy-on-write has nothing to copy from — it must be CREATED, not written. Everything else
        // (a re-enable, or any non-Versioned slot) already has storage, so the ordinary enable-then-write path applies.
        if (needsContent)
        {
            _ref.SetLocation(slot, _ref._accessor.CreateVersionedContentAndWrite(_ref._id, slot, in value));
        }

        // Enable AFTER the content exists: the bit is what makes the slot readable, so publishing it earlier would briefly expose a slot with no value.
        // Staged only — the cluster copy is written at commit (see Disable).
        _ref._enabledBits |= (ushort)(1 << slot);
        _ref._accessor.StageEnableDisable(_ref._id, _ref._enabledBits);

        if (!needsContent)
        {
            Write(comp) = value;
        }
    }

    /// <summary>Enable a component by handle. Stages the change for commit.</summary>
    public void Enable<T>(Comp<T> comp) where T : unmanaged
    {
        byte slot = _ref._archetype.GetSlot(comp._componentTypeId);
        if (CheckConfig.Enabled && slot >= _ref._archetype.ComponentCount)
        {
            ThrowHelper.ThrowInvalidOp($"Component slot {slot} is out of range for archetype {_ref._archetype.ArchetypeId}");
        }

        // #845: refuse to enable a Versioned component that has no value. A spawn allocates a slot's chunk and chain only for the components it supplies, so
        // an unsupplied one has no content at all and _locations stays 0 — the same "never written" state every chain-root reader in the engine already
        // recognises. Enabling it used to hand back whatever a recycled chunk held, which against a reused chunk is a DESTROYED entity's committed values.
        //
        // This is the case Disable/Enable cannot express: disabling preserves the payload, so a re-enable is fine and finds a non-zero location. There is no
        // way for a caller to initialise a never-supplied slot first, because Write is gated by the same EnabledBits as Read — which is why the old contract
        // (design decision #14) zero-initialised instead. Use the Enable(comp, in value) overload to supply the value and enable in one step.
        if (_ref.IsVersionedSlotAbsent(slot))
        {
            ThrowHelper.ThrowInvalidOp(
                $"Component at slot {slot} was never supplied for this entity, so it has no value to enable. "
              + "Use Enable(comp, in value) to supply one, or set it at Spawn.");
        }

        // Staged only — the cluster copy is written at commit (see Disable).
        _ref._enabledBits |= (ushort)(1 << slot);
        _ref._accessor.StageEnableDisable(_ref._id, _ref._enabledBits);
    }
}
