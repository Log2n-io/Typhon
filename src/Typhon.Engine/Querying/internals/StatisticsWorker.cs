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
///   <item>Checks <see cref="ComponentTable.MutationsSinceRebuild"/> against threshold</item>
///   <item>Computes page-sampling interval based on table size and <see cref="StatisticsOptions.SamplingMinEntities"/></item>
///   <item>Calls <see cref="StatisticsRebuilder.RebuildAll"/> for a single-pass chunk scan</item>
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

            foreach (var ct in _dbe.GetAllComponentTables())
            {
                if (_shutdown)
                {
                    break;
                }

                if (ct.IndexedFieldInfos.Length == 0)
                {
                    continue;
                }

                if (ct.MutationsSinceRebuild < _options.MutationThreshold)
                {
                    continue;
                }

                if (ct.EstimatedEntityCount < _options.MinEntitiesForRebuild)
                {
                    continue;
                }

                try
                {
                    int pageInterval = ComputeSamplingInterval(ct, _options.SamplingMinEntities);
                    using var rebuildScope = TyphonEvent.BeginStatisticsRebuild(ct.EstimatedEntityCount, ct.MutationsSinceRebuild, pageInterval);
                    StatisticsRebuilder.RebuildAll(ct, _epochManager, pageInterval);

                    // Reset counter after successful rebuild — if rebuild fails, mutations are preserved for retry
                    ct.MutationsSinceRebuild = 0;
                }
                catch (Exception ex)
                {
                    _lastError = ex;
                    // Continue processing other tables — one table's failure should not block the rest
                }
            }
        }
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
