using System;
using System.Threading;
using Typhon.Client;
using Typhon.Protocol;

namespace SwgTatooine.Bots;

/// <summary>
/// When a bot reads an owner endpoint: the browser's rule (<c>RevisionReader</c>, missions.md § 4). A read whenever the body's owner field moved since
/// the last one, one at a time. A failed read backs off: the next is due <c>retryTicks</c> after the failure ENDED, doubled per failure in a row up to
/// <see cref="MaxBackoffShift"/> doublings, plus a jitter of up to half the delay, and a moved revision does not cut the wait short. The journal
/// (<c>journal</c>, <c>GET /swg/missions</c>) and the inventory (<c>items</c>, <c>GET /swg/inventory</c>) read this way.
/// </summary>
/// <remarks>
/// <b>Why it backs off.</b> A server that answers slowly or not at all (a cold page cache under a whole swarm reconnecting, #1532) used to be asked again by
/// every bot the moment its read ended — the delay counted from when the read STARTED, and a read that timed out after ten seconds was past any three-second
/// delay — and again on every revision in between. Hundreds of bots in lock step kept the server at its worst.
/// </remarks>
public sealed class OwnerFieldPoll
{
    /// <summary>The most doublings of the retry delay: 12 ticks becomes at most 192, under a minute at 4 Hz.</summary>
    public const int MaxBackoffShift = 4;

    private readonly string _field;
    private readonly long _retryTicks;
    private uint _jitter;
    private ArchetypePlan _for;
    private int _ordinal = -1;
    private double _asked = double.NaN;
    private long _retryAt;
    private int _failures;
    private int _busy;

    /// <param name="field">The owner field's name on the controlled body.</param>
    /// <param name="retryTicks">How long a first failed read waits, in bot ticks.</param>
    /// <param name="seed">Spreads the jitter of the bots of one swarm: their index.</param>
    public OwnerFieldPoll(string field, long retryTicks, int seed = 0)
    {
        _field = field;
        _retryTicks = Math.Max(1, retryTicks);
        _jitter = (uint)seed * 2654435761u | 1u;
    }

    /// <summary>Failed reads in a row; 0 after a read that succeeded.</summary>
    public int Failures => _failures;

    /// <summary>The field's value on the controlled body; <see cref="double.NaN"/> without a body, without the field, or before it arrived.</summary>
    public double Revision(SelfState self)
    {
        if (self?.Archetype == null)
        {
            return double.NaN;
        }

        if (!ReferenceEquals(self.Archetype, _for))
        {
            _for = self.Archetype;
            _ordinal = -1;
            foreach (var field in self.Archetype.OwnerFields)
            {
                if (field.Name == _field)
                {
                    _ordinal = field.Ordinal;
                }
            }
        }

        return _ordinal >= 0 && _ordinal < self.Numbers.Length && self.Numbers[_ordinal] is { Length: > 0 } values ? values[0] : double.NaN;
    }

    /// <summary>
    /// Whether a read starts now: none is in flight, no failure is backing off, and the revision moved since the last read or a failed one is due again. A
    /// true answer must be followed by <see cref="End"/>.
    /// </summary>
    public bool TryBegin(double rev, long tick)
    {
        if (double.IsNaN(rev) || tick < _retryAt || (rev == _asked && _failures == 0) || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return false;
        }

        _asked = rev;
        return true;
    }

    /// <summary>Ends the read <see cref="TryBegin"/> started, at <paramref name="endTick"/>: the tick the read ENDED on, from which a failure backs off.</summary>
    public void End(bool ok, long endTick)
    {
        if (ok)
        {
            _failures = 0;
            _retryAt = 0;
        }
        else
        {
            var delay = _retryTicks << Math.Min(_failures, MaxBackoffShift);
            _failures++;

            // xorshift32: a different jitter per bot and per failure, so a swarm that failed together does not come back together.
            _jitter ^= _jitter << 13;
            _jitter ^= _jitter >> 17;
            _jitter ^= _jitter << 5;
            _retryAt = endTick + delay + (long)(_jitter % (ulong)(delay / 2 + 1));
        }

        Volatile.Write(ref _busy, 0);
    }
}
