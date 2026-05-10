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
