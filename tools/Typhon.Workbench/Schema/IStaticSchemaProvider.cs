using Typhon.Workbench.Dtos.Schema;

namespace Typhon.Workbench.Schema;

/// <summary>
/// Polymorphic schema-data source for the Workbench Schema Inspector. Each session kind that can serve schema data
/// implements this interface, and <see cref="SchemaService"/> dispatches through it without caring whether the data
/// came from a live engine or a parsed trace file.
/// </summary>
/// <remarks>
/// <para>
/// Two implementations ship today:
/// <list type="bullet">
/// <item><see cref="LiveSchemaProvider"/> — wraps a live <c>DatabaseEngine</c>; used by <c>OpenSession</c>.</item>
/// <item><see cref="TraceSchemaProvider"/> — projects the v7 static-structure tables parsed from a <c>.typhon-trace</c>
/// file; used by <c>TraceSession</c>.</item>
/// </list>
/// Sessions that don't have schema data available (e.g., live <c>AttachSession</c> — the engine doesn't currently
/// push schema over the live attach socket) return <c>null</c> from their <c>StaticSchemaProvider</c> property and
/// the controller maps that to a 404, which the UI surfaces as a "schema unavailable for this session type" empty state.
/// </para>
/// <para>
/// Methods that resolve a single component throw <see cref="System.Collections.Generic.KeyNotFoundException"/> when
/// the requested component name doesn't exist in this session's schema; the controller maps it to 404. Empty results
/// (e.g., a component with no indexes) are returned as empty arrays, never null.
/// </para>
/// </remarks>
public interface IStaticSchemaProvider
{
    /// <summary>Triage-friendly list of every registered component in this session's schema.</summary>
    ComponentSummaryDto[] ListComponents();

    /// <summary>Full byte-layout schema for a single component type.</summary>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">No component matches <paramref name="typeName"/>.</exception>
    ComponentSchemaDto GetComponentSchema(string typeName);

    /// <summary>Every archetype registered in this session's schema, without a component filter.</summary>
    ArchetypeInfoDto[] ListArchetypes();

    /// <summary>All archetypes that contain the given component type.</summary>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">No component matches <paramref name="typeName"/>.</exception>
    ArchetypeInfoDto[] GetArchetypesForComponent(string typeName);

    /// <summary>Indexes covering fields of the given component type.</summary>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">No component matches <paramref name="typeName"/>.</exception>
    IndexInfoDto[] GetIndexesForComponent(string typeName);

    /// <summary>
    /// Systems that read or reactively trigger on the given component type. Today only <see cref="LiveSchemaProvider"/>
    /// can populate this (requires a hosted runtime); <see cref="TraceSchemaProvider"/> returns
    /// <c>(RuntimeHosted: false, Systems: [])</c>.
    /// </summary>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">No component matches <paramref name="typeName"/>.</exception>
    SystemRelationshipsResponseDto GetSystemRelationships(string typeName);
}
