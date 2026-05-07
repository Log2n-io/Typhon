using Typhon.Workbench.Dtos.Profiler;

namespace Typhon.Workbench.Dtos.Data;

/// <summary>
/// Topology snapshot — system DAG, archetypes, component types, and phase order. Static for the lifetime of a session;
/// fetched once per attach. RFC 07 access declarations live on each <see cref="SystemDefinitionDto"/>.
/// </summary>
/// <param name="Phases">User-defined phase order from <c>RuntimeOptions.Phases</c> (RFC 07 §Q3). Empty for sessions
/// without phase declarations or for legacy v5 traces.</param>
public record TopologyDto(
    SystemDefinitionDto[] Systems,
    ArchetypeDto[] Archetypes,
    ComponentTypeDto[] ComponentTypes,
    string[] Phases);
