using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Profiler;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Builds the v7+ static-structure record arrays (<see cref="ComponentDefinitionRecord"/>, <see cref="ArchetypeDefinitionRecord"/>,
/// <see cref="IndexCatalogEntry"/>, <see cref="RuntimeConfigRecord"/>, <see cref="EventQueueRecord"/>,
/// <see cref="ResourceGraphNodeRecord"/>) by introspecting a live <see cref="DatabaseEngine"/> and the global
/// <see cref="ArchetypeRegistry"/>. Hosts call this at trace start to make their <c>.typhon-trace</c> files self-describing
/// — the Workbench schema panels then render trace sessions with the same fidelity as live <c>OpenSession</c> attaches.
/// </summary>
/// <remarks>
/// All methods are pure read-only over registry / engine state — safe to call on the host thread before <see cref="TyphonProfiler.Start"/>.
/// They produce plain POCOs (the records in <c>Typhon.Profiler</c>) so the data is decoupled from the engine's internal types and won't pin engine objects in memory.
/// </remarks>
internal static class ProfilerStaticDataBuilder
{
    /// <summary>
    /// Build all four static-structure arrays the engine + runtime can introspect. <paramref name="runtime"/> is
    /// optional — when null, the event-queue catalog is empty (host has no scheduler context).
    /// </summary>
    /// <remarks>
    /// All three builders share a single <see cref="ComponentIdMap"/> so component-type IDs stay consistent across the
    /// component-definitions, archetype-definitions, and index-catalog tables. Components whose CLR type isn't in the
    /// public <see cref="ArchetypeRegistry"/> map (engine-internal system tables like <c>ComponentR1</c>) get fresh
    /// negative sentinel IDs allocated by the map — guarantees uniqueness so the Workbench's downstream dictionary
    /// builds don't collide on a shared "unknown" key.
    /// </remarks>
    public static StaticDataBundle BuildAll(DatabaseEngine engine, TyphonRuntime runtime = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var idMap = new ComponentIdMap();
        var components = BuildComponentDefinitions(engine, idMap);
        var archetypes = BuildArchetypeDefinitions(idMap);
        var indexes = BuildIndexCatalog(engine, idMap);
        var queues = BuildEventQueueCatalog(runtime);
        return new StaticDataBundle(components, archetypes, indexes, queues);
    }

    /// <summary>
    /// One <see cref="ComponentDefinitionRecord"/> per registered component table. Field order matches the engine's
    /// FieldId allocation (not necessarily offset-ascending) — the Workbench can re-sort if needed.
    /// </summary>
    public static ComponentDefinitionRecord[] BuildComponentDefinitions(DatabaseEngine engine) => BuildComponentDefinitions(engine, new ComponentIdMap());

    private static ComponentDefinitionRecord[] BuildComponentDefinitions(DatabaseEngine engine, ComponentIdMap idMap)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var tables = engine.GetAllComponentTables().ToArray();
        var records = new ComponentDefinitionRecord[tables.Length];
        for (var i = 0; i < tables.Length; i++)
        {
            var table = tables[i];
            var def = table.Definition;
            var fields = new List<FieldDefinitionRecord>(def.FieldsByName.Count);
            foreach (var field in def.FieldsByName.Values)
            {
                if (field.IsStatic)
                {
                    continue;
                }

                var flags = (byte)(
                    (field.HasIndex ? 0x01 : 0)
                    | (field.IndexAllowMultiple ? 0x02 : 0)
                    | (field.IsIndexAuto ? 0x04 : 0)
                    | (field.HasSpatialIndex ? 0x08 : 0)
                    | (field.IsForeignKey ? 0x10 : 0));

                fields.Add(new FieldDefinitionRecord
                {
                    FieldId = field.FieldId,
                    Name = field.Name,
                    FieldType = (byte)field.Type,
                    UnderlyingType = (byte)field.UnderlyingType,
                    Offset = field.OffsetInComponentStorage,
                    Size = field.SizeInComponentStorage,
                    ArrayLength = field.ArrayLength,
                    Flags = flags,
                    SpatialFieldType = (byte)field.SpatialFieldType,
                    SpatialMode = (byte)field.SpatialMode,
                    SpatialCellSize = field.SpatialCellSize,
                    SpatialMargin = field.SpatialMargin,
                    SpatialCategory = field.SpatialCategory,
                    ForeignKeyTargetType = field.ForeignKeyTargetType?.FullName ?? string.Empty,
                });
            }

            records[i] = new ComponentDefinitionRecord
            {
                ComponentTypeId = idMap.GetOrAllocate(table),
                Name = def.POCOType?.FullName ?? def.Name,
                Revision = def.Revision,
                StorageMode = (byte)def.StorageMode,
                AllowMultiple = def.AllowMultiple,
                ComponentStorageSize = def.ComponentStorageSize,
                ComponentStorageOverhead = def.ComponentStorageOverhead,
                ComponentStorageTotalSize = def.ComponentStorageTotalSize,
                IndicesCount = (ushort)def.IndicesCount,
                MultipleIndicesCount = (ushort)def.MultipleIndicesCount,
                SpatialField = def.SpatialField?.Name ?? string.Empty,
                Fields = fields.ToArray(),
            };
        }
        return records;
    }

    /// <summary>
    /// One <see cref="ArchetypeDefinitionRecord"/> per archetype in the global registry. Skips half-initialised entries
    /// (same guard as <c>SchemaService.ListArchetypes</c>) where engine startup didn't finalise <c>_slotToComponentType</c>.
    /// </summary>
    public static ArchetypeDefinitionRecord[] BuildArchetypeDefinitions() => BuildArchetypeDefinitions(new ComponentIdMap());

    private static ArchetypeDefinitionRecord[] BuildArchetypeDefinitions(ComponentIdMap idMap)
    {
        var records = new List<ArchetypeDefinitionRecord>();
        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (meta._slotToComponentType == null || meta.ComponentCount == 0)
            {
                continue;
            }

            var children = meta.ChildArchetypeIds?.ToArray() ?? [];
            // Translate slot-ordered component type IDs through the shared idMap so any engine-internal type
            // referenced from this archetype's slot map gets the same synthetic ID it has in ComponentDefinitions.
            // For user types the registry already returned a stable ID, so the map is a no-op pass-through.
            var slotTypes = meta._slotToComponentType ?? [];
            var componentTypeIds = new int[slotTypes.Length];
            for (var s = 0; s < slotTypes.Length; s++)
            {
                componentTypeIds[s] = slotTypes[s] != null ? idMap.GetOrAllocate(slotTypes[s]) : -1;
            }
            var cascade = meta._cascadeTargets?.Select(t => t.ChildArchetypeId).ToArray() ?? [];

            var flags = (byte)((meta.IsClusterEligible ? 0x01 : 0) | (meta.HasClusterIndexes ? 0x02 : 0) | (meta.HasClusterSpatial ? 0x04 : 0));

            ArchetypeClusterInfoRecord clusterInfo = null;
            if (meta.IsClusterEligible && meta.ClusterLayout != null)
            {
                var ci = meta.ClusterLayout;
                clusterInfo = new ArchetypeClusterInfoRecord
                {
                    ClusterSize = (ushort)ci.ClusterSize,
                    ClusterStride = (uint)ci.ClusterStride,
                    HeaderSize = (uint)ci.HeaderSize,
                    EntityIdsOffset = (uint)ci.EntityIdsOffset,
                    IndexElementIdsBaseOffset = (uint)ci.IndexElementIdsBaseOffset,
                    MultipleIndexedFieldCount = (ushort)ci.MultipleIndexedFieldCount,
                };
            }

            records.Add(new ArchetypeDefinitionRecord
            {
                ArchetypeId = meta.ArchetypeId,
                Name = meta.ArchetypeType?.FullName ?? meta.ArchetypeType?.Name ?? $"Archetype#{meta.ArchetypeId}",
                Revision = meta.Revision,
                ParentArchetypeId = meta.ParentArchetypeId,
                ChildArchetypeIds = children,
                ComponentCount = meta.ComponentCount,
                ComponentTypeIds = componentTypeIds,
                VersionedSlotMask = meta.VersionedSlotMask,
                TransientSlotMask = meta.TransientSlotMask,
                CascadeTargets = cascade,
                Flags = flags,
                ClusterInfo = clusterInfo,
            });
        }
        return records.ToArray();
    }

    /// <summary>
    /// Flat (componentTypeId, fieldId) → index-variant catalog. Walks every component table; matches <c>IndexedFieldInfo</c>
    /// to its source <c>Field</c> via the field's offset (the same approach <c>SchemaService.GetIndexesForComponent</c> uses).
    /// </summary>
    public static IndexCatalogEntry[] BuildIndexCatalog(DatabaseEngine engine) => BuildIndexCatalog(engine, new ComponentIdMap());

    private static IndexCatalogEntry[] BuildIndexCatalog(DatabaseEngine engine, ComponentIdMap idMap)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var entries = new List<IndexCatalogEntry>();
        foreach (var table in engine.GetAllComponentTables())
        {
            var infos = table.IndexedFieldInfos;
            if (infos == null || infos.Length == 0)
            {
                continue;
            }
            var componentTypeId = idMap.GetOrAllocate(table);
            var fieldsByOffset = new Dictionary<int, DBComponentDefinition.Field>();
            foreach (var f in table.Definition.FieldsByName.Values)
            {
                fieldsByOffset[f.OffsetInComponentStorage] = f;
            }

            for (var i = 0; i < infos.Length; i++)
            {
                var info = infos[i];
                if (!fieldsByOffset.TryGetValue(info.OffsetToField, out var field))
                {
                    continue;
                }
                entries.Add(new IndexCatalogEntry
                {
                    ComponentTypeId = componentTypeId,
                    FieldId = field.FieldId,
                    Variant = EncodeIndexVariant(field.Type, field.IndexAllowMultiple),
                    AllowMultiple = info.AllowMultiple,
                    IsSpatial = false,
                    IsAuto = field.IsIndexAuto,
                });
            }

            // Spatial index is a separate axis — emit one entry when present so the catalog's flat list is complete.
            if (table.Definition.SpatialField != null)
            {
                entries.Add(new IndexCatalogEntry
                {
                    ComponentTypeId = componentTypeId,
                    FieldId = table.Definition.SpatialField.FieldId,
                    Variant = 0xFF,
                    AllowMultiple = false,
                    IsSpatial = true,
                    IsAuto = table.Definition.SpatialField.IsIndexAuto,
                });
            }
        }
        return entries.ToArray();
    }

    /// <summary>
    /// Build a <see cref="RuntimeConfigRecord"/> from a runtime options snapshot. Caller is responsible for sourcing the option values — typically
    /// <c>TyphonRuntime.Options</c> at session start.
    /// </summary>
    public static RuntimeConfigRecord BuildRuntimeConfig(int baseTickRate, int workerCount, int telemetryRingCapacity, int parallelQueryMinChunkSize) =>
        new()
        {
            BaseTickRate = baseTickRate,
            WorkerCount = workerCount,
            TelemetryRingCapacity = telemetryRingCapacity,
            ParallelQueryMinChunkSize = parallelQueryMinChunkSize,
        };

    /// <summary>
    /// Build the v11 Track→DAG hierarchy tables (#354) from a live <see cref="TyphonRuntime"/>'s <see cref="DagScheduler.Tracks"/>. Each
    /// <see cref="Track"/> becomes a <see cref="TrackRecord"/>; each <see cref="Dag"/> becomes a <see cref="DagRecord"/> whose
    /// <see cref="DagRecord.TrackIndex"/> points at its owning track in the returned tracks array. Returns empty arrays when the runtime is null
    /// or carries no scheduler (e.g., standalone profiling).
    /// </summary>
    public static (TrackRecord[] Tracks, DagRecord[] Dags) BuildTrackHierarchy(TyphonRuntime runtime)
    {
        if (runtime?.Scheduler == null)
        {
            return ([], []);
        }

        var schedulerTracks = runtime.Scheduler.Tracks;
        var tracks = new TrackRecord[schedulerTracks.Count];
        var dags = new List<DagRecord>();
        for (var t = 0; t < schedulerTracks.Count; t++)
        {
            var track = schedulerTracks[t];
            tracks[t] = new TrackRecord
            {
                Name = track.Name ?? string.Empty,
                OrderIndex = track.OrderIndex,
                Tags = [.. track.Tags],
            };
            foreach (var dag in track.Dags)
            {
                var phaseNames = new string[dag.ResolvedPhases.Length];
                for (var p = 0; p < phaseNames.Length; p++)
                {
                    phaseNames[p] = dag.ResolvedPhases[p].Name ?? string.Empty;
                }
                dags.Add(new DagRecord
                {
                    Id = dag.Id,
                    Name = dag.Name ?? string.Empty,
                    TrackIndex = t,
                    PhaseNames = phaseNames,
                });
            }
        }
        return (tracks, dags.ToArray());
    }

    /// <summary>
    /// Event-queue catalog from a live <see cref="TyphonRuntime"/>. Walks <see cref="DagScheduler.EventQueues"/>; the event-payload type is resolved by
    /// reflection from the queue's runtime type (every queue is an <c>EventQueue&lt;T&gt;</c> — T's full name is captured).
    /// Returns an empty array when the runtime is null or carries no queues.
    /// </summary>
    public static EventQueueRecord[] BuildEventQueueCatalog(TyphonRuntime runtime)
    {
        if (runtime?.Scheduler == null)
        {
            return [];
        }

        var queues = runtime.Scheduler.EventQueues;
        var records = new List<EventQueueRecord>(queues.Count);
        foreach (var q in queues)
        {
            if (q == null)
            {
                continue;
            }

            // Skip unassigned queues — QueueId == ushort.MaxValue means the queue was created outside a scheduler context (no QueueTickEnd telemetry will be
            // emitted for it). Including them in the catalog would confuse downstream readers expecting a 1:1 correspondence between catalog entries and per-tick events.
            if (q.QueueId == ushort.MaxValue)
            {
                continue;
            }

            // EventQueue<T>'s payload type is the single generic argument. Derive it once per queue rather than requiring callers to thread the type in — keeps
            // the catalog self-describing without per-host glue. Non-generic subclasses of EventQueueBase (none today, but defensive) get an empty event-type name.
            string eventTypeName = string.Empty;
            var qType = q.GetType();
            if (qType.IsGenericType && qType.GetGenericArguments() is { Length: >= 1 } args)
            {
                eventTypeName = args[0].FullName ?? args[0].Name;
            }
            records.Add(new EventQueueRecord
            {
                QueueIndex = q.QueueId,
                Name = q.Name ?? string.Empty,
                Capacity = q.Capacity,
                EventTypeName = eventTypeName,
            });
        }
        return records.ToArray();
    }

    /// <summary>
    /// Resource-graph snapshot from a root <c>ResourceNode</c>. Pre-order tree walk; each node maps to one record. The
    /// engine exposes its root node via <see cref="DatabaseEngine"/>'s ResourceNode base class — pass <c>engine</c> directly.
    /// </summary>
    public static ResourceGraphNodeRecord[] BuildResourceGraphSnapshot(IResource root)
    {
        if (root == null)
        {
            return [];
        }

        var nodes = new List<ResourceGraphNodeRecord>();
        var visited = new HashSet<IResource>(ReferenceEqualityComparer.Instance);
        WalkResource(root, parentId: -1, nodes, visited);
        return nodes.ToArray();
    }

    private static void WalkResource(IResource node, long parentId, List<ResourceGraphNodeRecord> nodes, HashSet<IResource> visited)
    {
        // Cycle guard — IResource graphs SHOULD be trees but parent/child wiring isn't enforced. Without this, a
        // cycle introduced by an engine bug would infinite-loop the trace export.
        if (!visited.Add(node))
        {
            return;
        }

        // Sequential ID per node — stable for the duration of this walk, guaranteed unique. The previous GetHashCode() approach risked silent tree corruption
        // on hash collisions (Workbench reconstructs the tree via parentId pointers; two nodes sharing an id would graft children to whichever the reader saw first).
        // The wire format is a long, but we only emit at most a few thousand resources — int range is fine.
        var id = (long)nodes.Count + 1;
        nodes.Add(new ResourceGraphNodeRecord
        {
            Id = id,
            Name = node.Name ?? string.Empty,
            Type = (byte)node.Type,
            ParentId = parentId,
            CreatedAtUtcTicks = 0, // IResource doesn't currently surface CreatedAt; leave 0 — informational only.
            ExhaustionPolicy = 0,
        });
        if (node.Children != null)
        {
            foreach (var child in node.Children)
            {
                WalkResource(child, id, nodes, visited);
            }
        }
    }

    /// <summary>
    /// Resolves CLR component types to stable trace-wire IDs. Wraps <see cref="ArchetypeRegistry.GetComponentTypeId(Type)"/>
    /// for user-registered components and allocates fresh negative sentinel IDs for engine-internal types whose CLR type
    /// is not in the registry's public map (system tables: <c>ComponentR1</c>, <c>ArchetypeR1</c>, <c>SchemaHistoryR1</c>),
    /// or for <see cref="ComponentTable"/>s whose <c>POCOType</c> is null.
    ///
    /// Why: <see cref="ArchetypeRegistry.GetComponentTypeId"/> returns -1 for unknown types and the previous
    /// <c>ResolveComponentTypeId</c> returned 0 for null POCOTypes — both are sentinels and multiple components hit them,
    /// producing duplicate IDs in the trace's component-definitions table. Downstream consumers (the Workbench
    /// <see cref="Typhon.Workbench.Schema.TraceSchemaProvider"/>) build dictionaries keyed by ID and would either crash
    /// (the original bug) or silently lose entries (the <c>TryAdd</c> fix). With unique negative sentinels, every
    /// component has a distinct ID — Workbench panels list every system table individually rather than collapsing them.
    ///
    /// The negative sentinel range starts at -2 (not -1) so a future legacy reader that interprets -1 as "missing" still
    /// works. Wire format is signed int32; -2..int.MinValue gives ample room.
    ///
    /// Same instance is shared across <see cref="BuildComponentDefinitions"/>, <see cref="BuildArchetypeDefinitions"/>,
    /// and <see cref="BuildIndexCatalog"/> within a single <see cref="BuildAll"/> call — guarantees an archetype's slot
    /// map references the same synthetic ID as the matching component-definition record.
    /// </summary>
    private sealed class ComponentIdMap
    {
        private readonly Dictionary<Type, int> _byType = new();
        private int _nextSyntheticId = -2;

        public int GetOrAllocate(ComponentTable table)
        {
            ArgumentNullException.ThrowIfNull(table);
            var pocoType = table.Definition.POCOType;
            if (pocoType == null)
            {
                // Null POCOType — can't dedupe by Type. Allocate a fresh ID per call. In practice this shouldn't
                // happen (every ComponentTable has a CLR type) but defensive against engine refactors.
                return _nextSyntheticId--;
            }
            return GetOrAllocate(pocoType);
        }

        public int GetOrAllocate(Type pocoType)
        {
            ArgumentNullException.ThrowIfNull(pocoType);
            if (_byType.TryGetValue(pocoType, out var cached))
            {
                return cached;
            }

            var registryId = ArchetypeRegistry.GetComponentTypeId(pocoType);
            var id = registryId >= 0 ? registryId : _nextSyntheticId--;
            _byType[pocoType] = id;
            return id;
        }
    }

    private static byte EncodeIndexVariant(FieldType type, bool allowMultiple)
    {
        // Low nibble: 0 Byte, 1 Short, 2 Int, 3 Long, 4 Float, 5 Double, 6 Char, 7 String64. Anything else => 0xF (unknown).
        var lo = (FieldType)((int)type & 0xFF) switch
        {
            FieldType.Byte => 0,
            FieldType.Short => 1,
            FieldType.Int => 2,
            FieldType.Long => 3,
            FieldType.Float => 4,
            FieldType.Double => 5,
            FieldType.Char => 6,
            FieldType.String64 => 7,
            _ => 0xF,
        };
        var hi = allowMultiple ? 0x10 : 0x00;
        return (byte)(hi | lo);
    }
}

/// <summary>
/// Bundle of static-data records returned by <see cref="ProfilerStaticDataBuilder.BuildAll"/>. Decomposed at the
/// <see cref="ProfilerSessionMetadata"/> construction site (each field maps to a metadata field of the same name).
/// </summary>
internal sealed record StaticDataBundle(
    ComponentDefinitionRecord[] ComponentDefinitions,
    ArchetypeDefinitionRecord[] ArchetypeDefinitions,
    IndexCatalogEntry[] IndexCatalog,
    EventQueueRecord[] EventQueues);
