using System.IO;
using System.Threading;
using NUnit.Framework;
using Typhon.Engine;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class ProductionConfigurationTests
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
    public void ProductionResourceEnvelope_UsesTheValidatedMemoryLimits()
    {
        SpaceBattleResourceEnvelope envelope = SpaceBattleProductionSettings.ResourceEnvelope;

        Assert.Multiple(() =>
        {
            Assert.That(envelope.PageCacheSizeBytes, Is.EqualTo(512UL * 1024 * 1024));
            Assert.That(envelope.MemoryBudgetBytes, Is.EqualTo(1024UL * 1024 * 1024));
            Assert.DoesNotThrow(envelope.Validate);
        });

        Assert.Throws<InvalidOperationException>(() => new SpaceBattleResourceEnvelope(
            PageCacheSizeBytes: 1024UL * 1024 * 1024,
            MemoryBudgetBytes: 512UL * 1024 * 1024).Validate());
    }

    [Test]
    public void GetProfilerTracePath_PlacesTheTraceBesideTheRunDatabase()
    {
        string databaseLocation = Path.Combine(_temporaryDirectory, "profiled-run.typhon");

        string tracePath = SpaceBattleProductionSettings.GetProfilerTracePath(databaseLocation);

        Assert.That(tracePath, Is.EqualTo(Path.Combine(_temporaryDirectory, "profiled-run.typhon-trace")));
    }

    [Test]
    public void ValidateDamageIntentQueueCapacity_RejectsMoreShipsThanTheWorkerQueuesCanHold()
    {
        long unsupportedShipCount = SpaceBattleProductionSettings.MaximumSupportedShipCount + 1;

        Assert.DoesNotThrow(() => SpaceBattleProductionSettings.ValidateDamageIntentQueueCapacity(
            SimulationDefinition.Default.ShipCount));
        if (unsupportedShipCount <= int.MaxValue)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SpaceBattleProductionSettings.ValidateDamageIntentQueueCapacity((int)unsupportedShipCount));
        }
    }

    [Test]
    public void Start_UsesTheProductionEnvelopeAndNeverShedsSimulationWork()
    {
        string databaseLocation = Path.Combine(_temporaryDirectory, "production-settings.typhon");
        using var simulation = SpaceBattleHost.Start(
            CreateDefinition(),
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        SpaceBattleRuntimeConfiguration configuration = simulation.RuntimeConfiguration;

        Assert.Multiple(() =>
        {
            Assert.That(configuration.PageCacheSizeBytes,
                Is.EqualTo(SpaceBattleProductionSettings.ResourceEnvelope.PageCacheSizeBytes));
            Assert.That(configuration.MemoryBudgetBytes,
                Is.EqualTo(SpaceBattleProductionSettings.ResourceEnvelope.MemoryBudgetBytes));
            Assert.That(configuration.ConfiguredWorkerCount, Is.EqualTo(-1));
            Assert.That(configuration.EffectiveWorkerCount,
                Is.EqualTo(Math.Max(1, Environment.ProcessorCount - 4)));
            Assert.That(configuration.OverloadMinimumTickRateHz, Is.EqualTo(SimulationDefinition.FixedTickRate));
            Assert.That(configuration.QueueGrowthEscalationTicks, Is.Zero);
            Assert.That(configuration.CurrentOverloadLevel, Is.EqualTo(OverloadLevel.Normal));
            Assert.That(configuration.Systems, Has.All.Matches<SpaceBattleSystemConfiguration>(system =>
                system.Priority == SystemPriority.Critical &&
                system.TickDivisor == 1 &&
                system.ThrottledTickDivisor == 1 &&
                !system.CanShed));
            Assert.That(configuration.EventQueues,
                Has.Count.EqualTo(configuration.EffectiveWorkerCount));
            Assert.That(configuration.EventQueues.Select(static queue => queue.Name),
                Is.EqualTo(Enumerable.Range(0, configuration.EffectiveWorkerCount)
                    .Select(static workerId => $"DamageIntent-{workerId}")));
            Assert.That(configuration.EventQueues.Select(static queue => queue.Capacity),
                Has.All.EqualTo(BehaviorRules.DamageIntentQueueCapacity));
        });
    }

    private static SimulationDefinition CreateDefinition() => new(
        runName: "production-settings-test",
        shipCount: 4,
        seed: SimulationDefinition.DefaultSeed,
        rulesetVersion: 1,
        worldSize: 1_000f,
        maximumHealth: 1_000,
        stagingTicks: 250,
        spatialCellSize: 100f,
        spatialMargin: 20f);

    private sealed class RecordingObservationSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
