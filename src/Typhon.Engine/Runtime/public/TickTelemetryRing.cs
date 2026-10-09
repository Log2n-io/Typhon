using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Pre-allocated circular buffer for tick telemetry. Zero allocation on <see cref="Record"/>.
/// Single writer (tick driver thread), multiple readers (diagnostics, metrics).
/// </summary>
/// <remarks>
/// Default capacity: 1024 entries (~17 seconds at 60Hz).
/// Memory: 1024 × (sizeof(TickTelemetry) + systemCount × sizeof(SystemTelemetry)).
/// For a 20-system DAG: ~1024 × (32 + 20 × 48) ≈ ~1 MB.
/// </remarks>
[PublicAPI]
public sealed class TickTelemetryRing
{
    private readonly TickTelemetry[] _ticks;
    private readonly SystemTelemetry[][] _systemMetrics;
    private readonly int _capacity;
    private readonly int _mask;
    private readonly int _systemCount;
    private long _head; // Next write position (monotonically increasing)

    /// <summary>
    /// Creates a new telemetry ring buffer.
    /// </summary>
    /// <param name="capacity">Must be a power of 2.</param>
    /// <param name="systemCount">Number of systems in the DAG.</param>
    public TickTelemetryRing(int capacity, int systemCount)
    {
        if (capacity < 1 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of 2.", nameof(capacity));
        }

        _capacity = capacity;
        _mask = capacity - 1;
        _systemCount = systemCount;
        _ticks = new TickTelemetry[capacity];
        _systemMetrics = new SystemTelemetry[capacity][];

        for (var i = 0; i < capacity; i++)
        {
            _systemMetrics[i] = new SystemTelemetry[systemCount];
        }
    }

    /// <summary>Number of ticks recorded so far (may exceed capacity — only last <see cref="Capacity"/> are retained).</summary>
    public long TotalTicksRecorded => _head;

    /// <summary>Ring buffer capacity.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Tick number of the oldest available entry, or -1 if no ticks recorded yet.
    /// </summary>
    public long OldestAvailableTick
    {
        get
        {
            if (_head == 0)
            {
                return -1;
            }

            return _head > _capacity ? _head - _capacity : 0;
        }
    }

    /// <summary>
    /// Tick number of the newest recorded entry, or -1 if no ticks recorded yet.
    /// </summary>
    public long NewestTick => _head > 0 ? _head - 1 : -1;

    /// <summary>
    /// The readable tick range at or after <paramref name="fromInclusive"/>, or <see langword="false"/> when there is none.
    /// </summary>
    /// <param name="fromInclusive">
    /// The oldest tick the caller wants. Clamped up to <see cref="OldestAvailableTick"/>; the caller owns what "the window" means, so this takes an absolute
    /// tick rather than a width and never reinterprets one.
    /// </param>
    /// <param name="first">The oldest readable tick in the range. Undefined when this returns <see langword="false"/>.</param>
    /// <param name="last">The newest readable tick in the range — <see cref="NewestTick"/>. Undefined when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <c>for (var t = first; t &lt;= last; t++)</c> is safe to pass to <see cref="GetTick"/>.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every window pass over this ring should start here, because the hand-written form has a trap and three shipped copies of it fell in.</b>
    /// <see cref="OldestAvailableTick"/> and <see cref="NewestTick"/> both report <c>-1</c> while the ring is empty, so the natural
    /// <c>for (var t = Math.Max(oldest, tick - window); t &lt;= newest; t++)</c> evaluates to <c>t = -1; -1 &lt;= -1</c> and calls
    /// <see cref="GetTick"/>(-1), which throws. Empty is not an edge case: the ring is written at the END of a tick, so it is the state every consumer sees
    /// on tick 0.
    /// </para>
    /// <para>
    /// <b>That throw is unusually destructive on a tick-path consumer</b> and is why this exists rather than a note in each caller.
    /// <c>StatsEncoder</c>'s three passes are reached from <c>SubscriptionsContext.Reset</c>, which runs BEFORE the ring is written — so the throw stopped the
    /// recording that would have made the next tick's call legal, and the condition sustained itself for every tick of the run. It surfaced as 508 ticks and
    /// not one telemetry record emitted, with every gate and null check passing; not as an exception anyone saw.
    /// </para>
    /// </remarks>
    public bool TryGetRange(long fromInclusive, out long first, out long last)
    {
        last = NewestTick;
        first = Math.Max(OldestAvailableTick, fromInclusive);
        return last >= 0 && first <= last;
    }

    /// <summary>
    /// One tick's telemetry, or <see langword="false"/> when the ring no longer holds it. The non-throwing peer of <see cref="GetTick"/>.
    /// </summary>
    /// <param name="tickNumber">The tick to read.</param>
    /// <param name="tick">The tick's telemetry. <c>default</c> when this returns <see langword="false"/>.</param>
    /// <returns><see langword="false"/> when <paramref name="tickNumber"/> has been evicted, or has not been recorded.</returns>
    /// <remarks>
    /// <para>
    /// <b>A range from <see cref="TryGetRange"/> is not a promise that every tick in it still exists when you get there.</b> The clamp is a snapshot and the
    /// validation is live: the tick driver is a concurrent writer, so on a window as wide as <see cref="Capacity"/> the oldest end clamps EXACTLY to
    /// <see cref="OldestAvailableTick"/>, and a single tick recorded between resolving the range and reading its first element evicts it. <see cref="GetTick"/>
    /// then throws — out of whatever public method was reading, which for <c>TyphonRuntime.ReadStats</c> means an operator's stats call fails rather than
    /// returning a slightly short window.
    /// </para>
    /// <para>
    /// A reader walking a range wants that skipped, not raised: losing the oldest sample of a percentile is the tearing such a reader already accepts by
    /// reading a live ring at all, and it is strictly less wrong than no answer. <see cref="GetTick"/> stays for a caller naming one tick it believes is
    /// present, where a throw is the right answer to a bug.
    /// </para>
    /// </remarks>
    public bool TryGetTick(long tickNumber, out TickTelemetry tick)
    {
        if (!Holds(tickNumber))
        {
            tick = default;
            return false;
        }

        tick = _ticks[(int)(tickNumber & _mask)];
        return true;
    }

    /// <summary>
    /// One tick's per-system telemetry, or <see langword="false"/> when the ring no longer holds it. The non-throwing peer of <see cref="GetSystemMetrics"/>.
    /// </summary>
    /// <param name="tickNumber">The tick to read.</param>
    /// <param name="systems">The tick's per-system metrics. Empty when this returns <see langword="false"/>.</param>
    /// <returns><see langword="false"/> when <paramref name="tickNumber"/> has been evicted, or has not been recorded.</returns>
    /// <remarks>Paired with <see cref="TryGetTick"/> for the same reason — see its remarks. A walk that guards one and not the other still throws.</remarks>
    public bool TryGetSystemMetrics(long tickNumber, out ReadOnlySpan<SystemTelemetry> systems)
    {
        if (!Holds(tickNumber))
        {
            systems = default;
            return false;
        }

        systems = _systemMetrics[(int)(tickNumber & _mask)].AsSpan(0, _systemCount);
        return true;
    }

    /// <summary>Whether the ring currently holds <paramref name="tickNumber"/>. The predicate <see cref="ValidateTickNumber"/> throws on.</summary>
    private bool Holds(long tickNumber) => _head != 0 && tickNumber >= OldestAvailableTick && tickNumber < _head;

    /// <summary>
    /// Records a tick's telemetry data into the ring buffer. Called at the end of each tick by the scheduler.
    /// Zero allocation — copies data into pre-allocated slots.
    /// </summary>
    /// <param name="tick">Tick-level telemetry.</param>
    /// <param name="systems">Per-system telemetry for this tick. Length must equal systemCount.</param>
    public void Record(in TickTelemetry tick, ReadOnlySpan<SystemTelemetry> systems)
    {
        var slot = (int)(_head & _mask);
        _ticks[slot] = tick;
        systems.CopyTo(_systemMetrics[slot]);
        _head++;
    }

    /// <summary>
    /// Returns the tick telemetry for a given tick number.
    /// </summary>
    /// <param name="tickNumber">Absolute tick number.</param>
    /// <returns>Reference to the stored telemetry. Only valid if the tick is still in the buffer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Tick is not in the buffer (too old or not yet recorded).</exception>
    public ref readonly TickTelemetry GetTick(long tickNumber)
    {
        ValidateTickNumber(tickNumber);
        return ref _ticks[(int)(tickNumber & _mask)];
    }

    /// <summary>
    /// Returns the per-system telemetry for a given tick number.
    /// </summary>
    /// <param name="tickNumber">Absolute tick number.</param>
    /// <returns>Span over the system metrics array for this tick.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Tick is not in the buffer.</exception>
    public ReadOnlySpan<SystemTelemetry> GetSystemMetrics(long tickNumber)
    {
        ValidateTickNumber(tickNumber);
        return _systemMetrics[(int)(tickNumber & _mask)].AsSpan(0, _systemCount);
    }

    private void ValidateTickNumber(long tickNumber)
    {
        if (_head == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tickNumber), "No ticks have been recorded yet.");
        }

        var oldest = OldestAvailableTick;
        if (tickNumber < oldest || tickNumber >= _head)
        {
            throw new ArgumentOutOfRangeException(nameof(tickNumber), $"Tick {tickNumber} is not in the buffer. Available range: [{oldest}, {_head - 1}].");
        }
    }
}
