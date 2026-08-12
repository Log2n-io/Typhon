using System.IO;
using System.Threading;
using NUnit.Framework;
using Typhon.Engine;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class TargetLockTests
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
    public void Start_CombatShipsCreateOneAcquiringLockForAnInRangeRosterCandidate()
    {
        SimulationDefinition definition = CreateCombatDefinition("target-lock-test");
        string databaseLocation = Path.Combine(_temporaryDirectory, "locks.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        InitialWorldSnapshot snapshot = simulation.WaitForSnapshot(252, TimeSpan.FromSeconds(15));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.TargetLocks, Is.Not.Empty);
            Assert.That(snapshot.TargetLocks.Select(static targetLock => targetLock.OwnerEntityKey),
                Is.Unique);
            Assert.That(snapshot.TargetLocks, Has.All.Matches<TargetLockSnapshot>(
                targetLock => targetLock.Status == TargetLockStatus.Acquiring &&
                    targetLock.TicksRemaining == BehaviorRules.LockAcquisitionDurationTicks));
        });

        foreach (var targetLock in snapshot.TargetLocks)
        {
            var owner = snapshot.Ships.Single(ship => ship.EntityKey == targetLock.OwnerEntityKey);
            var target = snapshot.Ships.Single(ship => ship.EntityKey == targetLock.TargetEntityKey);
            var distanceSquared =
                ((target.Position.X - owner.Position.X) * (target.Position.X - owner.Position.X)) +
                ((target.Position.Y - owner.Position.Y) * (target.Position.Y - owner.Position.Y)) +
                ((target.Position.Z - owner.Position.Z) * (target.Position.Z - owner.Position.Z));

            Assert.That(distanceSquared, Is.LessThanOrEqualTo(
                BehaviorRules.LockRange * BehaviorRules.LockRange));
        }
    }

    [Test]
    public void Start_WhenAcquisitionCompletes_LockedTargetAuthorizesItsOwnerWeapon()
    {
        SimulationDefinition definition = CreateCombatDefinition("target-lock-authorization-test");
        string databaseLocation = Path.Combine(_temporaryDirectory, "authorization.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        InitialWorldSnapshot snapshot = simulation.WaitForSnapshot(303, TimeSpan.FromSeconds(20));
        HashSet<long> lockedOwners = snapshot.TargetLocks
            .Where(static targetLock => targetLock.Status == TargetLockStatus.Locked)
            .Select(static targetLock => targetLock.OwnerEntityKey)
            .ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(lockedOwners, Is.Not.Empty);
            Assert.That(snapshot.Ships
                    .Where(ship => lockedOwners.Contains(ship.EntityKey))
                    .Select(static ship => ship.WeaponEnabled),
                Is.All.True);
        });
    }

    [Test]
    public void Start_WhenLockedWeaponsFire_ResolvesDamageAndDeathsInTheSameTick()
    {
        SimulationDefinition definition = CreateCombatDefinition(
            "damage-resolution-test",
            maximumHealth: 200);
        string databaseLocation = Path.Combine(_temporaryDirectory, "damage-resolution.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        IReadOnlyList<InitialWorldSnapshot> snapshots = simulation.WaitForSnapshots(
            [299, 300, 301, 302, 303],
            TimeSpan.FromSeconds(20));
        int resolutionSnapshotIndex = snapshots
            .Select(static (snapshot, index) => (Snapshot: snapshot, Index: index))
            .First(item => item.Snapshot.KillParticipations.Count > 0)
            .Index;
        InitialWorldSnapshot previousSnapshot = snapshots[resolutionSnapshotIndex - 1];
        InitialWorldSnapshot snapshot = snapshots[resolutionSnapshotIndex];
        HashSet<long> liveShipKeys = snapshot.Ships.Select(static ship => ship.EntityKey).ToHashSet();
        HashSet<long> previousLiveShipKeys = previousSnapshot.Ships
            .Select(static ship => ship.EntityKey)
            .ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Ships.Count, Is.LessThan(definition.ShipCount));
            Assert.That(snapshot.Ships.Select(static ship => ship.Health), Has.All.GreaterThan(0));
            Assert.That(snapshot.KillParticipations, Is.Not.Empty);
            Assert.That(snapshot.KillParticipations.Select(static participation => participation.TargetEntityKey),
                Has.All.Matches<long>(targetEntityKey =>
                    previousLiveShipKeys.Contains(targetEntityKey) &&
                    !liveShipKeys.Contains(targetEntityKey)));
            Assert.That(snapshot.TargetLocks, Has.All.Matches<TargetLockSnapshot>(targetLock =>
                liveShipKeys.Contains(targetLock.OwnerEntityKey) &&
                liveShipKeys.Contains(targetLock.TargetEntityKey)));
        });
    }

    [Test]
    public void Start_AfterDestroyedShipRefreshesRosterAndWorksetToAuthoritativeShips()
    {
        SimulationDefinition definition = CreateCombatDefinition(
            "roster-destroy-refresh-test",
            maximumHealth: 200);
        string databaseLocation = Path.Combine(_temporaryDirectory, "roster-destroy-refresh.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        Assert.That(
            SpinWait.SpinUntil(
                () => simulation.GetRuntimeDiagnostics().RuntimeShipViewRemovedCount > 0,
                TimeSpan.FromSeconds(30)),
            Is.True);
        simulation.RequestPause();
        Assert.That(simulation.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);

        InitialWorldSnapshot snapshot = simulation.GetSnapshot();
        SpaceBattleRuntimeDiagnosticsSnapshot diagnostics = simulation.GetRuntimeDiagnostics();
        long[] expectedKeys = snapshot.Ships
            .Select(static ship => ship.EntityKey)
            .Order()
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Ships.Count, Is.LessThan(definition.ShipCount));
            Assert.That(diagnostics.ViewMembershipCount, Is.EqualTo(snapshot.Ships.Count));
            Assert.That(diagnostics.CombatViewMembershipCount, Is.EqualTo(snapshot.Ships.Count));
            Assert.That(diagnostics.ShipRosterCount, Is.EqualTo(snapshot.Ships.Count));
            Assert.That(diagnostics.TickWorksetCount, Is.EqualTo(snapshot.Ships.Count));
            Assert.That(diagnostics.ShipRosterEntityKeys, Is.EqualTo(expectedKeys));
            Assert.That(diagnostics.TickWorksetEntityKeys, Is.EqualTo(expectedKeys));
        });
    }

    [Test]
    public void Start_WhenTheLastCombatShipIsDestroyed_DoesNotLeaveItInRosterOrWorkset()
    {
        SimulationDefinition definition = CreateCombatDefinition(
            "terminal-roster-destroy-test",
            maximumHealth: 200,
            shipCount: 2);
        string databaseLocation = Path.Combine(_temporaryDirectory, "terminal-roster-destroy.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        Assert.That(simulation.WaitForTerminal(TimeSpan.FromSeconds(30)), Is.True);

        InitialWorldSnapshot snapshot = simulation.GetSnapshot();
        SpaceBattleRuntimeDiagnosticsSnapshot diagnostics = simulation.GetRuntimeDiagnostics();
        long[] expectedKeys = snapshot.Ships
            .Select(static ship => ship.EntityKey)
            .Order()
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Run.Status, Is.EqualTo(SimulationRunStatus.Completed));
            Assert.That(diagnostics.ShipRosterEntityKeys, Is.EqualTo(expectedKeys));
            Assert.That(diagnostics.TickWorksetEntityKeys, Is.EqualTo(expectedKeys));
        });
    }

    [Test]
    public void Start_WhenShipsAreDamaged_SurvivorsEscapeAndCancelTheirOwnLocks()
    {
        SimulationDefinition definition = CreateCombatDefinition(
            "escape-reaction-test",
            maximumHealth: 1_000);
        string databaseLocation = Path.Combine(_temporaryDirectory, "escape-reaction.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        InitialWorldSnapshot reactionSnapshot = simulation.WaitForSnapshots(
                Enumerable.Range(299, 12).Select(static tick => (ulong)tick).ToArray(),
                TimeSpan.FromSeconds(20))
            .First(snapshot => snapshot.Ships.Any(ship => ship.Mode == BehaviorMode.Escaping));
        ShipSnapshot[] escapingShips = reactionSnapshot.Ships
            .Where(static ship => ship.Mode == BehaviorMode.Escaping)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(escapingShips, Is.Not.Empty);
            Assert.That(escapingShips, Has.All.Matches<ShipSnapshot>(ship =>
                ship.Health < definition.MaximumHealth &&
                ship.ModeTicksRemaining <= BehaviorRules.EscapingDurationTicks &&
                ship.TrackingTargetIsNull &&
                !ship.WeaponEnabled &&
                ship.AfterburnerEnabled &&
                ship.Motion.Speed == BehaviorRules.EscapingSpeed));
            Assert.That(reactionSnapshot.TargetLocks
                    .Where(targetLock => escapingShips.Any(ship => ship.EntityKey == targetLock.OwnerEntityKey)),
                Has.All.Matches<TargetLockSnapshot>(targetLock =>
                    targetLock.Status == TargetLockStatus.Releasing &&
                    targetLock.TicksRemaining == BehaviorRules.LockReleaseDurationTicks));
        });
    }

    [Test]
    public void Start_WhenWeaponsKillShips_ReportsEveryKillParticipationForTheSameTick()
    {
        SimulationDefinition definition = CreateCombatDefinition(
            "kill-participation-test",
            maximumHealth: 200);
        string databaseLocation = Path.Combine(_temporaryDirectory, "kill-participation.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        IReadOnlyList<InitialWorldSnapshot> snapshots = simulation.WaitForSnapshots(
            [300, 301, 302, 303],
            TimeSpan.FromSeconds(20));
        InitialWorldSnapshot resolutionSnapshot = snapshots
            .First(snapshot => snapshot.KillParticipations.Count > 0);
        HashSet<long> liveShipKeys = resolutionSnapshot.Ships
            .Select(static ship => ship.EntityKey)
            .ToHashSet();
        IReadOnlyList<(long Attacker, long Target)> participations = resolutionSnapshot.KillParticipations
            .Select(static participation => (participation.AttackerEntityKey, participation.TargetEntityKey))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(participations, Is.Not.Empty);
            Assert.That(participations, Is.Unique);
            Assert.That(resolutionSnapshot.KillParticipations.Select(static participation => participation.TargetEntityKey),
                Has.All.Matches<long>(targetEntityKey => !liveShipKeys.Contains(targetEntityKey)));
        });

        HashSet<long> woundedShipKeys = resolutionSnapshot.Ships
            .Where(static ship => ship.Mode == BehaviorMode.Escaping)
            .Select(static ship => ship.EntityKey)
            .ToHashSet();
        ShipSnapshot[] disengagingParticipants = resolutionSnapshot.KillParticipations
            .Select(static participation => participation.AttackerEntityKey)
            .Distinct()
            .Where(attackerEntityKey => !woundedShipKeys.Contains(attackerEntityKey))
            .Where(liveShipKeys.Contains)
            .Select(attackerEntityKey => resolutionSnapshot.Ships.Single(ship => ship.EntityKey == attackerEntityKey))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(disengagingParticipants, Is.Not.Empty);
            Assert.That(disengagingParticipants, Has.All.Matches<ShipSnapshot>(ship =>
                ship.Mode == BehaviorMode.Disengaging &&
                ship.ModeTicksRemaining == BehaviorRules.DisengagingDurationTicks &&
                ship.Motion.Speed == BehaviorRules.DisengagingSpeed &&
                !ship.WeaponEnabled &&
                !ship.AfterburnerEnabled));
        });
    }

    [Test]
    public void Start_WhenCombatEnds_ReleasesLocksBeforeFreeingTheirSlots()
    {
        SimulationDefinition definition = CreateCombatDefinition("target-lock-release-test");
        string databaseLocation = Path.Combine(_temporaryDirectory, "release.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        IReadOnlyList<InitialWorldSnapshot> snapshots = simulation.WaitForSnapshots(
            [502, 527],
            TimeSpan.FromSeconds(40));
        InitialWorldSnapshot releasingSnapshot = snapshots[0];
        Assert.Multiple(() =>
        {
            Assert.That(releasingSnapshot.TargetLocks, Is.Not.Empty);
            Assert.That(releasingSnapshot.TargetLocks, Has.All.Matches<TargetLockSnapshot>(
                targetLock => targetLock.Status == TargetLockStatus.Releasing &&
                    targetLock.TicksRemaining == BehaviorRules.LockReleaseDurationTicks));
            Assert.That(releasingSnapshot.Ships
                    .Where(ship => releasingSnapshot.TargetLocks.Any(targetLock =>
                        targetLock.OwnerEntityKey == ship.EntityKey))
                    .Select(static ship => ship.WeaponEnabled),
                Is.All.False);
        });

        HashSet<long> releasingLockIds = releasingSnapshot.TargetLocks
            .Select(static targetLock => targetLock.EntityKey)
            .ToHashSet();
        HashSet<long> releasingOwners = releasingSnapshot.TargetLocks
            .Select(static targetLock => targetLock.OwnerEntityKey)
            .ToHashSet();
        InitialWorldSnapshot releasedSnapshot = snapshots[1];
        Assert.Multiple(() =>
        {
            Assert.That(releasedSnapshot.TargetLocks
                    .Where(targetLock => releasingLockIds.Contains(targetLock.EntityKey)),
                Is.Empty);
            Assert.That(releasedSnapshot.TargetLocks
                    .Where(targetLock => releasingOwners.Contains(targetLock.OwnerEntityKey)),
                Is.Empty);
        });
    }

    [Test]
    public void RuntimeDiagnostics_KeepBothLockDirectionsConsistentAfterDeathsAndRelocking()
    {
        SimulationDefinition definition = CreateCombatDefinition(
            "target-lock-index-lifecycle-test",
            maximumHealth: 200);
        string databaseLocation = Path.Combine(_temporaryDirectory, "index-lifecycle.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        simulation.WaitForSnapshots(
            [300, 301, 302, 303],
            TimeSpan.FromSeconds(20));
        simulation.RequestPause();
        Assert.That(simulation.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);

        InitialWorldSnapshot snapshot = simulation.GetSnapshot();
        SpaceBattleRuntimeDiagnosticsSnapshot diagnostics = simulation.GetRuntimeDiagnostics();
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
            Assert.That(snapshot.TargetLocks, Has.All.Matches<TargetLockSnapshot>(targetLock =>
                shipKeys.Contains(targetLock.OwnerEntityKey) && shipKeys.Contains(targetLock.TargetEntityKey)));
        });
    }

    [Test]
    public void TargetLockCleanup_WhenTargetMovesOutOfRange_RemovesLockAndAllowsRelocking()
    {
        SimulationDefinition initialDefinition = CreateCombatDefinition(
            "target-lock-range-relock-test",
            maximumHealth: uint.MaxValue,
            shipCount: 2);
        SimulationDefinition resumedDefinition = CreateCombatDefinition(
            "target-lock-range-relock-test",
            maximumHealth: uint.MaxValue,
            shipCount: 2,
            worldSize: 1_000f);
        string databaseLocation = Path.Combine(_temporaryDirectory, "range-relock.typhon");
        InitialWorldSnapshot pausedSnapshot;

        using (SpaceBattleSimulation simulation = SpaceBattleHost.Start(
                   initialDefinition,
                   databaseLocation,
                   CancellationToken.None,
                   new RecordingObservationSink()))
        {
            simulation.WaitForSnapshot(303, TimeSpan.FromSeconds(20));
            simulation.RequestPause();
            Assert.That(simulation.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);
            pausedSnapshot = simulation.GetSnapshot();
        }

        TargetLockSnapshot lockToBreak = pausedSnapshot.TargetLocks
            .First(static targetLock => targetLock.Status == TargetLockStatus.Locked);
        ShipSnapshot ownerBeforeMove = pausedSnapshot.Ships
            .Single(ship => ship.EntityKey == lockToBreak.OwnerEntityKey);
        float farX = ownerBeforeMove.Position.X < resumedDefinition.WorldSize / 2f
            ? resumedDefinition.WorldSize - 1f
            : 0f;
        SetShipPosition(
            resumedDefinition,
            databaseLocation,
            lockToBreak.TargetEntityKey,
            new PositionSnapshot(farX, ownerBeforeMove.Position.Y, ownerBeforeMove.Position.Z));

        InitialWorldSnapshot outOfRangeSnapshot;
        using (SpaceBattleSimulation simulation = SpaceBattleHost.Start(
                   resumedDefinition,
                   databaseLocation,
                   CancellationToken.None,
                   new RecordingObservationSink()))
        {
            ulong nextTick = checked(pausedSnapshot.Run.CompletedTicks + 1);
            Assert.That(simulation.WaitForCompletedTicks(nextTick, TimeSpan.FromSeconds(5)), Is.True);
            simulation.RequestPause();
            Assert.That(simulation.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);
            outOfRangeSnapshot = simulation.GetSnapshot();
        }

        Assert.That(
            outOfRangeSnapshot.TargetLocks.Select(static targetLock => targetLock.EntityKey),
            Does.Not.Contain(lockToBreak.EntityKey));

        ShipSnapshot ownerAfterMove = outOfRangeSnapshot.Ships
            .Single(ship => ship.EntityKey == lockToBreak.OwnerEntityKey);
        SetShipPosition(
            resumedDefinition,
            databaseLocation,
            lockToBreak.TargetEntityKey,
            ownerAfterMove.Position);

        InitialWorldSnapshot relockedSnapshot;
        using (SpaceBattleSimulation simulation = SpaceBattleHost.Start(
                   resumedDefinition,
                   databaseLocation,
                   CancellationToken.None,
                   new RecordingObservationSink()))
        {
            ulong nextTick = checked(outOfRangeSnapshot.Run.CompletedTicks + 1);
            Assert.That(simulation.WaitForCompletedTicks(nextTick, TimeSpan.FromSeconds(5)), Is.True);
            simulation.RequestPause();
            Assert.That(simulation.WaitForPause(TimeSpan.FromSeconds(5)), Is.True);
            relockedSnapshot = simulation.GetSnapshot();
        }

        Assert.That(relockedSnapshot.TargetLocks, Has.Some.Matches<TargetLockSnapshot>(targetLock =>
            targetLock.EntityKey != lockToBreak.EntityKey &&
            targetLock.OwnerEntityKey == lockToBreak.OwnerEntityKey &&
            targetLock.TargetEntityKey == lockToBreak.TargetEntityKey));
    }

    [Test]
    // AC3: 端到端场景覆盖实体创建/销毁、行为模式变化、锁清理、同 tick 同时伤害结算和终态确认。
    public void ComprehensiveEndToEnd_CoversCreationDestructionModeLocksDamageAndTerminal()
    {
        SimulationDefinition definition = new(
            runName: "ac3-comprehensive-e2e",
            shipCount: 8,
            seed: SimulationDefinition.DefaultSeed,
            rulesetVersion: 1,
            worldSize: 300f,
            maximumHealth: 800,
            stagingTicks: 0,
            spatialCellSize: 100f,
            spatialMargin: 20f,
            maximumCompletedTicks: 4_000);
        string databaseLocation = Path.Combine(_temporaryDirectory, "ac3-comprehensive-e2e.typhon");

        using SpaceBattleSimulation simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());

        // 阶段 1：初始创建后所有飞船处于 Wandering 模式（staging=0）
        InitialWorldSnapshot initialSnapshot = simulation.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(initialSnapshot.Run.CompletedTicks, Is.Zero);
            Assert.That(initialSnapshot.Ships, Has.Count.EqualTo(definition.ShipCount));
            Assert.That(initialSnapshot.Ships, Has.All.Matches<ShipSnapshot>(ship =>
                ship.Mode == BehaviorMode.Staging &&
                ship.ModeTicksRemaining == 0));
            Assert.That(initialSnapshot.TargetLocks, Is.Empty);
            Assert.That(initialSnapshot.KillParticipations, Is.Empty);
        });

        // 阶段 2：第一个 tick 后，飞船进入 Wandering
        InitialWorldSnapshot tick1Snapshot = simulation.WaitForSnapshot(1, TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(tick1Snapshot.Ships, Has.Count.EqualTo(definition.ShipCount));
            Assert.That(tick1Snapshot.Ships, Has.All.Matches<ShipSnapshot>(ship =>
                ship.Mode == BehaviorMode.Wandering));
        });

        // 阶段 3：Tracking 或 Combat 阶段 — 验证至少部分飞船已离开 Wandering
        // 使用 WaitForSnapshots 滚动查找第一个有 Tracking/Combat 飞船的快照
        IReadOnlyList<InitialWorldSnapshot> transitionSnapshots = simulation.WaitForSnapshots(
            [202, 227, 252, 277, 302, 327, 352, 377, 402, 427, 452, 477, 502, 527],
            TimeSpan.FromSeconds(25));
        InitialWorldSnapshot trackingOrCombatSnapshot = transitionSnapshots
            .First(snapshot => snapshot.Ships.Any(ship =>
                ship.Mode is BehaviorMode.Tracking or BehaviorMode.Combat));
        Assert.Multiple(() =>
        {
            Assert.That(trackingOrCombatSnapshot.Ships, Has.Count.EqualTo(definition.ShipCount),
                "在 tracking/combat 阶段所有飞船应仍然存活");
            Assert.That(trackingOrCombatSnapshot.Ships, Has.Some.Matches<ShipSnapshot>(ship =>
                ship.Mode is BehaviorMode.Tracking or BehaviorMode.Combat),
                "至少应有部分飞船进入 Tracking 或 Combat");
        });

        // 阶段 4：持有 Acquiring 锁的阶段
        InitialWorldSnapshot acquiringLockSnapshot = transitionSnapshots
            .First(snapshot => snapshot.TargetLocks.Any(targetLock =>
                targetLock.Status == TargetLockStatus.Acquiring));
        Assert.Multiple(() =>
        {
            Assert.That(acquiringLockSnapshot.TargetLocks, Is.Not.Empty);
            Assert.That(acquiringLockSnapshot.TargetLocks, Has.All.Matches<TargetLockSnapshot>(targetLock =>
                targetLock.Status is TargetLockStatus.Acquiring or TargetLockStatus.Releasing));
        });

        // 阶段 5：Locked 锁和武器可用 — 滚动查找第一个 Locked 快照
        InitialWorldSnapshot lockedSnapshot = transitionSnapshots
            .Concat(simulation.WaitForSnapshots(
                [552, 553, 554, 555, 556, 557, 558, 559, 560, 561, 562, 563, 564, 565,
                 566, 567, 568, 569, 570, 571, 572, 573, 574, 575],
                TimeSpan.FromSeconds(25)))
            .FirstOrDefault(snapshot => snapshot.TargetLocks.Any(targetLock =>
                targetLock.Status == TargetLockStatus.Locked));
        if (lockedSnapshot != null)
        {
            Assert.Multiple(() =>
            {
                Assert.That(lockedSnapshot.Ships
                        .Where(ship => lockedSnapshot.TargetLocks
                            .Any(targetLock =>
                                targetLock.OwnerEntityKey == ship.EntityKey &&
                                targetLock.Status == TargetLockStatus.Locked))
                        .Select(static ship => ship.WeaponEnabled),
                    Has.All.True);
            });
        }

        // 阶段 6：伤害结算和击杀 — 查找第一个有击杀的快照
        IReadOnlyList<InitialWorldSnapshot> damageSearchSnapshots = simulation.WaitForSnapshots(
            [580, 590, 600, 610, 620, 630, 640, 650, 660, 670],
            TimeSpan.FromSeconds(30));
        InitialWorldSnapshot damageSnapshot = damageSearchSnapshots
            .FirstOrDefault(snapshot => snapshot.KillParticipations.Count > 0);
        if (damageSnapshot != null)
        {
            HashSet<long> liveKeys = damageSnapshot.Ships
                .Select(static ship => ship.EntityKey)
                .ToHashSet();

            Assert.Multiple(() =>
            {
                // 击杀参与记录完整
                Assert.That(damageSnapshot.KillParticipations, Is.Not.Empty);
                Assert.That(damageSnapshot.KillParticipations, Is.Unique);
                Assert.That(damageSnapshot.KillParticipations
                        .Select(static participation => participation.TargetEntityKey),
                    Has.All.Matches<long>(targetKey => !liveKeys.Contains(targetKey)),
                    "所有击杀目标都不在存活飞船中（同 tick 死亡）");
                Assert.That(damageSnapshot.Ships,
                    Has.All.Matches<ShipSnapshot>(ship => ship.Health > 0),
                    "没有存活飞船生命值为 0");

                // 锁清理 — 每个目标锁都指向存活飞船
                Assert.That(damageSnapshot.TargetLocks,
                    Has.All.Matches<TargetLockSnapshot>(targetLock =>
                        liveKeys.Contains(targetLock.OwnerEntityKey) &&
                        liveKeys.Contains(targetLock.TargetEntityKey)),
                    "所有目标锁必须指向存活飞船（死者锁已清理）");

                // 存活飞船数量减少
                Assert.That(damageSnapshot.Ships.Count, Is.LessThan(definition.ShipCount));
            });

            // 受伤存活者应切换到 Escaping
            ShipSnapshot[] escapingShips = damageSnapshot.Ships
                .Where(static ship => ship.Mode == BehaviorMode.Escaping)
                .ToArray();
            if (escapingShips.Length > 0)
            {
                Assert.That(escapingShips, Has.All.Matches<ShipSnapshot>(ship =>
                    ship.ModeTicksRemaining <= BehaviorRules.EscapingDurationTicks &&
                    ship.TrackingTargetIsNull &&
                    !ship.WeaponEnabled &&
                    ship.AfterburnerEnabled));
            }

            // 未受伤的击杀参与者应切换到 Disengaging
            HashSet<long> escapingKeys = escapingShips
                .Select(static ship => ship.EntityKey)
                .ToHashSet();
            ShipSnapshot[] disengagingParticipants = damageSnapshot.KillParticipations
                .Select(static participation => participation.AttackerEntityKey)
                .Distinct()
                .Where(attackerKey => !escapingKeys.Contains(attackerKey) && liveKeys.Contains(attackerKey))
                .Select(attackerKey => damageSnapshot.Ships.Single(ship => ship.EntityKey == attackerKey))
                .ToArray();
            Assert.That(disengagingParticipants, Has.All.Matches<ShipSnapshot>(ship =>
                ship.Mode == BehaviorMode.Disengaging &&
                ship.ModeTicksRemaining == BehaviorRules.DisengagingDurationTicks &&
                ship.Motion.Speed == BehaviorRules.DisengagingSpeed &&
                !ship.WeaponEnabled &&
                !ship.AfterburnerEnabled));
        }

        // 阶段 7：终态确认（4_000 tick 上限 = 160s 理论值，但 combat 应提前结束）
        Assert.That(simulation.WaitForTerminal(TimeSpan.FromSeconds(180)), Is.True);

        InitialWorldSnapshot terminalSnapshot = simulation.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(terminalSnapshot.Run.Status,
                Is.AnyOf(SimulationRunStatus.Completed, SimulationRunStatus.TimedOut));
            Assert.That(terminalSnapshot.Run.Outcome,
                Is.AnyOf(SimulationRunOutcome.Winner, SimulationRunOutcome.Draw, SimulationRunOutcome.TimedOut));

            if (terminalSnapshot.Run.Status == SimulationRunStatus.Completed)
            {
                if (terminalSnapshot.Run.Outcome == SimulationRunOutcome.Winner)
                {
                    Assert.That(terminalSnapshot.Run.AliveShipCount, Is.EqualTo(1));
                    Assert.That(terminalSnapshot.Run.WinnerEntityKey, Is.Not.Null);
                    Assert.That(terminalSnapshot.Ships, Has.Count.EqualTo(1));
                }
                else
                {
                    Assert.That(terminalSnapshot.Run.AliveShipCount, Is.Zero);
                    Assert.That(terminalSnapshot.Run.WinnerEntityKey, Is.Null);
                    Assert.That(terminalSnapshot.Ships, Is.Empty);
                }
            }

            // 终态没有活跃锁
            Assert.That(terminalSnapshot.TargetLocks, Is.Empty);
        });
    }

    private static void SetShipPosition(
        SimulationDefinition definition,
        string databaseLocation,
        long shipEntityKey,
        PositionSnapshot position)
    {
        using var engine = SpaceBattleDatabase.Open(definition, databaseLocation);
        using var transaction = engine.CreateQuickTransaction(Typhon.Engine.DurabilityMode.Immediate);
        EntityId shipId = transaction.Query<Ship>().Execute()
            .Single(entityId => entityId.EntityKey == shipEntityKey);
        EntityRef ship = transaction.OpenMut(shipId);
        ref PositionComponent currentPosition = ref ship.Write(Ship.Position);
        currentPosition.X = position.X;
        currentPosition.Y = position.Y;
        currentPosition.Z = position.Z;
        ref SpatialBoundsComponent bounds = ref ship.Write(Ship.SpatialBounds);
        bounds.Bounds.MinX = position.X;
        bounds.Bounds.MinY = position.Y;
        bounds.Bounds.MinZ = position.Z;
        bounds.Bounds.MaxX = position.X;
        bounds.Bounds.MaxY = position.Y;
        bounds.Bounds.MaxZ = position.Z;
        ref PauseShipCheckpointComponent checkpoint = ref ship.Write(Ship.PauseCheckpoint);
        checkpoint.PositionX = position.X;
        checkpoint.PositionY = position.Y;
        checkpoint.PositionZ = position.Z;
        checkpoint.BoundsMinX = position.X;
        checkpoint.BoundsMinY = position.Y;
        checkpoint.BoundsMinZ = position.Z;
        checkpoint.BoundsMaxX = position.X;
        checkpoint.BoundsMaxY = position.Y;
        checkpoint.BoundsMaxZ = position.Z;
        transaction.Commit();
    }

    private static SimulationDefinition CreateCombatDefinition(
        string runName,
        uint maximumHealth = 1_000,
        int shipCount = 64,
        float worldSize = 100f) => new(
        runName: runName,
        shipCount,
        seed: SimulationDefinition.DefaultSeed,
        rulesetVersion: 1,
        worldSize,
        maximumHealth: maximumHealth,
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
