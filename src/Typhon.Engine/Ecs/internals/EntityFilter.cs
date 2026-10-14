namespace Typhon.Engine.Internals;

/// <summary>
/// The component types a query names <c>Disabled&lt;T&gt;()</c>: the ones a per-entity predicate reads when disabled and present (QENABLE-01). Taken from the
/// query when it runs, not when the predicate is built, so a <c>Disabled&lt;T&gt;()</c> chained after the <c>Where</c> counts.
/// </summary>
internal readonly struct DisabledTypeIds
{
    private readonly int _count;
    private readonly int _t0;
    private readonly int _t1;
    private readonly int _t2;
    private readonly int _t3;

    public DisabledTypeIds(int count, int t0, int t1, int t2, int t3)
    {
        _count = count;
        _t0 = t0;
        _t1 = t1;
        _t2 = t2;
        _t3 = t3;
    }

    /// <summary>Whether the query names <c>Disabled&lt;T&gt;()</c> on the component type <paramref name="typeId"/>.</summary>
    public bool Contains(int typeId) =>
        (_count > 0 && _t0 == typeId) || (_count > 1 && _t1 == typeId) || (_count > 2 && _t2 == typeId) || (_count > 3 && _t3 == typeId);
}

/// <summary>
/// A query's per-entity predicate — a <c>Where&lt;T&gt;</c> lambda, or a <c>WhereField</c> compiled for the paths that test entities one at a time: the
/// transaction's own pending spawns and the spatial candidates. <paramref name="readDisabled"/>: the components it may read disabled (QENABLE-01).
/// </summary>
internal delegate bool EntityFilter(EntityId id, Transaction tx, DisabledTypeIds readDisabled);
