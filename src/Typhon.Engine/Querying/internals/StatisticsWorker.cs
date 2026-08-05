// unset

using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Background thread that periodically checks ComponentTables for mutation activity and triggers statistics rebuilds (HLL, MCV, Histogram) when thresholds are exceeded.
/// </summary>
/// <remarks>
/// <para>
/// Follows the <see cref="CheckpointManager"/> lifecycle pattern: dedicated background thread,
/// <see cref="ManualResetEventSlim"/> for wake/shutdown, double-check lock for idempotent <see cref="Start"/>.
/// </para>
/// <para>
/// The worker polls at <see cref="StatisticsOptions.PollIntervalMs"/> and for each ComponentTable:
/// <list type="number">
///   <item>Checks <see cref="ArchetypeClusterState.MutationsSinceRebuild"/> against threshold</item>
///   <item>Computes page-sampling interval based on table size and <see cref="StatisticsOptions.SamplingMinEntities"/></item>
///   <item>Calls <see cref="StatisticsRebuilder.RebuildClusterAll"/> for a single-pass cluster scan</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class StatisticsWorker : ResourceNode
{
    private readonly DatabaseEngine _dbe;
    private readonly StatisticsOptions _options;
    private readonly EpochManager _epochManager;

    private Thread _thread;
    private volatile bool _shutdown;
    private readonly Lock _lifecycleLock = new();
    private readonly ManualResetEventSlim _wakeEvent = new(false);
    private volatile Exception _lastError;

    internal StatisticsWorker(DatabaseEngine dbe, StatisticsOptions options, EpochManager epochManager, IResource parent) : 
        base("StatisticsWorker", ResourceType.Node, parent)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(epochManager);

        // Floor-clamp to prevent CPU spin or degenerate rebuild behavior
        if (options.PollIntervalMs < 100)
        {
            options.PollIntervalMs = 100;
        }
        if (options.MutationThreshold < 1)
        {
            options.MutationThreshold = 1;
        }

        _dbe = dbe;
        _options = options;
        _epochManager = epochManager;
    }

    /// <summary>Whether the worker thread is currently running.</summary>
    public bool IsRunning => _thread != null && _thread.IsAlive;

    /// <summary>Last exception encountered during statistics rebuild (diagnostic). Null if no error has occurred.</summary>
    public Exception LastError => _lastError;

    /// <summary>
    /// Starts the background worker thread. Idempotent — does nothing if already running.
    /// </summary>
    public void Start()
    {
        if (_thread != null && _thread.IsAlive)
        {
            return;
        }

        lock (_lifecycleLock)
        {
            if (_thread != null && _thread.IsAlive)
            {
                return;
            }

            _shutdown = false;
            _thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "Typhon-Statistics"
            };
            _thread.Start();
        }
    }

    /// <summary>
    /// Wakes the worker thread immediately to check for pending rebuilds.
    /// </summary>
    public void ForceRebuild() => _wakeEvent.Set();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shutdown = true;
            _wakeEvent.Set();
            _thread?.Join(TimeSpan.FromSeconds(10));
            _wakeEvent.Dispose();
        }
        base.Dispose(disposing);
    }

    private void WorkerLoop()
    {
        while (!_shutdown)
        {
            _wakeEvent.Wait(_options.PollIntervalMs);
            _wakeEvent.Reset();

            if (_shutdown)
            {
                break;
            }

            // The per-ComponentTable half of this sweep is gone (#629). It rebuilt statistics over ComponentSegment, where a cluster-backed archetype keeps
            // no entities, into an array no estimator reads — and it could never even run: ComponentTable.MutationsSinceRebuild was never incremented by any
            // production write path, so the threshold below it was never crossed. The per-archetype sweep is the whole job.
            RebuildClusterArchetypes();
        }
    }

    /// <summary>
    /// The per-archetype half of the sweep (#665). A cluster-backed archetype's entities are not in any <see cref="ComponentTable.ComponentSegment"/>, so the
    /// loop above sees no mutations for them and would leave their statistics frozen at whatever the first rebuild produced — or, if it were merely
    /// handed the ComponentTable, publish statistics built from an empty scan.
    /// </summary>
    /// <remarks>
    /// Deliberately a second pass rather than a branch inside the first: the two are keyed on different things (a ComponentTable is shared across archetypes,
    /// a cluster state belongs to exactly one) and share no threshold state.
    /// </remarks>
    private void RebuildClusterArchetypes()
    {
        var states = _dbe._archetypeStates;
        if (states == null)
        {
            return;
        }

        for (var i = 0; i < states.Length; i++)
        {
            if (_shutdown)
            {
                return;
            }

            var clusterState = states[i]?.ClusterState;
            if (clusterState == null || clusterState.MutationsSinceRebuild < _options.MutationThreshold)
            {
                continue;
            }

            var liveEntities = clusterState.ActiveClusterCount * clusterState.Layout.ClusterSize;
            if (liveEntities < _options.MinEntitiesForRebuild)
            {
                continue;
            }

            try
            {
                var clusterInterval = ComputeClusterSamplingInterval(clusterState, _options.SamplingMinEntities);
                using var rebuildScope = TyphonEvent.BeginStatisticsRebuild(liveEntities, clusterState.MutationsSinceRebuild, clusterInterval);
                StatisticsRebuilder.RebuildClusterAll(clusterState, _epochManager, clusterInterval);

                // Reset after a successful rebuild — a failure preserves the count so the next sweep retries.
                clusterState.MutationsSinceRebuild = 0;
            }
            catch (Exception ex)
            {
                _lastError = ex;
                // Continue with the other archetypes — one archetype's failure should not block the rest.
            }
        }
    }

    /// <summary>
    /// Cluster-granularity sampling interval: visit roughly <paramref name="samplingMinEntities"/> entities. Returns 1 (every active cluster) when the
    /// archetype is small enough. The cluster counterpart of <see cref="ComputeSamplingInterval"/>, whose unit is a page.
    /// </summary>
    private static int ComputeClusterSamplingInterval(ArchetypeClusterState clusterState, int samplingMinEntities)
    {
        var clusterSize = clusterState.Layout.ClusterSize;
        var totalEntities = clusterState.ActiveClusterCount * clusterSize;
        if (totalEntities <= samplingMinEntities || clusterSize == 0)
        {
            return 1;
        }

        var clustersNeeded = Math.Max(1, samplingMinEntities / clusterSize);
        return Math.Max(1, clusterState.ActiveClusterCount / clustersNeeded);
    }

    /// <summary>
    /// Computes page-granularity sampling interval: every Nth page to visit ~samplingMinEntities chunks.
    /// Returns 1 (full scan) when the table is small enough.
    /// </summary>
    private static int ComputeSamplingInterval(ComponentTable ct, int samplingMinEntities)
    {
        int totalEntities = ct.EstimatedEntityCount;
        if (totalEntities <= samplingMinEntities)
        {
            return 1;
        }

        int chunksPerPage = ct.ComponentSegment.ChunkCountPerPage;
        if (chunksPerPage == 0)
        {
            return 1;
        }

        int totalPages = ct.ComponentSegment.Length;
        int pagesNeeded = (samplingMinEntities + chunksPerPage - 1) / chunksPerPage;
        return Math.Max(1, totalPages / pagesNeeded);
    }
}
