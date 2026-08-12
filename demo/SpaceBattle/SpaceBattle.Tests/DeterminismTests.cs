using System.IO;
using System.Threading;
using NUnit.Framework;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class DeterminismTests
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
    // AC4: 相同 seed 与初始快照的重复运行产生相同决策、稳定状态快照和终态。
    // 使用紧凑世界让飞船快速交战，通过足够多的 snapshot 比对验证端到端确定性。
    public void SameSeed_ProducesIdenticalStateSnapshotsAndTerminalOutcome()
    {
        var definition = new SimulationDefinition(
            runName: "ac4-determinism",
            shipCount: 8,
            seed: SimulationDefinition.DefaultSeed,
            rulesetVersion: 1,
            worldSize: 80f,
            maximumHealth: 300,
            stagingTicks: 0,
            spatialCellSize: 50f,
            spatialMargin: 10f,
            maximumCompletedTicks: 6_000);
        var firstLocation = Path.Combine(_temporaryDirectory, "run-A.typhon");
        var secondLocation = Path.Combine(_temporaryDirectory, "run-B.typhon");

        // 只比对中间阶段的快照，不要求同时等待 terminal
        ulong[] compareTicks = [100, 302, 502, 1_000];

        IReadOnlyList<InitialWorldSnapshot> firstSnapshots;
        using (var simulation = SpaceBattleHost.Start(
                   definition,
                   firstLocation,
                   CancellationToken.None,
                   new RecordingObservationSink()))
        {
            firstSnapshots = simulation.WaitForSnapshots(compareTicks, TimeSpan.FromSeconds(90));
        }

        IReadOnlyList<InitialWorldSnapshot> secondSnapshots;
        using (var simulation = SpaceBattleHost.Start(
                   definition,
                   secondLocation,
                   CancellationToken.None,
                   new RecordingObservationSink()))
        {
            secondSnapshots = simulation.WaitForSnapshots(compareTicks, TimeSpan.FromSeconds(90));
        }

        Assert.Multiple(() =>
        {
            Assert.That(secondSnapshots.Count, Is.EqualTo(firstSnapshots.Count));
            for (var i = 0; i < compareTicks.Length; i++)
            {
                InitialWorldSnapshot a = firstSnapshots[i];
                InitialWorldSnapshot b = secondSnapshots[i];
                Assert.That(b.Run.CompletedTicks, Is.EqualTo(a.Run.CompletedTicks),
                    $"tick {compareTicks[i]} 的 completed ticks 不一致");
                Assert.That(b.Ships, Is.EqualTo(a.Ships).AsCollection,
                    $"tick {compareTicks[i]} 的飞船快照不一致");
                Assert.That(b.TargetLocks, Is.EqualTo(a.TargetLocks).AsCollection,
                    $"tick {compareTicks[i]} 的目标锁快照不一致");
                Assert.That(b.KillParticipations, Is.EqualTo(a.KillParticipations).AsCollection,
                    $"tick {compareTicks[i]} 的击杀参与记录不一致");
            }
        });

        // 两个仿真的终态也应当一致（compact 世界 + 低血量足够在 6_000 ticks 内结束）
        InitialWorldSnapshot firstTerminal;
        using (var sim = SpaceBattleHost.Start(
                   definition, firstLocation, CancellationToken.None, new RecordingObservationSink()))
        {
            Assert.That(sim.WaitForTerminal(TimeSpan.FromSeconds(120)), Is.True,
                "首个仿真正在 6_000 tick 内结束");
            firstTerminal = sim.GetSnapshot();
        }

        InitialWorldSnapshot secondTerminal;
        using (var sim = SpaceBattleHost.Start(
                   definition, secondLocation, CancellationToken.None, new RecordingObservationSink()))
        {
            Assert.That(sim.WaitForTerminal(TimeSpan.FromSeconds(120)), Is.True,
                "第二个仿真正在 6_000 tick 内结束");
            secondTerminal = sim.GetSnapshot();
        }

        Assert.Multiple(() =>
        {
            Assert.That(secondTerminal.Run, Is.EqualTo(firstTerminal.Run),
                "终态 SimulationRun 不一致");
            Assert.That(secondTerminal.Ships, Is.EqualTo(firstTerminal.Ships).AsCollection,
                "终态飞船快照不一致");
            Assert.That(secondTerminal.TargetLocks, Is.EqualTo(firstTerminal.TargetLocks).AsCollection,
                "终态目标锁不一致");
        });
    }

    [Test]
    // AC5: 至少比较两个不同 WorkerCount，证明 worker-local 结果按稳定实体键归并且结果不随 worker 数改变。
    public void DifferentWorkerCounts_ProduceIdenticalSnapshotsAndTerminalState()
    {
        var definition = new SimulationDefinition(
            runName: "ac5-worker-determinism",
            shipCount: 8,
            seed: SimulationDefinition.DefaultSeed,
            rulesetVersion: 1,
            worldSize: 80f,
            maximumHealth: 300,
            stagingTicks: 0,
            spatialCellSize: 50f,
            spatialMargin: 10f,
            maximumCompletedTicks: 6_000);
        var singleWorkerLocation = Path.Combine(_temporaryDirectory, "single-worker.typhon");
        var multiWorkerLocation = Path.Combine(_temporaryDirectory, "multi-worker.typhon");

        ulong[] compareTicks = [100, 302, 502, 1_000];
        IReadOnlyList<InitialWorldSnapshot> singleWorkerSnapshots;

        SpaceBattleProductionSettings.TestWorkerCountOverride = 1;
        try
        {
            using (var simulation = SpaceBattleHost.Start(
                       definition,
                       singleWorkerLocation,
                       CancellationToken.None,
                       new RecordingObservationSink()))
            {
                singleWorkerSnapshots = simulation.WaitForSnapshots(compareTicks, TimeSpan.FromSeconds(90));
            }
        }
        finally
        {
            SpaceBattleProductionSettings.TestWorkerCountOverride = null;
        }

        IReadOnlyList<InitialWorldSnapshot> multiWorkerSnapshots;
        using (var simulation = SpaceBattleHost.Start(
                   definition,
                   multiWorkerLocation,
                   CancellationToken.None,
                   new RecordingObservationSink()))
        {
            multiWorkerSnapshots = simulation.WaitForSnapshots(compareTicks, TimeSpan.FromSeconds(90));
        }

        int effectiveWorkerCount = SpaceBattleProductionSettings.EffectiveWorkerCount;

        Assert.Multiple(() =>
        {
            Assert.That(effectiveWorkerCount, Is.GreaterThanOrEqualTo(2),
                $"默认 worker 数 ({effectiveWorkerCount}) 应 >= 2，否则本测试退化为等价对比");

            Assert.That(multiWorkerSnapshots.Count, Is.EqualTo(singleWorkerSnapshots.Count));
            for (var i = 0; i < compareTicks.Length; i++)
            {
                InitialWorldSnapshot a = singleWorkerSnapshots[i];
                InitialWorldSnapshot b = multiWorkerSnapshots[i];
                Assert.That(b.Run.CompletedTicks, Is.EqualTo(a.Run.CompletedTicks),
                    $"tick {compareTicks[i]} 的 completed ticks 在 worker 数不同时不一致");
                Assert.That(b.Ships, Is.EqualTo(a.Ships).AsCollection,
                    $"tick {compareTicks[i]} 的飞船快照在 worker 数不同时不一致");
                Assert.That(b.TargetLocks, Is.EqualTo(a.TargetLocks).AsCollection,
                    $"tick {compareTicks[i]} 的目标锁快照在 worker 数不同时不一致");
                Assert.That(b.KillParticipations, Is.EqualTo(a.KillParticipations).AsCollection,
                    $"tick {compareTicks[i]} 的击杀参与在 worker 数不同时不一致");
            }
        });

        // 验证终态
        InitialWorldSnapshot singleWorkerTerminal;
        SpaceBattleProductionSettings.TestWorkerCountOverride = 1;
        try
        {
            using (var sim = SpaceBattleHost.Start(
                       definition, singleWorkerLocation, CancellationToken.None, new RecordingObservationSink()))
            {
                Assert.That(sim.WaitForTerminal(TimeSpan.FromSeconds(120)), Is.True);
                singleWorkerTerminal = sim.GetSnapshot();
            }
        }
        finally
        {
            SpaceBattleProductionSettings.TestWorkerCountOverride = null;
        }

        InitialWorldSnapshot multiWorkerTerminal;
        using (var sim = SpaceBattleHost.Start(
                   definition, multiWorkerLocation, CancellationToken.None, new RecordingObservationSink()))
        {
            Assert.That(sim.WaitForTerminal(TimeSpan.FromSeconds(120)), Is.True);
            multiWorkerTerminal = sim.GetSnapshot();
        }

        Assert.Multiple(() =>
        {
            Assert.That(multiWorkerTerminal.Run, Is.EqualTo(singleWorkerTerminal.Run),
                "终态 SimulationRun 在 worker 数不同时不一致");
            Assert.That(multiWorkerTerminal.Ships, Is.EqualTo(singleWorkerTerminal.Ships).AsCollection,
                "终态飞船快照在 worker 数不同时不一致");
            Assert.That(multiWorkerTerminal.TargetLocks, Is.EqualTo(singleWorkerTerminal.TargetLocks).AsCollection,
                "终态目标锁在 worker 数不同时不一致");
        });
    }

    private sealed class RecordingObservationSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
