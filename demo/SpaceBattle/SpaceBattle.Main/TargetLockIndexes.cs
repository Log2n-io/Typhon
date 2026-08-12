using Typhon.Engine;

namespace SpaceBattle;

internal readonly record struct TargetLockRelation(
    EntityId Owner,
    EntityId Target);

internal sealed class TargetLockIndexes
{
    private readonly SortedDictionary<long, EntityId> _locksByEntityKey = [];
    private readonly Dictionary<long, SortedSet<long>> _locksByOwner = [];
    private readonly Dictionary<long, SortedSet<long>> _locksByTarget = [];
    private readonly Dictionary<long, TargetLockRelation> _relationsByEntityKey = [];

    public int Count => _locksByEntityKey.Count;

    public static TargetLockIndexes Rebuild(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var indexes = new TargetLockIndexes();
        var targetLockIds = new List<EntityId>();
        foreach (EntityId targetLockId in transaction.Query<TargetLock>().Execute())
        {
            targetLockIds.Add(targetLockId);
        }

        targetLockIds.Sort(static (left, right) => left.EntityKey.CompareTo(right.EntityKey));
        foreach (EntityId targetLockId in targetLockIds)
        {
            TargetLockComponent targetLock = transaction.Open(targetLockId).Read(TargetLock.Data);
            indexes.Add(targetLockId, targetLock);
        }

        return indexes;
    }

    public void Add(EntityId targetLockId, TargetLockComponent targetLock)
        => Add(
            targetLockId,
            new TargetLockRelation(
                (EntityId)targetLock.Owner,
                (EntityId)targetLock.Target));

    public void Add(EntityId targetLockId, TargetLockRelation relation)
    {
        if (targetLockId.IsNull)
        {
            throw new ArgumentException("目标锁实体不能为空。", nameof(targetLockId));
        }

        if (!_relationsByEntityKey.TryAdd(
                targetLockId.EntityKey,
                relation))
        {
            throw new InvalidOperationException($"目标锁 {targetLockId.EntityKey} 已存在于派生索引中。");
        }

        _locksByEntityKey.Add(targetLockId.EntityKey, targetLockId);
        AddEndpoint(_locksByOwner, relation.Owner.EntityKey, targetLockId.EntityKey);
        AddEndpoint(_locksByTarget, relation.Target.EntityKey, targetLockId.EntityKey);
    }

    public bool Remove(EntityId targetLockId)
        => Remove(targetLockId, out _);

    public bool Remove(EntityId targetLockId, out TargetLockRelation relation)
    {
        if (!_relationsByEntityKey.Remove(targetLockId.EntityKey, out relation))
        {
            return false;
        }

        _locksByEntityKey.Remove(targetLockId.EntityKey);
        RemoveEndpoint(_locksByOwner, relation.Owner.EntityKey, targetLockId.EntityKey);
        RemoveEndpoint(_locksByTarget, relation.Target.EntityKey, targetLockId.EntityKey);
        return true;
    }

    public bool Contains(EntityId targetLockId)
        => _relationsByEntityKey.ContainsKey(targetLockId.EntityKey);

    public EntityId[] GetAllLockIds()
    {
        EntityId[] lockIds = new EntityId[_locksByEntityKey.Count];
        int index = 0;
        foreach (EntityId lockId in _locksByEntityKey.Values)
        {
            lockIds[index++] = lockId;
        }

        return lockIds;
    }

    public EntityId[] GetOwnerLockIds(long ownerEntityKey)
        => GetLockIds(_locksByOwner, ownerEntityKey);

    public EntityId[] GetLocksForShip(long shipEntityKey)
    {
        var lockKeys = new SortedSet<long>();
        AddEndpointKeys(_locksByOwner, shipEntityKey, lockKeys);
        AddEndpointKeys(_locksByTarget, shipEntityKey, lockKeys);
        EntityId[] lockIds = new EntityId[lockKeys.Count];
        int index = 0;
        foreach (long lockKey in lockKeys)
        {
            if (_locksByEntityKey.TryGetValue(lockKey, out EntityId lockId))
            {
                lockIds[index++] = lockId;
            }
        }

        if (index != lockIds.Length)
        {
            Array.Resize(ref lockIds, index);
        }

        return lockIds;
    }

    public Dictionary<long, int> CopyOwnerCounts()
        => CopyCounts(_locksByOwner);

    public Dictionary<long, int> CopyTargetCounts()
        => CopyCounts(_locksByTarget);

    public void Clear()
    {
        _locksByEntityKey.Clear();
        _locksByOwner.Clear();
        _locksByTarget.Clear();
        _relationsByEntityKey.Clear();
    }

    private EntityId[] GetLockIds(
        IReadOnlyDictionary<long, SortedSet<long>> byEndpoint,
        long endpointEntityKey)
    {
        if (!byEndpoint.TryGetValue(endpointEntityKey, out SortedSet<long> lockKeys))
        {
            return [];
        }

        EntityId[] lockIds = new EntityId[lockKeys.Count];
        int resultIndex = 0;
        foreach (long lockKey in lockKeys)
        {
            lockIds[resultIndex++] = _locksByEntityKey[lockKey];
        }

        return lockIds;
    }

    private static void AddEndpoint(
        IDictionary<long, SortedSet<long>> index,
        long endpointEntityKey,
        long targetLockEntityKey)
    {
        if (!index.TryGetValue(endpointEntityKey, out SortedSet<long> lockKeys))
        {
            lockKeys = [];
            index.Add(endpointEntityKey, lockKeys);
        }

        lockKeys.Add(targetLockEntityKey);
    }

    private static void AddEndpointKeys(
        IReadOnlyDictionary<long, SortedSet<long>> index,
        long endpointEntityKey,
        ISet<long> destination)
    {
        if (index.TryGetValue(endpointEntityKey, out SortedSet<long> lockKeys))
        {
            destination.UnionWith(lockKeys);
        }
    }

    private static void RemoveEndpoint(
        IDictionary<long, SortedSet<long>> index,
        long endpointEntityKey,
        long targetLockEntityKey)
    {
        if (!index.TryGetValue(endpointEntityKey, out SortedSet<long> lockKeys))
        {
            throw new InvalidOperationException("目标锁派生索引缺少关系端点。");
        }

        lockKeys.Remove(targetLockEntityKey);
        if (lockKeys.Count == 0)
        {
            index.Remove(endpointEntityKey);
        }
    }

    private static Dictionary<long, int> CopyCounts(
        IReadOnlyDictionary<long, SortedSet<long>> index)
    {
        var counts = new Dictionary<long, int>(index.Count);
        foreach (KeyValuePair<long, SortedSet<long>> pair in index)
        {
            counts.Add(pair.Key, pair.Value.Count);
        }

        return counts;
    }
}
