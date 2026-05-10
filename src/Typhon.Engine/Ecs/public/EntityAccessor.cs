// EntityAccessor — lightweight base for Transaction and PointInTimeAccessor.
// Contains the minimum state needed for MVCC-correct entity reads and SV/Transient writes.

using System;
using System.Collections.Generic;
using System.Diagnostics;
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

#if DEBUG
    private protected int _debugOwningThreadId;
#endif

    private protected Dictionary<Type, ComponentInfo> _componentInfos;

    /// <summary>Array-indexed ComponentInfo cache — O(1) lookup by componentTypeId. Avoids Dictionary hash + equality overhead on hot path.</summary>
    private protected ComponentInfo[] _componentInfosByTypeId;

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

    public long TSN { get; private protected set; }

    /// <summary>
    /// Prepare this accessor for mutation. Called once by <see cref="ArchetypeAccessor{TArch}"/>
    /// on first <c>OpenMut</c> to ensure the underlying accessor is in the correct state for writes.
    /// Base implementation is a no-op. Transaction overrides to call EnsureMutable + set InProgress state.
    /// </summary>
    internal virtual void PrepareForMutation() { }

    public EntityAccessor()
    {
        _componentInfos = new Dictionary<Type, ComponentInfo>(ComponentInfosMaxCapacity);
        _componentInfosByTypeId = new ComponentInfo[ComponentInfosMaxCapacity];
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
        _isDisposed = false;
#if DEBUG
        _debugOwningThreadId = Environment.CurrentManagedThreadId;
#endif
        _entityOperationCount = 0;
        _changeSet = _dbe.MMF.CreateChangeSet();
        TSN = tsn;
    }

    [Conditional("DEBUG")]
    private protected void AssertThreadAffinity()
    {
#if DEBUG
        Debug.Assert(
            _debugOwningThreadId == Environment.CurrentManagedThreadId,
            "EntityAccessor thread affinity violation: current thread differs from the creating thread. " +
            "Each EntityAccessor instance must be used only from its creating thread.");
#endif
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
            if (typeId >= 0 && typeId < _componentInfosByTypeId.Length)
            {
                _componentInfosByTypeId[typeId] = info;
            }

            return info;
        }

        var ct = _dbe.GetComponentTable(componentType) ?? _dbe.FindComponentTableBySchemaName(componentType);
        if (ct == null)
        {
            throw new InvalidOperationException($"The type {componentType} doesn't have a registered Component Table");
        }

        var isMultiple = ct.Definition.AllowMultiple;
        info = new ComponentInfo(isMultiple)
        {
            ComponentTypeId = typeId >= 0 ? typeId : ArchetypeRegistry.GetComponentTypeId(componentType),
            ComponentTable = ct,
            ComponentOverhead = ct.ComponentOverhead,
            SingleCache    = isMultiple ? null : new Dictionary<long, ComponentInfo.CompRevInfo>(),
            MultipleCache  = isMultiple ? new Dictionary<long, List<ComponentInfo.CompRevInfo>>() : null,
        };

        switch (ct.StorageMode)
        {
            case StorageMode.Transient:
                info.TransientCompContentAccessor = ct.TransientComponentSegment.CreateChunkAccessor();
                break;
            case StorageMode.SingleVersion:
                info.CompContentSegment  = ct.ComponentSegment;
                info.CompContentAccessor = ct.ComponentSegment.CreateChunkAccessor(_changeSet);
                break;
            default: // Versioned
                info.CompContentSegment   = ct.ComponentSegment;
                info.CompRevTableSegment  = ct.CompRevTableSegment;
                info.CompContentAccessor  = ct.ComponentSegment.CreateChunkAccessor(_changeSet);
                info.CompRevTableAccessor = ct.CompRevTableSegment.CreateChunkAccessor(_changeSet);
                break;
        }

        _componentInfos.Add(componentType, info);
        if (info.ComponentTypeId >= 0 && info.ComponentTypeId < _componentInfosByTypeId.Length)
        {
            _componentInfosByTypeId[info.ComponentTypeId] = info;
        }

        return info;
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

        _changeSet?.ReleaseExcessDirtyMarks();
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

        _changeSet?.ReleaseExcessDirtyMarks();

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
#if DEBUG
        _debugOwningThreadId = 0;
#endif
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

        Array.Clear(_componentInfosByTypeId);

        TSN = 0;
        _changeSet = null;
    }

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
        _changeSet?.ReleaseExcessDirtyMarks();
        _isDisposed = true;
        // No epoch exit here — InitLightweight does not enter a persistent epoch scope.
        // Transaction.Dispose overrides and exits its own epoch scope.
        ResetCore();
    }
}
