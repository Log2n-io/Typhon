using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The handshake between a commit that destroys a cascade parent and a commit that points a child at it (CASC-02): the destroyer's HEAD lookup must
/// see every child whose commit passed the parent check, and a child that did not pass it must not commit under a parent being destroyed.
/// </summary>
/// <remarks>
/// <para>
/// Dekker-style, on striped counters keyed by the parent's id. A referrer <see cref="Announce"/>s itself (an interlocked increment, a full fence) and
/// only then asks <see cref="IsDying"/>; a destroyer <see cref="MarkDying"/>s the parent (registered, then an interlocked increment) and only then reads
/// the referrer count (<see cref="BeginWait"/>, <see cref="IsDrained"/>). One of the two sees the other: either the referrer finds the parent dying and
/// refuses, or the destroyer finds the referrer counted and waits for it to publish — its child then in the trees the lookup reads.
/// </para>
/// <para>
/// <b>Two counters per stripe, a phase choosing between them.</b> A referrer counts itself in the current phase's counter; a destroyer flips the phase
/// after marking, and waits for the OLD phase's counter alone. A referrer that read the old phase and counted itself after the destroyer's read sees the
/// mark (the Dekker argument above), and one that read the new phase sees it too: so the destroyer needs only the referrers counted before its flip, a
/// finite set, and a stream of referrers to other parents of the stripe cannot keep it waiting. Two destroyers on one stripe flip it back and forth; one
/// may then wait for referrers that came after its flip — bounded by the other's wait, not by the referrer traffic.
/// </para>
/// <para>
/// No cycle: a referrer never waits — it refuses, and the caller may retry. A commit plays the destroyer before it announces anything, so a commit waiting
/// on a stripe holds no announcement another destroyer could wait on, and it waits before its commit card and any chain lock, key claim or supply.
/// </para>
/// </remarks>
internal sealed class CascadeReferrerGate
{
    private const int StripeBits = 8;
    private const int Stripes = 1 << StripeBits;

    /// <summary>Longs per stripe — one cache line: [0] the phase, [1] and [2] the referrer counters of phase 0 and 1.</summary>
    private const int Stride = 8;

    private readonly long[] _referrers = new long[Stripes * Stride];
    private readonly long[] _dying = new long[Stripes * Stride];
    private readonly ConcurrentDictionary<long, int> _dyingParents = new();

    /// <summary>The first long of <paramref name="parentPk"/>'s stripe (tests use it to find two parents sharing one).</summary>
    internal static int SlotOf(long parentPk) => (int)((ulong)parentPk * 0x9E3779B97F4A7C15UL >> (64 - StripeBits)) * Stride;

    /// <summary>
    /// A referrer, before it tests the parent: counted on the parent's stripe, in its current phase, until <see cref="Withdraw"/> with the token returned.
    /// </summary>
    internal int Announce(long parentPk)
    {
        var slot = SlotOf(parentPk);
        var counter = slot + 1 + (int)(Volatile.Read(ref _referrers[slot]) & 1);
        Interlocked.Increment(ref _referrers[counter]);
        return counter;
    }

    /// <summary>Gives back an <see cref="Announce"/>: the referrer's commit has published, or will never.</summary>
    internal void Withdraw(int token) => Interlocked.Decrement(ref _referrers[token]);

    /// <summary>After <see cref="Announce"/>: whether a committing transaction is destroying <paramref name="parentPk"/>.</summary>
    internal bool IsDying(long parentPk) => Volatile.Read(ref _dying[SlotOf(parentPk)]) != 0 && _dyingParents.ContainsKey(parentPk);

    /// <summary>A destroyer, before it looks up the parent's children: registered, then counted — a referrer that sees the count finds the parent.</summary>
    internal void MarkDying(long parentPk)
    {
        _dyingParents.AddOrUpdate(parentPk, 1, static (_, n) => n + 1);
        Interlocked.Increment(ref _dying[SlotOf(parentPk)]);
    }

    /// <summary>Gives back a <see cref="MarkDying"/>, once the destroy is published or will never be.</summary>
    internal void UnmarkDying(long parentPk)
    {
        Interlocked.Decrement(ref _dying[SlotOf(parentPk)]);
        while (true)
        {
            if (!_dyingParents.TryGetValue(parentPk, out var n))
            {
                Debug.Fail("UnmarkDying without MarkDying");
                return;
            }

            if (n == 1 ? _dyingParents.TryRemove(new(parentPk, 1)) : _dyingParents.TryUpdate(parentPk, n - 1, n))
            {
                return;
            }
        }
    }

    /// <summary>After <see cref="MarkDying"/>: flips the stripe's phase and returns the counter of the referrers announced before — the ones to wait for.</summary>
    internal int BeginWait(long parentPk)
    {
        var slot = SlotOf(parentPk);
        var old = Interlocked.Increment(ref _referrers[slot]) - 1;
        return slot + 1 + (int)(old & 1);
    }

    /// <summary>Whether every referrer of <see cref="BeginWait"/>'s phase has withdrawn.</summary>
    internal bool IsDrained(int counter) => Volatile.Read(ref _referrers[counter]) == 0;

    /// <summary>Tests: the referrers counted on <paramref name="parentPk"/>'s stripe, both phases.</summary>
    internal long ReferrersOnStripeOf(long parentPk)
    {
        var slot = SlotOf(parentPk);
        return Volatile.Read(ref _referrers[slot + 1]) + Volatile.Read(ref _referrers[slot + 2]);
    }
}
