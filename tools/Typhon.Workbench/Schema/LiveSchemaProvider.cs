using System.Collections.Generic;
using System.Linq;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Schema;

namespace Typhon.Workbench.Schema;

/// <summary>
/// <see cref="IStaticSchemaProvider"/> backed by a live <see cref="DatabaseEngine"/>. Reads from the engine's component
/// table registry + global <c>ArchetypeRegistry</c> on every call — no caching, since the engine is the source of truth
/// and counts (entities, chunks, occupancy) change as the user edits data. Used by <c>OpenSession</c>.
/// </summary>
/// <remarks>
/// All projection logic was lifted verbatim from the prior monolithic <c>SchemaService</c> implementation; behaviour is
/// unchanged. The class lives here (not in <c>SchemaService</c>) so the controller can dispatch through the interface
/// without an open-vs-trace branch — both paths route through the same shape.
/// </remarks>
public sealed class LiveSchemaProvider : IStaticSchemaProvider
{
    private readonly DatabaseEngine _engine;

    public LiveSchemaProvider(DatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    public ComponentSummaryDto[] ListComponents()
    {
        var tables = _engine.GetAllComponentTables().ToArray();
        var summaries = new ComponentSummaryDto[tables.Length];
        for (var i = 0; i < tables.Length; i++)
        {
            summaries[i] = BuildSummary(tables[i]);
        }
        return summaries;
    }

    public ComponentSchemaDto GetComponentSchema(string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        var table = ResolveComponentTable(typeName);
        return BuildSchema(table);
    }

    public ArchetypeInfoDto[] ListArchetypes()
    {
        var result = new List<ArchetypeInfoDto>();
        foreach (var archetype in ArchetypeRegistry.GetAllArchetypes())
        {
            if (archetype._slotToComponentType == null || archetype.ComponentCount == 0)
            {
                continue;
            }

            var entityCount = _engine.GetArchetypeEntityCount(archetype.ArchetypeId);
            var componentTypes = archetype.GetComponentTypes().Select(t => t.FullName ?? t.Name).ToArray();

            int chunkCount = 0;
            int chunkCapacity = 0;
            double occupancyPct = 0;
            string storageMode;

            if (archetype.IsClusterEligible && archetype.ClusterLayout != null)
            {
                storageMode = "cluster";
                chunkCapacity = archetype.ClusterLayout.ClusterSize;
                chunkCount = _engine.GetArchetypeClusterChunkCount(archetype.ArchetypeId);
                if (chunkCount > 0 && chunkCapacity > 0)
                {
                    double total = (double)chunkCount * chunkCapacity;
                    occupancyPct = total > 0 ? (entityCount * 100.0 / total) : 0;
                    if (occupancyPct > 100) occupancyPct = 100;
                }
            }
            else
            {
                storageMode = "legacy";
            }

            result.Add(new ArchetypeInfoDto(
                ArchetypeId: archetype.ArchetypeId.ToString(),
                Name: archetype.Alias ?? archetype.ArchetypeType?.FullName ?? archetype.ArchetypeType?.Name ?? archetype.ArchetypeId.ToString(),
                ComponentTypes: componentTypes,
                EntityCount: entityCount,
                ComponentSize: 0,
                StorageMode: storageMode,
                ChunkCount: chunkCount,
                ChunkCapacity: chunkCapacity,
                OccupancyPct: occupancyPct));
        }
        return result.ToArray();
    }

    public ArchetypeInfoDto[] GetArchetypesForComponent(string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        var table = ResolveComponentTable(typeName);
        var componentType = table.Definition.POCOType;
        if (componentType == null)
        {
            return [];
        }

        var result = new List<ArchetypeInfoDto>();
        foreach (var archetype in ArchetypeRegistry.GetAllArchetypes())
        {
            if (archetype._slotToComponentType == null || archetype.ComponentCount == 0)
            {
                continue;
            }

            var matchingSlot = -1;
            var slotTypes = archetype._slotToComponentType;
            for (var s = 0; s < slotTypes.Length; s++)
            {
                if (slotTypes[s] == componentType)
                {
                    matchingSlot = s;
                    break;
                }
            }
            if (matchingSlot < 0) continue;

            var entityCount = _engine.GetArchetypeEntityCount(archetype.ArchetypeId);
            var componentTypes = archetype.GetComponentTypes().Select(t => t.FullName ?? t.Name).ToArray();

            int componentSize = 0;
            int chunkCount = 0;
            int chunkCapacity = 0;
            double occupancyPct = 0;
            string storageMode;

            if (archetype.IsClusterEligible && archetype.ClusterLayout != null)
            {
                storageMode = "cluster";
                componentSize = archetype.ClusterLayout.ComponentSize(matchingSlot);
                chunkCapacity = archetype.ClusterLayout.ClusterSize;
                chunkCount = _engine.GetArchetypeClusterChunkCount(archetype.ArchetypeId);
                if (chunkCount > 0 && chunkCapacity > 0)
                {
                    double total = (double)chunkCount * chunkCapacity;
                    occupancyPct = total > 0 ? (entityCount * 100.0 / total) : 0;
                    if (occupancyPct > 100) occupancyPct = 100;
                }
            }
            else
            {
                storageMode = "legacy";
                componentSize = table.Definition.ComponentStorageSize;
            }

            result.Add(new ArchetypeInfoDto(
                ArchetypeId: archetype.ArchetypeId.ToString(),
                Name: archetype.Alias ?? archetype.ArchetypeType?.FullName ?? archetype.ArchetypeType?.Name ?? archetype.ArchetypeId.ToString(),
                ComponentTypes: componentTypes,
                EntityCount: entityCount,
                ComponentSize: componentSize,
                StorageMode: storageMode,
                ChunkCount: chunkCount,
                ChunkCapacity: chunkCapacity,
                OccupancyPct: occupancyPct));
        }
        return result.ToArray();
    }

    public IndexInfoDto[] GetIndexesForComponent(string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        var table = ResolveComponentTable(typeName);
        var infos = table.IndexedFieldInfos;
        if (infos == null || infos.Length == 0)
        {
            return [];
        }

        // Defensive against duplicate offsets: the engine's schema-build path forbids two fields at the same offset,
        // but a malformed schema DLL or a future refactor could violate the invariant. Dictionary + TryAdd (first-wins)
        // matches the same guard TraceSchemaProvider uses for component IDs — surfaces a synthetic "@offset" placeholder
        // for any colliding entry rather than failing the entire indexes-for-component request.
        var fieldsByOffset = new Dictionary<int, DBComponentDefinition.Field>();
        foreach (var f0 in table.Definition.FieldsByName.Values)
        {
            fieldsByOffset.TryAdd(f0.OffsetInComponentStorage, f0);
        }

        var result = new IndexInfoDto[infos.Length];
        for (var i = 0; i < infos.Length; i++)
        {
            var info = infos[i];
            var name = fieldsByOffset.TryGetValue(info.OffsetToField, out var f) ? f.Name : $"@{info.OffsetToField}";
            result[i] = new IndexInfoDto(
                FieldName: name,
                FieldOffset: info.OffsetToField,
                FieldSize: info.Size,
                AllowsMultiple: info.AllowMultiple,
                IndexType: "BTree");
        }
        return result;
    }

    public SystemRelationshipsResponseDto GetSystemRelationships(string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        // Validate the component exists before reporting "no systems" — preserves the controller's 404 semantics.
        _ = ResolveComponentTable(typeName);
        // Workbench doesn't host a TyphonRuntime today, so we can't populate Systems[]. The flag tells the client
        // whether the empty list means "no relationships" (true + []) or "feature unavailable" (false + []).
        return new SystemRelationshipsResponseDto(RuntimeHosted: false, Systems: []);
    }

    private static ComponentSummaryDto BuildSummary(ComponentTable table)
    {
        var def = table.Definition;
        return new ComponentSummaryDto(
            TypeName: def.Name,
            FullName: def.POCOType?.FullName ?? def.Name,
            StorageSize: def.ComponentStorageSize,
            FieldCount: def.FieldsByName.Count,
            ArchetypeCount: null,
            EntityCount: table.EstimatedEntityCount,
            IndexCount: table.IndexedFieldInfos?.Length ?? 0,
            StorageMode: def.StorageMode.ToString(),
            HasSpatialIndex: def.SpatialField != null);
    }

    private static ComponentSchemaDto BuildSchema(ComponentTable table)
    {
        var def = table.Definition;
        var fields = def.FieldsByName.Values
            .OrderBy(f => f.OffsetInComponentStorage)
            .Select(f => new FieldDto(
                Name: f.Name,
                TypeName: f.Type.ToString(),
                TypeFullName: f.DotNetType?.FullName ?? f.Type.ToString(),
                Offset: f.OffsetInComponentStorage,
                Size: f.SizeInComponentStorage,
                FieldId: f.FieldId,
                IsIndexed: f.HasIndex,
                IndexAllowsMultiple: f.IndexAllowMultiple,
                IsSpatial: f.HasSpatialIndex))
            .ToArray();

        return new ComponentSchemaDto(
            TypeName: def.Name,
            FullName: def.POCOType?.FullName ?? def.Name,
            StorageSize: def.ComponentStorageSize,
            TotalSize: def.ComponentStorageTotalSize,
            AllowMultiple: false,
            Revision: def.Revision,
            Fields: fields,
            StorageMode: def.StorageMode.ToString());
    }

    private ComponentTable ResolveComponentTable(string typeName)
    {
        foreach (var t in _engine.GetAllComponentTables())
        {
            if (t.Definition.Name == typeName) return t;
        }
        throw new KeyNotFoundException($"Component '{typeName}' is not registered.");
    }
}
