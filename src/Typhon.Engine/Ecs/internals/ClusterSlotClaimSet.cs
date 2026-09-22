using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// A set of <c>(cluster chunk id, slot index)</c> pairs, held as one 64-bit occupancy word per cluster in a flat array indexed by chunk id.
/// </summary>
/// <remarks>
/// <para><b>#882 — why this replaced a <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>.</b> Both of the tick fence's source-exclusion sets
/// (<c>CR-05</c>) are keyed by cluster chunk id, and chunk ids are allocated lowest-free-first, so they are dense from zero with holes rather than an
/// arbitrary key space. The throttle probes its set <b>once per queued relocation</b> — 54 800 hashed lookups per tick at the 25 % reference point of the
/// #872 matrix, to admit about 1 300 moves — and that was the largest single term in a step measured at 21 % of the fence's Prep phase. A flat array turns
/// each probe into one bounds-checked load.</para>
/// <para><b>The clear is O(touched), not O(capacity)</b>, which is what makes the flat array affordable on a sparse tick. Touched chunk ids are recorded the
/// first time a cluster's word goes non-zero, and <see cref="Clear"/> walks that list. This is the same shape as
/// <c>ArchetypeClusterState.ClearAabbRefreshBookkeeping</c>, which clears three flat per-chunk-id arrays through a membership bitmap for the same
/// reason.</para>
/// <para><b>No rule prescribes a container.</b> <c>CR-05</c> constrains the two sets' semantics — separate lifetimes, neither subsuming the other, only
/// mandatory requests claiming — and <c>TH-01</c> constrains the partition's order. Both are unchanged by the representation.</para>
/// <para>Built inside one archetype's Prep — one work item, one worker — and, since step 15, also READ from user threads by placement's draining-cluster
/// exclusion (<c>TryClaimPlaced</c>): a reader that sees last tick's set, or a torn view of this tick's, skips at most one candidate, and
/// <see cref="ContainsCluster"/> bounds-checks the array reference it read.</para>
/// </remarks>
internal sealed class ClusterSlotClaimSet
{
    /// <summary>One word per cluster chunk id; bit <c>s</c> set means slot <c>s</c> of that cluster is claimed. Grown by doubling, never shrunk.</summary>
    private ulong[] _claims = [];

    /// <summary>Chunk ids whose word is non-zero, in first-claim order — the domain <see cref="Clear"/> has to visit.</summary>
    private int[] _touched = [];

    private int _touchedCount;

    /// <summary>Number of distinct clusters holding at least one claim. Zero means the set is empty, which is what the callers gate on.</summary>
    internal int Count => _touchedCount;

    /// <summary>Drop every claim. Proportional to the clusters actually claimed, not to the segment's capacity.</summary>
    internal void Clear()
    {
        var claims = _claims;
        var touched = _touched;
        for (var i = 0; i < _touchedCount; i++)
        {
            claims[touched[i]] = 0UL;
        }

        _touchedCount = 0;
    }

    /// <summary>Claim one slot of one cluster. Idempotent — claiming a slot twice records the cluster once.</summary>
    /// <remarks>
    /// The two asserts are the type's contract made checkable rather than assumed. A negative chunk id would index below the array and a slot index past 63
    /// would wrap the shift modulo 64 and claim the wrong slot — both silent. Neither is reachable from today's producers (<see cref="MigrationRequest"/>
    /// masks the slot to six bits and every filer supplies a real chunk id), but the callers include <c>CR-05</c>'s duplicate-source guard, whose whole value
    /// is naming the defect precisely; an <see cref="IndexOutOfRangeException"/> from in here would replace that diagnosis with a stack trace.
    /// </remarks>
    internal void Claim(int clusterChunkId, int slotIndex)
    {
        Debug.Assert(clusterChunkId >= 0, "cluster chunk ids are segment indices and are never negative");
        Debug.Assert((uint)slotIndex < 64, "a slot index past 63 would wrap the shift modulo 64 and claim a different slot");

        if ((uint)clusterChunkId >= (uint)_claims.Length)
        {
            Grow(clusterChunkId);
        }

        var before = _claims[clusterChunkId];
        _claims[clusterChunkId] = before | (1UL << slotIndex);

        // Record the cluster only on the transition out of zero, so `_touched` holds each id once and `Count` is a distinct-cluster count.
        if (before == 0UL)
        {
            if (_touchedCount == _touched.Length)
            {
                Array.Resize(ref _touched, Math.Max(64, _touched.Length * 2));
            }

            _touched[_touchedCount++] = clusterChunkId;
        }
    }

    /// <summary>The claimed-slot mask for one cluster, or zero — including for a chunk id past anything ever claimed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ulong ClaimedSlots(int clusterChunkId)
        => (uint)clusterChunkId < (uint)_claims.Length ? _claims[clusterChunkId] : 0UL;

    /// <summary>True when any slot of <paramref name="clusterChunkId"/> is claimed. Equivalent to the dictionary's <c>ContainsKey</c>, because a word is only
    /// ever written with at least one bit set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ContainsCluster(int clusterChunkId) => ClaimedSlots(clusterChunkId) != 0UL;

    /// <summary>
    /// Grow to cover <paramref name="clusterChunkId"/>. Reachable on a normal tick, not only pathologically: the repair planner allocates fresh destination
    /// clusters mid-Prep, so a chunk id past the capacity the set was last sized for is ordinary. Doubling rather than exact, so a population creeping upward
    /// by one does not reallocate every tick.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int clusterChunkId)
    {
        var target = Math.Max(clusterChunkId + 1, Math.Max(256, _claims.Length * 2));
        Array.Resize(ref _claims, target);
    }
}
