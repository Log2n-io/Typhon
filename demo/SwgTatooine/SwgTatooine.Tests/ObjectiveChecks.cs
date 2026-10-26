using System;
using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// The crowd's lair objectives (lair-objectives.md; plan G1): the census that sends fighters where lairs are left unattended, and fighters that walk to
/// their lair, stop in range of it, and stay while it stands.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class ObjectiveChecks
{
    /// <summary>L-2: a point of interest's weight is its lairs over its players plus one, and nothing where no lair stands.</summary>
    [Test]
    public void TheWeight_IsLairsOverPlayersPlusOne()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LairCensus.WeightOf(4, 3), Is.EqualTo(1f));
            Assert.That(LairCensus.WeightOf(3, 0), Is.EqualTo(3f));
            Assert.That(LairCensus.WeightOf(0, 0), Is.Zero, "a point of interest with no standing lair has weight");
            Assert.That(LairCensus.WeightOf(2, -1), Is.EqualTo(2f), "a negative player count raised the weight");
        });
    }

    /// <summary>L-2: near the fighter, an unattended point of interest beats a crowded one; a far one is drawn rarely; one with no lair never.</summary>
    [Test]
    public void TheDraw_FavoursNearAndUnattended()
    {
        var pois = new List<(float X, float Z, float Radius)> { (100f, 0f, 50f), (0f, 100f, 50f), (8000f, 0f, 50f), (-100f, 0f, 50f) };
        var census = new LairCensus(4);
        census.Weight[0] = 4f / (1 + 3);   // near, crowded
        census.Weight[1] = 4f;             // near, unattended
        census.Weight[2] = 4f;             // far, unattended
        census.Weight[3] = 0f;             // near, no lair
        var drawn = new int[4];
        for (var n = 0; n < 10_000; n++)
        {
            drawn[census.DrawNear(pois, 0f, 0f, 2000f, (n + 0.5f) / 10_000f)]++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(drawn[3], Is.Zero, "a point of interest with no standing lair was drawn");
            Assert.That(drawn[1], Is.GreaterThan(drawn[0] * 3), "the unattended one is not preferred to the crowded one");
            Assert.That(drawn[2], Is.LessThan(drawn[1] / 10), "the far one is drawn as often as a near one");
            Assert.That(drawn[2], Is.GreaterThan(0), "the far one is never drawn");
            Assert.That(new LairCensus(2).DrawNear(pois, 0f, 0f, 2000f, 0.5f), Is.EqualTo(-1));
        });
    }

    /// <summary>
    /// L-1, L-3, L-4 over a five-minute run: the census counts every point of interest's lairs as its site does; fighters take objectives and see them
    /// fall; and on every once-a-second check of a fighter standing at its stand-off point, its standing lair is within weapon range — never out of it.
    /// </summary>
    [Test]
    [CancelAfter(240_000)]
    public void AFighter_WalksToItsLair_AndStaysInRangeWhileItStands()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            using var sim = new TatooineSim(Causality.LongEnough(dir, 3_000));
            sim.Initialize();
            sim.Run();

            var census = sim.Bridge.CensusOf(0);
            Assert.That(census, Is.Not.Null, "no census was ever taken");

            // Counted here from the lairs themselves — not from the site's own counter, which is what the census reads.
            var standing = new int[sim.Index.Pois.Count];
            using (var tx = sim.Dbe.CreateQuickTransaction())
            {
                foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
                {
                    var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
                    var vitals = cluster.GetReadOnlySpan(CreatureLair.Vitals);
                    for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                    {
                        var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                        var site = spawners[slot].Site;
                        if (cluster.Realm.Value == 0 && site >= 1 && site <= standing.Length && SimBridge.IsStandingLair(spawners[slot], vitals[slot]))
                        {
                            standing[site - 1]++;
                        }
                    }
                }
            }

            var b = sim.Bridge;
            Assert.Multiple(() =>
            {
                for (var i = 0; i < standing.Length; i++)
                {
                    // Taken at the last whole second; a lair or two may have been broken, retired or replaced since.
                    Assert.That(census.Lairs[i], Is.EqualTo(standing[i]).Within(2), $"point of interest {i}: the census and the lairs standing there disagree");
                }

                Assert.That(b.ObjectivesTaken, Is.GreaterThan(0), "no fighter took a lair as its objective");
                Assert.That(b.ObjectivesFallen, Is.GreaterThan(0), "no objective fell");
                Assert.That(b.ObjectivesHeldInRange, Is.GreaterThan(0), "no fighter was ever checked at its objective: L-3 was never tested");
                Assert.That(b.ObjectivesOutOfRange, Is.Zero, $"{b.ObjectivesOutOfRange} checks found a fighter out of range of its standing objective");
            });
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }
}
