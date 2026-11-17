using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    /// <inheritdoc cref="EntityRef.ReadText{T}(Comp{T}, in String1024)"/>
    public readonly string ReadText<T>(Comp<T> comp, in String1024 field) where T : unmanaged => _ref.ReadText(comp, in field);

    /// <inheritdoc cref="EntityRef.ReadText{T}(Comp{T}, in String1024, Span{byte})"/>
    public readonly int ReadText<T>(Comp<T> comp, in String1024 field, Span<byte> utf8) where T : unmanaged => _ref.ReadText(comp, in field, utf8);

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
    /// <para>A <see cref="ComponentCollection{T}"/> field is not set: the component keeps its collection, which changes through
    /// <see cref="CreateComponentCollectionAccessor{T, TElem}"/> alone. An empty handle in <paramref name="value"/> is ignored; one naming another buffer
    /// throws.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">A realm change the entity cannot make, or a value naming a collection buffer the entity does not
    /// hold.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set<T>(Comp<T> comp, in T value) where T : unmanaged
    {
        // Only the realm key and the spatial component take the checked path: a spatial archetype's other components stay inline.
        if ((_ref._archetype.CheckedSetSlotMask & (1 << _ref._archetype.GetSlot(comp._componentTypeId))) == 0)
        {
            Store(comp, in value);
            return;
        }
        SetChecked(comp._componentTypeId, in value);
    }

    /// <summary>
    /// The store of every <c>Set</c>: the value goes down with the write, so a value that keeps every fence-maintained indexed key of the component records
    /// none (#1346). A collection field keeps the buffer the stored component names (<see cref="CollectionHandles.Keep"/>, #1362).
    /// </summary>
    /// <param name="comp">The component.</param>
    /// <param name="value">The value.</param>
    /// <param name="adoptIntoEmpty">The store supplies a component that had no value: a collection the stored one lacks is adopted.</param>
    private void Store<T>(Comp<T> comp, in T value, bool adoptIntoEmpty = false) where T : unmanaged
    {
        var copy = value;   // on the stack: value may live in a managed array, and a pointer is taken only over the stack
        byte slot = _ref._archetype.GetSlot(comp._componentTypeId);
        ref var stored = ref WriteSlot<T>(slot, comp._componentTypeId, (byte*)&copy);
        if ((_ref._archetype.CollectionSlotMask & (1 << slot)) != 0)
        {
            // Out of line: inlined, the rule's body cost every Set ~1.7 ns on a SingleVersion component, collection or not (#1362, measured).
            KeepCollectionHandlesCold(slot, (byte*)&copy, (byte*)Unsafe.AsPointer(ref stored), adoptIntoEmpty);
        }
        stored = copy;
    }

    /// <summary>
    /// A whole-value store's collection rule (<see cref="CollectionHandles.Keep"/>): <paramref name="value"/>, about to be stored over
    /// <paramref name="stored"/> at <paramref name="slot"/>, gets the stored collection handles. Nothing on a component without a collection.
    /// </summary>
    internal void KeepCollectionHandles<T>(byte slot, byte* value, ref T stored, bool adoptIntoEmpty) where T : unmanaged
    {
        if ((_ref._archetype.CollectionSlotMask & (1 << slot)) != 0)
        {
            KeepCollectionHandlesCold(slot, value, (byte*)Unsafe.AsPointer(ref stored), adoptIntoEmpty);
        }
    }

    /// <summary>The body of <see cref="KeepCollectionHandles{T}"/>. <paramref name="stored"/> is engine-owned memory (a page, the staging buffer, the spawn
    /// arena), so a pointer over it is allowed.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void KeepCollectionHandlesCold(byte slot, byte* value, byte* stored, bool adoptIntoEmpty)
    {
        CollectionHandles.Keep(_ref._engineState.SlotToComponentTable[slot], value, stored, adoptIntoEmpty);
    }

    /// <summary>Set a component by type: as <see cref="Set{T}(Comp{T}, in T)"/>, resolving the slot via archetype metadata.</summary>
    /// <exception cref="InvalidOperationException">A realm change the entity cannot make.</exception>
    public void Set<T>(in T value) where T : unmanaged
    {
        if (_ref._archetype.CheckedSetSlotMask == 0)
        {
            var typeId = ArchetypeRegistry.GetComponentTypeId<T>();
            if (CheckConfig.Enabled && typeId < 0)
            {
                ThrowHelper.ThrowInvalidOp($"Component type {typeof(T).Name} not registered");
            }

            Store(new Comp<T>(typeId), in value);
            return;
        }
        SetChecked(ArchetypeRegistry.GetComponentTypeId<T>(), in value);
    }

    /// <summary>
    /// <see cref="Set{T}(Comp{T}, in T)"/> on a spatial or realm-keyed archetype. A position must be finite, or the fence could not place it in a cell and
    /// would fail terminally (CA-01, #1652): refused at the call, nothing stored. A store that changes the realm key moves the entity to another realm at
    /// the next fence: it gets <see cref="EntityAccessor.Teleport{T}"/>'s checks before anything is stored — the fence must never throw, so before D-2's
    /// revert this is the only place the application hears of a bad key — and, once stored, the flag a barrier-only archetype's fence needs to see the
    /// move. A position stored on a barrier-only archetype (<see cref="DatabaseEngine.SetSpatialBarrierOnly{TArch}"/>) gets the barrier's bookkeeping
    /// (<see cref="StoreSpatial{T}"/>): its fence runs no dirty scan, and an unrecorded move would leave the entity where no query finds it, neither at its
    /// old place nor at its new one.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SetChecked<T>(int componentTypeId, in T value, bool adoptIntoEmpty = false) where T : unmanaged
    {
        var comp = new Comp<T>(componentTypeId);
        byte slot = _ref._archetype.GetSlot(componentTypeId);
        var isSpatial = (_ref._archetype.SpatialSlotMask & (1 << slot)) != 0;
        if (isSpatial)
        {
            RefuseNonFinitePosition(in value);
        }
        if ((_ref._archetype.RealmKeySlotMask & (1 << slot)) == 0)
        {
            StoreChecked(comp, slot, isSpatial, in value, adoptIntoEmpty);
            return;
        }

        var state = _ref._engineState.ClusterState;
        ref readonly var spatial = ref state.SpatialSlot;
        var newRealm = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)), spatial.RealmKeyOffset));
        var oldRealm = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in _ref.ReadSlotRef<T>(slot))),
            spatial.RealmKeyOffset));
        if (newRealm == oldRealm)
        {
            StoreChecked(comp, slot, isSpatial, in value, adoptIntoEmpty);
            return;
        }

        state.ValidateRealmEntry(newRealm);   // a key riding in the spatial component: its position was checked above
        Store(comp, in value, adoptIntoEmpty);

        // An entity spawned in this transaction has no cluster yet: its placement reads the staged key at commit. Otherwise the slot is flagged as
        // Teleport flags it — a barrier-only archetype's fence runs no dirty scan and would never see the move.
        if (!_ref._isOwnSpawn)
        {
            state.FlagOutOfBarrierSpatialWrite(_ref._clusterChunkId, _ref._clusterSlotIndex);
        }
    }

    /// <summary>The store of a checked slot that changes no realm: <see cref="StoreSpatial{T}"/> for the spatial component, the plain store
    /// otherwise.</summary>
    private void StoreChecked<T>(Comp<T> comp, byte slot, bool isSpatial, in T value, bool adoptIntoEmpty) where T : unmanaged
    {
        if (isSpatial)
        {
            StoreSpatial(comp, slot, in value, adoptIntoEmpty);
        }
        else
        {
            Store(comp, in value, adoptIntoEmpty);
        }
    }

    /// <summary>
    /// The store of a position. On a barrier-only archetype it is followed by the bookkeeping <c>ClusterRef.WriteSpatial</c> does
    /// (<see cref="ArchetypeClusterState.ApplySpatialWrite"/>, CA-01, CA-02): the cluster's bound grows at once, a shrink and a crossing past the hysteresis
    /// margin are flagged for the fence, which hands the bound to the per-cell index, and the cluster is noted for dormancy. Nothing more
    /// on another archetype, whose fence finds the move by its dirty scan; nor for an entity spawned in this transaction, which has no cluster yet; nor on a
    /// Static archetype, whose fence moves nothing.
    /// </summary>
    /// <remarks>
    /// The barrier compares the old value with the new one, and only an in-place store has both: a Commit-discipline store is staged, the cluster HEAD keeps
    /// the old value, and the publish that lands it flags the slot (CC-02, <c>Transaction.PublishStagedEntry</c>). A Versioned spatial component lands at
    /// commit too; it is flagged here as <see cref="EntityAccessor.Teleport{T}"/> flags it.
    /// </remarks>
    private void StoreSpatial<T>(Comp<T> comp, byte slot, in T value, bool adoptIntoEmpty) where T : unmanaged
    {
        var state = _ref._engineState.ClusterState;
        if (!state.SpatialBarrierOnly || _ref._isOwnSpawn || state.SpatialSlot.FieldInfo.Mode != SpatialMode.Dynamic)
        {
            Store(comp, in value, adoptIntoEmpty);
            return;
        }

        var chunkId = _ref._clusterChunkId;
        if ((_ref._archetype.VersionedSlotMask & (1 << slot)) != 0)
        {
            Store(comp, in value, adoptIntoEmpty);
            state.FlagOutOfBarrierSpatialWrite(chunkId, _ref._clusterSlotIndex);
            return;
        }

        var old = _ref.ReadSlotRef<T>(slot);   // a copy, on the stack: the value the store replaces, read before it
        Store(comp, in value, adoptIntoEmpty);
        if (_ref._accessor.Discipline == CommitDiscipline.Commit)
        {
            return;   // staged (possibly escalated by this very store): the publish flags it
        }

        switch (state.SpatialSlot.FieldInfo.FieldType)
        {
            case SpatialFieldType.AABB2F:
                ApplyBarrierBookkeeping<T, AabbClusterEnumerator.Aabb2FReader>(state, old, value);
                break;
            case SpatialFieldType.AABB3F:
                ApplyBarrierBookkeeping<T, AabbClusterEnumerator.Aabb3FReader>(state, old, value);
                break;
            case SpatialFieldType.BSphere2F:
                ApplyBarrierBookkeeping<T, AabbClusterEnumerator.BSphere2FReader>(state, old, value);
                break;
            case SpatialFieldType.BSphere3F:
                ApplyBarrierBookkeeping<T, AabbClusterEnumerator.BSphere3FReader>(state, old, value);
                break;
            case SpatialFieldType.AABB2D:
                ApplyBarrierBookkeeping<T, AabbClusterEnumerator.Aabb2DReader>(state, old, value);
                break;
            case SpatialFieldType.AABB3D:
                ApplyBarrierBookkeeping<T, AabbClusterEnumerator.Aabb3DReader>(state, old, value);
                break;
            case SpatialFieldType.BSphere2D:
                ApplyBarrierBookkeeping<T, AabbClusterEnumerator.BSphere2DReader>(state, old, value);
                break;
            case SpatialFieldType.BSphere3D:
                ApplyBarrierBookkeeping<T, AabbClusterEnumerator.BSphere3DReader>(state, old, value);
                break;
            default:
                throw new InvalidOperationException(
                    $"Set: spatial field type {state.SpatialSlot.FieldInfo.FieldType} has no specialization. Add one when a new SpatialFieldType variant is "
                    + "introduced.");
        }
    }

    /// <summary>
    /// <see cref="StoreSpatial{T}"/>'s bookkeeping, compiled once per field type: <typeparamref name="TReader"/> is the decode the slot-set
    /// <c>ClusterRef.WriteSpatial</c> inlines, so <c>is3D</c> is a constant and a 2D tier's Z arms fold away as they do in <c>WriteSpatial</c>.
    /// <paramref name="old"/> and <paramref name="current"/> are by value: on the stack, the only memory a pointer may address here.
    /// </summary>
    private void ApplyBarrierBookkeeping<T, TReader>(ArchetypeClusterState state, T old, T current)
        where T : unmanaged where TReader : struct, AabbClusterEnumerator.IBoundsReader
    {
        var fieldOffset = state.SpatialSlot.FieldOffset;
        TReader.Read((byte*)&old + fieldOffset, out var oMinX, out var oMinY, out var oMinZ, out var oMaxX, out var oMaxY, out var oMaxZ);
        TReader.Read((byte*)&current + fieldOffset, out var nMinX, out var nMinY, out var nMinZ, out var nMaxX, out var nMaxY, out var nMaxZ);
        var chunkId = _ref._clusterChunkId;
        state.NoteSpatialWrite(chunkId);
        var haveOrigin = state.TryResolveCellFrame(chunkId, out var cellKey, out var grid, out var originX, out var originY, out var originZ);
        state.ApplySpatialWrite(chunkId, _ref._clusterSlotIndex, haveOrigin, cellKey, grid, originX, originY, originZ,
            oMinX, oMinY, oMinZ, oMaxX, oMaxY, oMaxZ, nMinX, nMinY, nMinZ, nMaxX, nMaxY, nMaxZ, TReader.Is3D);
    }

    /// <summary>
    /// Refuses a value of the spatial component whose position is not finite (CA-01): the fence places a moved entity in a cell, and a NaN or infinite
    /// centre has none — found there, it fails the fence for good. The same check as <see cref="EntityAccessor.Teleport{T}"/>'s.
    /// </summary>
    private void RefuseNonFinitePosition<T>(in T value) where T : unmanaged
    {
        ref readonly var spatial = ref _ref._engineState.ClusterState.SpatialSlot;
        var copy = value;   // on the stack: the centre is read through a pointer, which may never address managed memory
        SpatialGrid.ReadSpatialCenter3D((byte*)&copy + spatial.FieldOffset, spatial.FieldInfo.FieldType, out var x, out var y, out var z);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            throw new InvalidOperationException($"A position ({x}, {y}, {z}) that is not finite cannot be placed in any cell (CA-01): nothing was stored.");
        }
    }

    /// <summary>
    /// Zero-copy core of every write by handle: the bookkeeping, then a mutable reference to the component's storage. The reference is valid for the
    /// current call only, so it stays internal (#1199): <see cref="Set{T}(Comp{T}, in T)"/> stores through it at once.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T WriteRef<T>(Comp<T> comp) where T : unmanaged => ref WriteRef(comp, null);

    /// <summary><see cref="WriteRef{T}(Comp{T})"/> for a store of <paramref name="newValue"/> (on the stack), or of an unknown value when null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref T WriteRef<T>(Comp<T> comp, byte* newValue) where T : unmanaged =>
        ref WriteSlot<T>(_ref._archetype.GetSlot(comp._componentTypeId), comp._componentTypeId, newValue);

    /// <summary><see cref="WriteRef{T}(Comp{T}, byte*)"/> for a slot the caller has resolved.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref T WriteSlot<T>(byte slot, int componentTypeId, byte* newValue) where T : unmanaged
    {
        SystemAccessValidator.AssertWrite<T>(_ref._accessor);
        if (_ref._accessor is { GuardActive: true } guarded)
        {
            TickWriteGuard.CheckSlotWrite(guarded, _ref._archetype, _ref._engineState, slot);   // EW-01/EW-02 (#1651)
        }
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

        return ref WriteSlotRef<T>(slot, componentTypeId, newValue);
    }

    /// <summary>
    /// Open a mutable accessor on a collection field of <paramref name="copy"/>, a copy of <paramref name="comp"/> read from this handle:
    /// <c>var v = e.Read(comp); using (var c = e.CreateComponentCollectionAccessor(comp, ref v, ref v.Items)) { c.Add(x); }</c>
    /// </summary>
    /// <remarks>
    /// <para>For a Versioned component the new revision is created here, before the accessor exists. That is what makes the buffer shared, so the
    /// accessor clones it before changing it. Mutating a copy read before the revision existed would otherwise edit, in place, the buffer the committed
    /// revision still points to, and readers of that revision would see the change (#1199).</para>
    /// <para>Every buffer the accessor moves to — the first allocation, a clone, a <c>Replace</c> — is stored into the component at once, and into
    /// <paramref name="field"/>: the collection needs no <see cref="Set{T}(Comp{T}, in T)"/>. Set the copy only for its other fields — it then carries the
    /// same buffer.</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="field"/> is not a field of <paramref name="copy"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="copy"/> names another collection than the stored component: read before the collection
    /// last changed.</exception>
    public ComponentCollectionAccessor<TElem> CreateComponentCollectionAccessor<T, TElem>(Comp<T> comp, ref T copy, ref ComponentCollection<TElem> field)
        where T : unmanaged where TElem : unmanaged
    {
        var offset = (int)Unsafe.ByteOffset(ref Unsafe.As<T, byte>(ref copy), ref Unsafe.As<ComponentCollection<TElem>, byte>(ref field));
        if (offset < 0 || offset > sizeof(T) - sizeof(ComponentCollection<TElem>))
        {
            throw new ArgumentException("The collection must be a field of the copy passed with it.", nameof(field));
        }

        // The write is recorded here, before the accessor exists, in every storage mode (#1362). Versioned: the copy-on-write, which adds a reference to every
        // buffer the new revision shares with the committed one. SingleVersion: the dirty bit and the page owed to disk the fence logs from, or under the
        // Commit discipline the staged slot, whose staging adds a reference to every buffer it shares with HEAD. Only Versioned used to get it, so an in-place
        // SingleVersion append was never logged, and under the Commit discipline edited the committed buffer itself. Idempotent within a transaction. The
        // current value goes down with it: its keys are the stored ones, so a fence-indexed component records none (#1346).
        var current = Read(comp);
        byte slot = _ref._archetype.GetSlot(comp._componentTypeId);
        ref var stored = ref WriteSlot<T>(slot, comp._componentTypeId, (byte*)&current);

        // The copy must name the stored collection. A stale one — read before an accessor moved it — names a buffer whose reference is no longer this
        // value's: appended to, it edits a buffer an older revision owns; replaced, it releases a reference this value does not hold.
        var storedBufferId = Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref Unsafe.As<T, byte>(ref stored), offset));
        if (field._bufferId != storedBufferId)
        {
            ThrowHelper.ThrowInvalidOp($"The copy of '{typeof(T).Name}' names collection buffer {field._bufferId}, the entity holds {storedBufferId}: read the "
                + "component again before opening its collection.");
        }

        return _ref._accessor.CreateComponentCollectionAccessorCore(ref field, _ref._id, comp._componentTypeId, offset, &StoreCollectionBufferId<T>);
    }

    /// <summary>
    /// Store <paramref name="text"/> into the <see cref="String1024"/> field <paramref name="field"/> of <paramref name="copy"/>, a copy of
    /// <paramref name="comp"/> read from this handle: <c>var v = e.Read(comp); e.SetText(comp, ref v, ref v.Body, "hello");</c>
    /// </summary>
    /// <remarks>
    /// <para>The text is stored into the entity's component at once, and its handle into <paramref name="field"/>: the text needs no
    /// <see cref="Set{T}(Comp{T}, in T)"/>. Set the copy only for its other fields — it then carries the same handle.</para>
    /// <para>Text longer than <see cref="String1024.MaxUtf8Bytes"/> UTF-8 bytes is cut on a code-point boundary. A Versioned component, or a SingleVersion one
    /// under the Commit discipline, gets the new text in a new chunk: readers of the committed value keep reading the old one (#1094).</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="field"/> is not a field of <paramref name="copy"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="copy"/> names another text than the stored component: read before the text last
    /// changed.</exception>
    public void SetText<T>(Comp<T> comp, ref T copy, ref String1024 field, ReadOnlySpan<char> text) where T : unmanaged
    {
        Span<byte> utf8 = stackalloc byte[String1024.MaxUtf8Bytes];
        var length = TextChunks.Encode(text, utf8);
        StoreTextCore(comp, ref copy, ref field, utf8[..length], false);
    }

    /// <summary>
    /// The UTF-8 overload of <see cref="SetText{T}(Comp{T}, ref T, ref String1024, ReadOnlySpan{char})"/>: <paramref name="utf8"/> ends at its first NUL,
    /// and is cut on a code-point boundary past <see cref="String1024.MaxUtf8Bytes"/> bytes.
    /// </summary>
    public void SetText<T>(Comp<T> comp, ref T copy, ref String1024 field, ReadOnlySpan<byte> utf8) where T : unmanaged =>
        StoreTextCore(comp, ref copy, ref field, utf8[..TextChunks.FitUtf8(utf8)], false);

    /// <summary>Remove the text of the <see cref="String1024"/> field <paramref name="field"/> of <paramref name="copy"/>: its chunk is given back, the
    /// handle becomes empty.</summary>
    /// <exception cref="ArgumentException"><paramref name="field"/> is not a field of <paramref name="copy"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="copy"/> names another text than the stored component.</exception>
    public void ClearText<T>(Comp<T> comp, ref T copy, ref String1024 field) where T : unmanaged => StoreTextCore(comp, ref copy, ref field, default, true);

    private void StoreTextCore<T>(Comp<T> comp, ref T copy, ref String1024 field, scoped ReadOnlySpan<byte> utf8, bool clear) where T : unmanaged
    {
        var offset = (int)Unsafe.ByteOffset(ref Unsafe.As<T, byte>(ref copy), ref Unsafe.As<String1024, byte>(ref field));
        if (offset < 0 || offset > sizeof(T) - sizeof(String1024))
        {
            throw new ArgumentException("The text must be a field of the copy passed with it.", nameof(field));
        }

        // The write is recorded first, as for a collection: the copy-on-write revision, the staged slot, or the dirty bit and the page owed to disk. The text
        // is then stored into THAT value, which is what the log, the publish and the revision chain see.
        var current = Read(comp);
        byte slot = _ref._archetype.GetSlot(comp._componentTypeId);
        ref var stored = ref WriteSlot<T>(slot, comp._componentTypeId, (byte*)&current);
        var storedPtr = (byte*)Unsafe.AsPointer(ref stored);

        var storedHandle = TextChunks.HandleAt(storedPtr, offset);
        if (field._chunkId != storedHandle)
        {
            ThrowHelper.ThrowInvalidOp($"The copy of '{typeof(T).Name}' names text chunk {field._chunkId}, the entity holds {storedHandle}: read the "
                + "component again before changing its text.");
        }

        field._chunkId = _ref._accessor.StoreText(_ref._engineState.SlotToComponentTable[slot], (long)_ref._id.RawValue, storedPtr, offset, utf8, clear);
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
        var comp = new Comp<T>(componentTypeId);
        var current = handle.Read(comp);   // a handle moves no indexed key: the write records none (#1346)
        ref var stored = ref handle.WriteRef(comp, (byte*)&current);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref Unsafe.As<T, byte>(ref stored), fieldOffset), bufferId);
    }

    /// <summary>
    /// The by-type twin of <see cref="WriteRef{T}(Comp{T})"/>: same core, so a Transient slot of a mixed archetype is written in its own segment.
    /// </summary>
    internal ref T WriteRef<T>() where T : unmanaged
    {
        SystemAccessValidator.AssertWrite<T>(_ref._accessor);
        int typeId = ArchetypeRegistry.GetComponentTypeId<T>();
        if (CheckConfig.Enabled && typeId < 0)
        {
            ThrowHelper.ThrowInvalidOp($"Component type {typeof(T).Name} not registered");
        }
        byte slot = _ref._archetype.GetSlot(typeId);
        if (_ref._accessor is { GuardActive: true } guarded)
        {
            TickWriteGuard.CheckSlotWrite(guarded, _ref._archetype, _ref._engineState, slot);   // EW-01/EW-02 (#1651)
        }
        if (CheckConfig.Enabled && (_ref._enabledBits & (1 << slot)) == 0)
        {
            ThrowHelper.ThrowInvalidOp($"Component {typeof(T).Name} at slot {slot} is disabled");
        }

        return ref WriteSlotRef<T>(slot, typeId, null);
    }

    /// <summary>The write paths of every storage mode, for a validated <paramref name="slot"/>.</summary>
    /// <param name="slot">The validated component slot.</param>
    /// <param name="componentTypeId">The component's type id.</param>
    /// <param name="newValue">
    /// The value the caller is about to store, on the stack, or <c>null</c> when unknown (a returned ref). A known value that keeps every fence-maintained
    /// indexed key of the component records none (#1346); an unknown one records them on the entity's first write, as before.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref T WriteSlotRef<T>(byte slot, int componentTypeId, byte* newValue) where T : unmanaged
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
                // Shadow capture for the fence-maintained indexed fields (first write per entity per tick), only when the written component carries one:
                // a write to another component leaves the tree's keys in place, and a later write that does change one records them then (#1344). The
                // same holds for a value that keeps every key of this one (#1346).
                var transientPtr = _ref._transientClusterBase + _ref._clusterLayout.ComponentOffset(slot)
                                   + _ref._clusterSlotIndex * _ref._clusterLayout.ComponentSize(slot);
                if ((clusterState.FenceIndexedSlotMask & (1 << slot)) != 0
                    && (newValue == null || clusterState.IndexedKeysDiffer(slot, transientPtr, newValue)))
                {
                    ShadowClusterIndexedFields(clusterState);
                }
                clusterState.SetDirty(_ref._clusterChunkId, _ref._clusterSlotIndex, slot);
                return ref Unsafe.AsRef<T>(transientPtr);
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

            // Shadow capture for per-archetype B+Tree index maintenance (first write per entity per tick), only for a component with a fence-maintained
            // index (#1344) and a value that changes one of its keys (#1346), as the Transient arm above.
            if ((clusterState.FenceIndexedSlotMask & (1 << slot)) != 0 && (newValue == null || clusterState.IndexedKeysDiffer(slot, svHeadPtr, newValue)))
            {
                ShadowClusterIndexedFields(clusterState);
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
            // An own-spawn is skipped for two independent reasons. It has no OLD key to shadow: PublishSpawns inserts this entity's index entries fresh from
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
    /// Capture old indexed field values from cluster SoA for all indexed components, on the entity's first write this tick
    /// (<see cref="ArchetypeClusterState.CaptureFirstWrite"/>).
    /// </summary>
    /// <remarks>
    /// Walks both index homes since #655. A slot's bytes live in the segment matching its storage mode, so the base is chosen per home rather than taken from
    /// <c>_clusterBase</c> for all of them — a Transient slot's key read off a MIXED archetype's cluster base would capture whatever the SoA holds at that
    /// offset, which is another component's data. That is silent: the shadow entry looks well-formed and the drain moves the index off a key the entity never
    /// had. A PURE-Transient archetype has no second base (<c>_transientClusterBase</c> is null, <c>_clusterBase</c> IS the Transient one); the helper falls
    /// back to the primary base, without which it captured nothing and the tree kept the spawn key for the entity's whole lifetime.
    /// </remarks>
    private void ShadowClusterIndexedFields(ArchetypeClusterState clusterState)
    {
        // An entity another commit spawned is visible from its card, before that commit's PUBLISH indexes it from the slot's bytes (VIS-08). Written now,
        // the spawn indexed the new value and the fence then moved it from the old one, which the tree never held: two entries (#1370).
        clusterState.WaitSlotIndexesUnlocked(_ref._clusterChunkId, _ref._clusterSlotIndex);
        // Records the fence-maintained indexed slots — never a Versioned one, whose index moves at commit (a second Move from the fence corrupts the entry,
        // #711), nor a SingleVersion one under the Commit discipline (#1339). The destroy path defers exactly what was recorded (IX-06).
        // Through ClusterBase(): a Transient write lands here without having refreshed the primary base, which may be stale (#1199).
        clusterState.CaptureFirstWrite(_ref._clusterChunkId, _ref._clusterSlotIndex, _ref._id, _ref.ClusterBase(), _ref._transientClusterBase,
            _ref._accessor.Discipline);
    }

    /// <summary>Disable a component by handle. Stages the change for commit.</summary>
    public void Disable<T>(Comp<T> comp) where T : unmanaged
    {
        byte slot = _ref._archetype.GetSlot(comp._componentTypeId);
        // An enable or disable writes the component's slot and, at commit, the cluster's enabled bits: DV-01 (#1650) and the off-tick guard (#1651)
        SystemAccessValidator.AssertWrite<T>(_ref._accessor);
        if (_ref._accessor is { GuardActive: true } guarded)
        {
            TickWriteGuard.CheckStructural(guarded, _ref._archetype, "enabled or disabled a component of");
        }
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
        // An enable or disable writes the component's slot and, at commit, the cluster's enabled bits: DV-01 (#1650) and the off-tick guard (#1651)
        SystemAccessValidator.AssertWrite<T>(_ref._accessor);
        if (_ref._accessor is { GuardActive: true } guarded)
        {
            TickWriteGuard.CheckStructural(guarded, _ref._archetype, "enabled or disabled a component of");
        }
        if (CheckConfig.Enabled && slot >= _ref._archetype.ComponentCount)
        {
            ThrowHelper.ThrowInvalidOp($"Component slot {slot} is out of range for archetype {_ref._archetype.ArchetypeId}");
        }

        var needsContent = _ref.IsVersionedSlotAbsent(slot);
        var isChecked = (_ref._archetype.CheckedSetSlotMask & (1 << slot)) != 0;
        if (isChecked && (_ref._archetype.SpatialSlotMask & (1 << slot)) != 0)
        {
            RefuseNonFinitePosition(in value);   // before anything is created or staged, as Set refuses it (CA-01)
        }

        // A never-supplied Versioned slot has no chain, so Write's copy-on-write has nothing to copy from — it must be CREATED, not written. Everything else
        // (a re-enable, or any non-Versioned slot) already has storage, so the ordinary enable-then-write path applies.
        if (needsContent)
        {
            // A first supply, as a spawn's value: it may carry no text handle, which could only be another entity's (TXT-02, #1652).
            var table = _ref._engineState.SlotToComponentTable[slot];
            if (table.HasText)
            {
                CollectionHandles.RefuseSpawnedText(table, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));
            }
            _ref.SetLocation(slot, _ref._accessor.CreateVersionedContentAndWrite(_ref._id, slot, in value));
        }

        // Enable AFTER the content exists: the bit is what makes the slot readable, so publishing it earlier would briefly expose a slot with no value.
        // Staged only — the cluster copy is written at commit (see Disable), and after the store: a store that refuses the value stages nothing (#1362).
        // The value supplies the component, so a collection the slot lacks is adopted, as a spawn's is.
        _ref._enabledBits |= (ushort)(1 << slot);
        if (!needsContent)
        {
            // The realm key and the position take Set's checked store: a realm change validated, a position given the barrier's bookkeeping.
            if (isChecked)
            {
                SetChecked(comp._componentTypeId, in value, adoptIntoEmpty: true);
            }
            else
            {
                Store(comp, in value, adoptIntoEmpty: true);
            }
        }

        _ref._accessor.StageEnableDisable(_ref._id, _ref._enabledBits);
    }

    /// <summary>Enable a component by handle. Stages the change for commit.</summary>
    public void Enable<T>(Comp<T> comp) where T : unmanaged
    {
        byte slot = _ref._archetype.GetSlot(comp._componentTypeId);
        // An enable or disable writes the component's slot and, at commit, the cluster's enabled bits: DV-01 (#1650) and the off-tick guard (#1651)
        SystemAccessValidator.AssertWrite<T>(_ref._accessor);
        if (_ref._accessor is { GuardActive: true } guarded)
        {
            TickWriteGuard.CheckStructural(guarded, _ref._archetype, "enabled or disabled a component of");
        }
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
