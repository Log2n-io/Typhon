using System;

namespace SwgTatooine.Bots;

/// <summary>
/// An exponential back-off with jitter: <c>base</c> doubled per call in a row up to <c>maxShift</c> doublings, plus up to half again, until
/// <see cref="Reset"/>. The fleet's reconciler waits this long after a round that opened nothing (#1532).
/// </summary>
public sealed class RampBackoff
{
    private readonly TimeSpan _base;
    private readonly int _maxShift;
    private uint _jitter;
    private int _rounds;

    /// <param name="baseDelay">The first delay.</param>
    /// <param name="maxShift">The most doublings.</param>
    /// <param name="seed">Spreads the jitter of several fleets.</param>
    public RampBackoff(TimeSpan baseDelay, int maxShift, int seed)
    {
        _base = baseDelay;
        _maxShift = Math.Max(0, maxShift);
        _jitter = (uint)seed * 2654435761u | 1u;
    }

    /// <summary>Delays handed out since the last <see cref="Reset"/>.</summary>
    public int Rounds => _rounds;

    /// <summary>The next delay, and one more round.</summary>
    public TimeSpan Next()
    {
        var delay = _base * (1L << Math.Min(_rounds, _maxShift));
        _rounds++;
        _jitter ^= _jitter << 13;
        _jitter ^= _jitter >> 17;
        _jitter ^= _jitter << 5;
        return delay + (delay * (_jitter % 501u) / 1000.0);
    }

    /// <summary>Back to the first delay: a round succeeded.</summary>
    public void Reset() => _rounds = 0;
}
