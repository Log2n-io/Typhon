using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Writable entity handle: everything <see cref="EntityRef"/> reads, plus <see cref="Set{T}(Comp{T}, in T)"/>, <see cref="Enable{T}(Comp{T})"/> and
/// <see cref="Disable{T}"/>. Returned by <c>OpenMut</c> / <c>TryOpenMut</c> on every accessor; <c>Open</c> / <c>TryOpen</c> return the read-only
/// <see cref="EntityRef"/>, which has no write member at all — writing through a read-only open does not compile (#997).
/// </summary>
/// <remarks>
/// <para>Holds one <see cref="EntityRef"/> and no other state: read members forward to it, write members work on its fields. A writable resolve fetches
/// the entity's cluster page with <c>GetChunkAddress(…, dirty: true)</c> and has run the accessor's mutation prep (<c>EnsureMutable</c> + <c>InProgress</c>
/// for a <see cref="Transaction"/>) — the preconditions the write paths below rely on.</para>
/// <para>Converts implicitly to <see cref="EntityRef"/>, so read-only helpers take an <see cref="EntityRef"/>. The conversion copies: a Versioned slot
/// resolved on the copy is not memoized back here, and a copy taken before a Versioned <see cref="Set{T}(Comp{T}, in T)"/> keeps reading the pre-write
/// revision.</para>
/// <para>Values go in and out by copy (#1199): no member hands out a reference into a page, so a handle stays safe across anything its transaction does
/// next — its cluster base is re-resolved when the accessor that resolved it has let go of the page.</para>
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

    /// <summary>The realm the entity is in (<see cref="EntityRef.Realm"/>).</summary>
    public readonly RealmId Realm => _ref.Realm;

    /// <inheritdoc cref="EntityRef.IsValid"/>
    public readonly bool IsValid => !_ref._id.IsNull;

    /// <inheritdoc cref="EntityRef.ComponentCount"/>
    public readonly int ComponentCount => _ref.ComponentCount;

    /// <inheritdoc cref="EntityRef.GetComponentName"/>
    public readonly string GetComponentName(int slot) => _ref.GetComponentName(slot);

    /// <inheritdoc cref="EntityRef.Read{T}(Comp{T})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly T Read<T>(Comp<T> comp) where T : unmanaged => _ref.Read(comp);

    /// <inheritdoc cref="EntityRef.Read{T}()"/>
    public readonly T Read<T>() where T : unmanaged => _ref.Read<T>();

    /// <inheritdoc cref="EntityRef.TryRead{T}(out T)"/>
    public readonly bool TryRead<T>(out T value) where T : unmanaged => _ref.TryRead(out value);

    /// <inheritdoc cref="EntityRef.TryRead{T}(Comp{T}, out T)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryRead<T>(Comp<T> comp, out T value) where T : unmanaged => _ref.TryRead(comp, out value);

    /// <inheritdoc cref="EntityRef.IsEnabled(byte)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsEnabled(byte slotIndex) => _ref.IsEnabled(slotIndex);

    /// <inheritdoc cref="EntityRef.IsEnabled{T}(Comp{T})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsEnabled<T>(Comp<T> comp) where T : unmanaged => _ref.IsEnabled(comp);

    /// <inheritdoc cref="EntityRef.GetComponentSize"/>
    public readonly int GetComponentSize(int slot) => _ref.GetComponentSize(slot);

    /// <inheritdoc cref="EntityRef.ReadRaw(int, Span{byte})"/>
    public readonly int ReadRaw(int slot, Span<byte> destination) => _ref.ReadRaw(slot, destination);

    /// <inheritdoc cref="EntityRef.ReadRaw(int)"/>
    public readonly ReadOnlySpan<byte> ReadRaw(int slot) => _ref.ReadRaw(slot);

    /// <summary>Marks this entity's slot in its cluster's per-tick structure word — see <c>EntityRef.NotePushed</c>.</summary>
    internal readonly void NotePushed() => _ref.NotePushed();

    // ═══════════════════════════════════════════════════════════════════════
    // Write side
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Set a component by handle: <paramref name="value"/> is copied in, and the component's bookkeeping (dirty bit, index shadow, page owed to disk) is done
    /// in the same call. No reference into the page is handed out, so nothing can be written through a stale one (#1199). To change one field, read the
    /// component, change the copy and set it back.
    /// <para>For Versioned: copy-on-write (allocates new chunk, preserves old for concurrent readers). For SingleVersion with indexes: shadows old field
    /// values on first write per tick for deferred index maintenance.</para>
    /// <para>On the component carrying the archetype's <c>[RealmKey]</c>, a value whose key differs from the stored one is a realm change, validated
    /// here as <see cref="EntityAccessor.Teleport{T}"/> validates it (RM-05): an unregistered, closing or incompatible realm, a Static archetype, or a
    /// non-finite position throws, and nothing is stored.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">A realm change the entity cannot make.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set<T>(Comp<T> comp, in T value) where T : unmanaged
    {
        if (_ref._archetype.RealmKeySlotMask == 0)
        {
            WriteRef(comp) = value;
            return;
        }
        SetOnRealmKeyedArchetype(comp._componentTypeId, in value);
    }

    /// <summary>Set a component by type: as <see cref="Set{T}(Comp{T}, in T)"/>, resolving the slot via archetype metadata.</summary>
    /// <exception cref="InvalidOperationException">A realm change the entity cannot make.</exception>
    public void Set<T>(in T value) where T : unmanaged
    {
        if (_ref._archetype.RealmKeySlotMask == 0)
        {
            WriteRef<T>() = value;
            return;
        }
        SetOnRealmKeyedArchetype(ArchetypeRegistry.GetComponentTypeId<T>(), in value);
    }

    /// <summary>
    /// <see cref="Set{T}(Comp{T}, in T)"/> on an archetype with a <c>[RealmKey]</c>. A store that changes the key moves the entity to another realm at the
    /// next fence: it gets <see cref="EntityAccessor.Teleport{T}"/>'s checks before anything is stored — the fence must never throw, so before D-2's
    /// revert this is the only place the application hears of a bad key — and, once stored, the flag a barrier-only archetype's fence needs to see the
    /// move.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SetOnRealmKeyedArchetype<T>(int componentTypeId, in T value) where T : unmanaged
    {
        var comp = new Comp<T>(componentTypeId);
        byte slot = _ref._archetype.GetSlot(componentTypeId);
        if ((_ref._archetype.RealmKeySlotMask & (1 << slot)) == 0)
        {
            WriteRef(comp) = value;
            return;
        }

        var state = _ref._engineState.ClusterState;
        ref readonly var spatial = ref state.SpatialSlot;
        var newRealm = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)), spatial.RealmKeyOffset));
        var oldRealm = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in _ref.ReadSlotRef<T>(slot))),
            spatial.RealmKeyOffset));
        if (newRealm == oldRealm)
        {
            WriteRef(comp) = value;
            return;
        }

        state.ValidateRealmEntry(newRealm);
        if (slot == spatial.Slot)
        {
            // The key rides in the spatial component: the value carries the position too, and the fence must be able to place it in a cell.
            var copy = value;   // on the stack: the centre is read through a pointer, which may never address managed memory
            SpatialGrid.ReadSpatialCenter3D((byte*)&copy + spatial.FieldOffset, spatial.FieldInfo.FieldType, out var x, out var y, out var z);
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            {
                throw new InvalidOperationException($"A realm change to a non-finite position ({x}, {y}, {z}) cannot be placed in any grid.");
            }
        }

        WriteRef(comp) = value;

        // An entity spawned in this transaction has no cluster yet: its placement reads the staged key at commit. Otherwise the slot is flagged as
        // Teleport flags it — a barrier-only archetype's fence runs no dirty scan and would never see the move.
        if (!_ref._isOwnSpawn)
        {
            state.FlagOutOfBarrierSpatialWrite(_ref._clusterChunkId, _ref._clusterSlotIndex);
        }
    }

    /// <summary>
    /// Zero-copy core of every write by handle: the bookkeeping, then a mutable reference to the component's storage. The reference is valid for the
    /// current call only, so it stays internal (#1199): <see cref="Set{T}(Comp{T}, in T)"/> stores through it at once.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T WriteRef<T>(Comp<T> comp) where T : unmanaged
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

        return ref WriteSlotRef<T>(slot, comp._componentTypeId);
    }

    /// <summary>
    /// Open a mutable accessor on a collection field of <paramref name="copy"/>, a copy of <paramref name="comp"/> read from this handle:
    /// <c>var v = e.Read(comp); using (var c = e.CreateComponentCollectionAccessor(comp, ref v, ref v.Items)) { c.Add(x); }</c>
    /// </summary>
    /// <remarks>
    /// <para>For a Versioned component the new revision is created here, before the accessor exists. That is what makes the buffer shared, so the
    /// accessor clones it before changing it. Mutating a copy read before the revision existed would otherwise edit, in place, the buffer the committed
    /// revision still points to, and readers of that revision would see the change (#1199).</para>
    /// <para>On dispose the accessor stores the buffer it ended on into the component, and into <paramref name="field"/>: the collection needs no
    /// <see cref="Set{T}(Comp{T}, in T)"/>. Set the copy only for its other fields — it then carries the same buffer.</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="field"/> is not a field of <paramref name="copy"/>.</exception>
    public ComponentCollectionAccessor<TElem> CreateComponentCollectionAccessor<T, TElem>(Comp<T> comp, ref T copy, ref ComponentCollection<TElem> field)
        where T : unmanaged where TElem : unmanaged
    {
        var offset = (int)Unsafe.ByteOffset(ref Unsafe.As<T, byte>(ref copy), ref Unsafe.As<ComponentCollection<TElem>, byte>(ref field));
        if (offset < 0 || offset > sizeof(T) - sizeof(ComponentCollection<TElem>))
        {
            throw new ArgumentException("The collection must be a field of the copy passed with it.", nameof(field));
        }

        byte slot = _ref._archetype.GetSlot(comp._componentTypeId);
        if (_ref._engineState.SlotToComponentTable[slot].StorageMode == StorageMode.Versioned)
        {
            // The copy-on-write alone: it adds a reference to every buffer the new revision shares with the committed one. Idempotent within a transaction.
            WriteRef(comp);
        }

        return _ref._accessor.CreateComponentCollectionAccessorCore(ref field, _ref._id, comp._componentTypeId, offset, &StoreCollectionBufferId<T>);
    }

    /// <summary>
    /// Store <paramref name="bufferId"/> into the collection field at <paramref name="fieldOffset"/> of the entity's component: the write-back of an accessor
    /// made by <see cref="CreateComponentCollectionAccessor{T, TElem}"/>. Through a fresh writable open, so the store gets the component's bookkeeping;
    /// the copy-on-write is already done, so a Versioned component writes into the same new revision.
    /// </summary>
    private static void StoreCollectionBufferId<T>(EntityAccessor owner, EntityId entity, int componentTypeId, int fieldOffset, int bufferId)
        where T : unmanaged
    {
        var handle = owner.OpenMut(entity);
        ref var stored = ref handle.WriteRef(new Comp<T>(componentTypeId));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref Unsafe.As<T, byte>(ref stored), fieldOffset), bufferId);
    }

    /// <summary>
    /// The by-type twin of <see cref="WriteRef{T}(Comp{T})"/>: same core, so a Transient slot of a mixed archetype is written in its own segment.
    /// </summary>
    internal ref T WriteRef<T>() where T : unmanaged
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

        return ref WriteSlotRef<T>(slot, typeId);
    }

    /// <summary>The write paths of every storage mode, for a validated <paramref name="slot"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref T WriteSlotRef<T>(byte slot, int componentTypeId) where T : unmanaged
    {
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
            // The page was mapped dirty at resolve time (OpenMut → GetChunkAddress(dirty:true)), but through an accessor without a ChangeSet, which records
            // nothing: the write records its page below (PS-10, #1172).
            // The base through ClusterBase(): the cached one may sit in a page the resolving accessor has let go of since (#1199).
            byte* svHeadPtr = _ref.ClusterBase() + _ref._clusterLayout.ComponentOffset(slot) + _ref._clusterSlotIndex * _ref._clusterLayout.ComponentSize(slot);
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
                    svTable, componentTypeId, (long)_ref._id.RawValue, _ref._clusterChunkId * 64 + _ref._clusterSlotIndex, svHeadPtr);
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
            // The page owes a write (PS-10): unrecorded, an eviction before the fence reloads the old value (#1172).
            _ref._accessor.NoteSvInPlacePageWrite(clusterState, svHeadPtr);
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
        // Through ClusterBase(): a Transient write lands here without having refreshed the primary base, which may be stale (#1199).
        var clusterBase = _ref.ClusterBase();
        CaptureIndexedSlots(clusterState.IndexSlots, clusterBase, entityIndex, skipMask);

        // A PURE-Transient archetype has no second base — _clusterBase already IS the Transient one (see the field comment on _transientClusterBase), so it
        // stays null here. Passing it straight through hit CaptureIndexedSlots' null guard and captured NOTHING, leaving the tree on the pre-mutation key for
        // the entity's whole lifetime: spawn indexed correctly, every later in-place write was invisible to the index.
        byte* transientBase = _ref._transientClusterBase != null ? _ref._transientClusterBase : clusterBase;
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
            WriteRef(comp) = value;
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
