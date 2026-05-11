using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Non-generic base class for <see cref="View{T}"/>, <see cref="View{T1,T2}"/>, and <see cref="OrView{T}"/>.
/// Contains entity set management, delta tracking, disposal, and globally unique ViewId generation.
/// </summary>
public abstract class ViewBase : IView, IDisposable, IEnumerable<long>
{
    private static int _nextViewId;

    internal readonly HashMap<long> _entityIds = new();
    private readonly Dictionary<long, DeltaKind> _deltas = new(16);
    private int _addedCount;
    private int _removedCount;
    private int _modifiedCount;
    protected readonly FieldEvaluator[] _evaluators;
    private long _lastRefreshTSN;
    private int _disposed;
    private bool _overflowDetected;
    private readonly ExecutionPlan[] _cachedPlans;

    private protected ViewBase(FieldEvaluator[] evaluators, int[] fieldDependencies, IMemoryAllocator allocator, IResource resourceParent, int bufferCapacity,
        long baseTSN)
    {
        _evaluators = evaluators;
        FieldDependencies = fieldDependencies;
        ViewId = Interlocked.Increment(ref _nextViewId);
        DeltaBuffer = new ViewDeltaRingBuffer(allocator, resourceParent, bufferCapacity, baseTSN);
    }

    private protected ViewBase(FieldEvaluator[] evaluators, int[] fieldDependencies, IMemoryAllocator allocator, IResource resourceParent, ExecutionPlan[] plans,
        int bufferCapacity, long baseTSN) : this(evaluators, fieldDependencies, allocator, resourceParent, bufferCapacity, baseTSN)
    {
        _cachedPlans = plans;
    }

    public int ViewId { get; }
    public int[] FieldDependencies { get; }
    public bool IsDisposed => _disposed != 0;
    internal ViewDeltaRingBuffer DeltaBuffer { get; }

    /// <summary>
    /// Simulation-tier filter for materialization (issue #231). When set to anything other than <see cref="SimTier.All"/>, any system that materializes this
    /// view's entity set scopes the iteration to clusters in cells whose tier matches. Most useful for published views (subscription scenarios) where the view
    /// itself encodes tier scoping; for system dispatch the system-level <c>tier:</c> parameter is more direct.
    /// </summary>
    /// <remarks>
    /// <para>The effective tier filter applied at materialization time is <c>system.TierFilter &amp; view.TierFilter</c> (bit-AND of the system filter and view
    /// filter). When either is <see cref="SimTier.All"/>, that side is a no-op and the other wins; when both are set, only the intersection survives.</para>
    /// </remarks>
    internal SimTier TierFilter { get; private set; } = SimTier.All;

    /// <summary>
    /// Set this view's tier filter to scope subsequent materializations to a subset of <see cref="SimTier"/> values.
    /// Returns <c>this</c> (as <typeparamref name="T"/>) to allow chaining: <c>tx.Query&lt;Ant&gt;().ToView().WithTier(SimTier.Tier0)</c>. Issue #231.
    /// </summary>
    public T WithTier<T>(SimTier tier) where T : ViewBase
    {
        TierFilter = tier;
        return (T)this;
    }

    /// <summary>
    /// Set this view's tier filter to scope subsequent materializations to a subset of <see cref="SimTier"/> values.
    /// Non-generic overload. Returns <c>this</c> for chaining when the concrete type is not needed. Issue #231.
    /// </summary>
    public ViewBase WithTier(SimTier tier)
    {
        TierFilter = tier;
        return this;
    }

    /// <summary>True if this View has been published for client subscriptions via <c>PublishView()</c>.</summary>
    public bool IsPublished { get; internal set; }

    /// <summary>True if this View is used as a system input in the DAG scheduler.</summary>
    public bool IsSystemInput { get; internal set; }
    public int Count => _entityIds.Count;
    public long LastRefreshTSN => _lastRefreshTSN;
    public bool HasOverflow => _overflowDetected;
    public ExecutionPlan ExecutionPlan => _cachedPlans is { Length: > 0 } ? _cachedPlans[0] : default;
    public bool HasCachedPlan => _cachedPlans != null;

    public bool Contains(long pk) => _entityIds.Contains(pk);

    internal void AddEntityDirect(long pk) => _entityIds.TryAdd(pk);

    /// <summary>Direct access to the entity set for callers that need to populate it (e.g., PipelineExecutor during ToView).</summary>
    internal HashMap<long> EntityIdsInternal => _entityIds;

    public ViewDelta GetDelta() => new(_deltas, _addedCount, _removedCount, _modifiedCount);

    public void ClearDelta()
    {
        _deltas.Clear();
        _addedCount = 0;
        _removedCount = 0;
        _modifiedCount = 0;
    }

    internal HashMap<long>.Enumerator GetEnumerator() => _entityIds.GetEnumerator();

    IEnumerator<long> IEnumerable<long>.GetEnumerator() => ((IEnumerable<long>)_entityIds).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<long>)_entityIds).GetEnumerator();

    protected void SetLastRefreshTSN(long tsn) => _lastRefreshTSN = tsn;

    protected void SetOverflowDetected(bool value) => _overflowDetected = value;

    protected bool TryMarkDisposed() => Interlocked.Exchange(ref _disposed, 1) == 0;

    protected ExecutionPlan CachedPlan => _cachedPlans is { Length: > 0 } ? _cachedPlans[0] : default;

    protected ExecutionPlan[] CachedPlans => _cachedPlans;

    protected bool HasCachedPlanInternal => _cachedPlans != null;

    /// <summary>Drain the ring buffer, evaluate predicates, and update entity set and delta tracking.</summary>
    public abstract void Refresh(Transaction tx);

    /// <summary>Deregister from all owning ViewRegistries. Called during disposal.</summary>
    protected abstract void DeregisterFromRegistries();

    public void Dispose()
    {
        if (!TryMarkDisposed())
        {
            return;
        }

        DeregisterFromRegistries();
        // Safety fence: allow in-flight producers to complete TryAppend before freeing buffer memory
        Thread.SpinWait(100);
        DeltaBuffer.Dispose();
        _entityIds.Dispose();
        _deltas.Clear();
        _addedCount = 0;
        _removedCount = 0;
        _modifiedCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void ApplyDelta(long pk, bool wasInView, bool shouldBeInView)
    {
        if (!wasInView && shouldBeInView)
        {
            _entityIds.TryAdd(pk);
            CompactDelta(pk, DeltaKind.Added);
        }
        else if (wasInView && !shouldBeInView)
        {
            _entityIds.TryRemove(pk);
            CompactDelta(pk, DeltaKind.Removed);
        }
        else if (wasInView)
        {
            CompactDelta(pk, DeltaKind.Modified);
        }
    }

    private protected void CompactDelta(long pk, DeltaKind newKind)
    {
        if (!_deltas.TryGetValue(pk, out var existing))
        {
            // No existing delta — insert directly
            _deltas[pk] = newKind;
            switch (newKind)
            {
                case DeltaKind.Added: _addedCount++; break;
                case DeltaKind.Removed: _removedCount++; break;
                case DeltaKind.Modified: _modifiedCount++; break;
            }
            return;
        }

        switch (existing)
        {
            case DeltaKind.Added:
                if (newKind == DeltaKind.Removed)
                {
                    _deltas.Remove(pk);
                    _addedCount--; // Added + Removed → cancel
                }
                // Added + Modified → Added, Added + Added → Added (no change)
                return;

            case DeltaKind.Modified:
                if (newKind == DeltaKind.Removed)
                {
                    _deltas[pk] = DeltaKind.Removed;
                    _modifiedCount--;
                    _removedCount++; // Modified + Removed → Removed
                }
                // Modified + Modified → Modified (no change)
                return;

            case DeltaKind.Removed:
                if (newKind == DeltaKind.Added)
                {
                    _deltas[pk] = DeltaKind.Modified;
                    _removedCount--;
                    _modifiedCount++; // Removed + Added → Modified
                }
                return;
        }
    }

    /// <summary>
    /// Drains ring buffer entries that arrived during a RefreshFull re-scan, advancing the consumer position without processing entries
    /// (the full scan already captured the authoritative entity set).
    /// </summary>
    protected void DrainBufferAfterRefreshFull(long targetTSN)
    {
        while (DeltaBuffer.TryPeek(targetTSN, out _, out _, out var tsn, out _))
        {
            DeltaBuffer.Advance();
            SetLastRefreshTSN(tsn);
        }
    }

    /// <summary>
    /// Computes Added/Removed deltas by diffing old and new entity sets after a full refresh.
    /// Entities present in both sets are NOT reported as Modified — after overflow, granular field-change tracking is lost. Consumers needing field-change
    /// tracking after overflow should treat the overflow event itself as a full invalidation signal via <see cref="HasOverflow"/>.
    /// </summary>
    internal void ComputeRefreshFullDeltas(HashMap<long> oldEntities)
    {
        foreach (var pk in _entityIds)
        {
            if (!oldEntities.Contains(pk))
            {
                CompactDelta(pk, DeltaKind.Added);
            }
        }
        foreach (var pk in oldEntities)
        {
            if (!_entityIds.Contains(pk))
            {
                CompactDelta(pk, DeltaKind.Removed);
            }
        }
        oldEntities.Dispose();
    }
}
