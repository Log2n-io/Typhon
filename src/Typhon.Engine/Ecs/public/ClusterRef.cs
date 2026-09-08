using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Per-cluster accessor providing typed, zero-copy access to component SoA arrays.
/// Created by <see cref="ClusterEnumerator{TArch}"/>. Must not outlive the enumerator.
/// </summary>
/// <remarks>
/// <para>Component data is laid out in Structure-of-Arrays format within the cluster:
/// <c>Component₀[N], Component₁[N], ...</c> where N is the cluster size (8..64).</para>
/// <para>Iteration pattern using OccupancyBits TZCNT loop:</para>
/// <code>
/// ulong bits = cluster.OccupancyBits;
/// while (bits != 0)
/// {
///     int idx = BitOperations.TrailingZeroCount(bits);
///     bits &amp;= bits - 1;
///     ref var pos = ref cluster.Get(Ant.Position, idx);
///     // ...
/// }
/// </code>
/// </remarks>
[PublicAPI]
public unsafe ref struct ClusterRef<TArch> where TArch : class
{
    private readonly byte* _base;
    private readonly byte* _transientBase;  // TransientStore cluster base; null for pure-SV/V or pure-Transient (where _base IS TS)
    private readonly ArchetypeClusterInfo _layout;
    private readonly ArchetypeMetadata _meta;
    private readonly int _chunkId;
    private readonly ArchetypeClusterState _state; // null only on synthetic test refs; carries spatial bookkeeping + grid

    // ── Cached cell frame (#872 step 9) ────────────────────────────────────
    //
    // The cluster's cell origin is a per-CLUSTER constant, and WriteSpatial is called per ENTITY. Resolving it inside the write meant two array loads, a
    // bounds check and a CellKeyToCoords — itself a volatile load plus two derefs into the cell's CellState line — for every ant, every tick, inlined into
    // the simulation barrier. A ClusterRef is created once per cluster and then written through many times (AntHill: `foreach (var cluster in clusters)`
    // with a slot loop inside), so caching on the ref moves that work from O(entities) to O(clusters).
    //
    // Lazy rather than resolved in the constructor: the constructor runs for every cluster access including the read-only query paths, which never need an
    // origin. Cheap to keep valid — the cluster's cell cannot change while the ref is alive (a cluster is assigned a cell at creation and only ever released
    // to -1; migration moves ENTITIES between clusters, never a cluster between cells).
    private const int CellFrameUnresolved = -2;
    private int _cachedCellKey;
    private double _cachedOriginX;
    private double _cachedOriginY;
    private double _cachedOriginZ;

    internal ClusterRef(byte* basePtr, byte* transientBasePtr, ArchetypeClusterInfo layout, ArchetypeMetadata meta, int chunkId, ArchetypeClusterState state)
    {
        _base = basePtr;
        _transientBase = transientBasePtr;
        _layout = layout;
        _meta = meta;
        _chunkId = chunkId;
        _state = state;
        _cachedCellKey = CellFrameUnresolved;
        _cachedOriginX = 0f;
        _cachedOriginY = 0f;
        _cachedOriginZ = 0f;
    }

    /// <summary>Bitmask of occupied slots. Bit i = 1 means slot i contains a live entity.</summary>
    public ulong OccupancyBits
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => *(ulong*)_base;
    }

    /// <summary>Bitmask of entities with component at <paramref name="slot"/> enabled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong EnabledBits(int slot) => *(ulong*)(_base + _layout.EnabledBitsOffset(slot));

    /// <summary>Combined mask: alive AND component at slot enabled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ActiveBits(int slot) => OccupancyBits & EnabledBits(slot);

    /// <summary>Number of live entities in this cluster (PopCount of OccupancyBits).</summary>
    public int LiveCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => BitOperations.PopCount(OccupancyBits);
    }

    /// <summary>True when all slots are occupied.</summary>
    public bool IsFull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => OccupancyBits == _layout.FullMask;
    }

    /// <summary>Cluster size N (number of slots, 8..64).</summary>
    public int ClusterSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _layout.ClusterSize;
    }

    /// <summary>Full mask with lower N bits set.</summary>
    public ulong FullMask
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _layout.FullMask;
    }

    /// <summary>Resolve the correct base pointer for a component slot (Transient → _transientBase, else → _base).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* ResolveBase(byte slot) => (_transientBase != null && (_meta.TransientSlotMask & (1 << slot)) != 0) ? _transientBase : _base;

    /// <summary>
    /// Assert that <typeparamref name="T"/> strides the column exactly. Every accessor below hands out a <c>Span&lt;T&gt;</c> or a <c>ref T</c>, both of which
    /// step by <c>sizeof(T)</c>; if the column was laid out at a different stride, slot <c>i</c> is addressed at the wrong offset and a write spills into the
    /// neighbouring slot — silently, in Release (#816). The layout has matched <c>sizeof(T)</c> since <c>DBComponentDefinition.Build</c> started taking the CLR
    /// size, so in practice this fires only when the <see cref="Comp{T}"/> handle names a component that is not <typeparamref name="T"/>.
    /// <para>Inline-guard form: <see cref="CheckConfig.Enabled"/> is a <c>static readonly bool</c> that defaults to <see langword="false"/> and is set from
    /// configuration, not from the build flavour — so the JIT folds the whole check away in any build that leaves strict mode off, and the interpolated
    /// message is built only on the throw path.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckStride<T>(byte slot) where T : unmanaged
    {
        if (CheckConfig.Enabled && sizeof(T) != _layout.ComponentSize(slot))
        {
            ThrowHelper.ThrowInvalidOp(
                $"Component at slot {slot} has a column stride of {_layout.ComponentSize(slot)} bytes but {typeof(T).Name} is {sizeof(T)} bytes. "
              + $"The Comp<T> handle most likely names a different component.");
        }
    }

    /// <summary>
    /// Get a mutable span of the component's data across all N slots (its SoA array). For Versioned components use <see cref="GetReadOnlySpan{T}"/> instead —
    /// writing directly to the cluster slot bypasses the revision chain and breaks MVCC snapshot isolation.
    /// </summary>
    /// <typeparam name="T">Component value type.</typeparam>
    /// <param name="comp">Handle identifying the component within the archetype.</param>
    /// <returns>A mutable span of length <see cref="ClusterSize"/> over the component's SoA array.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown, when strict checks are enabled (<see cref="CheckConfig.Enabled"/>), if <typeparamref name="T"/> is a Versioned component.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> GetSpan<T>(Comp<T> comp) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        if (CheckConfig.Enabled && (_meta.VersionedSlotMask & (1 << slot)) != 0)
        {
            ThrowHelper.ThrowInvalidOp(
                $"GetSpan on Versioned component bypasses revision chain. Use GetReadOnlySpan for reads, OpenMut+Write for writes.");
        }
        CheckStride<T>(slot);

        // ── Handing out a mutable span over the SPATIAL column is a promise that positions may move ─────────────────
        //
        // This is the one write path that signals nothing. WriteSpatial sets the process bit when the bound grew or a
        // crossing fired; OpenMut sets the dirty bit; a destroy now flags a shrink. GetSpan sets NONE of them — its own
        // contract is that the caller opts in via MarkDirty, and ClusterSpatialTests'
        // TickFence_DirectSpanWrite_NoMarkSlotDirty_AABB_StillRefreshed exists precisely because real callers do not
        // (AntHill's chase loop: a 1 000-radius query found the ant, an 80-radius kill missed it, because the cluster's
        // AABB was frozen at spawn while the entities walked away — a silent zero-hit query, not a crash).
        //
        // The engine's answer used to be that the fence re-derived EVERY active cluster's bound every tick, which made
        // the missing signal free and invisible. That walk is what the dirty gate removes, so the signal has to become
        // real. One Interlocked.Or per GetSpan call — per CLUSTER, not per entity, on a call that is already resolving
        // a slot and computing a base pointer — buys back exactly the coverage the full rescan was providing.
        //
        // A PER-ARCHETYPE flag, not a per-cluster process bit, and the difference is the whole design.
        //
        // GetSpan returns a MUTABLE span; it does not observe whether the caller writes through it, and plenty of callers
        // do not — ClusterRepairTests.ReadAll enumerates every cluster and reads positions through exactly this method.
        // Setting the process bit here therefore marks a cluster "visit and republish" on a pure READ, which takes it past
        // the !boundsMoved skip into the outlier guard and drift detection. Measured: doing that made a read-only helper
        // relocate entities, reddening ARepairIsNeverBegunWithoutTheBudgetToFinishIt with "a refused repair still moved
        // entities". A read must not perturb the partition.
        //
        // So the claim recorded here is the weakest one that is actually true: "somebody was handed the ability to move
        // this archetype's positions without telling us which cluster". The refresh answers it by doing what it did before
        // the dirty gate existed — walking every active cluster once, this tick. An archetype that uses GetSpan on its
        // spatial column keeps exactly today's cost and today's behaviour; one that does not gets the gate. Nothing
        // regresses, and the AntHill case (TickFence_DirectSpanWrite_NoMarkSlotDirty_AABB_StillRefreshed) stays covered
        // for the reason it always was.
        //
        // Cleared in Finalize, after the refresh has consumed it — see ClearAabbRefreshBookkeeping.
        if (_state.SpatialSlot.HasSpatialIndex && _state.SpatialSlot.Slot == slot)
        {
            Volatile.Write(ref _state.SpatialSpanHandedOut, 1);
        }

        return new Span<T>(ResolveBase(slot) + _layout.ComponentOffset(slot), _layout.ClusterSize);
    }

    /// <summary>
    /// Mark every occupied slot in this cluster dirty for ONE component column — the columnar counterpart to what <c>EntityRef.Write</c> does per entity.
    /// <para>
    /// <b>Required after writing through <see cref="GetSpan{T}"/> for any durable (SingleVersion) component.</b> The direct cluster path sets no dirty bits,
    /// so without this the tick fence never serialises the change and it is lost on reopen. Transient components need no call — they are never persisted.
    /// </para>
    /// <para>
    /// Prefer this over <c>ClusterEnumerator.MarkCurrentDirty()</c>, which cannot name a component and therefore widens the archetype's written-slot union to
    /// "all slots" — that union is what narrows the columns a FenceBlock emits (#559), so losing it inflates WAL volume for every entity in the archetype.
    /// </para>
    /// </summary>
    /// <typeparam name="T">Component value type.</typeparam>
    /// <param name="comp">Handle identifying the component column that was written.</param>
    public void MarkDirty<T>(Comp<T> comp) where T : unmanaged
    {
        var componentSlot = _meta.GetSlot(comp._componentTypeId);
        var bits = OccupancyBits;
        while (bits != 0)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            _state.SetDirty(_chunkId, slot, componentSlot);
        }
    }

    /// <summary>Get a read-only span of component data for all N slots.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> GetReadOnlySpan<T>(Comp<T> comp) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        CheckStride<T>(slot);
        return new ReadOnlySpan<T>(ResolveBase(slot) + _layout.ComponentOffset(slot), _layout.ClusterSize);
    }

    /// <summary>Get a mutable reference to a single component value at the given slot index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get<T>(Comp<T> comp, int slotIndex) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        if (CheckConfig.Enabled && (_meta.VersionedSlotMask & (1 << slot)) != 0)
        {
            ThrowHelper.ThrowInvalidOp($"Get on Versioned component bypasses revision chain. Use OpenMut+Write for writes.");
        }
        CheckStride<T>(slot);
        return ref Unsafe.Add(ref Unsafe.AsRef<T>(ResolveBase(slot) + _layout.ComponentOffset(slot)), slotIndex);
    }

    /// <summary>Get a read-only reference to a single component value at the given slot index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T GetReadOnly<T>(Comp<T> comp, int slotIndex) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        CheckStride<T>(slot);
        return ref Unsafe.Add(ref Unsafe.AsRef<T>(ResolveBase(slot) + _layout.ComponentOffset(slot)), slotIndex);
    }

    /// <summary>Entity keys for all N slots. Use with slot index to reconstruct EntityId.</summary>
    public ReadOnlySpan<long> EntityIds
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_base + _layout.EntityIdsOffset, _layout.ClusterSize);
    }

    /// <summary>Read EntityId for the entity at the given slot (stored as full packed EntityId).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityId GetEntityId(int slotIndex) =>
        EntityId.FromRaw(*(long*)(_base + _layout.EntityIdsOffset + slotIndex * 8));

    /// <summary>The chunk ID of this cluster within the archetype's segment.</summary>
    public int ChunkId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _chunkId;
    }

    /// <summary>
    /// Tight AABB of all entities in this cluster, in <b>world</b> coordinates. Returns the empty sentinel (min = +inf, max = -inf) when the archetype has
    /// no spatial index, or when the cluster is not attached to a cell. For 2D archetypes, MinZ/MaxZ are ±infinity sentinels — use MinX/MinY/MaxX/MaxY only.
    /// </summary>
    /// <remarks>
    /// <para><b>Returns a value, not a <c>ref readonly</c>, since #872 step 9.</b> The engine stores these bounds <c>C15</c> cell-relative, and this property
    /// is the boundary where they become world coordinates again. A reference cannot convert, and leaving it as one would have silently changed what every
    /// existing caller receives: the AntHill rock gather (<c>TyphonBridge</c>) and the SpaceBattle renderer and camera cull all compare this box against
    /// world positions, so they would have kept compiling and started answering wrongly by exactly the distance to the cell's origin.</para>
    /// <para><b>And it returns a DIFFERENT type since #914</b> — <see cref="ClusterWorldAabb"/>, six f64 components. Two reasons, and the second is the
    /// stronger one. It is f64 because a world coordinate is f64 now, and an f32 box would quantise to ~64-unit steps at 10⁹. It is a separate type because
    /// handing world values back in <see cref="ClusterSpatialAabb"/> made one struct mean two incompatible things — stored-and-cell-relative on one side of
    /// this property, world on the other — with nothing in the type to tell a caller which one it was holding.</para>
    /// <para>The cost is a 56-byte copy plus two dependent loads for the cell origin, against returning a reference. That is the right trade for a public
    /// property: callers reading it per cluster per frame can afford it, and a caller that wants the raw stored frame is inside the engine and can use
    /// <see cref="CellRelativeBounds"/>.</para>
    /// </remarks>
    public ClusterWorldAabb SpatialBounds
    {
        get
        {
            if (_state?.ClusterAabbs == null || (uint)_chunkId >= (uint)_state.ClusterAabbs.Length)
            {
                return ClusterWorldAabb.Empty;
            }

            // A by-value COPY, not a ref: ClusterAabbs is CAS-grown concurrently by other threads' WriteSpatial calls, and reading the seven fields
            // through a reference would widen the window over which they can disagree. The copy is what the property did before #914 and there is no
            // reason to narrow it now.
            var box = _state.ClusterAabbs[_chunkId];
            if (!TryGetCellOrigin(out double originX, out double originY, out double originZ))
            {
                // No cell means the stored value is the Empty sentinel rather than a bound in some other frame — see the spawn union in
                // Transaction.ECS, which leaves it untouched when a cluster has no cell. A zero origin is therefore not a fallback: it is the identity
                // conversion applied to a value that is already ±Infinity on every axis.
                return ClusterWorldAabb.FromCellRelative(in box, 0d, 0d, 0d);
            }

            return ClusterWorldAabb.FromCellRelative(in box, originX, originY, originZ);
        }
    }

    /// <summary>
    /// The cluster's bounds in the <c>C15</c> CELL-RELATIVE frame they are stored in — engine-internal. Use <see cref="SpatialBounds"/> outside.
    /// </summary>
    internal ref readonly ClusterSpatialAabb CellRelativeBounds
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref (_state?.ClusterAabbs != null ? ref _state.ClusterAabbs[_chunkId] : ref ClusterSpatialAabb.s_empty);
    }

    /// <summary>
    /// Write-barrier API for spatial components — the canonical replacement for <c>cluster.GetSpan&lt;T&gt;()[slotIndex] = ...</c> when <c>T</c> contains the
    /// archetype's <see cref="SpatialIndexAttribute"/>-marked field. Performs (in order):
    /// <list type="number">
    /// <item>Reads the OLD spatial-field bytes at <paramref name="slotIndex"/></item>
    /// <item>Writes <paramref name="newValue"/> to the slot</item>
    /// <item>Updates <see cref="ArchetypeClusterState.ClusterAabbs"/> inline on AABB grow (O(1) CAS per axis)</item>
    /// <item>Flags <see cref="ArchetypeClusterState.ClusterShrinkPendingAxes"/> for axes where this slot was at an extreme and moved inward — fence rescans
    ///       only this cluster on those axes</item>
    /// <item>Flags <see cref="ArchetypeClusterState.ClusterMigrationPendingSlots"/> when the new position crosses the cell+hysteresis boundary — fence drains
    ///       the migration without any full scan</item>
    /// <item>Sets the cluster's bit in <see cref="ArchetypeClusterState.ClusterProcessBitmap"/> so the fence loop visits this cluster</item>
    /// </list>
    /// <para>
    /// <b>All eight <see cref="SpatialFieldType"/> variants are supported</b> — the four f32 tiers since #914 phase B, the four f64 ones since phase C.
    /// V1 handled <see cref="SpatialFieldType.AABB2F"/> alone (AntHill's <c>WorldBounds</c>), which is why a 3D archetype used to fall back to the MVCC
    /// path and pay one WAL frame per entity per tick. Each tier has its own decode specialization; they all hand f64 world bounds to one shared
    /// <c>ApplySpatialWrite</c>, whose <c>is3D</c> is a call-site constant so the 2D path costs what it always did.
    /// </para>
    /// <para>
    /// <b>WriteSpatial does NOT mark the slot dirty</b> (via <see cref="ArchetypeClusterState.SetDirty(int, int, int)"/>).
    /// The dirty bitmap drives WAL serialization and change-filtered dispatch — for high-frequency  simulation state (e.g., AntHill's ant positions), marking
    /// every slot dirty floods the WAL writer with one frame per entity per tick → backpressure that stalls TickDriver. The fence-time spatial maintenance does
    /// not need the dirty bit; it consumes <see cref="ArchetypeClusterState.ClusterMigrationPendingSlots"/> /
    /// <see cref="ArchetypeClusterState.ClusterProcessBitmap"/> directly. If your workload genuinely needs WAL persistence of the spatial field (e.g.,
    /// resumable autosave), either write through the MVCC <c>Transaction.OpenMut + Write</c> path (which marks dirty), or
    /// call <see cref="ArchetypeClusterState.SetDirty(int, int, int)"/> explicitly after <c>WriteSpatial</c>.
    /// </para>
    /// <para>
    /// Thread safety: safe to call concurrently from multiple workers operating on different slots of any cluster (including the same cluster). All
    /// bookkeeping writes use <see cref="Interlocked"/> primitives.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSpatial<T>(Comp<T> comp, int slotIndex, in T newValue) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        // Hottest path (AntHill per-entity-per-tick spatial write): inline-guard form so the gate JIT-folds to nothing when strict mode is off (#422 AC#6).
        if (CheckConfig.Enabled && (_meta.VersionedSlotMask & (1 << slot)) != 0)
        {
            ThrowHelper.ThrowInvalidOp($"WriteSpatial on Versioned component bypasses revision chain.");
        }
        if (CheckConfig.Enabled && (_state == null || !_state.SpatialSlot.HasSpatialIndex || _state.SpatialSlot.Slot != slot))
        {
            ThrowHelper.ThrowInvalidOp(
                $"WriteSpatial requires the archetype's spatial-indexed component (marked [SpatialIndex]). For non-spatial fields, use GetSpan or Get.");
        }

        CheckStride<T>(slot);

        var spatialSlot = _state.SpatialSlot;
        var slotBytes = ResolveBase(slot) + _layout.ComponentOffset(slot) + slotIndex * sizeof(T);
        var fieldPtr = slotBytes + spatialSlot.FieldOffset;

        var fieldType = spatialSlot.FieldInfo.FieldType;
        switch (fieldType)
        {
            case SpatialFieldType.AABB2F:
                WriteSpatialAabb2F(slotIndex, slotBytes, fieldPtr, in newValue);
                break;
            case SpatialFieldType.AABB3F:
                WriteSpatialAabb3F(slotIndex, slotBytes, fieldPtr, in newValue);
                break;
            case SpatialFieldType.BSphere2F:
                WriteSpatialBSphere2F(slotIndex, slotBytes, fieldPtr, in newValue);
                break;
            case SpatialFieldType.BSphere3F:
                WriteSpatialBSphere3F(slotIndex, slotBytes, fieldPtr, in newValue);
                break;
            case SpatialFieldType.AABB2D:
                WriteSpatialAabb2D(slotIndex, slotBytes, fieldPtr, in newValue);
                break;
            case SpatialFieldType.AABB3D:
                WriteSpatialAabb3D(slotIndex, slotBytes, fieldPtr, in newValue);
                break;
            case SpatialFieldType.BSphere2D:
                WriteSpatialBSphere2D(slotIndex, slotBytes, fieldPtr, in newValue);
                break;
            case SpatialFieldType.BSphere3D:
                WriteSpatialBSphere3D(slotIndex, slotBytes, fieldPtr, in newValue);
                break;
            default:
                // All eight SpatialFieldType variants have a case above since #914. This is the guard for a variant ADDED to the enum without one; the
                // alternative is an entity whose bound never reaches the cluster AABB, which CA-01 calls a silent false negative.
                throw new NotSupportedException(
                    $"WriteSpatial: spatial field type {fieldType} has no specialization. Add one when a new SpatialFieldType variant is introduced.");
        }
    }

    /// <summary>AABB3F specialization of <see cref="WriteSpatial{T}"/> — the 3D tier #914 exists for.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSpatialAabb3F<T>(int slotIndex, byte* slotBytes, byte* fieldPtr, in T newValue) where T : unmanaged
    {
        ref var oldBox = ref *(AABB3F*)fieldPtr;
        var oldMinX = oldBox.MinX; var oldMinY = oldBox.MinY; var oldMinZ = oldBox.MinZ;
        var oldMaxX = oldBox.MaxX; var oldMaxY = oldBox.MaxY; var oldMaxZ = oldBox.MaxZ;

        *(T*)slotBytes = newValue;

        ref var newBox = ref *(AABB3F*)fieldPtr;
        ApplySpatialWrite(slotIndex,
            oldMinX, oldMinY, oldMinZ, oldMaxX, oldMaxY, oldMaxZ,
            newBox.MinX, newBox.MinY, newBox.MinZ, newBox.MaxX, newBox.MaxY, newBox.MaxZ, is3D: true);
    }

    /// <summary>BSphere2F specialization — the enclosing box, and a Z the grid reads as the flat plane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSpatialBSphere2F<T>(int slotIndex, byte* slotBytes, byte* fieldPtr, in T newValue) where T : unmanaged
    {
        var oldBox = SpatialGeometry.Enclosing(*(BSphere2F*)fieldPtr);

        *(T*)slotBytes = newValue;

        var newBox = SpatialGeometry.Enclosing(*(BSphere2F*)fieldPtr);

        // Z is the 2D sentinel pair, exactly as the AABB2F path leaves it: ReadSpatialCenter3D reports posZ = 0 for both 2D tiers, so write-time and
        // fence-time place the entity in the same plane.
        ApplySpatialWrite(slotIndex,
            oldBox.MinX, oldBox.MinY, 0f, oldBox.MaxX, oldBox.MaxY, 0f,
            newBox.MinX, newBox.MinY, 0f, newBox.MaxX, newBox.MaxY, 0f, is3D: false);
    }

    /// <summary>BSphere3F specialization — the enclosing box on all three axes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSpatialBSphere3F<T>(int slotIndex, byte* slotBytes, byte* fieldPtr, in T newValue) where T : unmanaged
    {
        var oldBox = SpatialGeometry.Enclosing(*(BSphere3F*)fieldPtr);

        *(T*)slotBytes = newValue;

        var newBox = SpatialGeometry.Enclosing(*(BSphere3F*)fieldPtr);

        ApplySpatialWrite(slotIndex,
            oldBox.MinX, oldBox.MinY, oldBox.MinZ, oldBox.MaxX, oldBox.MaxY, oldBox.MaxZ,
            newBox.MinX, newBox.MinY, newBox.MinZ, newBox.MaxX, newBox.MaxY, newBox.MaxZ, is3D: true);
    }

    /// <summary>AABB2D specialization — the 2D f64 tier. Unreachable before #914 phase C, when <c>ValidateSupportedFieldType</c> still rejected f64.</summary>
    /// <remarks>
    /// The decode is the only thing that differs from <see cref="WriteSpatialAabb2F{T}"/>: the shared core takes f64 world bounds, so this tier hands its
    /// doubles straight through instead of widening f32 ones. That is the point of the tier — an <c>AABB2D</c> at 10⁹ carries mantissa an <c>AABB2F</c>
    /// cannot, and the cell-relative narrowing inside the core is where it becomes f32 again, bounded by one cell.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSpatialAabb2D<T>(int slotIndex, byte* slotBytes, byte* fieldPtr, in T newValue) where T : unmanaged
    {
        ref var oldBox = ref *(AABB2D*)fieldPtr;
        var oldMinX = oldBox.MinX; var oldMinY = oldBox.MinY;
        var oldMaxX = oldBox.MaxX; var oldMaxY = oldBox.MaxY;

        *(T*)slotBytes = newValue;

        ref var newBox = ref *(AABB2D*)fieldPtr;

        // Z is the flat-plane sentinel pair, as in every 2D tier: ReadSpatialCenter3D reports posZ = 0 for AABB2D too, so write-time and fence-time agree.
        ApplySpatialWrite(slotIndex,
            oldMinX, oldMinY, 0d, oldMaxX, oldMaxY, 0d,
            newBox.MinX, newBox.MinY, 0d, newBox.MaxX, newBox.MaxY, 0d, is3D: false);
    }

    /// <summary>AABB3D specialization — the 3D f64 tier, the widest thing the grid stores.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSpatialAabb3D<T>(int slotIndex, byte* slotBytes, byte* fieldPtr, in T newValue) where T : unmanaged
    {
        ref var oldBox = ref *(AABB3D*)fieldPtr;
        var oldMinX = oldBox.MinX; var oldMinY = oldBox.MinY; var oldMinZ = oldBox.MinZ;
        var oldMaxX = oldBox.MaxX; var oldMaxY = oldBox.MaxY; var oldMaxZ = oldBox.MaxZ;

        *(T*)slotBytes = newValue;

        ref var newBox = ref *(AABB3D*)fieldPtr;
        ApplySpatialWrite(slotIndex,
            oldMinX, oldMinY, oldMinZ, oldMaxX, oldMaxY, oldMaxZ,
            newBox.MinX, newBox.MinY, newBox.MinZ, newBox.MaxX, newBox.MaxY, newBox.MaxZ, is3D: true);
    }

    /// <summary>BSphere2D specialization — the enclosing box, in f64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSpatialBSphere2D<T>(int slotIndex, byte* slotBytes, byte* fieldPtr, in T newValue) where T : unmanaged
    {
        var oldBox = SpatialGeometry.Enclosing(*(BSphere2D*)fieldPtr);

        *(T*)slotBytes = newValue;

        var newBox = SpatialGeometry.Enclosing(*(BSphere2D*)fieldPtr);

        ApplySpatialWrite(slotIndex,
            oldBox.MinX, oldBox.MinY, 0d, oldBox.MaxX, oldBox.MaxY, 0d,
            newBox.MinX, newBox.MinY, 0d, newBox.MaxX, newBox.MaxY, 0d, is3D: false);
    }

    /// <summary>BSphere3D specialization — the enclosing box on all three axes, in f64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSpatialBSphere3D<T>(int slotIndex, byte* slotBytes, byte* fieldPtr, in T newValue) where T : unmanaged
    {
        var oldBox = SpatialGeometry.Enclosing(*(BSphere3D*)fieldPtr);

        *(T*)slotBytes = newValue;

        var newBox = SpatialGeometry.Enclosing(*(BSphere3D*)fieldPtr);

        ApplySpatialWrite(slotIndex,
            oldBox.MinX, oldBox.MinY, oldBox.MinZ, oldBox.MaxX, oldBox.MaxY, oldBox.MaxZ,
            newBox.MinX, newBox.MinY, newBox.MinZ, newBox.MaxX, newBox.MaxY, newBox.MaxZ, is3D: true);
    }

    /// <summary>
    /// The bookkeeping every <c>WriteSpatial</c> tier shares once its old and new boxes are decoded: grow the cluster bound, flag shrink, test for a cell
    /// crossing, and publish the cluster to the fence.
    /// </summary>
    /// <remarks>
    /// <para><b><paramref name="is3D"/> is a constant at every call site</b>, so the JIT folds the Z arms away for a 2D tier after inlining — the 2D path
    /// costs exactly what it did before the 3D tiers existed. It is not a runtime mode.</para>
    /// <para><b>The bounds arrive as f64 world coordinates whatever the tier's storage width</b> (#914 phase C). Widening an f32 tier's bound is exact, so
    /// AntHill's <c>AABB2F</c> path means bit-for-bit what it meant; and both things this method then does with them narrow immediately — the cluster AABB
    /// through <see cref="ClusterSpatialAabb.ToCellRelativeMin"/>, the migration test through the same cell-relative subtraction — so nothing downstream
    /// carries the wider value. Taking f32 here instead would have forced the f64 tiers to throw away their magnitude at the door, which is the whole thing
    /// they exist for.</para>
    /// <para><b>Z is skipped entirely for a 2D tier, and that is not an optimisation.</b> A 2D archetype's stored <c>ClusterSpatialAabb</c> leaves
    /// <c>MinZ</c>/<c>MaxZ</c> at the ±Infinity sentinel; growing them to 0 would turn the sentinel into a real bound, and the outlier guard's extent test
    /// (<c>MaxZ - MinZ</c>) would go from <c>-Infinity</c> — its "no Z bound" reading — to 0, which reads as a perfectly tight axis.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplySpatialWrite(int slotIndex,
        double oldMinX, double oldMinY, double oldMinZ, double oldMaxX, double oldMaxY, double oldMaxZ,
        double newMinX, double newMinY, double newMinZ, double newMaxX, double newMaxY, double newMaxZ, bool is3D)
    {
        // See WriteSpatialAabb2F for why no SetDirty happens here.
        bool haveOrigin = TryGetCellOrigin(out int cellKey, out double originX, out double originY, out double originZ);
        var aabbChanged = false;
        if (haveOrigin)
        {
            ref var stored = ref _state.ClusterAabbs[_chunkId];
            aabbChanged = MaybeGrowAndFlagShrink(ref stored,
                ClusterSpatialAabb.ToCellRelativeMin(oldMinX, originX), ClusterSpatialAabb.ToCellRelativeMin(oldMinY, originY),
                ClusterSpatialAabb.ToCellRelativeMin(oldMinZ, originZ),
                ClusterSpatialAabb.ToCellRelativeMax(oldMaxX, originX), ClusterSpatialAabb.ToCellRelativeMax(oldMaxY, originY),
                ClusterSpatialAabb.ToCellRelativeMax(oldMaxZ, originZ),
                ClusterSpatialAabb.ToCellRelativeMin(newMinX, originX), ClusterSpatialAabb.ToCellRelativeMin(newMinY, originY),
                ClusterSpatialAabb.ToCellRelativeMin(newMinZ, originZ),
                ClusterSpatialAabb.ToCellRelativeMax(newMaxX, originX), ClusterSpatialAabb.ToCellRelativeMax(newMaxY, originY),
                ClusterSpatialAabb.ToCellRelativeMax(newMaxZ, originZ),
                is3D);
        }

        // The REAL Z centre, which is what trap 1 of #914 was about: this used to be a hard-coded 0f, so a 3D entity would have been placed in the z = 0
        // plane at write time and in its true plane at fence time. Both detectors would have agreed with themselves and disagreed with each other, every
        // counter would have balanced, and the entity would simply have been in the wrong cell.
        var centerX = 0.5d * (newMinX + newMaxX);
        var centerY = 0.5d * (newMinY + newMaxY);
        var centerZ = is3D ? 0.5d * (newMinZ + newMaxZ) : 0d;

        var migrationFlagged = haveOrigin && MaybeFlagMigration(slotIndex, cellKey, originX, originY, originZ, centerX, centerY, centerZ);

        if (migrationFlagged)
        {
            _state.MigrationHint++;
        }

        if (aabbChanged || migrationFlagged)
        {
            SetClusterProcessBit();
        }
    }

    /// <summary>AABB2F specialization of <see cref="WriteSpatial{T}"/>. Inlined into the barrier on the AntHill hot path (WorldBounds.Bounds is AABB2F,
    /// point-form-encoded with MinX==MaxX, MinY==MaxY).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSpatialAabb2F<T>(int slotIndex, byte* slotBytes, byte* fieldPtr, in T newValue) where T : unmanaged
    {
        // Read old AABB before overwriting (fieldPtr points at the AABB2F inside the component).
        ref var oldAabb = ref *(AABB2F*)fieldPtr;
        var oldMinX = oldAabb.MinX;
        var oldMinY = oldAabb.MinY;
        var oldMaxX = oldAabb.MaxX;
        var oldMaxY = oldAabb.MaxY;

        // Write the new value (full T struct, may include non-spatial fields).
        *(T*)slotBytes = newValue;

        // Re-read the AABB2F from the freshly-written value (handles offset within T).
        ref var newAabb = ref *(AABB2F*)fieldPtr;
        var newMinX = newAabb.MinX;
        var newMinY = newAabb.MinY;
        var newMaxX = newAabb.MaxX;
        var newMaxY = newAabb.MaxY;

        // NOTE: WriteSpatial deliberately does NOT call _state.SetDirty for the spatial slot. The dirty bitmap drives WAL serialization and change-filtered
        // dispatch; for cluster archetypes where the spatial component is high-frequency simulation state (e.g., AntHill's WorldBounds), marking every slot
        // dirty floods the WAL with 100k frames/tick -> backpressure. The fence-time spatial maintenance does NOT need the dirty bit -- it consumes
        // ClusterMigrationPendingSlots / ClusterProcessBitmap directly. Callers that genuinely need WAL persistence of the spatial field should mutate it via
        // the MVCC Transaction path (which marks dirty), or call _state.SetDirty explicitly after WriteSpatial. See claude/design/spatial/write-time-spatial.md.

        // Z is the flat-plane sentinel pair: ReadSpatialCenter3D reports posZ = 0 for both 2D tiers, so write-time and fence-time agree on the plane.
        ApplySpatialWrite(slotIndex,
            oldMinX, oldMinY, 0f, oldMaxX, oldMaxY, 0f,
            newMinX, newMinY, 0f, newMaxX, newMaxY, 0f, is3D: false);
    }

    /// <summary>
    /// Inline AABB-grow (CAS per axis) + shrink-pending-axes flag. Returns true when either axis-extreme moved (in which case the cluster needs a
    /// fence-time <c>PerCellIndex.UpdateAt</c> with the fresh AABB).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MaybeGrowAndFlagShrink(ref ClusterSpatialAabb stored,
        float oldMinX, float oldMinY, float oldMinZ, float oldMaxX, float oldMaxY, float oldMaxZ,
        float newMinX, float newMinY, float newMinZ, float newMaxX, float newMaxY, float newMaxZ, bool is3D)
    {
        var changed = false;

        // GROW path (CAS loop per axis). Note: AABB2F has min/max as separate fields, so we CAS each independently.
        if (newMinX < stored.MinX) { ClusterSpatialAabb.CasMin(ref stored.MinX, newMinX); changed = true; }
        if (newMinY < stored.MinY) { ClusterSpatialAabb.CasMin(ref stored.MinY, newMinY); changed = true; }
        if (newMaxX > stored.MaxX) { ClusterSpatialAabb.CasMax(ref stored.MaxX, newMaxX); changed = true; }
        if (newMaxY > stored.MaxY) { ClusterSpatialAabb.CasMax(ref stored.MaxY, newMaxY); changed = true; }

        // SHRINK flag (only set when this slot WAS at an extreme AND moved inward). Bit layout:
        // 0x01=MinX, 0x02=MaxX, 0x04=MinY, 0x08=MaxY, 0x10=MinZ, 0x20=MaxZ (matches ClusterShrinkPendingAxes doc).
        byte shrinkMask = 0;
        if (oldMinX == stored.MinX && newMinX > oldMinX)
        {
            shrinkMask |= 0x01;
        }

        if (oldMaxX == stored.MaxX && newMaxX < oldMaxX)
        {
            shrinkMask |= 0x02;
        }

        if (oldMinY == stored.MinY && newMinY > oldMinY)
        {
            shrinkMask |= 0x04;
        }

        if (oldMaxY == stored.MaxY && newMaxY < oldMaxY)
        {
            shrinkMask |= 0x08;
        }

        // The Z axis, live since #914. It is gated on is3D rather than run unconditionally because a 2D archetype's stored MinZ/MaxZ are the +/-Infinity
        // sentinel: growing them to a real 0 would make the outlier guard's MaxZ - MinZ read 0 (a perfectly tight axis) where it currently reads -Infinity
        // (no Z bound at all), and the guard would stop distinguishing the two.
        if (is3D)
        {
            if (newMinZ < stored.MinZ) { ClusterSpatialAabb.CasMin(ref stored.MinZ, newMinZ); changed = true; }
            if (newMaxZ > stored.MaxZ) { ClusterSpatialAabb.CasMax(ref stored.MaxZ, newMaxZ); changed = true; }

            if (oldMinZ == stored.MinZ && newMinZ > oldMinZ)
            {
                shrinkMask |= 0x10;
            }

            if (oldMaxZ == stored.MaxZ && newMaxZ < oldMaxZ)
            {
                shrinkMask |= 0x20;
            }
        }

        if (shrinkMask != 0)
        {
            InterlockedOrByteArrayElement(_state.ClusterShrinkPendingAxes, _chunkId, shrinkMask);
            changed = true;
        }

        return changed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InterlockedOrByteArrayElement(byte[] array, int index, byte mask)
    {
        // CAS loop on the byte directly: read, OR, CompareExchange byte's int-aligned word. Since `byte` is 1-byte and Interlocked operates on int+, we widen
        // to a per-element approach: each cluster index gets its own array slot, so within-byte word collisions only happen across nearby cluster indices.
        // A simple CAS loop on the single byte slot suffices.
        while (true)
        {
            var current = array[index];
            var updated = (byte)(current | mask);
            if (current == updated)
            {
                return; // mask already set
            }

            // Use Interlocked.CompareExchange on the byte directly via Unsafe.As<byte, int>. Since the byte is part of a larger int chunk, we operate on
            // a 1-byte CAS via a small helper. .NET 7+ has Interlocked.CompareExchange(ref byte, byte, byte) — use it.
            if (Interlocked.CompareExchange(ref array[index], updated, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>
    /// World-space minimum corner of the cell this cluster belongs to — the origin its <c>C15</c> cell-relative bounds are measured from.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> when the archetype has no grid or the cluster has no cell yet, in which case there is no frame to express a bound in and the
    /// caller must leave <c>ClusterAabbs</c> alone. Writing a world-space bound as a fallback would be worse than writing nothing: it would be indistinguishable
    /// from a cell-relative one on read, and wrong by the whole distance to the origin.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetCellOrigin(out double originX, out double originY, out double originZ) =>
        TryGetCellOrigin(out _, out originX, out originY, out originZ);

    /// <summary>
    /// The cluster's cell key and the world-space origin its <c>C15</c> bounds are measured from, resolved once per <see cref="ClusterRef{TArch}"/> and
    /// cached. Also yields the key, so a caller needing both does not resolve the cluster's cell twice.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetCellOrigin(out int cellKey, out double originX, out double originY, out double originZ)
    {
        if (_cachedCellKey != CellFrameUnresolved)
        {
            cellKey = _cachedCellKey;
            originX = _cachedOriginX;
            originY = _cachedOriginY;
            originZ = _cachedOriginZ;
            return cellKey >= 0;
        }

        return ResolveCellOrigin(out cellKey, out originX, out originY, out originZ);
    }

    /// <summary>The cold half of <see cref="TryGetCellOrigin(out int, out double, out double, out double)"/> — taken once per cluster, never inlined into the
    /// per-entity write.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool ResolveCellOrigin(out int cellKey, out double originX, out double originY, out double originZ)
    {
        cellKey = -1;
        originX = 0f;
        originY = 0f;
        originZ = 0f;

        var grid = _state?.Grid;
        var clusterCellMap = _state?.ClusterCellMap;
        if (grid != null && clusterCellMap != null && (uint)_chunkId < (uint)clusterCellMap.Length)
        {
            cellKey = clusterCellMap[_chunkId];
            if (cellKey >= 0)
            {
                grid.CellOrigin(cellKey, out originX, out originY, out originZ);
            }
        }

        // Only a SUCCESSFUL resolution is cached. Caching the miss would be faster and is not safe: a cluster acquires its cell during creation, so a ref
        // taken before that assignment and used after it would answer "no cell" for the rest of its life — and the caller's response to "no cell" is to skip
        // the CA-01 grow entirely, silently. A miss therefore re-resolves, which costs nothing that matters: a live cluster always has a cell, so the miss
        // path is not a path the hot loop takes.
        if (cellKey >= 0)
        {
            _cachedCellKey = cellKey;
            _cachedOriginX = originX;
            _cachedOriginY = originY;
            _cachedOriginZ = originZ;
            return true;
        }

        return false;
    }

    /// <summary>Migration cell-boundary check. Returns true when a migration was flagged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MaybeFlagMigration(int slotIndex, int currentCellKey, double cellMinX, double cellMinY, double cellMinZ,
        double centerX, double centerY, double centerZ)
    {
        // The cell key and origin are PASSED IN rather than re-derived. The caller resolved both to convert the entity's bounds into the cluster's frame,
        // and this method used to repeat all of it — two array loads, a bounds check, CellKeyToCoords (itself two dependent loads into the cell's CellState)
        // and three multiplies — per entity per tick, inlined into the AntHill simulation barrier.
        var grid = _state.Grid;

        ref readonly var cfg = ref grid.Config;

        // ── The test is CELL-RELATIVE, and that is what keeps it f32 (#914) ──────────────────────────────────────
        //
        // The cell origin is f64 since phase A, because a frame at 10^9 needs the mantissa. Comparing the entity centre against it directly would drag six
        // comparisons per entity per tick into double on the hottest write path in the engine. Subtracting first costs three conversions and gives back an
        // offset that is at most one cell wide — which f32 holds exactly, wherever in the world the cell sits. That is the floating-origin bargain applied
        // to the migration test rather than only to stored bounds, and it is the same reason C15 can keep ClusterSpatialAabb in f32.
        var cellSize = (float)cfg.CellSize;
        var hyster = cellSize * cfg.MigrationHysteresisRatio;
        var relX = (float)(centerX - cellMinX);
        var relY = (float)(centerY - cellMinY);
        var relZ = (float)(centerZ - cellMinZ);

        var exited = relX < -hyster || relX > cellSize + hyster
                     || relY < -hyster || relY > cellSize + hyster
                     || relZ < -hyster || relZ > cellSize + hyster;
        if (!exited)
        {
            // Count the crossings the margin swallowed (#872). Without this the SpatialBarrierOnly path reports zero absorbed crossings forever:
            // DetectClusterMigrations only increments inside its legacy dirty-bits scan, and the barrier-only branch returns before reaching it — so the
            // ratio that tunes MigrationHysteresisRatio was structurally 0/N on the path both demos use. The decision is made here, so the count belongs here.
            //
            // The extra test is the SAME comparison without the margin: "left the cell" minus "left the cell plus margin" is exactly the absorbed set.
            //
            // Ungated on purpose, matching MigrationHint below and EntityMap's split counter: a static readonly profiler gate resolves to false by default, so
            // gating it would leave the number a structural zero in exactly the tool built to read it — trading a defect nobody can see for four float
            // compares in a method that already dereferences the grid, loads two arrays and calls CellKeyToCoords.
            //
            // Interlocked, unlike MigrationHint's plain ++ below: this value is PUBLISHED as an exact count through
            // SpatialMigrationTelemetry, where MigrationHint is documented as an order-of-magnitude work estimate. The atomic is affordable precisely because
            // it is rare — it fires only for a write that lands inside the margin band, not on every spatial write.
            var rawExited = relX < 0f || relX > cellSize
                            || relY < 0f || relY > cellSize
                            || relZ < 0f || relZ > cellSize;
            if (rawExited)
            {
                Interlocked.Increment(ref _state.HysteresisAbsorbedLive);
            }

            return false;
        }

        var newCellKey = grid.WorldToCellKey(centerX, centerY, centerZ);
        if (newCellKey == currentCellKey)
        {
            return false;
        }

        // Set bit in per-cluster migration bitmap (atomic OR), and stomp dest cell key. The key is a HINT: the drain (DrainPreFlaggedMigrations) re-reads
        // the slot's position and decides from that, so two slots of one cluster written to two cells, an entity written out and back within the tick,
        // and a write that reached a spawn's slot before its data landed, all resolve to where the entity actually is (CC-02).
        var slotBit = 1UL << slotIndex;
        Interlocked.Or(ref _state.ClusterMigrationPendingSlots[_chunkId], slotBit);
        _state.ClusterMigrationDestCellKeys[_chunkId] = newCellKey;
        return true;
    }

    /// <summary>Atomically set this cluster's bit in <see cref="ArchetypeClusterState.ClusterProcessBitmap"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetClusterProcessBit()
    {
        var wordIdx = _chunkId >> 6;
        var bit = 1L << (_chunkId & 63);
        Interlocked.Or(ref _state.ClusterProcessBitmap[wordIdx], bit);
    }
}

/// <summary>
/// Iterates active clusters for an archetype. Owns a <see cref="ChunkAccessor{TStore}"/> — must be disposed.
/// </summary>
/// <remarks>
/// <para>Supports <c>foreach</c> via <see cref="GetEnumerator"/>.</para>
/// <para>
/// <b>Non-empty guarantee:</b> the enumerator always yields clusters with <c>OccupancyBits != 0</c> (i.e. <c>LiveCount &gt;= 1</c>). Empty clusters can exist
/// in <c>ActiveClusterIds</c> during the fence's deferred-drain window — between Migrate (last slot released) and Finalize (chunk freed) — but
/// <see cref="MoveNext"/> filters them out so callers never observe a drained cluster.
/// </para>
/// <para>Usage:</para>
/// <code>
/// foreach (var cluster in ants.GetClusterEnumerator())
/// {
///     var positions = cluster.GetSpan&lt;Position&gt;(Ant.Position);
///     ulong bits = cluster.OccupancyBits; // guaranteed non-zero
///     while (bits != 0)
///     {
///         int slot = BitOperations.TrailingZeroCount(bits);
///         bits &amp;= bits - 1;
///         // ...
///     }
/// }
/// </code>
/// </remarks>
[PublicAPI]
public unsafe ref struct ClusterEnumerator<TArch> where TArch : class
{
    private ArchetypeClusterState _state;
    private ArchetypeMetadata _meta;
    private ChunkAccessor<PersistentStore> _accessor;
    private ChunkAccessor<TransientStore> _transientAccessor;
    private bool _hasTransientAccessor;
    private bool _hasPersistentAccessor;
    // Issue #231: source array for cluster chunk ids. Defaults to state.ActiveClusterIds but can point at a tier-filtered partition supplied
    // by TickContext.ClusterIds when a system declares a tier filter.
    private int[] _clusterIds;
    private int _index;
    private int _endIndex;

    [AllowCopy]
    internal static ClusterEnumerator<TArch> Create(ArchetypeClusterState state, ArchetypeMetadata meta,
        ChunkBasedSegment<PersistentStore> segment, ChunkBasedSegment<TransientStore> transientSegment = null)
    {
        var result = new ClusterEnumerator<TArch> { _state = state, _meta = meta };
        if (segment != null)
        {
            result._accessor = segment.CreateChunkAccessor();
            result._hasPersistentAccessor = true;
        }
        if (transientSegment != null)
        {
            result._transientAccessor = transientSegment.CreateChunkAccessor();
            result._hasTransientAccessor = true;
        }
        result._clusterIds = state.ActiveClusterIds;
        result._index = -1;
        result._endIndex = state.ActiveClusterCount;
        return result;
    }

    /// <summary>
    /// Create a scoped enumerator that iterates a range of <see cref="ArchetypeClusterState.ActiveClusterIds"/>.
    /// Used by non-tier-filtered parallel dispatch to partition cluster work across workers.
    /// </summary>
    [AllowCopy]
    internal static ClusterEnumerator<TArch> CreateScoped(ArchetypeClusterState state, ArchetypeMetadata meta,
        ChunkBasedSegment<PersistentStore> segment, ChunkBasedSegment<TransientStore> transientSegment,
        int startIndex, int endIndex)
    {
        var result = new ClusterEnumerator<TArch> { _state = state, _meta = meta };
        if (segment != null)
        {
            result._accessor = segment.CreateChunkAccessor();
            result._hasPersistentAccessor = true;
        }
        if (transientSegment != null)
        {
            result._transientAccessor = transientSegment.CreateChunkAccessor();
            result._hasTransientAccessor = true;
        }
        result._clusterIds = state.ActiveClusterIds;
        result._index = startIndex - 1;
        result._endIndex = endIndex;
        return result;
    }

    /// <summary>
    /// Create a scoped enumerator over an explicit cluster-id source array (issue #231). The source is typically a per-tier cluster list returned
    /// by <see cref="TierClusterIndex.GetClusters"/>. The range <c>[startIndex, endIndex)</c> indexes into <paramref name="clusterIds"/>, not
    /// into <see cref="ArchetypeClusterState.ActiveClusterIds"/>.
    /// </summary>
    [AllowCopy]
    internal static ClusterEnumerator<TArch> CreateScoped(ArchetypeClusterState state, ArchetypeMetadata meta, ChunkBasedSegment<PersistentStore> segment, 
        ChunkBasedSegment<TransientStore> transientSegment, int[] clusterIds, int startIndex, int endIndex)
    {
        ArgumentNullException.ThrowIfNull(clusterIds);
        var result = new ClusterEnumerator<TArch> { _state = state, _meta = meta };
        if (segment != null)
        {
            result._accessor = segment.CreateChunkAccessor();
            result._hasPersistentAccessor = true;
        }
        if (transientSegment != null)
        {
            result._transientAccessor = transientSegment.CreateChunkAccessor();
            result._hasTransientAccessor = true;
        }
        result._clusterIds = clusterIds;
        result._index = startIndex - 1;
        result._endIndex = endIndex;
        return result;
    }

    /// <summary>The chunk ID of the current cluster. Available after <see cref="MoveNext"/> returns true.</summary>
    public int CurrentChunkId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _clusterIds[_index];
    }

    /// <summary>
    /// Mark all occupied slots in the current cluster as dirty. Call this after writing to component data via
    /// <see cref="ClusterRef{TArch}.GetSpan{T}"/> — the direct cluster path does not set dirty bits automatically.
    /// Without this call, <c>DetectClusterMigrations</c> and the WAL tick fence will not see the changes.
    /// </summary>
    public void MarkCurrentDirty()
    {
        var chunkId = _clusterIds[_index];
        var basePtr = _hasPersistentAccessor ? _accessor.GetChunkAddress(chunkId) : _transientAccessor.GetChunkAddress(chunkId);
        var occupancy = *(ulong*)basePtr;
        while (occupancy != 0)
        {
            var slot = BitOperations.TrailingZeroCount(occupancy);
            occupancy &= occupancy - 1;
            _state.SetDirty(chunkId, slot);
        }
    }

    /// <summary>
    /// Mark a single slot in the current cluster as dirty. More precise than <see cref="MarkCurrentDirty"/> —
    /// use when only specific entities changed (e.g., after a cell-boundary crossing check). The slot index
    /// is the bit position from the <see cref="ClusterRef{TArch}.OccupancyBits"/> TZCNT loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkSlotDirty(int slotIndex) => _state.SetDirty(_clusterIds[_index], slotIndex);

    /// <summary>
    /// Advance to the next active cluster in the range, skipping drained clusters (<see cref="ClusterRef{TArch}.OccupancyBits"/> == 0) left in
    /// <c>ActiveClusterIds</c> by the fence's deferred-drain window. Guarantees <c>Current.OccupancyBits != 0</c> when returning true.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        while (++_index < _endIndex)
        {
            var chunkId = _clusterIds[_index];
            var basePtr = _hasPersistentAccessor ? _accessor.GetChunkAddress(chunkId) : _transientAccessor.GetChunkAddress(chunkId);
            if (*(ulong*)basePtr != 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Get the current cluster ref.</summary>
    public ClusterRef<TArch> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var chunkId = _clusterIds[_index];
            // Primary base: PersistentStore for mixed/SV, TransientStore for pure-Transient
            var basePtr = _hasPersistentAccessor ? _accessor.GetChunkAddress(chunkId) : _transientAccessor.GetChunkAddress(chunkId);
            // TransientStore base for mixed archetypes (null for pure-SV/V and pure-Transient)
            var transientPtr = (_hasTransientAccessor && _hasPersistentAccessor) ? _transientAccessor.GetChunkAddress(chunkId) : null;
            return new ClusterRef<TArch>(basePtr, transientPtr, _state.Layout, _meta, chunkId, _state);
        }
    }

    /// <summary>Release the ChunkAccessors.</summary>
    public void Dispose()
    {
        if (_hasPersistentAccessor)
        {
            _accessor.Dispose();
        }
        if (_hasTransientAccessor)
        {
            _transientAccessor.Dispose();
        }
    }

    /// <summary>Enable foreach.</summary>
    public ClusterEnumerator<TArch> GetEnumerator() => this;
}
