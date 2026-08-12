using System.IO;
using NUnit.Framework;
using Typhon.Engine;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class ShipMembershipViewTests
{
    private string _temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "SpaceBattle.Tests",
            TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public void Create_AfterReopen_RebuildsBothViewsWithNoHistoricalAddedDelta()
    {
        SimulationDefinition definition = CreateDefinition();
        string databaseLocation = Path.Combine(_temporaryDirectory, "recovery.typhon");
        SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        using DatabaseEngine engine = SpaceBattleDatabase.Open(definition, databaseLocation);
        EntityId runEntityId;
        using (Transaction transaction = engine.CreateReadOnlyTransaction())
        {
            runEntityId = transaction.Query<SimulationRunEntity>().Execute().Single();
            Assert.That(
                transaction.Query<Ship>()
                    .WhereField<ShipRunMembershipComponent>(membership => membership.RunEntityKey == runEntityId.EntityKey)
                    .Count(),
                Is.Zero,
                "Transient membership 不应跨数据库重开持久化");
        }

        using ShipMembershipViews views = ShipMembershipViews.RebuildAndCreate(
            engine,
            runEntityId,
            startupFenceTick: 0);

        Assert.Multiple(() =>
        {
            Assert.That(views.RuntimeShips.Count, Is.EqualTo(definition.ShipCount));
            Assert.That(views.CombatShips.Count, Is.EqualTo(definition.ShipCount));
            Assert.That(views.RuntimeShips.GetDelta().Added, Is.Empty);
            Assert.That(views.CombatShips.GetDelta().Added, Is.Empty);
        });

        using Transaction refresh = engine.CreateQuickTransaction();
        views.Refresh(refresh);

        Assert.Multiple(() =>
        {
            Assert.That(views.RuntimeShips.GetDelta().Added, Is.Empty);
            Assert.That(views.CombatShips.GetDelta().Added, Is.Empty);
        });

        views.Dispose();
        using (Transaction disposedRefresh = engine.CreateQuickTransaction())
        {
            Assert.Throws<ObjectDisposedException>(() => views.Refresh(disposedRefresh));
        }

        using ShipMembershipViews replacement = ShipMembershipViews.RebuildAndCreate(
            engine,
            runEntityId,
            startupFenceTick: 0);
        Assert.Multiple(() =>
        {
            Assert.That(replacement.RuntimeShips.Count, Is.EqualTo(definition.ShipCount));
            Assert.That(replacement.CombatShips.Count, Is.EqualTo(definition.ShipCount));
        });
    }

    [Test]
    public void Refresh_TwoViewsTrackSpawnAndDestroyIndependently()
    {
        SimulationDefinition definition = CreateDefinition();
        string databaseLocation = Path.Combine(_temporaryDirectory, "delta.typhon");
        SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        using DatabaseEngine engine = SpaceBattleDatabase.Open(definition, databaseLocation);
        EntityId runEntityId;
        using (Transaction transaction = engine.CreateReadOnlyTransaction())
        {
            runEntityId = transaction.Query<SimulationRunEntity>().Execute().Single();
        }

        using ShipMembershipViews views = ShipMembershipViews.RebuildAndCreate(engine, runEntityId, startupFenceTick: 0);
        EntityId[] spawnedShips = new EntityId[2];
        using (Transaction transaction = engine.CreateQuickTransaction())
        {
            for (var index = 0; index < spawnedShips.Length; index++)
            {
                spawnedShips[index] = SpawnShip(transaction, runEntityId.EntityKey);
            }

            transaction.Commit();
        }

        engine.WriteTickFence(1);
        using (Transaction refresh = engine.CreateQuickTransaction())
        {
            views.Refresh(refresh);
        }

        AssertViewDelta(views.RuntimeShips, definition.ShipCount + 2, added: 2, removed: 0);
        views.RuntimeShips.ClearDelta();
        AssertViewDelta(views.CombatShips, definition.ShipCount + 2, added: 2, removed: 0);
        views.CombatShips.ClearDelta();

        using (Transaction transaction = engine.CreateQuickTransaction())
        {
            foreach (EntityId spawnedShip in spawnedShips)
            {
                transaction.Destroy(spawnedShip);
            }

            transaction.Commit();
        }

        engine.WriteTickFence(2);
        using (Transaction refresh = engine.CreateQuickTransaction())
        {
            views.Refresh(refresh);
        }

        AssertViewDelta(views.RuntimeShips, definition.ShipCount, added: 0, removed: 2);
        views.RuntimeShips.ClearDelta();
        AssertViewDelta(views.CombatShips, definition.ShipCount, added: 0, removed: 2);
    }

    [Test]
    public void RuntimeRefresh_SpawnAndDestroyReachParallelConsumerAndBothViews()
    {
        SimulationDefinition definition = CreateDefinition();
        string databaseLocation = Path.Combine(_temporaryDirectory, "runtime-delta.typhon");
        SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        using DatabaseEngine engine = SpaceBattleDatabase.Open(definition, databaseLocation);
        EntityId runEntityId;
        using (Transaction transaction = engine.CreateReadOnlyTransaction())
        {
            runEntityId = transaction.Query<SimulationRunEntity>().Execute().Single();
        }

        using ShipMembershipViews views = ShipMembershipViews.RebuildAndCreate(engine, runEntityId, startupFenceTick: 0);
        int currentConsumerCount = 0;
        int completedConsumerCount = -1;
        long observedTicks = 0;
        using TyphonRuntime runtime = TyphonRuntime.Create(engine, schedule =>
        {
            Dag dag = schedule.PublicTrack.DeclareDag("ShipMembershipRuntimeTest");
            dag.CallbackSystem("ShipViewRefresh", context =>
            {
                views.RefreshForRuntime(context.Transaction);
                Interlocked.Exchange(ref currentConsumerCount, 0);
            });
            dag.QuerySystem(
                "ParallelShipConsumer",
                context => Interlocked.Add(ref currentConsumerCount, context.Entities.Count),
                after: "ShipViewRefresh",
                input: () => views.CombatShips,
                parallel: true);
            dag.CallbackSystem("CompleteConsumerCount", _ =>
            {
                Volatile.Write(ref completedConsumerCount, Volatile.Read(ref currentConsumerCount));
                Interlocked.Increment(ref observedTicks);
            }, after: "ParallelShipConsumer");
        }, new RuntimeOptions
        {
            BaseTickRate = 100,
            WorkerCount = 2,
        });

        runtime.Start();
        Assert.That(
            SpinWait.SpinUntil(
                () => Volatile.Read(ref observedTicks) > 0 &&
                    Volatile.Read(ref completedConsumerCount) == definition.ShipCount,
                TimeSpan.FromSeconds(5)),
            Is.True);

        EntityId spawnedShip;
        using (Transaction transaction = engine.CreateQuickTransaction())
        {
            spawnedShip = SpawnShip(transaction, runEntityId.EntityKey);
            transaction.Commit();
        }

        Assert.That(
            SpinWait.SpinUntil(
                () => Volatile.Read(ref completedConsumerCount) == definition.ShipCount + 1 &&
                    views.RuntimeShips.Count == definition.ShipCount + 1 &&
                    views.CombatShips.Count == definition.ShipCount + 1 &&
                    views.RuntimeAddedCount == 1 &&
                    views.CombatAddedCount == 1,
                TimeSpan.FromSeconds(5)),
            Is.True);

        using (Transaction transaction = engine.CreateQuickTransaction())
        {
            transaction.Destroy(spawnedShip);
            transaction.Commit();
        }

        Assert.That(
            SpinWait.SpinUntil(
                () => Volatile.Read(ref completedConsumerCount) == definition.ShipCount &&
                    views.RuntimeShips.Count == definition.ShipCount &&
                    views.CombatShips.Count == definition.ShipCount &&
                    views.RuntimeRemovedCount == 1 &&
                    views.CombatRemovedCount == 1,
                TimeSpan.FromSeconds(5)),
            Is.True);
        runtime.Shutdown();

        using Transaction verify = engine.CreateReadOnlyTransaction();
        int authoritativeShipCount = verify.Query<Ship>().Execute().Count;
        Assert.Multiple(() =>
        {
            Assert.That(authoritativeShipCount, Is.EqualTo(definition.ShipCount));
            Assert.That(completedConsumerCount, Is.EqualTo(authoritativeShipCount));
            Assert.That(views.RuntimeShips.Count, Is.EqualTo(authoritativeShipCount));
            Assert.That(views.CombatShips.Count, Is.EqualTo(authoritativeShipCount));
        });
    }

    private static void AssertViewDelta(EcsView<Ship> view, int count, int added, int removed)
    {
        ViewDelta delta = view.GetDelta();
        Assert.Multiple(() =>
        {
            Assert.That(view.Count, Is.EqualTo(count));
            Assert.That(delta.Added.Count, Is.EqualTo(added));
            Assert.That(delta.Removed.Count, Is.EqualTo(removed));
        });
    }

    private static EntityId SpawnShip(Transaction transaction, long runEntityKey)
    {
        PositionComponent position = default;
        SpatialBoundsComponent bounds = default;
        MotionComponent motion = default;
        HealthComponent health = new() { Current = 1_000 };
        BehaviorComponent behavior = default;
        TrackingComponent tracking = default;
        PauseShipCheckpointComponent pauseCheckpoint = default;
        ShipRunMembershipComponent membership = new() { RunEntityKey = runEntityKey };
        return transaction.Spawn<Ship>(
            Ship.Position.Set(in position),
            Ship.SpatialBounds.Set(in bounds),
            Ship.Motion.Set(in motion),
            Ship.Health.Set(in health),
            Ship.Behavior.Set(in behavior),
            Ship.Tracking.Set(in tracking),
            Ship.PauseCheckpoint.Set(in pauseCheckpoint),
            Ship.RunMembership.Set(in membership));
    }

    private static SimulationDefinition CreateDefinition() => new(
        runName: "ship-membership-view-test",
        shipCount: 4,
        seed: SimulationDefinition.DefaultSeed,
        rulesetVersion: 1,
        worldSize: 100f,
        maximumHealth: 1_000,
        stagingTicks: 0,
        spatialCellSize: 100f,
        spatialMargin: 20f);

    private sealed class RecordingObservationSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
