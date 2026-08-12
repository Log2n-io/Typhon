using System.IO;
using System.Threading;
using NUnit.Framework;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class PauseRecoveryTests
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
    public void CancellationDuringRun_PausesAfterFenceAndResumesFromPersistedTick()
    {
        var definition = CreateDefinition();
        var databaseLocation = Path.Combine(_temporaryDirectory, "pause.typhon");
        using var cancellation = new CancellationTokenSource();
        InitialWorldSnapshot paused;

        using (var simulation = SpaceBattleHost.Start(
                   definition,
                   databaseLocation,
                   cancellation.Token,
                   new RecordingObservationSink()))
        {
            Assert.That(simulation.WaitForCompletedTicks(1, TimeSpan.FromSeconds(5)), Is.True);

            cancellation.Cancel();

            Assert.Multiple(() =>
            {
                Assert.That(simulation.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(simulation.WaitForTerminal(TimeSpan.FromMilliseconds(100)), Is.False);
            });

            paused = simulation.GetSnapshot();
            Assert.Multiple(() =>
            {
                Assert.That(paused.Run.Status, Is.EqualTo(SimulationRunStatus.Running));
                Assert.That(paused.Run.CompletedTicks, Is.GreaterThanOrEqualTo(1));
                Assert.That(simulation.WaitForCompletedTicks(
                    checked(paused.Run.CompletedTicks + 1),
                    TimeSpan.FromMilliseconds(200)), Is.False);
            });
        }

        var persisted = SpaceBattleHost.ReadSnapshot(definition, databaseLocation);
        Assert.Multiple(() =>
        {
            Assert.That(persisted.Run.Status, Is.EqualTo(SimulationRunStatus.Running));
            Assert.That(persisted.Run.CompletedTicks, Is.EqualTo(paused.Run.CompletedTicks));
            Assert.That(persisted.Ships, Is.EqualTo(paused.Ships));
            Assert.That(persisted.TargetLocks, Is.EqualTo(paused.TargetLocks));
        });

        using var resumed = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        Assert.That(resumed.StartupResult.StartupAction, Is.EqualTo(SimulationStartupAction.Resumed));

        var resumedAtPause = resumed.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(resumedAtPause.Run.CompletedTicks, Is.EqualTo(paused.Run.CompletedTicks));
            Assert.That(resumedAtPause.Ships, Is.EqualTo(paused.Ships));
            Assert.That(resumedAtPause.TargetLocks, Is.EqualTo(paused.TargetLocks));
        });

        var nextTick = checked(paused.Run.CompletedTicks + 1);
        Assert.That(resumed.WaitForCompletedTicks(nextTick, TimeSpan.FromSeconds(5)), Is.True);
        var afterResume = resumed.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(afterResume.Run.ProcessSegment, Is.EqualTo(2));
            Assert.That(afterResume.Run.CompletedTicks, Is.GreaterThanOrEqualTo(nextTick));
        });

        resumed.RequestPause();
        Assert.That(resumed.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);
    }

    [Test]
    public void RuntimeDiagnostics_RebuildAcrossPauseAndResumeWithoutChangingMembership()
    {
        var definition = CreateDefinition();
        var databaseLocation = Path.Combine(_temporaryDirectory, "runtime-state.typhon");
        using var cancellation = new CancellationTokenSource();
        SpaceBattleRuntimeDiagnosticsSnapshot pausedDiagnostics;

        using (var simulation = SpaceBattleHost.Start(
                   definition,
                   databaseLocation,
                   cancellation.Token,
                   new RecordingObservationSink()))
        {
            Assert.That(simulation.WaitForCompletedTicks(1, TimeSpan.FromSeconds(5)), Is.True);
            cancellation.Cancel();
            Assert.That(simulation.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);
            pausedDiagnostics = simulation.GetRuntimeDiagnostics();

            Assert.Multiple(() =>
            {
                Assert.That(pausedDiagnostics.ViewMembershipCount, Is.EqualTo(definition.ShipCount));
                Assert.That(pausedDiagnostics.CombatViewMembershipCount, Is.EqualTo(definition.ShipCount));
                Assert.That(pausedDiagnostics.ShipRosterCount, Is.EqualTo(definition.ShipCount));
                Assert.That(pausedDiagnostics.TickWorksetCount, Is.EqualTo(definition.ShipCount));
                Assert.That(pausedDiagnostics.DerivedAliveShipCount, Is.EqualTo(definition.ShipCount));
                Assert.That(pausedDiagnostics.DerivedActiveLockCount, Is.EqualTo(0));
                Assert.That(pausedDiagnostics.ConsumerProcessingCounts["State"], Is.EqualTo(definition.ShipCount));
                Assert.That(pausedDiagnostics.ConsumerProcessingCounts["Combat"], Is.EqualTo(definition.ShipCount));
                Assert.That(pausedDiagnostics.RuntimeShipViewRefreshCount, Is.GreaterThan(0));
                Assert.That(
                    pausedDiagnostics.RuntimeShipViewRefreshCount,
                    Is.EqualTo(checked((long)pausedDiagnostics.CompletedTicks)));
                Assert.That(
                    pausedDiagnostics.CombatShipViewRefreshCount,
                    Is.EqualTo(pausedDiagnostics.RuntimeShipViewRefreshCount));
                Assert.That(pausedDiagnostics.RuntimeShipViewAddedCount, Is.Zero);
                Assert.That(pausedDiagnostics.CombatShipViewAddedCount, Is.Zero);
            });
        }

        using var resumed = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        var resumedDiagnostics = resumed.GetRuntimeDiagnostics();

        Assert.Multiple(() =>
        {
            Assert.That(resumedDiagnostics.ViewMembershipCount, Is.EqualTo(pausedDiagnostics.ViewMembershipCount));
            Assert.That(resumedDiagnostics.CombatViewMembershipCount, Is.EqualTo(pausedDiagnostics.CombatViewMembershipCount));
            Assert.That(resumedDiagnostics.ShipRosterCount, Is.EqualTo(pausedDiagnostics.ShipRosterCount));
            Assert.That(resumedDiagnostics.TickWorksetCount, Is.EqualTo(pausedDiagnostics.TickWorksetCount));
            Assert.That(resumedDiagnostics.DerivedAliveShipCount, Is.EqualTo(pausedDiagnostics.DerivedAliveShipCount));
            Assert.That(resumedDiagnostics.DerivedActiveLockCount, Is.EqualTo(pausedDiagnostics.DerivedActiveLockCount));
        });

        var nextTick = checked(pausedDiagnostics.CompletedTicks + 1);
        Assert.That(resumed.WaitForCompletedTicks(nextTick, TimeSpan.FromSeconds(5)), Is.True);
        resumed.RequestPause();
        Assert.That(resumed.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);
        var afterResumeDiagnostics = resumed.GetRuntimeDiagnostics();
        Assert.Multiple(() =>
        {
            Assert.That(afterResumeDiagnostics.ViewMembershipCount, Is.EqualTo(definition.ShipCount));
            Assert.That(afterResumeDiagnostics.CombatViewMembershipCount, Is.EqualTo(definition.ShipCount));
            Assert.That(afterResumeDiagnostics.ShipRosterCount, Is.EqualTo(definition.ShipCount));
            Assert.That(afterResumeDiagnostics.DerivedAliveShipCount, Is.EqualTo(definition.ShipCount));
            Assert.That(afterResumeDiagnostics.ConsumerProcessingCounts["State"], Is.EqualTo(definition.ShipCount));
            Assert.That(afterResumeDiagnostics.ConsumerProcessingCounts["Combat"], Is.EqualTo(definition.ShipCount));
            Assert.That(afterResumeDiagnostics.RuntimeShipViewAddedCount, Is.Zero);
            Assert.That(afterResumeDiagnostics.CombatShipViewAddedCount, Is.Zero);
            Assert.That(
                afterResumeDiagnostics.RuntimeShipViewRefreshCount,
                Is.EqualTo(checked((long)(afterResumeDiagnostics.CompletedTicks - pausedDiagnostics.CompletedTicks))));
            Assert.That(
                afterResumeDiagnostics.CombatShipViewRefreshCount,
                Is.EqualTo(afterResumeDiagnostics.RuntimeShipViewRefreshCount));
        });
    }

    [Test]
    public void RuntimeDiagnostics_ExposeStableRosterAndCurrentTickWorksetKeys()
    {
        var definition = CreateDefinition();
        var databaseLocation = Path.Combine(_temporaryDirectory, "roster-workset.typhon");

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        Assert.That(simulation.WaitForCompletedTicks(1, TimeSpan.FromSeconds(5)), Is.True);

        var expectedKeys = simulation.GetSnapshot().Ships
            .Select(static ship => ship.EntityKey)
            .Order()
            .ToArray();
        var diagnostics = simulation.GetRuntimeDiagnostics();

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.ShipRosterEntityKeys, Is.EqualTo(expectedKeys));
            Assert.That(diagnostics.TickWorksetEntityKeys, Is.EqualTo(expectedKeys));
        });
    }

    [Test]
    public void RuntimeDiagnostics_IndexesTargetLocksByOwnerAndTargetAtPauseBoundary()
    {
        var definition = new SimulationDefinition(
            runName: "runtime-lock-diagnostics-test",
            shipCount: 64,
            seed: SimulationDefinition.DefaultSeed,
            rulesetVersion: 1,
            worldSize: 100f,
            maximumHealth: 1_000,
            stagingTicks: 0,
            spatialCellSize: 100f,
            spatialMargin: 20f);
        var databaseLocation = Path.Combine(_temporaryDirectory, "runtime-lock-diagnostics.typhon");

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        simulation.WaitForSnapshot(252, TimeSpan.FromSeconds(20));
        simulation.RequestPause();
        Assert.That(simulation.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);

        InitialWorldSnapshot snapshot = simulation.GetSnapshot();
        SpaceBattleRuntimeDiagnosticsSnapshot diagnostics = simulation.GetRuntimeDiagnostics();
        AssertLockIndexesMatch(snapshot, diagnostics);

        ulong pausedTick = snapshot.Run.CompletedTicks;
        simulation.Dispose();
        using var resumed = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        Assert.That(resumed.WaitForCompletedTicks(
            checked(pausedTick + 1),
            TimeSpan.FromSeconds(5)), Is.True);
        resumed.RequestPause();
        Assert.That(resumed.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);
        AssertLockIndexesMatch(resumed.GetSnapshot(), resumed.GetRuntimeDiagnostics());
    }

    [Test]
    // AC1: 恢复后的 membership、稳定 roster、owner/target 锁索引和派生统计与权威 Ship/Target Lock 快照一致。
    public void AfterResume_DiagnosticsMatchAuthoritativeSnapshot()
    {
        var definition = new SimulationDefinition(
            runName: "ac1-diagnostics-match",
            shipCount: 8,
            seed: SimulationDefinition.DefaultSeed,
            rulesetVersion: 1,
            worldSize: 200f,
            maximumHealth: 1_000,
            stagingTicks: 0,
            spatialCellSize: 100f,
            spatialMargin: 20f);
        var databaseLocation = Path.Combine(_temporaryDirectory, "ac1-diagnostics-match.typhon");

        // 第一阶段：运行并暂停
        using (var first = SpaceBattleHost.Start(
                   definition,
                   databaseLocation,
                   CancellationToken.None,
                   new RecordingObservationSink()))
        {
            // 运行到 ships 开始交互的阶段
            first.WaitForSnapshot(203, TimeSpan.FromSeconds(20));
            first.RequestPause();
            Assert.That(first.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);
        }

        // 第二阶段：恢复
        using var resumed = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        Assert.That(resumed.StartupResult.StartupAction, Is.EqualTo(SimulationStartupAction.Resumed));

        // 恢复后立即获取诊断和快照（零个 tick 前进）
        InitialWorldSnapshot resumeSnapshot = resumed.GetSnapshot();
        SpaceBattleRuntimeDiagnosticsSnapshot resumeDiagnostics = resumed.GetRuntimeDiagnostics();

        long[] expectedRosterKeys = resumeSnapshot.Ships
            .Select(static ship => ship.EntityKey)
            .Order()
            .ToArray();
        long[] expectedWorksetKeys = expectedRosterKeys;

        // 恢复后的 owner/target 锁索引必须与权威 snapshot 一致
        IReadOnlyDictionary<long, int> authoritativeOwnerCounts = resumeSnapshot.TargetLocks
            .GroupBy(static targetLock => targetLock.OwnerEntityKey)
            .ToDictionary(static group => group.Key, static group => group.Count());
        IReadOnlyDictionary<long, int> authoritativeTargetCounts = resumeSnapshot.TargetLocks
            .GroupBy(static targetLock => targetLock.TargetEntityKey)
            .ToDictionary(static group => group.Key, static group => group.Count());

        Assert.Multiple(() =>
        {
            // membership 一致性
            Assert.That(resumeDiagnostics.ViewMembershipCount, Is.EqualTo(resumeSnapshot.Ships.Count),
                "ViewMembershipCount 必须与权威飞船快照一致");
            Assert.That(resumeDiagnostics.CombatViewMembershipCount, Is.EqualTo(resumeSnapshot.Ships.Count),
                "CombatViewMembershipCount 必须与权威飞船快照一致");

            // 稳定 roster 和 workset
            Assert.That(resumeDiagnostics.ShipRosterCount, Is.EqualTo(resumeSnapshot.Ships.Count),
                "ShipRosterCount 必须与权威飞船快照一致");
            Assert.That(resumeDiagnostics.TickWorksetCount, Is.EqualTo(resumeSnapshot.Ships.Count),
                "TickWorksetCount 必须与权威飞船快照一致");
            Assert.That(resumeDiagnostics.ShipRosterEntityKeys, Is.EqualTo(expectedRosterKeys),
                "ShipRosterEntityKeys 必须与权威飞船 entity key 列表一致");
            Assert.That(resumeDiagnostics.TickWorksetEntityKeys, Is.EqualTo(expectedWorksetKeys),
                "TickWorksetEntityKeys 必须与权威飞船 entity key 列表一致");

            // owner/target 锁索引
            Assert.That(resumeDiagnostics.OwnerLockIndex, Is.EqualTo(authoritativeOwnerCounts),
                "OwnerLockIndex 必须与权威目标锁 owner 分布一致");
            Assert.That(resumeDiagnostics.TargetLockIndex, Is.EqualTo(authoritativeTargetCounts),
                "TargetLockIndex 必须与权威目标锁 target 分布一致");

            // 派生统计
            Assert.That(resumeDiagnostics.DerivedAliveShipCount, Is.EqualTo((int)resumeSnapshot.Run.AliveShipCount),
                "DerivedAliveShipCount 必须与权威存活飞船数一致");
            Assert.That(resumeDiagnostics.DerivedActiveLockCount, Is.EqualTo(resumeSnapshot.TargetLocks.Count),
                "DerivedActiveLockCount 必须与权威目标锁总数一致");
        });
    }

    [Test]
    // AC2: 恢复后首个 refresh 没有历史重复 delta，首个 simulation tick 的消费者处理集合与权威 membership 一致。
    public void AfterResume_FirstRefreshHasNoHistoricalDelta_AndFirstTickConsumerMatchesMembership()
    {
        var definition = new SimulationDefinition(
            runName: "ac2-first-refresh",
            shipCount: 6,
            seed: SimulationDefinition.DefaultSeed,
            rulesetVersion: 1,
            worldSize: 200f,
            maximumHealth: 1_000,
            stagingTicks: 0,
            spatialCellSize: 100f,
            spatialMargin: 20f);
        var databaseLocation = Path.Combine(_temporaryDirectory, "ac2-first-refresh.typhon");

        // 第一阶段：运行并暂停（产生一些目标锁活动）
        using (var first = SpaceBattleHost.Start(
                   definition,
                   databaseLocation,
                   CancellationToken.None,
                   new RecordingObservationSink()))
        {
            first.WaitForSnapshot(203, TimeSpan.FromSeconds(20));
            first.RequestPause();
            Assert.That(first.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);
        }

        // 第二阶段：恢复
        using var resumed = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        Assert.That(resumed.StartupResult.StartupAction, Is.EqualTo(SimulationStartupAction.Resumed));

        // 恢复后立即检查：RefreshCount = 0（刚重建），AddedCount = 0（历史 delta 已清除）
        var diagnosticsAfterResume = resumed.GetRuntimeDiagnostics();

        Assert.Multiple(() =>
        {
            Assert.That(diagnosticsAfterResume.RuntimeShipViewAddedCount, Is.Zero,
                "恢复后首个 refresh 前 RuntimeShipViewAddedCount 必须是零（无历史重复 delta）");
            Assert.That(diagnosticsAfterResume.CombatShipViewAddedCount, Is.Zero,
                "恢复后首个 refresh 前 CombatShipViewAddedCount 必须是零");
            Assert.That(diagnosticsAfterResume.RuntimeShipViewRefreshCount, Is.Zero,
                "恢复后重建的 view 的 refresh 计数必须从零开始");
            Assert.That(diagnosticsAfterResume.CombatShipViewRefreshCount, Is.Zero,
                "恢复后重建的 combat view 的 refresh 计数必须从零开始");
        });

        // 运行第一个 tick 让 ShipViewRefreshSystem 执行 Refresh
        ulong firstTick = checked(diagnosticsAfterResume.CompletedTicks + 1);
        Assert.That(resumed.WaitForCompletedTicks(firstTick, TimeSpan.FromSeconds(10)), Is.True);

        var diagnosticsAfterFirstTick = resumed.GetRuntimeDiagnostics();
        var snapshotAfterFirstTick = resumed.GetSnapshot();
        int authoritativeShipCount = snapshotAfterFirstTick.Ships.Count;

        Assert.Multiple(() =>
        {
            // 第一个 refresh 没有 added（无新实体，历史 delta 不重复）
            Assert.That(diagnosticsAfterFirstTick.RuntimeShipViewAddedCount, Is.Zero,
                "首个 refresh 后 RuntimeShipViewAddedCount 仍为零（无新实体 spawn）");
            Assert.That(diagnosticsAfterFirstTick.CombatShipViewAddedCount, Is.Zero,
                "首个 refresh 后 CombatShipViewAddedCount 仍为零");
            Assert.That(diagnosticsAfterFirstTick.RuntimeShipViewRefreshCount, Is.EqualTo(1),
                "首个 refresh 后 RuntimeShipViewRefreshCount 应为 1");

            // 消费者处理集合与权威 membership 一致
            Assert.That(diagnosticsAfterFirstTick.ConsumerProcessingCounts["State"],
                Is.EqualTo(authoritativeShipCount),
                "State 消费者处理数量必须等于权威飞船数量");
            Assert.That(diagnosticsAfterFirstTick.ConsumerProcessingCounts["Combat"],
                Is.EqualTo(authoritativeShipCount),
                "Combat 消费者处理数量必须等于权威飞船数量");
        });
    }

    [Test]
    // AC6: 重复启动、暂停、恢复与关闭不会泄漏 view 注册、delta buffer 或其他 runtime workset 资源。
    public void RepeatedPauseResumeCycles_DoNotLeakViewsOrDeltaBuffers()
    {
        var definition = new SimulationDefinition(
            runName: "ac6-no-leak",
            shipCount: 4,
            seed: SimulationDefinition.DefaultSeed,
            rulesetVersion: 1,
            worldSize: 200f,
            maximumHealth: 1_000,
            stagingTicks: 0,
            spatialCellSize: 100f,
            spatialMargin: 20f,
            maximumCompletedTicks: 200);
        var databaseLocation = Path.Combine(_temporaryDirectory, "ac6-no-leak.typhon");

        const int cycleCount = 4;

        for (var cycle = 0; cycle < cycleCount; cycle++)
        {
            using var simulation = SpaceBattleHost.Start(
                definition,
                databaseLocation,
                CancellationToken.None,
                new RecordingObservationSink());

            if (simulation.StartupResult.StartupAction == SimulationStartupAction.Initialized)
            {
                // 首次初始化后运行几个 tick 然后暂停
                Assert.That(simulation.WaitForCompletedTicks(3, TimeSpan.FromSeconds(5)), Is.True);
            }
            else
            {
                // 恢复后运行几个 tick 然后暂停
                Assert.That(simulation.WaitForCompletedTicks(3, TimeSpan.FromSeconds(5)), Is.True);
            }

            simulation.RequestPause();
            Assert.That(simulation.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);

            var diagnostics = simulation.GetRuntimeDiagnostics();
            var snapshot = simulation.GetSnapshot();

            Assert.Multiple(() =>
            {
                // view 注册数量必须与当前权威船只数一致（无累积泄漏）
                Assert.That(diagnostics.ViewMembershipCount, Is.EqualTo(snapshot.Ships.Count),
                    $"循环 {cycle}：ViewMembershipCount 泄漏");

                // roster 和 workset 大小必须匹配
                Assert.That(diagnostics.ShipRosterCount, Is.EqualTo(snapshot.Ships.Count),
                    $"循环 {cycle}：ShipRosterCount 泄漏");
                Assert.That(diagnostics.TickWorksetCount, Is.EqualTo(snapshot.Ships.Count),
                    $"循环 {cycle}：TickWorksetCount 泄漏");

                // delta buffer 不累积：如果没有任何 spawn/destroy 操作，AddedCount 从头开始
                // 首次初始化后可能有瞬时 add（但在 pause 时已清除），恢复后从零开始
                if (cycle > 0)
                {
                    Assert.That(diagnostics.RuntimeShipViewAddedCount, Is.Zero,
                        $"循环 {cycle}：RuntimeShipViewAddedCount 在循环间不应累积");
                }
            });

            // simulation 退出时 Dispose 应正确清理
        }

        // 终态验证：数据库中的运行仍然正确
        var finalSnapshot = SpaceBattleHost.ReadSnapshot(definition, databaseLocation);
        Assert.Multiple(() =>
        {
            Assert.That(finalSnapshot.RunCount, Is.EqualTo(1),
                "多次暂停恢复后不应产生第二个 SimulationRun");
            Assert.That(finalSnapshot.Run.Status, Is.EqualTo(SimulationRunStatus.Running),
                "模拟应在暂停状态下结束");
        });
    }

    private static void AssertLockIndexesMatch(
        InitialWorldSnapshot snapshot,
        SpaceBattleRuntimeDiagnosticsSnapshot diagnostics)
    {
        IReadOnlyDictionary<long, int> ownerCounts = snapshot.TargetLocks
            .GroupBy(static targetLock => targetLock.OwnerEntityKey)
            .ToDictionary(static group => group.Key, static group => group.Count());
        IReadOnlyDictionary<long, int> targetCounts = snapshot.TargetLocks
            .GroupBy(static targetLock => targetLock.TargetEntityKey)
            .ToDictionary(static group => group.Key, static group => group.Count());
        HashSet<long> shipKeys = snapshot.Ships.Select(static ship => ship.EntityKey).ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.OwnerLockIndex, Is.EqualTo(ownerCounts));
            Assert.That(diagnostics.TargetLockIndex, Is.EqualTo(targetCounts));
            Assert.That(diagnostics.DerivedActiveLockCount, Is.EqualTo(snapshot.TargetLocks.Count));
            Assert.That(diagnostics.DerivedAliveShipCount, Is.EqualTo(snapshot.Ships.Count));
            Assert.That(snapshot.TargetLocks, Has.All.Matches<TargetLockSnapshot>(targetLock =>
                shipKeys.Contains(targetLock.OwnerEntityKey) && shipKeys.Contains(targetLock.TargetEntityKey)));
        });
    }

    private static SimulationDefinition CreateDefinition() => new(
        runName: "pause-recovery-test",
        shipCount: 4,
        seed: SimulationDefinition.DefaultSeed,
        rulesetVersion: 1,
        worldSize: 100f,
        maximumHealth: 1_000,
        stagingTicks: 0,
        spatialCellSize: 100f,
        spatialMargin: 20f,
        maximumCompletedTicks: 100);

    private sealed class RecordingObservationSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
