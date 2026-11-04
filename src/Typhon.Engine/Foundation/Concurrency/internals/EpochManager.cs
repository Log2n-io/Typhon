// CS1591: this file declares public-accessibility types that live in the internal namespace (Phase 2b entanglement, see
// claude/research/PublicVsInternalApiClassification.md). They are excluded from the published API reference, so consumer-facing
// doc coverage is not enforced here.
#pragma warning disable 1591

using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

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

    /// <summary>
    /// This engine's <c>EW-01</c> tick-fence exclusivity guard.
    /// </summary>
    /// <remarks>
    /// It lives here because <see cref="EpochManager"/> is the one per-engine object that every structure the fence maintains already holds a reference to —
    /// a B+Tree reaches it through its segment, and so does the EntityMap. The alternative, a process-wide static, would let one engine's fence indict
    /// another engine's legal write under the parallel test fixtures. Nothing about epoch management is involved; this is scoping, and this is the object
    /// whose scope is right.
    /// </remarks>
    internal ExclusiveWindow FenceWindow { get; }

    public EpochManager(string id, IResource parent) : base(id, ResourceType.Synchronization, parent)
    {
        _globalEpoch = 1; // Start at 1 so 0 means "no epoch" / "not pinned"
        _registry = new EpochThreadRegistry();
        FenceWindow = new ExclusiveWindow();
    }

    /// <summary>Current global epoch value. Monotonically increasing.</summary>
    public long GlobalEpoch => _globalEpoch;

    /// <summary>
    /// The minimum epoch pinned by any active thread. Pages tagged with an epoch
    /// &gt;= this value cannot be evicted. Returns <see cref="GlobalEpoch"/> if no threads are active.
    /// </summary>
    public long MinActiveEpoch => _registry.ComputeMinActiveEpoch(_globalEpoch);

    /// <summary>The threads pinned to an epoch, oldest first — which scope holds <see cref="MinActiveEpoch"/> back. Diagnostic.</summary>
    internal string DescribePinnedThreads() => _registry.DescribePinnedThreads(_globalEpoch);

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
    /// Advances the global epoch and returns the new value, as the retire stamp for a resource being handed to a deferred reclaimer.
    /// </summary>
    /// <remarks>
    /// <c>Interlocked.Increment</c> rather than a plain read of <see cref="GlobalEpoch"/>, for two reasons. It is a full fence on x64 and arm64,
    /// so the retiring thread's preceding work — removing the resource from wherever readers find it — cannot sink below the reclaimer's
    /// subsequent scan of the pin slots; that is the reclaimer half of the Dekker pair whose other half is the seq-cst store in
    /// <c>EpochThreadRegistry.PinCurrentThread</c>. And it guarantees the stamp is strictly greater than any epoch a thread already pinned, so
    /// an in-flight reader is never mistaken for one that arrived afterwards.
    /// </remarks>
    internal long BumpEpochForRetire() => Interlocked.Increment(ref _globalEpoch);

    /// <summary>The epoch the calling thread is pinned to, or 0 if unpinned. Diagnostic only — for asserting a pin did not move mid-operation.</summary>
    internal long CurrentThreadPinnedEpoch => _registry.CurrentThreadPinnedEpoch;

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
    /// How many times this thread has moved its pin. A B+Tree cursor compares it on every step: what it stands on is protected by the pin it was read under,
    /// and a moved pin protects it no longer, so the cursor finds its place again before it reads (EP-02).
    /// </summary>
    [ThreadStatic]
    internal static int ThreadRefreshCount;

    /// <summary>
    /// Advance the current thread's pinned epoch without unpinning.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long RefreshScope()
    {
        var oldEpoch = _globalEpoch;
        var newEpoch = Interlocked.Increment(ref _globalEpoch);
        _registry.RefreshPinnedEpoch(newEpoch);
        ThreadRefreshCount++;
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

    /// <summary>The calling thread's scope nesting depth: 0 when it holds no scope.</summary>
    internal int CurrentThreadDepth => _registry.CurrentThreadDepth;

    /// <summary>
    /// Leaves the calling thread's only scope at a walk step (EP-03, EP-04): the thread unpins, so nothing it does not hold by slot reference stays
    /// protected for it. Unlike <see cref="ExitScopeUnordered"/> it does not advance the global epoch: a walk suspends once per batch, and every advance
    /// sends every other thread's warm B+Tree accessors down their cold path. The thread's own tags are released at the next advance any thread makes.
    /// </summary>
    /// <remarks>The caller has checked that the depth is exactly 1 and that the unit is its own (<see cref="EntityAccessor.TrySuspendEpochAtWalkStep"/>).</remarks>
    internal void SuspendScope()
    {
        Debug.Assert(CurrentThreadDepth == 1, "a walk suspends only the thread's only scope");
        _registry.UnpinCurrentThreadUnordered();
    }

    /// <summary>
    /// Re-enters a scope suspended by <see cref="SuspendScope"/>, pinned at <paramref name="epoch"/>: the current one at a call boundary, where the epoch may
    /// move anyway (EP-03); or, for a resume the page layer makes in the middle of a call, the one the scope left at, so what the call obtained since it began
    /// is held as a call without the pause would hold it. Neither restores a page evicted or a chunk reclaimed during the pause: what a suspension relies on
    /// is that nothing is held by the epoch alone across a walk step.
    /// </summary>
    /// <remarks>
    /// Pinning below the global epoch lowers <see cref="MinActiveEpoch"/>: only more pages and retirements are held, never fewer. The pin is the same
    /// sequentially consistent store a fresh scope makes, so the Dekker pair with every reclaimer holds (<c>EpochThreadRegistry.PinCurrentThread</c>).
    /// </remarks>
    internal void ResumeScopeAt(long epoch)
    {
        var depth = _registry.PinCurrentThread(epoch);
        Debug.Assert(depth == 0, "a suspended scope resumes only on a thread holding none");
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
