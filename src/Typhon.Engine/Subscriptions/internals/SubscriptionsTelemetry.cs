using System;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>Which member of the replication track a duration belongs to. The order is the track's own, and the values index the per-tick record.</summary>
internal enum SubscriptionsStage
{
    /// <summary>S2a: the interest pass.</summary>
    Interest = 0,

    /// <summary>The blocks step and S1, the per-entity projection.</summary>
    Project = 1,

    /// <summary>The event drain.</summary>
    Events = 2,

    /// <summary>S2b: frame assembly.</summary>
    Frames = 3,

    /// <summary>The collapsed shape, which is all four inline in one dispatch.</summary>
    Collapsed = 4,
}

/// <summary>
/// What the replication track cost, per tick and per stage, with no sampling.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the track needs its own timing when every system already reports one.</b> The scheduler times systems; it does not know that these four are one
/// subsystem. "What does replication cost this server" is therefore a question nobody could answer without knowing which system names to add up — which is
/// exactly what the first cut of <c>typhon.subscriptions.track.p99</c> did, by summing every scheduler system whose name began with <c>Subscriptions</c>.
/// That works until an application declares a system with the same prefix, and it silently reports the wrong number rather than failing. This class is the
/// track measuring itself: a stage is timed because it IS a stage, not because of what it is called.
/// </para>
/// <para>
/// <b>The track's cost is a span, not a sum.</b> Stages run on many workers, so adding their wall times would count the same microsecond once per worker and
/// report a figure larger than the tick. What a tick actually spent on replication is the span from the first stage's first chunk to the last stage's last
/// chunk, and that is what <see cref="TrackMicroseconds"/> holds. The per-stage sums are kept alongside because they answer the other question — where the
/// time went — and the AC-1 harness reads both.
/// </para>
/// <para>
/// <b>Lock-free, allocation-free and unable to throw.</b> It is written from every worker on the tick path, where an allocation is a regression and an
/// exception aborts a tick. The ring is sized once at construction; every write is an <see cref="Interlocked"/> operation on a slot indexed by tick number;
/// a tick outside the ring's reach is dropped rather than resized into. Reads are taken by the stats encoder on the driver thread, one tick behind.
/// </para>
/// <para>
/// <b>Why the ring is not per-worker.</b> Contention here is four to five interlocked adds per stage per tick against slots that are already in the writing
/// core's cache — measured against a tick budget of hundreds of microseconds, it is noise. Per-worker accumulation would cost a cache line per worker per
/// stage and a reduction pass, to remove something that does not show.
/// </para>
/// </remarks>
internal sealed class SubscriptionsTelemetry
{
    /// <summary>How many stages a per-tick record holds.</summary>
    public const int StageCount = 5;

    /// <summary>How many ticks of history the ring keeps. A power of two so the index is a mask, and more than a second at any sane rate.</summary>
    public const int Depth = 256;

    private static readonly double TicksToMicroseconds = 1_000_000.0 / Stopwatch.Frequency;

    private readonly long[] _stageTicks = new long[Depth * StageCount];
    private readonly long[] _trackStart = new long[Depth];
    private readonly long[] _trackEnd = new long[Depth];
    private readonly long[] _tickNumber = new long[Depth];

    /// <summary>A timestamp for a stage to hand back to <see cref="NoteStage"/>.</summary>
    /// <returns>The raw stopwatch reading.</returns>
    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary>
    /// Opens a tick's record, discarding whatever the slot held from <see cref="Depth"/> ticks ago.
    /// </summary>
    /// <param name="tick">The tick about to run.</param>
    /// <remarks>
    /// Called on the tick driver before the track is dispatched, so no worker can be writing this slot. The tick number is stored last and read first, which
    /// is what lets a reader tell a fresh record from a stale one without a lock.
    /// </remarks>
    public void BeginTick(long tick)
    {
        var slot = (int)((ulong)tick & (Depth - 1));
        var at = slot * StageCount;
        for (var i = 0; i < StageCount; i++)
        {
            _stageTicks[at + i] = 0;
        }

        // Sentinels outside the value domain, not zero. A zero start is indistinguishable from "no chunk ran yet", and a timestamp is only never zero by
        // luck — the stopwatch's origin is arbitrary. Using the extremes makes the span logic a plain min/max with no special case in it.
        _trackStart[slot] = long.MaxValue;
        _trackEnd[slot] = long.MinValue;

        // Released last: a reader that sees this tick number is guaranteed to see the cleared slot behind it, so it can never add this tick's stage sums to
        // the ones left by the tick 256 before it.
        Volatile.Write(ref _tickNumber[slot], tick);
    }

    /// <summary>
    /// Records one chunk's worth of a stage.
    /// </summary>
    /// <param name="tick">The tick the chunk ran on.</param>
    /// <param name="stage">Which stage.</param>
    /// <param name="from">The reading <see cref="Now"/> gave before the work.</param>
    /// <param name="to">The reading after it.</param>
    /// <remarks>
    /// Called from every worker. The span ends are a CAS loop rather than an add, because the earliest start and the latest end are what bound the track —
    /// and both are monotone, so each loop runs once in the overwhelming majority of cases and never spins against a moving target.
    /// </remarks>
    public void NoteStage(long tick, SubscriptionsStage stage, long from, long to)
    {
        var slot = (int)((ulong)tick & (Depth - 1));
        if (Volatile.Read(ref _tickNumber[slot]) != tick)
        {
            // The record was recycled under us, which means this chunk outlived its tick by the ring's whole depth. Dropping it is right: the alternative is
            // adding a stale duration to a live tick's total, which is worse than a gap because it reads as a measurement.
            return;
        }

        Interlocked.Add(ref _stageTicks[(slot * StageCount) + (int)stage], to - from);
        Lower(ref _trackStart[slot], from);
        Raise(ref _trackEnd[slot], to);
    }

    /// <summary>What the whole track spent on a tick, as the span from its first chunk to its last.</summary>
    /// <param name="tick">The tick.</param>
    /// <returns>Microseconds, or zero when the tick is outside the ring or ran no stage.</returns>
    public double TrackMicroseconds(long tick)
    {
        var slot = (int)((ulong)tick & (Depth - 1));
        if (Volatile.Read(ref _tickNumber[slot]) != tick)
        {
            return 0;
        }

        var start = Volatile.Read(ref _trackStart[slot]);
        var end = Volatile.Read(ref _trackEnd[slot]);
        return start == long.MaxValue || end <= start ? 0 : (end - start) * TicksToMicroseconds;
    }

    /// <summary>What one stage spent on a tick, summed over its chunks.</summary>
    /// <param name="tick">The tick.</param>
    /// <param name="stage">The stage.</param>
    /// <returns>Microseconds of worker time, which for a parallel stage exceeds the wall span.</returns>
    public double StageMicroseconds(long tick, SubscriptionsStage stage)
    {
        var slot = (int)((ulong)tick & (Depth - 1));
        if (Volatile.Read(ref _tickNumber[slot]) != tick)
        {
            return 0;
        }

        return Volatile.Read(ref _stageTicks[(slot * StageCount) + (int)stage]) * TicksToMicroseconds;
    }

    /// <summary>Whether the ring still holds a record for a tick.</summary>
    /// <param name="tick">The tick.</param>
    /// <returns><see langword="true"/> when it has not been recycled.</returns>
    public bool Holds(long tick) => Volatile.Read(ref _tickNumber[(int)((ulong)tick & (Depth - 1))]) == tick;

    /// <summary>
    /// A percentile of the track's per-tick cost over the most recent ticks.
    /// </summary>
    /// <param name="newestTick">The newest tick to consider, normally the one that just ran.</param>
    /// <param name="window">How many ticks back to look; clamped to the ring's depth.</param>
    /// <param name="percentile">In [0, 1]; 0.5 is the median.</param>
    /// <param name="scratch">A caller-owned buffer of at least <paramref name="window"/> doubles, so this allocates nothing.</param>
    /// <returns>Microseconds, or zero when no tick in the window produced a measurement.</returns>
    /// <remarks>
    /// Nearest-rank on the ticks that actually ran a track, not on the whole window: a tick the gate turned the track off for contributes no sample, and
    /// counting it as a zero would drag every percentile towards zero in exactly the idle conditions where a spike matters most.
    /// </remarks>
    public double Percentile(long newestTick, int window, double percentile, Span<double> scratch)
    {
        var count = 0;
        var wanted = Math.Min(Math.Min(window, Depth), scratch.Length);
        for (var back = 0; back < wanted; back++)
        {
            var tick = newestTick - back;
            if (tick < 0)
            {
                break;
            }

            var value = TrackMicroseconds(tick);
            if (value > 0)
            {
                scratch[count++] = value;
            }
        }

        if (count == 0)
        {
            return 0;
        }

        var samples = scratch[..count];
        samples.Sort();
        var rank = (int)Math.Ceiling(percentile * count) - 1;
        return samples[Math.Clamp(rank, 0, count - 1)];
    }

    private static void Lower(ref long slot, long candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref slot);
            if (current <= candidate)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref slot, candidate, current) == current)
            {
                return;
            }
        }
    }

    private static void Raise(ref long slot, long candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref slot);
            if (current >= candidate)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref slot, candidate, current) == current)
            {
                return;
            }
        }
    }
}
