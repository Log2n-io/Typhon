using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Typhon.Client.Tests;

/// <summary>
/// The netId → handle map, which exists because the flat array it replaced was sized by the world's id RANGE and paid
/// for by every session: 2 MB of near-zeroes each, 67 % of a fifty-session bot process's heap, and 42 GB on a box
/// trying to hold a thousand sessions.
/// </summary>
/// <remarks>
/// The sizing tests are the point of the type and the churn test is the point of its implementation: open addressing
/// with backward-shift deletion is only correct if every probe sequence stays unbroken when an entry in the middle of a
/// run is removed, and that is not something reading the code reliably tells you.
/// </remarks>
[TestFixture]
public class NetIdMapTests
{
    /// <summary>
    /// <b>The whole reason this type exists.</b> One entity whose netId is near the top of a large world's range must
    /// cost the smallest table, not an array that reaches the id.
    /// </summary>
    [Test]
    public void AHighNetIdCostsNothingExtra()
    {
        var map = new NetIdMap();
        map.Set(4_000_000, 7);

        Assert.Multiple(() =>
        {
            Assert.That(map.Count, Is.EqualTo(1));
            Assert.That(map.Capacity, Is.EqualTo(64), "the table is sized by occupancy, never by the id");
            Assert.That(map.TryGet(4_000_000, out var v), Is.True);
            Assert.That(v, Is.EqualTo(7));
        });
    }

    /// <summary>
    /// Sized by what is held: 4 000 scattered entities over a 4-million id range must cost thousands of entries, not
    /// millions. The flat array was 16 MB for this case; two arrays of 8 192 are 64 KB.
    /// </summary>
    [Test]
    public void ScatteredIdsCostTheirCountNotTheirRange()
    {
        var map = new NetIdMap();
        var rng = new Random(1234);
        var expected = new Dictionary<uint, int>();

        while (expected.Count < 4000)
        {
            var id = (uint)rng.Next(1, 4_000_000);
            var value = expected.Count + 1;
            if (expected.TryAdd(id, value))
            {
                map.Set(id, value);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(map.Count, Is.EqualTo(4000));
            Assert.That(map.Capacity, Is.LessThanOrEqualTo(8192));
            foreach (var (id, value) in expected)
            {
                Assert.That(map.TryGet(id, out var got), Is.True, $"netId {id} was lost");
                Assert.That(got, Is.EqualTo(value));
            }
        });
    }

    /// <summary>A second Set on the same netId replaces the handle rather than adding an entry — an entity that moved slot.</summary>
    [Test]
    public void SetReplacesAnExistingHandle()
    {
        var map = new NetIdMap();
        map.Set(42, 1);
        map.Set(42, 2);

        Assert.Multiple(() =>
        {
            Assert.That(map.Count, Is.EqualTo(1));
            Assert.That(map.TryGet(42, out var v), Is.True);
            Assert.That(v, Is.EqualTo(2));
        });
    }

    /// <summary>A missing netId, and netId 0 which is the empty sentinel and must never be reported as present.</summary>
    [Test]
    public void AbsentIdsAreNotFound()
    {
        var map = new NetIdMap();
        map.Set(5, 1);

        Assert.Multiple(() =>
        {
            Assert.That(map.TryGet(6, out _), Is.False);
            Assert.That(map.TryGet(0, out _), Is.False);
        });
    }

    /// <summary>Remove takes the entry out, and removing something absent is not an error.</summary>
    [Test]
    public void RemoveTakesTheEntryOut()
    {
        var map = new NetIdMap();
        map.Set(9, 3);
        map.Remove(9);
        map.Remove(9);
        map.Remove(0);

        Assert.Multiple(() =>
        {
            Assert.That(map.Count, Is.Zero);
            Assert.That(map.TryGet(9, out _), Is.False);
        });
    }

    /// <summary>
    /// <b>Backward-shift deletion, against the case it exists for.</b> Keys that all land in one bucket form a probe
    /// run; removing one from the middle must leave every other reachable. With a tombstone-free table this is the
    /// invariant that breaks first, and it breaks silently — the entry is simply never found again.
    /// </summary>
    [Test]
    public void RemovingFromTheMiddleOfAProbeRunKeepsTheRestReachable()
    {
        var map = new NetIdMap();

        // Found rather than assumed: the keys have to share a bucket under the map's own hash, and a hand-picked
        // arithmetic pattern does not — multiples of 2^26 look like they would collide and provably do not, because the
        // fold keeps (k x golden ratio) mod 64, which still varies with k.
        var colliding = CollidingKeys(8, capacity: 64);

        for (var i = 0; i < colliding.Length; i++)
        {
            map.Set(colliding[i], i + 1);
        }

        map.Remove(colliding[3]);

        Assert.Multiple(() =>
        {
            Assert.That(map.TryGet(colliding[3], out _), Is.False);
            for (var i = 0; i < colliding.Length; i++)
            {
                if (i == 3)
                {
                    continue;
                }

                Assert.That(map.TryGet(colliding[i], out var v), Is.True, $"index {i} became unreachable");
                Assert.That(v, Is.EqualTo(i + 1));
            }
        });
    }

    /// <summary>
    /// <b>Churn against a Dictionary oracle</b>, which is what a session watching entities enter and leave for an hour
    /// actually does. 20 000 mixed operations, every one checked both ways: a table that degraded, lost an entry or
    /// resurrected a removed one shows up here and nowhere else.
    /// </summary>
    [Test]
    public void ChurnMatchesADictionary()
    {
        var map = new NetIdMap();
        var oracle = new Dictionary<uint, int>();
        var rng = new Random(20261015);

        for (var op = 0; op < 20_000; op++)
        {
            var id = (uint)rng.Next(1, 3000);
            if (rng.Next(100) < 60)
            {
                var value = op + 1;
                map.Set(id, value);
                oracle[id] = value;
            }
            else
            {
                map.Remove(id);
                oracle.Remove(id);
            }

            Assert.That(map.Count, Is.EqualTo(oracle.Count), $"count diverged after {op + 1} operations");
        }

        Assert.Multiple(() =>
        {
            foreach (var (id, value) in oracle)
            {
                Assert.That(map.TryGet(id, out var got), Is.True, $"netId {id} was lost");
                Assert.That(got, Is.EqualTo(value));
            }

            for (uint id = 1; id < 3000; id++)
            {
                if (!oracle.ContainsKey(id))
                {
                    Assert.That(map.TryGet(id, out _), Is.False, $"netId {id} was removed but is still found");
                }
            }
        });
    }

    /// <summary>Clear forgets everything and keeps the table, because a reconnecting session watches about what it watched before.</summary>
    [Test]
    public void ClearForgetsEverythingAndKeepsTheTable()
    {
        var map = new NetIdMap();
        for (uint id = 1; id <= 200; id++)
        {
            map.Set(id, (int)id);
        }

        var capacity = map.Capacity;
        map.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(map.Count, Is.Zero);
            Assert.That(map.Capacity, Is.EqualTo(capacity));
            Assert.That(map.TryGet(1, out _), Is.False);
        });

        map.Set(7, 7);
        Assert.That(map.TryGet(7, out var v), Is.True, "the table is still usable after a clear");
        Assert.That(v, Is.EqualTo(7));
    }

    /// <summary>
    /// netIds that share a bucket, by the same fold the map uses: multiply by the 32-bit golden ratio and keep the
    /// high bits. Stated here rather than exposed from the map, so a change to its hashing shows up as this test
    /// failing to find a run instead of as a test that silently stops testing collisions.
    /// </summary>
    /// <param name="count">How many colliding netIds to find.</param>
    /// <param name="capacity">The table size they must collide in.</param>
    /// <returns>The netIds.</returns>
    private static uint[] CollidingKeys(int count, int capacity)
    {
        var shift = 32 - System.Numerics.BitOperations.TrailingZeroCount((uint)capacity);
        var found = new List<uint>(count);
        var target = (2654435769u * 1u) >> shift;

        for (uint id = 1; id < 10_000_000 && found.Count < count; id++)
        {
            if ((id * 2654435769u) >> shift == target)
            {
                found.Add(id);
            }
        }

        Assert.That(found, Has.Count.EqualTo(count), "could not find a colliding run — has the map's hash changed?");
        return found.ToArray();
    }

    /// <summary>Growth keeps every entry: a dense run inserted across several doublings must all still be there.</summary>
    [Test]
    public void GrowthKeepsEveryEntry()
    {
        var map = new NetIdMap();
        const int count = 5000;
        for (uint id = 1; id <= count; id++)
        {
            map.Set(id, (int)id * 2);
        }

        Assert.Multiple(() =>
        {
            Assert.That(map.Count, Is.EqualTo(count));
            for (uint id = 1; id <= count; id++)
            {
                Assert.That(map.TryGet(id, out var v), Is.True, $"netId {id} was lost across a growth");
                Assert.That(v, Is.EqualTo((int)id * 2));
            }
        });
    }
}
