using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// The ClientRegion observer (09 § 7): a session's region is the convex hull it sent, its anchor; it holds the entities whose v̂ lies in the hull and whose
/// cell it was delivered.
/// </summary>
[TestFixture]
[NonParallelizable]
sealed class PushRegionTests : TestBase<PushRegionTests>
{
    /// <summary>A region 6 km wide over 250 m cells: a 29-cell window, wider than a Sphere window's 16-bit rows.</summary>
    private const double EdgeM = 6000;

    private const double CellM = 250;

    /// <summary>
    /// Under the oracle's churn, with every session panning, reshaping and now and then jumping its region, and frames skipped: at every quiet point each
    /// client holds exactly its region — checked against the entities' true positions with the v̂ margin, and by the shadow oracle against the geometric
    /// known-set on v̂ itself (SUB-16).
    /// </summary>
    /// <param name="skipPercent">The percentage of ticks some sessions' frames are left undrained.</param>
    /// <param name="deep">Serve the flat world with the deep implementation, which must agree with the flat one on it.</param>
    [Test]
    [VerifiesRule("SUB-16")]
    public void EachClientHoldsItsRegionUnderChurnAndMoves([Values(0, 60)] int skipPercent, [Values] bool deep)
    {
        // The deep implementation's window is a cube (W³ ≤ 2 809 cells, 9 cells of extent): the same region over cells twice as wide.
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 7300 + skipPercent, [skipPercent, 0, 30, skipPercent],
            nameof(PushRegionTests), replicationCellM: deep ? 2 * CellM : CellM, forceDeep: deep, regionEdgeM: deep ? 9 * 2 * CellM : EdgeM);
        oracle.Workload.WalkStrideM = 1.5f;
        long required = 0;
        for (var point = 0; point < 4; point++)
        {
            for (var i = 0; i < 40; i++)
            {
                oracle.Step();
            }

            oracle.Quiesce();
            oracle.AssertConverged($"quiet point {point}");
            required += oracle.RequiredAtLastPoint;
        }

        var push = oracle.Push;
        Assert.Multiple(() =>
        {
            Assert.That(push.ShadowIllegal, Is.Zero, "a record the client could not apply");
            Assert.That(push.ShadowMissing, Is.Zero, "an entity in a region's hull and delivered cells that its client did not hold");
            Assert.That(push.ShadowExtra, Is.Zero, "an entity its client held outside its region's hull or delivered cells");
            Assert.That(push.ShadowChecks, Is.GreaterThan(0), "the shadow oracle never ran");
            Assert.That(required, Is.GreaterThan(100), "the regions required almost nothing: the case did not run");
            Assert.That(oracle.RegionJumps, Is.GreaterThan(0), "no region jumped, so no reset was exercised");
            Assert.That(push.RegionResets, Is.GreaterThan(0), "no region change reset its session");
        });
    }

    /// <summary>
    /// A near budget under churn, region moves and skipped frames (SUB-23): at every quiet point each client holds its region's delivered cells exactly (the
    /// shadow oracle, on v̂), at most 1.1 × budget and no more than the estimate says — which is an upper bound — and its aggregate holds the server's count
    /// for every tile of its aggregate region, the hull less the cells the near tier delivered (09 § 8).
    /// </summary>
    /// <param name="skipPercent">The percentage of ticks some sessions' frames are left undrained.</param>
    [Test]
    [VerifiesRule("SUB-23")]
    [VerifiesRule("SUB-24")]
    public void ABudgetedRegionHoldsWholeCellsAndItsAggregateTheRest([Values(0, 60)] int skipPercent)
    {
        const int budget = 12;
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 7600 + skipPercent, [skipPercent, 0, 30, skipPercent],
            nameof(PushRegionTests), replicationCellM: CellM, regionEdgeM: EdgeM, nearBudget: budget, aggregateCells: 2);
        oracle.Workload.WalkStrideM = 1.5f;
        var push = oracle.Push;
        var grid = push.Aggregates[0];
        var creature = oracle.Frames.PlanIndex(nameof(ProjCreature));
        var rock = oracle.Frames.PlanIndex(nameof(ProjRock));
        long aggregated = 0, bound = 0;
        for (var point = 0; point < 4; point++)
        {
            for (var i = 0; i < 40; i++)
            {
                oracle.Step();
            }

            oracle.Quiesce();
            oracle.AssertConverged($"quiet point {point}");
            Assert.That(push.AggregateDifferencesForTest(), Is.Zero, "the near budget's cell counts and the tile counts equal a recount of the blocks");
            foreach (var session in oracle.Sessions)
            {
                var replica = oracle.Frames.Replica(session);
                var held = replica.NetIds(creature).Length + replica.NetIds(rock).Length;
                bound += held > budget ? 1 : 0;
                Assert.That(held, Is.LessThanOrEqualTo(budget * 11 / 10).And.LessThanOrEqualTo(push.RegionHeldOf(session)),
                    $"session {session.Value} at point {point}: within 1.1 × budget, and within the estimate");

                aggregated += RegionAggregateCheck.Assert(push, session, oracle.Frames.Subscriptions.Ingress.RowOf(session).Region, grid,
                    replica.Store.Aggregates[grid.GridIdx], creature, deep: false, $"session {session.Value}, point {point}");
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(push.ShadowIllegal, Is.Zero);
            Assert.That(push.ShadowMissing, Is.Zero);
            Assert.That(push.ShadowExtra, Is.Zero);
            Assert.That(push.ShadowChecks, Is.GreaterThan(0));
            Assert.That(push.RegionCellsUndelivered + bound, Is.GreaterThan(0), "the budget never bound: the case did not run");
            Assert.That(aggregated, Is.GreaterThan(50), "the aggregate regions counted almost nothing");
        });
    }

    /// <summary>A session that has sent no region holds nothing, over a populated world (SUB-16's unplaced clause, for a region).</summary>
    [Test]
    [VerifiesRule("SUB-16")]
    public void ASessionThatSentNoRegionHoldsNothing()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 7400, [0], nameof(PushRegionTests),
            replicationCellM: CellM, regionEdgeM: EdgeM);
        var session = oracle.Frames.OpenSessions(1, OracleHarness.Profile)[0];
        for (var i = 0; i < 20; i++)
        {
            oracle.Step();
            oracle.Frames.Deliver(session);
        }

        var replica = oracle.Frames.Replica(session);
        var creature = oracle.Frames.PlanIndex(nameof(ProjCreature));
        Assert.Multiple(() =>
        {
            Assert.That(replica.NetIds(creature), Is.Empty);
            Assert.That(replica.NetIds(oracle.Frames.PlanIndex(nameof(ProjRock))), Is.Empty);
            Assert.That(oracle.LiveEntityCount, Is.GreaterThan(100), "the world is populated");
            Assert.That(oracle.Frames.Replica(oracle.Sessions[0]).NetIds(creature), Is.Not.Empty, "a session of the same profile that sent one holds entities");
        });
    }

    /// <summary>A region wider than the window bound allows is refused at Start, naming the setting (09 § 7: maxEdgeM ≤ 48 c in a flat grid).</summary>
    [Test]
    [VerifiesRule("SUB-16")]
    public void ARegionWiderThanTheWindowBoundIsRefusedAtStart()
    {
        var engine = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var e = Assert.Throws<System.InvalidOperationException>(() =>
            OracleHarness.Create(engine, seed: 7500, [0], nameof(PushRegionTests), replicationCellM: CellM, regionEdgeM: (48 * CellM) + 1));
        Assert.That(e.Message, Does.Contain("maxEdgeM").And.Contain("48 cells"));
    }
}
