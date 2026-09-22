// EntityAccessor — lightweight base for Transaction and PointInTimeAccessor.
// Contains the minimum state needed for MVCC-correct entity reads and SV/Transient writes.

using System;
using System.Collections.Generic;
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
    private protected const int EpochRefreshInterval = 128;

    private protected bool _isDisposed;
    private protected DatabaseEngine _dbe;
    internal DatabaseEngine DBE => _dbe;
    private protected EpochManager _epochManager;

    // Thread that created this accessor. Promoted out of #if DEBUG (#422) so the strict-mode thread-affinity check compiles
    // always-on. The check itself is gated by CheckConfig.Enabled, so this 4-byte field is the only unconditional cost.
    private protected int _owningThreadId;

    /// <summary>
    /// True when this accessor entered an epoch scope in <see cref="InitLightweight"/> that it never exits — the PointInTimeAccessor worker path. Lets
    /// <c>ResolveEntity</c> skip <c>EpochManager.IsCurrentThreadInScope</c> (two <c>[ThreadStatic]</c> reads plus a padded-slot load) on every resolve.
    /// False on every other path, where the original check still runs.
    /// </summary>
    private protected bool _ownsPersistentEpochScope;

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
    private protected ChunkAccessor<PersistentStore> _entityMapCacheAccessor;
    private protected bool _hasEntityMapCache;

    /// <summary>Cached cluster accessor for same-archetype repeated lookups (cluster-eligible archetypes only).</summary>
    private protected ushort _clusterCacheArchId;
    private protected ChunkAccessor<PersistentStore> _clusterCacheAccessor;
    private protected bool _hasClusterCache;
    private protected ChunkAccessor<TransientStore> _transientClusterCacheAccessor;
    private protected bool _hasTransientClusterCache;

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
    /// Prepare this accessor for mutation. Called once by <see cref="ArchetypeAccessor{TArch}"/>
    /// on first <c>OpenMut</c> to ensure the underlying accessor is in the correct state for writes.
    /// Base implementation is a no-op. Transaction overrides to call EnsureMutable + set InProgress state.
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
    /// Initialize this accessor for lightweight point-in-time access.
    /// Creates a per-thread ChangeSet for dirty page tracking.
    /// Does NOT enter a persistent epoch scope — entity resolution uses per-call EpochGuard, and ChunkAccessors protect their own pages via ref counting.
    /// Does NOT insert into TransactionChain.
    /// </summary>
    internal void InitLightweight(DatabaseEngine dbe, long tsn)
    {
        _dbe = dbe;
        _epochManager = _dbe.EpochManager;
        // Enter epoch scope on calling thread — required for ChunkAccessor creation.
        // Epoch exit is intentionally omitted from Dispose() because PointInTimeAccessor disposes per-thread accessors from a different (cleanup) thread.
        // The epoch scope is cleaned up when the EpochManager is disposed with the DatabaseEngine.
        // Runtime integration (#211) will add proper per-worker epoch cleanup hooks.
        _ = _epochManager.EnterScope();
        // The scope entered above is never exited for this accessor's lifetime (see the note on Dispose omission), and the accessor is thread-affine
        // (_owningThreadId below). So "is this thread in an epoch scope?" is provably true from here on, and ResolveEntity can skip asking — that question
        // costs two [ThreadStatic] reads plus a padded-slot load on EVERY entity resolve.
        _ownsPersistentEpochScope = true;
        _isDisposed = false;
        _owningThreadId = Environment.CurrentManagedThreadId;
        _entityOperationCount = 0;
        _changeSet = _dbe.MMF.CreateChangeSet();
        _ownsChangeSet = true;
        TSN = tsn;
    }

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

        _changeSet?.ReleaseDirtyMarks();
        var newEpoch = _epochManager.RefreshScope();
        ChunkBasedSegment<PersistentStore>.RefreshWarmCacheEpoch(newEpoch);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void CheckEpochRefresh()
    {
        if (++_entityOperationCount >= EpochRefreshInterval)
        {
            FlushAndRefreshEpoch();
            _entityOperationCount = 0;
        }
    }

    /// <summary>
    /// Unconditionally refresh the epoch scope for this accessor. Flushes all dirty ChunkAccessor state and advances the pinned epoch, allowing pages from
    /// older epochs to be evicted.
    /// Called by <see cref="PointInTimeAccessor.FlushWorker"/> at the end of each parallel chunk.
    /// </summary>
    internal void RefreshEpochScope()
    {
        FlushAndRefreshEpoch();
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
    /// Flushes dirty ChunkAccessor state and caps DirtyCounter, then updates TSN.
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
        _ownsPersistentEpochScope = false;
        if (_hasEntityMapCache)
        {
            _entityMapCacheAccessor.Dispose();
            _hasEntityMapCache = false;
        }
        if (_hasClusterCache)
        {
            _clusterCacheAccessor.Dispose();
            _hasClusterCache = false;
        }
        if (_hasTransientClusterCache)
        {
            _transientClusterCacheAccessor.Dispose();
            _hasTransientClusterCache = false;
        }
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
            if (_hasClusterCache)
            {
                _clusterCacheAccessor.Dispose();
                _hasClusterCache = false;
            }
            if (_hasTransientClusterCache)
            {
                _transientClusterCacheAccessor.Dispose();
                _hasTransientClusterCache = false;
            }
            return;
        }

        // No thread affinity assert — PointInTimeAccessor disposes per-thread
        // accessors from the cleanup thread (different from the creating thread).
        // Transaction.Dispose overrides and adds its own affinity check + epoch exit.
        FlushAccessors();
        _changeSet?.ReleaseDirtyMarks();
        _isDisposed = true;
        // No epoch exit here — InitLightweight does not enter a persistent epoch scope.
        // Transaction.Dispose overrides and exits its own epoch scope.
        ResetCore();
    }
}
