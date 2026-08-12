using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

internal readonly record struct PauseRunCheckpointSnapshot(
    ulong CompletedTicks,
    uint AliveShipCount);

internal static class SpaceBattleCheckpoint
{
    public static bool TryReadRun(
        Transaction transaction,
        out PauseRunCheckpointSnapshot checkpoint)
    {
        var runEntities = transaction.Query<SimulationRunEntity>().Execute();
        if (runEntities.Count != 1)
        {
            checkpoint = default;
            return false;
        }

        EntityId runEntityId = runEntities.Single();
        ref readonly PauseRunCheckpointComponent data = ref transaction
            .Open(runEntityId)
            .Read(SimulationRunEntity.PauseCheckpoint);
        if (data.IsValid > 1)
        {
            throw SpaceBattleRecoveryValidation.Invalid("SimulationRun 暂停检查点标记非法。");
        }

        if (data.IsValid == 0)
        {
            checkpoint = default;
            return false;
        }

        ref readonly SimulationRunComponent run = ref transaction
            .Open(runEntityId)
            .Read(SimulationRunEntity.Run);
        if (data.CompletedTicks > run.CompletedTicks)
        {
            throw SpaceBattleRecoveryValidation.Invalid("暂停检查点位于当前完成 tick 之后。");
        }

        checkpoint = new PauseRunCheckpointSnapshot(data.CompletedTicks, data.AliveShipCount);
        return true;
    }

    public static void Persist(
        DatabaseEngine engine,
        EntityId runEntityId,
        ulong completedTicks)
    {
        using var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate);
        EntityRef runEntity = transaction.OpenMut(runEntityId);
        uint aliveShipCount = 0;
        foreach (EntityId shipId in transaction.Query<Ship>().Execute())
        {
            aliveShipCount++;
            EntityRef ship = transaction.OpenMut(shipId);
            ship.Write(Ship.PauseCheckpoint) = CaptureShip(ship);
        }

        runEntity.Write(SimulationRunEntity.PauseCheckpoint) = new PauseRunCheckpointComponent
        {
            CompletedTicks = completedTicks,
            AliveShipCount = aliveShipCount,
            IsValid = 1,
        };

        foreach (EntityId targetLockId in transaction.Query<TargetLock>().Execute())
        {
            EntityRef targetLock = transaction.OpenMut(targetLockId);
            ref readonly TargetLockComponent data = ref targetLock.Read(TargetLock.Data);
            EntityId owner = data.Owner;
            EntityId target = data.Target;
            targetLock.Write(TargetLock.PauseCheckpoint) = new PauseTargetLockCheckpointComponent
            {
                IsValid = 1,
                OwnerEntityKey = owner.EntityKey,
                TargetEntityKey = target.EntityKey,
                TicksRemaining = data.TicksRemaining,
                Status = data.Status,
            };
        }

        transaction.Commit();
    }

    public static void Restore(
        DatabaseEngine engine,
        SimulationDefinition definition,
        EntityId runEntityId,
        ulong completedTicks)
    {
        using var transaction = engine.CreateQuickTransaction(DurabilityMode.Immediate);
        EntityRef runEntity = transaction.Open(runEntityId);
        ref readonly PauseRunCheckpointComponent runCheckpoint = ref runEntity
            .Read(SimulationRunEntity.PauseCheckpoint);
        if (runCheckpoint.IsValid > 1)
        {
            throw SpaceBattleRecoveryValidation.Invalid("SimulationRun 暂停检查点标记非法。");
        }

        if (runCheckpoint.IsValid == 0)
        {
            transaction.Commit();
            return;
        }

        if (runCheckpoint.CompletedTicks > completedTicks)
        {
            throw SpaceBattleRecoveryValidation.Invalid("暂停检查点位于当前完成 tick 之后。");
        }

        if (runCheckpoint.CompletedTicks < completedTicks)
        {
            // 这是更早进程段留下的检查点；当前 tick-fence 主数据才是恢复权威，不能用旧快照覆盖它。
            transaction.Commit();
            return;
        }

        EntityId[] shipIds = transaction.Query<Ship>().Execute()
            .OrderBy(static id => id.EntityKey)
            .ToArray();
        if (runCheckpoint.AliveShipCount != shipIds.Length)
        {
            throw SpaceBattleRecoveryValidation.Invalid("暂停检查点的飞船数量与权威飞船数量不匹配。");
        }

        ref readonly SimulationRunComponent run = ref runEntity.Read(SimulationRunEntity.Run);
        if (run.AliveShipCount != runCheckpoint.AliveShipCount)
        {
            throw SpaceBattleRecoveryValidation.Invalid("暂停检查点的存活数量与 SimulationRun 不匹配。");
        }

        var shipsByEntityKey = shipIds.ToDictionary(static entityId => entityId.EntityKey);
        var validatedShips = new List<ValidatedShipCheckpoint>(shipIds.Length);
        foreach (EntityId shipId in shipIds)
        {
            EntityRef ship = transaction.Open(shipId);
            ref readonly PauseShipCheckpointComponent checkpoint = ref ship.Read(Ship.PauseCheckpoint);
            if (checkpoint.IsValid != 1)
            {
                throw SpaceBattleRecoveryValidation.Invalid($"舰船 {shipId.EntityKey} 缺少暂停检查点。");
            }

            ValidateShipCheckpoint(definition, completedTicks, shipId, checkpoint, shipsByEntityKey);
            validatedShips.Add(new ValidatedShipCheckpoint(shipId, checkpoint));
        }

        var lockedTargetsByOwner = new Dictionary<long, long>();
        var occupiedLockCounts = new Dictionary<long, int>();
        var shipModes = validatedShips.ToDictionary(
            static ship => ship.EntityId.EntityKey,
            static ship => (BehaviorMode)ship.Checkpoint.Mode);
        var validatedTargetLocks = new List<ValidatedTargetLockCheckpoint>();
        foreach (EntityId targetLockId in transaction.Query<TargetLock>().Execute())
        {
            EntityRef targetLock = transaction.Open(targetLockId);
            ref readonly PauseTargetLockCheckpointComponent checkpoint = ref targetLock
                .Read(TargetLock.PauseCheckpoint);
            if (checkpoint.IsValid != 1)
            {
                throw SpaceBattleRecoveryValidation.Invalid($"目标锁 {targetLockId.EntityKey} 缺少暂停检查点。");
            }

            if (!shipsByEntityKey.TryGetValue(checkpoint.OwnerEntityKey, out EntityId owner) ||
                !shipsByEntityKey.TryGetValue(checkpoint.TargetEntityKey, out EntityId target))
            {
                throw SpaceBattleRecoveryValidation.Invalid($"目标锁 {targetLockId.EntityKey} 的端点不在暂停检查点中。");
            }

            ValidateTargetLockCheckpoint(
                targetLockId,
                checkpoint,
                owner,
                target,
                lockedTargetsByOwner,
                occupiedLockCounts,
                shipsByEntityKey,
                shipModes);
            validatedTargetLocks.Add(new ValidatedTargetLockCheckpoint(targetLockId, checkpoint, owner, target));
        }

        ValidateCheckpointEquipment(validatedShips, lockedTargetsByOwner);

        foreach (ValidatedShipCheckpoint validatedShip in validatedShips)
        {
            EntityRef ship = transaction.OpenMut(validatedShip.EntityId);
            PauseShipCheckpointComponent checkpoint = validatedShip.Checkpoint;
            ship.Write(Ship.Position) = new PositionComponent
            {
                X = checkpoint.PositionX,
                Y = checkpoint.PositionY,
                Z = checkpoint.PositionZ,
            };
            ship.Write(Ship.SpatialBounds) = new SpatialBoundsComponent
            {
                Bounds = new AABB3F
                {
                    MinX = checkpoint.BoundsMinX,
                    MinY = checkpoint.BoundsMinY,
                    MinZ = checkpoint.BoundsMinZ,
                    MaxX = checkpoint.BoundsMaxX,
                    MaxY = checkpoint.BoundsMaxY,
                    MaxZ = checkpoint.BoundsMaxZ,
                },
            };
            ship.Write(Ship.Motion) = new MotionComponent
            {
                DirectionX = checkpoint.DirectionX,
                DirectionY = checkpoint.DirectionY,
                DirectionZ = checkpoint.DirectionZ,
                Speed = checkpoint.Speed,
            };
            ship.Write(Ship.Health).Current = checkpoint.Health;
            ship.Write(Ship.Behavior) = new BehaviorComponent
            {
                DecisionOrdinal = checkpoint.DecisionOrdinal,
                ModeTicksRemaining = checkpoint.ModeTicksRemaining,
                Mode = checkpoint.Mode,
            };
            ref TrackingComponent tracking = ref ship.Write(Ship.Tracking);
            tracking.Target = checkpoint.TrackingTargetEntityKey == 0
                ? EntityLink<Ship>.Null
                : shipsByEntityKey[checkpoint.TrackingTargetEntityKey];
            tracking.TrackingTicksRemaining = checkpoint.TrackingTicksRemaining;

            RestoreEnabledComponent(
                ref ship,
                Ship.Weapon,
                checkpoint.WeaponEnabled == 1,
                new WeaponComponent { CooldownTicksRemaining = checkpoint.CooldownTicksRemaining });
            RestoreEnabledComponent(
                ref ship,
                Ship.Afterburner,
                checkpoint.AfterburnerEnabled == 1,
                new AfterburnerComponent { ActivatedTick = checkpoint.AfterburnerActivatedTick });
        }

        foreach (ValidatedTargetLockCheckpoint validatedTargetLock in validatedTargetLocks)
        {
            EntityRef targetLock = transaction.OpenMut(validatedTargetLock.EntityId);
            PauseTargetLockCheckpointComponent checkpoint = validatedTargetLock.Checkpoint;
            ref readonly TargetLockComponent currentData = ref targetLock.Read(TargetLock.Data);
            if ((EntityId)currentData.Owner != validatedTargetLock.Owner ||
                (EntityId)currentData.Target != validatedTargetLock.Target)
            {
                throw SpaceBattleRecoveryValidation.Invalid(
                    $"目标锁 {validatedTargetLock.EntityId.EntityKey} 的权威端点与暂停检查点不一致。");
            }

            ref TargetLockComponent data = ref targetLock.Write(TargetLock.Data);
            data.TicksRemaining = checkpoint.TicksRemaining;
            data.Status = checkpoint.Status;
        }

        transaction.Commit();
    }

    public static List<ShipSnapshot> ReadShipSnapshots(Transaction transaction)
    {
        var snapshots = new List<ShipSnapshot>();
        foreach (EntityId shipId in transaction.Query<Ship>().Execute())
        {
            EntityRef ship = transaction.Open(shipId);
            ref readonly PauseShipCheckpointComponent checkpoint = ref ship
                .Read(Ship.PauseCheckpoint);
            if (checkpoint.IsValid != 1)
            {
                throw SpaceBattleRecoveryValidation.Invalid($"舰船 {shipId.EntityKey} 缺少暂停检查点。");
            }

            snapshots.Add(new ShipSnapshot(
                shipId.EntityKey,
                new PositionSnapshot(checkpoint.PositionX, checkpoint.PositionY, checkpoint.PositionZ),
                new SpatialBoundsSnapshot(
                    checkpoint.BoundsMinX,
                    checkpoint.BoundsMinY,
                    checkpoint.BoundsMinZ,
                    checkpoint.BoundsMaxX,
                    checkpoint.BoundsMaxY,
                    checkpoint.BoundsMaxZ),
                new MotionSnapshot(
                    checkpoint.DirectionX,
                    checkpoint.DirectionY,
                    checkpoint.DirectionZ,
                    checkpoint.Speed),
                checkpoint.Health,
                (BehaviorMode)checkpoint.Mode,
                checkpoint.ModeTicksRemaining,
                checkpoint.TrackingTargetEntityKey == 0,
                checkpoint.WeaponEnabled != 0,
                checkpoint.AfterburnerEnabled != 0));
        }

        snapshots.Sort(static (left, right) => left.EntityKey.CompareTo(right.EntityKey));
        return snapshots;
    }

    public static List<TargetLockSnapshot> ReadTargetLockSnapshots(Transaction transaction)
    {
        var snapshots = new List<TargetLockSnapshot>();
        foreach (EntityId targetLockId in transaction.Query<TargetLock>().Execute())
        {
            EntityRef targetLock = transaction.Open(targetLockId);
            ref readonly PauseTargetLockCheckpointComponent checkpoint = ref targetLock
                .Read(TargetLock.PauseCheckpoint);
            if (checkpoint.IsValid != 1)
            {
                throw SpaceBattleRecoveryValidation.Invalid($"目标锁 {targetLockId.EntityKey} 缺少暂停检查点。");
            }

            snapshots.Add(new TargetLockSnapshot(
                targetLockId.EntityKey,
                checkpoint.OwnerEntityKey,
                checkpoint.TargetEntityKey,
                (TargetLockStatus)checkpoint.Status,
                checkpoint.TicksRemaining));
        }

        snapshots.Sort(static (left, right) => left.EntityKey.CompareTo(right.EntityKey));
        return snapshots;
    }

    private static PauseShipCheckpointComponent CaptureShip(EntityRef ship)
    {
        ref readonly PositionComponent position = ref ship.Read(Ship.Position);
        ref readonly SpatialBoundsComponent bounds = ref ship.Read(Ship.SpatialBounds);
        ref readonly MotionComponent motion = ref ship.Read(Ship.Motion);
        ref readonly HealthComponent health = ref ship.Read(Ship.Health);
        ref readonly BehaviorComponent behavior = ref ship.Read(Ship.Behavior);
        ref readonly TrackingComponent tracking = ref ship.Read(Ship.Tracking);
        WeaponComponent weapon = ship.IsEnabled(Ship.Weapon)
            ? ship.Read(Ship.Weapon)
            : default;
        AfterburnerComponent afterburner = ship.IsEnabled(Ship.Afterburner)
            ? ship.Read(Ship.Afterburner)
            : default;
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

    private static void ValidateShipCheckpoint(
        SimulationDefinition definition,
        ulong completedTicks,
        EntityId shipId,
        PauseShipCheckpointComponent checkpoint,
        IReadOnlyDictionary<long, EntityId> shipsByEntityKey)
    {
        if (checkpoint.WeaponEnabled > 1 || checkpoint.AfterburnerEnabled > 1)
        {
            throw SpaceBattleRecoveryValidation.Invalid($"舰船 {shipId.EntityKey} 的装备启用标记非法。");
        }

        PositionComponent position = new()
        {
            X = checkpoint.PositionX,
            Y = checkpoint.PositionY,
            Z = checkpoint.PositionZ,
        };
        SpatialBoundsComponent bounds = new()
        {
            Bounds = new AABB3F
            {
                MinX = checkpoint.BoundsMinX,
                MinY = checkpoint.BoundsMinY,
                MinZ = checkpoint.BoundsMinZ,
                MaxX = checkpoint.BoundsMaxX,
                MaxY = checkpoint.BoundsMaxY,
                MaxZ = checkpoint.BoundsMaxZ,
            },
        };
        MotionComponent motion = new()
        {
            DirectionX = checkpoint.DirectionX,
            DirectionY = checkpoint.DirectionY,
            DirectionZ = checkpoint.DirectionZ,
            Speed = checkpoint.Speed,
        };
        SpaceBattleRecoveryValidation.ValidatePosition(position, definition.WorldSize, $"暂停检查点中的飞船 {shipId.EntityKey}");
        SpaceBattleRecoveryValidation.ValidateBounds(bounds, position, definition.WorldSize, $"暂停检查点中的飞船 {shipId.EntityKey}");
        SpaceBattleRecoveryValidation.ValidateMotion(motion, $"暂停检查点中的飞船 {shipId.EntityKey}");
        if (checkpoint.Health == 0 || checkpoint.Health > definition.MaximumHealth)
        {
            throw SpaceBattleRecoveryValidation.Invalid($"暂停检查点中的飞船 {shipId.EntityKey} 生命值非法。");
        }

        BehaviorMode mode = (BehaviorMode)checkpoint.Mode;
        if (!Enum.IsDefined(mode))
        {
            throw SpaceBattleRecoveryValidation.Invalid($"暂停检查点中的飞船 {shipId.EntityKey} 行为模式非法。");
        }

        SpaceBattleRecoveryValidation.ValidateBehaviorTimers(
            mode,
            checkpoint.ModeTicksRemaining,
            definition,
            $"暂停检查点中的飞船 {shipId.EntityKey}");

        if ((checkpoint.WeaponEnabled == 1 && mode != BehaviorMode.Combat) ||
            (checkpoint.AfterburnerEnabled == 1 && mode != BehaviorMode.Escaping) ||
            (checkpoint.WeaponEnabled == 1 && checkpoint.AfterburnerEnabled == 1))
        {
            throw SpaceBattleRecoveryValidation.Invalid($"暂停检查点中的飞船 {shipId.EntityKey} 装备组合非法。");
        }

        if (checkpoint.WeaponEnabled == 1 &&
            checkpoint.CooldownTicksRemaining > BehaviorRules.WeaponFireIntervalTicks)
        {
            throw SpaceBattleRecoveryValidation.Invalid($"暂停检查点中的飞船 {shipId.EntityKey} 武器冷却 tick 非法。");
        }

        if (checkpoint.AfterburnerEnabled == 1 && checkpoint.AfterburnerActivatedTick > completedTicks)
        {
            throw SpaceBattleRecoveryValidation.Invalid($"暂停检查点中的飞船 {shipId.EntityKey} 加力器启动 tick 非法。");
        }

        if (checkpoint.TrackingTargetEntityKey != 0 &&
            (!shipsByEntityKey.TryGetValue(checkpoint.TrackingTargetEntityKey, out EntityId target) ||
             target.EntityKey == shipId.EntityKey))
        {
            throw SpaceBattleRecoveryValidation.Invalid($"暂停检查点中的飞船 {shipId.EntityKey} 追踪目标无效。");
        }
    }

    private static void ValidateTargetLockCheckpoint(
        EntityId targetLockId,
        PauseTargetLockCheckpointComponent checkpoint,
        EntityId owner,
        EntityId target,
        IDictionary<long, long> lockedTargetsByOwner,
        IDictionary<long, int> occupiedLockCounts,
        IReadOnlyDictionary<long, EntityId> shipsByEntityKey,
        IReadOnlyDictionary<long, BehaviorMode> shipModes)
    {
        if (checkpoint.Status is not ((byte)TargetLockStatus.Acquiring or
            (byte)TargetLockStatus.Locked or
            (byte)TargetLockStatus.Releasing))
        {
            throw SpaceBattleRecoveryValidation.Invalid($"暂停检查点中的目标锁 {targetLockId.EntityKey} 状态非法。");
        }

        if ((checkpoint.Status is (byte)TargetLockStatus.Acquiring or (byte)TargetLockStatus.Releasing) &&
            checkpoint.TicksRemaining == 0)
        {
            throw SpaceBattleRecoveryValidation.Invalid($"暂停检查点中的目标锁 {targetLockId.EntityKey} 剩余 tick 非法。");
        }

        if (owner == target)
        {
            throw SpaceBattleRecoveryValidation.Invalid($"暂停检查点中的目标锁 {targetLockId.EntityKey} 自指向。");
        }

        if ((checkpoint.Status is (byte)TargetLockStatus.Acquiring or (byte)TargetLockStatus.Locked) &&
            (!shipsByEntityKey.TryGetValue(owner.EntityKey, out _) ||
             shipModes[owner.EntityKey] != BehaviorMode.Combat))
        {
            throw SpaceBattleRecoveryValidation.Invalid($"暂停检查点中的目标锁 {targetLockId.EntityKey} owner 无效。");
        }

        int occupiedLockCount = occupiedLockCounts.TryGetValue(owner.EntityKey, out int currentLockCount)
            ? currentLockCount + 1
            : 1;
        if (occupiedLockCount > BehaviorRules.MaximumTargetLocksPerShip)
        {
            throw SpaceBattleRecoveryValidation.Invalid($"暂停检查点中的飞船 {owner.EntityKey} 占用超过最大锁定数量的目标锁槽位。");
        }

        occupiedLockCounts[owner.EntityKey] = occupiedLockCount;

        if (checkpoint.Status == (byte)TargetLockStatus.Locked &&
            !lockedTargetsByOwner.TryAdd(owner.EntityKey, target.EntityKey))
        {
            throw SpaceBattleRecoveryValidation.Invalid($"暂停检查点中的飞船 {owner.EntityKey} 拥有多个 Locked 目标锁。");
        }
    }

    private static void ValidateCheckpointEquipment(
        IReadOnlyList<ValidatedShipCheckpoint> ships,
        IReadOnlyDictionary<long, long> lockedTargetsByOwner)
    {
        foreach (ValidatedShipCheckpoint validatedShip in ships)
        {
            PauseShipCheckpointComponent checkpoint = validatedShip.Checkpoint;
            if (checkpoint.WeaponEnabled != 1)
            {
                continue;
            }

            if (!lockedTargetsByOwner.TryGetValue(validatedShip.EntityId.EntityKey, out long targetEntityKey) ||
                checkpoint.TrackingTargetEntityKey != targetEntityKey)
            {
                throw SpaceBattleRecoveryValidation.Invalid($"暂停检查点中的飞船 {validatedShip.EntityId.EntityKey} 武器没有对应的 Locked 目标锁。");
            }
        }
    }

    private static void RestoreEnabledComponent<T>(
        ref EntityRef ship,
        Comp<T> component,
        bool enabled,
        T value)
        where T : unmanaged
    {
        bool wasEnabled = ship.IsEnabled(component);
        if (enabled && !wasEnabled)
        {
            ship.Enable(component);
        }

        if (enabled || wasEnabled)
        {
            ship.Write(component) = value;
        }

        if (!enabled && wasEnabled)
        {
            ship.Disable(component);
        }
    }

    private readonly record struct ValidatedShipCheckpoint(
        EntityId EntityId,
        PauseShipCheckpointComponent Checkpoint);

    private readonly record struct ValidatedTargetLockCheckpoint(
        EntityId EntityId,
        PauseTargetLockCheckpointComponent Checkpoint,
        EntityId Owner,
        EntityId Target);
}
