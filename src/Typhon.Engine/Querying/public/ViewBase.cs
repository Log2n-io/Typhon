using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Non-generic base class for <see cref="EcsView{TArchetype}"/> and <see cref="NavigationView{TSource,TTarget}"/>.
/// Contains entity set management, delta tracking, disposal, and process-unique ViewId generation.
/// </summary>
public abstract class ViewBase : IView, IDisposable, IEnumerable<long>
{
    private static int NextViewId;

    internal readonly HashMap<long> _entityIds = new();
    private readonly Dictionary<long, DeltaKind> _deltas = new(16);
    private int _addedCount;
    private int _removedCount;
    private int _modifiedCount;
    /// <summary>Field predicate evaluators applied during refresh. Empty for pull-mode views with no WHERE clause.</summary>
    protected readonly FieldEvaluator[] Evaluators;
    private long _lastRefreshTSN;
    private int _disposed;
    private bool _overflowDetected;
    private readonly ExecutionPlan[] _cachedPlans;

    private protected ViewBase(FieldEvaluator[] evaluators, int[] fieldDependencies, IMemoryAllocator allocator, IResource resourceParent, int bufferCapacity,
        long baseTSN, string sourceFile = null, int sourceLine = 0, string sourceMethod = null)
    {
        Evaluators = evaluators;
        FieldDependencies = fieldDependencies;
        ViewId = Interlocked.Increment(ref NextViewId);
        DeltaBuffer = new ViewDeltaRingBuffer(allocator, resourceParent, bufferCapacity, baseTSN);
        SourceFile = sourceFile;
        SourceLine = sourceLine;
        SourceMethod = sourceMethod;
    }

    private protected ViewBase(FieldEvaluator[] evaluators, int[] fieldDependencies, IMemoryAllocator allocator, IResource resourceParent, ExecutionPlan[] plans,
        int bufferCapacity, long baseTSN, string sourceFile = null, int sourceLine = 0, string sourceMethod = null)
        : this(evaluators, fieldDependencies, allocator, resourceParent, bufferCapacity, baseTSN, sourceFile, sourceLine, sourceMethod)
    {
        _cachedPlans = plans;
    }

    /// <inheritdoc/>
    public int ViewId { get; }

    /// <inheritdoc/>
    public int[] FieldDependencies { get; }

    /// <inheritdoc/>
    public bool IsDisposed => _disposed != 0;
    internal ViewDeltaRingBuffer DeltaBuffer { get; }

    /// <summary>User source file where this View was constructed. Captured via <c>[CallerFilePath]</c> on the public Query API (e.g., <c>ToView()</c>).
    /// Null if construction was internal/unattributed. Path form depends on the consuming project's <c>PathMap</c> / <c>DeterministicSourcePaths</c>
    /// MSBuild configuration — Typhon's own projects produce <c>/_/…</c> repo-relative paths.</summary>
    public string SourceFile { get; }

    /// <summary>User source line where this View was constructed. Captured via <c>[CallerLineNumber]</c>. Zero if unattributed.</summary>
    public int SourceLine { get; }

    /// <summary>User source method name where this View was constructed. Captured via <c>[CallerMemberName]</c>. Null if unattributed.</summary>
    public string SourceMethod { get; }

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
    /// <summary>Number of entities currently in the view.</summary>
    public int Count => _entityIds.Count;

    /// <summary>Transaction sequence number (TSN) of the most recent refresh.</summary>
    public long LastRefreshTSN => _lastRefreshTSN;

    /// <summary>
    /// True when the delta ring buffer overflowed since the last refresh, forcing a full re-scan.
    /// After overflow, per-field <c>Modified</c> tracking is lost — only Added/Removed are reported.
    /// </summary>
    public bool HasOverflow => _overflowDetected;

    /// <summary>Cached execution plan for the primary query branch, or <c>default</c> when the view has no cached plan.</summary>
    public ExecutionPlan ExecutionPlan => _cachedPlans is { Length: > 0 } ? _cachedPlans[0] : default;

    /// <summary>True when this view was built with a cached execution plan.</summary>
    public bool HasCachedPlan => _cachedPlans != null;

    /// <summary>Returns <c>true</c> when the entity with the given primary key is currently in the view.</summary>
    /// <param name="pk">Entity primary key.</param>
    public bool Contains(long pk) => _entityIds.Contains(pk);

    internal void AddEntityDirect(long pk) => _entityIds.TryAdd(pk);

    /// <summary>Direct access to the entity set for callers that need to populate it (e.g., PipelineExecutor during ToView).</summary>
    internal HashMap<long> EntityIdsInternal => _entityIds;

    /// <summary>
    /// Returns a zero-allocation <see cref="ViewDelta"/> over the entities added, removed, and modified since the last
    /// <see cref="ClearDelta"/>. The returned struct is only valid until the next <see cref="ClearDelta"/> call.
    /// </summary>
    public ViewDelta GetDelta() => new(_deltas, _addedCount, _removedCount, _modifiedCount);

    /// <summary>Clears accumulated delta tracking, invalidating any <see cref="ViewDelta"/> previously returned by <see cref="GetDelta"/>.</summary>
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

    /// <summary>Records the TSN of the current refresh, surfaced by <see cref="LastRefreshTSN"/>.</summary>
    /// <param name="tsn">Transaction sequence number of the refresh.</param>
    protected void SetLastRefreshTSN(long tsn) => _lastRefreshTSN = tsn;

    /// <summary>Sets the overflow flag surfaced by <see cref="HasOverflow"/>.</summary>
    /// <param name="value">New overflow state.</param>
    protected void SetOverflowDetected(bool value) => _overflowDetected = value;

    /// <summary>Atomically transitions the view to the disposed state. Returns <c>true</c> on the first call, <c>false</c> if already disposed.</summary>
    protected bool TryMarkDisposed() => Interlocked.Exchange(ref _disposed, 1) == 0;

    /// <summary>First cached execution plan, or <c>default</c> when the view has no cached plan.</summary>
    protected ExecutionPlan CachedPlan => _cachedPlans is { Length: > 0 } ? _cachedPlans[0] : default;

    /// <summary>All cached execution plans (one per OR branch), or <c>null</c> when the view has no cached plan.</summary>
    protected ExecutionPlan[] CachedPlans => _cachedPlans;

    /// <summary>True when cached execution plans are present.</summary>
    protected bool HasCachedPlanInternal => _cachedPlans != null;

    /// <summary>Drain the ring buffer, evaluate predicates, and update entity set and delta tracking.</summary>
    /// <remarks>
    /// The three trailing <c>caller…</c> parameters are populated by <c>[CallerFilePath]</c> / <c>[CallerLineNumber]</c> / <c>[CallerMemberName]</c>
    /// at user call sites. When the runtime/scheduler invokes a refresh, it must use <see cref="RefreshFromScheduler"/> instead so engine-internal
    /// paths don't end up as the "user execution site" in the trace.
    /// </remarks>
    public abstract void Refresh(
        Transaction tx,
        [CallerFilePath]   string callerFile = null,
        [CallerLineNumber] int    callerLine = 0,
        [CallerMemberName] string callerMethod = null);

    /// <summary>
    /// Scheduler / pipeline entry point. Bypasses caller-attribute capture so engine-internal call sites are NOT recorded as user execution sites
    /// (a scheduler-driven refresh has no user site — the owning system is the relevant attribution and is already on the span).
    /// </summary>
    internal void RefreshFromScheduler(Transaction tx) => Refresh(tx, callerFile: null, callerLine: 0, callerMethod: null);

    /// <summary>
    /// Emit a <see cref="Typhon.Profiler.TraceEventKind.QueryDefinitionDescribe"/> trace event for this view's
    /// owning query identity. Called once per system-tick from the runtime's per-system dispatch (see
    /// <c>TyphonRuntime.OnSystemStartInternal</c> / <c>OnParallelQueryPrepare</c>). The underlying tracker
    /// dedups across the session — only the first call per identity actually writes to the trace.
    /// Default implementation is a no-op; <see cref="EcsView{T}"/> overrides to emit. Telemetry-gated by
    /// the caller so this method may assume <c>TelemetryConfig.QueryActive == true</c> on entry.
    /// </summary>
    internal virtual void EmitDescriptorIfNeeded() { }

    /// <summary>
    /// Emit one <see cref="Typhon.Profiler.TraceEventKind.QueryPlan"/> span per system tick for this view's owning
    /// query identity, using caller-supplied start/end timestamps. Called from <c>TyphonRuntime.OnSystemEndInternal</c>
    /// after the system body completes. The pair (start, end) brackets the system's view-consumption window so the
    /// Workbench Execution Inspector can drill into per-execution timings even for pull-mode views that never go through
    /// <c>PlanBuilder.BuildPlan</c> at consumption time. The <paramref name="ownerSystemIdx"/> attaches the span to the
    /// owning scheduler system so the Workbench detail pane can round-trip from a clicked chunk to the execution.
    /// Default implementation is a no-op; <see cref="EcsView{T}"/> overrides to emit. Telemetry-gated by the caller
    /// (assumes <c>TelemetryConfig.QueryActive == true</c>).
    /// </summary>
    internal virtual void EmitPerTickQueryPlan(long startTimestamp, long endTimestamp, ushort ownerSystemIdx) { }

    /// <summary>Deregister from all owning ViewRegistries. Called during disposal.</summary>
    protected abstract void DeregisterFromRegistries();

    /// <summary>
    /// Deregisters the view from all owning registries and releases the delta ring buffer and entity set. Idempotent — subsequent calls are no-ops.
    /// </summary>
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

    /// <summary>
    /// Updates the entity set and delta tracking for one entity given its previous and new membership: adds it, removes it,
    /// or marks it <c>Modified</c> when membership is unchanged but the entity remains in the view.
    /// </summary>
    /// <param name="pk">Entity primary key.</param>
    /// <param name="wasInView">Whether the entity was in the view before this change.</param>
    /// <param name="shouldBeInView">Whether the entity should be in the view after this change.</param>
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
