using System.Diagnostics;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

public static class SpaceBattleHost
{
    private const ulong InitialPositionX = 1;
    private const ulong InitialPositionY = 2;
    private const ulong InitialPositionZ = 3;

    public static SpaceBattleRunResult Run(
        SimulationDefinition definition,
        string databaseLocation,
        CancellationToken cancellationToken,
        ISpaceBattleObservationSink observationSink)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseLocation);
        ArgumentNullException.ThrowIfNull(observationSink);
        SpaceBattleProductionSettings.ValidateDamageIntentQueueCapacity(definition.ShipCount);
        cancellationToken.ThrowIfCancellationRequested();

        var databaseAlreadyExists = Directory.Exists(databaseLocation) || File.Exists(databaseLocation);
        using var engine = SpaceBattleDatabase.Open(definition, databaseLocation);
        var result = InitializeOrResume(
            engine,
            definition,
            databaseAlreadyExists,
            cancellationToken,
            observationSink,
            out _);
        PublishInitializationCompleted(result, observationSink);
        return result;
    }

    public static SpaceBattleSimulation Start(
        SimulationDefinition definition,
        string databaseLocation,
        CancellationToken cancellationToken,
        ISpaceBattleObservationSink observationSink)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseLocation);
        ArgumentNullException.ThrowIfNull(observationSink);
        SpaceBattleProductionSettings.ValidateDamageIntentQueueCapacity(definition.ShipCount);
        cancellationToken.ThrowIfCancellationRequested();

        var databaseAlreadyExists = Directory.Exists(databaseLocation) || File.Exists(databaseLocation);
        var engine = SpaceBattleDatabase.Open(definition, databaseLocation);

        try
        {
            var startupResult = InitializeOrResume(
                engine,
                definition,
                databaseAlreadyExists,
                cancellationToken,
                observationSink,
                out var persistedRun);

            if (startupResult.StartupAction == SimulationStartupAction.Initialized)
            {
                var reopenStartedAt = Stopwatch.GetTimestamp();
                engine.Dispose();
                engine = SpaceBattleDatabase.Open(definition, databaseLocation);
                startupResult = startupResult with
                {
                    InitializationDuration = startupResult.InitializationDuration +
                        Stopwatch.GetElapsedTime(reopenStartedAt),
                };
            }

            PublishInitializationCompleted(startupResult, observationSink);

            var simulation = SpaceBattleSimulation.Create(
                engine,
                definition,
                persistedRun.EntityId,
                persistedRun.CompletedTicks,
                startupResult,
                observationSink);
            simulation.RegisterPauseCancellation(cancellationToken);
            engine = null!;
            return simulation;
        }
        finally
        {
            engine?.Dispose();
        }
    }

    private static SpaceBattleRunResult InitializeOrResume(
        DatabaseEngine engine,
        SimulationDefinition definition,
        bool databaseAlreadyExists,
        CancellationToken cancellationToken,
        ISpaceBattleObservationSink observationSink,
        out PersistedRun persistedRun)
    {
        var existingRun = FindPersistedRun(engine, databaseAlreadyExists);
        if (existingRun is not null)
        {
            persistedRun = existingRun.Value;
            ValidateRunIdentity(definition, persistedRun);
            return ResumeRunningRun(engine, definition, persistedRun);
        }

        var stopwatch = Stopwatch.StartNew();
        var result = CreateInitialWorld(
            engine,
            definition,
            cancellationToken,
            observationSink,
            stopwatch);
        persistedRun = FindPersistedRun(engine, databaseAlreadyExists: true)!.Value;
        return result;
    }

    private static SpaceBattleRunResult CreateInitialWorld(
        DatabaseEngine engine,
        SimulationDefinition definition,
        CancellationToken cancellationToken,
        ISpaceBattleObservationSink observationSink,
        Stopwatch stopwatch)
    {
        using var bulkLoad = engine.BeginBulkLoad(new BulkLoadOptions
        {
            ProgressBatchSize = Math.Max(1, Math.Min(10_000, definition.ShipCount)),
            ProgressReporter = progress => observationSink.Publish(new InitializationProgress(
                checked((int)Math.Max(0, progress.EntitiesSpawned - 1)),
                definition.ShipCount)),
        });

        var run = new SimulationRunComponent
        {
            Seed = definition.Seed,
            CompletedTicks = 0,
            RulesetVersion = definition.RulesetVersion,
            InitialShipCount = checked((uint)definition.ShipCount),
            AliveShipCount = checked((uint)definition.ShipCount),
        };
        var runState = new SimulationRunStateComponent
        {
            ProcessSegment = 1,
            Status = (byte)SimulationRunStatus.Running,
            Outcome = (byte)SimulationRunOutcome.None,
            WinnerEntityKey = 0,
        };
        var pauseRunCheckpoint = default(PauseRunCheckpointComponent);
        EntityId runEntityId = bulkLoad.Spawn<SimulationRunEntity>(
            SimulationRunEntity.Run.Set(in run),
            SimulationRunEntity.State.Set(in runState),
            SimulationRunEntity.PauseCheckpoint.Set(in pauseRunCheckpoint));

        var motion = new MotionComponent { DirectionX = 1f, DirectionY = 0f, DirectionZ = 0f, Speed = 0f };
        var health = new HealthComponent { Current = definition.MaximumHealth };
        var behavior = new BehaviorComponent
        {
            DecisionOrdinal = 0,
            ModeTicksRemaining = definition.StagingTicks,
            Mode = (byte)BehaviorMode.Staging,
        };
        var tracking = new TrackingComponent
        {
            Target = EntityLink<Ship>.Null,
            TrackingTicksRemaining = 0,
        };
        var pauseShipCheckpoint = default(PauseShipCheckpointComponent);
        ShipRunMembershipComponent runMembership = new() { RunEntityKey = runEntityId.EntityKey };

        for (var index = 0; index < definition.ShipCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var position = default(PositionComponent);
            var bounds = default(SpatialBoundsComponent);

            var entityId = bulkLoad.Spawn<Ship>(
                Ship.Position.Set(in position),
                Ship.SpatialBounds.Set(in bounds),
                Ship.Motion.Set(in motion),
                Ship.Health.Set(in health),
                Ship.Behavior.Set(in behavior),
                Ship.Tracking.Set(in tracking),
                Ship.RunMembership.Set(in runMembership),
                Ship.PauseCheckpoint.Set(in pauseShipCheckpoint));

            var packedEntityId = ((ulong)entityId.EntityKey << 12) | entityId.ArchetypeId;
            position = CreateInitialPosition(definition, packedEntityId);
            bounds = CreatePointBounds(position);
            bulkLoad.Update(entityId, in position);
            bulkLoad.Update(entityId, in bounds);
        }

        cancellationToken.ThrowIfCancellationRequested();
        bulkLoad.CompleteBulkLoad();
        stopwatch.Stop();

        return new SpaceBattleRunResult(
            definition.ShipCount,
            stopwatch.Elapsed,
            SimulationStartupAction.Initialized);
    }

    private static void PublishInitializationCompleted(
        SpaceBattleRunResult result,
        ISpaceBattleObservationSink observationSink)
    {
        if (result.StartupAction == SimulationStartupAction.Initialized)
        {
            observationSink.Publish(new InitializationCompleted(
                result.ShipCount,
                result.InitializationDuration));
        }
    }

    private static PersistedRun? FindPersistedRun(
        DatabaseEngine engine,
        bool databaseAlreadyExists)
    {
        using var transaction = engine.CreateReadOnlyTransaction();
        var runEntities = transaction.Query<SimulationRunEntity>().Execute();
        if (runEntities.Count == 0)
        {
            if (databaseAlreadyExists)
            {
                throw new InvalidOperationException("既有数据库的 SimulationRun 实体数量必须为 1，实际为 0。");
            }

            return null;
        }

        if (runEntities.Count != 1)
        {
            throw new InvalidOperationException($"SimulationRun 实体数量必须为 1，实际为 {runEntities.Count}。");
        }

        var runEntityId = runEntities.Single();
        var runEntity = transaction.Open(runEntityId);
        ref readonly var run = ref runEntity.Read(SimulationRunEntity.Run);
        ref readonly var runState = ref runEntity.Read(SimulationRunEntity.State);
        return new PersistedRun(
            runEntityId,
            run.Seed,
            run.RulesetVersion,
            run.CompletedTicks,
            run.AliveShipCount,
            (SimulationRunStatus)runState.Status);
    }

    private static void ValidateRunIdentity(
        SimulationDefinition definition,
        PersistedRun persistedRun)
    {
        if (persistedRun.Seed != definition.Seed)
        {
            throw new InvalidOperationException(
                $"SimulationRun seed 不匹配：数据库为 {persistedRun.Seed}，当前定义为 {definition.Seed}。");
        }

        if (persistedRun.RulesetVersion != definition.RulesetVersion)
        {
            throw new InvalidOperationException(
                $"SimulationRun ruleset version 不匹配：数据库为 {persistedRun.RulesetVersion}，当前定义为 {definition.RulesetVersion}。");
        }
    }

    private static SpaceBattleRunResult ResumeRunningRun(
        DatabaseEngine engine,
        SimulationDefinition definition,
        PersistedRun persistedRun)
    {
        if (persistedRun.Status != SimulationRunStatus.Running)
        {
            throw new InvalidOperationException($"SimulationRun 状态 {persistedRun.Status} 不可恢复。");
        }

        SpaceBattleCheckpoint.Restore(engine, definition, persistedRun.EntityId, persistedRun.CompletedTicks);
        SpaceBattleRecoveryValidation.ValidateCurrent(engine, definition, persistedRun.EntityId);
        using var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate);
        ref var runState = ref transaction.OpenMut(persistedRun.EntityId).Write(SimulationRunEntity.State);
        runState.ProcessSegment = checked(runState.ProcessSegment + 1);
        transaction.Commit();

        return new SpaceBattleRunResult(
            checked((int)persistedRun.AliveShipCount),
            TimeSpan.Zero,
            SimulationStartupAction.Resumed);
    }

    public static InitialWorldSnapshot ReadSnapshot(
        SimulationDefinition definition,
        string databaseLocation)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseLocation);

        using var engine = SpaceBattleDatabase.Open(definition, databaseLocation);
        return ReadSnapshot(engine);
    }

    internal static InitialWorldSnapshot ReadSnapshot(DatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        using var transaction = engine.CreateReadOnlyTransaction();
        return ReadSnapshot(transaction);
    }

    internal static InitialWorldSnapshot ReadSnapshot(Transaction transaction)
        => ReadSnapshot(transaction, []);

    internal static InitialWorldSnapshot ReadSnapshot(
        Transaction transaction,
        IReadOnlyList<KillParticipation> killParticipations)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var runEntities = transaction.Query<SimulationRunEntity>().Execute();
        if (runEntities.Count != 1)
        {
            throw new InvalidOperationException($"SimulationRun 实体数量必须为 1，实际为 {runEntities.Count}。");
        }

        var runEntity = transaction.Open(runEntities.Single());
        ref readonly var run = ref runEntity.Read(SimulationRunEntity.Run);
        ref readonly var runState = ref runEntity.Read(SimulationRunEntity.State);
        bool usePauseCheckpoint = SpaceBattleCheckpoint.TryReadRun(
            transaction,
            out var checkpoint) &&
            checkpoint.CompletedTicks == run.CompletedTicks;

        var runSnapshot = new SimulationRunSnapshot(
            run.Seed,
            run.CompletedTicks,
            run.RulesetVersion,
            run.InitialShipCount,
            run.AliveShipCount,
            runState.ProcessSegment,
            (SimulationRunStatus)runState.Status,
            (SimulationRunOutcome)runState.Outcome,
            runState.Outcome == (byte)SimulationRunOutcome.Winner
                ? runState.WinnerEntityKey
                : null);

        var shipEntities = transaction.Query<Ship>().Execute().OrderBy(static id => id.EntityKey);
        var ships = new List<ShipSnapshot>();
        foreach (var entityId in shipEntities)
        {
            var entity = transaction.Open(entityId);
            ref readonly var position = ref entity.Read(Ship.Position);
            ref readonly var bounds = ref entity.Read(Ship.SpatialBounds);
            ref readonly var motion = ref entity.Read(Ship.Motion);
            ref readonly var health = ref entity.Read(Ship.Health);
            ref readonly var behavior = ref entity.Read(Ship.Behavior);
            ref readonly var tracking = ref entity.Read(Ship.Tracking);

            ships.Add(new ShipSnapshot(
                entityId.EntityKey,
                new PositionSnapshot(position.X, position.Y, position.Z),
                new SpatialBoundsSnapshot(
                    bounds.Bounds.MinX,
                    bounds.Bounds.MinY,
                    bounds.Bounds.MinZ,
                    bounds.Bounds.MaxX,
                    bounds.Bounds.MaxY,
                    bounds.Bounds.MaxZ),
                new MotionSnapshot(motion.DirectionX, motion.DirectionY, motion.DirectionZ, motion.Speed),
                health.Current,
                (BehaviorMode)behavior.Mode,
                behavior.ModeTicksRemaining,
                tracking.Target.IsNull,
                entity.IsEnabled(Ship.Weapon),
                entity.IsEnabled(Ship.Afterburner)));
        }

        var targetLockEntities = transaction.Query<TargetLock>().Execute().OrderBy(static id => id.EntityKey);
        var targetLocks = new List<TargetLockSnapshot>();
        foreach (var entityId in targetLockEntities)
        {
            var entity = transaction.Open(entityId);
            ref readonly var targetLock = ref entity.Read(TargetLock.Data);
            var owner = (EntityId)targetLock.Owner;
            var target = (EntityId)targetLock.Target;
            targetLocks.Add(new TargetLockSnapshot(
                entityId.EntityKey,
                owner.EntityKey,
                target.EntityKey,
                (TargetLockStatus)targetLock.Status,
                targetLock.TicksRemaining));
        }

        if (usePauseCheckpoint)
        {
            ships = SpaceBattleCheckpoint.ReadShipSnapshots(transaction);
            targetLocks = SpaceBattleCheckpoint.ReadTargetLockSnapshots(transaction);
        }

        List<KillParticipationSnapshot> participationSnapshots = killParticipations
            .Select(static participation => new KillParticipationSnapshot(
                participation.Attacker.EntityKey,
                participation.Target.EntityKey))
            .ToList();
        return new InitialWorldSnapshot(
            runEntities.Count,
            runSnapshot,
            ships,
            targetLocks,
            participationSnapshots);
    }

    private static PositionComponent CreateInitialPosition(
        SimulationDefinition definition,
        ulong packedEntityId) => new()
        {
            X = DeterministicRandom.Coordinate(
                definition.Seed,
                packedEntityId,
                decisionOrdinal: 0,
                InitialPositionX,
                definition.WorldSize),
            Y = DeterministicRandom.Coordinate(
                definition.Seed,
                packedEntityId,
                decisionOrdinal: 0,
                InitialPositionY,
                definition.WorldSize),
            Z = DeterministicRandom.Coordinate(
                definition.Seed,
                packedEntityId,
                decisionOrdinal: 0,
                InitialPositionZ,
                definition.WorldSize),
        };

    private static SpatialBoundsComponent CreatePointBounds(PositionComponent position) => new()
    {
        Bounds = new AABB3F
        {
            MinX = position.X,
            MinY = position.Y,
            MinZ = position.Z,
            MaxX = position.X,
            MaxY = position.Y,
            MaxZ = position.Z,
        },
    };

    private readonly record struct PersistedRun(
        EntityId EntityId,
        ulong Seed,
        uint RulesetVersion,
        ulong CompletedTicks,
        uint AliveShipCount,
        SimulationRunStatus Status);
}
