using System.Buffers;
using System.Diagnostics;
using Typhon.Engine;

namespace SpaceBattle;

public sealed class SpaceBattleSimulation : IDisposable
{
    private readonly DatabaseEngine _engine;
    private readonly TyphonRuntime _runtime;
    private readonly SimulationRuntimeState _state;
    private readonly SpaceBattleObservationPublisher _observationPublisher;
    private readonly object _terminalPersistenceSync = new();
    private readonly object _pausePersistenceSync = new();
    private CancellationTokenRegistration _pauseCancellation;
    private bool _terminalStatePersisted;
    private bool _pauseStatePersisted;
    private int _disposed;

    internal SpaceBattleSimulation(
        DatabaseEngine engine,
        TyphonRuntime runtime,
        SimulationRuntimeState state,
        SpaceBattleRunResult startupResult,
        SpaceBattleObservationPublisher observationPublisher)
    {
        _engine = engine;
        _runtime = runtime;
        _state = state;
        _observationPublisher = observationPublisher;
        StartupResult = startupResult;
    }

    public SpaceBattleRunResult StartupResult { get; }

    public int TickRate => _runtime.Options.BaseTickRate;

    public float SimulationDeltaSeconds => SimulationDefinition.FixedSimulationDeltaSeconds;

    public SpaceBattleRuntimeConfiguration RuntimeConfiguration => new(
        SpaceBattleProductionSettings.ResourceEnvelope.PageCacheSizeBytes,
        SpaceBattleProductionSettings.ResourceEnvelope.MemoryBudgetBytes,
        _runtime.Options.WorkerCount,
        _runtime.Scheduler.WorkerCount,
        _runtime.Options.Overload.MinTickRateHz,
        _runtime.Options.Overload.QueueGrowthTicks,
        _runtime.CurrentOverloadLevel,
        _runtime.UserSystems.Select(static system => new SpaceBattleSystemConfiguration(
            system.Name,
            system.Priority,
            system.TickDivisor,
            system.ThrottledTickDivisor,
            system.CanShed)).ToArray(),
        _runtime.Scheduler.EventQueues.Select(static queue => new SpaceBattleEventQueueConfiguration(
            queue.Name,
            queue.Capacity)).ToArray());

    public IReadOnlyList<string> SystemNames => _runtime.UserSystems.Select(static system => system.Name).ToArray();

    public IReadOnlyList<string> SystemPhases => _runtime.UserSystems.Select(static system => system.Phase.Name).ToArray();

    public bool WaitForCompletedTicks(ulong completedTicks, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var startedAt = Stopwatch.GetTimestamp();
        if (!_state.WaitForCompletedTicks(completedTicks, timeout))
        {
            return false;
        }

        var elapsed = Stopwatch.GetTimestamp() - startedAt;
        var remaining = timeout - TimeSpan.FromSeconds(elapsed / (double)Stopwatch.Frequency);
        if (remaining <= TimeSpan.Zero || !SpinWait.SpinUntil(
                () => _runtime.CurrentTickNumber >= _state.RuntimeTicksRequired(completedTicks),
                remaining))
        {
            return false;
        }

        return true;
    }

    public bool WaitForTerminal(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var startedAt = Stopwatch.GetTimestamp();
        if (!_state.WaitForTerminal(timeout, out ulong completedTicks))
        {
            return false;
        }

        var elapsed = Stopwatch.GetTimestamp() - startedAt;
        var remaining = timeout - TimeSpan.FromSeconds(elapsed / (double)Stopwatch.Frequency);
        if (remaining <= TimeSpan.Zero || !SpinWait.SpinUntil(
                () => _runtime.CurrentTickNumber >= _state.RuntimeTicksRequired(completedTicks),
                remaining))
        {
            return false;
        }

        PersistTerminalState(completedTicks);
        return true;
    }

    public void RequestPause()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _state.RequestPause();
    }

    public bool WaitForPause(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return WaitForPauseCore(timeout);
    }

    private bool WaitForPauseCore(TimeSpan timeout)
    {
        _state.RequestPause();
        var startedAt = Stopwatch.GetTimestamp();
        if (!_state.WaitForPause(timeout, out ulong completedTicks))
        {
            return false;
        }

        var remaining = timeout == Timeout.InfiniteTimeSpan
            ? Timeout.InfiniteTimeSpan
            : timeout - Stopwatch.GetElapsedTime(startedAt);
        bool timedOut = remaining != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero;
        if (timedOut || !SpinWait.SpinUntil(
            () => _runtime.CurrentTickNumber >= _state.RuntimeTicksRequired(completedTicks),
            remaining))
        {
            return false;
        }

        PersistPausedState(completedTicks);
        return true;
    }

    public InitialWorldSnapshot GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return SpaceBattleHost.ReadSnapshot(_engine);
    }

    public SpaceBattleRuntimeDiagnosticsSnapshot GetRuntimeDiagnostics()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _state.GetDiagnosticsSnapshot();
    }

    /// <summary>获取 PhaseTimingCollector 收集到的样本快照（仅供 benchmark 使用）。</summary>
    internal IReadOnlyList<TickPhaseSample> GetPhaseTimingSamples()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _state.PhaseTiming.Samples;
    }

    public InitialWorldSnapshot WaitForSnapshot(ulong completedTicks, TimeSpan timeout)
        => WaitForSnapshots([completedTicks], timeout)[0];

    public IReadOnlyList<InitialWorldSnapshot> WaitForSnapshots(
        IReadOnlyList<ulong> completedTicks,
        TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(completedTicks);
        var requestedTicks = completedTicks.Distinct().Order().ToArray();
        if (requestedTicks.Length == 0)
        {
            throw new ArgumentException("至少需要请求一个模拟 tick。", nameof(completedTicks));
        }

        var startedAt = Stopwatch.GetTimestamp();
        _state.BeginSnapshotRequest(requestedTicks);

        try
        {
            var snapshots = new List<InitialWorldSnapshot>(requestedTicks.Length);
            foreach (var requestedTick in requestedTicks)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
                if (remaining <= TimeSpan.Zero ||
                    !_state.WaitForSnapshot(requestedTick, remaining, out var snapshot))
                {
                    throw new TimeoutException($"等待模拟 tick {requestedTick} 的快照超时。");
                }

                remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
                if (remaining <= TimeSpan.Zero || !WaitForCompletedTicks(requestedTick, remaining))
                {
                    throw new TimeoutException($"等待模拟 tick {requestedTick} 完成持久化超时。");
                }

                snapshots.Add(snapshot);
            }

            return snapshots;
        }
        finally
        {
            _state.EndSnapshotRequest();
        }
    }

    internal void RegisterPauseCancellation(CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
        {
            _pauseCancellation = cancellationToken.Register(
                static state => ((SpaceBattleSimulation)state!).RequestPause(),
                this);
        }
    }

    internal static SpaceBattleSimulation Create(
        DatabaseEngine engine,
        SimulationDefinition definition,
        EntityId runEntityId,
        ulong completedTicks,
        SpaceBattleRunResult startupResult,
        ISpaceBattleObservationSink observationSink)
    {
        ShipMembershipViews shipViews = ShipMembershipViews.RebuildAndCreate(
            engine,
            runEntityId,
            startupFenceTick: 0);
        uint aliveShipCount;
        try
        {
            using Transaction transaction = engine.CreateQuickTransaction();
            aliveShipCount = checked((uint)transaction.Query<Ship>().Execute().Count);
        }
        catch
        {
            shipViews.Dispose();
            throw;
        }

        SimulationRuntimeState state = new(definition, runEntityId, completedTicks, aliveShipCount, shipViews);
        try
        {
            using Transaction transaction = engine.CreateQuickTransaction();
            state.InitializeShipRoster(transaction);
            state.InitializeModeCounts(transaction);
            state.RebuildTargetLockIndexes(transaction);
        }
        catch
        {
            state.Dispose();
            throw;
        }
        SpaceBattleObservationPublisher observationPublisher = new(
            engine,
            observationSink);
        TyphonRuntime runtime = null!;

        try
        {
            runtime = TyphonRuntime.Create(
                engine,
                schedule => ConfigureRuntime(schedule, state, observationPublisher),
                new RuntimeOptions
                {
                    BaseTickRate = SimulationDefinition.FixedTickRate,
                    WorkerCount = SpaceBattleProductionSettings.TestWorkerCountOverride ?? SpaceBattleProductionSettings.AutomaticWorkerCount,
                    Overload = new OverloadOptions
                    {
                        MinTickRateHz = SimulationDefinition.FixedTickRate,
                        QueueGrowthTicks = SpaceBattleProductionSettings.DisabledQueueGrowthEscalationTicks,
                    },
                    TelemetryRingCapacity = GetTelemetryRingCapacity(definition.MaximumCompletedTicks),
                });
            observationPublisher.Start(runtime);
            runtime.Start();
            return new SpaceBattleSimulation(
                engine,
                runtime,
                state,
                startupResult,
                observationPublisher);
        }
        catch
        {
            observationPublisher.Dispose();
            runtime?.Dispose();
            state.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _pauseCancellation.Dispose();
        if (_state.IsRunning &&
            !WaitForPauseCore(Timeout.InfiniteTimeSpan) &&
            !_state.IsTerminal)
        {
            throw new InvalidOperationException("无法在关闭 SpaceBattle 前完成当前模拟 tick。");
        }

        _runtime.Shutdown();
        _observationPublisher.Dispose();
        _runtime.Dispose();
        _state.Dispose();
        _engine.Dispose();
    }

    private void PersistTerminalState(ulong completedTicks)
    {
        lock (_terminalPersistenceSync)
        {
            if (_terminalStatePersisted)
            {
                return;
            }

            using (var transaction = _engine.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                ref SimulationRunStateComponent runState = ref transaction.OpenMut(_state.RunEntityId)
                    .Write(SimulationRunEntity.State);
                if ((SimulationRunStatus)runState.Status == SimulationRunStatus.Running)
                {
                    throw new InvalidOperationException("终态尚未写入 SimulationRun。");
                }

                transaction.Commit();
            }

            SpaceBattleCheckpoint.Persist(_engine, _state.RunEntityId, completedTicks);
            _terminalStatePersisted = true;
        }
    }

    private void PersistPausedState(ulong completedTicks)
    {
        lock (_pausePersistenceSync)
        {
            if (_pauseStatePersisted)
            {
                return;
            }

            SpaceBattleCheckpoint.Persist(_engine, _state.RunEntityId, completedTicks);
            _pauseStatePersisted = true;
        }
    }

    private static void ConfigureRuntime(
        RuntimeSchedule schedule,
        SimulationRuntimeState state,
        SpaceBattleObservationPublisher observationPublisher)
    {
        var dag = schedule.PublicTrack.DeclareDag("SpaceBattle")
            .Phases(
                SpaceBattlePhases.ShipViewRefresh,
                SpaceBattlePhases.State,
                SpaceBattlePhases.Steering,
                SpaceBattlePhases.Movement,
                SpaceBattlePhases.TargetLockCleanup,
                SpaceBattlePhases.Targeting,
                SpaceBattlePhases.Combat,
                SpaceBattlePhases.Resolution,
                SpaceBattlePhases.Output);
        EventQueue<DamageIntent>[] damageIntentQueues = CreateDamageIntentQueues(dag);
        DamageResolutionState damageResolutionState = new();
        dag.Add(new ShipViewRefreshSystem(state));
        dag.Add(new StateSystem(state));
        dag.Add(new SteeringSystem(state));
        dag.Add(new MovementSystem(state));
        dag.Add(new TargetLockCleanupSystem(state));
        dag.Add(new TargetingSystem(state));
        dag.Add(new CombatSystem(state, state.CombatShips, damageIntentQueues, damageResolutionState));
        dag.Add(new DamageResolutionSystem(state, damageIntentQueues, damageResolutionState));
        dag.Add(new ResolutionSystem(state, damageResolutionState));
        dag.Add(new OutputSystem(state, damageResolutionState, observationPublisher));
    }

    private static EventQueue<DamageIntent>[] CreateDamageIntentQueues(Dag dag)
    {
        EventQueue<DamageIntent>[] queues = new EventQueue<DamageIntent>[SpaceBattleProductionSettings.EffectiveWorkerCount];
        for (int workerId = 0; workerId < queues.Length; workerId++)
        {
            queues[workerId] = dag.CreateEventQueue<DamageIntent>(
                $"DamageIntent-{workerId}",
                BehaviorRules.DamageIntentQueueCapacity);
        }

        return queues;
    }

    private static int GetTelemetryRingCapacity(ulong maximumCompletedTicks)
    {
        ulong capacity = 1;
        while (capacity < maximumCompletedTicks)
        {
            capacity <<= 1;
        }

        return checked((int)capacity);
    }
}

internal static class SpaceBattlePhases
{
    public static readonly Phase ShipViewRefresh = new("ShipViewRefresh");
    public static readonly Phase State = new("State");
    public static readonly Phase Steering = new("Steering");
    public static readonly Phase Movement = new("Movement");
    public static readonly Phase TargetLockCleanup = new("TargetLockCleanup");
    public static readonly Phase Targeting = new("Targeting");
    public static readonly Phase Combat = new("Combat");
    public static readonly Phase Resolution = new("Resolution");
    public static readonly Phase Output = new("Output");
}

internal static class SpaceBattleSystemPolicies
{
    public static SystemBuilder Apply(SystemBuilder builder) => builder
        .Priority(SystemPriority.Critical)
        .TickDivisor(1)
        .ThrottledTickDivisor(1)
        .CanShed(false);
}

internal static class ShipRoster
{
    public static EntityId[] Ordered(IEnumerable<EntityId> shipIds) => shipIds
        .OrderBy(static id => id.EntityKey)
        .ToArray();

    public static int IndexOf(IReadOnlyList<EntityId> roster, EntityId shipId)
    {
        var low = 0;
        var high = roster.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = roster[middle].EntityKey.CompareTo(shipId.EntityKey);
            if (comparison == 0)
            {
                return middle;
            }

            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return -1;
    }

    public static EntityId[] ApplyDelta(
        EntityId[] roster,
        IReadOnlyList<EntityId> added,
        IReadOnlyList<EntityId> removed)
    {
        if (added.Count == 0 && removed.Count == 0)
        {
            return roster;
        }

        HashSet<long> removedKeys = removed.Count == 0 ? null : new HashSet<long>(removed.Count);
        foreach (EntityId shipId in removed)
        {
            removedKeys?.Add(shipId.EntityKey);
        }

        var addedShips = new List<EntityId>(added.Count);
        var addedKeys = new HashSet<long>(added.Count);
        foreach (EntityId shipId in added)
        {
            if (addedKeys.Add(shipId.EntityKey))
            {
                addedShips.Add(shipId);
            }
        }

        addedShips.Sort(static (left, right) => left.EntityKey.CompareTo(right.EntityKey));
        var nextRoster = new EntityId[roster.Length + addedShips.Count];
        var rosterIndex = 0;
        var addedIndex = 0;
        var nextIndex = 0;
        while (rosterIndex < roster.Length || addedIndex < addedShips.Count)
        {
            while (rosterIndex < roster.Length && removedKeys?.Contains(roster[rosterIndex].EntityKey) == true)
            {
                rosterIndex++;
            }

            if (rosterIndex == roster.Length)
            {
                while (addedIndex < addedShips.Count)
                {
                    nextRoster[nextIndex++] = addedShips[addedIndex++];
                }

                continue;
            }

            if (addedIndex == addedShips.Count)
            {
                nextRoster[nextIndex++] = roster[rosterIndex++];
                continue;
            }

            var rosterShip = roster[rosterIndex];
            var addedShip = addedShips[addedIndex];
            var comparison = rosterShip.EntityKey.CompareTo(addedShip.EntityKey);
            if (comparison < 0)
            {
                nextRoster[nextIndex++] = rosterShip;
                rosterIndex++;
            }
            else if (comparison > 0)
            {
                nextRoster[nextIndex++] = addedShip;
                addedIndex++;
            }
            else
            {
                nextRoster[nextIndex++] = rosterShip;
                rosterIndex++;
                addedIndex++;
            }
        }

        if (nextIndex == roster.Length && nextRoster.AsSpan(0, nextIndex).SequenceEqual(roster))
        {
            return roster;
        }

        Array.Resize(ref nextRoster, nextIndex);
        return nextRoster;
    }
}

internal sealed class SimulationRuntimeState : IDisposable
{
    private readonly object _sync = new();
    private EntityId[] _shipRoster = [];
    private EntityId[] _tickWorkset = [];
    private IReadOnlyList<EntityId> _shipRosterReadOnly = Array.Empty<EntityId>();
    private IReadOnlyList<EntityId> _tickWorksetReadOnly = Array.Empty<EntityId>();
    private TargetLockIndexes _targetLockIndexes = new();
    private readonly List<TargetLockIndexMutation> _pendingTargetLockMutations = [];
    private readonly Dictionary<string, int> _consumerProcessingCounts = [];
    private ulong _completedTicks;
    private ulong _terminalCompletedTicks;
    private ulong _pauseCompletedTicks;
    private ulong _shotsFired;
    private ulong _hits;
    private ulong _deaths;
    private uint _aliveShipCount;
    private int _derivedActiveLockCount;
    private int _modeCountStaging;
    private int _modeCountWandering;
    private int _modeCountTracking;
    private int _modeCountCombat;
    private int _modeCountDisengaging;
    private int _modeCountEscaping;
    private readonly HashSet<long> _destroyedShipKeys = [];
    private int _isRunning = 1;
    private int _isTerminal;
    private bool _pauseRequested;
    private bool _pauseAcknowledged;
    private bool _tickInProgress;
    private ulong[] _requestedSnapshotTicks = [];
    private readonly Dictionary<ulong, InitialWorldSnapshot> _snapshots = new();
    internal PhaseTimingCollector PhaseTiming { get; } = new();

    private readonly record struct TargetLockIndexMutation(
        EntityId TargetLockId,
        bool IsAddition);

    public SimulationRuntimeState(
        SimulationDefinition definition,
        EntityId runEntityId,
        ulong completedTicks,
        uint aliveShipCount,
        ShipMembershipViews shipViews)
    {
        Definition = definition;
        RunEntityId = runEntityId;
        _completedTicks = completedTicks;
        _aliveShipCount = aliveShipCount;
        StartingCompletedTicks = completedTicks;
        ShipViews = shipViews;
    }

    public SimulationDefinition Definition { get; }

    public EntityId RunEntityId { get; }

    public ulong StartingCompletedTicks { get; }

    public ShipMembershipViews ShipViews { get; }

    public EcsView<Ship> Ships => ShipViews.RuntimeShips;

    public EcsView<Ship> CombatShips => ShipViews.CombatShips;

    public IReadOnlyList<EntityId> ShipRoster
    {
        get
        {
            lock (_sync)
            {
                return _shipRosterReadOnly;
            }
        }
    }

    public IReadOnlyList<EntityId> TickWorkset
    {
        get
        {
            lock (_sync)
            {
                return _tickWorksetReadOnly;
            }
        }
    }

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public bool IsTerminal => Volatile.Read(ref _isTerminal) != 0;

    public int DerivedActiveLockCount
    {
        get
        {
            lock (_sync)
            {
                return _derivedActiveLockCount;
            }
        }
    }

    public uint AliveShipCount
    {
        get
        {
            lock (_sync)
            {
                return _aliveShipCount;
            }
        }
    }

    public void Dispose()
    {
        ShipViews.Dispose();
        lock (_sync)
        {
            _shipRoster = [];
            _tickWorkset = [];
            _shipRosterReadOnly = Array.Empty<EntityId>();
            _tickWorksetReadOnly = Array.Empty<EntityId>();
            _targetLockIndexes.Clear();
            _pendingTargetLockMutations.Clear();
            _consumerProcessingCounts.Clear();
            _derivedActiveLockCount = 0;
            _modeCountStaging = 0;
            _modeCountWandering = 0;
            _modeCountTracking = 0;
            _modeCountCombat = 0;
            _modeCountDisengaging = 0;
            _modeCountEscaping = 0;
            _destroyedShipKeys.Clear();
        }
    }

    public SpaceBattleRuntimeDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        lock (_sync)
        {
            long[] shipRosterEntityKeys = _shipRoster.Select(static ship => ship.EntityKey).ToArray();
            long[] tickWorksetEntityKeys = _tickWorkset.Select(static ship => ship.EntityKey).ToArray();
            return SpaceBattleRuntimeDiagnosticsSnapshot.Create(
                _completedTicks,
                Ships.Count,
                CombatShips.Count,
                _shipRoster.Length,
                _tickWorkset.Length,
                _targetLockIndexes.CopyOwnerCounts(),
                _targetLockIndexes.CopyTargetCounts(),
                checked((int)_aliveShipCount),
                _derivedActiveLockCount,
                ShipViews.RuntimeRefreshCount,
                ShipViews.CombatRefreshCount,
                ShipViews.RuntimeAddedCount,
                ShipViews.CombatAddedCount,
                ShipViews.RuntimeRemovedCount,
                ShipViews.CombatRemovedCount,
                shipRosterEntityKeys,
                tickWorksetEntityKeys,
                _consumerProcessingCounts);
        }
    }

    public void InitializeShipRoster(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        List<EntityId> aliveShipIds = new(Ships.Count);
        foreach (EntityId shipId in Ships.GetEntityEnumerator())
        {
            if (transaction.IsAlive(shipId))
            {
                aliveShipIds.Add(shipId);
            }
        }

        EntityId[] shipRoster = global::SpaceBattle.ShipRoster.Ordered(aliveShipIds);
        lock (_sync)
        {
            PublishShipRoster(shipRoster);
            _tickWorkset = shipRoster;
            _tickWorksetReadOnly = _shipRosterReadOnly;
        }
    }

    public void InitializeModeCounts(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        int staging = 0, wandering = 0, tracking = 0, combat = 0, disengaging = 0, escaping = 0;
        foreach (EntityId shipId in Ships.GetEntityEnumerator())
        {
            if (!transaction.IsAlive(shipId))
            {
                continue;
            }

            var mode = (BehaviorMode)transaction.Open(shipId).Read(Ship.Behavior).Mode;
            switch (mode)
            {
                case BehaviorMode.Staging: staging++; break;
                case BehaviorMode.Wandering: wandering++; break;
                case BehaviorMode.Tracking: tracking++; break;
                case BehaviorMode.Combat: combat++; break;
                case BehaviorMode.Disengaging: disengaging++; break;
                case BehaviorMode.Escaping: escaping++; break;
            }
        }

        lock (_sync)
        {
            _modeCountStaging = staging;
            _modeCountWandering = wandering;
            _modeCountTracking = tracking;
            _modeCountCombat = combat;
            _modeCountDisengaging = disengaging;
            _modeCountEscaping = escaping;
            _destroyedShipKeys.Clear();
        }
    }

    public void RecordModeTransition(BehaviorMode oldMode, BehaviorMode newMode)
    {
        lock (_sync)
        {
            DecrementModeCount(oldMode);
            IncrementModeCount(newMode);
        }
    }

    private void IncrementModeCount(BehaviorMode mode)
    {
        switch (mode)
        {
            case BehaviorMode.Staging: _modeCountStaging++; break;
            case BehaviorMode.Wandering: _modeCountWandering++; break;
            case BehaviorMode.Tracking: _modeCountTracking++; break;
            case BehaviorMode.Combat: _modeCountCombat++; break;
            case BehaviorMode.Disengaging: _modeCountDisengaging++; break;
            case BehaviorMode.Escaping: _modeCountEscaping++; break;
        }
    }

    private void DecrementModeCount(BehaviorMode mode)
    {
        switch (mode)
        {
            case BehaviorMode.Staging: _modeCountStaging--; break;
            case BehaviorMode.Wandering: _modeCountWandering--; break;
            case BehaviorMode.Tracking: _modeCountTracking--; break;
            case BehaviorMode.Combat: _modeCountCombat--; break;
            case BehaviorMode.Disengaging: _modeCountDisengaging--; break;
            case BehaviorMode.Escaping: _modeCountEscaping--; break;
        }
    }

    public SpaceBattleModeCounts GetDerivedModeCounts()
    {
        lock (_sync)
        {
            return new SpaceBattleModeCounts(
                _modeCountStaging,
                _modeCountWandering,
                _modeCountTracking,
                _modeCountCombat,
                _modeCountDisengaging,
                _modeCountEscaping);
        }
    }

    public void RefreshShipViews(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ShipViews.Refresh(transaction);
        try
        {
            lock (_sync)
            {
                IReadOnlyList<EntityId> added = ShipViews.RuntimeShips.Added;
                IReadOnlyList<EntityId> liveAdded = Array.Empty<EntityId>();
                if (added.Count != 0)
                {
                    var liveAddedList = new List<EntityId>(added.Count);
                    foreach (EntityId shipId in added)
                    {
                        if (transaction.IsAlive(shipId))
                        {
                            liveAddedList.Add(shipId);
                        }
                    }

                    liveAdded = liveAddedList;
                }

                EntityId[] nextRoster = global::SpaceBattle.ShipRoster.ApplyDelta(
                    _shipRoster,
                    liveAdded,
                    ShipViews.RuntimeShips.Removed);
                if (!ReferenceEquals(nextRoster, _shipRoster))
                {
                    PublishShipRoster(nextRoster);
                }
            }
        }
        finally
        {
            ShipViews.ClearDeltas();
        }
    }

    public void RebuildTargetLockIndexes(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        TargetLockIndexes targetLockIndexes = TargetLockIndexes.Rebuild(transaction);
        lock (_sync)
        {
            _targetLockIndexes = targetLockIndexes;
            _pendingTargetLockMutations.Clear();
            _derivedActiveLockCount = targetLockIndexes.Count;
        }
    }

    private void PublishShipRoster(EntityId[] shipRoster)
    {
        _shipRoster = shipRoster;
        _shipRosterReadOnly = Array.AsReadOnly(shipRoster);
        _aliveShipCount = checked((uint)shipRoster.Length);
    }

    public Dictionary<long, int> CopyOwnerLockCounts()
    {
        lock (_sync)
        {
            return _targetLockIndexes.CopyOwnerCounts();
        }
    }

    public EntityId[] CopyTargetLockIds()
    {
        lock (_sync)
        {
            return _targetLockIndexes.GetAllLockIds();
        }
    }

    public EntityId[] CopyTargetLockIdsForOwner(long ownerEntityKey)
    {
        lock (_sync)
        {
            return _targetLockIndexes.GetOwnerLockIds(ownerEntityKey);
        }
    }

    public EntityId[] CopyTargetLockIdsForShip(long shipEntityKey)
    {
        lock (_sync)
        {
            return _targetLockIndexes.GetLocksForShip(shipEntityKey);
        }
    }

    public void AddTargetLock(EntityId targetLockId, TargetLockComponent targetLock)
    {
        lock (_sync)
        {
            TargetLockRelation relation = new(
                (EntityId)targetLock.Owner,
                (EntityId)targetLock.Target);
            _targetLockIndexes.Add(targetLockId, relation);
            _pendingTargetLockMutations.Add(new(targetLockId, IsAddition: true));
            _derivedActiveLockCount++;
        }
    }

    public bool RemoveTargetLock(EntityId targetLockId)
    {
        lock (_sync)
        {
            bool removed = _targetLockIndexes.Remove(targetLockId);
            if (removed)
            {
                _pendingTargetLockMutations.Add(new(targetLockId, IsAddition: false));
                _derivedActiveLockCount--;
            }

            return removed;
        }
    }

    private void ReconcilePendingTargetLockMutations(Transaction transaction)
    {
        lock (_sync)
        {
            foreach (TargetLockIndexMutation mutation in _pendingTargetLockMutations)
            {
                bool isAlive = transaction.IsAlive(mutation.TargetLockId);
                if (!isAlive)
                {
                    if (mutation.IsAddition && _targetLockIndexes.Remove(mutation.TargetLockId))
                    {
                        _derivedActiveLockCount--;
                    }

                    continue;
                }

                TargetLockComponent targetLock = transaction.Open(mutation.TargetLockId).Read(TargetLock.Data);
                TargetLockRelation relation = new(
                    (EntityId)targetLock.Owner,
                    (EntityId)targetLock.Target);
                bool wasIndexed = _targetLockIndexes.Remove(mutation.TargetLockId);
                _targetLockIndexes.Add(mutation.TargetLockId, relation);
                if (!wasIndexed)
                {
                    _derivedActiveLockCount++;
                }
            }

            _pendingTargetLockMutations.Clear();
        }
    }

    public void RecordConsumerProcessed(string consumerName, int processedCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentOutOfRangeException.ThrowIfNegative(processedCount);
        lock (_sync)
        {
            _consumerProcessingCounts[consumerName] =
                _consumerProcessingCounts.GetValueOrDefault(consumerName) + processedCount;
        }
    }

    public ulong CompletedTicks
    {
        get
        {
            lock (_sync)
            {
                return _completedTicks;
            }
        }
    }

    public ulong CompletedTicksForRuntimeTick(long runtimeTickNumber) => checked(
        StartingCompletedTicks + (ulong)runtimeTickNumber + 1);

    public long RuntimeTicksRequired(ulong completedTicks) => completedTicks <= StartingCompletedTicks
        ? 0
        : checked((long)(completedTicks - StartingCompletedTicks));

    public void MarkCompletedTicks(ulong completedTicks)
    {
        lock (_sync)
        {
            _completedTicks = completedTicks;
            Monitor.PulseAll(_sync);
        }
    }

    public bool BeginTick(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        lock (_sync)
        {
            if (_isRunning == 0)
            {
                return false;
            }

            _consumerProcessingCounts.Clear();
            _tickInProgress = true;
        }

        try
        {
            ReconcilePendingTargetLockMutations(transaction);
            lock (_sync)
            {
                _tickWorkset = _shipRoster;
                _tickWorksetReadOnly = _shipRosterReadOnly;
            }
            return true;
        }
        catch
        {
            lock (_sync)
            {
                _tickInProgress = false;
                Monitor.PulseAll(_sync);
            }

            throw;
        }
    }

    public void EndTick(ulong completedTicks, bool completed)
    {
        lock (_sync)
        {
            _tickInProgress = false;
            if (completed && _pauseRequested && _isTerminal == 0)
            {
                Volatile.Write(ref _isRunning, 0);
                _pauseCompletedTicks = completedTicks;
                _pauseAcknowledged = true;
            }

            Monitor.PulseAll(_sync);
        }
    }

    public void RequestPause()
    {
        lock (_sync)
        {
            if (_isTerminal != 0 || _pauseAcknowledged)
            {
                return;
            }

            _pauseRequested = true;
            if (!_tickInProgress)
            {
                Volatile.Write(ref _isRunning, 0);
                _pauseCompletedTicks = _completedTicks;
                _pauseAcknowledged = true;
            }

            Monitor.PulseAll(_sync);
        }
    }

    public uint ApplyDestroyedShips(
        int destroyedShipCount,
        IReadOnlyList<EntityId> destroyedShips,
        IReadOnlyList<BehaviorMode> destroyedShipModes)
    {
        lock (_sync)
        {
            _aliveShipCount = checked(_aliveShipCount - (uint)destroyedShipCount);

            for (int i = 0; i < destroyedShipModes.Count; i++)
            {
                DecrementModeCount(destroyedShipModes[i]);
                _destroyedShipKeys.Add(destroyedShips[i].EntityKey);
            }

            if (destroyedShips.Count != 0)
            {
                EntityId[] nextRoster = global::SpaceBattle.ShipRoster.ApplyDelta(
                    _shipRoster,
                    Array.Empty<EntityId>(),
                    destroyedShips);
                if (!ReferenceEquals(nextRoster, _shipRoster))
                {
                    PublishShipRoster(nextRoster);
                    _tickWorkset = nextRoster;
                    _tickWorksetReadOnly = _shipRosterReadOnly;
                }
            }

            return _aliveShipCount;
        }
    }

    public bool IsDestroyedShip(long entityKey)
    {
        lock (_sync)
        {
            return _destroyedShipKeys.Contains(entityKey);
        }
    }

    public void ClearDestroyedShipKeys()
    {
        lock (_sync)
        {
            _destroyedShipKeys.Clear();
        }
    }

    public void SetAliveShipCount(uint aliveShipCount)
    {
        lock (_sync)
        {
            _aliveShipCount = aliveShipCount;
        }
    }

    public SpaceBattleTickSample RecordTickSample(
        long runtimeTickNumber,
        ulong completedTicks,
        uint processSegment,
        SpaceBattleRunSample run,
        SpaceBattleModeCounts modes,
        int activeLockCount,
        int shotsFired,
        int hits,
        int deaths)
    {
        lock (_sync)
        {
            _shotsFired = checked(_shotsFired + (ulong)shotsFired);
            _hits = checked(_hits + (ulong)hits);
            _deaths = checked(_deaths + (ulong)deaths);

            return new SpaceBattleTickSample(
                runtimeTickNumber,
                completedTicks,
                processSegment,
                run,
                modes,
                new SpaceBattleCounters(
                    run.AliveShipCount,
                    activeLockCount,
                    _shotsFired,
                    _hits,
                    _deaths));
        }
    }

    public void MarkTerminal(ulong completedTicks)
    {
        lock (_sync)
        {
            _terminalCompletedTicks = completedTicks;
            Volatile.Write(ref _isTerminal, 1);
            Volatile.Write(ref _isRunning, 0);
            Monitor.PulseAll(_sync);
        }
    }

    public bool WaitForTerminal(TimeSpan timeout, out ulong completedTicks)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        lock (_sync)
        {
            while (!IsTerminal)
            {
                var remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    completedTicks = 0;
                    return false;
                }

                Monitor.Wait(_sync, TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency));
            }

            completedTicks = _terminalCompletedTicks;
            return true;
        }
    }

    public bool WaitForPause(TimeSpan timeout, out ulong completedTicks)
    {
        bool infinite = timeout == Timeout.InfiniteTimeSpan;
        var deadline = infinite
            ? long.MaxValue
            : Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        lock (_sync)
        {
            while (!_pauseAcknowledged && !IsTerminal)
            {
                if (infinite)
                {
                    Monitor.Wait(_sync);
                    continue;
                }

                var remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    completedTicks = 0;
                    return false;
                }

                Monitor.Wait(_sync, TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency));
            }

            completedTicks = _pauseCompletedTicks;
            return _pauseAcknowledged;
        }
    }

    public void BeginSnapshotRequest(IReadOnlyList<ulong> completedTicks)
    {
        lock (_sync)
        {
            if (_requestedSnapshotTicks.Length != 0)
            {
                throw new InvalidOperationException("同一模拟一次只能等待一组精确 tick 快照。");
            }

            if (completedTicks[0] <= _completedTicks)
            {
                throw new InvalidOperationException(
                    $"无法读取已经完成的 tick {completedTicks[0]}；当前已完成 tick {_completedTicks}。");
            }

            _requestedSnapshotTicks = completedTicks.ToArray();
            _snapshots.Clear();
        }
    }

    public bool IsSnapshotRequested(ulong completedTicks)
    {
        lock (_sync)
        {
            return Array.BinarySearch(_requestedSnapshotTicks, completedTicks) >= 0;
        }
    }

    public void CaptureSnapshot(ulong completedTicks, InitialWorldSnapshot snapshot)
    {
        lock (_sync)
        {
            if (Array.BinarySearch(_requestedSnapshotTicks, completedTicks) < 0)
            {
                return;
            }

            _snapshots[completedTicks] = snapshot;
            Monitor.PulseAll(_sync);
        }
    }

    public bool WaitForSnapshot(
        ulong completedTicks,
        TimeSpan timeout,
        out InitialWorldSnapshot snapshot)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        lock (_sync)
        {
            while (Array.BinarySearch(_requestedSnapshotTicks, completedTicks) >= 0 &&
                   !_snapshots.TryGetValue(completedTicks, out snapshot))
            {
                var remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    snapshot = null!;
                    return false;
                }

                Monitor.Wait(_sync, TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency));
            }

            snapshot = null!;
            return Array.BinarySearch(_requestedSnapshotTicks, completedTicks) >= 0 &&
                _snapshots.TryGetValue(completedTicks, out snapshot);
        }
    }

    public void EndSnapshotRequest()
    {
        lock (_sync)
        {
            _requestedSnapshotTicks = [];
            _snapshots.Clear();
        }
    }

    public bool WaitForCompletedTicks(ulong completedTicks, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        lock (_sync)
        {
            while (_completedTicks < completedTicks)
            {
                var remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    return false;
                }

                Monitor.Wait(_sync, TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency));
            }

            return true;
        }
    }
}

internal sealed class ShipViewRefreshSystem(SimulationRuntimeState state) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("ShipViewRefresh")
        .Phase(SpaceBattlePhases.ShipViewRefresh)
        .ShouldRun(() => state.IsRunning)
        .Reads<ShipRunMembershipComponent>();

    protected override void Execute(TickContext context)
    {
        state.PhaseTiming.BeginPhase(PhaseTimingCollector.ShipViewRefresh);
        state.RefreshShipViews(context.Transaction);
        state.PhaseTiming.EndPhase(PhaseTimingCollector.ShipViewRefresh);
    }
}

internal sealed class StateSystem(SimulationRuntimeState state) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("State")
        .After("ShipViewRefresh")
        .Phase(SpaceBattlePhases.State)
        .ShouldRun(() => state.IsRunning)
        .Writes<BehaviorComponent>();

    protected override void Execute(TickContext context)
    {
        state.PhaseTiming.BeginTick();
        state.PhaseTiming.BeginPhase(PhaseTimingCollector.State);
        if (!state.BeginTick(context.Transaction))
        {
            state.PhaseTiming.EndPhase(PhaseTimingCollector.State);
            return;
        }

        state.ClearDestroyedShipKeys();

        int processedCount = 0;
        foreach (var shipId in state.TickWorkset)
        {
            if (!context.Transaction.IsAlive(shipId))
            {
                continue;
            }

            processedCount++;
            var ship = context.Transaction.OpenMut(shipId);
            ref var behavior = ref ship.Write(Ship.Behavior);
            if ((BehaviorMode)behavior.Mode != BehaviorMode.Staging)
            {
                continue;
            }

            if (behavior.ModeTicksRemaining > 0)
            {
                behavior.ModeTicksRemaining--;
                continue;
            }

            behavior.Mode = (byte)BehaviorMode.Wandering;
            behavior.DecisionOrdinal++;
            state.RecordModeTransition(BehaviorMode.Staging, BehaviorMode.Wandering);
        }

        state.RecordConsumerProcessed("State", processedCount);
        state.PhaseTiming.EndPhase(PhaseTimingCollector.State);
    }
}

internal sealed class SteeringSystem(SimulationRuntimeState state) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("Steering")
        .After("State")
        .Phase(SpaceBattlePhases.Steering)
        .ShouldRun(() => state.IsRunning)
        .ReadsFresh<BehaviorComponent>()
        .Reads<PositionComponent>()
        .Writes<BehaviorComponent>()
        .Writes<TrackingComponent>()
        .Writes<MotionComponent>()
        .Writes<AfterburnerComponent>();

    protected override void Execute(TickContext context)
    {
        state.PhaseTiming.BeginPhase(PhaseTimingCollector.Steering);
        IReadOnlyList<EntityId> shipIds = state.TickWorkset;
        List<TrackingStart> trackingStarts = null;
        int processedCount = 0;

        foreach (var shipId in shipIds)
        {
            if (!context.Transaction.IsAlive(shipId))
            {
                continue;
            }

            processedCount++;
            var ship = context.Transaction.OpenMut(shipId);
            ref var behavior = ref ship.Write(Ship.Behavior);
            ref var tracking = ref ship.Write(Ship.Tracking);
            var shipIdForRandom = BehaviorRules.PackShipId(shipId);
            var oldMode = (BehaviorMode)behavior.Mode;

            switch (oldMode)
            {
                case BehaviorMode.Wandering:
                    ProcessWandering(
                        state.Definition,
                        shipId,
                        shipIdForRandom,
                        ref behavior,
                        ref tracking,
                        ref ship.Write(Ship.Motion),
                        ref trackingStarts);
                    break;
                case BehaviorMode.Tracking:
                    ProcessTracking(
                        context,
                        state.Definition,
                        ship,
                        shipIdForRandom,
                        ref behavior,
                        ref tracking,
                        ref ship.Write(Ship.Motion));
                    break;
                case BehaviorMode.Disengaging:
                    if (behavior.ModeTicksRemaining == 0)
                    {
                        StartWandering(
                            state.Definition,
                            shipIdForRandom,
                            ref behavior,
                            ref tracking,
                            ref ship.Write(Ship.Motion));
                    }

                    break;
                case BehaviorMode.Escaping:
                    ProcessEscaping(
                        state.Definition,
                        ship,
                        shipIdForRandom,
                        ref behavior,
                        ref tracking,
                        ref ship.Write(Ship.Motion));
                    break;
            }

            var newMode = (BehaviorMode)behavior.Mode;
            if (newMode != oldMode)
            {
                state.RecordModeTransition(oldMode, newMode);
            }
        }

        if (trackingStarts is not null)
        {
            StartTracking(context, state.Definition, state.ShipRoster, trackingStarts);
            // 派生统计：StartTracking 中目标选择失败时改回 Wandering
            foreach (var trackingStart in trackingStarts)
            {
                var ship = context.Transaction.OpenMut(trackingStart.ShipId);
                var finalMode = (BehaviorMode)ship.Read(Ship.Behavior).Mode;
                if (finalMode == BehaviorMode.Wandering)
                {
                    state.RecordModeTransition(BehaviorMode.Tracking, BehaviorMode.Wandering);
                }
            }
        }

        state.RecordConsumerProcessed("Steering", processedCount);
        state.PhaseTiming.EndPhase(PhaseTimingCollector.Steering);
    }

    private static void ProcessWandering(
        SimulationDefinition definition,
        EntityId shipId,
        ulong shipIdForRandom,
        ref BehaviorComponent behavior,
        ref TrackingComponent tracking,
        ref MotionComponent motion,
        ref List<TrackingStart> trackingStarts)
    {
        if (behavior.DecisionOrdinal == 1 && behavior.ModeTicksRemaining == 0)
        {
            StartWandering(definition, shipIdForRandom, ref behavior, ref tracking, ref motion);
            return;
        }

        if (behavior.ModeTicksRemaining > 0)
        {
            behavior.ModeTicksRemaining--;
            return;
        }

        var decisionOrdinal = behavior.DecisionOrdinal;
        switch (BehaviorRules.DecideWandering(definition.Seed, shipIdForRandom, decisionOrdinal))
        {
            case WanderingDecision.ContinueWandering:
                StartWandering(definition, shipIdForRandom, ref behavior, ref tracking, ref motion);
                break;
            case WanderingDecision.Track:
                behavior.Mode = (byte)BehaviorMode.Tracking;
                behavior.DecisionOrdinal++;
                trackingStarts ??= new List<TrackingStart>();
                trackingStarts.Add(new TrackingStart(shipId, shipIdForRandom, decisionOrdinal));
                break;
            case WanderingDecision.Combat:
                behavior.Mode = (byte)BehaviorMode.Combat;
                behavior.ModeTicksRemaining = BehaviorRules.CombatAcquisitionDurationTicks + 1;
                behavior.DecisionOrdinal++;
                break;
            default:
                throw new InvalidOperationException("未知的游荡决策。");
        }
    }

    private static void ProcessTracking(
        TickContext context,
        SimulationDefinition definition,
        EntityRef ship,
        ulong shipIdForRandom,
        ref BehaviorComponent behavior,
        ref TrackingComponent tracking,
        ref MotionComponent motion)
    {
        if (tracking.Target.IsNull ||
            !context.Transaction.TryOpen(tracking.Target, out var target) ||
            tracking.TrackingTicksRemaining == 0)
        {
            StartWandering(
                definition,
                shipIdForRandom,
                ref behavior,
                ref tracking,
                ref motion);
            return;
        }

        ref readonly var position = ref ship.Read(Ship.Position);
        ref readonly var targetPosition = ref target.Read(Ship.Position);
        var nextMotion = BehaviorRules.CreateTrackingMotion(
            new PositionSnapshot(position.X, position.Y, position.Z),
            new PositionSnapshot(targetPosition.X, targetPosition.Y, targetPosition.Z),
            new MotionSnapshot(motion.DirectionX, motion.DirectionY, motion.DirectionZ, motion.Speed));
        SetMotion(ref motion, nextMotion);
        tracking.TrackingTicksRemaining--;
    }

    private static void StartTracking(
        TickContext context,
        SimulationDefinition definition,
        IReadOnlyList<EntityId> roster,
        IReadOnlyList<TrackingStart> trackingStarts)
    {
        foreach (var trackingStart in trackingStarts)
        {
            var ship = context.Transaction.OpenMut(trackingStart.ShipId);
            ref var behavior = ref ship.Write(Ship.Behavior);
            ref var tracking = ref ship.Write(Ship.Tracking);
            ref var motion = ref ship.Write(Ship.Motion);
            var sourceIndex = ShipRoster.IndexOf(roster, trackingStart.ShipId);
            if (sourceIndex < 0)
            {
                continue;
            }
            var targetIndex = BehaviorRules.SelectTrackingTargetIndex(
                definition.Seed,
                trackingStart.ShipIdForRandom,
                trackingStart.DecisionOrdinal,
                roster.Count,
                sourceIndex);
            if (targetIndex < 0)
            {
                StartWandering(
                    definition,
                    trackingStart.ShipIdForRandom,
                    ref behavior,
                    ref tracking,
                    ref motion);
                continue;
            }

            var target = context.Transaction.Open(roster[targetIndex]);
            ref readonly var position = ref ship.Read(Ship.Position);
            ref readonly var targetPosition = ref target.Read(Ship.Position);
            tracking.Target = roster[targetIndex];
            tracking.TrackingTicksRemaining = BehaviorRules.TrackingDurationTicks;
            SetMotion(
                ref motion,
                BehaviorRules.CreateTrackingMotion(
                    new PositionSnapshot(position.X, position.Y, position.Z),
                    new PositionSnapshot(targetPosition.X, targetPosition.Y, targetPosition.Z),
                    new MotionSnapshot(motion.DirectionX, motion.DirectionY, motion.DirectionZ, motion.Speed)));
        }
    }

    internal static void StartWandering(
        SimulationDefinition definition,
        ulong shipIdForRandom,
        ref BehaviorComponent behavior,
        ref TrackingComponent tracking,
        ref MotionComponent motion)
    {
        behavior.Mode = (byte)BehaviorMode.Wandering;
        behavior.ModeTicksRemaining = BehaviorRules.WanderingDecisionIntervalTicks;
        tracking.Target = EntityLink<Ship>.Null;
        tracking.TrackingTicksRemaining = 0;
        SetMotion(
            ref motion,
            BehaviorRules.CreateWanderingMotion(definition.Seed, shipIdForRandom, behavior.DecisionOrdinal));
        behavior.DecisionOrdinal++;
    }

    private static void ProcessEscaping(
        SimulationDefinition definition,
        EntityRef ship,
        ulong shipIdForRandom,
        ref BehaviorComponent behavior,
        ref TrackingComponent tracking,
        ref MotionComponent motion)
    {
        if (behavior.ModeTicksRemaining == 0)
        {
            StartWandering(
                definition,
                shipIdForRandom,
                ref behavior,
                ref tracking,
                ref motion);
            if (ship.IsEnabled(Ship.Afterburner))
            {
                ship.Disable(Ship.Afterburner);
            }

            return;
        }

        motion.Speed = BehaviorRules.EscapingSpeed;
    }

    internal static void SetMotion(ref MotionComponent motion, MotionSnapshot value)
    {
        motion.DirectionX = value.DirectionX;
        motion.DirectionY = value.DirectionY;
        motion.DirectionZ = value.DirectionZ;
        motion.Speed = value.Speed;
    }

    private readonly record struct TrackingStart(EntityId ShipId, ulong ShipIdForRandom, ulong DecisionOrdinal);
}

internal sealed class MovementSystem(SimulationRuntimeState state) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("Movement")
        .After("Steering")
        .Phase(SpaceBattlePhases.Movement)
        .ShouldRun(() => state.IsRunning)
        .ReadsFresh<BehaviorComponent>()
        .ReadsFresh<MotionComponent>()
        .Writes<PositionComponent>()
        .Writes<SpatialBoundsComponent>()
        .Writes<MotionComponent>();

    protected override void Execute(TickContext context)
    {
        state.PhaseTiming.BeginPhase(PhaseTimingCollector.Movement);
        int processedCount = 0;
        foreach (var shipId in state.TickWorkset)
        {
            if (!context.Transaction.IsAlive(shipId))
            {
                continue;
            }

            processedCount++;
            var ship = context.Transaction.OpenMut(shipId);
            ref readonly var behavior = ref ship.Read(Ship.Behavior);
            if ((BehaviorMode)behavior.Mode == BehaviorMode.Staging)
            {
                continue;
            }

            ref var position = ref ship.Write(Ship.Position);
            ref var motion = ref ship.Write(Ship.Motion);
            var step = MovementRules.Advance(
                new PositionSnapshot(position.X, position.Y, position.Z),
                new MotionSnapshot(motion.DirectionX, motion.DirectionY, motion.DirectionZ, motion.Speed),
                SimulationDefinition.FixedSimulationDeltaSeconds,
                state.Definition.WorldSize);

            position.X = step.Position.X;
            position.Y = step.Position.Y;
            position.Z = step.Position.Z;
            motion.DirectionX = step.Motion.DirectionX;
            motion.DirectionY = step.Motion.DirectionY;
            motion.DirectionZ = step.Motion.DirectionZ;

            ref var bounds = ref ship.Write(Ship.SpatialBounds);
            bounds.Bounds.MinX = position.X;
            bounds.Bounds.MinY = position.Y;
            bounds.Bounds.MinZ = position.Z;
            bounds.Bounds.MaxX = position.X;
            bounds.Bounds.MaxY = position.Y;
            bounds.Bounds.MaxZ = position.Z;
        }

        state.RecordConsumerProcessed("Movement", processedCount);
        state.PhaseTiming.EndPhase(PhaseTimingCollector.Movement);
    }
}

internal sealed class TargetLockCleanupSystem(SimulationRuntimeState state) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("TargetLockCleanup")
        .After("Movement")
        .Phase(SpaceBattlePhases.TargetLockCleanup)
        .ShouldRun(() => state.IsRunning)
        .Reads<PositionComponent>()
        .ReadsFresh<BehaviorComponent>()
        .ReadsFresh<TargetLockComponent>()
        .Writes<BehaviorComponent>()
        .Writes<TrackingComponent>()
        .Writes<MotionComponent>()
        .Writes<WeaponComponent>()
        .Writes<TargetLockComponent>();

    protected override void Execute(TickContext context)
    {
        state.PhaseTiming.BeginPhase(PhaseTimingCollector.TargetLockCleanup);
        int processedCount = AdvanceTimedBehaviorDurations(context);
        AdvanceExistingLocks(context, state);
        state.RecordConsumerProcessed("TargetLockCleanup", processedCount);
        state.PhaseTiming.EndPhase(PhaseTimingCollector.TargetLockCleanup);
    }

    private int AdvanceTimedBehaviorDurations(TickContext context)
    {
        int processedCount = 0;
        foreach (var shipId in state.TickWorkset)
        {
            if (!context.Transaction.IsAlive(shipId))
            {
                continue;
            }

            processedCount++;
            var ship = context.Transaction.Open(shipId);
            ref readonly var currentBehavior = ref ship.Read(Ship.Behavior);
            BehaviorMode mode = (BehaviorMode)currentBehavior.Mode;
            if (mode is not (BehaviorMode.Combat or BehaviorMode.Disengaging or BehaviorMode.Escaping) ||
                currentBehavior.ModeTicksRemaining == 0)
            {
                continue;
            }

            var mutableShip = context.Transaction.OpenMut(shipId);
            ref var behavior = ref mutableShip.Write(Ship.Behavior);
            if (--behavior.ModeTicksRemaining != 0 || mode != BehaviorMode.Combat)
            {
                continue;
            }

            ref var tracking = ref mutableShip.Write(Ship.Tracking);
            ref var motion = ref mutableShip.Write(Ship.Motion);
            SteeringSystem.StartWandering(
                state.Definition,
                BehaviorRules.PackShipId(shipId),
                ref behavior,
                ref tracking,
                ref motion);
            state.RecordModeTransition(BehaviorMode.Combat, BehaviorMode.Wandering);
        }

        return processedCount;
    }

    private static void AdvanceExistingLocks(
        TickContext context,
        SimulationRuntimeState state)
    {
        foreach (EntityId targetLockId in state.CopyTargetLockIds())
        {
            if (!context.Transaction.IsAlive(targetLockId))
            {
                state.RemoveTargetLock(targetLockId);
                continue;
            }

            var targetLockEntity = context.Transaction.OpenMut(targetLockId);
            ref var targetLock = ref targetLockEntity.Write(TargetLock.Data);
            var ownerId = (EntityId)targetLock.Owner;
            var targetId = (EntityId)targetLock.Target;
            if (!context.Transaction.TryOpen(ownerId, out var owner))
            {
                DestroyTargetLock(context, state, targetLockId);
                continue;
            }

            if (!context.Transaction.TryOpen(targetId, out var target))
            {
                DisableWeapon(context, ownerId);
                DestroyTargetLock(context, state, targetLockId);
                continue;
            }

            if (!IsWithinLockRange(owner, target))
            {
                if ((TargetLockStatus)targetLock.Status == TargetLockStatus.Locked)
                {
                    ref var ownerBehavior = ref context.Transaction.OpenMut(ownerId).Write(Ship.Behavior);
                    if ((BehaviorMode)ownerBehavior.Mode == BehaviorMode.Combat)
                    {
                        ownerBehavior.ModeTicksRemaining = BehaviorRules.CombatAcquisitionDurationTicks + 1;
                    }
                }

                DisableWeapon(context, ownerId);
                DestroyTargetLock(context, state, targetLockId);
                continue;
            }

            switch ((TargetLockStatus)targetLock.Status)
            {
                case TargetLockStatus.Releasing:
                    DisableWeapon(context, ownerId);
                    if (targetLock.TicksRemaining == 0 || --targetLock.TicksRemaining == 0)
                    {
                        DestroyTargetLock(context, state, targetLockId);
                        continue;
                    }

                    break;
                case TargetLockStatus.Acquiring:
                    if ((BehaviorMode)owner.Read(Ship.Behavior).Mode != BehaviorMode.Combat)
                    {
                        BeginRelease(context, ownerId, ref targetLock);
                        break;
                    }

                    if (targetLock.TicksRemaining == 0 || --targetLock.TicksRemaining == 0)
                    {
                        targetLock.Status = (byte)TargetLockStatus.Locked;
                        EnableWeapon(context, ownerId, targetId);
                    }

                    break;
                case TargetLockStatus.Locked:
                    if ((BehaviorMode)owner.Read(Ship.Behavior).Mode != BehaviorMode.Combat)
                    {
                        BeginRelease(context, ownerId, ref targetLock);
                    }
                    else
                    {
                        EnableWeapon(context, ownerId, targetId);
                    }

                    break;
                default:
                    throw new InvalidOperationException("未知的目标锁定状态。");
            }

        }
    }

    internal static void BeginRelease(
        TickContext context,
        EntityId ownerId,
        ref TargetLockComponent targetLock)
    {
        targetLock.Status = (byte)TargetLockStatus.Releasing;
        targetLock.TicksRemaining = BehaviorRules.LockReleaseDurationTicks;
        DisableWeapon(context, ownerId);
    }

    private static void EnableWeapon(TickContext context, EntityId ownerId, EntityId targetId)
    {
        EntityRef owner = context.Transaction.OpenMut(ownerId);
        ref TrackingComponent tracking = ref owner.Write(Ship.Tracking);
        tracking.Target = targetId;
        tracking.TrackingTicksRemaining = 0;
        if (!owner.IsEnabled(Ship.Weapon))
        {
            owner.Enable(Ship.Weapon);
            owner.Write(Ship.Weapon).CooldownTicksRemaining = 0;
        }
    }

    internal static void DisableWeapon(TickContext context, EntityId ownerId)
    {
        EntityRef owner = context.Transaction.OpenMut(ownerId);
        ref TrackingComponent tracking = ref owner.Write(Ship.Tracking);
        tracking.Target = EntityLink<Ship>.Null;
        tracking.TrackingTicksRemaining = 0;
        if (owner.IsEnabled(Ship.Weapon))
        {
            owner.Disable(Ship.Weapon);
        }
    }

    internal static void ClearDeadLocks(
        TickContext context,
        SimulationRuntimeState state,
        IReadOnlyList<EntityId> destroyedShips)
    {
        foreach (EntityId destroyedShipId in destroyedShips)
        {
            foreach (EntityId targetLockId in state.CopyTargetLockIdsForShip(destroyedShipId.EntityKey))
            {
                if (!context.Transaction.IsAlive(targetLockId))
                {
                    state.RemoveTargetLock(targetLockId);
                    continue;
                }

                TargetLockComponent targetLock = context.Transaction.Open(targetLockId).Read(TargetLock.Data);
                EntityId ownerId = (EntityId)targetLock.Owner;
                if (context.Transaction.TryOpen(ownerId, out _))
                {
                    DisableWeapon(context, ownerId);
                }

                DestroyTargetLock(context, state, targetLockId);
            }
        }
    }

    internal static void DestroyTargetLock(
        TickContext context,
        SimulationRuntimeState state,
        EntityId targetLockId)
    {
        if (context.Transaction.IsAlive(targetLockId))
        {
            context.Transaction.Destroy(targetLockId);
        }

        state.RemoveTargetLock(targetLockId);
    }

    internal static bool IsWithinLockRange(EntityRef source, EntityRef target)
    {
        ref readonly PositionComponent sourcePosition = ref source.Read(Ship.Position);
        ref readonly PositionComponent targetPosition = ref target.Read(Ship.Position);
        return CombatRules.IsWithinRange(
            new PositionSnapshot(sourcePosition.X, sourcePosition.Y, sourcePosition.Z),
            new PositionSnapshot(targetPosition.X, targetPosition.Y, targetPosition.Z),
            BehaviorRules.LockRange);
    }
}

internal sealed class TargetingSystem(SimulationRuntimeState state) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("Targeting")
        .After("TargetLockCleanup")
        .Phase(SpaceBattlePhases.Targeting)
        .ShouldRun(() => state.IsRunning)
        .Reads<PositionComponent>()
        .ReadsFresh<BehaviorComponent>()
        .ReadsFresh<TargetLockComponent>()
        .Writes<BehaviorComponent>()
        .Writes<TargetLockComponent>();

    protected override void Execute(TickContext context)
    {
        state.PhaseTiming.BeginPhase(PhaseTimingCollector.Targeting);
        Dictionary<long, int> activeLockCounts = state.CopyOwnerLockCounts();
        IReadOnlyList<EntityId> roster = state.TickWorkset;
        int processedCount = 0;

        foreach (var shipId in roster)
        {
            if (!context.Transaction.IsAlive(shipId))
            {
                continue;
            }

            processedCount++;
            if (activeLockCounts.GetValueOrDefault(shipId.EntityKey) >=
                BehaviorRules.MaximumTargetLocksPerShip)
            {
                continue;
            }

            var ship = context.Transaction.OpenMut(shipId);
            ref var behavior = ref ship.Write(Ship.Behavior);
            if ((BehaviorMode)behavior.Mode != BehaviorMode.Combat)
            {
                continue;
            }

            var decisionOrdinal = behavior.DecisionOrdinal++;
            var sourceIndex = ShipRoster.IndexOf(roster, shipId);
            if (sourceIndex < 0)
            {
                continue;
            }

            var targetId = FindInRangeCandidate(
                context,
                roster,
                sourceIndex,
                shipId,
                state.Definition.Seed,
                decisionOrdinal);
            if (targetId.IsNull)
            {
                continue;
            }

            var targetLock = new TargetLockComponent
            {
                Owner = shipId,
                Target = targetId,
                TicksRemaining = BehaviorRules.LockAcquisitionDurationTicks,
                Status = (byte)TargetLockStatus.Acquiring,
            };
            var pauseCheckpoint = new PauseTargetLockCheckpointComponent
            {
                OwnerEntityKey = shipId.EntityKey,
                TargetEntityKey = targetId.EntityKey,
                TicksRemaining = targetLock.TicksRemaining,
                Status = targetLock.Status,
            };
            EntityId targetLockId = context.Transaction.Spawn<TargetLock>(
                TargetLock.Data.Set(in targetLock),
                TargetLock.PauseCheckpoint.Set(in pauseCheckpoint));
            state.AddTargetLock(targetLockId, targetLock);
            activeLockCounts[shipId.EntityKey] =
                activeLockCounts.GetValueOrDefault(shipId.EntityKey) + 1;
        }

        state.RecordConsumerProcessed("Targeting", processedCount);
        state.PhaseTiming.EndPhase(PhaseTimingCollector.Targeting);
    }

    private static EntityId FindInRangeCandidate(
        TickContext context,
        IReadOnlyList<EntityId> roster,
        int sourceIndex,
        EntityId shipId,
        ulong seed,
        ulong decisionOrdinal)
    {
        var source = context.Transaction.Open(shipId);
        for (var candidateOrdinal = 0;
             candidateOrdinal < BehaviorRules.MaximumLockCandidatesPerAttempt;
             candidateOrdinal++)
        {
            var candidateIndex = BehaviorRules.SelectLockTargetCandidateIndex(
                seed,
                BehaviorRules.PackShipId(shipId),
                decisionOrdinal,
                roster.Count,
                sourceIndex,
                candidateOrdinal);
            var candidate = context.Transaction.Open(roster[candidateIndex]);
            if (TargetLockCleanupSystem.IsWithinLockRange(source, candidate))
            {
                return roster[candidateIndex];
            }
        }

        return EntityId.Null;
    }
}

internal sealed class CombatSystem(
    SimulationRuntimeState state,
    EcsView<Ship> ships,
    IReadOnlyList<EventQueue<DamageIntent>> damageIntentQueues,
    DamageResolutionState damageResolutionState) : QuerySystem
{
    protected override void Configure(SystemBuilder builder)
    {
        SpaceBattleSystemPolicies.Apply(builder)
            .Name("Combat")
            .After("Targeting")
            .Phase(SpaceBattlePhases.Combat)
            .ShouldRun(() => state.IsRunning)
            .Input(() => ships)
            .Parallel()
            .ReadsFresh<BehaviorComponent>()
            .ReadsFresh<TrackingComponent>()
            .AdditionalReads<PositionComponent>()
            .Writes<WeaponComponent>();

        foreach (EventQueue<DamageIntent> queue in damageIntentQueues)
        {
            builder.WritesEvents(queue);
        }
    }

    protected override void Execute(TickContext context)
    {
        state.PhaseTiming.BeginParallelPhase();
        state.RecordConsumerProcessed("Combat", context.Entities.Count);
        EntityAccessor accessor = context.Accessor;
        EventQueue<DamageIntent> damageIntentQueue = damageIntentQueues[context.WorkerId];
        foreach (EntityId attackerId in context.Entities)
        {
            EntityRef attacker = accessor.OpenMut(attackerId);
            ref readonly TrackingComponent tracking = ref attacker.Read(Ship.Tracking);
            if (tracking.Target.IsNull ||
                !accessor.TryOpen(tracking.Target, out EntityRef target) ||
                !attacker.IsEnabled(Ship.Weapon) ||
                (BehaviorMode)attacker.Read(Ship.Behavior).Mode != BehaviorMode.Combat)
            {
                continue;
            }

            EntityId targetId = (EntityId)tracking.Target;
            ref WeaponComponent weapon = ref attacker.Write(Ship.Weapon);
            ref readonly PositionComponent attackerPosition = ref attacker.Read(Ship.Position);
            ref readonly PositionComponent targetPosition = ref target.Read(Ship.Position);
            WeaponFireResult fireResult = CombatRules.AdvanceWeaponFire(
                weapon.CooldownTicksRemaining,
                new PositionSnapshot(attackerPosition.X, attackerPosition.Y, attackerPosition.Z),
                new PositionSnapshot(targetPosition.X, targetPosition.Y, targetPosition.Z));
            weapon.CooldownTicksRemaining = fireResult.CooldownTicksRemaining;
            damageResolutionState.RecordShot(fireResult.Hit);
            if (fireResult.Hit)
            {
                damageIntentQueue.Push(new DamageIntent(attackerId, targetId));
            }
        }

        state.PhaseTiming.EndParallelPhase(PhaseTimingCollector.Combat);
    }
}

internal sealed class DamageResolutionSystem(
    SimulationRuntimeState state,
    IReadOnlyList<EventQueue<DamageIntent>> damageIntentQueues,
    DamageResolutionState damageResolutionState) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder)
    {
        SpaceBattleSystemPolicies.Apply(builder)
            .Name("DamageResolution")
            .After("Combat")
            .Phase(SpaceBattlePhases.Resolution)
            .ShouldRun(() => state.IsRunning && HasDamageIntents())
            .Writes<HealthComponent>();

        foreach (EventQueue<DamageIntent> queue in damageIntentQueues)
        {
            builder.ReadsEvents(queue);
        }
    }

    protected override void Execute(TickContext context)
    {
        state.PhaseTiming.BeginResolutionPhase();
        damageResolutionState.Clear();
        int intentCount = 0;
        foreach (EventQueue<DamageIntent> queue in damageIntentQueues)
        {
            intentCount += queue.Count;
        }

        if (intentCount == 0)
        {
            return;
        }

        DamageIntent[] damageIntents = ArrayPool<DamageIntent>.Shared.Rent(intentCount);
        try
        {
            int count = 0;
            foreach (EventQueue<DamageIntent> queue in damageIntentQueues)
            {
                count += queue.Drain(damageIntents.AsSpan(count, queue.Count));
            }

            Array.Sort(damageIntents, 0, count, DamageIntentComparer.Instance);
            for (int groupStart = 0; groupStart < count;)
            {
                EntityId targetId = damageIntents[groupStart].Target;
                int groupEnd = groupStart + 1;
                while (groupEnd < count && damageIntents[groupEnd].Target == targetId)
                {
                    groupEnd++;
                }

                int participatingAttackerCount = 1;
                EntityId previousAttackerId = damageIntents[groupStart].Attacker;
                for (int intentIndex = groupStart + 1; intentIndex < groupEnd; intentIndex++)
                {
                    EntityId attackerId = damageIntents[intentIndex].Attacker;
                    if (attackerId != previousAttackerId)
                    {
                        participatingAttackerCount++;
                        previousAttackerId = attackerId;
                    }
                }

                if (context.Transaction.TryOpen(targetId, out _))
                {
                    EntityRef target = context.Transaction.OpenMut(targetId);
                    ref HealthComponent health = ref target.Write(Ship.Health);
                    ref readonly BehaviorComponent targetBehavior = ref target.Read(Ship.Behavior);
                    DamageResolution resolution = DamageResolutionRules.Resolve(
                        health.Current,
                        groupEnd - groupStart,
                        participatingAttackerCount);
                    health.Current = resolution.RemainingHealth;
                    if (resolution.IsDestroyed)
                    {
                        RecordKillParticipations(damageIntents, groupStart, groupEnd, targetId);
                        damageResolutionState.RecordDestroyedShip(targetId, (BehaviorMode)targetBehavior.Mode);
                        context.Transaction.Destroy(targetId);
                    }
                    else if (resolution.AppliedDamage > 0)
                    {
                        damageResolutionState.AddDamagedSurvivor(targetId);
                    }
                }

                groupStart = groupEnd;
            }
        }
        finally
        {
            ArrayPool<DamageIntent>.Shared.Return(damageIntents);
        }

        // 将本 tick 的死亡及时传播到 workset、roster 与派生统计（AC2）
        if (damageResolutionState.DestroyedShipCount > 0)
        {
            state.ApplyDestroyedShips(
                damageResolutionState.DestroyedShipCount,
                damageResolutionState.DestroyedShips,
                damageResolutionState.DestroyedShipModes);
        }
    }

    private bool HasDamageIntents() => damageIntentQueues.Any(static queue => !queue.IsEmpty);

    private void RecordKillParticipations(
        DamageIntent[] damageIntents,
        int groupStart,
        int groupEnd,
        EntityId targetId)
    {
        EntityId previousAttackerId = EntityId.Null;
        for (int intentIndex = groupStart; intentIndex < groupEnd; intentIndex++)
        {
            EntityId attackerId = damageIntents[intentIndex].Attacker;
            if (intentIndex == groupStart || attackerId != previousAttackerId)
            {
                damageResolutionState.AddKillParticipation(attackerId, targetId);
                previousAttackerId = attackerId;
            }
        }
    }

    private sealed class DamageIntentComparer : IComparer<DamageIntent>
    {
        public static DamageIntentComparer Instance { get; } = new();

        public int Compare(DamageIntent left, DamageIntent right)
        {
            int targetComparison = left.Target.EntityKey.CompareTo(right.Target.EntityKey);
            return targetComparison != 0
                ? targetComparison
                : left.Attacker.EntityKey.CompareTo(right.Attacker.EntityKey);
        }
    }
}

internal readonly record struct KillParticipation(EntityId Attacker, EntityId Target);

internal sealed class DamageResolutionState
{
    private readonly List<KillParticipation> _killParticipations = [];
    private readonly List<EntityId> _destroyedShips = [];
    private readonly List<BehaviorMode> _destroyedShipModes = [];

    public IReadOnlyList<KillParticipation> KillParticipations => _killParticipations;

    public IReadOnlyList<EntityId> DestroyedShips => _destroyedShips;

    public IReadOnlyList<BehaviorMode> DestroyedShipModes => _destroyedShipModes;

    public void AddKillParticipation(EntityId attacker, EntityId target)
        => _killParticipations.Add(new KillParticipation(attacker, target));

    public HashSet<EntityId> DamagedSurvivors { get; } = [];

    public int DestroyedShipCount { get; private set; }

    private int _shotsFiredThisTick;
    private int _hitsThisTick;

    public int ShotsFiredThisTick => Volatile.Read(ref _shotsFiredThisTick);

    public int HitsThisTick => Volatile.Read(ref _hitsThisTick);

    public void AddDamagedSurvivor(EntityId shipId) => DamagedSurvivors.Add(shipId);

    public void RecordDestroyedShip(EntityId shipId, BehaviorMode destroyedMode)
    {
        _destroyedShips.Add(shipId);
        _destroyedShipModes.Add(destroyedMode);
        DestroyedShipCount++;
    }

    public void RecordShot(bool hit)
    {
        Interlocked.Increment(ref _shotsFiredThisTick);
        if (hit)
        {
            Interlocked.Increment(ref _hitsThisTick);
        }
    }

    public void Clear()
    {
        _killParticipations.Clear();
        _destroyedShips.Clear();
        _destroyedShipModes.Clear();
        DamagedSurvivors.Clear();
        DestroyedShipCount = 0;
        Interlocked.Exchange(ref _shotsFiredThisTick, 0);
        Interlocked.Exchange(ref _hitsThisTick, 0);
    }
}

internal sealed class ResolutionSystem(
    SimulationRuntimeState state,
    DamageResolutionState damageResolutionState) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("Resolution")
        .After("DamageResolution")
        .Phase(SpaceBattlePhases.Resolution)
        .ShouldRun(() => state.IsRunning)
        .Writes<BehaviorComponent>()
        .Writes<TrackingComponent>()
        .Writes<MotionComponent>()
        .Writes<WeaponComponent>()
        .Writes<AfterburnerComponent>()
        .Writes<TargetLockComponent>();

    protected override void Execute(TickContext context)
    {
        state.PhaseTiming.BeginResolutionPhase();
        TargetLockCleanupSystem.ClearDeadLocks(context, state, damageResolutionState.DestroyedShips);
        ApplyCombatReactions(context);
        int processedCount = 0;
        foreach (EntityId shipId in state.TickWorkset)
        {
            if (!context.Transaction.IsAlive(shipId))
            {
                continue;
            }

            processedCount++;

            EntityRef ship = context.Transaction.OpenMut(shipId);
            ref BehaviorComponent behavior = ref ship.Write(Ship.Behavior);
            if ((BehaviorMode)behavior.Mode != BehaviorMode.Tracking)
            {
                continue;
            }

            ref TrackingComponent tracking = ref ship.Write(Ship.Tracking);
            if (!tracking.Target.IsNull && context.Transaction.IsAlive(tracking.Target))
            {
                continue;
            }

            SteeringSystem.StartWandering(
                state.Definition,
                BehaviorRules.PackShipId(shipId),
                ref behavior,
                ref tracking,
                ref ship.Write(Ship.Motion));
            state.RecordModeTransition(BehaviorMode.Tracking, BehaviorMode.Wandering);
        }

        state.RecordConsumerProcessed("Resolution", processedCount);
        state.PhaseTiming.EndResolutionPhase();
    }

    private void ApplyCombatReactions(TickContext context)
    {
        foreach (EntityId woundedShipId in damageResolutionState.DamagedSurvivors)
        {
            if (context.Transaction.TryOpen(woundedShipId, out _))
            {
                EnterEscaping(context, woundedShipId);
            }
        }

        foreach (KillParticipation participation in damageResolutionState.KillParticipations)
        {
            if (damageResolutionState.DamagedSurvivors.Contains(participation.Attacker) ||
                !context.Transaction.TryOpen(participation.Attacker, out _))
            {
                continue;
            }

            EnterDisengaging(context, participation.Attacker);
        }
    }

    private void EnterEscaping(TickContext context, EntityId shipId)
    {
        EntityRef ship = context.Transaction.OpenMut(shipId);
        ref BehaviorComponent behavior = ref ship.Write(Ship.Behavior);
        ref TrackingComponent tracking = ref ship.Write(Ship.Tracking);
        ref MotionComponent motion = ref ship.Write(Ship.Motion);
        BehaviorMode oldMode = (BehaviorMode)behavior.Mode;
        bool wasEscaping = oldMode == BehaviorMode.Escaping;
        ulong escapeOrdinal = wasEscaping
            ? behavior.DecisionOrdinal - 1
            : behavior.DecisionOrdinal++;
        behavior.Mode = (byte)BehaviorMode.Escaping;
        behavior.ModeTicksRemaining = BehaviorRules.EscapingDurationTicks;
        if (!wasEscaping)
        {
            SteeringSystem.SetMotion(
                ref motion,
                BehaviorRules.CreateEscapeMotion(BehaviorRules.SelectEscapeFace(
                    state.Definition.Seed,
                    BehaviorRules.PackShipId(shipId),
                    escapeOrdinal)));
            state.RecordModeTransition(oldMode, BehaviorMode.Escaping);
        }

        TargetLockCleanupSystem.DisableWeapon(context, shipId);
        if (!ship.IsEnabled(Ship.Afterburner))
        {
            ship.Enable(Ship.Afterburner);
        }

        if (!wasEscaping)
        {
            ship.Write(Ship.Afterburner).ActivatedTick = checked((ulong)context.TickNumber);
        }

        foreach (EntityId targetLockId in state.CopyTargetLockIdsForOwner(shipId.EntityKey))
        {
            if (!context.Transaction.IsAlive(targetLockId))
            {
                state.RemoveTargetLock(targetLockId);
                continue;
            }

            EntityRef targetLockEntity = context.Transaction.OpenMut(targetLockId);
            ref TargetLockComponent targetLock = ref targetLockEntity.Write(TargetLock.Data);
            if ((TargetLockStatus)targetLock.Status != TargetLockStatus.Releasing)
            {
                TargetLockCleanupSystem.BeginRelease(context, shipId, ref targetLock);
            }
        }
    }

    private void EnterDisengaging(TickContext context, EntityId shipId)
    {
        EntityRef ship = context.Transaction.OpenMut(shipId);
        ref BehaviorComponent behavior = ref ship.Write(Ship.Behavior);
        var oldMode = (BehaviorMode)behavior.Mode;
        behavior.Mode = (byte)BehaviorMode.Disengaging;
        behavior.ModeTicksRemaining = BehaviorRules.DisengagingDurationTicks;
        state.RecordModeTransition(oldMode, BehaviorMode.Disengaging);
        ship.Write(Ship.Motion).Speed = BehaviorRules.DisengagingSpeed;
        TargetLockCleanupSystem.DisableWeapon(context, shipId);
        if (ship.IsEnabled(Ship.Afterburner))
        {
            ship.Disable(Ship.Afterburner);
        }
    }
}

internal sealed class OutputSystem(
    SimulationRuntimeState state,
    DamageResolutionState damageResolutionState,
    SpaceBattleObservationPublisher observationPublisher) : CallbackSystem
{
    protected override void Configure(SystemBuilder builder) => SpaceBattleSystemPolicies.Apply(builder)
        .Name("Output")
        .After("Resolution")
        .Phase(SpaceBattlePhases.Output)
        .ShouldRun(() => state.IsRunning)
        .ReadsFresh<PositionComponent>()
        .ReadsFresh<SpatialBoundsComponent>()
        .ReadsFresh<MotionComponent>()
        .ReadsFresh<HealthComponent>()
        .ReadsFresh<BehaviorComponent>()
        .ReadsFresh<TrackingComponent>()
        .ReadsFresh<WeaponComponent>()
        .ReadsFresh<AfterburnerComponent>()
        .ReadsFresh<TargetLockComponent>()
        .Writes<SimulationRunComponent>()
        .Writes<SimulationRunStateComponent>();

    protected override void Execute(TickContext context)
    {
        state.PhaseTiming.BeginPhase(PhaseTimingCollector.Output);
        ulong completedTicks = 0;
        bool tickCompleted = false;
        try
        {
            completedTicks = state.CompletedTicksForRuntimeTick(context.TickNumber);
            ref SimulationRunComponent run = ref context.Transaction.OpenMut(state.RunEntityId)
                .Write(SimulationRunEntity.Run);
            run.CompletedTicks = completedTicks;
            uint aliveShipCount = state.AliveShipCount;
            run.AliveShipCount = aliveShipCount;
            if (aliveShipCount <= 1 || completedTicks >= state.Definition.MaximumCompletedTicks)
            {
                TerminalShipState terminalShipState = ReadTerminalShipState(context);
                state.SetAliveShipCount(terminalShipState.AliveShipCount);
                run.AliveShipCount = terminalShipState.AliveShipCount;
                if (terminalShipState.AliveShipCount <= 1 ||
                    completedTicks >= state.Definition.MaximumCompletedTicks)
                {
                    ref SimulationRunStateComponent terminalRunState = ref context.Transaction.OpenMut(state.RunEntityId)
                        .Write(SimulationRunEntity.State);
                    SetTerminalResult(terminalShipState, ref terminalRunState);
                    state.MarkTerminal(completedTicks);
                }
            }

            if (state.IsSnapshotRequested(completedTicks))
            {
                state.CaptureSnapshot(
                    completedTicks,
                    SpaceBattleHost.ReadSnapshot(context.Transaction, damageResolutionState.KillParticipations));
            }

            SpaceBattleModeCounts modeCounts = state.GetDerivedModeCounts();
            int activeLockCount = state.DerivedActiveLockCount;
            EntityRef runEntity = context.Transaction.Open(state.RunEntityId);
            ref readonly SimulationRunStateComponent runState = ref runEntity.Read(SimulationRunEntity.State);
            SpaceBattleRunSample runSnapshot = new(
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
                    : 0,
                runState.Outcome == (byte)SimulationRunOutcome.Winner);
            SpaceBattleTickSample sample = state.RecordTickSample(
                context.TickNumber,
                completedTicks,
                runState.ProcessSegment,
                runSnapshot,
                modeCounts,
                activeLockCount,
                damageResolutionState.ShotsFiredThisTick,
                damageResolutionState.HitsThisTick,
                damageResolutionState.DestroyedShipCount);
            observationPublisher.TryPublish(in sample);

            state.RecordConsumerProcessed("Output", modeCounts.Total);
            state.MarkCompletedTicks(completedTicks);
            state.PhaseTiming.EndPhase(PhaseTimingCollector.Output);
            state.PhaseTiming.EndTick(completedTicks);
            tickCompleted = true;
        }
        finally
        {
            state.EndTick(completedTicks, tickCompleted);
            damageResolutionState.Clear();
        }
    }

    private static void SetTerminalResult(
        TerminalShipState terminalShipState,
        ref SimulationRunStateComponent runState)
    {
        if (terminalShipState.AliveShipCount == 1)
        {
            runState.Status = (byte)SimulationRunStatus.Completed;
            runState.Outcome = (byte)SimulationRunOutcome.Winner;
            runState.WinnerEntityKey = terminalShipState.Winner.EntityKey;
            return;
        }

        if (terminalShipState.AliveShipCount == 0)
        {
            runState.Status = (byte)SimulationRunStatus.Completed;
            runState.Outcome = (byte)SimulationRunOutcome.Draw;
            runState.WinnerEntityKey = 0;
            return;
        }

        runState.Status = (byte)SimulationRunStatus.TimedOut;
        runState.Outcome = (byte)SimulationRunOutcome.TimedOut;
        runState.WinnerEntityKey = 0;
    }

    private static TerminalShipState ReadTerminalShipState(TickContext context)
    {
        EntityId survivor = EntityId.Null;
        uint aliveShipCount = 0;
        foreach (EntityId shipId in context.Transaction.Query<Ship>().Execute())
        {
            if (!context.Transaction.IsAlive(shipId))
            {
                continue;
            }

            aliveShipCount++;
            survivor = aliveShipCount == 1 ? shipId : EntityId.Null;
        }

        return new TerminalShipState(aliveShipCount, survivor);
    }

    private readonly record struct TerminalShipState(uint AliveShipCount, EntityId Winner);
}
