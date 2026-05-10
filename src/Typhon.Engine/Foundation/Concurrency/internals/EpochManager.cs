using JetBrains.Annotations;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Engine.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Manages epoch-based resource protection. One instance per <see cref="DatabaseEngine"/>.
/// Threads enter/exit epoch scopes via <see cref="EpochGuard"/>; pages tagged with an epoch
/// cannot be evicted until all scopes referencing that epoch have exited.
/// </summary>
[PublicAPI]
public sealed class EpochManager : ResourceNode, IMetricSource
{
    private long _globalEpoch;
    private readonly EpochThreadRegistry _registry;

    public EpochManager(string id, IResource parent) : base(id, ResourceType.Synchronization, parent)
    {
        _globalEpoch = 1; // Start at 1 so 0 means "no epoch" / "not pinned"
        _registry = new EpochThreadRegistry();
    }

    /// <summary>Current global epoch value. Monotonically increasing.</summary>
    public long GlobalEpoch => _globalEpoch;

    /// <summary>
    /// The minimum epoch pinned by any active thread. Pages tagged with an epoch
    /// &gt;= this value cannot be evicted. Returns <see cref="GlobalEpoch"/> if no threads are active.
    /// </summary>
    public long MinActiveEpoch => _registry.ComputeMinActiveEpoch(_globalEpoch);

    /// <summary>Number of active (pinned) slots in the thread registry.</summary>
    public int ActiveSlotCount => _registry.ActiveSlotCount;

    /// <summary>Returns true if the current thread is inside an epoch scope (depth &gt; 0).</summary>
    public bool IsCurrentThreadInScope => _registry.IsCurrentThreadInScope;

    // ═══════════════════════════════════════════════════════════════════════
    // Scope Management
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enter an epoch scope on the current thread. Pins the current global epoch,
    /// preventing eviction of pages tagged with this or later epochs.
    /// </summary>
    /// <returns>The depth before entering (0 for outermost scope).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int EnterScope() => _registry.PinCurrentThread(_globalEpoch);

    /// <summary>
    /// Exit an epoch scope on the current thread. If this is the outermost scope,
    /// unpins the thread and advances the global epoch. Enforces LIFO ordering.
    /// </summary>
    /// <param name="expectedDepth">The depth returned by the matching <see cref="EnterScope"/> call.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitScope(int expectedDepth)
    {
        if (_registry.UnpinCurrentThread(expectedDepth))
        {
            // Outermost scope exited — advance the global epoch
            var newEpoch = Interlocked.Increment(ref _globalEpoch);
            TyphonEvent.EmitConcurrencyEpochAdvance((uint)newEpoch);
        }
    }

    /// <summary>
    /// Advance the current thread's pinned epoch without unpinning.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long RefreshScope()
    {
        var oldEpoch = _globalEpoch;
        var newEpoch = Interlocked.Increment(ref _globalEpoch);
        _registry.RefreshPinnedEpoch(newEpoch);
        TyphonEvent.EmitConcurrencyEpochRefresh((uint)oldEpoch, (uint)newEpoch);
        return newEpoch;
    }

    /// <summary>
    /// Exit an epoch scope without enforcing LIFO ordering. Used by <see cref="Transaction"/>
    /// which can be disposed in any order. If this is the outermost scope, unpins the thread
    /// and advances the global epoch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitScopeUnordered()
    {
        if (_registry.UnpinCurrentThreadUnordered())
        {
            // Outermost scope exited — advance the global epoch
            var newEpoch = Interlocked.Increment(ref _globalEpoch);
            TyphonEvent.EmitConcurrencyEpochAdvance((uint)newEpoch);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // IMetricSource
    // ═══════════════════════════════════════════════════════════════════════

    public void ReadMetrics(IMetricWriter writer) => writer.WriteCapacity(_registry.ActiveSlotCount, EpochThreadRegistry.MaxSlots);

    public void ResetPeaks()
    {
        // No high-water marks currently tracked
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _registry.Dispose();
        }
        base.Dispose(disposing);
    }
}
