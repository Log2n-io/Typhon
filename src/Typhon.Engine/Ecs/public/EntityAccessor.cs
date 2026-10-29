// EntityAccessor — lightweight base for Transaction and PointInTimeAccessor.
// Contains the minimum state needed for MVCC-correct entity reads and SV/Transient writes.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Base class providing MVCC-correct entity access at a frozen TSN. Holds the minimum state
/// needed for entity reads and SingleVersion/Transient writes: engine reference, epoch scope,
/// component accessor cache, and ChangeSet.
/// <para>
/// <see cref="Transaction"/> extends this with spawn/destroy, commit/rollback, and TransactionChain
/// insertion. <see cref="PointInTimeAccessor"/> wraps per-thread instances of this class for
/// lock-free parallel entity access.
/// </para>
/// </summary>
[PublicAPI]
public partial class EntityAccessor : IDisposable
{
    private protected const int ComponentInfosMaxCapacity = 131;

    /// <summary>
    /// Number of entity operations between epoch refreshes. Each operation touches ~4-20 pages.
    /// At 128 ops × ~10 pages/op = ~1280 pages — refreshes before saturating a 1024-page cache.
    /// </summary>
    internal const int EpochRefreshInterval = 128;

    /// <summary>
    /// The refresh interval while the page cache is under back-pressure (EP-03, #1230). The epoch keeps every page any thread touched since the oldest pin,
    /// about pinned threads × the interval × the pages an operation touches; at 128, 32 threads over a 64 MiB cache needed twice the cache. A power of two:
    /// the pressure flag is read only every this many operations.
    /// </summary>
    internal const int PressuredEpochRefreshInterval = 8;

    /// <summary>
    /// Whether the <paramref name="count"/>th operation since the last refresh refreshes: every <see cref="EpochRefreshInterval"/>, or every
    /// <see cref="PressuredEpochRefreshInterval"/> while the cache is under pressure. The flag is read once per short interval, so the cadence costs
    /// a mask and a branch per operation at rest.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected bool EpochRefreshDue(int count) =>
        (count & (PressuredEpochRefreshInterval - 1)) == 0 && (count >= EpochRefreshInterval || _dbe.MMF.UnderPressure);

    private protected bool _isDisposed;
    private protected DatabaseEngine _dbe;

    /// <summary>The engine's commit cards, through which a revision walk resolves flagged entries (VIS-01).</summary>
    internal CommitCardTable CommitCards => _dbe.CommitCards;
    internal DatabaseEngine DBE => _dbe;
    private protected EpochManager _epochManager;

    // Thread that created this accessor. Promoted out of #if DEBUG (#422) so the strict-mode thread-affinity check compiles
    // always-on. The check itself is gated by CheckConfig.Enabled, so this 4-byte field is the only unconditional cost.
    private protected int _owningThreadId;

    /// <summary>
    /// A <see cref="PointInTimeAccessor"/> worker (<see cref="InitLightweight"/>): its snapshot is not in the transaction chain, so a chain-walk failure
    /// below the retention floor is an expiry (<c>ThrowIfSnapshotExpired</c>), and it may read only inside a lease (<see cref="_leaseDepth"/>).
    /// </summary>
    private protected bool _isPointInTime;

    /// <summary>
    /// Leases open on this accessor (<see cref="PointInTimeAccessor.WorkerLease"/>), each holding an epoch scope on the owning thread. While one is, the
    /// thread is provably in scope and a resolve skips <c>EpochManager.IsCurrentThreadInScope</c> (two <c>[ThreadStatic]</c> reads plus a padded-slot
    /// load). A point-in-time worker with none open is refused (<see cref="NeedsEpochGuard"/>): a per-call guard would cover the map lookup but not the
    /// reads after it (#1532). It used to hold a scope from its first use until its thread died, and that pin froze the cache's floor.
    /// </summary>
    private protected int _leaseDepth;

    private protected Dictionary<Type, ComponentInfo> _componentInfos;

    /// <summary>Array-indexed ComponentInfo cache — O(1) lookup by componentTypeId. Avoids Dictionary hash + equality overhead on hot path.</summary>
    /// <remarks>
    /// Grown to cover every type id this accessor meets (<see cref="EnsureInfoSlot"/>). Sized once at <see cref="ComponentInfosMaxCapacity"/>, it silently
    /// dropped any id past that — a process with more component types than that sends those components down the dictionary path on every lookup, and
    /// <see cref="ResetForNewSnapshot"/>, which flushes only the entries this array holds, never committed their chunk accessors.
    /// </remarks>
    private protected ComponentInfo[] _componentInfosByTypeId;

    /// <summary>
    /// The entries this accessor held on its previous lease, by componentTypeId, waiting to be rebound. <see cref="ResetCore"/> moves them here instead of
    /// dropping them, and the slow path of <see cref="GetComponentInfo"/> rebinds one before it allocates — so a pooled transaction touching the same
    /// components every tick allocates nothing for them after its first lease.
    /// </summary>
    private protected ComponentInfo[] _recycledInfosByTypeId;

    /// <summary>How many component entries this accessor holds on its current lease — one per component type it has touched.</summary>
    internal int ComponentInfoCount => _componentInfos.Count;

    /// <summary>
    /// Cached EntityMap accessor for same-archetype repeated lookups.
    /// Reused across multiple calls targeting the same archetype. Disposed in ResetCore().
    /// </summary>
    private protected ushort _entityMapCacheArchId;

    /// <summary>
    /// How many entities this accessor destroyed and has not committed (CLUSTERWALK-04). Always 0 except on a <see cref="Transaction"/>, the only accessor
    /// that destroys; here on the base so a cluster walk reads it once per cluster with no type test.
    /// </summary>
    internal int OwnDestroyCount;

    /// <summary>The slots this accessor's own pending destroys hold in an archetype's clusters (CLUSTERWALK-04); null — none — except on a
    /// <see cref="Transaction"/>. Reached only when <see cref="OwnDestroyCount"/> moved.</summary>
    internal virtual OwnDestroyedSlots OwnDestroyedSlotsFor(ArchetypeMetadata meta, ArchetypeClusterState state) => null;
    private protected ChunkAccessor<PersistentStore> _entityMapCacheAccessor;
    private protected bool _hasEntityMapCache;

    /// <summary>
    /// Bumped whenever an accessor that resolved an <see cref="EntityRef"/>'s cluster base lets go of a page — its cluster caches here and every
    /// <see cref="ArchetypeAccessor{TArch}"/> built on this accessor (#1199). A handle resolved under an older value re-resolves its base.
    /// </summary>
    internal readonly ResolveGeneration ClusterGeneration = new();

    /// <summary>
    /// Cluster accessors for point lookups, one entry per archetype, least recently used replaced (cluster-eligible archetypes only). Several ways, not
    /// one: two live handles of different archetypes (an attacker and its target) re-resolve through here in turn, and a single entry was disposed and
    /// rebuilt on every alternation — each time invalidating every handle (#1199).
    /// </summary>
    private ClusterCache _clusterCache;
    private long _clusterCacheClock;
    private byte _clusterCacheMru;

    private protected int _entityOperationCount;
    private protected ChangeSet _changeSet;

    /// <summary>
    /// True when <see cref="_changeSet"/> was created by this accessor and must therefore be released by it.
    /// </summary>
    /// <remarks>
    /// False when the ChangeSet belongs to an enclosing unit of work, which releases it on its own dispose. Releasing
    /// another owner's marks destroys protection for work still in flight (#385); never releasing one's own strands every
    /// mark for the life of the process (#824). The flag is what keeps the two apart.
    /// </remarks>
    private protected bool _ownsChangeSet;

    /// <summary>
    /// Effective commit discipline for <see cref="StorageMode.SingleVersion"/> writes in this accessor's scope.
    /// Always <see cref="CommitDiscipline.TickFence"/> for a bare <see cref="EntityAccessor"/> (parallel workers never stage); a <see cref="Transaction"/>
    /// resolves it from the explicit argument or escalates on first touch of a <see cref="CommitDiscipline.Commit"/> component (CM-02).
    /// </summary>
    private protected CommitDiscipline _discipline;

    /// <summary>Effective commit discipline for SingleVersion writes (see <see cref="_discipline"/>).</summary>
    internal CommitDiscipline Discipline => _discipline;

    /// <summary>True once any in-place (TickFence) SingleVersion write has been applied — blocks late escalation to Commit (CM-02).</summary>
    private protected bool _didInPlaceSvWrite;

    /// <summary>Transaction sequence number defining this accessor's MVCC read snapshot; entity visibility is evaluated against this value.</summary>
    public long TSN { get; private protected set; }

    /// <summary>
    /// Prepare this accessor for mutation, inside a profiling span. Called by <see cref="ArchetypeAccessor{TArch}"/> on its first writable open; later
    /// opens call <c>PrepareOpenMut</c> directly. Base implementation is a no-op. Transaction overrides it to run <c>PrepareOpenMut</c> in the span.
    /// </summary>
    internal virtual void PrepareForMutation() { }

    /// <summary>Creates a new <see cref="EntityAccessor"/> with empty component-lookup caches; an internal init path binds it before use.</summary>
    public EntityAccessor()
    {
        _componentInfos = new Dictionary<Type, ComponentInfo>(ComponentInfosMaxCapacity);
        _componentInfosByTypeId = new ComponentInfo[ComponentInfosMaxCapacity];
        _recycledInfosByTypeId = new ComponentInfo[ComponentInfosMaxCapacity];
    }

    /// <summary>
    /// Initialize this accessor for lightweight point-in-time access: a <see cref="PointInTimeAccessor"/> worker. Creates a per-thread ChangeSet for dirty
    /// page tracking and does not insert into the TransactionChain. It enters NO epoch scope: the worker reads only inside a
    /// <see cref="PointInTimeAccessor.WorkerLease"/>, which pins the thread for its length (EP-04, #1532).
    /// </summary>
    internal void InitLightweight(DatabaseEngine dbe, long tsn)
    {
        _dbe = dbe;
        _epochManager = _dbe.EpochManager;
        _isPointInTime = true;
        _isDisposed = false;
        _owningThreadId = Environment.CurrentManagedThreadId;
        _entityOperationCount = 0;
        _changeSet = _dbe.MMF.CreateChangeSet(drainedByCheckpoint: true);
        _ownsChangeSet = true;
        TSN = tsn;
    }

    /// <summary>
    /// Whether a page read at a call boundary needs an epoch guard of its own: not under a lease (its scope covers the thread), not on a thread already
    /// in scope (a transaction's). A point-in-time worker outside every lease throws instead: it has no scope to read under (EP-04).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected bool NeedsEpochGuard()
    {
        if (_leaseDepth > 0)
        {
            return false;
        }

        if (_isPointInTime)
        {
            ThrowReadOutsideLease();
        }

        return !_epochManager.IsCurrentThreadInScope;
    }

    /// <summary>
    /// An entry point that hands out a walker over pages (<c>For</c>, <c>GetClusterEnumerator</c>) refuses a point-in-time worker with no lease open,
    /// as a resolve does (<see cref="NeedsEpochGuard"/>): what it hands out reads pages for as long as it lives, and only the lease protects them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void RequireLeaseIfPointInTime()
    {
        if (_isPointInTime && _leaseDepth == 0)
        {
            ThrowReadOutsideLease();
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowReadOutsideLease() =>
        ThrowHelper.ThrowInvalidOp("A PointInTimeAccessor worker accessor was used outside its lease. Read through `using var lease = pta.Enter(workerId);` "
            + "and `lease.Accessor`, within the lease's scope.");

    /// <summary>
    /// A lease opens on this worker accessor, on the calling thread (which already holds the lease's epoch scope). The thread becomes the owner: a worker
    /// index may be served by a different thread from one round to the next (a <c>Parallel.For</c>), never by two at once.
    /// </summary>
    internal void BeginLease()
    {
        CheckConfig.Require(CheckConfig.Enabled, _leaseDepth == 0 || _owningThreadId == Environment.CurrentManagedThreadId,
            $"PointInTimeAccessor worker accessor leased by two threads at once");
        _owningThreadId = Environment.CurrentManagedThreadId;
        _leaseDepth++;
    }

    /// <summary>The lease <see cref="BeginLease"/> opened ends; its epoch scope is exited by the lease right after.</summary>
    internal void EndLease()
    {
        CheckConfig.Require(CheckConfig.Enabled, _leaseDepth > 0, $"PointInTimeAccessor lease ended twice (a copy of it disposed)");
        if (_leaseDepth > 0)
        {
            _leaseDepth--;
        }
    }

    /// <summary>Whether a lease is open on this accessor — a reset or a dispose then would race the thread reading through it.</summary>
    internal bool IsLeased => _leaseDepth > 0;

    /// <summary>Whether a lease is open on this accessor on another thread than the caller's: a flush then would race the thread reading through it.</summary>
    internal bool IsLeasedByAnotherThread => _leaseDepth > 0 && _owningThreadId != Environment.CurrentManagedThreadId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void AssertThreadAffinity()
    {
        // Inline-guard form (not CheckConfig.Require): short-circuits on the JIT-folded gate BEFORE reading Environment.CurrentManagedThreadId, so the per-op
        // TLS read is skipped entirely when strict mode is off (the whole body folds away — CheckConfig.Enabled is a static readonly). A throw is safe
        // here — a call-boundary check, never reached while holding an OLC latch.
        if (CheckConfig.Enabled && _owningThreadId != Environment.CurrentManagedThreadId)
        {
            ThrowHelper.ThrowInvalidOp(
                "EntityAccessor thread affinity violation: current thread differs from the creating thread. " +
                "Each EntityAccessor instance must be used only from its creating thread.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Component accessor cache
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fast path: get ComponentInfo by pre-known componentTypeId. Avoids the Dictionary&lt;Type, int&gt; lookup
    /// in ArchetypeRegistry.GetComponentTypeId that GetComponentInfo(Type) would do.
    /// Falls back to the Type-based path if not yet cached.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected ComponentInfo GetComponentInfoByTypeId(int componentTypeId, Type componentType)
    {
        if (componentTypeId >= 0 && componentTypeId < _componentInfosByTypeId.Length)
        {
            var cached = _componentInfosByTypeId[componentTypeId];
            if (cached != null)
            {
                return cached;
            }
        }

        // Fall back to full creation path (only on first access)
        return GetComponentInfo(componentType);
    }

    /// <summary>
    /// The already-created <see cref="ComponentInfo"/> for <paramref name="componentTypeId"/>, or null — never creates one.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="GetComponentInfoByTypeId"/>, for callers asking "did THIS accessor touch that component?" rather than "give me its info".
    /// A non-null answer means the component was read, written or spawned here, so its <c>SingleCache</c> is worth probing; a null answer is definitive and
    /// costs one array index. Using the creating overload for that question allocates a ComponentInfo per untouched component — see the resolver's absent-slot
    /// path (#845), which asks it for every Versioned slot of every entity it opens.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected ComponentInfo TryGetExistingComponentInfo(int componentTypeId)
        => componentTypeId >= 0 && componentTypeId < _componentInfosByTypeId.Length ? _componentInfosByTypeId[componentTypeId] : null;

    private protected ComponentInfo GetComponentInfo(Type componentType)
    {
        // Fast path: array-indexed lookup by componentTypeId (avoids Dictionary hash + equality check)
        var typeId = ArchetypeRegistry.GetComponentTypeId(componentType);
        if (typeId >= 0 && typeId < _componentInfosByTypeId.Length)
        {
            var cached = _componentInfosByTypeId[typeId];
            if (cached != null)
            {
                return cached;
            }
        }

        // Slow path: create and cache
        if (_componentInfos.TryGetValue(componentType, out var info))
        {
            // Already in Dictionary but not in array (shouldn't happen, but handle gracefully)
            if (typeId >= 0)
            {
                EnsureInfoSlot(typeId);
                _componentInfosByTypeId[typeId] = info;
            }

            return info;
        }

        var ct = _dbe.GetComponentTable(componentType) ?? _dbe.FindComponentTableBySchemaName(componentType);
        if (ct == null)
        {
            throw new InvalidOperationException($"The type {componentType} doesn't have a registered Component Table");
        }

        // Resolve the component's in-memory type id. When the passed id and the componentType lookup both miss, fall back to the ComponentTable's POCOType —
        // the per-engine authority. componentType can be a duplicate Type object from a collectible ALC (#384) that never landed in the global registry,
        // whereas the table's POCOType is the registered identity the archetype metadata slot map is keyed on. (The table itself is already resolved by
        // schema name above, so it is correct regardless of which Type object keyed this lookup.)
        int resolvedTypeId = typeId;
        if (resolvedTypeId < 0)
        {
            resolvedTypeId = ArchetypeRegistry.GetComponentTypeId(componentType);
            if (resolvedTypeId < 0)
            {
                resolvedTypeId = ArchetypeRegistry.GetComponentTypeId(ct.Definition.POCOType);
            }
        }

        info = TakeRecycledInfo(resolvedTypeId, ct) ?? new ComponentInfo();
        info.Bind(resolvedTypeId, ct, _changeSet);

        _componentInfos.Add(componentType, info);
        if (info.ComponentTypeId >= 0)
        {
            EnsureInfoSlot(info.ComponentTypeId);
            _componentInfosByTypeId[info.ComponentTypeId] = info;
        }

        return info;
    }

    /// <summary>Grows the by-type-id arrays so <paramref name="componentTypeId"/> has a slot in both.</summary>
    /// <param name="componentTypeId">A non-negative type id.</param>
    /// <remarks>Growth only, on the slow path, bounded by the number of component types the process registers. The accessor is thread-affine.</remarks>
    private void EnsureInfoSlot(int componentTypeId)
    {
        if (componentTypeId < _componentInfosByTypeId.Length)
        {
            return;
        }

        var grown = Math.Max(componentTypeId + 1, _componentInfosByTypeId.Length * 2);
        Array.Resize(ref _componentInfosByTypeId, grown);
        Array.Resize(ref _recycledInfosByTypeId, grown);
    }

    /// <summary>The entry this accessor held for <paramref name="componentTypeId"/> on its previous lease, if it served the same table.</summary>
    /// <param name="componentTypeId">The component's type id.</param>
    /// <param name="table">The table this lease resolved.</param>
    /// <returns>The entry, removed from the recycled set, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The table is compared by reference, which is what makes a pooled accessor that moves to another engine — or an engine whose table was rebuilt — safe:
    /// its old entry is discarded rather than rebound onto a table it never described.
    /// </remarks>
    private ComponentInfo TakeRecycledInfo(int componentTypeId, ComponentTable table)
    {
        if ((uint)componentTypeId >= (uint)_recycledInfosByTypeId.Length)
        {
            return null;
        }

        var recycled = _recycledInfosByTypeId[componentTypeId];
        _recycledInfosByTypeId[componentTypeId] = null;
        return recycled != null && ReferenceEquals(recycled.ComponentTable, table) ? recycled : null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Epoch management
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Flush all pending dirty state and advance the epoch within this accessor.
    /// Must only be called at a quiescent point — no B+Tree OLC write locks held,
    /// no ChunkAccessor mid-operation.
    /// </summary>
    private protected void FlushAndRefreshEpoch()
    {
        CommitAccessorChanges();
        _changeSet?.ReleaseDirtyMarks();
        var newEpoch = _epochManager.RefreshScope();
        ChunkBasedSegment<PersistentStore>.RefreshWarmCacheEpoch(newEpoch);
    }

    /// <summary>Commits the dirty state of every cached ChunkAccessor.</summary>
    private void CommitAccessorChanges()
    {
        foreach (var ci in _componentInfos.Values)
        {
            if (ci.ComponentTable.StorageMode == StorageMode.Transient)
            {
                ci.TransientCompContentAccessor.CommitChanges();
            }
            else
            {
                ci.CompContentAccessor.CommitChanges();
                if (ci.ComponentTable.StorageMode == StorageMode.Versioned)
                {
                    ci.CompRevTableAccessor.CommitChanges();
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void CheckEpochRefresh()
    {
        if (EpochRefreshDue(++_entityOperationCount) && LiveIndexCursors == 0)
        {
            FlushAndRefreshEpoch();
            _entityOperationCount = 0;
        }
    }

    /// <summary>
    /// A mutable accessor over a <see cref="ComponentCollection{T}"/> field, bound to this accessor's <see cref="ChangeSet"/> so edits are tracked for
    /// commit/rollback. Public callers reach it through <see cref="EntityRefMut.CreateComponentCollectionAccessor{T, TElem}"/>, which orders the
    /// copy-on-write before it (#1199).
    /// </summary>
    internal ComponentCollectionAccessor<T> CreateComponentCollectionAccessorCore<T>(ref ComponentCollection<T> field) where T : unmanaged
    {
        AssertThreadAffinity();
        return new ComponentCollectionAccessor<T>(_changeSet, _dbe.GetComponentCollectionVSBS<T>(), ref field);
    }

    /// <summary>The same, writing every buffer id it moves to back into the entity's stored component (#1199, #1362).</summary>
    internal unsafe ComponentCollectionAccessor<T> CreateComponentCollectionAccessorCore<T>(
        ref ComponentCollection<T> field,
        EntityId entity,
        int componentTypeId,
        int fieldOffset,
        delegate*<EntityAccessor, EntityId, int, int, int, void> writeBack) where T : unmanaged
    {
        AssertThreadAffinity();
        return new ComponentCollectionAccessor<T>(_changeSet, _dbe.GetComponentCollectionVSBS<T>(), ref field, this, entity, componentTypeId, fieldOffset,
            writeBack);
    }

    /// <summary>
    /// Count an entity open toward the epoch refresh, and refresh every <see cref="EpochRefreshInterval"/> of them, read-only transactions included (#1189).
    /// Call only at the start of a <see cref="Transaction"/>'s open, from a thread inside its epoch scope.
    /// </summary>
    /// <remarks>
    /// Safe since #1199: nothing an open hands out outlives the call that produced it — reads copy, writes are <c>Set</c>, and a handle's cluster base is
    /// re-resolved once the accessor that resolved it lets go of the page — so a refresh can no longer leave a live reference over an evictable page.
    /// Without it, a transaction that opens N entities kept every page they touched, and every page any other thread touched since it began, unevictable
    /// until it ended.
    /// <para>Not PTA workers: a worker's pin lasts one lease (<see cref="PointInTimeAccessor.WorkerLease"/>) — one chunk in the runtime — and ends with it,
    /// on purpose. A refresh increments the global epoch, and every other thread's warm B+Tree accessor, keyed on it, takes its cold path at its next
    /// operation; workers refreshing every 128 opens each would do that to each other continuously. A worker's pinned set is bounded by its lease.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void NoteEntityOpen()
    {
        if (EpochRefreshDue(++_entityOperationCount) && LiveIndexCursors == 0)
        {
            _entityOperationCount = 0;
            EnumerateRefreshEpoch();
        }
    }

    /// <summary>
    /// B+Tree range cursors live on this thread (<c>BTree.RangeEnumerator</c>). While one is, no per-operation refresh moves the thread's epoch: the cursor
    /// keeps its current node across calls, and retired nodes are reclaimed once the minimum active epoch has passed them (<c>BTree.DeferredReclaim</c>)
    /// — a refresh under a live cursor could let its node be reused while it still reads it. An open made inside such a scan (an FK reverse lookup's
    /// per-entity action) therefore waits for the scan to end; the count stays at the threshold, so the refresh happens at the first operation after.
    /// </summary>
    /// <remarks>A cursor disposed twice (a <c>foreach</c> over a <c>using</c> copy) would decrement twice, so the decrement clamps at zero.</remarks>
    [ThreadStatic]
    internal static int LiveIndexCursors;

    /// <summary>
    /// How often a long broad walk moves its transaction's epoch (<see cref="BeginScanYield"/>): the commit-card wait's interval. Tests change it.
    /// </summary>
    internal static TimeSpan ScanEpochRefreshInterval = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// The cluster count past which an occupancy count arms its epoch yield and its refreshable back-pressure wait (#1364, #1230): about 65 k entities,
    /// tens of microseconds of popcounts. Below it the walk is too short for either to matter, and arming them cost a measured 7 %. Tests lower it.
    /// </summary>
    internal static int LongWalkClusters = 1024;

    /// <summary>Invoked at every yield point of a broad walk, after the refresh decision (tests).</summary>
    internal static Action ScanYieldProbe;

    private long _scanRefreshedAt;
    private Action _scanYield;

    /// <summary>
    /// A long broad walk's yield point (#1364): the entity-map walks call it between buckets, the cluster scans between clusters. There the walk holds only
    /// the slot-reference pins of its own scan accessor (EP-02), so it is a call boundary (EP-03) and the epoch may move. Held for the whole walk, the epoch
    /// kept every page the writers tagged meanwhile protected: a count over 12M entities stalled every writer with the whole cache epoch-protected and
    /// nothing dirty. Refreshed at most every <see cref="ScanEpochRefreshInterval"/> of the walk, never under a live index cursor.
    /// </summary>
    /// <remarks>Called once, at the walk's start: the interval runs from there. Run from the transaction's previous refresh instead, a count of a few
    /// microseconds paid a full epoch refresh at its first yield whenever a millisecond had passed since the last one — on most calls.</remarks>
    internal Action BeginScanYield()
    {
        _scanRefreshedAt = Stopwatch.GetTimestamp();
        return _scanYield ??= YieldDuringScan;
    }

    private void YieldDuringScan()
    {
        if (LiveIndexCursors == 0)
        {
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_scanRefreshedAt, now) >= ScanEpochRefreshInterval)
            {
                FlushAndRefreshEpoch();
                _scanRefreshedAt = Stopwatch.GetTimestamp();
            }
        }

        ScanYieldProbe?.Invoke();
    }

    /// <summary>
    /// Flushes a point-in-time worker at the end of its system (<see cref="PointInTimeAccessor.FlushWorker"/>): commits every dirty ChunkAccessor state and
    /// lets go of every page its windows hold — the component accessors', the entity map's and the cluster cache — then releases the change set's dirty
    /// marks. A parked worker holds no page (EP-04). It moves no epoch: the worker holds none outside a lease, and this runs on whichever thread completed
    /// the system — a refresh there moved that thread's pin, never the worker's (#1532).
    /// </summary>
    /// <remarks>
    /// The cluster cache goes because a worker's handles do not outlive the system it ran, and the cache lives as long as the worker accessor — across
    /// systems and ticks. Kept, its slots stay pinned, and a slot a writable open mapped dirty holds its page's <c>ActiveChunkWriters</c> above zero, which
    /// keeps the checkpoint from capturing that page (#1199).
    /// </remarks>
    internal void FlushAtSystemEnd()
    {
        DisposeClusterCache();
        foreach (var ci in _componentInfos.Values)
        {
            ci.ReleaseWindows(_changeSet);
        }

        if (_hasEntityMapCache)
        {
            _entityMapCacheAccessor.Dispose();
            _hasEntityMapCache = false;
        }

        _changeSet?.ReleaseDirtyMarks();
        _entityOperationCount = 0;
    }

    /// <summary>
    /// Epoch refresh for bulk enumerators. Subclasses may override to add shortcuts (e.g. read-only skip).
    /// </summary>
    internal virtual void EnumerateRefreshEpoch() => FlushAndRefreshEpoch();

    // ═══════════════════════════════════════════════════════════════════════
    // Accessor lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reset this accessor for a new MVCC snapshot without reallocating.
    /// Flushes dirty ChunkAccessor state and releases this accessor's DirtyCounter marks, then updates TSN.
    /// ComponentInfo cache and ChunkAccessors are preserved — page caches stay warm.
    /// Called by <see cref="PointInTimeAccessor"/> at the start of each tick to reuse
    /// per-thread accessors across ticks (zero allocation after warmup).
    /// </summary>
    internal void ResetForNewSnapshot(long newTsn)
    {
        // Flush pending dirty state from previous tick.
        // Iterate the flat array (no Dictionary enumerator allocation) — only non-null slots.
        for (var i = 0; i < _componentInfosByTypeId.Length; i++)
        {
            var ci = _componentInfosByTypeId[i];
            if (ci == null)
            {
                continue;
            }

            if (ci.ComponentTable.StorageMode == StorageMode.Transient)
            {
                ci.TransientCompContentAccessor.CommitChanges();
            }
            else
            {
                ci.CompContentAccessor.CommitChanges();
                if (ci.ComponentTable.StorageMode == StorageMode.Versioned)
                {
                    ci.CompRevTableAccessor.CommitChanges();
                }
            }
        }

        _changeSet?.ReleaseDirtyMarks();

        // Update snapshot — ComponentInfo cache stays warm (ChunkAccessor page caches preserved)
        TSN = newTsn;
        _entityOperationCount = 0;
    }

    /// <summary>Dispose all ChunkAccessors to flush dirty pages.</summary>
    private protected void FlushAccessors()
    {
        foreach (var info in _componentInfos.Values)
        {
            info.DisposeAccessors();
        }
    }

    /// <summary>Reset base fields for reuse. Subclasses call this AFTER their own cleanup.</summary>
    private protected virtual void ResetCore()
    {
        _dbe = null;
        _epochManager = null;
        _owningThreadId = 0;
        _isPointInTime = false;
        _leaseDepth = 0;
        if (_hasEntityMapCache)
        {
            _entityMapCacheAccessor.Dispose();
            _hasEntityMapCache = false;
        }
        DisposeClusterCache();
        if (_componentInfos.Capacity <= ComponentInfosMaxCapacity)
        {
            _componentInfos.Clear();
        }
        else
        {
            _componentInfos = new Dictionary<Type, ComponentInfo>(ComponentInfosMaxCapacity);
        }

        // Kept for the next lease rather than dropped: the slow path rebinds them. Only entries the array indexes are recycled — every entry with a type id,
        // since the array grows to cover them; one without (a type the registry never saw) is simply dropped, as every entry used to be.
        for (var i = 0; i < _componentInfosByTypeId.Length; i++)
        {
            var info = _componentInfosByTypeId[i];
            if (info != null)
            {
                _recycledInfosByTypeId[i] = info;
                _componentInfosByTypeId[i] = null;
            }
        }

        FreeCommitStaging();
        _discipline = CommitDiscipline.TickFence;
        _didInPlaceSvWrite = false;

        TSN = 0;
        _changeSet = null;
    }

    /// <summary>
    /// Releases the accessor's cached chunk accessors, drops excess dirty-page marks, and resets its state for reuse. A bare accessor holds no persistent epoch
    /// scope, so none is exited here; <see cref="Transaction"/> overrides this to add a thread-affinity check and exit its own epoch scope.
    /// </summary>
    public virtual void Dispose()
    {
        if (_isDisposed)
        {
            if (_hasEntityMapCache)
            {
                _entityMapCacheAccessor.Dispose();
                _hasEntityMapCache = false;
            }
            DisposeClusterCache();
            return;
        }

        // No thread affinity assert — PointInTimeAccessor disposes per-thread
        // accessors from the cleanup thread (different from the creating thread).
        // Transaction.Dispose overrides and adds its own affinity check + epoch exit.
        FlushAccessors();
        _changeSet?.ReleaseDirtyMarks();
        _isDisposed = true;
        // No epoch exit here: a point-in-time worker holds a scope only inside a lease, which exits it on its own thread (EP-04).
        // Transaction.Dispose overrides and exits its own epoch scope.
        ResetCore();
    }
}
