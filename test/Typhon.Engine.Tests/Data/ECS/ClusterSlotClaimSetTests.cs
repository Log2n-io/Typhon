using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// <see cref="ClusterSlotClaimSet"/> — the flat per-cluster slot-claim storage that replaced the two source-exclusion dictionaries (#882).
/// </summary>
/// <remarks>
/// <para>The set is a representation change under <c>CR-05</c>, not a semantic one, so the fixtures that prove the RULE —
/// <c>ClusterMigrationSourceExclusivityTests</c> — stay the end-to-end authority. What those cannot see is the container's own edges: a chunk id past
/// anything ever claimed, a clear that must not cost the segment's capacity, and the <c>Count == 0</c> gate both call sites branch on before probing at
/// all.</para>
/// <para><b>Ablations</b> — each assertion below reddens under a named production mutation:
/// dropping the <c>before == 0UL</c> guard in <c>Claim</c> (the touched list gains duplicates and <c>Count</c> stops being a distinct-cluster count);
/// clearing only <c>_touchedCount</c> without zeroing the words (stale claims survive);
/// dropping the bounds test in <c>ClaimedSlots</c> (an unclaimed high chunk id throws instead of reporting nothing).</para>
/// </remarks>
[TestFixture]
class ClusterSlotClaimSetTests
{
    [Test]
    public void AClaimedSlotIsReportedAndItsNeighboursAreNot()
    {
        var set = new ClusterSlotClaimSet();
        set.Claim(7, 3);

        Assert.Multiple(() =>
        {
            Assert.That(set.ClaimedSlots(7), Is.EqualTo(1UL << 3), "only the claimed slot's bit is set");
            Assert.That(set.ContainsCluster(7), Is.True);
            Assert.That(set.ClaimedSlots(6), Is.EqualTo(0UL), "an adjacent cluster is untouched");
            Assert.That(set.ContainsCluster(6), Is.False);
            Assert.That(set.Count, Is.EqualTo(1), "one distinct cluster holds a claim");
        });
    }

    [Test]
    public void SlotSixtyThreeDoesNotSignExtend()
    {
        // 1L << 63 would set the sign bit of a signed shift; the mask is deliberately ulong. A regression here would make slot 63 claim every slot.
        var set = new ClusterSlotClaimSet();
        set.Claim(1, 63);

        Assert.That(set.ClaimedSlots(1), Is.EqualTo(1UL << 63));
        Assert.That(set.ClaimedSlots(1) & 1UL, Is.EqualTo(0UL), "claiming slot 63 must not also claim slot 0");
    }

    [Test]
    public void ManySlotsOfOneClusterAccumulateIntoOneWordAndCountTheClusterOnce()
    {
        var set = new ClusterSlotClaimSet();
        for (var slot = 0; slot < 64; slot += 2)
        {
            set.Claim(11, slot);
        }

        // Claiming the same slot again must not re-record the cluster — this is what makes Count a distinct-cluster count rather than a claim count, and
        // both call sites gate on `Count > 0` meaning "any claim at all".
        set.Claim(11, 0);

        Assert.Multiple(() =>
        {
            Assert.That(set.ClaimedSlots(11), Is.EqualTo(0x5555555555555555UL), "every even slot, and no odd one");
            Assert.That(set.Count, Is.EqualTo(1), "one cluster, however many of its slots were claimed");
        });
    }

    [Test]
    public void AChunkIdPastAnythingEverClaimedReportsNothingRatherThanThrowing()
    {
        // The repair planner probes clusters it is considering, not clusters it has claimed, so a probe beyond the high-water mark is the ordinary case and
        // must be free. The dictionary this replaced answered it with GetValueOrDefault.
        var set = new ClusterSlotClaimSet();
        set.Claim(2, 1);

        Assert.Multiple(() =>
        {
            Assert.That(set.ClaimedSlots(int.MaxValue), Is.EqualTo(0UL));
            Assert.That(set.ContainsCluster(1_000_000), Is.False);
            Assert.That(set.ClaimedSlots(0), Is.EqualTo(0UL));
        });
    }

    [Test]
    public void ClearDropsEveryClaimAndTheSetIsReusable()
    {
        var set = new ClusterSlotClaimSet();
        set.Claim(3, 5);
        set.Claim(900, 60);
        Assert.That(set.Count, Is.EqualTo(2));

        set.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(set.Count, Is.EqualTo(0), "an empty set is what the Count > 0 gate reads");
            Assert.That(set.ClaimedSlots(3), Is.EqualTo(0UL), "a word cleared, not merely forgotten");
            Assert.That(set.ClaimedSlots(900), Is.EqualTo(0UL));
        });

        // Reuse across ticks is the whole point of the type — a second round must behave like the first.
        set.Claim(3, 6);
        Assert.That(set.ClaimedSlots(3), Is.EqualTo(1UL << 6), "the second round sees no residue of the first");
        Assert.That(set.Count, Is.EqualTo(1));
    }

    [Test]
    public void GrowthPastThePresizedBoundKeepsEveryEarlierClaim()
    {
        // Reachable on a NORMAL tick, not pathologically: the repair planner allocates fresh destination clusters mid-Prep via AllocateEmptyClusterForCell,
        // AFTER BuildRepairSourceExclusions has sized the set. A grow that dropped the low claims would reopen #877 silently.
        // 200 distinct clusters, deliberately past the touched list's first capacity (64) so its DOUBLING path runs. At 40 it never did, and the closing
        // assertion below claimed to cover it — a review caught the gap.
        const int Distinct = 200;
        var set = new ClusterSlotClaimSet();
        for (var chunkId = 0; chunkId < Distinct; chunkId++)
        {
            set.Claim(chunkId, chunkId & 63);
        }

        set.Claim(50_000, 17);

        Assert.Multiple(() =>
        {
            for (var chunkId = 0; chunkId < Distinct; chunkId++)
            {
                Assert.That(set.ClaimedSlots(chunkId), Is.EqualTo(1UL << (chunkId & 63)), $"claim on cluster {chunkId} survived the grow");
            }

            Assert.That(set.ClaimedSlots(50_000), Is.EqualTo(1UL << 17), "the claim that forced the grow is itself recorded");
            Assert.That(set.Count, Is.EqualTo(Distinct + 1));
        });

        // And the touched list must have grown with it, or Clear would walk a stale array.
        set.Clear();
        Assert.Multiple(() =>
        {
            Assert.That(set.Count, Is.EqualTo(0));
            Assert.That(set.ClaimedSlots(50_000), Is.EqualTo(0UL), "the far claim is cleared too");
            for (var chunkId = 0; chunkId < Distinct; chunkId++)
            {
                Assert.That(set.ClaimedSlots(chunkId), Is.EqualTo(0UL), $"cluster {chunkId} was reached by Clear's walk of a GROWN touched list");
            }
        });
    }
}
