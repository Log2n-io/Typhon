using Typhon.Workbench.Dtos.Profiler;

namespace Typhon.Workbench.Dtos.Data;

/// <summary>
/// Result of a topology-query endpoint (<c>/queries/who-writes/{component}</c>, <c>/queries/who-reads/{component}</c>).
/// Echoes the queried name + the matching systems so callers don't need to remember which key they searched for.
/// </summary>
/// <param name="Query">The component (or future event/resource) name the caller asked about.</param>
/// <param name="Systems">Zero or more systems whose RFC 07 declarations match the query.</param>
public record SystemListDto(string Query, SystemDefinitionDto[] Systems);
