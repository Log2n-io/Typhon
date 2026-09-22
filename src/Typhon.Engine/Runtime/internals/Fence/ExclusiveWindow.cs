using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Runtime enforcement of <c>EW-01</c>: while the tick fence is open, no thread other than the fence's own may mutate the structures the fence maintains.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an assertion at the mutation sites and not a probe at the fence.</b> Step 3 of #872 tried two cheaper detectors and rejected both on evidence.
/// <c>TransactionChain.ActiveCount &gt; 0</c> at fence entry reddened 21 tests, none of them violations, because it counts transaction handles that have not
/// been <i>disposed</i> — including committed-but-in-scope ones and the long-lived read transaction that owns a pull View. And "no system runs concurrently
/// with the fence" verifies the half that was never in doubt, since that is what <c>TickEndCallback</c> means. Neither measures the invariant. This does: it
/// fires on an actual write, from an actual foreign thread, while the window is actually open, and it catches a writer reached by any path — including ones
/// no source scan would find, which is the whole reason step 3 deferred its <c>AC-3.4</c> to here.
/// </para>
/// <para>
/// <b>Per engine, never global.</b> The window hangs off <see cref="EpochManager"/> because that is the one per-engine object every mutation site already
/// holds a reference to. A process-wide static would be simpler and wrong: fixtures run in parallel, so one engine's fence would indict another engine's
/// perfectly legal write, and this project has already paid for a global that behaved that way (the unsynchronised <c>ArchetypeRegistry</c>).
/// </para>
/// <para>
/// <b>Always compiled, not <c>[Conditional("DEBUG")]</c>.</b> The merge gate runs Release, so a Debug-only guard is one the gate never executes — and
/// <c>AC-6.6</c> asks for the sharded and the serial passes both. The closed-window cost is one relaxed field read and a branch that is never taken outside
/// the fence.
/// </para>
/// </remarks>
internal sealed class ExclusiveWindow
{
    /// <summary>Open depth. Non-zero means a fence is running on this engine.</summary>
    /// <remarks>
    /// A depth rather than a flag because a runtime-less host may call <c>WriteTickFence</c> from inside something that has already opened it, and because
    /// the serial and parallel fence paths are not mutually exclusive by construction — only by current wiring.
    /// </remarks>
    private int _depth;

    private int _violations;
    private string _firstViolationSite;
    private int _firstViolationThreadId;

    /// <summary>Set the first time a fence thread mutates a guarded structure with the window open.</summary>
    /// <remarks>
    /// This exists so <c>AC-6.1</c>'s verifier cannot pass vacuously. "Zero foreign writers" is also what an engine that never opened the window, or never
    /// wrote an index inside it, would report — so the test asserts this alongside the zero, and a workload that produces no fence-time mutation fails as
    /// loudly as one that produces a foreign write.
    /// <para>
    /// Written under a read guard rather than unconditionally. W workers store to one field on every mutation would ping-pong the line between cores for the
    /// life of the fence; storing only on the transition leaves a shared line that is read-only after the first write of the run.
    /// </para>
    /// </remarks>
    private bool _sawFenceMutation;

    /// <summary>
    /// Per-thread nesting count of "this thread is part of the fence". Set by the thread that opens the window and by each fence worker for the duration of
    /// its chunk.
    /// </summary>
    /// <remarks>
    /// <c>[ThreadStatic]</c> and therefore shared across engines on one thread. That is the correct direction to be imprecise in: it can only ever cause a
    /// missed report (a thread legitimately inside engine A's fence writing to engine B), never a false one. A detector that cries wolf gets deleted; one
    /// that occasionally stays quiet still catches the case it was built for.
    /// </remarks>
    [ThreadStatic]
    private static int FenceThreadDepth;

    /// <summary>True while a fence is running on this engine.</summary>
    internal bool IsOpen => Volatile.Read(ref _depth) != 0;

    /// <summary>Foreign mutations seen since the last <see cref="ResetCounters"/>. <c>AC-6.1</c> asserts this is zero.</summary>
    internal int Violations => Volatile.Read(ref _violations);

    /// <summary>The first offending call site, or <c>null</c> when there has been none.</summary>
    internal string FirstViolationSite => Volatile.Read(ref _firstViolationSite);

    /// <summary>Managed thread id of the first offender, or zero.</summary>
    /// <remarks>
    /// Published AFTER <see cref="FirstViolationSite"/> wins its CAS, so a reader that catches the window between the two sees the site with a thread id of
    /// zero. Left that way deliberately: closing it means CAS-ing site and id together as one object, which is an allocation on a path whose next statement
    /// is a <c>throw</c>, and folding the id into the site string would cost the typed accessor the live-workload verifier puts in its failure message. The
    /// site alone identifies the offending call; the id is a convenience.
    /// </remarks>
    internal int FirstViolationThreadId => Volatile.Read(ref _firstViolationThreadId);

    /// <summary>True once a fence thread has mutated a guarded structure with the window open. See <see cref="_sawFenceMutation"/>.</summary>
    internal bool ObservedFenceMutation => Volatile.Read(ref _sawFenceMutation);

    /// <summary>Clears the violation record. Tests only — the counters are cumulative for the life of the engine otherwise.</summary>
    internal void ResetCounters()
    {
        Volatile.Write(ref _violations, 0);
        Volatile.Write(ref _firstViolationSite, null);
        Volatile.Write(ref _firstViolationThreadId, 0);
        Volatile.Write(ref _sawFenceMutation, false);
    }

    /// <summary>Opens the window and enrols the calling thread, which is doing the fence's serial work.</summary>
    internal Scope Open()
    {
        Interlocked.Increment(ref _depth);
        FenceThreadDepth++;
        return new Scope(this, true);
    }

    /// <summary>Enrols the calling thread as a fence worker without changing the window's open state.</summary>
    internal Scope EnterWorker()
    {
        FenceThreadDepth++;
        return new Scope(this, false);
    }

    /// <summary>
    /// Records a mutation of a fence-owned structure, and fails loudly when it comes from outside the fence while the window is open.
    /// </summary>
    /// <param name="site">Call-site name for the message. A literal at every call, so no allocation and no formatting on the fast path.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void NoteMutation(string site)
    {
        // Closed is overwhelmingly the common case: outside the fence this is one field read and a not-taken branch.
        if (Volatile.Read(ref _depth) == 0)
        {
            return;
        }

        // `> 0`, not `!= 0`. A Scope disposed on a thread other than the one that opened it drives this counter NEGATIVE, and a negative depth read as
        // "enrolled" would silently switch the detector off on two threads at once — for the rest of the process, since both are pool threads that outlive
        // the fence. The whole point of this class is to fail loudly; the one failure mode it must not have is going quiet.
        if (FenceThreadDepth > 0)
        {
            if (!Volatile.Read(ref _sawFenceMutation))
            {
                Volatile.Write(ref _sawFenceMutation, true);
            }

            return;
        }

        Violation(site);
    }

    /// <remarks>
    /// Counted before it throws. The throw is what <c>EW-01</c>'s <c>[fatal]</c> asks for — the alternative is silent structural corruption that the B+Tree
    /// validators find much later or a query answers wrongly and nothing finds at all — but a foreign thread is exactly the kind of place an exception gets
    /// swallowed, so the count is what a test can assert on regardless.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Violation(string site)
    {
        // The site is published BEFORE the count, and the order matters: a test that reads a non-zero Violations and then reads FirstViolationSite must
        // not be able to see the count without the detail. Interlocked.Increment is a full fence, so the two writes above cannot sink past it. The CAS on the
        // site is what makes "first" mean first — two concurrent offenders would otherwise race to overwrite each other.
        var threadId = Environment.CurrentManagedThreadId;
        if (Interlocked.CompareExchange(ref _firstViolationSite, site, null) == null)
        {
            Volatile.Write(ref _firstViolationThreadId, threadId);
        }

        Interlocked.Increment(ref _violations);

        ThrowHelper.ThrowInvalidOp(
            $"EW-01 violation: thread {threadId} called {site} while the tick fence was open. The fence writes cluster B+Trees, the EntityMap and the "
            + "per-cell spatial structures with no OLC validation, no write latch and no B-link right-walk, so a concurrent mutation corrupts them silently. "
            + "The usual cause is a side transaction from TickContext.CreateSideTransaction committed after the system that created it returned — commit and "
            + "dispose it before returning. A host driving WriteTickFence itself owns the same obligation.");
    }

    /// <summary>Undoes one <see cref="Open"/> or <see cref="EnterWorker"/>.</summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly ExclusiveWindow _window;
        private readonly bool _owned;

        internal Scope(ExclusiveWindow window, bool owned)
        {
            _window = window;
            _owned = owned;
        }

        public void Dispose()
        {
            if (_window == null)
            {
                return;
            }

            FenceThreadDepth--;
            Debug.Assert(FenceThreadDepth >= 0, "ExclusiveWindow.Scope disposed on a thread that never entered it — see the `> 0` note in NoteMutation.");

            if (_owned)
            {
                Interlocked.Decrement(ref _window._depth);
            }
        }
    }
}
