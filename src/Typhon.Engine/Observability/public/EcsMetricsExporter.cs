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
    public const string MeterName = "Typhon.ECS";
    public const string MeterVersion = "1.0.0";

    private readonly DatabaseEngine _dbe;
    private readonly Meter _meter;

    public EcsMetricsExporter(DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        _dbe = dbe;
        _meter = new Meter(MeterName, MeterVersion);
        RegisterInstruments();
    }

    public Meter Meter => _meter;

    private void RegisterInstruments()
    {
        _meter.CreateObservableGauge("typhon.ecs.entity_count", EnumerateEntityCount, "{entities}", "Live entity count per archetype");
        _meter.CreateObservableGauge("typhon.ecs.entitymap.load_factor", EnumerateLoadFactor, "1", "EntityMap hash table load factor per archetype (0.0-1.0)");
        _meter.CreateObservableCounter("typhon.ecs.entitymap.splits_total", EnumerateSplitCount, "{splits}", "Cumulative EntityMap bucket splits per archetype");
        _meter.CreateObservableGauge("typhon.ecs.transient.allocated_bytes", EnumerateTransientAllocatedBytes, "bytes", "Transient heap memory allocated per component type");
        _meter.CreateObservableGauge("typhon.ecs.transient.utilization", EnumerateTransientUtilization, "1", "Transient chunk utilization per component type (allocated/capacity)");
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

    public void Dispose() => _meter.Dispose();
}
