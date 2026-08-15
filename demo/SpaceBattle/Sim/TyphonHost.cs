using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SpaceBattle;

/// <summary>
/// Boots a Typhon engine and drives the tick fence by hand.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT using <c>TyphonRuntime</c>. The runtime owns its own clock on a TickDriver thread, which is
/// exactly what this tool must not have: the whole point is to pause, single-step and slow the simulation down to
/// watch migration and AABB churn happen. Driving <see cref="DatabaseEngine.WriteTickFence(long)"/> directly gives a
/// deterministic, host-owned clock — the "test/admin path" the fence documents — at the cost of the parallel fence,
/// which is irrelevant at observation speeds.
/// </para>
/// <para>
/// It also halves startup: no worker pool, no DAG construction, no scheduler.
/// </para>
/// </remarks>
// Internal, not public: several members return engine-internal types (ArchetypeClusterState, SpatialGrid), and a
// public member cannot expose a less-accessible type. Nothing outside this assembly consumes it anyway.
internal sealed class TyphonHost : IDisposable
{
    private readonly Config _cfg;
    private ServiceProvider _provider;
    private IServiceScope _scope;

    public DatabaseEngine DBE { get; private set; }

    /// <summary>The grid config we constructed — the accessor cannot give WorldMin/WorldMax back.</summary>
    public SpatialGridConfig GridConfig { get; private set; }

    public long Tick { get; private set; }

    public TyphonHost(Config cfg) => _cfg = cfg;

    public void Boot()
    {
        PagedMMF.AcwTracePage = _cfg.AcwTracePage;
        PagedMMF.DirtyTracePage = _cfg.DirtyTracePage;
        var services = new ServiceCollection();
        services
            .AddLogging(c => c.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opt =>
            {
                opt.DatabaseName = "SpaceBattle";
                opt.DatabaseDirectory = AppContext.BaseDirectory;
                opt.DatabaseCacheSize = 256 * 1024 * 1024;
            })
            .AddScopedDatabaseEngine(opt =>
            {
                // Keep the WAL on: with it off, UoW.Flush syncs pages inline instead of delegating to the async WAL
                // thread, which measured ~4x slower on the AntHill fence. Same trap applies here.
                //
                // The defaults are tuned for a database you intend to keep. This one is deleted at boot four lines
                // below, so paying for crash durability buys nothing and the WAL writer is the demo's tightest
                // throughput constraint: ~25 000 ships x ~92 B of changed components at 60 Hz is on the order of
                // 100 MB/s of log, and a stall there surfaces as WalBackPressureTimeout in the tick fence.
                opt.Wal = new WalWriterOptions
                {
                    UseFUA = _cfg.WalUseFua,
                    SegmentSize = (uint)(_cfg.WalSegmentSizeMB * 1024 * 1024),
                    StagingBufferSize = _cfg.WalStagingBufferKB * 1024,
                    PreAllocateSegments = _cfg.WalPreAllocateSegments,
                };
            });

        _provider = services.BuildServiceProvider();
        _provider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        _scope = _provider.CreateScope();
        DBE = _scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        DBE.RegisterComponentFromAccessor<Pos>();
        DBE.RegisterComponentFromAccessor<Motion>();
        DBE.RegisterComponentFromAccessor<Combat>();
        DBE.RegisterComponentFromAccessor<StationInfo>();
        DBE.RegisterComponentFromAccessor<Bullet>();
        DBE.RegisterComponentFromAccessor<Miner>();
        DBE.RegisterComponentFromAccessor<Asteroid>();
        DBE.RegisterComponentFromAccessor<PickupInfo>();

        GridConfig = new SpatialGridConfig(
            worldMin: Vector2.Zero,
            worldMax: new Vector2(_cfg.WorldSize, _cfg.WorldSize),
            cellSize: _cfg.CellSize,
            migrationHysteresisRatio: _cfg.MigrationHysteresis);
        DBE.ConfigureSpatialGrid(GridConfig);

        DBE.InitializeArchetypes();

        // Every archetype here mutates its spatial field exclusively through WriteSpatial, so opt them all in:
        // the fence then visits only clusters whose ClusterProcessBitmap bit is set instead of scanning every
        // active cluster unconditionally. Stations never move, so their bitmap stays empty and their fence pass
        // early-returns entirely — which is the cheap half of the static/dynamic split, for free.
        DBE.SetSpatialBarrierOnly<Ship>();
        DBE.SetSpatialBarrierOnly<Station>();
        DBE.SetSpatialBarrierOnly<Shot>();
        DBE.SetSpatialBarrierOnly<Rock>();
        DBE.SetSpatialBarrierOnly<Loot>();

        ShipArchetypeId = Archetype<Ship>.Metadata.ArchetypeId;
        StationArchetypeId = Archetype<Station>.Metadata.ArchetypeId;
        ShotArchetypeId = Archetype<Shot>.Metadata.ArchetypeId;
        RockArchetypeId = Archetype<Rock>.Metadata.ArchetypeId;
        LootArchetypeId = Archetype<Loot>.Metadata.ArchetypeId;
    }

    public int ShipArchetypeId { get; private set; }
    public int StationArchetypeId { get; private set; }
    public int ShotArchetypeId { get; private set; }
    public int RockArchetypeId { get; private set; }
    public int LootArchetypeId { get; private set; }

    /// <summary>Runs the engine's tick fence: migration detection, migration apply, AABB refresh, finalize.</summary>
    /// <summary>
    /// Checkpoint progress. A WAL segment can only be retired once a checkpoint has made its contents redundant,
    /// so these counters are what distinguish "the log is written faster than the disk takes it" from "the log is
    /// never retired at all" — measured, the demo accrued 31 GB across 116 segments with none recycled.
    /// </summary>
    /// <summary>
    /// #817 step 1: is the coverage gate stuck on the SAME page every cycle, or a churning set? Identity decides
    /// the fix — a pinned page is a leaked-ACW / stale-counter bug with the gate behaving correctly, whereas a
    /// churning set means the gate genuinely has no liveness guarantee and needs the per-page LSN refinement.
    /// </summary>
    public string DescribeCheckpointSkips()
    {
        var cm = DBE.CheckpointManager;
        if (cm == null)
        {
            return "no checkpoint manager";
        }
        var (acw, held, stale) = cm.SkipCauses;
        var pages = cm.LastSkippedPages;
        var sb = new System.Text.StringBuilder();
        sb.Append("skipped ").Append(pages.Length)
          .Append("  repeated-from-previous-cycle ").Append(cm.RepeatSkippedPages)
          .Append("  longest single-page streak ").Append(cm.MaxConsecutiveSkipsForOnePage)
          .Append("   causes: ACW ").Append(acw).Append("  writer-held>100ms ").Append(held)
          .Append("  stale-counter ").Append(stale).Append("   pages [");
        for (var i = 0; i < pages.Length && i < 12; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(pages[i]);
        }
        if (pages.Length > 12)
        {
            sb.Append(" …");
        }
        sb.Append(']');

        // The clincher. Read at report time, with the tick loop finished and no transaction open, so any non-zero
        // ActiveChunkWriters here cannot be a page that merely happens to be busy — it is a registration that was
        // incremented and never released.
        var pinned = 0;
        var sample = new System.Text.StringBuilder();
        for (var i = 0; i < pages.Length; i++)
        {
            var live = cm.ActiveChunkWritersOf(pages[i]);
            if (live == 0)
            {
                continue;
            }
            pinned++;
            if (pinned <= 6)
            {
                sample.Append(' ').Append(pages[i]).Append(":acw=").Append(live);
            }
        }
        sb.Append("   QUIESCENT non-zero ACW: ").Append(pinned).Append(" of ").Append(pages.Length).Append(sample);
        return sb.ToString();
    }

    /// <summary>Dirty-page census (#817 follow-up: PageCacheBackpressureTimeout).</summary>
    public (int Dirty, int Total, int FirstDirtyPage) CheckpointStats0() =>
        DBE.CheckpointManager?.CountDirtyPages() ?? (0, 0, -1);

    public (long Checkpoints, long SegmentsRecycled, long PagesWritten, long GatedCycles, bool Running) CheckpointStats()
    {
        var cm = DBE.CheckpointManager;
        return cm == null
            ? (0, 0, 0, 0, false)
            : (cm.TotalCheckpoints, cm.TotalSegmentsRecycled, cm.TotalPagesWritten, cm.ConsecutiveGatedCycles, cm.IsRunning);
    }

    /// <summary>Worst single tick fence observed, ms. Cumulative for the run.</summary>
    public double MaxFenceMs { get; private set; }

    /// <summary>Fences that took longer than <see cref="LongFenceThresholdMs"/>. The tail, counted rather than averaged.</summary>
    public int LongFenceCount { get; private set; }

    /// <summary>Tick number of the worst fence, so a stall can be correlated with what the simulation was doing.</summary>
    public long MaxFenceTick { get; private set; }

    public const double LongFenceThresholdMs = 50.0;

    /// <summary>
    /// The fence is where the WAL is written, and therefore where the engine blocks when the log cannot keep up —
    /// a <c>WalBackPressureTimeout</c> is raised from inside this call after 30 s of waiting for buffer space.
    /// </summary>
    /// <remarks>
    /// Timed for the MAXIMUM, not the mean. A back-pressure stall is a tail event: measured over 3 000 ticks at
    /// 23 465 ships the mean fence was 0.22 ms and utterly unaffected by WAL tuning, which says nothing whatsoever
    /// about whether one fence in ten thousand blocked for seconds. Mean latency cannot see the failure this
    /// instrument exists to catch, so the max and an over-threshold count are what get recorded.
    /// </remarks>
    public void RunTickFence()
    {
        Tick++;
        if (_cfg.ForceCheckpointEveryTicks > 0 && Tick % _cfg.ForceCheckpointEveryTicks == 0)
        {
            DBE.ForceCheckpoint();
        }
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        DBE.WriteTickFence(Tick);
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (ms > MaxFenceMs)
        {
            MaxFenceMs = ms;
            MaxFenceTick = Tick;
        }
        if (ms > LongFenceThresholdMs)
        {
            LongFenceCount++;
        }
    }

    // ─── Internal state access, for the debug overlays ────────────────────────────────────────────────────────────
    // Everything below is why this assembly is on the engine's InternalsVisibleTo list. There is no public route to
    // per-cell occupancy, to the authoritative cluster->cell map, or to the migration counters.

    public ArchetypeClusterState ClusterStateOf(int archetypeId) =>
        DBE._archetypeStates[archetypeId].ClusterState;

    public SpatialGrid Grid => DBE.SpatialGrid;

    /// <summary>Per-cell entity count. Cross-archetype sum — ships, stations and shots all contribute.</summary>
    public int CellEntityCount(int cellKey) => Grid.GetCell(cellKey).EntityCount;

    public int CellClusterCount(int cellKey) => Grid.GetCell(cellKey).ClusterCount;

    /// <summary>
    /// Entities in the cells the rectangle touches, summed from level-1 occupancy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An estimate, and knowingly a generous one: a cell is counted whole even when the rectangle clips a corner of
    /// it, so a tight camera over one 2 km cell reports everything in that cell. That bias is the right one for the
    /// caller — the LOD rule uses it to decide whether the scene is about to saturate, and over-estimating means
    /// erring toward the cheaper representation.
    /// </para>
    /// <para>
    /// Costs O(cells touched) and touches no entity data, which is what makes it usable in the density tier where
    /// counting entities is precisely what we are trying to avoid.
    /// </para>
    /// </remarks>
    public int CountEntitiesInRect(in WorldRect rect)
    {
        var g = GridConfig;
        var inv = 1f / g.CellSize;
        var cx0 = Math.Clamp((int)MathF.Floor((rect.MinX - g.WorldMin.X) * inv), 0, g.GridWidth - 1);
        var cy0 = Math.Clamp((int)MathF.Floor((rect.MinY - g.WorldMin.Y) * inv), 0, g.GridHeight - 1);
        var cx1 = Math.Clamp((int)MathF.Floor((rect.MaxX - g.WorldMin.X) * inv), 0, g.GridWidth - 1);
        var cy1 = Math.Clamp((int)MathF.Floor((rect.MaxY - g.WorldMin.Y) * inv), 0, g.GridHeight - 1);

        var total = 0;
        for (var cy = cy0; cy <= cy1; cy++)
        {
            for (var cx = cx0; cx <= cx1; cx++)
            {
                total += CellEntityCount(Grid.ComputeCellKey(cx, cy));
            }
        }
        return total;
    }

    public byte CellTier(int cellKey) => Grid.GetCell(cellKey).Tier;

    /// <summary>Authoritative cluster home cell. Differs from "cell of the AABB centre" inside the hysteresis zone —
    /// which is precisely the disagreement this tool exists to make visible.</summary>
    public int ClusterHomeCell(int archetypeId, int chunkId)
    {
        var st = ClusterStateOf(archetypeId);
        var map = st?.ClusterCellMap;
        return map != null && chunkId < map.Length ? map[chunkId] : -1;
    }

    public MigrationCounters ReadMigrationCounters()
    {
        var c = default(MigrationCounters);
        foreach (var id in new[] { ShipArchetypeId, ShotArchetypeId, StationArchetypeId, RockArchetypeId, LootArchetypeId })
        {
            var st = ClusterStateOf(id);
            if (st == null)
            {
                continue;
            }
            c.Migrations += st.LastTickMigrationCount;
            c.HysteresisAbsorbed += st.LastTickHysteresisAbsorbedCount;
            c.ExecuteMs += st.LastTickMigrationExecuteMs;
            c.ActiveClusters += st.ActiveClusterCount;
        }
        return c;
    }

    public void Dispose()
    {
        _scope?.Dispose();
        _provider?.Dispose();
    }
}

public struct MigrationCounters
{
    public int Migrations;
    public int HysteresisAbsorbed;
    public double ExecuteMs;
    public int ActiveClusters;
}
