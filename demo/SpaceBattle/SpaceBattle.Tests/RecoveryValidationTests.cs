using System.IO;
using System.Threading;
using NUnit.Framework;
using Typhon.Engine;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class RecoveryValidationTests
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
    public void Run_WhenPersistedModeIsInvalid_RejectsTheRunWithoutAdvancingTheSegment()
    {
        var definition = CreateDefinition();
        var databaseLocation = CreateWorld(definition, "invalid-mode.typhon");

        using (var engine = SpaceBattleDatabase.Open(definition, databaseLocation))
        using (var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            var runId = transaction.Query<SimulationRunEntity>().Execute().Single();
            var ship = transaction.OpenMut(transaction.Query<Ship>().Execute().Single());
            var checkpoint = CaptureCheckpoint(ship);
            checkpoint.Mode = byte.MaxValue;
            ship.Write(Ship.PauseCheckpoint) = checkpoint;
            WriteRunCheckpoint(transaction, runId, 1);
            transaction.Commit();
        }

        var exception = Assert.Throws<InvalidOperationException>(() => SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink()));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("行为模式"));
            Assert.That(ReadRun(definition, databaseLocation).ProcessSegment, Is.EqualTo(1));
        });
    }

    [Test]
    public void Run_WhenPersistedPositionIsNotFinite_RejectsTheRunWithoutAdvancingTheSegment()
    {
        var definition = CreateDefinition();
        var databaseLocation = CreateWorld(definition, "non-finite-position.typhon");

        using (var engine = SpaceBattleDatabase.Open(definition, databaseLocation))
        using (var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            var runId = transaction.Query<SimulationRunEntity>().Execute().Single();
            var ship = transaction.OpenMut(transaction.Query<Ship>().Execute().Single());
            var checkpoint = CaptureCheckpoint(ship);
            checkpoint.PositionX = float.NaN;
            checkpoint.BoundsMinX = float.NaN;
            checkpoint.BoundsMaxX = float.NaN;
            ship.Write(Ship.PauseCheckpoint) = checkpoint;
            WriteRunCheckpoint(transaction, runId, 1);
            transaction.Commit();
        }

        var exception = Assert.Throws<InvalidOperationException>(() => SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink()));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("坐标"));
            Assert.That(ReadRun(definition, databaseLocation).ProcessSegment, Is.EqualTo(1));
        });
    }

    [Test]
    public void Run_WhenPersistedPositionIsOutsideTheWorld_RejectsTheRun()
    {
        var definition = CreateDefinition();
        var databaseLocation = CreateWorld(definition, "out-of-world-position.typhon");

        using (var engine = SpaceBattleDatabase.Open(definition, databaseLocation))
        using (var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            var runId = transaction.Query<SimulationRunEntity>().Execute().Single();
            var ship = transaction.OpenMut(transaction.Query<Ship>().Execute().Single());
            var checkpoint = CaptureCheckpoint(ship);
            checkpoint.PositionX = definition.WorldSize + 1f;
            checkpoint.BoundsMinX = definition.WorldSize + 1f;
            checkpoint.BoundsMaxX = definition.WorldSize + 1f;
            ship.Write(Ship.PauseCheckpoint) = checkpoint;
            WriteRunCheckpoint(transaction, runId, 1);
            transaction.Commit();
        }

        Assert.Throws<InvalidOperationException>(() => SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink()));
    }

    [Test]
    public void Run_WhenWeaponAndAfterburnerAreBothEnabled_RejectsTheRun()
    {
        var definition = CreateDefinition();
        var databaseLocation = CreateWorld(definition, "illegal-equipment.typhon");

        using (var engine = SpaceBattleDatabase.Open(definition, databaseLocation))
        using (var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            var runId = transaction.Query<SimulationRunEntity>().Execute().Single();
            var ship = transaction.OpenMut(transaction.Query<Ship>().Execute().Single());
            var checkpoint = CaptureCheckpoint(ship);
            checkpoint.WeaponEnabled = 1;
            checkpoint.AfterburnerEnabled = 1;
            ship.Write(Ship.PauseCheckpoint) = checkpoint;
            WriteRunCheckpoint(transaction, runId, 1);
            transaction.Commit();
        }

        Assert.Throws<InvalidOperationException>(() => SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink()));
    }

    [Test]
    public void Run_WhenPauseCheckpointIsOlderThanTheRun_UsesTheCurrentTickFenceState()
    {
        var definition = CreateDefinition();
        var databaseLocation = CreateWorld(definition, "stale-checkpoint.typhon");

        using (var engine = SpaceBattleDatabase.Open(definition, databaseLocation))
        using (var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            var runId = transaction.Query<SimulationRunEntity>().Execute().Single();
            transaction.OpenMut(runId).Write(SimulationRunEntity.Run).CompletedTicks = 1;
            transaction.OpenMut(runId).Write(SimulationRunEntity.PauseCheckpoint) = new PauseRunCheckpointComponent
            {
                CompletedTicks = 0,
                AliveShipCount = 1,
                IsValid = 1,
            };
            transaction.Commit();
        }

        var result = SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        var snapshot = SpaceBattleHost.ReadSnapshot(definition, databaseLocation);

        Assert.Multiple(() =>
        {
            Assert.That(result.StartupAction, Is.EqualTo(SimulationStartupAction.Resumed));
            Assert.That(snapshot.Run.CompletedTicks, Is.EqualTo(1));
            Assert.That(snapshot.Run.ProcessSegment, Is.EqualTo(2));
            Assert.That(snapshot.Ships.Single().Mode, Is.EqualTo(BehaviorMode.Staging));
        });
    }

    [Test]
    public void Run_WhenTargetLockReferencesDestroyedShip_RejectsTheRun()
    {
        var definition = CreateDefinition(shipCount: 2);
        var databaseLocation = CreateWorld(definition, "dangling-lock.typhon");

        using (var engine = SpaceBattleDatabase.Open(definition, databaseLocation))
        using (var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            var shipIds = transaction.Query<Ship>().Execute().OrderBy(static id => id.EntityKey).ToArray();
            var targetLock = new TargetLockComponent
            {
                Owner = shipIds[0],
                Target = shipIds[1],
                Status = (byte)TargetLockStatus.Locked,
            };
            transaction.Spawn<TargetLock>(
                TargetLock.Data.Set(in targetLock),
                TargetLock.PauseCheckpoint.Set(default(PauseTargetLockCheckpointComponent)));
            transaction.Destroy(shipIds[1]);
            transaction.Commit();
        }

        Assert.Throws<InvalidOperationException>(() => SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink()));
    }

    [Test]
    public void Run_WhenCheckpointValidationFails_DoesNotPartiallyRestoreShipState()
    {
        var definition = CreateDefinition(shipCount: 2);
        var databaseLocation = CreateWorld(definition, "partial-checkpoint.typhon");
        PositionComponent originalPosition;

        using (var engine = SpaceBattleDatabase.Open(definition, databaseLocation))
        using (var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            var runId = transaction.Query<SimulationRunEntity>().Execute().Single();
            var shipIds = transaction.Query<Ship>().Execute().OrderBy(static id => id.EntityKey).ToArray();
            var firstShip = transaction.OpenMut(shipIds[0]);
            originalPosition = firstShip.Read(Ship.Position);
            var checkpoint = new PauseShipCheckpointComponent
            {
                IsValid = 1,
                PositionX = originalPosition.X + 1f,
                PositionY = originalPosition.Y,
                PositionZ = originalPosition.Z,
                BoundsMinX = originalPosition.X + 1f,
                BoundsMinY = originalPosition.Y,
                BoundsMinZ = originalPosition.Z,
                BoundsMaxX = originalPosition.X + 1f,
                BoundsMaxY = originalPosition.Y,
                BoundsMaxZ = originalPosition.Z,
                DirectionX = 1f,
                Speed = 0f,
                Health = definition.MaximumHealth,
                ModeTicksRemaining = definition.StagingTicks,
                Mode = (byte)BehaviorMode.Staging,
            };
            firstShip.Write(Ship.PauseCheckpoint) = checkpoint;
            transaction.OpenMut(shipIds[1]).Write(Ship.PauseCheckpoint) = default;
            transaction.OpenMut(runId).Write(SimulationRunEntity.PauseCheckpoint) = new PauseRunCheckpointComponent
            {
                CompletedTicks = 0,
                AliveShipCount = 2,
                IsValid = 1,
            };
            transaction.Commit();
        }

        Assert.Throws<InvalidOperationException>(() => SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink()));

        using var rawEngine = SpaceBattleDatabase.Open(definition, databaseLocation);
        using var rawTransaction = rawEngine.CreateReadOnlyTransaction();
        var shipId = rawTransaction.Query<Ship>().Execute().OrderBy(static id => id.EntityKey).First();
        var position = rawTransaction.Open(shipId).Read(Ship.Position);
        Assert.That(
            new PositionSnapshot(position.X, position.Y, position.Z),
            Is.EqualTo(new PositionSnapshot(originalPosition.X, originalPosition.Y, originalPosition.Z)));
    }

    private string CreateWorld(SimulationDefinition definition, string fileName)
    {
        var databaseLocation = Path.Combine(_temporaryDirectory, fileName);
        SpaceBattleHost.Run(
            definition,
            databaseLocation,
            CancellationToken.None,
            new RecordingObservationSink());
        return databaseLocation;
    }

    private static SimulationRunSnapshot ReadRun(
        SimulationDefinition definition,
        string databaseLocation) => SpaceBattleHost.ReadSnapshot(definition, databaseLocation).Run;

    private static void WriteRunCheckpoint(
        Transaction transaction,
        EntityId runId,
        uint aliveShipCount)
    {
        transaction.OpenMut(runId).Write(SimulationRunEntity.PauseCheckpoint) = new PauseRunCheckpointComponent
        {
            CompletedTicks = 0,
            AliveShipCount = aliveShipCount,
            IsValid = 1,
        };
    }

    private static SimulationDefinition CreateDefinition(int shipCount = 1) => new(
        runName: "recovery-validation-test",
        shipCount,
        seed: SimulationDefinition.DefaultSeed,
        rulesetVersion: 1,
        worldSize: 100f,
        maximumHealth: 1_000,
        stagingTicks: 250,
        spatialCellSize: 100f,
        spatialMargin: 20f);

    private static PauseShipCheckpointComponent CaptureCheckpoint(EntityRef ship)
    {
        var position = ship.Read(Ship.Position);
        var bounds = ship.Read(Ship.SpatialBounds);
        var motion = ship.Read(Ship.Motion);
        var health = ship.Read(Ship.Health);
        var behavior = ship.Read(Ship.Behavior);
        var tracking = ship.Read(Ship.Tracking);
        var weapon = ship.IsEnabled(Ship.Weapon) ? ship.Read(Ship.Weapon) : default;
        var afterburner = ship.IsEnabled(Ship.Afterburner) ? ship.Read(Ship.Afterburner) : default;
        EntityId trackingTarget = tracking.Target;
        return new PauseShipCheckpointComponent
        {
            IsValid = 1,
            PositionX = position.X,
            PositionY = position.Y,
            PositionZ = position.Z,
            BoundsMinX = bounds.Bounds.MinX,
            BoundsMinY = bounds.Bounds.MinY,
            BoundsMinZ = bounds.Bounds.MinZ,
            BoundsMaxX = bounds.Bounds.MaxX,
            BoundsMaxY = bounds.Bounds.MaxY,
            BoundsMaxZ = bounds.Bounds.MaxZ,
            DirectionX = motion.DirectionX,
            DirectionY = motion.DirectionY,
            DirectionZ = motion.DirectionZ,
            Speed = motion.Speed,
            Health = health.Current,
            DecisionOrdinal = behavior.DecisionOrdinal,
            ModeTicksRemaining = behavior.ModeTicksRemaining,
            Mode = behavior.Mode,
            TrackingTargetEntityKey = trackingTarget.IsNull ? 0 : trackingTarget.EntityKey,
            TrackingTicksRemaining = tracking.TrackingTicksRemaining,
            CooldownTicksRemaining = weapon.CooldownTicksRemaining,
            AfterburnerActivatedTick = afterburner.ActivatedTick,
            WeaponEnabled = ship.IsEnabled(Ship.Weapon) ? (byte)1 : (byte)0,
            AfterburnerEnabled = ship.IsEnabled(Ship.Afterburner) ? (byte)1 : (byte)0,
        };
    }

    private sealed class RecordingObservationSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
        }
    }
}
