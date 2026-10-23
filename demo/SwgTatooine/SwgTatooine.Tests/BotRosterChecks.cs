using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SwgTatooine.Bots;

namespace SwgTatooine.Tests;

/// <summary>
/// Taking dead sessions out of a fleet's population, which is the step whose absence made every loss permanent.
/// </summary>
/// <remarks>
/// <b>These exist because the missing behaviour was a missing REQUIREMENT, not a wrong line.</b> The fleet counted slots it had
/// filled and never asked whether the sessions in them were still open, so a target of 8 000 was compared against a count holding
/// 4 407 closed sockets and growth never ran again. No review of the old code could have caught that — nothing in it was wrong on
/// its own terms — which is why the property is stated here as a test rather than described in a comment.
/// </remarks>
[TestFixture]
internal sealed class BotRosterChecks
{
    /// <summary>A population with nothing dead in it is left exactly as it was.</summary>
    [Test]
    public void AllAliveIsLeftAlone()
    {
        var slots = new[] { "a", "b", "c", null, null };
        var victims = new List<string>();

        var kept = BotRoster.Compact(slots, 3, _ => true, victims);

        Assert.Multiple(() =>
        {
            Assert.That(kept, Is.EqualTo(3));
            Assert.That(victims, Is.Empty);
            Assert.That(slots[..3], Is.EqualTo(new[] { "a", "b", "c" }), "a pass that changes nothing must not reorder the population");
        });
    }

    /// <summary>Every dead slot is handed back exactly once, and every live one survives.</summary>
    /// <remarks>
    /// The set, not the order: a swap-remove reorders what it keeps, deliberately, because the account index lives on the bot and
    /// not in its slot. What must hold is that nothing live is dropped and nothing dead is kept — dropping a live bot leaks its
    /// index and its socket, keeping a dead one is the bug this type was extracted over.
    /// </remarks>
    [Test]
    public void DeadSlotsComeOutAndLiveOnesStay()
    {
        var slots = Enumerable.Range(0, 20).Select(i => i.ToString()).ToArray();
        var dead = new HashSet<string> { "0", "3", "4", "11", "19" };
        var victims = new List<string>();

        var kept = BotRoster.Compact(slots, slots.Length, slot => !dead.Contains(slot), victims);

        Assert.Multiple(() =>
        {
            Assert.That(kept, Is.EqualTo(15));
            Assert.That(victims, Is.EquivalentTo(dead), "a victim list that is not exactly the dead set means a leak or a double release");
            Assert.That(slots[..kept], Is.EquivalentTo(Enumerable.Range(0, 20).Select(i => i.ToString()).Except(dead)));
            Assert.That(slots[kept..], Is.All.Null, "slots above the new count must be emptied; a stale reference there is pinged forever");
        });
    }

    /// <summary>A population that is entirely dead empties completely rather than leaving a survivor behind.</summary>
    /// <remarks>
    /// The all-dead case is the one a swap-remove gets wrong if it writes the null before the move: the last victim would overwrite
    /// itself with null and then be handed back as a survivor, or not handed back at all.
    /// </remarks>
    [Test]
    public void AnEntirelyDeadPopulationEmpties()
    {
        var slots = new[] { "a", "b", "c", "d" };
        var victims = new List<string>();

        var kept = BotRoster.Compact(slots, 4, _ => false, victims);

        Assert.Multiple(() =>
        {
            Assert.That(kept, Is.Zero);
            Assert.That(victims, Is.EquivalentTo(new[] { "a", "b", "c", "d" }));
            Assert.That(slots, Is.All.Null);
        });
    }

    /// <summary>Only the published part of the array is read, so the capacity beyond the population is untouched.</summary>
    /// <remarks>
    /// The array is the fleet's CAPACITY and is allocated once; the count says how much of it is a population. A compaction that
    /// walked the whole array would retire bots that have not been opened yet and release indices nobody holds.
    /// </remarks>
    [Test]
    public void SlotsAboveTheCountAreNeverRead()
    {
        var slots = new[] { "a", "b", "dead-but-unpublished", "x" };
        var victims = new List<string>();

        var kept = BotRoster.Compact(slots, 2, _ => true, victims);

        Assert.Multiple(() =>
        {
            Assert.That(kept, Is.EqualTo(2));
            Assert.That(victims, Is.Empty);
            Assert.That(slots[2], Is.EqualTo("dead-but-unpublished"), "the unpublished tail was rewritten");
            Assert.That(slots[3], Is.EqualTo("x"));
        });
    }

    /// <summary>A null below the count is removed without being reported as a victim.</summary>
    /// <remarks>
    /// A null there is a slot a shrink emptied: there is nothing to dispose and no index to release, but leaving it in the
    /// population would have the ping loop and the driver skip a slot forever while the count claims a session that is not there.
    /// </remarks>
    [Test]
    public void ANullBelowTheCountIsRemovedQuietly()
    {
        var slots = new[] { "a", null, "c" };
        var victims = new List<string>();

        var kept = BotRoster.Compact(slots, 3, _ => true, victims);

        Assert.Multiple(() =>
        {
            Assert.That(kept, Is.EqualTo(2));
            Assert.That(victims, Is.Empty, "a null slot has no session to hand back");
            Assert.That(slots[..2], Is.EquivalentTo(new[] { "a", "c" }));
            Assert.That(slots[2], Is.Null);
        });
    }

    /// <summary>
    /// The property the dial depends on: losses and replacements over many rounds never lose or duplicate an account index.
    /// </summary>
    /// <remarks>
    /// This is the loop the control panel runs for ten minutes — sessions die, the pass takes them out, their indices go back to
    /// the pool, the next chunk re-opens them — played against <see cref="BotIndexPool"/> so the two types are exercised together.
    /// Two live bots sharing an index is the failure that reads as <c>AccountAlreadyPlaying</c> on an arriving session, and a
    /// leaked index is reach the fleet never gets back.
    /// </remarks>
    [Test]
    public void LossAndReplacementOverManyRoundsNeitherLosesNorDuplicatesAnIndex()
    {
        const int Capacity = 500;
        const int Target = 400;

        var pool = new BotIndexPool(Capacity);
        var slots = new string[Capacity];
        var count = 0;
        var dead = new HashSet<string>();
        var rng = new Random(20261016);

        // Grow to the target the way the reconciler does: one chunk at a time, from the pool, each bot named from its index.
        void GrowToTarget()
        {
            while (count < Target)
            {
                var from = pool.Allocate(Math.Min(50, Target - count), out var got);
                Assert.That(got, Is.GreaterThan(0), "the pool ran dry although every lost index was released");
                for (var k = 0; k < got; k++)
                {
                    slots[count++] = (from + k).ToString();
                }
            }
        }

        for (var round = 0; round < 40; round++)
        {
            GrowToTarget();

            Assert.That(slots[..count], Is.Unique, "two live bots hold one account index, which the server refuses as AccountAlreadyPlaying");

            // Kill a scattered share of them, as a silence close would.
            dead.Clear();
            var killing = rng.Next(1, 60);
            for (var k = 0; k < killing; k++)
            {
                dead.Add(slots[rng.Next(count)]);
            }

            var victims = new List<string>();
            count = BotRoster.Compact(slots, count, slot => !dead.Contains(slot), victims);

            Assert.That(victims, Is.EquivalentTo(dead), "round " + round + " handed back a different set than died");

            foreach (var victim in victims)
            {
                pool.Release(int.Parse(victim), 1);
            }
        }

        // The round trip closes with a growth, which is the recovery itself: the dial must be able to reach its target again after
        // forty rounds of losses, from indices that have each been handed out and given back several times.
        GrowToTarget();

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(Target), "the fleet could not be grown back to its target after forty rounds of loss");
            Assert.That(slots[..count], Is.Unique);
            Assert.That(pool.Available, Is.EqualTo(Capacity - Target), "forty rounds of loss and replacement drifted the pool");
        });
    }
}
