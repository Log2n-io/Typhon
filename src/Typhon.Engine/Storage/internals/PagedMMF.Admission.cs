// CS1591: PagedMMF is public in the internal namespace (see PagedMMF.cs); not part of the published API reference.
#pragma warning disable 1591

using System;
using System.Threading;

namespace Typhon.Engine.Internals;

public partial class PagedMMF
{
    // ═══════════════════════════════════════════════════════════════════════
    // S4/S5 (#1382): admission, paced by the health state
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Units of work admitted and not yet disposed. Counted on every creation, so the gate knows how many run when it turns on; alone on its cache line.
    /// </summary>
    private PaddedCounter _admittedUnits;

    /// <summary>How many units of work may run at once; 0 when the gate is off. Set by the health evaluation (<see cref="AdjustAdmission"/>).</summary>
    private int _admissionLimit;

    private readonly object _admissionSync = new();
    private int _admissionWaiters;
    private long _admissionWaits;
    private long _admissionDeadlineAdmits;

    /// <summary>The fewest units of work the gate ever lets run: one always does, so the gate alone never stops the engine.</summary>
    internal const int MinAdmissionLimit = 1;

    /// <summary>
    /// The least the gate lets run when it turns on at Overloaded: a quarter of the cores. The units running at that instant can be few — one, measured,
    /// with 16 writers between their units of work — and a limit set from them alone throttled a merely slow engine to one writer.
    /// </summary>
    internal static int OverloadedAdmissionFloor => Math.Max(MinAdmissionLimit, Environment.ProcessorCount / 4);

    /// <summary>How long one admission round waits before the waiter re-evaluates the health state itself: waiting writers allocate nothing.</summary>
    internal static int AdmissionRoundMilliseconds = 10;

    /// <summary>The gate's current limit, 0 when off (S4).</summary>
    internal int AdmissionLimit => Volatile.Read(ref _admissionLimit);

    /// <summary>Units of work admitted and not yet disposed.</summary>
    internal long AdmittedUnits => Volatile.Read(ref _admittedUnits.Value);

    /// <summary>Units of work that waited at the gate before they were admitted.</summary>
    internal long AdmissionWaits => Interlocked.Read(ref _admissionWaits);

    /// <summary>Units of work admitted past the limit because their deadline came: slowed, never refused.</summary>
    internal long AdmissionDeadlineAdmits => Interlocked.Read(ref _admissionDeadlineAdmits);

    /// <summary>
    /// Admits a unit of work (S4 of the starvation policy, #1382): at once while the gate is off, else once fewer than <see cref="AdmissionLimit"/> run.
    /// The wait holds nothing — no pin, latch or lock, before any append (PS-23) — so it slows the work at its start rather than in the middle, where
    /// a waiter's pin keeps pages from the work already running. A thread already inside an epoch scope is never held: it would wait with its pin. Past
    /// <paramref name="wc"/>'s deadline the unit is admitted all the same: the gate paces, it does not refuse. Pair with <see cref="ReleaseAdmission"/>.
    /// </summary>
    internal void Admit(ref WaitContext wc)
    {
        // Nor is a thread that already holds an admitted unit: it would wait holding that unit's marks and registry slot, and at a limit of one it waits
        // for itself until its deadline.
        if (Volatile.Read(ref _admissionLimit) == 0 || t_heldAdmissions > 0 || EpochManager.IsCurrentThreadInScope)
        {
            Interlocked.Increment(ref _admittedUnits.Value);
            t_heldAdmissions++;
            return;
        }

        AdmitSlow(ref wc);
        t_heldAdmissions++;
    }

    /// <summary>Units of work this thread holds, admitted by <see cref="Admit"/> and not yet released (by this thread).</summary>
    [ThreadStatic] private static int t_heldAdmissions;

    private void AdmitSlow(ref WaitContext wc)
    {
        var waited = false;
        lock (_admissionSync)
        {
            Interlocked.Increment(ref _admissionWaiters);   // a full fence: ordered before the count read below, as the release's are (B4)
            try
            {
                while (true)
                {
                    var limit = Volatile.Read(ref _admissionLimit);
                    if (limit == 0 || Volatile.Read(ref _admittedUnits.Value) < limit)
                    {
                        break;
                    }

                    if (wc.ShouldStop)
                    {
                        Interlocked.Increment(ref _admissionDeadlineAdmits);
                        break;
                    }

                    waited = true;
                    Monitor.Wait(_admissionSync, AdmissionRoundMilliseconds);

                    // The writers waiting here allocate nothing, so nothing else may evaluate the state that holds them; a waiter does — outside the lock:
                    // a transition logs and raises the public event, which must not run under it, nor stall the releases behind it.
                    Monitor.Exit(_admissionSync);
                    try
                    {
                        EvaluateStorageHealth();
                    }
                    finally
                    {
                        Monitor.Enter(_admissionSync);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _admissionWaiters);
            }

            // Counted under the lock: two waiters woken together see each other's admission.
            Interlocked.Increment(ref _admittedUnits.Value);
        }

        if (waited)
        {
            Interlocked.Increment(ref _admissionWaits);
        }
    }

    /// <summary>A unit of work admitted by <see cref="Admit"/> on thread <paramref name="admittedBy"/> ended: one more may run.</summary>
    internal void ReleaseAdmission(int admittedBy)
    {
        if (admittedBy == Environment.CurrentManagedThreadId && t_heldAdmissions > 0)
        {
            t_heldAdmissions--;
        }

        Interlocked.Decrement(ref _admittedUnits.Value);
        if (Volatile.Read(ref _admissionWaiters) != 0)
        {
            lock (_admissionSync)
            {
                Monitor.Pulse(_admissionSync);
            }
        }
    }

    /// <summary>
    /// Sets the gate's limit from the health state, at each evaluation (S5): off when Healthy; Overloaded turns it on at three quarters of the units
    /// running and holds it, never below <see cref="OverloadedAdmissionFloor"/>; Starved halves it, down to one, once what runs is within it; Pressured
    /// raises it by a quarter. Every change is logged, and one that raises the limit
    /// wakes the waiters. Evaluations are at least <see cref="HealthEvaluationInterval"/> apart, so a step down from Overloaded, a second of calm, has
    /// already let the limit grow back.
    /// </summary>
    private void AdjustAdmission(StorageHealthState state)
    {
        var limit = Volatile.Read(ref _admissionLimit);
        var running = (int)Math.Min(int.MaxValue, Math.Max(0, AdmittedUnits));
        int next;
        switch (state)
        {
            case StorageHealthState.Starved:
                // Halved once the last cut took effect, not every evaluation: running far above the limit means the work admitted before is still
                // draining, and halving again only locks the engine at one when it recovers.
                next = limit == 0 ? Math.Max(MinAdmissionLimit, running / 2) : running <= limit ? Math.Max(MinAdmissionLimit, limit / 2) : limit;
                break;
            case StorageHealthState.Overloaded:
                next = limit != 0 ? Math.Max(limit, OverloadedAdmissionFloor) : Math.Max(OverloadedAdmissionFloor, running - running / 4);
                break;
            case StorageHealthState.Pressured:
                next = limit == 0 ? 0 : limit + Math.Max(1, limit / 4);
                break;
            default:
                next = 0;
                break;
        }

        if (next == limit)
        {
            return;
        }

        Volatile.Write(ref _admissionLimit, next);
        if (Logger != null)
        {
            LogAdmissionLimitChanged(Logger, state, limit, next, running);
        }

        if (next == 0 || next > limit)
        {
            lock (_admissionSync)
            {
                Monitor.PulseAll(_admissionSync);
            }
        }
    }

    /// <summary>Test seam: sets the gate's limit, 0 for off, waking the waiters.</summary>
    internal void SetAdmissionLimitForTests(int limit)
    {
        Volatile.Write(ref _admissionLimit, limit);
        lock (_admissionSync)
        {
            Monitor.PulseAll(_admissionSync);
        }
    }

    /// <summary>Test seam: one evaluation's step of the limit, for <paramref name="state"/>.</summary>
    internal void AdjustAdmissionForTests(StorageHealthState state) => AdjustAdmission(state);

    /// <summary>Test seam: units of work waiting at the gate now.</summary>
    internal int AdmissionWaitersForTests => Volatile.Read(ref _admissionWaiters);
}
