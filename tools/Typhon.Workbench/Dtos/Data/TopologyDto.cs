using System.Collections.Generic;
using Typhon.Workbench.Dtos.Profiler;

namespace Typhon.Workbench.Dtos.Data;

/// <summary>
/// Topology snapshot — system DAG, archetypes, component types, and phase order. Static for the lifetime of a session;
/// fetched once per attach. RFC 07 access declarations live on each <see cref="SystemDefinitionDto"/>.
/// </summary>
/// <param name="Phases">Transitional flat phase list (#354 W4) — the distinct DAG-local phase names in first-seen
/// order. Phases are now DAG-scoped; this collapses duplicates across DAGs and is retained only for panels not yet
/// migrated to <see cref="Tracks"/>. New consumers should read the DAG-local <see cref="DagDto.Phases"/> instead.</param>
/// <param name="Tracks">Runtime partitioning hierarchy (#354) — <c>tracks[] → dags[] → phases[]</c>. Tracks execute in
/// <see cref="TrackDto.OrderIndex"/> order; engine-internal tracks carry the <c>engine</c> tag. A system is placed via
/// its <see cref="SystemDefinitionDto.DagId"/>. Empty for traces with no track data.</param>
/// <param name="ComponentFamilies">Workbench Data Flow module L2 grouping. Maps every component name to its family
/// (resolved by <c>[ComponentFamily]</c> attribute first, then by name heuristic). Trace sessions use heuristic-only
/// since the attribute is gone after recording.</param>
public record TopologyDto(
    SystemDefinitionDto[] Systems,
    ArchetypeDto[] Archetypes,
    ComponentTypeDto[] ComponentTypes,
    string[] Phases,
    TrackDto[] Tracks,
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
