using Typhon.Engine;

namespace SpaceBattle;

public sealed class ShipMembershipViews : IDisposable
{
    private readonly Transaction _viewTransaction;
    private long _runtimeRefreshCount;
    private long _combatRefreshCount;
    private long _runtimeAddedCount;
    private long _combatAddedCount;
    private long _runtimeRemovedCount;
    private long _combatRemovedCount;
    private int _disposed;

    private ShipMembershipViews(
        Transaction viewTransaction,
        EcsView<Ship> runtimeShips,
        EcsView<Ship> combatShips)
    {
        _viewTransaction = viewTransaction;
        RuntimeShips = runtimeShips;
        CombatShips = combatShips;
    }

    public EcsView<Ship> RuntimeShips { get; }

    public EcsView<Ship> CombatShips { get; }

    public long RuntimeRefreshCount => Volatile.Read(ref _runtimeRefreshCount);

    public long CombatRefreshCount => Volatile.Read(ref _combatRefreshCount);

    public long RuntimeAddedCount => Volatile.Read(ref _runtimeAddedCount);

    public long CombatAddedCount => Volatile.Read(ref _combatAddedCount);

    public long RuntimeRemovedCount => Volatile.Read(ref _runtimeRemovedCount);

    public long CombatRemovedCount => Volatile.Read(ref _combatRemovedCount);

    public static ShipMembershipViews RebuildAndCreate(
        DatabaseEngine engine,
        EntityId runEntityId,
        long startupFenceTick)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (runEntityId.IsNull)
        {
            throw new ArgumentException("SimulationRun entity 不能为空。", nameof(runEntityId));
        }

        using (Transaction rebuild = engine.CreateQuickTransaction())
        {
            foreach (EntityId shipId in rebuild.Query<Ship>().Execute())
            {
                rebuild.OpenMut(shipId).Write(Ship.RunMembership).RunEntityKey = runEntityId.EntityKey;
            }

            rebuild.Commit();
        }

        engine.WriteTickFence(startupFenceTick);

        Transaction viewTransaction = engine.CreateQuickTransaction();
        EcsView<Ship> runtimeShips = null;
        EcsView<Ship> combatShips = null;
        try
        {
            runtimeShips = CreateView(viewTransaction, runEntityId.EntityKey);
            combatShips = CreateView(viewTransaction, runEntityId.EntityKey);
            runtimeShips.ClearDelta();
            combatShips.ClearDelta();
            return new ShipMembershipViews(viewTransaction, runtimeShips, combatShips);
        }
        catch
        {
            try
            {
                combatShips?.Dispose();
            }
            finally
            {
                try
                {
                    runtimeShips?.Dispose();
                }
                finally
                {
                    viewTransaction.Dispose();
                }
            }

            throw;
        }
    }

    public void Refresh(Transaction transaction)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(transaction);
        RuntimeShips.Refresh(transaction);
        ViewDelta runtimeDelta = RuntimeShips.GetDelta();
        Interlocked.Increment(ref _runtimeRefreshCount);
        Interlocked.Add(ref _runtimeAddedCount, runtimeDelta.Added.Count);
        Interlocked.Add(ref _runtimeRemovedCount, runtimeDelta.Removed.Count);

        CombatShips.Refresh(transaction);
        ViewDelta combatDelta = CombatShips.GetDelta();
        Interlocked.Increment(ref _combatRefreshCount);
        Interlocked.Add(ref _combatAddedCount, combatDelta.Added.Count);
        Interlocked.Add(ref _combatRemovedCount, combatDelta.Removed.Count);
    }

    public void RefreshForRuntime(Transaction transaction)
    {
        Refresh(transaction);
        ClearDeltas();
    }

    internal void ClearDeltas()
    {
        RuntimeShips.ClearDelta();
        CombatShips.ClearDelta();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                CombatShips.Dispose();
            }
            finally
            {
                try
                {
                    RuntimeShips.Dispose();
                }
                finally
                {
                    _viewTransaction.Dispose();
                }
            }
        }
    }

    private static EcsView<Ship> CreateView(Transaction transaction, long runEntityKey) => transaction
        .Query<Ship>()
        .WhereField<ShipRunMembershipComponent>(membership => membership.RunEntityKey == runEntityKey)
        .ToView();
}
