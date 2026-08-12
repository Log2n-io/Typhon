using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

internal static class SpaceBattleRecoveryValidation
{
    public static void ValidateCurrent(
        DatabaseEngine engine,
        SimulationDefinition definition,
        EntityId runEntityId)
    {
        using var transaction = engine.CreateReadOnlyTransaction();
        ValidateCurrent(transaction, definition, runEntityId);
    }

    public static void ValidateCurrent(
        Transaction transaction,
        SimulationDefinition definition,
        EntityId runEntityId)
    {
        var runEntities = transaction.Query<SimulationRunEntity>().Execute();
        if (runEntities.Count != 1 || runEntities.Single() != runEntityId)
        {
            throw Invalid("SimulationRun 实体在恢复校验期间发生变化。");
        }

        if (!transaction.IsAlive(runEntityId))
        {
            throw Invalid("SimulationRun 实体已不存在。");
        }

        EntityRef runEntity = transaction.Open(runEntityId);
        ref readonly SimulationRunComponent run = ref runEntity.Read(SimulationRunEntity.Run);
        ref readonly SimulationRunStateComponent runState = ref runEntity.Read(SimulationRunEntity.State);
        ValidateRun(definition, run, runState);

        EntityId[] shipIds = transaction.Query<Ship>().Execute().OrderBy(static id => id.EntityKey).ToArray();
        if (run.InitialShipCount != definition.ShipCount)
        {
            throw Invalid($"初始飞船数量 {run.InitialShipCount} 与当前定义 {definition.ShipCount} 不匹配。");
        }

        if (run.AliveShipCount != shipIds.Length)
        {
            throw Invalid($"SimulationRun 存活数量 {run.AliveShipCount} 与权威飞船数量 {shipIds.Length} 不匹配。");
        }

        var shipIdsByEntityKey = shipIds.ToDictionary(static id => id.EntityKey);
        var lockedTargetsByOwner = new Dictionary<long, long>();
        var occupiedLockCounts = new Dictionary<long, int>();
        foreach (EntityId shipId in shipIds)
        {
            ValidateShip(transaction, definition, run.CompletedTicks, shipId, shipIdsByEntityKey);
        }

        foreach (EntityId targetLockId in transaction.Query<TargetLock>().Execute())
        {
            EntityRef targetLockEntity = transaction.Open(targetLockId);
            ref readonly TargetLockComponent targetLock = ref targetLockEntity.Read(TargetLock.Data);
            ValidateTargetLock(
                transaction,
                shipIdsByEntityKey,
                targetLockId,
                targetLock,
                lockedTargetsByOwner,
                occupiedLockCounts);
        }

        ValidateEquipmentAndLocks(transaction, shipIds, lockedTargetsByOwner);
        ValidateTerminalState(run, runState, shipIds);
        ValidatePauseCheckpointEnvelope(runEntity, run.CompletedTicks);
    }

    private static void ValidateRun(
        SimulationDefinition definition,
        SimulationRunComponent run,
        SimulationRunStateComponent runState)
    {
        if (run.Seed != definition.Seed)
        {
            throw Invalid($"SimulationRun seed 不匹配：数据库为 {run.Seed}，当前定义为 {definition.Seed}。");
        }

        if (run.RulesetVersion != definition.RulesetVersion)
        {
            throw Invalid($"SimulationRun ruleset version 不匹配：数据库为 {run.RulesetVersion}，当前定义为 {definition.RulesetVersion}。");
        }

        if (run.AliveShipCount > run.InitialShipCount)
        {
            throw Invalid("SimulationRun 存活数量超过初始飞船数量。");
        }

        if (run.CompletedTicks > definition.MaximumCompletedTicks)
        {
            throw Invalid("SimulationRun 完成 tick 超过最大 tick 限制。");
        }

        if (runState.ProcessSegment == 0 || runState.ProcessSegment == uint.MaxValue)
        {
            throw Invalid("SimulationRun process segment 无法安全递增。");
        }

        switch ((SimulationRunStatus)runState.Status)
        {
            case SimulationRunStatus.Running:
                if (runState.Outcome != (byte)SimulationRunOutcome.None || runState.WinnerEntityKey != 0)
                {
                    throw Invalid("运行中的 SimulationRun 带有非法终态字段。");
                }

                break;
            case SimulationRunStatus.Completed:
                if (runState.Outcome is not ((byte)SimulationRunOutcome.Winner or (byte)SimulationRunOutcome.Draw))
                {
                    throw Invalid("已完成 SimulationRun 的 outcome 非法。");
                }

                break;
            case SimulationRunStatus.TimedOut:
                if (runState.Outcome != (byte)SimulationRunOutcome.TimedOut ||
                    runState.WinnerEntityKey != 0 ||
                    run.CompletedTicks < definition.MaximumCompletedTicks)
                {
                    throw Invalid("超时 SimulationRun 的终态字段非法。");
                }

                break;
            default:
                throw Invalid($"SimulationRun 状态值 {runState.Status} 非法。");
        }
    }

    private static void ValidateShip(
        Transaction transaction,
        SimulationDefinition definition,
        ulong completedTicks,
        EntityId shipId,
        IReadOnlyDictionary<long, EntityId> shipIdsByEntityKey)
    {
        EntityRef ship = transaction.Open(shipId);
        ref readonly PositionComponent position = ref ship.Read(Ship.Position);
        ref readonly SpatialBoundsComponent bounds = ref ship.Read(Ship.SpatialBounds);
        ref readonly MotionComponent motion = ref ship.Read(Ship.Motion);
        ref readonly HealthComponent health = ref ship.Read(Ship.Health);
        ref readonly BehaviorComponent behavior = ref ship.Read(Ship.Behavior);
        ref readonly TrackingComponent tracking = ref ship.Read(Ship.Tracking);

        ValidatePosition(position, definition.WorldSize, $"飞船 {shipId.EntityKey}");
        ValidateBounds(bounds, position, definition.WorldSize, $"飞船 {shipId.EntityKey}");
        ValidateMotion(motion, $"飞船 {shipId.EntityKey}");
        if (health.Current == 0 || health.Current > definition.MaximumHealth)
        {
            throw Invalid($"飞船 {shipId.EntityKey} 的生命值 {health.Current} 非法。");
        }

        if (!Enum.IsDefined((BehaviorMode)behavior.Mode))
        {
            throw Invalid($"飞船 {shipId.EntityKey} 的行为模式 {behavior.Mode} 非法。");
        }

        ValidateBehaviorTimers(
            (BehaviorMode)behavior.Mode,
            behavior.ModeTicksRemaining,
            definition,
            $"飞船 {shipId.EntityKey}");

        EntityId trackingTarget = tracking.Target;
        if (!trackingTarget.IsNull &&
            (!shipIdsByEntityKey.TryGetValue(trackingTarget.EntityKey, out EntityId targetId) ||
             targetId != trackingTarget ||
             targetId == shipId))
        {
            throw Invalid($"飞船 {shipId.EntityKey} 的追踪目标无效。");
        }

        bool weaponEnabled = ship.IsEnabled(Ship.Weapon);
        bool afterburnerEnabled = ship.IsEnabled(Ship.Afterburner);
        if (weaponEnabled && afterburnerEnabled)
        {
            throw Invalid($"飞船 {shipId.EntityKey} 同时启用了武器和加力器。");
        }

        if (weaponEnabled && (BehaviorMode)behavior.Mode != BehaviorMode.Combat)
        {
            throw Invalid($"飞船 {shipId.EntityKey} 在非 Combat 模式启用了武器。");
        }

        if (weaponEnabled && ship.Read(Ship.Weapon).CooldownTicksRemaining > BehaviorRules.WeaponFireIntervalTicks)
        {
            throw Invalid($"飞船 {shipId.EntityKey} 的武器冷却 tick 非法。");
        }

        if (afterburnerEnabled && (BehaviorMode)behavior.Mode != BehaviorMode.Escaping)
        {
            throw Invalid($"飞船 {shipId.EntityKey} 在非 Escaping 模式启用了加力器。");
        }

        if (afterburnerEnabled)
        {
            ref readonly AfterburnerComponent afterburner = ref ship.Read(Ship.Afterburner);
            if (afterburner.ActivatedTick > completedTicks)
            {
                throw Invalid($"飞船 {shipId.EntityKey} 的加力器启动 tick 非法。");
            }
        }
    }

    private static void ValidateTargetLock(
        Transaction transaction,
        IReadOnlyDictionary<long, EntityId> shipIdsByEntityKey,
        EntityId targetLockId,
        TargetLockComponent targetLock,
        IDictionary<long, long> lockedTargetsByOwner,
        IDictionary<long, int> occupiedLockCounts)
    {
        EntityId owner = targetLock.Owner;
        EntityId target = targetLock.Target;
        if (owner.IsNull ||
            target.IsNull ||
            !shipIdsByEntityKey.TryGetValue(owner.EntityKey, out EntityId ownerId) ||
            ownerId != owner ||
            !shipIdsByEntityKey.TryGetValue(target.EntityKey, out EntityId targetId) ||
            targetId != target ||
            owner == target)
        {
            throw Invalid($"目标锁 {targetLockId.EntityKey} 的端点悬挂或自指向。");
        }

        TargetLockStatus status = (TargetLockStatus)targetLock.Status;
        if (!Enum.IsDefined(status))
        {
            throw Invalid($"目标锁 {targetLockId.EntityKey} 的状态 {targetLock.Status} 非法。");
        }

        if (status is TargetLockStatus.Acquiring or TargetLockStatus.Releasing &&
            targetLock.TicksRemaining == 0)
        {
            throw Invalid($"目标锁 {targetLockId.EntityKey} 的剩余 tick 非法。");
        }

        if (status is TargetLockStatus.Acquiring or TargetLockStatus.Locked)
        {
            ref readonly BehaviorComponent ownerBehavior = ref transaction.Open(owner).Read(Ship.Behavior);
            if ((BehaviorMode)ownerBehavior.Mode != BehaviorMode.Combat)
            {
                throw Invalid($"目标锁 {targetLockId.EntityKey} 的 owner 不在 Combat 模式。");
            }
        }

        if (status == TargetLockStatus.Locked)
        {
            lockedTargetsByOwner.Add(owner.EntityKey, target.EntityKey);
        }

        int occupiedLockCount = occupiedLockCounts.TryGetValue(owner.EntityKey, out int currentLockCount)
            ? currentLockCount + 1
            : 1;
        if (occupiedLockCount > BehaviorRules.MaximumTargetLocksPerShip)
        {
            throw Invalid($"飞船 {owner.EntityKey} 占用超过最大锁定数量的目标锁槽位。");
        }

        occupiedLockCounts[owner.EntityKey] = occupiedLockCount;
    }

    private static void ValidateEquipmentAndLocks(
        Transaction transaction,
        IReadOnlyList<EntityId> shipIds,
        IReadOnlyDictionary<long, long> lockedTargetsByOwner)
    {
        foreach (EntityId shipId in shipIds)
        {
            EntityRef ship = transaction.Open(shipId);
            if (!ship.IsEnabled(Ship.Weapon))
            {
                continue;
            }

            ref readonly TrackingComponent tracking = ref ship.Read(Ship.Tracking);
            EntityId trackingTarget = tracking.Target;
            if (!lockedTargetsByOwner.TryGetValue(shipId.EntityKey, out long targetEntityKey) ||
                trackingTarget.IsNull ||
                trackingTarget.EntityKey != targetEntityKey)
            {
                throw Invalid($"飞船 {shipId.EntityKey} 的武器没有对应的 Locked 目标锁。");
            }
        }
    }

    private static void ValidatePauseCheckpointEnvelope(EntityRef runEntity, ulong completedTicks)
    {
        ref readonly PauseRunCheckpointComponent checkpoint = ref runEntity.Read(SimulationRunEntity.PauseCheckpoint);
        if (checkpoint.IsValid > 1)
        {
            throw Invalid("SimulationRun 暂停检查点标记非法。");
        }

        if (checkpoint.IsValid == 1 && checkpoint.CompletedTicks > completedTicks)
        {
            throw Invalid("暂停检查点位于当前完成 tick 之后。");
        }
    }

    internal static void ValidatePosition(
        PositionComponent position,
        float worldSize,
        string subject)
    {
        if (!float.IsFinite(position.X) ||
            !float.IsFinite(position.Y) ||
            !float.IsFinite(position.Z))
        {
            throw Invalid($"{subject} 的坐标包含非有限值。");
        }

        if (position.X < 0f || position.X > worldSize ||
            position.Y < 0f || position.Y > worldSize ||
            position.Z < 0f || position.Z > worldSize)
        {
            throw Invalid($"{subject} 的坐标超出战斗世界。");
        }
    }

    internal static void ValidateBounds(
        SpatialBoundsComponent bounds,
        PositionComponent position,
        float worldSize,
        string subject)
    {
        AABB3F value = bounds.Bounds;
        if (!float.IsFinite(value.MinX) ||
            !float.IsFinite(value.MinY) ||
            !float.IsFinite(value.MinZ) ||
            !float.IsFinite(value.MaxX) ||
            !float.IsFinite(value.MaxY) ||
            !float.IsFinite(value.MaxZ))
        {
            throw Invalid($"{subject} 的空间边界包含非有限值。");
        }

        if (value.MinX != position.X || value.MinY != position.Y || value.MinZ != position.Z ||
            value.MaxX != position.X || value.MaxY != position.Y || value.MaxZ != position.Z)
        {
            throw Invalid($"{subject} 的空间边界不是位置的点边界。");
        }

        if (value.MinX < 0f || value.MinY < 0f || value.MinZ < 0f ||
            value.MaxX > worldSize || value.MaxY > worldSize || value.MaxZ > worldSize)
        {
            throw Invalid($"{subject} 的空间边界超出战斗世界。");
        }
    }

    internal static void ValidateMotion(MotionComponent motion, string subject)
    {
        if (!float.IsFinite(motion.DirectionX) ||
            !float.IsFinite(motion.DirectionY) ||
            !float.IsFinite(motion.DirectionZ) ||
            !float.IsFinite(motion.Speed) ||
            motion.Speed < 0f)
        {
            throw Invalid($"{subject} 的运动状态非法。");
        }

        float lengthSquared =
            (motion.DirectionX * motion.DirectionX) +
            (motion.DirectionY * motion.DirectionY) +
            (motion.DirectionZ * motion.DirectionZ);
        if (!float.IsFinite(lengthSquared) ||
            lengthSquared <= 0f ||
            MathF.Abs(lengthSquared - 1f) > 0.001f)
        {
            throw Invalid($"{subject} 的方向向量未归一化。");
        }
    }

    internal static void ValidateBehaviorTimers(
        BehaviorMode mode,
        ushort modeTicksRemaining,
        SimulationDefinition definition,
        string subject)
    {
        ushort maximum = mode switch
        {
            BehaviorMode.Staging => definition.StagingTicks,
            BehaviorMode.Wandering => BehaviorRules.WanderingDecisionIntervalTicks,
            BehaviorMode.Tracking => BehaviorRules.TrackingDurationTicks,
            BehaviorMode.Combat => checked((ushort)(BehaviorRules.CombatAcquisitionDurationTicks + 1)),
            BehaviorMode.Disengaging => BehaviorRules.DisengagingDurationTicks,
            BehaviorMode.Escaping => BehaviorRules.EscapingDurationTicks,
            _ => throw Invalid($"{subject} 的行为模式非法。"),
        };

        if (modeTicksRemaining > maximum)
        {
            throw Invalid($"{subject} 的模式剩余 tick 非法。");
        }
    }

    private static void ValidateTerminalState(
        SimulationRunComponent run,
        SimulationRunStateComponent runState,
        IReadOnlyList<EntityId> shipIds)
    {
        switch ((SimulationRunStatus)runState.Status, (SimulationRunOutcome)runState.Outcome)
        {
            case (SimulationRunStatus.Completed, SimulationRunOutcome.Winner):
                if (run.AliveShipCount != 1 ||
                    runState.WinnerEntityKey == 0 ||
                    !shipIds.Any(id => id.EntityKey == runState.WinnerEntityKey))
                {
                    throw Invalid("胜者终态与权威飞船状态不一致。");
                }

                break;
            case (SimulationRunStatus.Completed, SimulationRunOutcome.Draw):
                if (run.AliveShipCount != 0 || runState.WinnerEntityKey != 0)
                {
                    throw Invalid("平局终态与权威飞船状态不一致。");
                }

                break;
            case (SimulationRunStatus.TimedOut, SimulationRunOutcome.TimedOut):
                if (run.AliveShipCount <= 1)
                {
                    throw Invalid("超时终态的存活数量不一致。");
                }

                break;
        }
    }

    internal static InvalidOperationException Invalid(string message)
        => new($"恢复校验失败：{message}");
}
