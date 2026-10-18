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

    /// <summary>True when the calling thread is part of a fence that is running on this engine: the fence's own thread or one of its workers.</summary>
    internal bool OnFenceThread => FenceThreadDepth > 0 && IsOpen;

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
    /// Records a mutation of a fence-owned structure, and fails loudly when it comes from outside the fence while the window is open AND the structure is one
    /// the fence is currently writing without its protocol.
    /// </summary>
    /// <param name="site">Call-site name for the message. A literal at every call, so no allocation and no formatting on the fast path.</param>
    /// <param name="unlatchedHere">
    /// Whether the fence is, right now, mutating THIS structure through a path that has dropped its latch / OLC validation.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Observation and violation are separate, and conflating them is what made this detector reject correct code.</b> The fence writes B+Trees and the
    /// EntityMap every tick, almost entirely through their ordinary latched mutators; it drops the protocol on exactly one path per structure — a B+Tree's bulk
    /// value rewrite, which repoints migrants' index entries and is therefore reached only for a SPATIAL archetype, and an EntityMap's pre-sorted bulk batch,
    /// which a concurrent insert can invalidate by splitting the map. So:
    /// </para>
    /// <list type="bullet">
    ///   <item>every fence-thread mutation still sets <see cref="ObservedFenceMutation"/>, because that is the anti-vacuity signal EW-01's verifier pairs with
    ///         its zero — a fence that wrote nothing must fail as loudly as one that was raced;</item>
    ///   <item>a foreign-thread mutation is a violation only when <paramref name="unlatchedHere"/>, because everywhere else the structure's own latch or OLC
    ///         protocol makes it safe — and it always did.</item>
    /// </list>
    /// <para>
    /// Before this parameter existed the check was "is any fence running on this engine", with no idea which structure the caller held. An off-tick commit of a
    /// <c>StorageMode.Versioned</c>, non-spatial entity — which writes the EntityMap on spawn and a B+Tree for any indexed field, both fully latched — was
    /// rejected for a hazard that was not present. That is a false positive on the engine's own concurrency guarantee, and the guarantee is the thing to keep.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void NoteMutation(string site, bool unlatchedHere)
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

        // Foreign, with the window open — but harmless unless the fence has dropped this structure's protocol.
        if (!unlatchedHere)
        {
            return;
        }

        Violation(site);
    }

    /// <summary>
    /// Reports a mutation whose "is this structure unlatched" answer is a counter the caller owns.
    /// </summary>
    /// <param name="site">Call-site name, a literal at every call.</param>
    /// <param name="unlatchedDepth">The structure's own unlatched-rewrite depth, read only if the window is open.</param>
    /// <remarks>
    /// <b>By <c>ref</c> so the depth is not READ when the window is closed, which is almost always.</b> A caller that passed
    /// <c>Volatile.Read(ref depth) != 0</c> evaluated the argument first, so every latched mutation paid that load to hand it to a
    /// method whose first act is to return — one load more than the unconditional call it replaced, on the engine's hottest index
    /// path. Taking the counter instead of the answer moves it after the early-out.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void NoteMutation(string site, ref int unlatchedDepth)
    {
        if (Volatile.Read(ref _depth) == 0)
        {
            return;
        }

        NoteMutationSlow(site, Volatile.Read(ref unlatchedDepth) != 0);
    }

    /// <summary>The open-window half of <see cref="NoteMutation(string, ref int)"/>, kept out of line so the fast path stays small.</summary>
    /// <param name="site">Call-site name.</param>
    /// <param name="unlatchedHere">Whether the fence has dropped this structure's protocol right now.</param>
    private void NoteMutationSlow(string site, bool unlatchedHere)
    {
        if (FenceThreadDepth > 0)
        {
            if (!Volatile.Read(ref _sawFenceMutation))
            {
                Volatile.Write(ref _sawFenceMutation, true);
            }

            return;
        }

        if (!unlatchedHere)
        {
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
            $"EW-01 violation: thread {threadId} called {site} while the tick fence was rewriting that same structure without its latch — a B+Tree's bulk "
            + "value rewrite or the EntityMap's pre-sorted batch, both reached only for a SPATIAL archetype's migrants. A concurrent mutation there relocates "
            + "keys out from under a slice that is not validating versions, or splits the map the batch was sorted against. Do this work from a system, or "
            + "from a side transaction committed and disposed before the creating system returns. A host driving WriteTickFence itself owns the same "
            + "obligation. NOTE: an ordinary off-tick write to a Versioned, non-spatial entity is NOT this — it is safe and no longer reported.");
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
