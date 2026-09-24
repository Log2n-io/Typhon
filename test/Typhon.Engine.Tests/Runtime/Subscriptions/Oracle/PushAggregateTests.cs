using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// The aggregate tier (09 § 8): per-tile counts per archetype, maintained from the push step's events and sent in <c>AGG</c> blocks.
/// </summary>
[TestFixture]
[NonParallelizable]
sealed class PushAggregateTests : TestBase<PushAggregateTests>
{
    /// <summary>
    /// Under the oracle's churn — spawns, destroys, teleports, walkers, migrations — and skipped frames, the counts maintained from events equal a recount
    /// of the blocks every 50 ticks (SUB-24), for a World and for a Sphere profile.
    /// </summary>
    /// <param name="world">A World profile, or a Sphere one.</param>
    /// <param name="skipPercent">The percentage of ticks some sessions' frames are left undrained.</param>
    [Test]
    [VerifiesRule("SUB-24")]
    public void TheCountsEqualARecountUnderChurn([Values] bool world, [Values(0, 60)] int skipPercent)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 8100 + skipPercent, [skipPercent, 0, 30, skipPercent],
            nameof(PushAggregateTests), worldObserver: world, walkRadius: world ? 0 : 3000, aggregateCells: 2);
        oracle.Workload.WalkStrideM = 1.5f;
        var grids = oracle.Push.Aggregates;
        Assert.That(grids, Has.Length.EqualTo(1));

        for (var i = 0; i < 150; i++)
        {
            oracle.Step();
            if ((i + 1) % 50 == 0)
            {
                Assert.That(oracle.Push.AggregateDifferencesForTest(), Is.Zero, $"counts drifted from a recount after {i + 1} ticks");
            }
        }

        var total = 0;
        for (var r = 0; r < grids[0].Rows * grids[0].ArchetypeCount; r++)
        {
            total += grids[0].Counts[r];
        }

        Assert.That(total, Is.GreaterThan(world ? 20 : 100), "the grid counts almost nothing: the case did not run");
    }

    /// <summary>
    /// What a client holds of the aggregate is the server's counts: after churn and a quiet stretch in which every session drains, each session's store
    /// holds, tile for tile and archetype for archetype, what the grid counts — the sums included.
    /// </summary>
    /// <param name="skipPercent">The percentage of ticks some sessions' frames are left undrained.</param>
    [Test]
    [VerifiesRule("SUB-24")]
    public void TheClientsCountsAreTheServers([Values(0, 60)] int skipPercent)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 8200 + skipPercent, [skipPercent, 0, 30, skipPercent],
            nameof(PushAggregateTests), worldObserver: true, aggregateCells: 2);
        for (var i = 0; i < 120; i++)
        {
            oracle.Step();
        }

        oracle.Quiesce();
        var server = oracle.Push.Aggregates[0];
        var plans = oracle.Frames.Subscriptions.Plans;
        Assert.Multiple(() =>
        {
            foreach (var session in oracle.Sessions)
            {
                var client = oracle.Frames.Replica(session).Store.Aggregates[server.GridIdx];
                long serverSum = 0, clientSum = 0;
                for (var r = 0; r < server.Rows; r++)
                {
                    var tile = server.Tiles[r];
                    for (var a = 0; a < plans.Length; a++)
                    {
                        var column = server.Columns[a];
                        if (column < 0)
                        {
                            continue;
                        }

                        var expected = server.CountAt(tile, a);
                        var got = client.Counts.Length == 0 ? 0 : client.Counts[((int)tile * client.ArchetypeCount) + column];
                        Assert.That(got, Is.EqualTo(expected), $"session {session.Value}: tile {tile}, {plans[a].Name}");
                        serverSum += expected;
                        clientSum += got;
                    }
                }

                Assert.That(clientSum, Is.EqualTo(serverSum).And.GreaterThan(0), $"session {session.Value}: the sums");
            }
        });
    }

    /// <summary>
    /// An aggregate is refused where it cannot be served: alone in a profile, beside a second aggregate, over a tile that is not a whole number of cells,
    /// or counting an archetype no profile replicates.
    /// </summary>
    [Test]
    public void AnAggregateThatCannotBeServedIsRefused([Range(0, 3)] int @case)
    {
        // An int, not the shape's name: a test argument lands in the database's file name.
        var shape = new[] { "alone", "two", "fraction", "unreplicated" }[@case];
        var because = new[] { "alone", "two aggregates", "whole number", "no profile replicates" }[@case];
        var cell = ProjectionTestSchema.ReplicationCellFor(100);
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        void Declare(SubscriptionsRegistry subs)
        {
            ProjectionTestSchema.DeclareCreature(subs);
            ProjectionTestSchema.DeclareRock(subs);
            subs.Profile("p", p =>
            {
                if (shape != "alone")
                {
                    p.World().Of<ProjCreature>();
                }

                switch (shape)
                {
                    case "fraction":
                        p.Aggregate(1.5 * cell, 1).Of<ProjCreature>();
                        break;
                    case "unreplicated":
                        p.Aggregate(2 * cell, 1).Of<ProjRock>();
                        break;
                    default:
                        p.Aggregate(2 * cell, 1).Of<ProjCreature>();
                        break;
                }

                if (shape == "two")
                {
                    p.Aggregate(4 * cell, 1).Of<ProjCreature>();
                }
            });
        }

        var ex = Assert.Catch<System.Exception>(() => FrameHarness.Create(dbe, Declare, nameof(PushAggregateTests) + shape, replicationCellM: cell).Dispose());
        Assert.That(ex!.Message, Does.Contain(because));
    }
}
