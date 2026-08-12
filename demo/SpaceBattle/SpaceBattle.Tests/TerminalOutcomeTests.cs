using System.IO;
using System.Threading;
using NUnit.Framework;
using Typhon.Engine;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class TerminalOutcomeTests
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
    public void Start_WhenOneShipSurvives_PersistsTheWinnerAndProtectsTheRunHistory()
    {
        var definition = CreateDefinition(shipCount: 1, maximumCompletedTicks: 10);
        var databaseLocation = Path.Combine(_temporaryDirectory, "winner.typhon");

        using (var simulation = SpaceBattleHost.Start(
                   definition,
                   databaseLocation,
                   CancellationToken.None,
                   new RecordingObservationSink()))
        {
            Assert.That(simulation.WaitForTerminal(TimeSpan.FromSeconds(5)), Is.True);

            var snapshot = simulation.GetSnapshot();
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Run.CompletedTicks, Is.EqualTo(1));
                Assert.That(snapshot.Run.AliveShipCount, Is.EqualTo(1));
                Assert.That(snapshot.Run.Status, Is.EqualTo(SimulationRunStatus.Completed));
                Assert.That(snapshot.Run.Outcome, Is.EqualTo(SimulationRunOutcome.Winner));
                Assert.That(snapshot.Run.WinnerEntityKey, Is.EqualTo(snapshot.Ships.Single().EntityKey));
            });
        }

        Assert.Throws<InvalidOperationException>(() => SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink()));
    }

    [Test]
    public void Start_WhenEveryShipWasEliminated_PersistsADraw()
    {
        var definition = CreateDefinition(shipCount: 2, maximumCompletedTicks: 10);
        var databaseLocation = Path.Combine(_temporaryDirectory, "draw.typhon");
        SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        DestroyAllShips(definition, databaseLocation);

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        Assert.That(simulation.WaitForTerminal(TimeSpan.FromSeconds(5)), Is.True);

        var snapshot = simulation.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Run.CompletedTicks, Is.EqualTo(1));
            Assert.That(snapshot.Run.AliveShipCount, Is.Zero);
            Assert.That(snapshot.Run.Status, Is.EqualTo(SimulationRunStatus.Completed));
            Assert.That(snapshot.Run.Outcome, Is.EqualTo(SimulationRunOutcome.Draw));
            Assert.That(snapshot.Run.WinnerEntityKey, Is.Null);
        });
    }

    [Test]
    public void Start_WhenTheCompletedTickLimitIsReached_PersistsATimeout()
    {
        var definition = CreateDefinition(shipCount: 2, maximumCompletedTicks: 1);
        var databaseLocation = Path.Combine(_temporaryDirectory, "timeout.typhon");

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        Assert.That(simulation.WaitForTerminal(TimeSpan.FromSeconds(5)), Is.True);

        var snapshot = simulation.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Run.CompletedTicks, Is.EqualTo(1));
            Assert.That(snapshot.Run.AliveShipCount, Is.EqualTo(2));
            Assert.That(snapshot.Run.Status, Is.EqualTo(SimulationRunStatus.TimedOut));
            Assert.That(snapshot.Run.Outcome, Is.EqualTo(SimulationRunOutcome.TimedOut));
            Assert.That(snapshot.Run.WinnerEntityKey, Is.Null);
        });
    }

    private static SimulationDefinition CreateDefinition(int shipCount, ulong maximumCompletedTicks) => new(
        runName: "terminal-test",
        shipCount,
        seed: SimulationDefinition.DefaultSeed,
        rulesetVersion: 1,
        worldSize: 1_000f,
        maximumHealth: 1_000,
        stagingTicks: 250,
        spatialCellSize: 100f,
        spatialMargin: 20f,
        maximumCompletedTicks: maximumCompletedTicks);

    private static void DestroyAllShips(SimulationDefinition definition, string databaseLocation)
    {
        using var engine = SpaceBattleDatabase.Open(definition, databaseLocation);
        using var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate);
        var runId = transaction.Query<SimulationRunEntity>().Execute().Single();
        foreach (EntityId shipId in transaction.Query<Ship>().Execute())
        {
            transaction.Destroy(shipId);
        }

        transaction.OpenMut(runId).Write(SimulationRunEntity.Run).AliveShipCount = 0;
        transaction.Commit();
    }

    private sealed class RecordingObservationSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
