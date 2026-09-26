using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Realms D1 (RT-3): one archetype's active clusters that lie in runnable realms, in active-list order, and a bit per chunk id for those that do not.
/// Rebuilt at tick start, single-threaded, only when the realm policy or the archetype's cluster set changed; dispatch and the dirty scans only read it.
/// </summary>
/// <remarks>
/// <see cref="Filtering"/> is false whenever no active cluster of the archetype lies in a non-runnable realm — always with one realm, or with every realm
/// runnable — and every reader then takes the path it took before realms (RLM-04). A cluster's realm never changes (entities migrate between clusters),
/// so the list only goes stale when a cluster is added or removed or a realm's state flips.
/// </remarks>
internal sealed class RealmDispatchIndex
{
    /// <summary>True when at least one active cluster lies in a non-runnable realm: dispatch selects from <see cref="Ids"/> and the scans test
    /// <see cref="Excluded"/>.</summary>
    internal bool Filtering;

    /// <summary>The runnable active clusters, <see cref="Count"/> of them; valid while <see cref="Filtering"/>.</summary>
    internal int[] Ids = [];

    internal int Count;

    /// <summary>One bit per chunk id: set when the cluster lies in a non-runnable realm. Valid while <see cref="Filtering"/>.</summary>
    internal ulong[] Excluded = [];

    /// <summary>Moves whenever the content changes — the tier index's staleness stamp for the runnable set.</summary>
    internal int Stamp;

    /// <summary>Active clusters left out by the last rebuild — telemetry and tests.</summary>
    internal int ExcludedCount;

    private int _builtRunnableEpoch = -1;
    private int _builtClusterSetVersion = -1;
    private int[] _scratch = [];
    private ulong[] _scratchExcluded = [];

    /// <summary>The cluster-set version this list was built at. A selection finding the live version moved filters the live list instead (review #4).
    /// </summary>
    internal int BuiltClusterSetVersion => _builtClusterSetVersion;

    /// <summary>True when <paramref name="chunkId"/> lies in a non-runnable realm. Only meaningful while <see cref="Filtering"/>.</summary>
    internal bool IsExcluded(int chunkId)
    {
        var word = chunkId >> 6;
        return word < Excluded.Length && (Excluded[word] & (1UL << (chunkId & 63))) != 0;
    }

    /// <summary>Brings the index up to date with the realm policy and the cluster set. Tick start only (RLM-03).</summary>
    internal void Update(ArchetypeClusterState cs, RealmTable realms)
    {
        // Keyed on RUNNABILITY, not the whole policy: an Active <-> Simulated or divisor-only flip changes no member (review #4).
        var policy = realms.RunnableEpoch;
        if (realms.NonRunnableCount == 0)
        {
            // The common case, every realm runnable: nothing to filter, whatever the cluster set.
            if (Filtering)
            {
                Filtering = false;
                ExcludedCount = 0;
                Stamp++;
            }

            _builtRunnableEpoch = policy;
            _builtClusterSetVersion = -1;
            return;
        }

        var version = cs.ClusterSetVersion;
        if (policy == _builtRunnableEpoch && version == _builtClusterSetVersion)
        {
            return;
        }

        // Built into scratch and compared with the current lists: the stamp — the tier index's staleness signal — moves only when the content changed
        // (review #4), so a policy flip that leaves this archetype's runnable set as it was costs no tier-index rebuild.
        var realmMap = cs.ClusterRealmMap;
        var active = cs.ReadActiveClusterList(out var activeCount);
        if (_scratch.Length < activeCount)
        {
            _scratch = new int[Math.Max(activeCount, _scratch.Length * 2)];
        }

        var words = ((realmMap?.Length ?? 0) + 63) >> 6;
        if (_scratchExcluded.Length < words)
        {
            _scratchExcluded = new ulong[Math.Max(words, _scratchExcluded.Length * 2)];
        }
        else
        {
            Array.Clear(_scratchExcluded);
        }

        var count = 0;
        var excluded = 0;
        for (var i = 0; i < activeCount; i++)
        {
            var chunkId = active[i];
            if (realmMap != null && chunkId < realmMap.Length && !realms.IsRunnable(realmMap[chunkId]))
            {
                var word = chunkId >> 6;
                if (word >= _scratchExcluded.Length)
                {
                    Array.Resize(ref _scratchExcluded, Math.Max(word + 1, _scratchExcluded.Length * 2));
                }

                _scratchExcluded[word] |= 1UL << (chunkId & 63);
                excluded++;
                continue;
            }

            _scratch[count++] = chunkId;
        }

        var same = Filtering == (excluded > 0) && count == Count && _scratch.AsSpan(0, count).SequenceEqual(Ids.AsSpan(0, Count))
            && Excluded.Length == _scratchExcluded.Length && Excluded.AsSpan().SequenceEqual(_scratchExcluded);
        (Ids, _scratch) = (_scratch, Ids);
        (Excluded, _scratchExcluded) = (_scratchExcluded, Excluded);
        Count = count;
        ExcludedCount = excluded;
        Filtering = excluded > 0;
        if (!same)
        {
            Stamp++;
        }

        _builtRunnableEpoch = policy;
        _builtClusterSetVersion = version;
    }
}
