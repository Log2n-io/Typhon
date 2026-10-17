using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

public partial class PagedMMF
{
    // ═══════════════════════════════════════════════════════════════════════
    // S1 (#1382): a wait goes on while the cache serves pages
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// This cache's allocations that found a slot while it was under pressure: the progress an overload wait looks for (<see cref="OverloadBudget"/>)
    /// and the health state's. Counted only under pressure, so the healthy path pays nothing; a waiter's rounds keep the flag set for any wait. Alone on
    /// its cache line; plain increments, a lost one only delays an extension by a page.
    /// </summary>
    private PaddedCounter _servedUnderPressure;

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct PaddedCounter
    {
        [FieldOffset(64)] public long Value;
    }

    internal long ServedUnderPressure => Volatile.Read(ref _servedUnderPressure.Value);

    private long _overloadWaitExtensions;

    /// <summary>Back-pressure waits, restarts included, that went on past their timeout because pages were being served (S1).</summary>
    internal long OverloadWaitExtensions => Interlocked.Read(ref _overloadWaitExtensions);

    /// <summary>
    /// The budget of a back-pressure wait that may outlast its timeout while the engine is slow, not stuck (S1 of the starvation policy,
    /// #1382): within its first timeout it always may; past it, one timeout at a time for as long as a page was served during the previous one, up to
    /// <see cref="TimeoutOptions.PageCacheOverloadWaitCeiling"/>. A whole timeout without a page served is a stall, and the wait gives up — the one
    /// case it should: nothing it could wait for is coming.
    /// </summary>
    internal struct OverloadBudget
    {
        private long _since;
        private long _periodStart;
        private long _servedMark;

        /// <summary>When the wait began, a <see cref="Stopwatch"/> timestamp; 0 before <see cref="Start"/>.</summary>
        internal readonly long Since => _since;

        internal readonly TimeSpan Waited => _since == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_since);

        /// <summary>Starts the budget's clock, once; <paramref name="served"/> is the engine's <see cref="ServedUnderPressure"/>.</summary>
        internal void Start(long served)
        {
            if (_since == 0)
            {
                _since = _periodStart = Stopwatch.GetTimestamp();
                _servedMark = served;
            }
        }

        /// <summary>Whether the wait may go on (see the type); <paramref name="extended"/> when it goes on past a timeout because pages were served.</summary>
        internal bool Allows(TimeSpan timeout, TimeSpan ceiling, long served, out bool extended)
        {
            extended = false;
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_since, now) >= (ceiling > timeout ? ceiling : timeout))
            {
                return false;
            }

            return Stopwatch.GetElapsedTime(_periodStart, now) < timeout || Extend(now, served, ref extended);
        }

        /// <summary>
        /// A wait whose own timeout has just run out (its clock is not this budget's): goes on only if a page was served since the last check, up to the
        /// ceiling — no grace period first, which a clock started later than the timeout's would have granted.
        /// </summary>
        internal bool AllowsPastTimeout(TimeSpan timeout, TimeSpan ceiling, long served)
        {
            var extended = false;
            var now = Stopwatch.GetTimestamp();
            return Stopwatch.GetElapsedTime(_since, now) < (ceiling > timeout ? ceiling : timeout) && Extend(now, served, ref extended);
        }

        private bool Extend(long now, long served, ref bool extended)
        {
            if (served == _servedMark)
            {
                return false;
            }

            _servedMark = served;
            _periodStart = now;
            extended = true;
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // S3 (#1382): the storage health state
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>How often the health state is re-evaluated at most; every evaluation is one interlocked compare on a timestamp.</summary>
    internal static TimeSpan HealthEvaluationInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Back-pressure lasting this long makes the cache <see cref="StorageHealthState.Overloaded"/>.</summary>
    internal static TimeSpan OverloadedAfter = TimeSpan.FromSeconds(1);

    /// <summary>Back-pressure with no page served for this long makes the cache <see cref="StorageHealthState.Starved"/>.</summary>
    internal static TimeSpan StarvedAfter = TimeSpan.FromSeconds(1);

    /// <summary>How long a lower state must hold before the state steps down to it: a burst does not flap the state.</summary>
    internal static TimeSpan CalmBeforeStepDown = TimeSpan.FromSeconds(1);

    private long _healthEvaluatedAt;
    private int _healthEvaluating;
    private long _healthLastServed;
    private long _healthLastServedAt;
    private long _healthPressureSince;
    private long _healthCalmSince;
    private long _healthStateSince;
    private readonly long[] _healthEntries = new long[4];

    /// <summary>
    /// The page cache's health (#1382): whether the work is going through at full speed, slowed by back-pressure, slowed for long, or not served at all.
    /// The engine does not fail the work for it: it waits while pages are served, and reports the state — in the log on every change, in the profiler's
    /// gauges and in the resource tree — so a workload too large for the cache and disk it was given is seen, not silently slowed.
    /// </summary>
    internal StorageHealthState StorageHealth => (StorageHealthState)Volatile.Read(ref _underPressure.Health);

    /// <summary>How many times the state entered <paramref name="state"/>.</summary>
    internal long HealthEntries(StorageHealthState state) => Interlocked.Read(ref _healthEntries[(int)state]);

    /// <summary>Raised on every change of <see cref="StorageHealth"/>, on the thread that saw it: old state, new state. Must not block.</summary>
    internal Action<StorageHealthState, StorageHealthState> StorageHealthChanged { get; set; }

    /// <summary>
    /// Re-evaluates <see cref="StorageHealth"/>, at most every <see cref="HealthEvaluationInterval"/>: from each back-pressure round, from allocations
    /// while the state is degraded, and from the checkpoint loop's wakes, so it steps down when the engine falls idle too. Escalates at once; steps down
    /// once the lower state has held for <see cref="CalmBeforeStepDown"/>.
    /// </summary>
    internal void EvaluateStorageHealth()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - Volatile.Read(ref _healthEvaluatedAt) < (long)(HealthEvaluationInterval.TotalSeconds * Stopwatch.Frequency)
            || Interlocked.CompareExchange(ref _healthEvaluating, 1, 0) != 0)
        {
            return;
        }

        // One evaluator at a time, however long a transition's log or callback takes: the flag admits one, and its release below publishes the plain
        // fields to the next (an acquire on its compare-exchange).
        try
        {
            EvaluateStorageHealthCore(now);
        }
        finally
        {
            Volatile.Write(ref _healthEvaluatedAt, now);
            Volatile.Write(ref _healthEvaluating, 0);
        }
    }

    private void EvaluateStorageHealthCore(long now)
    {
        // Pressure is the flag back-pressure rounds hold set, within PressureHold of the last round, not "a round since the last evaluation": evaluations
        // come from allocations too, between the rounds. Only an allocation clears the flag, so an engine that falls idle after a burst still reads it
        // set: past the hold it is not pressure, or "pressured, nothing served" would have called an idle engine Starved.
        // Starved is "nothing served for StarvedAfter while pressured": its clock runs only under pressure, so an idle stretch before it does not count.
        var served = ServedUnderPressure;
        var pressured = _underPressure.Value != 0 && now < Volatile.Read(ref _pressureClearAt);
        if (served != _healthLastServed || !pressured || _healthPressureSince == 0)
        {
            _healthLastServedAt = now;
        }

        _healthLastServed = served;
        if (!pressured)
        {
            _healthPressureSince = 0;
        }
        else if (_healthPressureSince == 0)
        {
            _healthPressureSince = now;
        }

        StorageHealthState target;
        if (!pressured)
        {
            target = StorageHealthState.Healthy;
        }
        else if (Stopwatch.GetElapsedTime(_healthLastServedAt, now) >= StarvedAfter)
        {
            target = StorageHealthState.Starved;
        }
        else if (Stopwatch.GetElapsedTime(_healthPressureSince, now) >= OverloadedAfter)
        {
            target = StorageHealthState.Overloaded;
        }
        else
        {
            target = StorageHealthState.Pressured;
        }

        var current = StorageHealth;
        if (target > current)
        {
            _healthCalmSince = 0;
            Transition(current, target, now);
        }
        else if (target < current)
        {
            if (_healthCalmSince == 0)
            {
                _healthCalmSince = now;
            }
            else if (Stopwatch.GetElapsedTime(_healthCalmSince, now) >= CalmBeforeStepDown)
            {
                _healthCalmSince = 0;
                Transition(current, target, now);
            }
        }
        else
        {
            _healthCalmSince = 0;
        }

        AdjustAdmission(StorageHealth);
    }

    private void Transition(StorageHealthState from, StorageHealthState to, long now)
    {
        Volatile.Write(ref _underPressure.Health, (int)to);
        Interlocked.Increment(ref _healthEntries[(int)to]);
        var inPrevious = _healthStateSince == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_healthStateSince, now);
        _healthStateSince = now;
        if (Logger != null)
        {
            if (to >= StorageHealthState.Overloaded)
            {
                LogStorageHealthDegraded(Logger, from, to,
                    _healthPressureSince == 0 ? 0 : Stopwatch.GetElapsedTime(_healthPressureSince, now).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(_healthLastServedAt, now).TotalMilliseconds, WritebackDebtPercent());
            }
            else
            {
                LogStorageHealthChanged(Logger, from, to, inPrevious.TotalMilliseconds);
            }
        }

        try
        {
            StorageHealthChanged?.Invoke(from, to);
        }
        catch
        {
            // A subscriber's fault must not fail the allocation that saw the change.
        }
    }

    /// <summary>Test seam: a back-pressure round, counted as the allocation loop counts one, then the state re-evaluated.</summary>
    internal void NoteBackpressureRoundForTests()
    {
        ++_metrics.BackpressureWaitCount;
        NotePressure();
    }

    /// <summary>Test seam: an allocation served, as one that found a slot is counted.</summary>
    internal void NoteServedForTests() => NoteAllocated();

    /// <summary>Test seam: forces the state, as an evaluation would set it.</summary>
    internal void SetStorageHealthForTests(StorageHealthState state) => Transition(StorageHealth, state, Stopwatch.GetTimestamp());
}
