using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// In-tick idle: what a worker does inside a dispatch when no system is ready for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The legacy policy spins, then yields forever.</b> <c>Thread.Yield</c> gives the core up only when another thread is ready on it, and otherwise
/// returns at once — so an idle worker on a quiet core is a tight loop at 100 %, and on a busy one it hands the core away for up to a scheduling
/// quantum. Measured on the SWG demo at 1 000 sessions: about a third of all worker time was this loop (≈ 10 cores), which starved the thread pool that
/// runs the sends and ASP.NET Core; and the yield is what spread a stage's chunk starts over milliseconds (#906).
/// </para>
/// <para>
/// <b>The parking policy</b> (<see cref="WorkerHotSpinners"/> ≥ 0) keeps a few workers spinning — with <c>PAUSE</c>, never a yield, so a system that
/// becomes ready is taken in well under a microsecond — and parks every other idle worker on its own event after <see cref="WorkerParkAfterUs"/>. Whoever
/// publishes work wakes as many parked workers as it has chunks for, and the completion that ends a track wakes them all so they leave the dispatch.
/// A parked worker costs nothing and wakes in tens of microseconds (the per-worker event, P3: 17 µs first, 85 µs median, 146 µs last of 32).
/// </para>
/// <para>
/// <b>No lost wake, by the order of two fences.</b> A worker announces it is parked (state <see cref="IdleParked"/>) and only then re-checks for work,
/// with a full fence between the store and the loads. A publisher stores the ready flag and claim word first and scans for parked workers only after a
/// full fence. So either the worker's re-check sees the work, or the publisher's scan sees the worker. A wake is claimed by a compare-exchange of the
/// state from parked to busy, so exactly one side Sets and exactly one Wait consumes it; the event is Reset only by its worker, after the Set it owns.
/// The park wait still has a backstop, as a liveness net only.
/// </para>
/// </remarks>
public sealed partial class DagScheduler
{
    private const int IdleBusy = 0;
    private const int IdleSpinning = 1;
    private const int IdleParked = 2;

    /// <summary>
    /// How many idle workers keep spinning inside a dispatch while the others park. A negative value keeps the legacy spin-then-yield loop. Default 0.
    /// </summary>
    /// <remarks>
    /// A spinner starts a newly ready system without a kernel wake, which is what it is for — and measured, it does not pay: on the SWG demo at
    /// 1 000 sessions, two spinners made the tick 3.9 % longer at P50 than none (5 of 6 interleaved pairs), because a spinning worker takes execution
    /// resources from its busy SMT sibling and holds the core active, while the wake it saves is paid off the critical path whenever the publisher's own
    /// chunk covers it. Kept as a knob for a box where wakes are dearer than cores.
    /// </remarks>
    public static int WorkerHotSpinners { get; set; }

    /// <summary>
    /// How long an idle worker spins before it may park, in microseconds. Default 10: long enough for a successor that becomes ready within microseconds
    /// to be taken without a park and a wake; measured, 10 µs beat 50 µs (tick P99 8 % shorter) and matched 0.
    /// </summary>
    public static int WorkerParkAfterUs { get; set; } = 10;

    /// <summary>Whether worker idle time is measured (<see cref="WorkerIdle"/>). A measurement switch: a few timestamps per idle spell.</summary>
    public static bool MeasureWorkerIdle { get; set; }

    /// <summary>Test seam: how long a parked worker waits before re-checking by itself. A liveness net, not a way to be woken.</summary>
    internal TimeSpan ParkBackstop = TimeSpan.FromMilliseconds(2);

    /// <summary>This scheduler's <see cref="WorkerHotSpinners"/>, taken at construction so a test can set it without reaching every other scheduler.</summary>
    internal int HotSpinners;

    /// <summary>This scheduler's <see cref="WorkerParkAfterUs"/>, taken at construction.</summary>
    internal int ParkAfterUs;

    /// <summary>This scheduler's <see cref="MeasureWorkerIdle"/>, taken at construction.</summary>
    internal bool MeasureIdle;

    // Per worker: busy / spinning / parked, each on its own cache line, since a worker writes its own on every idle spell and publishers scan them all.
    private CacheLinePaddedInt[] _idleState;
    private ManualResetEventSlim[] _parkWake;

    // A hint for publishers: zero means nobody is parked and the scan can be skipped. Read after the publisher's fence.
    private CacheLinePaddedInt _parkedCount;

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct WorkerIdleCounters
    {
        [FieldOffset(0)] public long InDispatchTicks;
        [FieldOffset(8)] public long IdleTicks;
        [FieldOffset(16)] public long ParkedTicks;
        [FieldOffset(24)] public long Parks;
        [FieldOffset(32)] public long Backstops;
        [FieldOffset(40)] public long Spells;
    }

    private WorkerIdleCounters[] _idleCounters;
    private long _parkWakes;

    // Running totals of the worker-utilization telemetry, written by the timer thread at tick end.
    private long _utilActiveTicks;
    private long _utilTickTicks;
    private long _utilTicks;

    /// <summary>
    /// Worker utilization since start, when <c>Scheduler:Gauges:WorkerUtilization</c> telemetry is on: summed worker time inside system and chunk bodies,
    /// summed tick wall time, and the tick count. Active ÷ (workers × wall) is the share of the pool doing useful work.
    /// </summary>
    public (double ActiveMs, double TickWallMs, long Ticks, int Workers) WorkerUtilization
    {
        get
        {
            var ms = 1000d / Stopwatch.Frequency;
            return (Volatile.Read(ref _utilActiveTicks) * ms, Volatile.Read(ref _utilTickTicks) * ms, Volatile.Read(ref _utilTicks), _workerCount);
        }
    }

    private void InitIdle(int workerCount)
    {
        _idleState = new CacheLinePaddedInt[workerCount];
        _parkWake = new ManualResetEventSlim[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            _parkWake[i] = new ManualResetEventSlim(false, spinCount: 0);
        }

        _idleCounters = new WorkerIdleCounters[workerCount];
        HotSpinners = WorkerHotSpinners;
        ParkAfterUs = WorkerParkAfterUs;
        MeasureIdle = MeasureWorkerIdle;
    }

    /// <summary>
    /// Worker time inside dispatches since start, while <see cref="MeasureWorkerIdle"/> is on: total, idle (spinning or parked), parked, and the counts of
    /// parks, backstop expiries, idle spells and wakes publishers issued. Summed over workers, in milliseconds.
    /// </summary>
    public (double InDispatchMs, double IdleMs, double ParkedMs, long Parks, long Backstops, long Spells, long Wakes) WorkerIdle
    {
        get
        {
            long inDispatch = 0, idle = 0, parked = 0, parks = 0, backstops = 0, spells = 0;
            for (var i = 0; i < _idleCounters.Length; i++)
            {
                ref var c = ref _idleCounters[i];
                inDispatch += Volatile.Read(ref c.InDispatchTicks);
                idle += Volatile.Read(ref c.IdleTicks);
                parked += Volatile.Read(ref c.ParkedTicks);
                parks += Volatile.Read(ref c.Parks);
                backstops += Volatile.Read(ref c.Backstops);
                spells += Volatile.Read(ref c.Spells);
            }

            var ms = 1000d / Stopwatch.Frequency;
            return (inDispatch * ms, idle * ms, parked * ms, parks, backstops, spells, Interlocked.Read(ref _parkWakes));
        }
    }

    /// <summary>Accumulates one finished idle spell into the worker's own counters.</summary>
    private void NoteIdleSpell(int workerId, long spellTicks, long parkedTicks)
    {
        ref var c = ref _idleCounters[workerId];
        c.IdleTicks += spellTicks;
        c.ParkedTicks += parkedTicks;
        c.Spells++;
    }

    /// <summary>How many workers other than <paramref name="self"/> are spinning idle right now.</summary>
    private int CountSpinning(int self)
    {
        var n = 0;
        for (var i = 0; i < _idleState.Length; i++)
        {
            if (i != self && Volatile.Read(ref _idleState[i].Value) == IdleSpinning)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// Parks an idle worker until a publisher wakes it, the dispatch ends, or the backstop expires.
    /// </summary>
    /// <param name="workerId">The worker.</param>
    /// <returns>The Stopwatch ticks spent parked.</returns>
    private long ParkIdleWorker(int workerId)
    {
        ref var state = ref _idleState[workerId].Value;
        Volatile.Write(ref state, IdleParked);
        Interlocked.Increment(ref _parkedCount.Value);

        // The full fence above (the increment) orders the parked store before these loads; the publisher fences between its work store and its scan.
        if (FindReadySystem() >= 0 || _systemsRemaining.Value == 0 || _tickInProgress == 0 || Volatile.Read(ref _workerShutdown) != 0)
        {
            if (Interlocked.CompareExchange(ref state, IdleSpinning, IdleParked) == IdleParked)
            {
                Interlocked.Decrement(ref _parkedCount.Value);
                return 0;
            }

            // A publisher claimed this worker between the store and the re-check, and its Set is on the way: consume it so the event is clear.
            _parkWake[workerId].Wait();
            _parkWake[workerId].Reset();
            Volatile.Write(ref state, IdleSpinning);
            return 0;
        }

        var from = Stopwatch.GetTimestamp();
        if (MeasureIdle)
        {
            _idleCounters[workerId].Parks++;
        }

        var woken = _parkWake[workerId].Wait(ParkBackstop);
        if (!woken)
        {
            if (Interlocked.CompareExchange(ref state, IdleSpinning, IdleParked) == IdleParked)
            {
                // The backstop, with nobody claiming this worker: nothing to consume.
                Interlocked.Decrement(ref _parkedCount.Value);
                if (MeasureIdle)
                {
                    _idleCounters[workerId].Backstops++;
                }

                return Stopwatch.GetTimestamp() - from;
            }

            // Claimed as the backstop expired: its Set is on the way.
            _parkWake[workerId].Wait();
        }

        _parkWake[workerId].Reset();
        Volatile.Write(ref state, IdleSpinning);
        return Stopwatch.GetTimestamp() - from;
    }

    /// <summary>
    /// Wakes up to <paramref name="count"/> parked workers, after work has been published.
    /// </summary>
    /// <param name="count">How many workers the published work can use; <see cref="int.MaxValue"/> for all.</param>
    /// <param name="netOfSpinners">Whether the workers spinning right now count against <paramref name="count"/>: they will take the work themselves.</param>
    /// <remarks>
    /// The fence first: the caller's stores of the ready flag and claim word must be visible before this reads any worker's state, or a worker parking
    /// at the same moment could miss the work while this misses the worker. A no-op under the legacy policy, where nobody parks.
    /// </remarks>
    private void WakeParked(int count, bool netOfSpinners = false)
    {
        if (count <= 0 || HotSpinners < 0)
        {
            return;
        }

        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _parkedCount.Value) == 0)
        {
            return;
        }

        if (netOfSpinners)
        {
            // The spinners actually spinning, not the configured number: when they are busy, the work needs the parked workers instead.
            count -= CountSpinning(-1);
            if (count <= 0)
            {
                return;
            }
        }

        for (var i = 0; i < _idleState.Length && count > 0; i++)
        {
            if (Volatile.Read(ref _idleState[i].Value) == IdleParked
                && Interlocked.CompareExchange(ref _idleState[i].Value, IdleBusy, IdleParked) == IdleParked)
            {
                Interlocked.Decrement(ref _parkedCount.Value);
                _parkWake[i].Set();
                count--;
                if (MeasureIdle)
                {
                    Interlocked.Increment(ref _parkWakes);
                }
            }
        }
    }

    /// <summary>Wakes parked workers for a multi-chunk dispatch: one per chunk beyond what the spinners and the publishing worker take themselves.</summary>
    /// <param name="totalChunks">The dispatch's chunk count.</param>
    private void WakeForChunks(int totalChunks) => WakeParked(totalChunks - 1, netOfSpinners: true);
}
