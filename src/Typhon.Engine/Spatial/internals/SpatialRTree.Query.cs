using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>The identity of one entry whose stored box satisfied a query.</summary>
internal readonly struct SpatialQueryResult
{
    /// <summary>
    /// The value the tree was given as the entry's identity. <b>Not necessarily an entity id</b> — the tree is generic over its payload, and #872 step 9 puts
    /// CLUSTER chunk ids in it for the per-cell cluster trees. The field was called <c>EntityId</c> until then, which read as a type guarantee it never made
    /// and cost a reviewer an hour concluding the cluster trees were indexing entities.
    /// </summary>
    public readonly long PayloadId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpatialQueryResult(long payloadId) => PayloadId = payloadId;
}

/// <summary>Stack buffer for DFS traversal of the R-Tree during AABB queries.</summary>
[InlineArray(256)]
internal struct QueryStackBuffer
{
    private int _element0;
}

internal unsafe partial class SpatialRTree<TStore>
{
    /// <summary>
    /// Query all entities whose fat AABB overlaps the given query box.
    /// Returns a ref struct enumerator suitable for foreach.
    /// </summary>
    /// <param name="queryCoords">CoordCount doubles: [min0, min1, ..., max0, max1, ...]</param>
    /// <param name="changeSet">ChangeSet for page access tracking</param>
    /// <param name="categoryMask">
    /// Category bitmask; when non-zero, only entities whose category mask contains all of these bits match. Pass <c>0</c> (default) to disable category filtering.
    /// </param>
    /// <remarks>
    /// <paramref name="queryCoords"/> is <c>scoped</c>: the enumerator copies it into its own inline buffer and never retains the caller's memory. Saying so
    /// is what lets a caller pass a <c>stackalloc</c> span and then hold the enumerator — which the full-extent walk in <c>CellClusterTree</c> needs, and which
    /// ref-safety otherwise refuses on the assumption that the span escapes.
    /// </remarks>
    internal AABBQueryEnumerator QueryAABB(scoped ReadOnlySpan<double> queryCoords, ChangeSet changeSet = null, uint categoryMask = 0)
        => new(this, queryCoords, changeSet, categoryMask);

    /// <summary>
    /// An AABB query that borrows the caller's accessor instead of creating one.
    /// </summary>
    /// <remarks>
    /// The accessor must outlive the returned enumerator — normally it is a local in the scope containing the <c>foreach</c>,
    /// and ref-safety analysis enforces it. Reusing one across many queries is what keeps its page window warm; see
    /// <c>AABBQueryEnumerator._borrowed</c>.
    /// </remarks>
    internal AABBQueryEnumerator QueryAABBWith(scoped ReadOnlySpan<double> queryCoords, ref ChunkAccessor<TStore> accessor, uint categoryMask = 0)
        => new(this, queryCoords, ref accessor, categoryMask);

    /// <summary>An AABB query stated in f32, the width every caller of a cell cluster tree already holds.</summary>
    internal AABBQueryEnumerator QueryAABBF32(float minX, float minY, float minZ, float maxX, float maxY, float maxZ, uint categoryMask)
        => new(this, minX, minY, minZ, maxX, maxY, maxZ, categoryMask, ref Unsafe.NullRef<ChunkAccessor<TStore>>());

    /// <inheritdoc cref="QueryAABBF32"/>
    internal AABBQueryEnumerator QueryAABBF32With(float minX, float minY, float minZ, float maxX, float maxY, float maxZ, uint categoryMask,
        ref ChunkAccessor<TStore> accessor)
        => new(this, minX, minY, minZ, maxX, maxY, maxZ, categoryMask, ref accessor);

    /// <summary>
    /// Ref struct enumerator for AABB overlap queries. Uses stack-based DFS with OLC read validation per node. Zero heap allocations.
    /// </summary>
    internal ref struct AABBQueryEnumerator
    {
        private readonly SpatialRTree<TStore> _tree;
        private ChunkAccessor<TStore> _accessor;

        /// <summary>
        /// The caller's accessor when it lent one; a null ref when <see cref="_accessor"/> is ours to create and dispose.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this exists.</b> An accessor created per query starts with an empty 32-slot page window, so the first
        /// <c>GetChunkAddress</c> of every query takes the eviction-and-load slow path — measured as exactly one
        /// <c>LoadAndGet → FindEvictionSlot → EvictSlot → LoadIntoSlot → RequestPageEpoch</c> chain per query, ~21% of a small
        /// query's time. The B+Tree never pays it: its read path takes the caller's accessor
        /// (<c>BTree.TryGet(TKey, ref ChunkAccessor{TStore})</c>) and its write paths rent a thread-local warm one.</para>
        /// <para>A <c>ref</c> field rather than a pointer because <see cref="ChunkAccessor{TStore}"/> holds managed references
        /// and so cannot be pointed at (CS8500). Ref-safety analysis then guarantees what the pointer form could only promise:
        /// the compiler will not let the enumerator outlive the accessor it borrowed.</para>
        /// </remarks>
        private readonly ref ChunkAccessor<TStore> _borrowed;
        private readonly SpatialNodeDescriptor _desc;

        // Query bounds stored inline (max 6 doubles for 3D)
        private fixed double _queryCoords[6];
        private readonly int _coordCount;
        private readonly uint _categoryMask;

        /// <summary>
        /// DFS stack of chunk ids to visit — a buffer borrowed from <see cref="QueryStackPool"/>, <c>null</c> until this enumerator first descends a tree.
        /// </summary>
        /// <remarks>
        /// <para><b>Why it is rented lazily rather than in the constructor, which is the whole reason this is safe.</b> <see cref="GetEnumerator"/> returns
        /// <c>this</c> — a COPY. A constructor-time rent would put the same array on both the copy and the original, and a caller writing
        /// <c>using var e = tree.QueryAABB(…);</c> and then <c>foreach (… in e)</c> disposes both, returning one buffer twice and handing it to two live
        /// enumerators. Renting on the first stack write instead means the discarded original owns nothing, and only the instance that actually iterates
        /// ever holds a buffer. <see cref="QueryStackPool.Return"/> guards the same case a second time; this is what makes it unreachable.</para>
        /// <para>Two live enumerators on one thread therefore hold two distinct arrays, which is what lets nested queries stay correct — see
        /// <see cref="QueryStackPool"/>.</para>
        /// </remarks>
        private int[] _stack;
        private int _stackTop;

        /// <summary>Proof that this instance, and not a copy of it, owns <see cref="_stack"/> — see <c>QueryStackPool.TokenSlot</c>.</summary>
        private int _stackToken;

        /// <summary>
        /// The root to seed the stack with on first descent, or <c>0</c> for an empty tree or a stack already seeded.
        /// </summary>
        /// <remarks>
        /// The constructor used to push the root directly. It cannot any more without renting, so it records the root here and the first pass through the
        /// DFS loop does the push — see <see cref="_stack"/>.
        /// </remarks>
        private int _pendingRootChunkId;

        // Current leaf iteration
        private int _currentLeafChunkId;

        /// <summary>The current leaf's chunk address, resolved once when the leaf is entered — see the resume loop in <see cref="MoveNext"/>.</summary>
        private byte* _currentLeafBase;

        /// <summary>The query box contains this leaf's MBR, so every entry in it matches and the per-entry overlap test is redundant.</summary>
        private bool _currentLeafFullyContained;

        // ── The f32 fast path ───────────────────────────────────────────────────────────────────────────────────
        //
        // Entries are stored f32 and the query box is held f64, so the scalar test widens six coordinates per entry and
        // branches on desc.CoordSize to decide how to read each one. SpatialNodeDescriptor's own doc claims its readonly
        // fields "are treated as constants after inlining" — that is only true of STATIC readonly fields; these arrive as a
        // struct copied into the tree and again into this enumerator, so CoordSize is a real load and a real branch, six
        // times per entry. Keeping a float copy of the box removes both, and makes the comparison native-width, which is
        // what lets it vectorise at all: a node's coordinate arrays are already SoA, so all 13 MinX values are contiguous.
        private readonly float _qMinXf;
        private readonly float _qMinYf;
        private readonly float _qMinZf;
        private readonly float _qMaxXf;
        private readonly float _qMaxYf;
        private readonly float _qMaxZf;

        /// <summary>3D f32 variant — the only one the cell cluster trees use, and the only one the vector path handles.</summary>
        private readonly bool _f32x3D;

        /// <summary>
        /// The float box came straight from the caller and the f64 <see cref="_queryCoords"/> array was never filled.
        /// </summary>
        /// <remarks>
        /// Callers hand this tree an f32 box; routing it through f64 and back cost eleven calls and ~9% of a small query's
        /// time — <c>QueryToCoords</c>'s two local functions, four <c>IsNaN</c> tests, two infinity tests and the outward
        /// re-rounding, none of which the vector path consumes. When the vector path is handling both leaves and internal
        /// nodes the f64 array has no reader at all, so it is not built. If either vector gate is off, the scalar test needs
        /// it, and the constructor fills it — hence the flag rather than an unconditional skip.
        /// </remarks>
        private readonly bool _floatBoxIsAuthoritative;

        /// <summary>
        /// Is the vectorised leaf scan usable here? 3D f32 only, and only while leaf capacity fits the mask.
        /// </summary>
        /// <remarks>
        /// A field, not a property: it is constant for the life of a query, and as a property it re-read two statics and a
        /// descriptor field on every node of the descent. The capacity bound is the honest limit of a <see cref="uint"/>
        /// mask — R3Df32 gives 13 at the shipped 512-byte stride, and a larger stride would raise it.
        /// </remarks>
        private readonly bool _useMask;

        /// <inheritdoc cref="_useMask"/>
        private readonly bool _useInternalMask;

        /// <summary>Bit i set = entry i of the current leaf matched. Fits a uint because leaf capacity is 13 (32 is asserted below).</summary>
        private uint _leafMatchMask;
        private int _currentLeafIndex;
        private int _currentLeafCount;

        private SpatialQueryResult _current;
        private bool _disposed;

        // Phase 3: Spatial:Query:Aabb span (Tier-2 gated). ResultCount/RestartCount filled during enumeration.
        private SpatialQueryAabbEvent _span;

        internal AABBQueryEnumerator(SpatialRTree<TStore> tree, scoped ReadOnlySpan<double> queryCoords, ChangeSet changeSet, uint categoryMask = 0)
            : this(tree, queryCoords, changeSet, categoryMask, ref Unsafe.NullRef<ChunkAccessor<TStore>>())
        {
        }

        /// <summary>Construct over an accessor the CALLER owns. See <see cref="_borrowed"/> for why this exists.</summary>
        internal AABBQueryEnumerator(SpatialRTree<TStore> tree, scoped ReadOnlySpan<double> queryCoords, ref ChunkAccessor<TStore> accessor,
            uint categoryMask)
            : this(tree, queryCoords, null, categoryMask, ref accessor)
        {
        }

        private AABBQueryEnumerator(SpatialRTree<TStore> tree, scoped ReadOnlySpan<double> queryCoords, ChangeSet changeSet, uint categoryMask,
            ref ChunkAccessor<TStore> borrowed)
        {
            _tree = tree;
            _desc = tree._desc;
            _coordCount = _desc.CoordCount;
            _borrowed = ref borrowed;
            _accessor = Unsafe.IsNullRef(ref borrowed) ? tree._segment.CreateChunkAccessor(changeSet) : default;
            _stackTop = 0;
            _currentLeafChunkId = 0;
            _currentLeafBase = null;
            _currentLeafFullyContained = false;
            _currentLeafIndex = -1;
            _currentLeafCount = 0;
            _current = default;
            _disposed = false;
            _categoryMask = categoryMask;

            int len = Math.Min(queryCoords.Length, 6);
            for (int i = 0; i < len; i++)
            {
                _queryCoords[i] = queryCoords[i];
            }

            _floatBoxIsAuthoritative = false;

            // Root recorded, not pushed — the stack is rented on first descent. See _stack.
            _stack = null;
            _stackToken = 0;
            _pendingRootChunkId = tree._rootChunkId;

            // Gated on the span's OWN flag, which is a generated `static readonly bool` — so with the profiler off the JIT drops the
            // call, the interceptor behind it and the struct it would have returned. Begun unconditionally this cost ~40 ns on every
            // query, measured by A/B; the six counter bumps below were already gated on this same flag, so nothing else changes.
            // Rounded OUTWARD. A double that lands between two floats must widen the box, never narrow it: narrowing drops a
            // cluster grazing the query edge, which SQ-01 counts as a false negative however small the margin.
            _f32x3D = _desc.CoordSize == 4 && _coordCount == 6;
            _useMask = SpatialQueryTuning.SimdLeafScan && _f32x3D && _desc.LeafCapacity <= 32;
            _useInternalMask = SpatialQueryTuning.SimdInternalScan && _f32x3D && _desc.InternalCapacity <= 32;
            _qMinXf = Down(_queryCoords[0]);
            _qMinYf = Down(_queryCoords[1]);
            _qMinZf = Down(_queryCoords[2]);
            _qMaxXf = Up(_queryCoords[3]);
            _qMaxYf = Up(_queryCoords[4]);
            _qMaxZf = Up(_queryCoords[5]);
            _leafMatchMask = 0;

            _span = SpatialQueryTuning.GateQuerySpan && !TelemetryConfig.SpatialQueryAabbActive
                ? default
                : TyphonEvent.BeginSpatialQueryAabb(categoryMask);
        }

        /// <summary>
        /// Construct from an f32 query box, which is what every caller of a cell cluster tree actually holds.
        /// </summary>
        /// <remarks>
        /// <para><b>The f64 coordinate array is not built when nothing will read it.</b> With both vector paths active the
        /// scalar overlap tests are unreachable, so <see cref="_queryCoords"/> has no reader; filling it meant widening six
        /// floats, clamping each against the tree's full extent, and rounding the result back outward — eleven calls and
        /// ~9% of a small query, measured, to produce a value nothing consumed. If either vector gate is off the scalar
        /// path needs it and it is filled, which is what <see cref="_floatBoxIsAuthoritative"/> records.</para>
        /// <para><b>Infinities are kept, not clamped.</b> The f64 path maps an open bound to ±1e30 because an infinite
        /// coordinate poisons the node-MBR arithmetic it unions into; a query box is only ever COMPARED, never unioned, so
        /// the float box can hold a true infinity and be exactly the "everything" bound it is meant to be. NaN is the one
        /// value that must not pass through — it compares false on every axis and would silently answer nothing — so it
        /// maps to the open bound on that side, matching what the f64 path does with it.</para>
        /// </remarks>
        internal AABBQueryEnumerator(SpatialRTree<TStore> tree, float minX, float minY, float minZ, float maxX, float maxY, float maxZ,
            uint categoryMask, ref ChunkAccessor<TStore> borrowed)
        {
            _tree = tree;
            _desc = tree._desc;
            _coordCount = _desc.CoordCount;
            _borrowed = ref borrowed;
            _accessor = Unsafe.IsNullRef(ref borrowed) ? tree._segment.CreateChunkAccessor(null) : default;
            _stackTop = 0;
            _currentLeafChunkId = 0;
            _currentLeafBase = null;
            _currentLeafFullyContained = false;
            _currentLeafIndex = -1;
            _currentLeafCount = 0;
            _current = default;
            _disposed = false;
            _categoryMask = categoryMask;
            _leafMatchMask = 0;

            _f32x3D = _desc.CoordSize == 4 && _coordCount == 6;
            _useMask = SpatialQueryTuning.SimdLeafScan && _f32x3D && _desc.LeafCapacity <= 32;
            _useInternalMask = SpatialQueryTuning.SimdInternalScan && _f32x3D && _desc.InternalCapacity <= 32;

            _qMinXf = float.IsNaN(minX) ? float.NegativeInfinity : minX;
            _qMinYf = float.IsNaN(minY) ? float.NegativeInfinity : minY;
            _qMinZf = float.IsNaN(minZ) ? float.NegativeInfinity : minZ;
            _qMaxXf = float.IsNaN(maxX) ? float.PositiveInfinity : maxX;
            _qMaxYf = float.IsNaN(maxY) ? float.PositiveInfinity : maxY;
            _qMaxZf = float.IsNaN(maxZ) ? float.PositiveInfinity : maxZ;

            _floatBoxIsAuthoritative = SpatialQueryTuning.DirectFloatBox && _useMask && _useInternalMask;
            if (!_floatBoxIsAuthoritative)
            {
                Span<double> coords = stackalloc double[6];
                CellClusterTree.QueryToCoords(minX, minY, minZ, maxX, maxY, maxZ, coords);
                for (int i = 0; i < 6; i++)
                {
                    _queryCoords[i] = coords[i];
                }
            }

            // Root recorded, not pushed — the stack is rented on first descent. See _stack.
            _stack = null;
            _stackToken = 0;
            _pendingRootChunkId = tree._rootChunkId;

            _span = SpatialQueryTuning.GateQuerySpan && !TelemetryConfig.SpatialQueryAabbActive
                ? default
                : TyphonEvent.BeginSpatialQueryAabb(categoryMask);
        }

        public SpatialQueryResult Current => _current;

        public AABBQueryEnumerator GetEnumerator() => this;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Down(double v)
        {
            float f = (float)v;
            return f > v ? MathF.BitDecrement(f) : f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Up(double v)
        {
            float f = (float)v;
            return f < v ? MathF.BitIncrement(f) : f;
        }

        /// <summary>
        /// Test every entry of a 3D-f32 leaf at once, returning a bit per matching entry.
        /// </summary>
        /// <remarks>
        /// Six compares over contiguous float arrays, ANDed into one mask — the shape the B+Tree's <c>CountLessThan</c> has
        /// used since it was written, and which this tree's node layout has always permitted without anyone taking it. The
        /// scalar tail exists because leaf capacity is 13, not a multiple of the vector width.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private uint MatchLeafF32(byte* nodeBase, int count)
        {
            float* minX = (float*)(nodeBase + _desc.LeafCoordOffsets);
            float* minY = minX + _desc.LeafCapacity;
            float* minZ = minY + _desc.LeafCapacity;
            float* maxX = minZ + _desc.LeafCapacity;
            float* maxY = maxX + _desc.LeafCapacity;
            float* maxZ = maxY + _desc.LeafCapacity;

            uint mask = 0;
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                var qMinX = Vector256.Create(_qMinXf);
                var qMinY = Vector256.Create(_qMinYf);
                var qMinZ = Vector256.Create(_qMinZf);
                var qMaxX = Vector256.Create(_qMaxXf);
                var qMaxY = Vector256.Create(_qMaxYf);
                var qMaxZ = Vector256.Create(_qMaxZf);
                for (; i + 8 <= count; i += 8)
                {
                    var m = Vector256.GreaterThanOrEqual(Vector256.Load(maxX + i), qMinX)
                        & Vector256.LessThanOrEqual(Vector256.Load(minX + i), qMaxX)
                        & Vector256.GreaterThanOrEqual(Vector256.Load(maxY + i), qMinY)
                        & Vector256.LessThanOrEqual(Vector256.Load(minY + i), qMaxY)
                        & Vector256.GreaterThanOrEqual(Vector256.Load(maxZ + i), qMinZ)
                        & Vector256.LessThanOrEqual(Vector256.Load(minZ + i), qMaxZ);
                    mask |= m.ExtractMostSignificantBits() << i;
                }
            }

            if (Vector128.IsHardwareAccelerated)
            {
                var qMinX = Vector128.Create(_qMinXf);
                var qMinY = Vector128.Create(_qMinYf);
                var qMinZ = Vector128.Create(_qMinZf);
                var qMaxX = Vector128.Create(_qMaxXf);
                var qMaxY = Vector128.Create(_qMaxYf);
                var qMaxZ = Vector128.Create(_qMaxZf);
                for (; i + 4 <= count; i += 4)
                {
                    var m = Vector128.GreaterThanOrEqual(Vector128.Load(maxX + i), qMinX)
                        & Vector128.LessThanOrEqual(Vector128.Load(minX + i), qMaxX)
                        & Vector128.GreaterThanOrEqual(Vector128.Load(maxY + i), qMinY)
                        & Vector128.LessThanOrEqual(Vector128.Load(minY + i), qMaxY)
                        & Vector128.GreaterThanOrEqual(Vector128.Load(maxZ + i), qMinZ)
                        & Vector128.LessThanOrEqual(Vector128.Load(minZ + i), qMaxZ);
                    mask |= m.ExtractMostSignificantBits() << i;
                }
            }

            for (; i < count; i++)
            {
                if (maxX[i] >= _qMinXf && minX[i] <= _qMaxXf
                    && maxY[i] >= _qMinYf && minY[i] <= _qMaxYf
                    && maxZ[i] >= _qMinZf && minZ[i] <= _qMaxZf)
                {
                    mask |= 1u << i;
                }
            }

            return mask;
        }

        /// <summary>
        /// Classify every child of a 3D-f32 internal node at once: which overlap the query box, and which it contains outright.
        /// </summary>
        /// <remarks>
        /// The leaf scan alone left half the box work scalar — at 512 clusters and a 2% query box the traversal ran 30 internal
        /// tests against 32 leaf tests. Internal entries are SoA on the same layout as leaf entries, so the same six compares
        /// apply; containment costs one extra pair per axis over the same loaded vectors, which is why both masks come out of
        /// one pass rather than two.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private void MatchInternalF32(byte* nodeBase, int count, out uint overlaps, out uint contains)
        {
            float* minX = (float*)(nodeBase + _desc.HeaderSize);
            float* minY = minX + _desc.InternalCapacity;
            float* minZ = minY + _desc.InternalCapacity;
            float* maxX = minZ + _desc.InternalCapacity;
            float* maxY = maxX + _desc.InternalCapacity;
            float* maxZ = maxY + _desc.InternalCapacity;

            uint ov = 0;
            uint ct = 0;
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                var qMinX = Vector256.Create(_qMinXf);
                var qMinY = Vector256.Create(_qMinYf);
                var qMinZ = Vector256.Create(_qMinZf);
                var qMaxX = Vector256.Create(_qMaxXf);
                var qMaxY = Vector256.Create(_qMaxYf);
                var qMaxZ = Vector256.Create(_qMaxZf);
                for (; i + 8 <= count; i += 8)
                {
                    var loX = Vector256.Load(minX + i);
                    var loY = Vector256.Load(minY + i);
                    var loZ = Vector256.Load(minZ + i);
                    var hiX = Vector256.Load(maxX + i);
                    var hiY = Vector256.Load(maxY + i);
                    var hiZ = Vector256.Load(maxZ + i);

                    var o = Vector256.GreaterThanOrEqual(hiX, qMinX) & Vector256.LessThanOrEqual(loX, qMaxX)
                        & Vector256.GreaterThanOrEqual(hiY, qMinY) & Vector256.LessThanOrEqual(loY, qMaxY)
                        & Vector256.GreaterThanOrEqual(hiZ, qMinZ) & Vector256.LessThanOrEqual(loZ, qMaxZ);
                    var c = Vector256.GreaterThanOrEqual(loX, qMinX) & Vector256.LessThanOrEqual(hiX, qMaxX)
                        & Vector256.GreaterThanOrEqual(loY, qMinY) & Vector256.LessThanOrEqual(hiY, qMaxY)
                        & Vector256.GreaterThanOrEqual(loZ, qMinZ) & Vector256.LessThanOrEqual(hiZ, qMaxZ);
                    ov |= o.ExtractMostSignificantBits() << i;
                    ct |= c.ExtractMostSignificantBits() << i;
                }
            }

            for (; i < count; i++)
            {
                float loX = minX[i], loY = minY[i], loZ = minZ[i];
                float hiX = maxX[i], hiY = maxY[i], hiZ = maxZ[i];
                if (hiX >= _qMinXf && loX <= _qMaxXf && hiY >= _qMinYf && loY <= _qMaxYf && hiZ >= _qMinZf && loZ <= _qMaxZf)
                {
                    ov |= 1u << i;
                    if (loX >= _qMinXf && hiX <= _qMaxXf && loY >= _qMinYf && hiY <= _qMaxYf && loZ >= _qMinZf && hiZ <= _qMaxZf)
                    {
                        ct |= 1u << i;
                    }
                }
            }

            overlaps = ov;
            contains = ov & ct;
        }

        public bool MoveNext()
        {
            // Mask-driven leaf scan: the whole leaf was tested when it was entered, so this pops matches off a bitmask and
            // never touches a coordinate again.
            while (_currentLeafChunkId != 0 && _useMask)
            {
                if (_leafMatchMask == 0)
                {
                    _currentLeafChunkId = 0;
                    break;
                }

                int idx = BitOperations.TrailingZeroCount(_leafMatchMask);
                _leafMatchMask &= _leafMatchMask - 1;
                if (_categoryMask != 0
                    && (SpatialNodeHelper.ReadLeafCategoryMask(_currentLeafBase, idx, _desc) & _categoryMask) != _categoryMask)
                {
                    continue;
                }

                _current = new SpatialQueryResult(SpatialNodeHelper.ReadLeafEntityId(_currentLeafBase, idx, _desc));
                if (TelemetryConfig.SpatialQueryAabbActive && _span.ResultCount < ushort.MaxValue)
                {
                    _span.ResultCount++;
                }

                return true;
            }

            // Resume leaf scan if in progress
            while (_currentLeafChunkId != 0)
            {
                _currentLeafIndex++;
                if (_currentLeafIndex >= _currentLeafCount)
                {
                    _currentLeafChunkId = 0;
                    break;
                }

                // Resolved when the leaf was entered, not here. Nothing between two MoveNext calls touches this accessor, so the address
                // cannot move under us; CountInAABB in this same file has always held it across its leaf scan. Re-resolving per entry cost
                // one GetChunkAddress per ENTRY — 5 per query against 1 node on a 4-cluster cell, measured by trace.
                byte* leafBase = SpatialQueryTuning.HoistLeafBase ? _currentLeafBase : ChunkAddress(_currentLeafChunkId);
                if (_currentLeafFullyContained || LeafEntryOverlapsQuery(leafBase, _currentLeafIndex))
                {
                    if (_categoryMask != 0 && (SpatialNodeHelper.ReadLeafCategoryMask(leafBase, _currentLeafIndex, _desc) & _categoryMask) != _categoryMask)
                    {
                        continue;
                    }
                    _current = new SpatialQueryResult(SpatialNodeHelper.ReadLeafEntityId(leafBase, _currentLeafIndex, _desc));
                    if (TelemetryConfig.SpatialQueryAabbActive && _span.ResultCount < ushort.MaxValue)
                    {
                        _span.ResultCount++;
                    }

                    return true;
                }
            }

            // First descent: take a traversal stack and seed it with the root. Every earlier return path in this method needs a leaf this loop has already
            // entered, so nothing above can run before this — and a query that finds no candidate cell never reaches here and never rents at all, which is
            // what keeps the empty-query path free of the tree entirely.
            if (_pendingRootChunkId != 0)
            {
                _stack = QueryStackPool.Rent(out _stackToken);
                _stack[0] = _pendingRootChunkId;
                _stackTop = 1;
                _pendingRootChunkId = 0;
            }

            // DFS traversal
            while (_stackTop > 0)
            {
                // Sign-bit encoding, exactly as CountInAABB does it: bit 31 marks a subtree the query box fully contains, so every
                // descendant matches and no overlap test under it can fail. Safe because chunk ids are small positive ints.
                int raw = _stack[--_stackTop];
                bool fullyContained = (raw & FullyContainedFlag) != 0;
                int chunkId = raw & ~FullyContainedFlag;
                byte* nodeBase = ChunkAddress(chunkId);

                var latch = GetLatch(nodeBase);
                int version = latch.ReadVersion();
                if (version == 0)
                {
                    // Locked or obsolete: restart from root
                    RestartFromRoot();
                    if (TelemetryConfig.SpatialQueryAabbActive && _span.RestartCount < byte.MaxValue)
                    {
                        _span.RestartCount++;
                    }

                    continue;
                }

                bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);
                int count = SpatialNodeHelper.GetCount(nodeBase);

                if (!latch.ValidateVersion(version))
                {
                    RestartFromRoot();
                    if (TelemetryConfig.SpatialQueryAabbActive && _span.RestartCount < byte.MaxValue)
                    {
                        _span.RestartCount++;
                    }

                    continue;
                }

                // Node-level category mask pruning: skip entire node if no entries match
                if (_categoryMask != 0 && (SpatialNodeHelper.ReadUnionCategoryMask(nodeBase, _desc) & _categoryMask) == 0)
                {
                    continue;
                }

                if (isLeaf)
                {
                    // Start scanning this leaf
                    _currentLeafChunkId = chunkId;
                    _currentLeafBase = nodeBase;
                    _currentLeafFullyContained = fullyContained;
                    _currentLeafIndex = -1;
                    _currentLeafCount = count;
                    if (_useMask)
                    {
                        // A contained leaf needs no test at all: every entry matches, so the mask is simply "all of them".
                        _leafMatchMask = fullyContained
                            ? (count >= 32 ? uint.MaxValue : (1u << count) - 1u)
                            : MatchLeafF32(nodeBase, count);
                    }
                    if (TelemetryConfig.SpatialQueryAabbActive && _span.LeavesEntered < ushort.MaxValue)
                    {
                        _span.LeavesEntered++;
                    }

                    return MoveNext(); // Re-enter to scan leaf entries
                }

                // Internal node: push overlapping children (reverse order for DFS), each tagged with whether the query box
                // contains it outright. A contained child's whole subtree is answered without another comparison.
                if (_useInternalMask && !fullyContained)
                {
                    // Descending index preserved: the DFS visits children in ascending order because they are pushed in
                    // reverse, and the differential fixtures compare id SETS but the demotion rebuild reads this order.
                    MatchInternalF32(nodeBase, count, out uint overlaps, out uint contains);
                    for (int i = count - 1; i >= 0; i--)
                    {
                        if ((overlaps & (1u << i)) == 0)
                        {
                            continue;
                        }

                        PushChild(
                            SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc),
                            SpatialQueryTuning.FullyContained && (contains & (1u << i)) != 0);
                    }
                }
                else
                {
                    for (int i = count - 1; i >= 0; i--)
                    {
                        if (fullyContained)
                        {
                            PushChild(SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc), true);
                            continue;
                        }

                        if (InternalEntryOverlapsQuery(nodeBase, i))
                        {
                            int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                            PushChild(childId, SpatialQueryTuning.FullyContained && InternalEntryInsideQuery(nodeBase, i));
                        }
                    }
                }
                if (TelemetryConfig.SpatialQueryAabbActive && _span.NodesVisited < ushort.MaxValue)
                {
                    _span.NodesVisited++;
                }

                if (!latch.ValidateVersion(version))
                {
                    RestartFromRoot();
                    if (TelemetryConfig.SpatialQueryAabbActive && _span.RestartCount < byte.MaxValue)
                    {
                        _span.RestartCount++;
                    }
                }
            }

            return false;
        }

        /// <summary>Bit 31 of a DFS stack entry: the query box contains this subtree entirely.</summary>
        private const int FullyContainedFlag = unchecked((int)0x80000000);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PushChild(int childId, bool contained)
        {
            if (_stackTop < QueryStackPool.Capacity)
            {
                _stack[_stackTop++] = contained ? childId | FullyContainedFlag : childId;
            }
            else
            {
                // Tier-0 always-on record (#422): latch-safe — never throw here (we hold an OLC read latch).
                SpatialRTreeDiagnostics.RecordDfsStackOverflow("AABB");
            }
        }

        /// <summary>Is this internal entry's box entirely INSIDE the query box? The containment counterpart of <see cref="InternalEntryOverlapsQuery"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool InternalEntryInsideQuery(byte* nodeBase, int index)
        {
            if (_coordCount == 4)
            {
                return SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 0, _desc) >= _queryCoords[0]
                    && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 2, _desc) <= _queryCoords[2]
                    && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 1, _desc) >= _queryCoords[1]
                    && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 3, _desc) <= _queryCoords[3];
            }

            return SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 0, _desc) >= _queryCoords[0]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 3, _desc) <= _queryCoords[3]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 1, _desc) >= _queryCoords[1]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 4, _desc) <= _queryCoords[4]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 2, _desc) >= _queryCoords[2]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 5, _desc) <= _queryCoords[5];
        }

        /// <summary>Resolve a chunk through whichever accessor this enumerator is using.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [UnscopedRef]
        private byte* ChunkAddress(int chunkId) =>
            Unsafe.IsNullRef(ref _borrowed) ? _accessor.GetChunkAddress(chunkId) : _borrowed.GetChunkAddress(chunkId);

        private void RestartFromRoot()
        {
            _stackTop = 0;
            _currentLeafChunkId = 0;
            _currentLeafBase = null;
            _currentLeafFullyContained = false;
            if (_tree._rootChunkId != 0)
            {
                // Only ever reached from inside the DFS loop, which rents before it runs — the null-coalesce is for the refactor that moves a caller, not for
                // a path that exists today.
                if (_stack == null)
                {
                    _stack = QueryStackPool.Rent(out _stackToken);
                }

                _stack[0] = _tree._rootChunkId;
                _stackTop = 1;
            }
        }

        /// <summary>Separating-axis AABB overlap test for leaf entries.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool LeafEntryOverlapsQuery(byte* nodeBase, int index)
        {
            if (_coordCount == 4)
            {
                // 2D fast path: fully unrolled, no loop
                return SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 2, _desc) >= _queryCoords[0]
                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 0, _desc) <= _queryCoords[2]
                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 3, _desc) >= _queryCoords[1]
                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 1, _desc) <= _queryCoords[3];
            }

            // 3D fast path: fully unrolled
            return SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 3, _desc) >= _queryCoords[0]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 0, _desc) <= _queryCoords[3]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 4, _desc) >= _queryCoords[1]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 1, _desc) <= _queryCoords[4]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 5, _desc) >= _queryCoords[2]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 2, _desc) <= _queryCoords[5];
        }

        /// <summary>Separating-axis AABB overlap test for internal node entries.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool InternalEntryOverlapsQuery(byte* nodeBase, int index)
        {
            if (_coordCount == 4)
            {
                // 2D fast path: fully unrolled
                return SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 2, _desc) >= _queryCoords[0]
                    && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 0, _desc) <= _queryCoords[2]
                    && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 3, _desc) >= _queryCoords[1]
                    && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 1, _desc) <= _queryCoords[3];
            }

            // 3D fast path: fully unrolled
            return SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 3, _desc) >= _queryCoords[0]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 0, _desc) <= _queryCoords[3]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 4, _desc) >= _queryCoords[1]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 1, _desc) <= _queryCoords[4]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 5, _desc) >= _queryCoords[2]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 2, _desc) <= _queryCoords[5];
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                // Nulled before it is handed back, so this instance can never name a buffer the pool has already re-issued. A query that never descended a
                // tree rented nothing and returns nothing.
                var stack = _stack;
                var token = _stackToken;
                _stack = null;
                _stackToken = 0;
                QueryStackPool.Return(stack, token);

                _span.Dispose();
                if (Unsafe.IsNullRef(ref _borrowed))
                {
                    _accessor.Dispose();   // a borrowed one belongs to the caller and outlives us
                }
            }
        }
    }

    // ── Radius Query ─────────────────────────────────────────────────────

    /// <summary>
    /// Query all entities whose fat AABB overlaps a sphere defined by center + radius.
    /// Converts to AABB query internally. False positive rate: ~21% (2D), ~48% (3D) — caller post-filters.
    /// </summary>
    internal RadiusEnumerator QueryRadius(ReadOnlySpan<double> center, double radius, ChangeSet changeSet = null, uint categoryMask = 0)
        => new(this, center, radius, changeSet, categoryMask);

    internal ref struct RadiusEnumerator
    {
        private AABBQueryEnumerator _inner;
        private SpatialQueryRadiusEvent _span;
        private bool _disposed;

        internal RadiusEnumerator(SpatialRTree<TStore> tree, ReadOnlySpan<double> center, double radius, ChangeSet changeSet, uint categoryMask = 0)
        {
            radius = Math.Max(radius, 0); // Clamp negative radius to empty query
            int halfCoord = tree._desc.CoordCount / 2;
            Span<double> aabb = stackalloc double[tree._desc.CoordCount];
            for (int d = 0; d < halfCoord; d++)
            {
                aabb[d] = center[d] - radius;
                aabb[d + halfCoord] = center[d] + radius;
            }
#pragma warning disable CS9080 // Use of variable in this context may expose referenced variables outside of their declaration scope
            _inner = new AABBQueryEnumerator(tree, aabb, changeSet, categoryMask);
#pragma warning restore CS9080 // Use of variable in this context may expose referenced variables outside of their declaration scope
            _disposed = false;
            _span = TyphonEvent.BeginSpatialQueryRadius((float)radius);
        }

        public SpatialQueryResult Current => _inner.Current;
        public RadiusEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            var hit = _inner.MoveNext();
            if (hit && TelemetryConfig.SpatialQueryRadiusActive && _span.ResultCount < ushort.MaxValue)
            {
                _span.ResultCount++;
            }

            return hit;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _span.Dispose();
                _inner.Dispose();
            }
        }
    }

    // ── Ray Query ────────────────────────────────────────────────────────

    /// <summary>
    /// Query entities whose fat AABB intersects a ray, yielding results in front-to-back order.
    /// Uses a min-heap sorted by ray entry distance for priority traversal.
    /// </summary>
    internal RayEnumerator QueryRay(ReadOnlySpan<double> origin, ReadOnlySpan<double> direction, double maxDist, ChangeSet changeSet = null,
        uint categoryMask = 0) => new(this, origin, direction, maxDist, changeSet, categoryMask);

    /// <summary>Inline min-heap buffer for the ray priority queue. Overflow spills to <see cref="ArrayPool{T}"/> — a fast path, not a limit.</summary>
    [InlineArray(SpatialRTreeConstants.RayHeapInlineCapacity)]
    internal struct RayHeapChunkIds { private int _element0; }

    [InlineArray(SpatialRTreeConstants.RayHeapInlineCapacity)]
    internal struct RayHeapDistances { private double _element0; }

    internal ref struct RayEnumerator
    {
        private readonly SpatialRTree<TStore> _tree;
        private ChunkAccessor<TStore> _accessor;
        private readonly SpatialNodeDescriptor _desc;
        private readonly double _maxDist;
        private readonly uint _categoryMask;

        // Ray parameters stored as fixed arrays (origin + inverse direction, max 3 dimensions)
        private fixed double _origin[3];
        private fixed double _invDir[3];
        private readonly int _coordCount;

        // Min-heap of (chunkId, tEntry). The inline buffers are the zero-allocation fast path; once the traversal frontier outgrows them the heap spills to
        // pooled arrays and _spillChunkIds becomes the active storage (see TryGrowHeap). _heapCapacity always describes the ACTIVE storage.
        private RayHeapChunkIds _heapChunkIds;
        private RayHeapDistances _heapDists;
        private int[] _spillChunkIds;
        private double[] _spillDists;
        private int _heapCapacity;
        private int _heapSize;

        // Current leaf iteration
        private int _currentLeafChunkId;
        private int _currentLeafIndex;
        private int _currentLeafCount;

        private SpatialQueryResult _current;
        private bool _disposed;

        // Phase 3: Spatial:Query:Ray span (Tier-2 gated).
        private SpatialQueryRayEvent _span;

        internal RayEnumerator(SpatialRTree<TStore> tree, ReadOnlySpan<double> origin, ReadOnlySpan<double> direction, double maxDist,
            ChangeSet changeSet, uint categoryMask = 0)
        {
            _tree = tree;
            _desc = tree._desc;
            _coordCount = _desc.CoordCount;
            _accessor = tree._segment.CreateChunkAccessor(changeSet);
            _maxDist = maxDist;
            _heapSize = 0;
            _spillChunkIds = null;
            _spillDists = null;
            _heapCapacity = SpatialRTreeConstants.RayHeapInlineCapacity;
            _currentLeafChunkId = 0;
            _currentLeafIndex = -1;
            _currentLeafCount = 0;
            _current = default;
            _disposed = false;
            _categoryMask = categoryMask;
            _span = TyphonEvent.BeginSpatialQueryRay((float)maxDist);

            int halfCoordInit = _desc.CoordCount / 2;
            bool degenerate = double.IsNaN(maxDist) || maxDist < 0;
            for (int d = 0; d < halfCoordInit; d++)
            {
                _origin[d] = d < origin.Length ? origin[d] : 0;
                double dir = d < direction.Length ? direction[d] : 0;
                _invDir[d] = dir != 0 ? 1.0 / dir : double.MaxValue;
                degenerate |= double.IsNaN(_origin[d]) || double.IsNaN(_invDir[d]);
            }

            if (tree._rootChunkId != 0 && !degenerate)
            {
                // A single element into an empty heap: sift-up is a no-op, so write slot 0 of the inline buffer directly. No spill can exist yet, which is
                // what lets this skip the span plumbing the rest of the heap operations need.
                _heapChunkIds[0] = tree._rootChunkId;
                _heapDists[0] = 0.0;
                _heapSize = 1;
            }
        }

        /// <summary>
        /// The heap's active storage: the inline buffer until a spill happens, the pooled arrays afterwards.
        /// </summary>
        /// <remarks>
        /// Deliberately recomputed inside each live call rather than cached in a field. <see cref="GetEnumerator"/> returns <c>this</c> <b>by value</b>, so a
        /// span captured at construction would survive into the copy while pointing at the original's inline buffer — a dead temporary. Creating it against
        /// the live <c>this</c> and passing it down as a parameter (never storing, never returning it) keeps the copy correct.
        /// </remarks>
        [UnscopedRef]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Span<int> HeapIds() => _spillChunkIds != null ? _spillChunkIds.AsSpan(0, _heapCapacity)
                : MemoryMarshal.CreateSpan(ref Unsafe.As<RayHeapChunkIds, int>(ref _heapChunkIds), SpatialRTreeConstants.RayHeapInlineCapacity);

        [UnscopedRef]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Span<double> HeapDists()
            => _spillDists != null ? _spillDists.AsSpan(0, _heapCapacity)
                : MemoryMarshal.CreateSpan(ref Unsafe.As<RayHeapDistances, double>(ref _heapDists), SpatialRTreeConstants.RayHeapInlineCapacity);

        /// <summary>
        /// Double the heap into pooled arrays, copying the live entries across. Returns false only at <see cref="SpatialRTreeConstants.MaxRayHeapCapacity"/>,
        /// having already recorded the overflow — the caller then drops the child, which is the pre-#589 behaviour kept as a last resort for corrupt trees.
        /// </summary>
        /// <remarks>
        /// Renting here is safe under the traversal's optimistic-concurrency protocol: <see cref="OlcLatch.ReadVersion"/> only snapshots a version and holds
        /// nothing, so an allocation (or a throw) on this path cannot leak a latch.
        /// </remarks>
        private bool TryGrowHeap()
        {
            if (_heapCapacity >= SpatialRTreeConstants.MaxRayHeapCapacity)
            {
                SpatialRTreeDiagnostics.RecordDfsStackOverflow("ray");
                return false;
            }

            int requested = Math.Min(_heapCapacity * 2, SpatialRTreeConstants.MaxRayHeapCapacity);
            var newIds = ArrayPool<int>.Shared.Rent(requested);
            var newDists = ArrayPool<double>.Shared.Rent(requested);

            // Counted only once both rentals succeeded, so the figure stays a count of growths rather than of attempts.
            Interlocked.Increment(ref SpatialRTreeDiagnostics.RayHeapSpillCount);

            HeapIds()[.._heapSize].CopyTo(newIds);
            HeapDists()[.._heapSize].CopyTo(newDists);

            ReturnSpillBuffers();

            _spillChunkIds = newIds;
            _spillDists = newDists;

            // Rent may hand back a larger array than requested — take the capacity actually available, but never past the ceiling.
            _heapCapacity = Math.Min(Math.Min(newIds.Length, newDists.Length), SpatialRTreeConstants.MaxRayHeapCapacity);
            return true;
        }

        private void ReturnSpillBuffers()
        {
            if (_spillChunkIds != null)
            {
                ArrayPool<int>.Shared.Return(_spillChunkIds);
                _spillChunkIds = null;
            }

            if (_spillDists != null)
            {
                ArrayPool<double>.Shared.Return(_spillDists);
                _spillDists = null;
            }
        }

        public SpatialQueryResult Current => _current;
        public RayEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            // Hoist stackalloc buffers outside all loops
            int halfCoord = _coordCount / 2;
            Span<double> coords = stackalloc double[_coordCount];

            // Pin fixed arrays directly — avoids stackalloc + copy per MoveNext() call
            fixed (double* pOrigin = _origin)
            fixed (double* pInvDir = _invDir)
            {
                var origin = new ReadOnlySpan<double>(pOrigin, halfCoord);
                var invDir = new ReadOnlySpan<double>(pInvDir, halfCoord);

                // Resume leaf scan if in progress
                while (_currentLeafChunkId != 0)
                {
                    _currentLeafIndex++;
                    if (_currentLeafIndex >= _currentLeafCount)
                    {
                        _currentLeafChunkId = 0;
                        break;
                    }

                    byte* leafBase = _accessor.GetChunkAddress(_currentLeafChunkId);
                    SpatialNodeHelper.ReadLeafEntryCoords(leafBase, _currentLeafIndex, coords, _desc);

                    var (hit, t) = SpatialGeometry.RayAABBIntersect(origin, invDir, coords, _coordCount);
                    if (hit && t <= _maxDist)
                    {
                        if (_categoryMask != 0 && (SpatialNodeHelper.ReadLeafCategoryMask(leafBase, _currentLeafIndex, _desc) & _categoryMask) != _categoryMask)
                        {
                            continue;
                        }
                        _current = new SpatialQueryResult(SpatialNodeHelper.ReadLeafEntityId(leafBase, _currentLeafIndex, _desc));
                        if (TelemetryConfig.SpatialQueryRayActive && _span.ResultCount < ushort.MaxValue)
                        {
                            _span.ResultCount++;
                        }

                        return true;
                    }
                }

                // Priority queue traversal
                while (_heapSize > 0)
                {
                    double nextDist = _heapDists[0];
                    if (nextDist > _maxDist)
                    {
                        break; // Early termination
                    }

                    int chunkId = HeapPop();
                    byte* nodeBase = _accessor.GetChunkAddress(chunkId);

                    var latch = GetLatch(nodeBase);
                    int version = latch.ReadVersion();
                    if (version == 0)
                    {
                        RestartFromRoot();
                        if (TelemetryConfig.SpatialQueryRayActive && _span.RestartCount < byte.MaxValue)
                        {
                            _span.RestartCount++;
                        }

                        continue;
                    }

                    bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);
                    int count = SpatialNodeHelper.GetCount(nodeBase);

                    if (!latch.ValidateVersion(version))
                    {
                        RestartFromRoot();
                        if (TelemetryConfig.SpatialQueryRayActive && _span.RestartCount < byte.MaxValue)
                        {
                            _span.RestartCount++;
                        }

                        continue;
                    }

                    // Node-level category mask pruning
                    if (_categoryMask != 0 && (SpatialNodeHelper.ReadUnionCategoryMask(nodeBase, _desc) & _categoryMask) == 0)
                    {
                        continue;
                    }

                    if (TelemetryConfig.SpatialQueryRayActive && _span.NodesVisited < ushort.MaxValue)
                    {
                        _span.NodesVisited++;
                    }

                    if (isLeaf)
                    {
                        _currentLeafChunkId = chunkId;
                        _currentLeafIndex = -1;
                        _currentLeafCount = count;
                        return MoveNext();
                    }

                    // Internal node: push children with their ray entry distances
                    for (int i = 0; i < count; i++)
                    {
                        SpatialNodeHelper.ReadInternalEntryCoords(nodeBase, i, coords, _desc);
                        var (hit, t) = SpatialGeometry.RayAABBIntersect(origin, invDir, coords, _coordCount);
                        // The heap grows on demand (#589). Folding a fixed capacity into this condition — as the original `&& _heapSize < 64` did — drops a
                        // child the ray genuinely hits, and with it the whole subtree beneath: a silently incomplete result, violating SQ-01. TryGrowHeap
                        // returns false only at the corrupt-tree ceiling, and records the overflow before it does.
                        if (hit && t <= _maxDist)
                        {
                            if (_heapSize == _heapCapacity && !TryGrowHeap())
                            {
                                continue;
                            }

                            int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                            HeapPush(childId, t);
                        }
                    }

                    if (!latch.ValidateVersion(version))
                    {
                        RestartFromRoot();
                        if (TelemetryConfig.SpatialQueryRayActive && _span.RestartCount < byte.MaxValue)
                        {
                            _span.RestartCount++;
                        }
                    }
                }

                return false;
            } // fixed (_origin, _invDir)
        }

        private void RestartFromRoot()
        {
            // Any spill buffer is deliberately retained across a restart: the retraversal will need the same capacity, and returning it here would churn the
            // pool once per OLC validation failure.
            _heapSize = 0;
            _currentLeafChunkId = 0;
            if (_tree._rootChunkId != 0)
            {
                HeapPush(_tree._rootChunkId, 0.0);
            }
        }

        /// <summary>Push a node. The caller must have ensured spare capacity (see the push site in <see cref="MoveNext"/>).</summary>
        private void HeapPush(int chunkId, double dist)
        {
            var ids = HeapIds();
            var dists = HeapDists();

            int i = _heapSize++;
            ids[i] = chunkId;
            dists[i] = dist;
            // Sift up
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (dists[parent] <= dists[i])
                {
                    break;
                }
                (ids[parent], ids[i]) = (ids[i], ids[parent]);
                (dists[parent], dists[i]) = (dists[i], dists[parent]);
                i = parent;
            }
        }

        private int HeapPop()
        {
            var ids = HeapIds();
            var dists = HeapDists();

            int result = ids[0];
            _heapSize--;
            if (_heapSize > 0)
            {
                ids[0] = ids[_heapSize];
                dists[0] = dists[_heapSize];
                // Sift down
                int i = 0;
                while (true)
                {
                    int left = 2 * i + 1;
                    int right = 2 * i + 2;
                    int smallest = i;
                    if (left < _heapSize && dists[left] < dists[smallest])
                    {
                        smallest = left;
                    }
                    if (right < _heapSize && dists[right] < dists[smallest])
                    {
                        smallest = right;
                    }
                    if (smallest == i)
                    {
                        break;
                    }
                    (ids[i], ids[smallest]) = (ids[smallest], ids[i]);
                    (dists[i], dists[smallest]) = (dists[smallest], dists[i]);
                    i = smallest;
                }
            }
            return result;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                // Runs even when the caller breaks out of the foreach or MoveNext throws — both go through foreach's finally.
                ReturnSpillBuffers();
                _span.Dispose();
                _accessor.Dispose();
            }
        }
    }

    // ── Frustum Query ────────────────────────────────────────────────────

    /// <summary>
    /// Query entities whose fat AABB intersects a frustum defined by a set of half-space planes.
    /// Optimizes with INSIDE subtree yields (entire subtree visible → skip per-entry plane tests).
    /// Planes packed as (normalX, normalY, [normalZ,] distance), dimCount+1 doubles per plane.
    /// </summary>
    internal FrustumEnumerator QueryFrustum(ReadOnlySpan<double> planes, int planeCount, ChangeSet changeSet = null, uint categoryMask = 0)
        => new(this, planes, planeCount, changeSet, categoryMask);

    /// <summary>Stack buffer for frustum DFS — encodes (chunkId, fullyInside) via sign bit.</summary>
    [InlineArray(256)]
    internal struct FrustumStackBuffer { private int _element0; }

    internal ref struct FrustumEnumerator
    {
        private readonly SpatialRTree<TStore> _tree;
        private ChunkAccessor<TStore> _accessor;
        private readonly SpatialNodeDescriptor _desc;
        private readonly int _planeCount;
        private readonly int _dimCount;
        private readonly int _planeDataLen; // _planeCount * (_dimCount + 1)
        private readonly uint _categoryMask;

        // Planes stored inline: max 6 planes × 4 doubles = 24 doubles
        private fixed double _planes[24];

        // DFS stack — sign bit encodes fullyInside flag
        private FrustumStackBuffer _stack;
        private int _stackTop;

        // Current leaf iteration
        private int _currentLeafChunkId;
        private int _currentLeafIndex;
        private int _currentLeafCount;
        private bool _currentLeafFullyInside;

        private SpatialQueryResult _current;
        private bool _disposed;

        // Phase 3: Spatial:Query:Frustum span (Tier-2 gated).
        private SpatialQueryFrustumEvent _span;

        internal FrustumEnumerator(SpatialRTree<TStore> tree, ReadOnlySpan<double> planes, int planeCount, ChangeSet changeSet, uint categoryMask = 0)
        {
            _tree = tree;
            _desc = tree._desc;
            _dimCount = _desc.CoordCount / 2;
            _planeCount = planeCount;
            _planeDataLen = planeCount * (_dimCount + 1);
            _accessor = tree._segment.CreateChunkAccessor(changeSet);
            _stackTop = 0;
            _currentLeafChunkId = 0;
            _currentLeafIndex = -1;
            _currentLeafCount = 0;
            _currentLeafFullyInside = false;
            _current = default;
            _disposed = false;
            _categoryMask = categoryMask;
            _span = TyphonEvent.BeginSpatialQueryFrustum((byte)Math.Min(planeCount, byte.MaxValue));

            int len = Math.Min(planes.Length, 24);
            for (int i = 0; i < len; i++)
            {
                _planes[i] = planes[i];
            }

            if (tree._rootChunkId != 0)
            {
                _stack[0] = tree._rootChunkId; // bit 31 clear = needs testing
                _stackTop = 1;
            }
        }

        public SpatialQueryResult Current => _current;
        public FrustumEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            // Hoist reusable coord buffer outside all loops
            Span<double> coords = stackalloc double[_desc.CoordCount];

            // Pin fixed plane array directly — avoids stackalloc + copy per MoveNext() call
            fixed (double* p = _planes)
            {
                var planeSpan = new ReadOnlySpan<double>(p, _planeDataLen);

                // Resume leaf scan
                while (_currentLeafChunkId != 0)
                {
                    _currentLeafIndex++;
                    if (_currentLeafIndex >= _currentLeafCount)
                    {
                        _currentLeafChunkId = 0;
                        break;
                    }

                    if (_currentLeafFullyInside)
                    {
                        // INSIDE optimization: yield without plane tests (but still check category mask)
                        byte* leafBase = _accessor.GetChunkAddress(_currentLeafChunkId);
                        if (_categoryMask != 0 && (SpatialNodeHelper.ReadLeafCategoryMask(leafBase, _currentLeafIndex, _desc) & _categoryMask) != _categoryMask)
                        {
                            continue;
                        }
                        _current = new SpatialQueryResult(SpatialNodeHelper.ReadLeafEntityId(leafBase, _currentLeafIndex, _desc));
                        if (TelemetryConfig.SpatialQueryFrustumActive && _span.ResultCount < ushort.MaxValue)
                        {
                            _span.ResultCount++;
                        }

                        return true;
                    }

                    // Test individual entry against frustum
                    byte* lb = _accessor.GetChunkAddress(_currentLeafChunkId);
                    SpatialNodeHelper.ReadLeafEntryCoords(lb, _currentLeafIndex, coords, _desc);

                    int cls = SpatialGeometry.ClassifyAABBAgainstPlanes(coords, planeSpan, _planeCount, _dimCount);
                    if (cls != SpatialGeometry.FrustumOutside)
                    {
                        if (_categoryMask != 0 && (SpatialNodeHelper.ReadLeafCategoryMask(lb, _currentLeafIndex, _desc) & _categoryMask) != _categoryMask)
                        {
                            continue;
                        }
                        _current = new SpatialQueryResult(SpatialNodeHelper.ReadLeafEntityId(lb, _currentLeafIndex, _desc));
                        if (TelemetryConfig.SpatialQueryFrustumActive && _span.ResultCount < ushort.MaxValue)
                        {
                            _span.ResultCount++;
                        }

                        return true;
                    }
                }

                // DFS traversal
                while (_stackTop > 0)
                {
                    int encoded = _stack[--_stackTop];
                    bool fullyInside = (encoded & unchecked((int)0x80000000)) != 0;
                    int chunkId = encoded & 0x7FFFFFFF;

                    byte* nodeBase = _accessor.GetChunkAddress(chunkId);

                    var latch = GetLatch(nodeBase);
                    int version = latch.ReadVersion();
                    if (version == 0)
                    {
                        RestartFromRoot();
                        if (TelemetryConfig.SpatialQueryFrustumActive && _span.RestartCount < byte.MaxValue)
                        {
                            _span.RestartCount++;
                        }

                        continue;
                    }

                    bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);
                    int count = SpatialNodeHelper.GetCount(nodeBase);

                    if (!latch.ValidateVersion(version))
                    {
                        RestartFromRoot();
                        if (TelemetryConfig.SpatialQueryFrustumActive && _span.RestartCount < byte.MaxValue)
                        {
                            _span.RestartCount++;
                        }

                        continue;
                    }

                    // Node-level category mask pruning
                    if (_categoryMask != 0 && (SpatialNodeHelper.ReadUnionCategoryMask(nodeBase, _desc) & _categoryMask) == 0)
                    {
                        continue;
                    }

                    if (TelemetryConfig.SpatialQueryFrustumActive && _span.NodesVisited < ushort.MaxValue)
                    {
                        _span.NodesVisited++;
                    }

                    if (isLeaf)
                    {
                        _currentLeafChunkId = chunkId;
                        _currentLeafIndex = -1;
                        _currentLeafCount = count;
                        _currentLeafFullyInside = fullyInside;
                        return MoveNext();
                    }

                    if (fullyInside)
                    {
                        // All children are fully inside — push with fullyInside flag
                        for (int i = count - 1; i >= 0; i--)
                        {
                            int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                            if (_stackTop < 256)
                            {
                                _stack[_stackTop++] = childId | unchecked((int)0x80000000); // bit 31 = fully inside
                            }
                            else
                            {
                                // Tier-0 always-on record (#422): latch-safe — never throw under the OLC read latch.
                                SpatialRTreeDiagnostics.RecordDfsStackOverflow("frustum");
                            }
                        }
                    }
                    else
                    {
                        // Classify each child
                        for (int i = count - 1; i >= 0; i--)
                        {
                            SpatialNodeHelper.ReadInternalEntryCoords(nodeBase, i, coords, _desc);
                            int cls = SpatialGeometry.ClassifyAABBAgainstPlanes(coords, planeSpan, _planeCount, _dimCount);
                            if (cls == SpatialGeometry.FrustumOutside)
                            {
                                continue;
                            }
                            int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                            if (_stackTop < 256)
                            {
                                _stack[_stackTop++] = cls == SpatialGeometry.FrustumInside ? childId | unchecked((int)0x80000000) : childId;
                            }
                            else
                            {
                                // Tier-0 always-on record (#422): latch-safe — never throw under the OLC read latch.
                                SpatialRTreeDiagnostics.RecordDfsStackOverflow("frustum");
                            }
                        }
                    }

                    if (!latch.ValidateVersion(version))
                    {
                        RestartFromRoot();
                        if (TelemetryConfig.SpatialQueryFrustumActive && _span.RestartCount < byte.MaxValue)
                        {
                            _span.RestartCount++;
                        }
                    }
                }

                return false;
            } // fixed (_planes)
        }

        private void RestartFromRoot()
        {
            _stackTop = 0;
            _currentLeafChunkId = 0;
            if (_tree._rootChunkId != 0)
            {
                _stack[0] = _tree._rootChunkId;
                _stackTop = 1;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _span.Dispose();
                _accessor.Dispose();
            }
        }
    }

    // ── kNN Query ────────────────────────────────────────────────────────

    /// <summary>
    /// Find the k nearest entity candidates to a point via iterative radius expansion.
    /// Returns entities whose fat AABB falls within the search radius. The <c>distSq</c> field is set to 0 — callers must recompute actual distances from
    /// component data (the tree stores fat AABBs, not tight bounds). Converges in 1–2 iterations for k &lt; 20.
    /// </summary>
    /// <returns>Number of results written (may be less than k if fewer entities exist).</returns>
    internal int QueryKNN(ReadOnlySpan<double> center, int k, Span<(long payloadId, double distSq)> results, ChangeSet changeSet = null, uint categoryMask = 0)
    {
        if (k <= 0 || _entityCount == 0)
        {
            return 0;
        }

        // Phase 3: Spatial:Query:Knn span (Tier-2 gated). IterCount/FinalRadius/ResultCount filled at exit.
        var knnScope = TyphonEvent.BeginSpatialQueryKnn((ushort)Math.Min(k, ushort.MaxValue));
        try
        {

            int halfCoord = _desc.CoordCount / 2;

            // Estimate initial radius from entity density
            double worldVolume = 1.0;
            if (_entityCount > 1)
            {
                // Read root node MBR to estimate world extent
                var accessor = _segment.CreateChunkAccessor(changeSet);
                try
                {
                    byte* rootBase = accessor.GetChunkAddress(_rootChunkId);
                    for (int d = 0; d < halfCoord; d++)
                    {
                        double extent = SpatialNodeHelper.ReadNodeMBRCoord(rootBase, d + halfCoord, _desc) -
                                        SpatialNodeHelper.ReadNodeMBRCoord(rootBase, d, _desc);
                        if (extent > 0)
                        {
                            worldVolume *= extent;
                        }
                    }
                }
                finally
                {
                    accessor.Dispose();
                }
            }

            double entityDensity = _entityCount / Math.Max(worldVolume, 1e-10);
            double volumeForK = k / Math.Max(entityDensity, 1e-10);
            double radius = Math.Pow(volumeForK, 1.0 / halfCoord) * 1.5; // 1.5x safety factor
            radius = Math.Max(radius, 1.0); // Minimum radius

            // Iterative expansion — collect candidate entity IDs within expanding radius. distSq is set to 0 at the tree level because the tree stores fat
            // AABBs, not tight bounds. Callers must recompute actual distances from component data for precise ordering.
            int maxCandidates = Math.Min(k * 4, 256);
            Span<(long payloadId, double distSq)> candidates = stackalloc (long, double)[maxCandidates];
            int lastCount = 0;

            for (int iteration = 0; iteration < 8; iteration++)
            {
                int count = 0;
                foreach (var result in QueryRadius(center, radius, changeSet, categoryMask))
                {
                    if (count >= candidates.Length)
                    {
                        break;
                    }
                    candidates[count++] = (result.PayloadId, 0);
                }

                if (count >= k || count == lastCount || radius > 1e15)
                {
                    int resultCount = Math.Min(count, k);
                    resultCount = Math.Min(resultCount, results.Length);
                    for (int i = 0; i < resultCount; i++)
                    {
                        results[i] = candidates[i];
                    }
                    knnScope.IterCount = (byte)Math.Min(iteration + 1, byte.MaxValue);
                    knnScope.FinalRadius = (float)radius;
                    knnScope.ResultCount = (ushort)Math.Min(resultCount, ushort.MaxValue);
                    return resultCount;
                }

                lastCount = count;
                radius *= 2.0;
            }

            // Iteration limit reached — return whatever candidates we have from the last pass
            int finalCount = Math.Min(lastCount, k);
            finalCount = Math.Min(finalCount, results.Length);
            for (int i = 0; i < finalCount; i++)
            {
                results[i] = candidates[i];
            }
            knnScope.IterCount = 8;
            knnScope.FinalRadius = (float)radius;
            knnScope.ResultCount = (ushort)Math.Min(finalCount, ushort.MaxValue);
            return finalCount;
        }
        finally
        {
            knnScope.Dispose();
        }
    }

    // ── Count Queries ────────────────────────────────────────────────────

    /// <summary>
    /// Count entities whose fat AABB overlaps the given query box without materializing results.
    /// Uses a subtree counting shortcut: when a node's MBR is fully contained within the query region, its entries are counted without per-entry overlap
    /// tests (up to ~30x faster for large fully-covered regions).
    /// </summary>
    internal int CountInAABB(ReadOnlySpan<double> queryCoords, ChangeSet changeSet = null, uint categoryMask = 0)
    {
        if (_rootChunkId == 0)
        {
            return 0;
        }

        // Phase 3: Spatial:Query:Count span (variant 0=AABB). ResultCount filled at exit.
        var countScope = TyphonEvent.BeginSpatialQueryCount(0);
        var accessor = _segment.CreateChunkAccessor(changeSet);
        try
        {
            int count = 0;
            QueryStackBuffer stack = default;
            int coordCount = _desc.CoordCount;

            // Copy query coords to stackalloc buffer for pointer-based access in hot loops
            double* qc = stackalloc double[6];
            int len = Math.Min(queryCoords.Length, 6);
            for (int i = 0; i < len; i++)
            {
                qc[i] = queryCoords[i];
            }

            // Sign-bit encoding: bit 31 marks a node as "fully contained" — all its descendants
            // are geometrically inside the query region, so overlap tests can be skipped.
            // Safe because chunk IDs are small positive ints (allocated sequentially from 0).
            const int fullyContainedFlag = unchecked((int)0x80000000);

            stack[0] = _rootChunkId;
            var stackTop = 1;

            while (stackTop > 0)
            {
                int raw = stack[--stackTop];
                bool fullyContained = (raw & fullyContainedFlag) != 0;
                int chunkId = raw & 0x7FFFFFFF;

                byte* nodeBase = accessor.GetChunkAddress(chunkId);

                var latch = GetLatch(nodeBase);
                int version = latch.ReadVersion();
                if (version == 0)
                {
                    count = 0;
                    stack[0] = _rootChunkId;
                    stackTop = 1;
                    continue;
                }

                bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);
                int nodeCount = SpatialNodeHelper.GetCount(nodeBase);

                if (!latch.ValidateVersion(version))
                {
                    count = 0;
                    stack[0] = _rootChunkId;
                    stackTop = 1;
                    continue;
                }

                // Node-level category pruning: skip entire node if no entries can match
                if (categoryMask != 0 && (SpatialNodeHelper.ReadUnionCategoryMask(nodeBase, _desc) & categoryMask) == 0)
                {
                    continue;
                }

                if (isLeaf)
                {
                    if (fullyContained && categoryMask == 0)
                    {
                        // Maximum shortcut: all entries geometrically match, no category filter
                        count += nodeCount;
                    }
                    else if (fullyContained)
                    {
                        // Fully contained but need category check (skip overlap tests)
                        for (int i = 0; i < nodeCount; i++)
                        {
                            if ((SpatialNodeHelper.ReadLeafCategoryMask(nodeBase, i, _desc) & categoryMask) == categoryMask)
                            {
                                count++;
                            }
                        }
                    }
                    else
                    {
                        // Standard path: overlap test + category test per entry
                        if (coordCount == 4)
                        {
                            // 2D unrolled leaf scan
                            for (int i = 0; i < nodeCount; i++)
                            {
                                if (SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 2, _desc) >= qc[0]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 0, _desc) <= qc[2]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 3, _desc) >= qc[1]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 1, _desc) <= qc[3])
                                {
                                    if (categoryMask == 0
                                        || (SpatialNodeHelper.ReadLeafCategoryMask(nodeBase, i, _desc) & categoryMask) == categoryMask)
                                    {
                                        count++;
                                    }
                                }
                            }
                        }
                        else
                        {
                            // 3D unrolled leaf scan
                            for (int i = 0; i < nodeCount; i++)
                            {
                                if (SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 3, _desc) >= qc[0]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 0, _desc) <= qc[3]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 4, _desc) >= qc[1]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 1, _desc) <= qc[4]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 5, _desc) >= qc[2]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 2, _desc) <= qc[5])
                                {
                                    if (categoryMask == 0
                                        || (SpatialNodeHelper.ReadLeafCategoryMask(nodeBase, i, _desc) & categoryMask) == categoryMask)
                                    {
                                        count++;
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Internal node
                    if (fullyContained)
                    {
                        // All children inherit fully-contained status
                        for (int i = nodeCount - 1; i >= 0; i--)
                        {
                            int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                            if (stackTop < 256)
                            {
                                stack[stackTop++] = childId | fullyContainedFlag;
                            }
                            else
                            {
                                // Tier-0 always-on record (#422): latch-safe — never throw under the OLC read latch.
                                SpatialRTreeDiagnostics.RecordDfsStackOverflow("count");
                            }
                        }
                    }
                    else
                    {
                        // Classify each child: disjoint / overlapping / fully contained
                        if (coordCount == 4)
                        {
                            // 2D unrolled containment classification
                            for (int i = nodeCount - 1; i >= 0; i--)
                            {
                                double cMinX = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 0, _desc);
                                double cMinY = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 1, _desc);
                                double cMaxX = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 2, _desc);
                                double cMaxY = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 3, _desc);

                                // Disjoint?
                                if (cMaxX < qc[0] || cMinX > qc[2] || cMaxY < qc[1] || cMinY > qc[3])
                                {
                                    continue;
                                }

                                int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                                if (stackTop < 256)
                                {
                                    // Fully contained?
                                    if (cMinX >= qc[0] && cMaxX <= qc[2] && cMinY >= qc[1] && cMaxY <= qc[3])
                                    {
                                        stack[stackTop++] = childId | fullyContainedFlag;
                                    }
                                    else
                                    {
                                        stack[stackTop++] = childId;
                                    }
                                }
                                else
                                {
                                    // Tier-0 always-on record (#422): latch-safe — never throw under the OLC read latch.
                                    SpatialRTreeDiagnostics.RecordDfsStackOverflow("count");
                                }
                            }
                        }
                        else
                        {
                            // 3D unrolled containment classification
                            for (int i = nodeCount - 1; i >= 0; i--)
                            {
                                double cMinX = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 0, _desc);
                                double cMinY = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 1, _desc);
                                double cMinZ = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 2, _desc);
                                double cMaxX = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 3, _desc);
                                double cMaxY = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 4, _desc);
                                double cMaxZ = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 5, _desc);

                                // Disjoint?
                                if (cMaxX < qc[0] || cMinX > qc[3] || cMaxY < qc[1] || cMinY > qc[4]
                                    || cMaxZ < qc[2] || cMinZ > qc[5])
                                {
                                    continue;
                                }

                                int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                                if (stackTop < 256)
                                {
                                    // Fully contained?
                                    if (cMinX >= qc[0] && cMaxX <= qc[3] && cMinY >= qc[1] && cMaxY <= qc[4]
                                        && cMinZ >= qc[2] && cMaxZ <= qc[5])
                                    {
                                        stack[stackTop++] = childId | fullyContainedFlag;
                                    }
                                    else
                                    {
                                        stack[stackTop++] = childId;
                                    }
                                }
                                else
                                {
                                    // Tier-0 always-on record (#422): latch-safe — never throw under the OLC read latch.
                                    SpatialRTreeDiagnostics.RecordDfsStackOverflow("count");
                                }
                            }
                        }
                    }
                }

                if (!latch.ValidateVersion(version))
                {
                    count = 0;
                    stack[0] = _rootChunkId;
                    stackTop = 1;
                }
            }

            countScope.ResultCount = count;
            return count;
        }
        finally
        {
            countScope.Dispose();
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Count entities whose fat AABB overlaps a sphere defined by center + radius.
    /// Converts to AABB query internally (same coarse filter as <see cref="RadiusEnumerator"/>).
    /// </summary>
    internal int CountInRadius(ReadOnlySpan<double> center, double radius, ChangeSet changeSet = null, uint categoryMask = 0)
    {
        // Phase 3: Spatial:Query:Count span (variant 1=Radius). Inner CountInAABB also emits its own variant=0 span.
        var countScope = TyphonEvent.BeginSpatialQueryCount(1);
        try
        {
            radius = Math.Max(radius, 0);
            int halfCoord = _desc.CoordCount / 2;
            Span<double> aabb = stackalloc double[_desc.CoordCount];
            for (int d = 0; d < halfCoord; d++)
            {
                aabb[d] = center[d] - radius;
                aabb[d + halfCoord] = center[d] + radius;
            }
            var result = CountInAABB(aabb, changeSet, categoryMask);
            countScope.ResultCount = result;
            return result;
        }
        finally
        {
            countScope.Dispose();
        }
    }
}
