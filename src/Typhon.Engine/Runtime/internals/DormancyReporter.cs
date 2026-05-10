using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// Static API for collecting deferred cluster wake requests during parallel system execution (issue #233).
/// Uses <c>[ThreadStatic]</c> lists so each worker thread appends without contention. A global <see cref="ConcurrentBag{T}"/> registers all thread-local lists
/// for single-threaded drain at the migration fence.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Each <see cref="List{T}"/> is written by exactly one thread (the <c>[ThreadStatic]</c> owner). <see cref="DrainAll"/> reads
/// all lists single-threaded after all parallel work completes — no race.</para> <para><b>Entry format:</b> Each <c>long</c> packs
/// <c>(archetypeId &lt;&lt; 32 | (uint)chunkId)</c>. This avoids per-request struct allocation and keeps the thread-local list a flat <c>List&lt;long&gt;</c>.</para>
/// <para><b>Deduplication:</b> Implicit — <see cref="ArchetypeClusterState.ProcessWakeRequest"/> is a no-op for clusters that are
/// already <see cref="ClusterSleepState.WakePending"/>.</para>
/// </remarks>
internal static class DormancyReporter
{
    [ThreadStatic] private static List<long> t_wakeRequests;
    [ThreadStatic] private static bool t_registered;

    // Registry of all thread-local lists. ConcurrentBag.Add is lock-free; iteration at drain time is safe because it runs single-threaded after all parallel
    // systems complete.
    private static readonly ConcurrentBag<List<long>> s_allLists = new();

    // Atomic flag for O(1) HasPendingRequests in the common case (no requests). Set by RequestWake, cleared by DrainAll.
    private static volatile bool s_hasAnyRequest;

    /// <summary>
    /// Request that a sleeping cluster be woken. Safe to call from any thread during parallel system execution.
    /// The request is deferred — the cluster transitions to <see cref="ClusterSleepState.WakePending"/> at the next migration fence,
    /// and becomes <see cref="ClusterSleepState.Active"/> at the start of the following tick.
    /// </summary>
    /// <param name="archetypeId">Archetype that owns the cluster (needed to route the request at drain time).</param>
    /// <param name="clusterChunkId">Cluster chunk ID within the archetype.</param>
    public static void RequestWake(int archetypeId, int clusterChunkId)
    {
        if (t_wakeRequests == null)
        {
            t_wakeRequests = new List<long>(64);
            if (!t_registered)
            {
                s_allLists.Add(t_wakeRequests);
                t_registered = true;
            }
        }
        t_wakeRequests.Add(((long)archetypeId << 32) | (uint)clusterChunkId);
        s_hasAnyRequest = true;
    }

    /// <summary>
    /// Drain all thread-local wake request lists and dispatch to the appropriate <see cref="ArchetypeClusterState"/>.
    /// Called single-threaded from <c>WriteClusterTickFence</c> before the per-archetype loop.
    /// </summary>
    /// <param name="archetypeStates">Engine's per-archetype state array, indexed by archetype ID.</param>
    internal static void DrainAll(ArchetypeEngineState[] archetypeStates)
    {
        foreach (var list in s_allLists)
        {
            for (int i = 0; i < list.Count; i++)
            {
                long packed = list[i];
                int archetypeId = (int)(packed >> 32);
                int chunkId = (int)(packed & 0xFFFFFFFF);

                if (archetypeId >= 0 && archetypeId < archetypeStates.Length)
                {
                    var cs = archetypeStates[archetypeId]?.ClusterState;
                    cs?.ProcessWakeRequest(chunkId);
                }
            }
            list.Clear();
        }
        s_hasAnyRequest = false;
    }

    /// <summary>
    /// Returns true if any thread-local list has pending wake requests. O(1) via atomic flag in the common case (no requests).
    /// </summary>
    internal static bool HasPendingRequests => s_hasAnyRequest;

    /// <summary>
    /// Reset all thread-local lists. For test teardown only — ensures no stale state bleeds between tests.
    /// </summary>
    internal static void Reset()
    {
        foreach (var list in s_allLists)
        {
            list.Clear();
        }
    }
}
