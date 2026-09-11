using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Exports ECS metrics to OpenTelemetry via <see cref="System.Diagnostics.Metrics.Meter"/>: per-archetype EntityMap gauges and per-component transient memory gauges.
/// All metrics are zero-cost reads of existing fields — no new Interlocked overhead on hot paths.
/// </summary>
[PublicAPI]
[ExcludeFromCodeCoverage]
public sealed class EcsMetricsExporter : IDisposable
{
    /// <summary>
    /// The OpenTelemetry <see cref="System.Diagnostics.Metrics.Meter"/> name under which all ECS instruments are published.
    /// </summary>
    public const string MeterName = "Typhon.ECS";

    /// <summary>
    /// The Meter version reported to OpenTelemetry.
    /// </summary>
    public const string MeterVersion = "1.0.0";

    private readonly DatabaseEngine _dbe;
    private readonly Meter _meter;

    /// <summary>
    /// Creates the exporter and registers the ECS observable instruments on a new <see cref="System.Diagnostics.Metrics.Meter"/>.
    /// The instruments read live engine state on collection; no work is done until an OTel consumer polls them.
    /// </summary>
    /// <param name="dbe">The engine whose archetype and component-table state is sampled. Must not be <c>null</c>.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="dbe"/> is <c>null</c>.</exception>
    public EcsMetricsExporter(DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        _dbe = dbe;
        _meter = new Meter(MeterName, MeterVersion);
        RegisterInstruments();
    }

    /// <summary>
    /// The OpenTelemetry <see cref="System.Diagnostics.Metrics.Meter"/> that owns the ECS observable instruments.
    /// </summary>
    public Meter Meter => _meter;

    private void RegisterInstruments()
    {
        _meter.CreateObservableGauge("typhon.ecs.entity_count", EnumerateEntityCount, "{entities}", "Live entity count per archetype");
        _meter.CreateObservableGauge("typhon.ecs.entitymap.load_factor", EnumerateLoadFactor, "1", "EntityMap hash table load factor per archetype (0.0-1.0)");
        _meter.CreateObservableCounter("typhon.ecs.entitymap.splits_total", EnumerateSplitCount, "{splits}", "Cumulative EntityMap bucket splits per archetype");
        _meter.CreateObservableGauge("typhon.ecs.transient.allocated_bytes", EnumerateTransientAllocatedBytes, "bytes", "Transient heap memory allocated per component type");
        _meter.CreateObservableGauge("typhon.ecs.transient.utilization", EnumerateTransientUtilization, "1", "Transient chunk utilization per component type (allocated/capacity)");

        // Spatial partitioning. The per-tick gauges are reset at every tick fence, so a scrape samples one arbitrary tick — they are here because "what
        // did the last tick cost" is a real question, but the *_total counters are what a rate should be computed from. See SpatialMigrationTelemetry.
        _meter.CreateObservableGauge("typhon.ecs.spatial.migrations", EnumerateMigrationCount, "{migrations}",
            "Cluster migrations executed in the last completed tick, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.hysteresis_absorbed", EnumerateHysteresisAbsorbed, "{crossings}",
            "Cell-boundary crossings absorbed by the hysteresis margin in the last completed tick, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.migration_duration_ms", EnumerateMigrationExecuteMs, "ms",
            "Wall-clock migration execution time in the last completed tick, summed across workers, per archetype");
        // #912. The slice count is what makes migration_duration_ms divisible: that gauge is a sum of per-SLICE spans, and the parallel fence sizes slices
        // from the worker count, so a dashboard dividing it by the migration count reads a per-slice fixed cost as a per-entity one and sees the number
        // climb whenever the engine is given more workers. Exported beside it rather than in a doc comment for that reason.
        _meter.CreateObservableGauge("typhon.ecs.spatial.migration_slices", EnumerateMigrationSliceCount, "{slices}",
            "Migrate-phase slices in the last completed tick, per archetype — the number of spans summed into migration_duration_ms");

        // #926. Exported beside the slice count because that is what it must be read against: one batch per indexed field per slice, so this tracks
        // migration_slices and NOT migration_count. A deployment where it starts following the migration count has lost the batching and is taking an
        // archetype-wide latch per migrated entity again.
        _meter.CreateObservableGauge("typhon.ecs.spatial.zone_map_batch_opens", EnumerateZoneMapBatchOpens, "{batches}",
            "Zone-map batches opened by the last tick's Migrate slices, per archetype — one per indexed field per slice");
        _meter.CreateObservableGauge("typhon.ecs.spatial.migration_prologue_ms", EnumerateMigrationPrologueMs, "ms",
            "Part of migration_duration_ms spent renting accessors before the first migrant, summed across slices, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.migration_epilogue_ms", EnumerateMigrationEpilogueMs, "ms",
            "Part of migration_duration_ms spent releasing accessors after the last migrant, summed across slices, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.crossings_executed", EnumerateCrossingsExecuted, "{migrations}",
            "Cell-crossing migrations executed in the last completed tick, per archetype");

        // #910 T0. A jump is a crossing into a non-adjacent cell; the largest arrival is the group an arrival repack would act on; a clamped destination
        // is a position written outside the configured world, which the engine used to absorb without a trace.
        _meter.CreateObservableGauge("typhon.ecs.spatial.jump_crossings", EnumerateJumpCrossings, "{migrations}",
            "Cell crossings into a non-adjacent cell in the last completed tick, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.clamped_destinations", EnumerateClampedDestinations, "{migrations}",
            "Cell crossings whose position lay outside the configured world and were clamped into an edge cell, last completed tick, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.largest_arrival_run", EnumerateLargestArrivalRun, "{migrations}",
            "Most cell crossings into one destination cell in the last completed tick, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.arrival_cells_touched", EnumerateArrivalCellsTouched, "{cells}",
            "Distinct destination cells of the last completed tick's cell crossings, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.relocations_executed", EnumerateRelocationsExecuted, "{migrations}",
            "Intra-cell relocations executed in the last completed tick, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.repairs_executed", EnumerateRepairsExecuted, "{migrations}",
            "Repair moves executed in the last completed tick, per archetype — the three sum exactly to migrations");
        _meter.CreateObservableGauge("typhon.ecs.spatial.finalize_lock_acquisitions", EnumerateFinalizeLockAcquisitions, "{acquisitions}",
            "Exclusive acquisitions of the archetype-wide finalize latch in the last completed tick — the fence's only archetype-wide serialisation point");
        _meter.CreateObservableGauge("typhon.ecs.spatial.active_clusters", EnumerateActiveClusterCount, "{clusters}",
            "Live clusters per archetype — the denominator for the migration and drifter rates");
        _meter.CreateObservableGauge("typhon.ecs.spatial.clusters_scanned", EnumerateClustersScanned, "{clusters}",
            "Clusters written, and therefore examined by the intra-cell drifter scan, in the last completed tick, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.drifters_detected", EnumerateDriftersDetected, "{entities}",
            "Entities found outside their cluster's target region in the last completed tick, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.drift_absorbed", EnumerateDriftAbsorbed, "{entities}",
            "Entities inside the intra-cell drift margin, and therefore left alone, in the last completed tick, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.recluster_budget_ms", EnumerateReclusterBudgetMs, "ms",
            "Per-tick re-clustering budget consumed in the last completed tick, per archetype (zero until throttled re-clustering exists)");
        // #911 O2. Tightness is the reading the whole subsystem is judged on, and until now it existed in no runtime surface at all. The sample count is
        // exported beside the two means because they are a mean over WRITTEN clusters: a settled archetype reports zero samples, and a scrape that cannot
        // see that would read "clusters are points" off a mean of nothing.
        _meter.CreateObservableGauge("typhon.ecs.spatial.tightness_samples", EnumerateTightnessSamples, "{clusters}",
            "Clusters that contributed a tightness reading in the last completed tick, per archetype — zero on a tick that wrote nothing");
        _meter.CreateObservableGauge("typhon.ecs.spatial.cluster_extent_ratio", EnumerateMeanClusterExtentRatio, "1",
            "Mean cluster max-axis extent as a fraction of the cell edge, over the clusters written in the last completed tick, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.packing_bound", EnumerateMeanPackingBound, "1",
            "Mean packing bound (slots/entities)^(1/d) as a fraction of the cell edge, over the same clusters, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.tightness_to_bound", EnumerateMeanTightnessToBound, "1",
            "Measured extent against what geometry allows: 1 is optimal packing, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.max_cluster_overhang", EnumerateMaxClusterOverhang, "1",
            "Largest distance any cluster box reaches outside its own cell, in world units, per archetype — a running maximum, not a per-tick value");
        _meter.CreateObservableGauge("typhon.ecs.spatial.cell_tree_promotions", EnumerateCellTreePromotions, "{cells}",
            "Cell halves promoted to a per-cell R-Tree in the last completed tick, per archetype");
        _meter.CreateObservableGauge("typhon.ecs.spatial.cell_tree_demotions", EnumerateCellTreeDemotions, "{cells}",
            "Cell halves demoted back to the linear scan in the last completed tick, per archetype");
        _meter.CreateObservableCounter("typhon.ecs.spatial.migrations_total", EnumerateTotalMigrations, "{migrations}",
            "Cumulative cluster migrations since engine open, per archetype");
        _meter.CreateObservableCounter("typhon.ecs.spatial.hysteresis_absorbed_total", EnumerateTotalHysteresisAbsorbed, "{crossings}",
            "Cumulative hysteresis-absorbed cell-boundary crossings since engine open, per archetype");

        // Engine-wide, set once at open and constant thereafter. The transient cell layer is rebuilt from entity positions on every open; these two say what
        // that costs, which is what decides whether it can stay transient.
        // #911. Engine-wide and a SPAN: the per-archetype `migration_duration_ms` above is summed across workers, so it cannot be compared to a frame
        // budget on its own. Their ratio is the parallelism the work achieved.
        _meter.CreateObservableGauge("typhon.ecs.spatial.fence_span_ms", () => _dbe.LastFenceSpanMs, "ms",
            "Span of the previous partitioning fence — what the host waited, not summed worker CPU; zero when the fence ran serially");

        // Exported because migration_duration_ms and its siblings above are CPU SUMMED ACROSS WORKERS, and a summed figure rises with the worker count on
        // unchanged work. Without this ratio beside them a dashboard cannot tell work being spread from work getting slower, and reads every added worker as
        // a regression. Divide a summed-CPU figure by this to get the elapsed cost a frame budget is spent in — using migration_duration_ms, which covers the
        // same three phases this ratio does. Below 1 is a real reading: dispatch overhead exceeding the work.
        _meter.CreateObservableGauge("typhon.ecs.spatial.fence_migration_parallelism", () => _dbe.LastFenceMigrationParallelism, "{workers}",
            "Summed worker CPU over elapsed span for the previous tick's migration phases — the divisor that makes the summed-CPU gauges readable");

        // The span above starts at Prep's Prepare; this one brackets the whole fence call, so it also carries the serial prep no worker count shrinks.
        // Their difference is the fence's Amdahl fraction, which is why both are exported rather than one standing in for the other.
        _meter.CreateObservableGauge("typhon.ecs.spatial.fence_stall_ms", () => _dbe.LastFenceStallMs, "ms",
            "Whole previous partitioning fence as the host felt it, serial prep included — the interruption to budget a frame against");
        _meter.CreateObservableGauge("typhon.ecs.open.cellstate_rebuild_ms", () => _dbe.OpenCellStateRebuildMs, "ms",
            "Milliseconds spent reconstructing cluster-to-cell mappings at open");
        _meter.CreateObservableGauge("typhon.ecs.open.cluster_aabb_rebuild_ms", () => _dbe.OpenClusterAabbRebuildMs, "ms",
            "Milliseconds spent recomputing cluster AABBs and the per-cell index at open");
    }

    private IEnumerable<Measurement<long>> EnumerateMigrationCount() => EnumerateSpatialLong(static t => t.MigrationCount);

    private IEnumerable<Measurement<long>> EnumerateHysteresisAbsorbed() => EnumerateSpatialLong(static t => t.HysteresisAbsorbedCount);

    private IEnumerable<Measurement<long>> EnumerateActiveClusterCount() => EnumerateSpatialLong(static t => t.ActiveClusterCount);

    private IEnumerable<Measurement<long>> EnumerateClustersScanned() => EnumerateSpatialLong(static t => t.ClustersScanned);

    private IEnumerable<Measurement<long>> EnumerateDriftersDetected() => EnumerateSpatialLong(static t => t.DriftersDetected);

    private IEnumerable<Measurement<long>> EnumerateDriftAbsorbed() => EnumerateSpatialLong(static t => t.DriftAbsorbedCount);

    private IEnumerable<Measurement<long>> EnumerateTotalMigrations() => EnumerateSpatialLong(static t => t.TotalMigrations);

    private IEnumerable<Measurement<long>> EnumerateTotalHysteresisAbsorbed() => EnumerateSpatialLong(static t => t.TotalHysteresisAbsorbed);

    private IEnumerable<Measurement<double>> EnumerateMigrationExecuteMs() => EnumerateSpatialDouble(static t => t.MigrationExecuteMs);

    private IEnumerable<Measurement<double>> EnumerateReclusterBudgetMs() => EnumerateSpatialDouble(static t => t.ReclusterBudgetUsedMs);

    private IEnumerable<Measurement<long>> EnumerateMigrationSliceCount() => EnumerateSpatialLong(static t => t.MigrationSliceCount);

    private IEnumerable<Measurement<long>> EnumerateZoneMapBatchOpens() => EnumerateSpatialLong(static t => t.ZoneMapBatchOpens);

    private IEnumerable<Measurement<double>> EnumerateMigrationPrologueMs() => EnumerateSpatialDouble(static t => t.MigrationPrologueMs);

    private IEnumerable<Measurement<double>> EnumerateMigrationEpilogueMs() => EnumerateSpatialDouble(static t => t.MigrationEpilogueMs);

    private IEnumerable<Measurement<long>> EnumerateCrossingsExecuted() => EnumerateSpatialLong(static t => t.CrossingsExecuted);

    private IEnumerable<Measurement<long>> EnumerateJumpCrossings() => EnumerateSpatialLong(static t => t.JumpCrossings);

    private IEnumerable<Measurement<long>> EnumerateClampedDestinations() => EnumerateSpatialLong(static t => t.ClampedDestinations);

    private IEnumerable<Measurement<long>> EnumerateLargestArrivalRun() => EnumerateSpatialLong(static t => t.LargestArrivalRun);

    private IEnumerable<Measurement<long>> EnumerateArrivalCellsTouched() => EnumerateSpatialLong(static t => t.ArrivalCellsTouched);

    private IEnumerable<Measurement<long>> EnumerateRelocationsExecuted() => EnumerateSpatialLong(static t => t.RelocationsExecuted);

    private IEnumerable<Measurement<long>> EnumerateRepairsExecuted() => EnumerateSpatialLong(static t => t.RepairsExecuted);

    private IEnumerable<Measurement<long>> EnumerateFinalizeLockAcquisitions() => EnumerateSpatialLong(static t => t.FinalizeLockAcquisitions);

    private IEnumerable<Measurement<long>> EnumerateTightnessSamples() => EnumerateSpatialLong(static t => t.TightnessSampleCount);

    private IEnumerable<Measurement<long>> EnumerateCellTreePromotions() => EnumerateSpatialLong(static t => t.CellTreePromotions);

    private IEnumerable<Measurement<long>> EnumerateCellTreeDemotions() => EnumerateSpatialLong(static t => t.CellTreeDemotions);

    private IEnumerable<Measurement<double>> EnumerateMeanClusterExtentRatio() => EnumerateSpatialDouble(static t => t.MeanClusterExtentRatio);

    private IEnumerable<Measurement<double>> EnumerateMeanPackingBound() => EnumerateSpatialDouble(static t => t.MeanPackingBound);

    private IEnumerable<Measurement<double>> EnumerateMeanTightnessToBound() => EnumerateSpatialDouble(static t => t.MeanTightnessToBound);

    private IEnumerable<Measurement<double>> EnumerateMaxClusterOverhang() => EnumerateSpatialDouble(static t => t.MaxClusterOverhang);

    /// <summary>
    /// Walks every archetype that owns cluster state and projects one field of its <see cref="SpatialMigrationTelemetry"/> snapshot, tagged by archetype name.
    /// Archetypes without cluster state are skipped rather than reported as zero — a non-spatial archetype has no migration count, and emitting one would
    /// invite a consumer to average over it.
    /// </summary>
    private IEnumerable<Measurement<long>> EnumerateSpatialLong(Func<SpatialMigrationTelemetry, long> select)
    {
        var states = _dbe._archetypeStates;
        if (states == null)
        {
            yield break;
        }

        for (int i = 0; i < states.Length; i++)
        {
            if (states[i]?.ClusterState == null)
            {
                continue;
            }

            yield return new Measurement<long>(select(_dbe.GetSpatialTelemetry(i)),
                new KeyValuePair<string, object>(TyphonSpanAttributes.EcsArchetype, GetArchetypeName(i)));
        }
    }

    /// <summary>Double-valued twin of <see cref="EnumerateSpatialLong"/>; see its remarks for why non-spatial archetypes are skipped.</summary>
    private IEnumerable<Measurement<double>> EnumerateSpatialDouble(Func<SpatialMigrationTelemetry, double> select)
    {
        var states = _dbe._archetypeStates;
        if (states == null)
        {
            yield break;
        }

        for (int i = 0; i < states.Length; i++)
        {
            if (states[i]?.ClusterState == null)
            {
                continue;
            }

            yield return new Measurement<double>(select(_dbe.GetSpatialTelemetry(i)),
                new KeyValuePair<string, object>(TyphonSpanAttributes.EcsArchetype, GetArchetypeName(i)));
        }
    }

    private IEnumerable<Measurement<long>> EnumerateEntityCount()
    {
        var states = _dbe._archetypeStates;
        if (states == null)
        {
            yield break;
        }

        for (int i = 0; i < states.Length; i++)
        {
            var es = states[i];
            if (es?.EntityMap == null)
            {
                continue;
            }

            yield return new Measurement<long>(es.EntityMap.EntryCount, new KeyValuePair<string, object>(TyphonSpanAttributes.EcsArchetype, GetArchetypeName(i)));
        }
    }

    private IEnumerable<Measurement<double>> EnumerateLoadFactor()
    {
        var states = _dbe._archetypeStates;
        if (states == null)
        {
            yield break;
        }

        for (int i = 0; i < states.Length; i++)
        {
            var es = states[i];
            if (es?.EntityMap == null)
            {
                continue;
            }

            yield return new Measurement<double>(es.EntityMap.LoadFactor, new KeyValuePair<string, object>(TyphonSpanAttributes.EcsArchetype, GetArchetypeName(i)));
        }
    }

    private IEnumerable<Measurement<long>> EnumerateSplitCount()
    {
        var states = _dbe._archetypeStates;
        if (states == null)
        {
            yield break;
        }

        for (int i = 0; i < states.Length; i++)
        {
            var es = states[i];
            if (es?.EntityMap == null)
            {
                continue;
            }

            yield return new Measurement<long>(es.EntityMap._splitCount, new KeyValuePair<string, object>(TyphonSpanAttributes.EcsArchetype, GetArchetypeName(i)));
        }
    }

    private IEnumerable<Measurement<long>> EnumerateTransientAllocatedBytes()
    {
        foreach (var table in _dbe.GetAllComponentTables())
        {
            if (table.StorageMode != StorageMode.Transient || table.TransientComponentSegment == null)
            {
                continue;
            }

            // PageCount is a plain int field — 32-bit read is atomic on x64, no Interlocked needed
            long bytes = (long)table.TransientComponentSegment.Store.PageCount * PagedMMF.PageSize;
            yield return new Measurement<long>(bytes, new KeyValuePair<string, object>(TyphonSpanAttributes.EcsComponentType, table.Definition.Name));
        }
    }

    private IEnumerable<Measurement<double>> EnumerateTransientUtilization()
    {
        foreach (var table in _dbe.GetAllComponentTables())
        {
            if (table.StorageMode != StorageMode.Transient || table.TransientComponentSegment == null)
            {
                continue;
            }

            int capacity = table.TransientComponentSegment.ChunkCapacity;
            double utilization = capacity > 0 ? (double)table.TransientComponentSegment.AllocatedChunkCount / capacity : 0.0;
            yield return new Measurement<double>(utilization, new KeyValuePair<string, object>(TyphonSpanAttributes.EcsComponentType, table.Definition.Name));
        }
    }

    private static string GetArchetypeName(int archetypeId)
    {
        var meta = ArchetypeRegistry.GetMetadata((ushort)archetypeId);
        return meta?.ArchetypeType?.Name ?? archetypeId.ToString();
    }

    /// <summary>
    /// Disposes the underlying <see cref="System.Diagnostics.Metrics.Meter"/>, unregistering every ECS instrument.
    /// </summary>
    public void Dispose() => _meter.Dispose();
}
