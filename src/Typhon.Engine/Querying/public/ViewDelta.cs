using System.Collections;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// A zero-allocation view into the delta state of a <see cref="View{T}"/> or <see cref="View{T1,T2}"/>.
/// <para>
/// <b>Lifetime:</b> This struct references the View's internal delta storage directly — no copies are made.
/// The data is only valid until the next call to <see cref="View{T}.ClearDelta"/> (or the equivalent on
/// <see cref="View{T1,T2}"/>). After <c>ClearDelta()</c>, all collections will appear empty.
/// Do not cache this struct across refresh cycles.
/// </para>
/// <para>
/// <b>Typical usage:</b>
/// <code>
/// view.Refresh(tx);
/// var delta = view.GetDelta();
/// // Process delta.Added, delta.Removed, delta.Modified
/// view.ClearDelta();
/// // delta is now invalid — all collections report empty
/// </code>
/// </para>
/// </summary>
public readonly struct ViewDelta
{
    /// <summary>Entity PKs that entered the view since the last <c>ClearDelta()</c>.</summary>
    public readonly DeltaView Added;

    /// <summary>Entity PKs that left the view since the last <c>ClearDelta()</c>.</summary>
    public readonly DeltaView Removed;

    /// <summary>Entity PKs that remained in the view but had field values change since the last <c>ClearDelta()</c>.</summary>
    public readonly DeltaView Modified;

    internal ViewDelta(Dictionary<long, DeltaKind> deltas, int addedCount, int removedCount, int modifiedCount)
    {
        Added = new DeltaView(deltas, DeltaKind.Added, addedCount);
        Removed = new DeltaView(deltas, DeltaKind.Removed, removedCount);
        Modified = new DeltaView(deltas, DeltaKind.Modified, modifiedCount);
    }

    /// <summary><c>true</c> when no entities were added, removed, or modified.</summary>
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Modified.Count == 0;
}

/// <summary>
/// A zero-allocation, filtered read-only view over a View's internal delta dictionary.
/// Provides O(1) <see cref="Count"/> and <see cref="Contains"/> lookups.
/// <para>
/// <b>Lifetime:</b> This struct shares the same lifetime as its parent <see cref="ViewDelta"/>.
/// It becomes invalid after <c>ClearDelta()</c> is called on the owning View.
/// </para>
/// </summary>
public readonly struct DeltaView : IReadOnlyCollection<long>
{
    private readonly Dictionary<long, DeltaKind> _deltas;
    private readonly DeltaKind _filter;

    /// <summary>Number of entity PKs in this delta category.</summary>
    public int Count { get; }

    internal DeltaView(Dictionary<long, DeltaKind> deltas, DeltaKind filter, int count)
    {
        _deltas = deltas;
        _filter = filter;
        Count = count;
    }

    /// <summary>O(1) check whether the given entity PK is in this delta category.</summary>
    public bool Contains(long pk) => _deltas != null && _deltas.TryGetValue(pk, out var kind) && kind == _filter;

    /// <inheritdoc />
    public IEnumerator<long> GetEnumerator()
    {
        if (_deltas == null)
        {
            yield break;
        }

        foreach (var kv in _deltas)
        {
            if (kv.Value == _filter)
            {
                yield return kv.Key;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
