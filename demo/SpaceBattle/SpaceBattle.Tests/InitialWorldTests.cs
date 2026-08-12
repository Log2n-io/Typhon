using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class InitialWorldTests
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
    public void Run_CreatesOneRunAndDeterministicStagingShips()
    {
        var definition = CreateDefinition(shipCount: 8);
        var databaseLocation = Path.Combine(_temporaryDirectory, "initial-world.typhon");
        var observations = new RecordingObservationSink();

        var result = SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            observations);

        var persisted = SpaceBattleHost.ReadSnapshot(definition, databaseLocation);

        Assert.Multiple(() =>
        {
            Assert.That(result.ShipCount, Is.EqualTo(8));
            Assert.That(result.StartupAction, Is.EqualTo(SimulationStartupAction.Initialized));
            Assert.That(persisted.RunCount, Is.EqualTo(1));
            Assert.That(persisted.Run.Seed, Is.EqualTo(definition.Seed));
            Assert.That(persisted.Run.RulesetVersion, Is.EqualTo(definition.RulesetVersion));
            Assert.That(persisted.Run.CompletedTicks, Is.Zero);
            Assert.That(persisted.Run.InitialShipCount, Is.EqualTo(8));
            Assert.That(persisted.Run.AliveShipCount, Is.EqualTo(8));
            Assert.That(persisted.Run.ProcessSegment, Is.EqualTo(1));
            Assert.That(persisted.Run.Status, Is.EqualTo(SimulationRunStatus.Running));
            Assert.That(persisted.Ships, Has.Count.EqualTo(8));
            Assert.That(observations.Items, Has.Exactly(1).TypeOf<InitializationCompleted>());
        });

        foreach (var ship in persisted.Ships)
        {
            Assert.Multiple(() =>
            {
                Assert.That(ship.Position.X, Is.InRange(0f, definition.WorldSize).And.LessThan(definition.WorldSize));
                Assert.That(ship.Position.Y, Is.InRange(0f, definition.WorldSize).And.LessThan(definition.WorldSize));
                Assert.That(ship.Position.Z, Is.InRange(0f, definition.WorldSize).And.LessThan(definition.WorldSize));
                Assert.That(ship.Bounds.MinX, Is.EqualTo(ship.Position.X));
                Assert.That(ship.Bounds.MinY, Is.EqualTo(ship.Position.Y));
                Assert.That(ship.Bounds.MinZ, Is.EqualTo(ship.Position.Z));
                Assert.That(ship.Bounds.MaxX, Is.EqualTo(ship.Position.X));
                Assert.That(ship.Bounds.MaxY, Is.EqualTo(ship.Position.Y));
                Assert.That(ship.Bounds.MaxZ, Is.EqualTo(ship.Position.Z));
                Assert.That(ship.Motion.DirectionX, Is.EqualTo(1f));
                Assert.That(ship.Motion.DirectionY, Is.Zero);
                Assert.That(ship.Motion.DirectionZ, Is.Zero);
                Assert.That(ship.Motion.Speed, Is.Zero);
                Assert.That(ship.Health, Is.EqualTo(definition.MaximumHealth));
                Assert.That(ship.Mode, Is.EqualTo(BehaviorMode.Staging));
                Assert.That(ship.ModeTicksRemaining, Is.EqualTo(definition.StagingTicks));
                Assert.That(ship.TrackingTargetIsNull, Is.True);
                Assert.That(ship.WeaponEnabled, Is.False);
                Assert.That(ship.AfterburnerEnabled, Is.False);
            });
        }
    }

    [Test]
    public void Run_WithSameDefinition_CreatesIdenticalPersistentSnapshots()
    {
        var definition = CreateDefinition(shipCount: 12);
        var firstLocation = Path.Combine(_temporaryDirectory, "first.typhon");
        var secondLocation = Path.Combine(_temporaryDirectory, "second.typhon");

        SpaceBattleHost.Run(
            definition,
            firstLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        SpaceBattleHost.Run(
            definition,
            secondLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        var first = SpaceBattleHost.ReadSnapshot(definition, firstLocation);
        var second = SpaceBattleHost.ReadSnapshot(definition, secondLocation);

        Assert.Multiple(() =>
        {
            Assert.That(second.RunCount, Is.EqualTo(first.RunCount));
            Assert.That(second.Run, Is.EqualTo(first.Run));
            Assert.That(second.Ships, Is.EqualTo(first.Ships).AsCollection);
        });
    }

    [Test]
    public void Run_WhenCancelledDuringBulkLoad_RejectsTheExistingEmptyDatabase()
    {
        var definition = CreateDefinition(shipCount: 20);
        var databaseLocation = Path.Combine(_temporaryDirectory, "cancelled.typhon");
        using var cancellation = new CancellationTokenSource();
        var cancellingSink = new CancellingObservationSink(cancellation);

        Assert.Throws<OperationCanceledException>(() => SpaceBattleHost.Run(
            definition,
            databaseLocation,
            cancellation.Token,
            cancellingSink));

        Assert.Throws<InvalidOperationException>(() => SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink()));
    }

    private static SimulationDefinition CreateDefinition(int shipCount) => new(
        runName: "test",
        shipCount: shipCount,
        seed: SimulationDefinition.DefaultSeed,
        rulesetVersion: 1,
        worldSize: 1_000f,
        maximumHealth: 1_000,
        stagingTicks: 250,
        spatialCellSize: 100f,
        spatialMargin: 20f);

    private sealed class RecordingObservationSink : ISpaceBattleObservationSink
    {
        public List<SpaceBattleObservation> Items { get; } = [];

        public void Publish(SpaceBattleObservation observation) => Items.Add(observation);
    }

    private sealed class CancellingObservationSink(CancellationTokenSource cancellation)
        : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
            if (observation is InitializationProgress)
            {
                cancellation.Cancel();
            }
        }
    }
}
