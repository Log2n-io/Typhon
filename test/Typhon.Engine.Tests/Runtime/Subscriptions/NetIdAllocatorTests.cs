using System.Collections.Generic;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #954 / #955 follow-up — network identity allocation, the generation that makes reuse observable, and the one-tick quarantine that keeps a reused identity
/// out of the frame that reported its predecessor's leave (SUB-06).
/// </summary>
/// <remarks>
/// Four properties carry the weight. The generation must change on reuse, or a session silently mistakes a replacement entity for the original. An identity
/// must not come back in the tick it was released, or one frame carries the new entity's enter and the old one's leave and the client applies them in the
/// wrong order. The identity space must stay bounded by the PEAK watched count rather than by total allocations. And a double release must be rejected:
/// collapsing "live" and "end of list" onto one sentinel makes the second release thread an identity to itself, after which every allocation returns that same
/// id — one identity held by two live entities, with no error anywhere.
/// </remarks>
[TestFixture]
class NetIdAllocatorTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp() => _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "NetIdAllocatorTests" });

    [TearDown]
    public void TearDown() => _registry?.Dispose();

    private NetIdAllocator NewAllocator(int initialCapacity = 256, int quarantineTicks = 1)
        => new("NetIds", _registry.Runtime, initialCapacity, quarantineTicks);

    [Test]
    public void AFreshAllocatorIssuesIdentitiesFromOneAndNeverZero()
    {
        using var allocator = NewAllocator();

        Assert.Multiple(() =>
        {
            Assert.That(allocator.LiveCount, Is.Zero);
            Assert.That(allocator.HighWaterMark, Is.Zero);
            Assert.That(allocator.Allocate(), Is.EqualTo(1u), "zero is reserved for 'no identity'");
            Assert.That(allocator.Allocate(), Is.EqualTo(2u));
            Assert.That(allocator.LiveCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void LiveIdentitiesAreDistinct()
    {
        using var allocator = NewAllocator();
        var seen = new HashSet<uint>();

        for (var i = 0; i < 1_000; i++)
        {
            Assert.That(seen.Add(allocator.Allocate()), Is.True, $"identity {i} was issued twice while live");
        }

        Assert.That(allocator.LiveCount, Is.EqualTo(1_000));
    }

    /// <summary>
    /// The quarantine's reason for existing, and the case that would otherwise be invisible until a client misrendered it. A free list handing back the
    /// identity just released means one frame can carry the leave of its old holder and the enter of its new one; the wire applies leaves LAST, so the leave
    /// lands on the entity that just entered. The generation cannot rescue that — both records share a tick, so the client has no ordering to recover.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-06")]
    public void AnIdentityIsNotReissuedInTheTickItWasReleased()
    {
        using var allocator = NewAllocator();
        var a = allocator.Allocate();
        var b = allocator.Allocate();

        allocator.Release(a);

        var next = allocator.Allocate();

        Assert.Multiple(() =>
        {
            Assert.That(next, Is.Not.EqualTo(a), "an identity released this tick must not come back in the same tick");
            Assert.That(next, Is.Not.EqualTo(b), "nor may a live identity be reissued");
            Assert.That(allocator.QuarantinedCount, Is.EqualTo(1), "it is held, not lost");
        });
    }

    [Test]
    public void DrainingTheQuarantineMakesReleasedIdentitiesReissuable()
    {
        using var allocator = NewAllocator();
        var a = allocator.Allocate();
        allocator.Release(a);

        allocator.DrainQuarantine();

        Assert.Multiple(() =>
        {
            Assert.That(allocator.QuarantinedCount, Is.Zero);
            Assert.That(allocator.Allocate(), Is.EqualTo(a), "after the tick boundary the identity recycles, keeping the space dense");
        });
    }

    /// <summary>
    /// The allocator must not raid the quarantine when the free list runs dry: minting is the correct answer, because reaching in is exactly the same-tick
    /// reuse the quarantine exists to prevent.
    /// </summary>
    [Test]
    public void AnEmptyFreeListMintsRatherThanRaidingTheQuarantine()
    {
        using var allocator = NewAllocator();
        var a = allocator.Allocate();
        allocator.Release(a);

        var minted = allocator.Allocate();

        Assert.Multiple(() =>
        {
            Assert.That(minted, Is.GreaterThan(a), "a fresh identity, not the quarantined one");
            Assert.That(allocator.HighWaterMark, Is.EqualTo(minted), "the high-water mark rises by exactly the one minted");
            Assert.That(allocator.QuarantinedCount, Is.EqualTo(1), "and the quarantined identity is still waiting");
        });
    }

    /// <summary>
    /// SUB-06: a session that missed the release must still resolve the reuse as leave-then-enter, which it can only do if the pair changed.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-06")]
    public void ReusingAnIdentityBumpsItsGeneration()
    {
        using var allocator = NewAllocator();
        var first = allocator.Allocate();
        var generationWhileFirstHeld = allocator.GenerationOf(first);

        allocator.Release(first);
        allocator.DrainQuarantine();
        var reused = allocator.Allocate();

        Assert.Multiple(() =>
        {
            Assert.That(reused, Is.EqualTo(first), "a released identity should be recycled, keeping the space dense");
            Assert.That(allocator.GenerationOf(reused), Is.Not.EqualTo(generationWhileFirstHeld),
                "an unchanged pair would read to a session as the same entity continuing, not as a replacement");
        });
    }

    [Test]
    public void ReleaseBumpsTheGenerationEvenWhenTheIdentityIsNeverReissued()
    {
        using var allocator = NewAllocator();
        var netId = allocator.Allocate();
        var before = allocator.GenerationOf(netId);

        allocator.Release(netId);

        Assert.That(allocator.GenerationOf(netId), Is.Not.EqualTo(before),
            "an entity that leaves and is never replaced must still read as gone to a session holding the old pair");
    }

    /// <summary>
    /// The identity probed here is inside the allocated array but above the high-water mark, so this actually reads the generation store. Probing far past
    /// the array would exit at the bounds guard instead, and any implementation would pass.
    /// </summary>
    [Test]
    public void AnIdentityThatWasNeverIssuedHasGenerationZero()
    {
        using var allocator = NewAllocator(initialCapacity: 256);
        allocator.Allocate();

        Assert.Multiple(() =>
        {
            Assert.That(allocator.HighWaterMark, Is.EqualTo(1u));
            Assert.That(allocator.Capacity, Is.GreaterThan(64), "the probe below must land inside the array, not past it");
            Assert.That(allocator.GenerationOf(64), Is.Zero, "issued nothing, so no generation");
            Assert.That(allocator.GenerationOf(NetIdAllocator.NoNetId), Is.Zero, "the reserved identity carries no generation");
        });
    }

    [Test]
    public void ReleasingSomethingNeverIssuedIsRejected()
    {
        using var allocator = NewAllocator();
        allocator.Allocate();

        Assert.Multiple(() =>
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => allocator.Release(NetIdAllocator.NoNetId));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => allocator.Release(500));
        });
    }

    /// <summary>
    /// The critical case. A second release of a live-then-freed identity must be refused: unrefused, it threads the identity to itself and every subsequent
    /// allocation returns it, so two entities hold one identity while the live count runs backwards. It must be refused while the identity is still
    /// QUARANTINED too, which is the window the drain has not yet closed.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-06")]
    public void ReleasingTheSameIdentityTwiceIsRejected()
    {
        using var allocator = NewAllocator();
        var a = allocator.Allocate();
        var b = allocator.Allocate();
        var c = allocator.Allocate();

        allocator.Release(a);

        Assert.Throws<System.ArgumentException>(() => allocator.Release(a), "a is quarantined, not live");

        allocator.DrainQuarantine();

        Assert.Throws<System.ArgumentException>(() => allocator.Release(a), "and still not live once it is merely free");

        // The lists must be intact and the live count truthful afterwards.
        var reissued = new List<uint> { allocator.Allocate(), allocator.Allocate() };

        Assert.Multiple(() =>
        {
            Assert.That(allocator.LiveCount, Is.EqualTo(4), "b, c and two more");
            Assert.That(reissued[0], Is.Not.EqualTo(reissued[1]), "the free list must not have become a cycle");
            Assert.That(reissued, Does.Not.Contain(b));
            Assert.That(reissued, Does.Not.Contain(c), "a live identity must never be reissued");
        });
    }

    /// <summary>
    /// A double release of an identity deeper in the list corrupts the tail rather than the head, so it needs its own case.
    /// </summary>
    [Test]
    public void ReleasingAnIdentityDeepInTheFreeListTwiceIsRejected()
    {
        using var allocator = NewAllocator();
        var ids = new List<uint>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(allocator.Allocate());
        }

        foreach (var id in ids)
        {
            allocator.Release(id);
        }

        Assert.Throws<System.ArgumentException>(() => allocator.Release(ids[0]), "the first released identity is deepest in the list");

        allocator.DrainQuarantine();

        // Every identity must come back exactly once.
        var reissued = new HashSet<uint>();
        for (var i = 0; i < ids.Count; i++)
        {
            Assert.That(reissued.Add(allocator.Allocate()), Is.True, $"identity {i} came back twice — the list is a cycle");
        }

        Assert.That(reissued, Is.EquivalentTo(ids), "the same five identities, no more and no fewer");
    }

    /// <summary>
    /// Releasing a batch before draining is what actually exercises the quarantine's links; one at a time would never put more than a single entry on it.
    /// </summary>
    [Test]
    public void ABatchOfReleasedIdentitiesAllComeBackExactlyOnce()
    {
        using var allocator = NewAllocator();
        var ids = new List<uint>();
        for (var i = 0; i < 64; i++)
        {
            ids.Add(allocator.Allocate());
        }

        foreach (var id in ids)
        {
            allocator.Release(id);
        }

        Assert.That(allocator.LiveCount, Is.Zero);
        Assert.That(allocator.QuarantinedCount, Is.EqualTo(64));

        allocator.DrainQuarantine();

        var reissued = new HashSet<uint>();
        for (var i = 0; i < ids.Count; i++)
        {
            Assert.That(reissued.Add(allocator.Allocate()), Is.True, $"allocation {i} repeated an identity still on the list");
        }

        Assert.Multiple(() =>
        {
            Assert.That(reissued, Is.EquivalentTo(ids), "recycling must return exactly the released set");
            Assert.That(allocator.HighWaterMark, Is.EqualTo((uint)ids.Count), "and must not have minted anything new");
        });
    }

    /// <summary>
    /// The property that keeps this bounded by the live population: churn at a stable live count must not grow the identity space. An allocator that bumped a
    /// counter per allocation would pass every other test here and still grow without bound.
    /// </summary>
    /// <remarks>
    /// The drain between release and re-allocation is the production pattern, not a test convenience: it runs once per tick, before the replication track
    /// dispatches. Without it each round would mint a fresh set and the high-water mark would climb with total allocations rather than with the peak — which
    /// is precisely the unbounded growth SUB-13 forbids, so this test also pins that the drain is actually wired.
    /// </remarks>
    [Test]
    public void ChurnAtAStablePopulationDoesNotGrowTheIdentitySpace()
    {
        using var allocator = NewAllocator();
        var live = new List<uint>();

        for (var i = 0; i < 200; i++)
        {
            live.Add(allocator.Allocate());
        }

        var peak = allocator.HighWaterMark;
        var capacityAtPeak = allocator.Capacity;

        // Twenty full turnovers, each a tick: release the whole set, cross the tick boundary, then re-allocate it.
        for (var round = 0; round < 20; round++)
        {
            foreach (var id in live)
            {
                allocator.Release(id);
            }

            allocator.DrainQuarantine();

            for (var i = 0; i < live.Count; i++)
            {
                live[i] = allocator.Allocate();
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(allocator.LiveCount, Is.EqualTo(200), "the watched count never changed");
            Assert.That(allocator.HighWaterMark, Is.EqualTo(peak), "4 000 allocations at a stable population must not raise the high-water mark");
            Assert.That(allocator.Capacity, Is.EqualTo(capacityAtPeak), "and must not grow the side arrays");
            Assert.That(live, Is.Unique, "and every live identity must still be distinct");
        });
    }

    /// <summary>
    /// Growth has to carry both side arrays. The free list is deliberately left non-empty across the growth so that severing its links would be detected —
    /// with an empty list the copy is unobservable.
    /// </summary>
    [Test]
    public void GrowthPreservesGenerationsAndTheFreeList()
    {
        using var allocator = NewAllocator(initialCapacity: 4);

        var early = new List<uint> { allocator.Allocate(), allocator.Allocate(), allocator.Allocate() };
        allocator.Release(early[0]);
        allocator.Release(early[1]);
        var generationAfterRelease = allocator.GenerationOf(early[0]);
        Assert.That(generationAfterRelease, Is.Not.Zero);

        allocator.DrainQuarantine();

        // Force several growths while two identities sit on the free list.
        var seen = new HashSet<uint>();
        for (var i = 0; i < 64; i++)
        {
            Assert.That(seen.Add(allocator.Allocate()), Is.True, $"allocation {i} repeated a live identity across a growth");
        }

        Assert.Multiple(() =>
        {
            Assert.That(allocator.Capacity, Is.GreaterThan(4));
            Assert.That(allocator.GenerationOf(early[0]), Is.EqualTo(generationAfterRelease), "a rehoused generation array must carry its history across");
            Assert.That(seen, Does.Contain(early[0]), "the identities parked on the free list must survive growth and be reissued");
            Assert.That(seen, Does.Contain(early[1]));
        });
    }

    /// <summary>
    /// The quarantine threads through the same links the free list does, so a growth that happens while entries are still quarantined must carry them too.
    /// Severing them here would strand the identities silently — they would never come back, and only the high-water mark would drift to show it.
    /// </summary>
    [Test]
    public void GrowthPreservesTheQuarantine()
    {
        using var allocator = NewAllocator(initialCapacity: 4);

        var early = new List<uint> { allocator.Allocate(), allocator.Allocate(), allocator.Allocate() };
        allocator.Release(early[0]);
        allocator.Release(early[1]);

        // Grow WITHOUT draining, so the quarantine's links are what must survive the copy.
        for (var i = 0; i < 64; i++)
        {
            allocator.Allocate();
        }

        Assert.That(allocator.Capacity, Is.GreaterThan(4));
        Assert.That(allocator.QuarantinedCount, Is.EqualTo(2), "still held across the growth");

        allocator.DrainQuarantine();

        var reissued = new HashSet<uint> { allocator.Allocate(), allocator.Allocate() };

        Assert.That(reissued, Is.EquivalentTo(early.GetRange(0, 2)), "both quarantined identities must survive growth and come back");
    }

    /// <summary>
    /// D1: an identity released at tick N is held for the whole window a session may be skipped for, not for one tick.
    /// </summary>
    /// <remarks>
    /// The one-tick hold keeps a leave and an enter out of the same FRAME. It does not keep them out of the same frame of a SKIPPED session, which receives
    /// everything since its baseline in one message: with a one-tick hold, an identity released at N and reissued at N+1 reaches such a session as a leave and
    /// an enter for the same number, in one frame, with no ordering it can recover. Holding for the skip-close window makes that unrepresentable.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-06")]
    public void AReleasedIdentityIsHeldForTheSkipWindow()
    {
        const int Window = 5;
        using var allocator = NewAllocator(quarantineTicks: Window);

        var identity = allocator.Allocate();
        allocator.Release(identity);

        Assert.Multiple(() =>
        {
            Assert.That(allocator.QuarantineTicks, Is.EqualTo(Window));

            for (var tick = 1; tick < Window; tick++)
            {
                allocator.DrainQuarantine();
                Assert.That(allocator.QuarantinedCount, Is.EqualTo(1), $"still held {tick} tick(s) after the release");
                Assert.That(allocator.Allocate(), Is.Not.EqualTo(identity), $"reissued {tick} tick(s) into a {Window}-tick window");
            }

            allocator.DrainQuarantine();
            Assert.That(allocator.QuarantinedCount, Is.Zero, "the window has passed");
            Assert.That(allocator.Allocate(), Is.EqualTo(identity), "and the identity is reissuable again, rather than lost");
        });
    }

    /// <summary>Releases spread across the window come back on their own schedule, so the ring cannot bunch them or drop one.</summary>
    [Test]
    public void EachTicksReleasesComeBackOnTheirOwnSchedule()
    {
        const int Window = 3;
        using var allocator = NewAllocator(quarantineTicks: Window);

        var first = allocator.Allocate();
        var second = allocator.Allocate();

        allocator.Release(first);
        allocator.DrainQuarantine();
        allocator.Release(second);

        for (var tick = 0; tick < Window - 1; tick++)
        {
            allocator.DrainQuarantine();
        }

        Assert.Multiple(() =>
        {
            Assert.That(allocator.QuarantinedCount, Is.EqualTo(1), "the second release is still inside its own window");
            Assert.That(allocator.Allocate(), Is.EqualTo(first), "the first release has served its window and comes back first");

            allocator.DrainQuarantine();
            Assert.That(allocator.QuarantinedCount, Is.Zero);
            Assert.That(allocator.Allocate(), Is.EqualTo(second));
        });
    }
}
