using System;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Debug-only concurrency guard for the replication structures, whose "single-threaded by contract" claim is otherwise unenforceable.
/// </summary>
/// <remarks>
/// <para>
/// <b>It detects two callers at once — NOT a change of thread.</b> That distinction is the whole design, and the first version got it wrong. The pool,
/// directory and identity allocator are serialized by the tick fence's PHASE BARRIERS, not by running on one particular thread. The cluster-drain path
/// proves it: <c>DrainPendingClusterFinalizations</c> is reached either from <c>PrepareArchetypeFinalizeHeads</c> on the driver thread or from
/// <c>FinalizeArchetypeFence</c> as a dispatched work item on an arbitrary pool worker, and which one an archetype takes is decided per tick by
/// <c>FinalizeSliceable</c>. A guard that adopted the first caller and rejected every later one therefore threw on perfectly correct code the first time an
/// archetype changed path — and, worse, threw from inside a drain loop that has no try/finally, leaking the chunk id it was about to free.
/// </para>
/// <para>
/// What must never happen is two callers <i>inside at the same time</i>: the directory's backing map publishes its array, capacity and mask as separate
/// plain writes when it grows, so an overlapping reader gets a torn view rather than a stale one, and the pool's intrusive free list corrupts the same way.
/// That is what this catches.
/// </para>
/// <para>
/// <c>[Conditional("DEBUG")]</c> on both halves means the pair vanishes from Release entirely, so this costs the tick path nothing. Callers pair them with
/// try/finally; an <see cref="Exit"/> that never ran would wedge the guard and report a false positive on the next call, which is worse than no guard.
/// </para>
/// </remarks>
internal struct ReplicationThreadAffinity
{
    /// <summary>1 while a caller is inside a guarded member. Interlocked because detecting a race cannot itself race.</summary>
    private int _inside;

    /// <summary>Marks entry to a guarded member, failing if another caller is already inside one.</summary>
    /// <param name="owner">The structure being guarded, for the message.</param>
    /// <param name="member">The member being entered, for the message.</param>
    [Conditional("DEBUG")]
    public void Enter(string owner, string member)
    {
        if (Interlocked.CompareExchange(ref _inside, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                $"{owner}.{member} was entered while another caller was already inside {owner}. These structures carry no lock because the tick fence's " +
                "phase barriers serialize them; two callers at once corrupts the free list and can tear the directory's backing array. Note this is a " +
                "CONCURRENCY failure, not a thread-identity one — running on a different thread from last tick is legal and expected.");
        }
    }

    /// <summary>Marks exit from a guarded member. Must be reached on every path, including exceptional ones.</summary>
    [Conditional("DEBUG")]
    public void Exit() => Volatile.Write(ref _inside, 0);
}
