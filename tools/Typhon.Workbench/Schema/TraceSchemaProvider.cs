using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Typhon.Profiler;
using Typhon.Schema.Definition;
using Typhon.Workbench.Dtos.Schema;

namespace Typhon.Workbench.Schema;

/// <summary>
/// <see cref="IStaticSchemaProvider"/> backed by the v7 static-structure tables embedded in a <c>.typhon-trace</c> file.
/// Used by <c>TraceSession</c> so the Schema Inspector renders trace sessions with the same shape as live <c>OpenSession</c>
/// attaches.
/// </summary>
/// <remarks>
/// <para>
/// <b>Live-only fields are zeroed.</b> Several DTO fields require runtime state the trace doesn't capture
/// (entity counts, cluster chunk counts, occupancy percentages). They're returned as 0 in trace mode — the UI shows
/// the same column structure but with empty values, which the panels render gracefully (no "no data" empty state — the
/// schema metadata IS present, just not the live counts). When the trace recorded an entity-count snapshot in the future,
/// surfacing it here is a one-line change.
/// </para>
/// <para>
/// Records are captured at construction (passed in by <c>TraceSessionRuntime</c> after metadata parse). The provider holds
/// references to the immutable record arrays — no further parsing happens on each call. Lookups by component name use a
/// case-sensitive name index; <see cref="GetComponentSchema"/> matches against both the short name (<c>Position</c>) and
/// the full CLR name (<c>Game.Position</c>) so the controller's path parameter behaviour is identical to live mode.
/// </para>
/// </remarks>
public sealed class TraceSchemaProvider : IStaticSchemaProvider
{
    private readonly IReadOnlyList<ComponentDefinitionRecord> _components;
    private readonly IReadOnlyList<ArchetypeDefinitionRecord> _archetypes;
    private readonly IReadOnlyList<IndexCatalogEntry> _indexes;

    /// <summary>
    /// Pre-built lookup from ComponentTypeId → record. Computed once at construction so per-archetype name resolution
    /// in <see cref="ListArchetypes"/> doesn't rebuild the dictionary on every call (was O(archetypes × components),
    /// now O(archetypes) post-construction). First-wins on duplicate IDs — defensive against malformed traces; the
    /// engine-side <c>ProfilerStaticDataBuilder.ComponentIdMap</c> guarantees uniqueness in normal flows.
    /// </summary>
    private readonly Dictionary<int, ComponentDefinitionRecord> _componentById;

    public TraceSchemaProvider(
        IReadOnlyList<ComponentDefinitionRecord> components,
        IReadOnlyList<ArchetypeDefinitionRecord> archetypes,
        IReadOnlyList<IndexCatalogEntry> indexes)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(archetypes);
        ArgumentNullException.ThrowIfNull(indexes);
        _components = components;
        _archetypes = archetypes;
        _indexes = indexes;
        _componentById = new Dictionary<int, ComponentDefinitionRecord>(components.Count);
        foreach (var c in components) _componentById.TryAdd(c.ComponentTypeId, c);
    }

    public ComponentSummaryDto[] ListComponents()
    {
        var result = new ComponentSummaryDto[_components.Count];
        for (var i = 0; i < _components.Count; i++)
        {
            var c = _components[i];
            result[i] = new ComponentSummaryDto(
                TypeName: ShortName(c.Name),
                FullName: c.Name,
                StorageSize: c.ComponentStorageSize,
                FieldCount: c.Fields?.Length ?? 0,
                ArchetypeCount: CountArchetypesContaining(c.ComponentTypeId),
                EntityCount: 0,
                IndexCount: c.IndicesCount,
                StorageMode: ((StorageMode)c.StorageMode).ToString());
        }
        return result;
    }

    public ComponentSchemaDto GetComponentSchema(string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        var c = ResolveComponent(typeName);
        var fields = (c.Fields ?? [])
            .OrderBy(f => f.Offset)
            .Select(f => new FieldDto(
                Name: f.Name,
                TypeName: FieldTypeNameFor(f.FieldType),
                TypeFullName: FieldTypeNameFor(f.FieldType),
                Offset: f.Offset,
                Size: f.Size,
                FieldId: f.FieldId,
                IsIndexed: (f.Flags & 0x01) != 0,
                IndexAllowsMultiple: (f.Flags & 0x02) != 0))
            .ToArray();

        return new ComponentSchemaDto(
            TypeName: ShortName(c.Name),
            FullName: c.Name,
            StorageSize: c.ComponentStorageSize,
            TotalSize: c.ComponentStorageTotalSize,
            AllowMultiple: c.AllowMultiple,
            Revision: c.Revision,
            Fields: fields,
            StorageMode: ((StorageMode)c.StorageMode).ToString());
    }

    public ArchetypeInfoDto[] ListArchetypes()
    {
        var result = new ArchetypeInfoDto[_archetypes.Count];
        for (var i = 0; i < _archetypes.Count; i++)
        {
            result[i] = ProjectArchetype(_archetypes[i], focusedSlot: -1, focusedComponentSize: 0);
        }
        return result;
    }

    public ArchetypeInfoDto[] GetArchetypesForComponent(string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        var c = ResolveComponent(typeName);
        var result = new List<ArchetypeInfoDto>();
        foreach (var a in _archetypes)
        {
            var slot = -1;
            for (var s = 0; s < (a.ComponentTypeIds?.Length ?? 0); s++)
            {
                if (a.ComponentTypeIds[s] == c.ComponentTypeId)
                {
                    slot = s;
                    break;
                }
            }
            if (slot < 0) continue;

            // Component size in this archetype: the trace record doesn't carry per-slot sizes — the cluster info has
            // ClusterStride which is total-bytes-per-cluster, not per-component. We surface the component's authored
            // ComponentStorageSize as a lower-bound; cluster overhead would need an engine-side helper to compute exactly.
            result.Add(ProjectArchetype(a, slot, c.ComponentStorageSize));
        }
        return result.ToArray();
    }

    public IndexInfoDto[] GetIndexesForComponent(string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        var c = ResolveComponent(typeName);
        // Defensive against duplicate field IDs (engine schema invariants forbid them, but a malformed trace
        // could carry them — same first-wins guard as the component-id case in ResolveComponentNames).
        var fieldsById = new Dictionary<int, FieldDefinitionRecord>();
        foreach (var f0 in c.Fields ?? []) fieldsById.TryAdd(f0.FieldId, f0);

        var result = new List<IndexInfoDto>();
        foreach (var entry in _indexes)
        {
            if (entry.ComponentTypeId != c.ComponentTypeId) continue;
            var fieldName = fieldsById.TryGetValue(entry.FieldId, out var f) ? f.Name : $"#{entry.FieldId}";
            var fieldOffset = f?.Offset ?? -1;
            var fieldSize = f?.Size ?? 0;
            result.Add(new IndexInfoDto(
                FieldName: fieldName,
                FieldOffset: fieldOffset,
                FieldSize: fieldSize,
                AllowsMultiple: entry.AllowMultiple,
                IndexType: entry.IsSpatial ? "Spatial" : "BTree"));
        }
        return result.ToArray();
    }

    public SystemRelationshipsResponseDto GetSystemRelationships(string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        _ = ResolveComponent(typeName); // 404 if unknown
        // Trace mode doesn't host a runtime — same envelope shape as live mode (which also returns RuntimeHosted: false today).
        return new SystemRelationshipsResponseDto(RuntimeHosted: false, Systems: []);
    }

    private ArchetypeInfoDto ProjectArchetype(ArchetypeDefinitionRecord a, int focusedSlot, int focusedComponentSize)
    {
        var componentTypes = ResolveComponentNames(a.ComponentTypeIds ?? []);
        var isCluster = (a.Flags & 0x01) != 0 && a.ClusterInfo != null;
        var storageMode = isCluster ? "cluster" : "legacy";
        var chunkCapacity = isCluster ? a.ClusterInfo.ClusterSize : 0;
        var componentSize = focusedSlot >= 0 ? focusedComponentSize : 0;

        return new ArchetypeInfoDto(
            ArchetypeId: a.ArchetypeId.ToString(CultureInfo.InvariantCulture),
            ComponentTypes: componentTypes,
            EntityCount: 0,
            ComponentSize: componentSize,
            StorageMode: storageMode,
            ChunkCount: 0,
            ChunkCapacity: chunkCapacity,
            OccupancyPct: 0);
    }

    private string[] ResolveComponentNames(int[] componentTypeIds)
    {
        if (componentTypeIds.Length == 0) return [];
        // Uses the constructor-cached `_componentById` map — see its doc comment for the rationale (was a
        // hot-path perf footgun in the previous implementation). Falls back to "#id" for unknown IDs so
        // archetypes referencing a missing component still render gracefully rather than failing the request.
        var names = new string[componentTypeIds.Length];
        for (var i = 0; i < componentTypeIds.Length; i++)
        {
            names[i] = _componentById.TryGetValue(componentTypeIds[i], out var c) ? c.Name : $"#{componentTypeIds[i]}";
        }
        return names;
    }

    private int CountArchetypesContaining(int componentTypeId)
    {
        var n = 0;
        foreach (var a in _archetypes)
        {
            var ids = a.ComponentTypeIds;
            if (ids == null) continue;
            for (var i = 0; i < ids.Length; i++)
            {
                if (ids[i] == componentTypeId) { n++; break; }
            }
        }
        return n;
    }

    private ComponentDefinitionRecord ResolveComponent(string typeName)
    {
        foreach (var c in _components)
        {
            if (c.Name == typeName) return c;
            // Match short name too — engine's DBComponentDefinition.Name is typically the short class name; the trace
            // serialises FullName. Accept either so the controller's path parameter behaves the same as live mode.
            if (ShortName(c.Name) == typeName) return c;
        }
        throw new KeyNotFoundException($"Component '{typeName}' is not registered.");
    }

    private static string ShortName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return fullName ?? string.Empty;
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 ? fullName[(lastDot + 1)..] : fullName;
    }

    /// <summary>
    /// Map the wire byte for <c>Typhon.Schema.Definition.FieldType</c> to the same friendly string the live provider
    /// produces (<c>FieldType.Int</c> → <c>"Int"</c>, <c>FieldType.String64</c> → <c>"String64"</c>, etc.). Falls back
    /// to the raw byte when the value is unknown so trace files from a future build with new field-type enum values
    /// still render something useful.
    /// </summary>
    private static string FieldTypeNameFor(byte fieldTypeByte)
    {
        var enumType = typeof(Typhon.Schema.Definition.FieldType);
        if (System.Enum.IsDefined(enumType, (int)fieldTypeByte))
        {
            return ((Typhon.Schema.Definition.FieldType)fieldTypeByte).ToString();
        }
        return $"FieldType#{fieldTypeByte}";
    }
}
