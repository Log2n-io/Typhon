using System.Threading;
using Typhon.Client;
using Typhon.Protocol;

namespace SwgTatooine.Bots;

/// <summary>
/// When a bot reads an owner endpoint: the browser's rule (<c>RevisionReader</c>, missions.md § 4). A read whenever the body's owner field moved since
/// the last one, one at a time; a failed read due again after <c>retryTicks</c>, or at once on the next revision. The journal (<c>journal</c>,
/// <c>GET /swg/missions</c>) and the inventory (<c>items</c>, <c>GET /swg/inventory</c>) read this way.
/// </summary>
public sealed class OwnerFieldPoll
{
    private readonly string _field;
    private readonly long _retryTicks;
    private ArchetypePlan _for;
    private int _ordinal = -1;
    private double _asked = double.NaN;
    private long _retryAt;
    private int _busy;

    /// <param name="field">The owner field's name on the controlled body.</param>
    /// <param name="retryTicks">How long a failed read waits, in bot ticks.</param>
    public OwnerFieldPoll(string field, long retryTicks)
    {
        _field = field;
        _retryTicks = retryTicks;
    }

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
    /// Whether a read starts now: the revision moved since the last read, or a failed one is due again, and none is in flight. A true answer must be
    /// followed by <see cref="End"/>.
    /// </summary>
    public bool TryBegin(double rev, long tick)
    {
        if (double.IsNaN(rev) || (rev == _asked && tick < _retryAt) || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return false;
        }

        _asked = rev;
        _retryAt = long.MaxValue;
        return true;
    }

    /// <summary>Ends the read <see cref="TryBegin"/> started at <paramref name="tick"/>: a failed one is asked again after the retry delay.</summary>
    public void End(bool ok, long tick)
    {
        if (!ok)
        {
            _retryAt = tick + _retryTicks;
        }

        Volatile.Write(ref _busy, 0);
    }
}
