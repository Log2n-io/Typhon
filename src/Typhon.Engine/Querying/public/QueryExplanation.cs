using System.Collections.Generic;
using System.Text;

namespace Typhon.Engine;

/// <summary>How a query reached one archetype's rows, as recorded by <see cref="EcsQuery{TArchetype}.Explain"/>.</summary>
/// <remarks>A diagnostic vocabulary, not a contract on the planner: values may be added or split before 1.0.</remarks>
public enum QueryAccessPath : byte
{
    /// <summary>
    /// The archetype was not scanned: it cannot match (it lacks the predicate's component, or a component an <c>Enabled</c>/<c>Disabled</c> clause names).
    /// </summary>
    Skipped,
    /// <summary>The query's archetype mask is empty: nothing was read.</summary>
    Empty,
    /// <summary>
    /// B+Tree seek on one key of an indexed field; the other predicates are evaluated on the clusters it reached, and the seeked field's own only when
    /// the range cannot enforce them alone (see <see cref="ArchetypeAccess.EvaluatedPerEntity"/>).
    /// </summary>
    IndexSeek,
    /// <summary>B+Tree range scan of an indexed field; predicates evaluated on the clusters it reached as for <see cref="IndexSeek"/>.</summary>
    IndexRange,
    /// <summary>Every active cluster, pruned by the index fields' zone maps, every predicate evaluated per slot. No tree is read.</summary>
    ClusterScan,
    /// <summary>Every entity of the archetype, walked through its entity map; a <c>Where(Func)</c> predicate is then evaluated per entity.</summary>
    BroadScan,
    /// <summary><c>Count()</c> answered from the clusters' occupancy words (a popcount per cluster), no entity visited.</summary>
    OccupancyCount,
    /// <summary>The spatial index produced the candidates; other predicates were evaluated per candidate.</summary>
    Spatial
}

/// <summary>Which terminal <see cref="EcsQuery{TArchetype}.Explain"/> runs: the access path can differ between them.</summary>
public enum ExplainTerminal : byte
{
    /// <summary>Explain <see cref="EcsQuery{TArchetype}.Execute"/>.</summary>
    Execute,
    /// <summary>Explain <see cref="EcsQuery{TArchetype}.Count"/>, which alone can take <see cref="QueryAccessPath.OccupancyCount"/>.</summary>
    Count
}

/// <summary>The access path one archetype took during an explained run.</summary>
/// <param name="Archetype">The archetype's name.</param>
/// <param name="Path">The path taken.</param>
/// <param name="Index">The index read, as <c>Component.Field</c>, or <c>null</c> when no tree was read.</param>
/// <param name="PushedDown">Predicates the index range enforced before any row was visited (the narrowing).</param>
/// <param name="EvaluatedPerEntity">Predicates evaluated per visited row or slot.</param>
/// <param name="EstimatedRows">
/// The planner's estimate for its most selective predicate (not necessarily the indexed field shown), or -1 when it had no statistics (several archetypes
/// match) or built no plan. One estimate per query, not per archetype.
/// </param>
/// <param name="ActualRows">
/// Rows this archetype contributed before any <c>Where(Func)</c> post-filter. A cluster or index scan counts the transaction's own pending destroys (they
/// are removed from the result afterwards); a broad scan has already left them out.
/// </param>
/// <param name="Note">Why the archetype was skipped, or another fact worth reading; <c>null</c> otherwise.</param>
public sealed record ArchetypeAccess(
    string Archetype,
    QueryAccessPath Path,
    string Index,
    IReadOnlyList<string> PushedDown,
    IReadOnlyList<string> EvaluatedPerEntity,
    long EstimatedRows,
    int ActualRows,
    string Note)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Archetype).Append(": ").Append(Path);
        if (Index != null)
        {
            sb.Append(" on ").Append(Index);
        }
        if (PushedDown.Count > 0)
        {
            sb.Append(" | pushed down: ").AppendJoin(", ", PushedDown);
        }
        if (EvaluatedPerEntity.Count > 0)
        {
            sb.Append(" | per entity: ").AppendJoin(", ", EvaluatedPerEntity);
        }
        if (EstimatedRows >= 0)
        {
            sb.Append(" | est ").Append(EstimatedRows);
        }
        sb.Append(" | rows ").Append(ActualRows);
        if (Note != null)
        {
            sb.Append(" (").Append(Note).Append(')');
        }
        return sb.ToString();
    }
}

/// <summary>
/// What an <see cref="EcsQuery{TArchetype}"/> did when it ran: the path each archetype took, the predicates pushed into an index, and the rows each produced.
/// </summary>
/// <remarks>
/// Recorded by the executing code itself while the terminal runs, so it reports the engine's decision rather than a re-derivation of it. A diagnostic
/// surface: fields may change before 1.0.
/// </remarks>
/// <param name="Terminal">The terminal that ran.</param>
/// <param name="QueryPath">
/// The query-level path when one applies to the whole query (<see cref="QueryAccessPath.Empty"/>, <see cref="QueryAccessPath.Spatial"/>); null when each
/// archetype chose its own, listed in <paramref name="Archetypes"/>.
/// </param>
/// <param name="Archetypes">
/// One entry per archetype the scan visited, in the order it visited them; empty when <paramref name="QueryPath"/> answered for the whole query.
/// </param>
/// <param name="PendingSpawnRows">Rows contributed by the transaction's own uncommitted spawns, which no index or map holds yet.</param>
/// <param name="WhereFilterRemoved">Rows a <c>Where(Func)</c> post-filter removed, or -1 when the query has none.</param>
/// <param name="ResultCount">What the terminal returned (the set's size, or the count).</param>
public sealed record QueryExplanation(
    ExplainTerminal Terminal,
    QueryAccessPath? QueryPath,
    IReadOnlyList<ArchetypeAccess> Archetypes,
    int PendingSpawnRows,
    int WhereFilterRemoved,
    int ResultCount)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Terminal).Append("() -> ").Append(ResultCount);
        if (QueryPath != null)
        {
            sb.Append(" [").Append(QueryPath).Append(']');
        }
        sb.AppendLine();
        foreach (var a in Archetypes)
        {
            sb.Append("  ").Append(a).AppendLine();
        }
        if (PendingSpawnRows > 0)
        {
            sb.Append("  pending spawns: ").Append(PendingSpawnRows).AppendLine();
        }
        if (WhereFilterRemoved >= 0)
        {
            sb.Append("  Where(Func) removed: ").Append(WhereFilterRemoved).AppendLine();
        }
        return sb.ToString();
    }
}

/// <summary>Collects <see cref="ArchetypeAccess"/> entries while an explained terminal runs. Absent (null) on every ordinary run.</summary>
internal sealed class QueryExplainRecorder
{
    public readonly List<ArchetypeAccess> Archetypes = [];

    /// <summary>The terminal's result set, when it has one, so a scan can report how many rows each archetype added.</summary>
    public HashSet<EntityId> ResultSet;

    public QueryAccessPath? QueryPath;
    public int PendingSpawnRows;
    public int WhereFilterRemoved = -1;

    public int ResultCountNow => ResultSet?.Count ?? 0;

    public void Add(string archetype, QueryAccessPath path, int actualRows, string note = null, string index = null, IReadOnlyList<string> pushedDown = null,
        IReadOnlyList<string> evaluated = null, long estimated = -1) =>
        Archetypes.Add(new ArchetypeAccess(archetype, path, index, pushedDown ?? [], evaluated ?? [], estimated, actualRows, note));
}
