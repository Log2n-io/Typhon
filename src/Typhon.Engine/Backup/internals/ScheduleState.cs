using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// When the next scheduled point is due (plan 1416 DL-18/19/20, BKP-15): decided on a MONOTONIC clock, anchored once at start from the age of the newest
/// point of the engine's timeline and re-anchored at every success, so a wall clock set back a day delays a point by at most one interval and a restart
/// neither doubles nor skips one. Failures back off. Pure; the scheduler serialises its calls.
/// </summary>
internal sealed class ScheduleState
{
    /// <summary>The longest first retry: a minute, or a quarter of the interval when that is shorter.</summary>
    public static readonly TimeSpan MaxBaseRetry = TimeSpan.FromSeconds(60);

    private readonly long _interval;
    private readonly long _baseRetry;
    private readonly long _maxRetry;
    private readonly long _frequency;
    private bool _anchored;
    private long _anchor;
    private long _retryAt;
    private int _failures;

    /// <param name="interval">The schedule; zero = none (nothing is ever due, and nothing is retried automatically).</param>
    /// <param name="frequency">Monotonic ticks per second (<see cref="System.Diagnostics.Stopwatch.Frequency"/>).</param>
    public ScheduleState(TimeSpan interval, long frequency)
    {
        _frequency = frequency;
        _interval = Ticks(interval);
        _maxRetry = _interval / 4;
        _baseRetry = Math.Min(Ticks(MaxBaseRetry), _maxRetry);
    }

    /// <summary>
    /// Anchors the schedule at start: the newest point of this timeline is <c>utcNow − lastSuccessUtc</c> old (clamped to [0, interval]: a clock that jumped
    /// back reads as just taken, one that jumped forward as due now). No point: due at once (after the scheduler's grace).
    /// </summary>
    public void Initialize(DateTime utcNow, long monoNow, DateTime lastSuccessUtc)
    {
        _retryAt = 0;
        _failures = 0;
        if (lastSuccessUtc == DateTime.MinValue)
        {
            _anchored = false;
            return;
        }

        var age = Math.Clamp(Ticks(utcNow - lastSuccessUtc), 0, _interval);
        _anchor = monoNow - age;
        _anchored = true;
    }

    /// <summary>Whether a scheduled point is due at <paramref name="monoNow"/>.</summary>
    public bool IsDue(long monoNow) => _interval > 0 && monoNow >= DueAt();

    /// <summary>The monotonic instant the next point is due; <see cref="long.MaxValue"/> with no schedule.</summary>
    public long DueAt()
    {
        if (_interval == 0)
        {
            return long.MaxValue;
        }

        if (_retryAt != 0)
        {
            return _retryAt;
        }

        return _anchored ? _anchor + _interval : long.MinValue;
    }

    /// <summary>A point was published and verified: the next is one interval from now.</summary>
    public void Succeeded(long monoNow)
    {
        _anchor = monoNow;
        _anchored = true;
        _retryAt = 0;
        _failures = 0;
    }

    /// <summary>
    /// A job failed (DL-20). Deferred (the store held, a job running, the engine closing): retried after the base delay, not counted. Counted: retried on a
    /// doubling backoff from the base delay up to a quarter of the interval. Permanent: retried at the cap (only a change of the engine's state helps).
    /// </summary>
    public void Failed(BackupScheduler.FailureClass failure, long monoNow)
    {
        if (_interval == 0)
        {
            return;
        }

        long delay;
        switch (failure)
        {
            case BackupScheduler.FailureClass.Deferred:
                delay = _baseRetry;
                break;
            case BackupScheduler.FailureClass.Permanent:
                delay = _maxRetry;
                break;
            default:
                delay = _baseRetry;
                for (var i = 0; i < _failures && delay < _maxRetry; i++)
                {
                    delay *= 2;
                }

                delay = Math.Min(delay, _maxRetry);
                _failures++;
                break;
        }

        _retryAt = monoNow + Math.Max(delay, 1);
    }

    /// <summary>The wall-clock estimate of <see cref="DueAt"/>, for display; <see cref="DateTime.MinValue"/> with no schedule.</summary>
    public DateTime NextDueUtc(DateTime utcNow, long monoNow)
    {
        var due = DueAt();
        if (due == long.MaxValue)
        {
            return DateTime.MinValue;
        }

        return due <= monoNow ? utcNow : utcNow + TimeSpan.FromSeconds((double)(due - monoNow) / _frequency);
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _frequency);
}
