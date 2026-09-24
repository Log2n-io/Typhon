using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// <see cref="ReplicationOccupancy"/> against a dictionary: open addressing with backward-shift deletion is easy to get subtly wrong where a probe run
/// wraps the table's end or a deletion moves an entry past its home.
/// </summary>
[TestFixture]
class ReplicationOccupancyTests
{
    [Test]
    public void RandomAddsAndRemovesMatchADictionary([Values(1, 2, 3)] int seed)
    {
        var map = new ReplicationOccupancy();
        var reference = new Dictionary<ulong, int>();
        var random = new Random(seed);

        // Few distinct keys, so cells are emptied and refilled constantly and probe runs collide; enough of them to force a grow mid-run.
        const int Keys = 3000;
        for (var op = 0; op < 200_000; op++)
        {
            var key = PushReplication.Key(random.Next(Keys), random.Next(3));
            var held = reference.GetValueOrDefault(key);
            var delta = held == 0 || random.Next(3) != 0 ? random.Next(1, 4) : -random.Next(1, held + 1);
            map.Add(key, delta);
            if (held + delta == 0)
            {
                reference.Remove(key);
            }
            else
            {
                reference[key] = held + delta;
            }

            if (op % 20_000 == 0)
            {
                AssertSame(map, reference);
            }
        }

        AssertSame(map, reference);
        Assert.That(map.Underflows, Is.Zero);
    }

    [Test]
    public void AnUnderflowIsCountedAndLeavesNoEntry()
    {
        var map = new ReplicationOccupancy();
        map.Add(7, 1);
        map.Add(7, -2);
        map.Add(9, -1);

        Assert.Multiple(() =>
        {
            Assert.That(map.Underflows, Is.EqualTo(2));
            Assert.That(map.Count, Is.Zero);
            Assert.That(map.Get(7), Is.Zero);
        });
    }

    private static void AssertSame(ReplicationOccupancy map, Dictionary<ulong, int> reference)
    {
        Assert.That(map.Count, Is.EqualTo(reference.Count), "occupied cells");
        foreach (var (key, count) in reference)
        {
            Assert.That(map.Get(key), Is.EqualTo(count), $"cell {key}");
        }

        var copy = new ReplicationOccupancy();
        foreach (var (key, count) in reference)
        {
            copy.Add(key, count);
        }

        Assert.That(map.Differences(copy), Is.Zero, "Differences disagrees with a map holding the same counts");
    }
}
