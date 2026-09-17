using System;

namespace Typhon.Client;

/// <summary>How a <see cref="Clock"/> is tuned. Every value has the default the TypeScript SDK's <c>ClockOptions</c> applies (05-sdks § 1).</summary>
public sealed class ClockOptions
{
    /// <summary>The server's tick period in milliseconds, from <c>WELCOME</c>'s catalog. Required, and must be positive.</summary>
    public double TickPeriodMs { get; init; }

    /// <summary>The render delay applied before enough frames have arrived to measure jitter, clamped into the bounds below.</summary>
    public double InitialDelayMs { get; init; } = 200;

    /// <summary>The smallest adaptive render delay.</summary>
    public double MinDelayMs { get; init; } = 150;

    /// <summary>The largest adaptive render delay; a store's segment ring must span it.</summary>
    public double MaxDelayMs { get; init; } = 300;

    /// <summary>The window over which the offset and the jitter are measured, in milliseconds.</summary>
    public double WindowMs { get; init; } = 2000;

    /// <summary>The largest speed-up or slow-down applied to converge on the target time; 0.05 is ±5 %.</summary>
    public double MaxRateAdjust { get; init; } = 0.05;

    /// <summary>An error at least this large jumps render time forward, or holds it when it is that far ahead.</summary>
    public double SnapMs { get; init; } = 1000;

    /// <summary>
    /// How far render time may run past the newest frame before it holds. The default is longer than the 500 ms keepalive interval, so a quiet view whose
    /// entities all move on long straight segments never stutters.
    /// </summary>
    public double MaxExtrapolationMs { get; init; } = 1000;
}

/// <summary>
/// Maps server ticks to local time and produces the render time motion is evaluated at (05-sdks § 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Server timeline.</b> Tick → server milliseconds is piecewise linear: one piece per tick period, since the server stretches ticks under overload and says
/// so (the <c>PERIOD</c> flag). Render time runs on that timeline, in milliseconds, and is converted to a tick only at the end, through the piece it falls in —
/// so time before a period change keeps the old period and time after it the new one.
/// </para>
/// <para>
/// <b>Offset.</b> Each frame yields <c>offset = recvMs − serverMs(tick)</c>. The smallest offset over a window belongs to the least-delayed frame and is the
/// best estimate of the true offset; delay only ever adds.
/// </para>
/// <para>
/// <b>Render delay.</b> <c>period + p95(offset − minOffset)</c>, clamped into <see cref="ClockOptions.MinDelayMs"/>…<see cref="ClockOptions.MaxDelayMs"/>: one
/// tick plus the jitter, so the next segment has almost always arrived before render time reaches its <c>t0</c>.
/// </para>
/// <para>
/// <b>Convergence.</b> Render time advances with local time, corrected by at most ±<see cref="ClockOptions.MaxRateAdjust"/> toward <c>serverNow − delay</c>. It
/// jumps forward only past <see cref="ClockOptions.SnapMs"/>, and never jumps backward: when it is that far ahead (the first frame after a stall looks late) it
/// holds until the target catches up.
/// </para>
/// <para>
/// <b>Output.</b> <see cref="RenderTick"/> (an integer) plus <see cref="RenderFrac"/> in [0, 1): motion evaluation subtracts integer ticks first (see
/// <see cref="MotionEvaluator"/>). The milliseconds are <see cref="double"/> and stay sub-microsecond exact for centuries.
/// </para>
/// <para>
/// A port of the TypeScript SDK's <c>clock/clock.ts</c>, arithmetic for arithmetic, so both SDKs land on the same render tick from the same frame arrivals.
/// Nothing here allocates after construction.
/// </para>
/// </remarks>
public sealed class Clock
{
    private const int SampleCapacity = 128;
    private const int MinSamplesForJitter = 4;
    private const int PeriodHistory = 8;

    private readonly double _initialPeriodMs;
    private readonly double _initialDelayMs;
    private readonly double _minDelayMs;
    private readonly double _delayCeilingMs;
    private readonly double _windowMs;
    private readonly double _maxRateAdjust;
    private readonly double _snapMs;
    private readonly double _maxExtrapolationMs;

    // Pieces of the tick → server-time map, oldest first, in [0, _pieceCount).
    private readonly double[] _pieceTick = new double[PeriodHistory];
    private readonly double[] _pieceMs = new double[PeriodHistory];
    private readonly double[] _piecePeriod = new double[PeriodHistory];

    private readonly double[] _sampleRecvMs = new double[SampleCapacity];
    private readonly double[] _sampleOffset = new double[SampleCapacity];
    private readonly double[] _scratch = new double[SampleCapacity];

    private double _delayMs;
    private double _renderMs;
    private bool _started;
    private double _lastUpdateMs;
    private long _newestTick = -1;
    private int _pieceCount;
    private double _pendingPeriodMs;
    private int _sampleStart;
    private int _sampleCount;
    private double _minOffsetMs;

    /// <summary>Creates a clock.</summary>
    /// <param name="options">The tuning; only <see cref="ClockOptions.TickPeriodMs"/> has no default.</param>
    /// <exception cref="ArgumentOutOfRangeException">The tick period is not positive.</exception>
    public Clock(ClockOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!(options.TickPeriodMs > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.TickPeriodMs, "the tick period must be positive");
        }

        _initialPeriodMs = options.TickPeriodMs;
        _pendingPeriodMs = options.TickPeriodMs;
        _minDelayMs = options.MinDelayMs;
        _delayCeilingMs = options.MaxDelayMs;
        _initialDelayMs = Clamp(options.InitialDelayMs, _minDelayMs, _delayCeilingMs);
        _delayMs = _initialDelayMs;
        _windowMs = options.WindowMs;
        _maxRateAdjust = options.MaxRateAdjust;
        _snapMs = options.SnapMs;
        _maxExtrapolationMs = options.MaxExtrapolationMs;
    }

    /// <summary>Render time's integer tick, what <see cref="MotionEvaluator"/> takes.</summary>
    public long RenderTick { get; private set; }

    /// <summary>The fraction of a tick past <see cref="RenderTick"/>, in [0, 1).</summary>
    public double RenderFrac { get; private set; }

    /// <summary>The largest render delay this clock applies, in milliseconds: a store's segment ring must span it.</summary>
    public double MaxDelayMs => _delayCeilingMs;

    /// <summary>The current render delay in milliseconds.</summary>
    public double RenderDelayMs => _delayMs;

    /// <summary>The current tick period in milliseconds — the newest piece's, which <c>PERIOD</c> changes.</summary>
    public double TickPeriodMs => _pieceCount > 0 ? _piecePeriod[_pieceCount - 1] : _pendingPeriodMs;

    /// <summary>The newest tick received, or −1.</summary>
    public long LatestTick => _newestTick;

    /// <summary>The estimated offset between local time and the server timeline, in milliseconds: the smallest over the window.</summary>
    public double OffsetMs => _minOffsetMs;

    /// <summary>Render time as a single value, for display only: evaluation takes <see cref="RenderTick"/> and <see cref="RenderFrac"/>.</summary>
    public double RenderTime => RenderTick + RenderFrac;

    /// <summary>Records a frame. Frames must be passed in arrival order.</summary>
    /// <param name="tick">The frame's tick.</param>
    /// <param name="recvMs">The local time it was received at, in milliseconds.</param>
    public void OnFrame(long tick, double recvMs)
    {
        if (_pieceCount == 0)
        {
            _pieceTick[0] = tick;
            _pieceMs[0] = 0;
            _piecePeriod[0] = _pendingPeriodMs;
            _pieceCount = 1;
        }

        if (tick > _newestTick)
        {
            _newestTick = tick;
        }

        PushSample(recvMs, recvMs - ServerMsOf(tick));
    }

    /// <summary>The server changed its tick period from <paramref name="tick"/> on (the <c>PERIOD</c> flag).</summary>
    /// <param name="tick">The first tick of the new period.</param>
    /// <param name="periodMs">The new period in milliseconds.</param>
    /// <exception cref="ArgumentOutOfRangeException">The period is not positive.</exception>
    public void OnPeriodChange(long tick, double periodMs)
    {
        if (!(periodMs > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(periodMs), periodMs, "the tick period must be positive");
        }

        if (_pieceCount == 0)
        {
            _pendingPeriodMs = periodMs;
            return;
        }

        var ms = ServerMsOf(tick);
        if (_pieceCount == PeriodHistory)
        {
            _pieceTick.AsSpan(1).CopyTo(_pieceTick);
            _pieceMs.AsSpan(1).CopyTo(_pieceMs);
            _piecePeriod.AsSpan(1).CopyTo(_piecePeriod);
            _pieceCount--;
        }

        _pieceTick[_pieceCount] = tick;
        _pieceMs[_pieceCount] = ms;
        _piecePeriod[_pieceCount] = periodMs;
        _pieceCount++;
    }

    /// <summary>Forgets every frame, sample and period change (a reconnection): the next frame re-anchors and the next update snaps.</summary>
    public void Reset()
    {
        _newestTick = -1;
        _started = false;
        _sampleCount = 0;
        _sampleStart = 0;
        _minOffsetMs = 0;
        _delayMs = _initialDelayMs;
        _pendingPeriodMs = _initialPeriodMs;
        _pieceCount = 0;
        _renderMs = 0;
        RenderTick = 0;
        RenderFrac = 0;
    }

    /// <summary>Advances render time to local time <paramref name="nowMs"/>. Call once per rendered frame.</summary>
    /// <param name="nowMs">Local time in milliseconds.</param>
    public void Update(double nowMs)
    {
        if (_pieceCount == 0 || _sampleCount == 0)
        {
            return;
        }

        var target = nowMs - _minOffsetMs - _delayMs;
        if (!_started)
        {
            _renderMs = target;
            _started = true;
        }
        else
        {
            var elapsed = Math.Max(0, nowMs - _lastUpdateMs);
            var predicted = _renderMs + elapsed;
            var error = target - predicted;
            if (error >= _snapMs)
            {
                _renderMs = target;
            }
            else if (error > -_snapMs)
            {
                var correction = Clamp(error / TickPeriodMs * 0.5, -_maxRateAdjust, _maxRateAdjust);
                _renderMs = predicted + (elapsed * correction);
            }

            // Otherwise render time is far ahead of the target: hold.
        }

        _lastUpdateMs = nowMs;
        var limit = ServerMsOf(_newestTick) + _maxExtrapolationMs;
        if (_renderMs > limit)
        {
            _renderMs = limit;
        }

        ToTick(_renderMs);
    }

    private static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;

    private double ServerMsOf(long tick)
    {
        var p = _pieceCount - 1;
        while (p > 0 && tick < _pieceTick[p])
        {
            p--;
        }

        return _pieceMs[p] + ((tick - _pieceTick[p]) * _piecePeriod[p]);
    }

    private void ToTick(double ms)
    {
        var p = _pieceCount - 1;
        while (p > 0 && ms < _pieceMs[p])
        {
            p--;
        }

        var rel = (ms - _pieceMs[p]) / _piecePeriod[p];
        var whole = Math.Floor(rel);
        RenderTick = (long)(_pieceTick[p] + whole);
        RenderFrac = rel - whole;
    }

    private void PushSample(double recvMs, double offset)
    {
        var index = (_sampleStart + _sampleCount) % SampleCapacity;
        _sampleRecvMs[index] = recvMs;
        _sampleOffset[index] = offset;
        if (_sampleCount < SampleCapacity)
        {
            _sampleCount++;
        }
        else
        {
            _sampleStart = (_sampleStart + 1) % SampleCapacity;
        }

        while (_sampleCount > 1 && recvMs - _sampleRecvMs[_sampleStart] > _windowMs)
        {
            _sampleStart = (_sampleStart + 1) % SampleCapacity;
            _sampleCount--;
        }

        var min = double.PositiveInfinity;
        for (var i = 0; i < _sampleCount; i++)
        {
            var o = _sampleOffset[(_sampleStart + i) % SampleCapacity];
            if (o < min)
            {
                min = o;
            }
        }

        _minOffsetMs = min;
        var n = _sampleCount;
        if (n < MinSamplesForJitter)
        {
            return;
        }

        // Insertion sort of the jitter samples into the scratch buffer: n ≤ 128, no allocation.
        var s = _scratch;
        for (var i = 0; i < n; i++)
        {
            var v = _sampleOffset[(_sampleStart + i) % SampleCapacity] - min;
            var j = i;
            while (j > 0 && s[j - 1] > v)
            {
                s[j] = s[j - 1];
                j--;
            }

            s[j] = v;
        }

        // Nearest-rank 95th percentile.
        var p95 = s[(int)Math.Ceiling(n * 0.95) - 1];
        _delayMs = Clamp(TickPeriodMs + p95, _minDelayMs, _delayCeilingMs);
    }
}
