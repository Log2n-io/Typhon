using System.IO;
using System.Threading;
using NUnit.Framework;
using Typhon.Engine;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class StartupTests
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
    public void Run_WhenPersistedRunIsRunning_ResumesWithoutCreatingAnotherWorld()
    {
        var definition = CreateDefinition();
        var databaseLocation = Path.Combine(_temporaryDirectory, "running.typhon");

        SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        var resumed = SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        var snapshot = SpaceBattleHost.ReadSnapshot(definition, databaseLocation);

        Assert.Multiple(() =>
        {
            Assert.That(resumed.StartupAction, Is.EqualTo(SimulationStartupAction.Resumed));
            Assert.That(snapshot.RunCount, Is.EqualTo(1));
            Assert.That(snapshot.Ships, Has.Count.EqualTo(definition.ShipCount));
            Assert.That(snapshot.Run.ProcessSegment, Is.EqualTo(2));
        });
    }

    [Test]
    public void Run_WhenPersistedSeedDiffers_RejectsTheExistingRunWithoutChangingIt()
    {
        var definition = CreateDefinition();
        var databaseLocation = Path.Combine(_temporaryDirectory, "seed-mismatch.typhon");

        SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        var changedSeed = CreateDefinition(seed: 42);

        Assert.Throws<InvalidOperationException>(() => SpaceBattleHost.Run(
            changedSeed,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink()));

        var snapshot = SpaceBattleHost.ReadSnapshot(definition, databaseLocation);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Run.Seed, Is.EqualTo(definition.Seed));
            Assert.That(snapshot.Run.ProcessSegment, Is.EqualTo(1));
            Assert.That(snapshot.Ships, Has.Count.EqualTo(definition.ShipCount));
        });
    }

    [Test]
    public void Run_WhenPersistedRulesetVersionDiffers_RejectsTheExistingRunWithoutChangingIt()
    {
        var definition = CreateDefinition();
        var databaseLocation = Path.Combine(_temporaryDirectory, "ruleset-mismatch.typhon");

        SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        Assert.Throws<InvalidOperationException>(() => SpaceBattleHost.Run(
            CreateDefinition(rulesetVersion: 2),
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink()));

        var snapshot = SpaceBattleHost.ReadSnapshot(definition, databaseLocation);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Run.RulesetVersion, Is.EqualTo(definition.RulesetVersion));
            Assert.That(snapshot.Run.ProcessSegment, Is.EqualTo(1));
        });
    }

    [TestCase(SimulationRunStatus.Completed)]
    [TestCase(SimulationRunStatus.TimedOut)]
    public void Run_WhenPersistedRunIsTerminal_ProtectsTheHistoricalWorld(
        SimulationRunStatus terminalStatus)
    {
        var definition = CreateDefinition();
        var databaseLocation = Path.Combine(_temporaryDirectory, $"{terminalStatus}.typhon");

        SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        SetRunStatus(definition, databaseLocation, terminalStatus);

        Assert.Throws<InvalidOperationException>(() => SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink()));

        var snapshot = SpaceBattleHost.ReadSnapshot(definition, databaseLocation);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Run.Status, Is.EqualTo(terminalStatus));
            Assert.That(snapshot.Run.ProcessSegment, Is.EqualTo(1));
            Assert.That(snapshot.Ships, Has.Count.EqualTo(definition.ShipCount));
        });
    }

    [Test]
    public void Run_WhenPersistedRunIsMissingButShipsExist_RejectsTheAmbiguousWorld()
    {
        var definition = CreateDefinition();
        var databaseLocation = Path.Combine(_temporaryDirectory, "missing-run.typhon");

        SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        DestroyRun(definition, databaseLocation);

        Assert.Throws<InvalidOperationException>(() => SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink()));
    }

    [Test]
    public void Run_WhenExistingDatabaseHasNoSimulationRun_RejectsTheAmbiguousWorld()
    {
        var definition = CreateDefinition();
        var databaseLocation = Path.Combine(_temporaryDirectory, "empty-existing.typhon");

        using (SpaceBattleDatabase.Open(definition, databaseLocation))
        {
        }

        Assert.Throws<InvalidOperationException>(() => SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink()));
    }

    [Test]
    public void Run_WhenMultiplePersistedRunsExist_RejectsTheAmbiguousWorld()
    {
        var definition = CreateDefinition();
        var databaseLocation = Path.Combine(_temporaryDirectory, "multiple-runs.typhon");

        SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        AddDuplicateRun(definition, databaseLocation);

        Assert.Throws<InvalidOperationException>(() => SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink()));
    }

    private static SimulationDefinition CreateDefinition(
        ulong seed = SimulationDefinition.DefaultSeed,
        uint rulesetVersion = 1) => new(
        runName: "startup-test",
        shipCount: 8,
        seed: seed,
        rulesetVersion: rulesetVersion,
        worldSize: 1_000f,
        maximumHealth: 1_000,
        stagingTicks: 250,
        spatialCellSize: 100f,
        spatialMargin: 20f);

    private static void SetRunStatus(
        SimulationDefinition definition,
        string databaseLocation,
        SimulationRunStatus status)
    {
        using var engine = SpaceBattleDatabase.Open(definition, databaseLocation);
        using var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate);
        var runId = transaction.Query<SimulationRunEntity>().Execute().Single();
        transaction.OpenMut(runId).Write(SimulationRunEntity.State).Status = (byte)status;
        transaction.Commit();
    }

    private static void DestroyRun(SimulationDefinition definition, string databaseLocation)
    {
        using var engine = SpaceBattleDatabase.Open(definition, databaseLocation);
        using var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate);
        var runId = transaction.Query<SimulationRunEntity>().Execute().Single();
        transaction.Destroy(runId);
        transaction.Commit();
    }

    private static void AddDuplicateRun(SimulationDefinition definition, string databaseLocation)
    {
        using var engine = SpaceBattleDatabase.Open(definition, databaseLocation);
        using var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate);
        var runId = transaction.Query<SimulationRunEntity>().Execute().Single();
        var entity = transaction.Open(runId);
        var duplicate = entity.Read(SimulationRunEntity.Run);
        var duplicateState = entity.Read(SimulationRunEntity.State);
        transaction.Spawn<SimulationRunEntity>(
            SimulationRunEntity.Run.Set(in duplicate),
            SimulationRunEntity.State.Set(in duplicateState));
        transaction.Commit();
    }

    private sealed class RecordingObservationSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
