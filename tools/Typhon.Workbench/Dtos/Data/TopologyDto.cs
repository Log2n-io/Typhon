using System.Collections.Generic;
using Typhon.Workbench.Dtos.Profiler;

namespace Typhon.Workbench.Dtos.Data;

/// <summary>
/// Topology snapshot — system DAG, archetypes, component types, and phase order. Static for the lifetime of a session;
/// fetched once per attach. RFC 07 access declarations live on each <see cref="SystemDefinitionDto"/>.
/// </summary>
/// <param name="Phases">User-defined phase order from <c>RuntimeOptions.Phases</c> (RFC 07 §Q3). Empty for sessions
/// without phase declarations or for legacy v5 traces.</param>
/// <param name="ComponentFamilies">Workbench Data Flow module L2 grouping. Maps every component name to its family
/// (resolved by <c>[ComponentFamily]</c> attribute first, then by name heuristic). Trace sessions use heuristic-only
/// since the attribute is gone after recording.</param>
public record TopologyDto(
    SystemDefinitionDto[] Systems,
    ArchetypeDto[] Archetypes,
    ComponentTypeDto[] ComponentTypes,
    string[] Phases,
    ComponentFamilyMapDto ComponentFamilies);

/// <summary>
/// Component-family mapping surfaced through <see cref="TopologyDto.ComponentFamilies"/>. Drives the L2 ("Component-family")
/// granularity of the Data Flow Timeline and the Access Matrix's family-rollup mode. <see cref="ComponentToFamily"/> is
/// the source of truth (every known component name maps to exactly one family); <see cref="FamilyOrder"/> gives the
/// canonical render order so UI rows are stable across sessions.
/// </summary>
public record ComponentFamilyMapDto(
    Dictionary<string, string> ComponentToFamily,
    string[] FamilyOrder);
