using System.Collections.Generic;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #954 — network identity allocation and the generation that makes reuse observable (SUB-06).
/// </summary>
/// <remarks>
/// Three properties carry the weight. The generation must change on reuse, or a session silently mistakes a replacement entity for the original. The
/// identity space must stay bounded by the PEAK watched count rather than by total allocations. And a double release must be rejected: collapsing "live" and
/// "end of free list" onto one sentinel makes the second release thread an identity to itself, after which every allocation returns that same id — one
/// identity held by two live entities, with no error anywhere.
/// </remarks>
[TestFixture]
class NetIdAllocatorTests
{
    [Test]
    public void AFreshAllocatorIssuesIdentitiesFromOneAndNeverZero()
    {
        var allocator = new NetIdAllocator();

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
        var allocator = new NetIdAllocator();
        var seen = new HashSet<uint>();

        for (var i = 0; i < 1_000; i++)
        {
            Assert.That(seen.Add(allocator.Allocate()), Is.True, $"identity {i} was issued twice while live");
        }

        Assert.That(allocator.LiveCount, Is.EqualTo(1_000));
    }

    /// <summary>
    /// SUB-06: a session that missed the release must still resolve the reuse as leave-then-enter, which it can only do if the pair changed.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-06")]
    public void ReusingAnIdentityBumpsItsGeneration()
    {
        var allocator = new NetIdAllocator();
        var first = allocator.Allocate();
        var generationWhileFirstHeld = allocator.GenerationOf(first);

        allocator.Release(first);
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
        var allocator = new NetIdAllocator();
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
        var allocator = new NetIdAllocator(initialCapacity: 256);
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
        var allocator = new NetIdAllocator();
        allocator.Allocate();

        Assert.Multiple(() =>
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => allocator.Release(NetIdAllocator.NoNetId));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => allocator.Release(500));
        });
    }

    /// <summary>
    /// The critical case. A second release of a live-then-freed identity must be refused: unrefused, it threads the identity to itself and every subsequent
    /// allocation returns it, so two entities hold one identity while the live count runs backwards.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-06")]
    public void ReleasingTheSameIdentityTwiceIsRejected()
    {
        var allocator = new NetIdAllocator();
        var a = allocator.Allocate();
        var b = allocator.Allocate();
        var c = allocator.Allocate();

        allocator.Release(a);

        Assert.Throws<System.ArgumentException>(() => allocator.Release(a), "a is already free");

        // The free list must be intact and the live count truthful afterwards.
        var reissued = new List<uint> { allocator.Allocate(), allocator.Allocate() };

        Assert.Multiple(() =>
        {
            Assert.That(allocator.LiveCount, Is.EqualTo(4), "b, c and two fresh identities");
            Assert.That(reissued[0], Is.Not.EqualTo(reissued[1]), "the free list must not have become a cycle");
            Assert.That(reissued, Does.Not.Contain(b));
            Assert.That(reissued, Does.Not.Contain(c), "a live identity must never be reissued");
        });
    }

    /// <summary>
    /// A double release of an identity deeper in the free list corrupts the tail rather than the head, so it needs its own case.
    /// </summary>
    [Test]
    public void ReleasingAnIdentityDeepInTheFreeListTwiceIsRejected()
    {
        var allocator = new NetIdAllocator();
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

        // Every identity must come back exactly once.
        var reissued = new HashSet<uint>();
        for (var i = 0; i < ids.Count; i++)
        {
            Assert.That(reissued.Add(allocator.Allocate()), Is.True, $"identity {i} came back twice — the free list is a cycle");
        }

        Assert.That(reissued, Is.EquivalentTo(ids), "the same five identities, no more and no fewer");
    }

    /// <summary>
    /// Release-then-allocate one at a time never puts more than one identity on the free list, because LIFO hands back the id just freed. Releasing a batch
    /// first is what actually exercises the list's links.
    /// </summary>
    [Test]
    public void ABatchOfReleasedIdentitiesAllComeBackExactlyOnce()
    {
        var allocator = new NetIdAllocator();
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
    /// The property that keeps this bounded by the watched set: churn at a stable watched count must not grow the identity space. An allocator that bumped a
    /// counter per allocation would pass every other test here and still grow without bound.
    /// </summary>
    [Test]
    public void ChurnAtAStablePopulationDoesNotGrowTheIdentitySpace()
    {
        var allocator = new NetIdAllocator();
        var live = new List<uint>();

        for (var i = 0; i < 200; i++)
        {
            live.Add(allocator.Allocate());
        }

        var peak = allocator.HighWaterMark;
        var capacityAtPeak = allocator.Capacity;

        // Twenty full turnovers, each releasing the whole set before re-allocating it, so the free list actually reaches depth 200.
        for (var round = 0; round < 20; round++)
        {
            foreach (var id in live)
            {
                allocator.Release(id);
            }

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
        var allocator = new NetIdAllocator(initialCapacity: 4);

        var early = new List<uint> { allocator.Allocate(), allocator.Allocate(), allocator.Allocate() };
        allocator.Release(early[0]);
        allocator.Release(early[1]);
        var generationAfterRelease = allocator.GenerationOf(early[0]);
        Assert.That(generationAfterRelease, Is.Not.Zero);

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
}
