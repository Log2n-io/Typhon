using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using NUnit.Framework;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class ObservabilityTests
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
    public void Start_PublishesPeriodicLogSnapshotsOffTheTickThread()
    {
        var definition = CreateDefinition(maximumCompletedTicks: 30);
        var databaseLocation = Path.Combine(_temporaryDirectory, "observability.typhon");
        var observations = new RecordingObservationSink();

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            observations);

        Assert.That(simulation.WaitForCompletedTicks(25, TimeSpan.FromSeconds(10)), Is.True);

        var log = observations.WaitForSingle<SpaceBattleLogSnapshot>(
            static snapshot => snapshot.CompletedTicks == 25,
            TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(log.ProcessSegment, Is.EqualTo(1));
            Assert.That(log.Run.Status, Is.EqualTo(SimulationRunStatus.Running));
            Assert.That(log.Run.Outcome, Is.EqualTo(SimulationRunOutcome.None));
            Assert.That(log.Modes.Total, Is.EqualTo(log.Counters.AliveShipCount));
            Assert.That(log.Modes.Staging, Is.Zero);
            Assert.That(log.Modes.Wandering + log.Modes.Tracking + log.Modes.Combat +
                log.Modes.Disengaging + log.Modes.Escaping, Is.EqualTo(log.Counters.AliveShipCount));
            Assert.That(log.Counters.ActiveLockCount, Is.GreaterThanOrEqualTo(0));
            Assert.That(log.Counters.ShotsFired, Is.GreaterThanOrEqualTo(log.Counters.Hits));
            Assert.That(log.Counters.Deaths, Is.GreaterThanOrEqualTo(0));
            Assert.That(log.Performance.SampleCount, Is.EqualTo(25));
            Assert.That(log.Performance.P50ActualDurationMilliseconds, Is.GreaterThan(0));
            Assert.That(log.Performance.P95ActualDurationMilliseconds, Is.GreaterThanOrEqualTo(
                log.Performance.P50ActualDurationMilliseconds));
            Assert.That(log.Performance.P99ActualDurationMilliseconds, Is.GreaterThanOrEqualTo(
                log.Performance.P95ActualDurationMilliseconds));
            Assert.That(log.Performance.OverrunCount, Is.GreaterThanOrEqualTo(0));
        });

        Assert.That(observations.GetThreadIds<SpaceBattleLogSnapshot>(),
            Has.None.EqualTo(Environment.CurrentManagedThreadId));
    }

    [Test]
    public void Start_PublishesResourceSnapshotsEveryOneHundredTwentyFiveTicks()
    {
        var definition = CreateDefinition(maximumCompletedTicks: 130);
        var databaseLocation = Path.Combine(_temporaryDirectory, "resources.typhon");
        var observations = new RecordingObservationSink();

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            observations);

        Assert.That(simulation.WaitForCompletedTicks(125, TimeSpan.FromSeconds(15)), Is.True);

        var resource = observations.WaitForSingle<SpaceBattleResourceSnapshot>(
            static snapshot => snapshot.CompletedTicks == 125,
            TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(resource.ProcessSegment, Is.EqualTo(1));
            Assert.That(resource.Snapshot.Nodes, Is.Not.Empty);
        });
    }

    [Test]
    public void Start_PublishesTerminalOutcomeEvenBeforeThePeriodicLogCadence()
    {
        var definition = CreateDefinition(shipCount: 1, maximumCompletedTicks: 10);
        var databaseLocation = Path.Combine(_temporaryDirectory, "terminal-observability.typhon");
        var observations = new RecordingObservationSink();

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            observations);

        Assert.That(simulation.WaitForTerminal(TimeSpan.FromSeconds(5)), Is.True);

        var log = observations.WaitForSingle<SpaceBattleLogSnapshot>(
            static snapshot => snapshot.Run.Status == SimulationRunStatus.Completed,
            TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(log.CompletedTicks, Is.EqualTo(1));
            Assert.That(log.Run.Outcome, Is.EqualTo(SimulationRunOutcome.Winner));
            Assert.That(log.Run.WinnerEntityKey, Is.Not.Null);
            Assert.That(log.ProcessSegment, Is.EqualTo(1));
        });
    }

    private static SimulationDefinition CreateDefinition(
        int shipCount = 8,
        ulong maximumCompletedTicks = 200) => new(
        runName: "observability-test",
        shipCount,
        seed: SimulationDefinition.DefaultSeed,
        rulesetVersion: 1,
        worldSize: 1_000f,
        maximumHealth: 1_000,
        stagingTicks: 0,
        spatialCellSize: 100f,
        spatialMargin: 20f,
        maximumCompletedTicks: maximumCompletedTicks);

    private sealed class RecordingObservationSink : ISpaceBattleObservationSink
    {
        private readonly ConcurrentQueue<(SpaceBattleObservation Observation, int ThreadId)> _items = [];

        public IReadOnlyList<SpaceBattleObservation> Items => _items.Select(static item => item.Observation).ToArray();

        public void Publish(SpaceBattleObservation observation)
            => _items.Enqueue((observation, Environment.CurrentManagedThreadId));

        public T WaitForSingle<T>(Func<T, bool> predicate, TimeSpan timeout)
            where T : SpaceBattleObservation
        {
            T result = null!;
            if (SpinWait.SpinUntil(() =>
            {
                var matching = _items
                    .Where(static item => item.Observation is T)
                    .Select(static item => (T)item.Observation)
                    .Where(predicate)
                    .ToArray();
                if (matching.Length == 1)
                {
                    result = matching[0];
                    return true;
                }

                return false;
            }, timeout))
            {
                return result;
            }

            Assert.Fail($"未在 {timeout} 内收到满足条件的 {typeof(T).Name}。观察数：{_items.Count}。");
            return null!;
        }

        public IReadOnlyList<int> GetThreadIds<T>() where T : SpaceBattleObservation
            => _items
                .Where(static item => item.Observation is T)
                .Select(static item => item.ThreadId)
                .ToArray();
    }
}
