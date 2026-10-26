using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// Which sessions survive the per-session row cap (#WB-02) — the selection the operator table's usefulness rests on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ungated and pure, deliberately.</b> <c>SubscriptionsOperatorTelemetryTests</c> proves the emission happens at all and needs the telemetry subtree on, so
/// it runs in one dedicated process; reaching the case below through it would need sixty-five live sessions with contrived byte counts. The arithmetic is where
/// the claim lives, so the arithmetic is what is extracted — the same split <c>BotRoster</c> uses for the fleet's compaction.
/// </para>
/// <para>
/// <b>What a defect here looks like in the product:</b> the Subscriptions panel captions its table "busiest first" and reports how many sessions it is not
/// showing. If the selection keeps the wrong rows, both statements are false at exactly the population where the cap engages — 64 arbitrary rows out of 8 000,
/// presented as the heaviest — and nothing fails. That is the failure this fixture exists to make loud.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class TopSessionRowsTests
{
    /// <summary>A population under the cap is kept whole, which is what an ordinary server looks like.</summary>
    [Test]
    public void EverythingFitsBelowTheCap()
    {
        var selection = new TopSessionRows(8);
        Offer(selection, [5, 1, 9]);

        var kept = Bytes(selection.Heaviest());

        Assert.Multiple(() =>
        {
            Assert.That(selection.Count, Is.EqualTo(3));
            Assert.That(kept, Is.EqualTo(new long[] { 9, 5, 1 }), "kept rows are ordered heaviest first, which is the order the panel claims");
        });
    }

    /// <summary>The heaviest survive a population many times the cap, whatever order they arrive in.</summary>
    /// <remarks>
    /// The arrival order matters because the session table yields its open list in slot order, which has nothing to do with traffic: the heaviest session can
    /// be the first offered, the last, or anywhere between. All three are exercised by shuffling a known population.
    /// </remarks>
    [Test]
    public void TheHeaviestSurviveARealPopulation()
    {
        const int cap = 64;
        var bytes = Enumerable.Range(1, 8000).Select(i => (long)i * 7).ToArray();
        var rng = new Random(20261016);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var shuffled = bytes.OrderBy(_ => rng.Next()).ToArray();
            var selection = new TopSessionRows(cap);
            Offer(selection, shuffled);

            var kept = Bytes(selection.Heaviest());
            var expected = bytes.OrderByDescending(b => b).Take(cap).ToArray();

            Assert.That(kept, Is.EqualTo(expected), "attempt " + attempt + ": the cap kept rows that are not the heaviest");
        }
    }

    /// <summary>
    /// A tie is kept in arrival order, so an idle server behaves exactly as the old truncating cap did.
    /// </summary>
    /// <remarks>
    /// Worth pinning rather than leaving to chance: on a server where every session is quiet, every candidate's key is zero and "the heaviest" names no
    /// particular row. Keeping the first seen makes the table stable between emissions instead of reshuffling 64 identical rows every second, which would read
    /// as churn that is not happening.
    /// </remarks>
    [Test]
    public void TiesKeepTheSessionSeenFirst()
    {
        var selection = new TopSessionRows(3);
        for (var id = 1UL; id <= 10UL; id++)
        {
            selection.Offer(new OperatorSessionRow { SessionId = id, Bytes = 0 });
        }

        var kept = selection.Heaviest();

        Assert.That(kept.ToArray().Select(r => r.SessionId), Is.EquivalentTo(new ulong[] { 1, 2, 3 }),
            "a quiet server must show the same sessions each emission, not a fresh arbitrary three");
    }

    /// <summary>Every field of a kept row survives the selection, not only the key it was selected on.</summary>
    /// <remarks>
    /// A heap swaps whole rows. Keying on one field while moving another — or offering a row and emitting a neighbour's realm beside its byte count — would
    /// attribute one session's traffic to another, which is worse than showing no row at all.
    /// </remarks>
    [Test]
    public void AKeptRowCarriesAllOfItsOwnFields()
    {
        var selection = new TopSessionRows(2);
        selection.Offer(new OperatorSessionRow { SessionId = 11, Bytes = 10, Frames = 1, FramesProduced = 100, FramesSkipped = 7, RealmId = 3, DegradeLevel = 1 });
        selection.Offer(new OperatorSessionRow { SessionId = 22, Bytes = 99, Frames = 9, FramesProduced = 900, FramesSkipped = 0, RealmId = 65535, DegradeLevel = 0 });
        selection.Offer(new OperatorSessionRow { SessionId = 33, Bytes = 1, Frames = 0, FramesProduced = 10, FramesSkipped = 70, RealmId = 1, DegradeLevel = 2 });

        var kept = selection.Heaviest().ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(kept.Select(r => r.SessionId), Is.EqualTo(new ulong[] { 22, 11 }));

            Assert.That(kept[0].Frames, Is.EqualTo(9));
            Assert.That(kept[0].FramesProduced, Is.EqualTo(900));
            Assert.That(kept[0].FramesSkipped, Is.Zero);
            Assert.That(kept[0].RealmId, Is.EqualTo(65535), "the 'no realm yet' sentinel must not be mangled into a realm");
            Assert.That(kept[0].DegradeLevel, Is.Zero);

            Assert.That(kept[1].Frames, Is.EqualTo(1));
            Assert.That(kept[1].FramesProduced, Is.EqualTo(100));
            Assert.That(kept[1].FramesSkipped, Is.EqualTo(7));
            Assert.That(kept[1].RealmId, Is.EqualTo(3));
            Assert.That(kept[1].DegradeLevel, Is.EqualTo(1));
        });
    }

    /// <summary>A cleared selection is a new window, with nothing of the previous one left in it.</summary>
    /// <remarks>
    /// The instance is reused for the life of the assembler — that is the point of the type — so a window that leaked a row from the last one would report a
    /// session's traffic from a second ago as this second's, and a closed session's as an open one's.
    /// </remarks>
    [Test]
    public void ClearStartsTheNextWindowEmpty()
    {
        var selection = new TopSessionRows(4);
        Offer(selection, [500, 400, 300]);
        _ = selection.Heaviest();

        selection.Clear();
        Assert.That(selection.Count, Is.Zero);

        Offer(selection, [2, 1]);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Count, Is.EqualTo(2), "a cleared selection that still counts last window's rows would overstate ReportedSessions");
            Assert.That(Bytes(selection.Heaviest()), Is.EqualTo(new long[] { 2, 1 }), "last window's heavier rows outranked this window's");
        });
    }

    /// <summary>Offering after the rows have been ordered throws instead of silently selecting against the wrong row.</summary>
    /// <remarks>
    /// Ordering consumes the heap, so the root is no longer the smallest row kept and a later offer would be compared against whichever row the sort left
    /// there. The emission's contract is clear-offer-emit once per window; a future caller that gets it wrong should find out here.
    /// </remarks>
    [Test]
    public void OfferingAfterOrderingIsRefused()
    {
        var selection = new TopSessionRows(2);
        Offer(selection, [5, 6]);
        _ = selection.Heaviest();

        Assert.That(() => selection.Offer(new OperatorSessionRow { Bytes = 100 }), Throws.InstanceOf<InvalidOperationException>());
    }

    /// <summary>A capacity below one is a programming error, not a selection that keeps nothing.</summary>
    [Test]
    public void ACapacityBelowOneIsRefused()
    {
        Assert.That(() => new TopSessionRows(0), Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    private static void Offer(TopSessionRows selection, IEnumerable<long> bytes)
    {
        var id = 1UL;
        foreach (var b in bytes)
        {
            selection.Offer(new OperatorSessionRow { SessionId = id++, Bytes = b });
        }
    }

    private static long[] Bytes(ReadOnlySpan<OperatorSessionRow> rows)
    {
        var result = new long[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            result[i] = rows[i].Bytes;
        }

        return result;
    }
}
