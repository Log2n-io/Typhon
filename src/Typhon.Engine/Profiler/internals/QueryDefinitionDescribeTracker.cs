using System.Collections.Concurrent;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side dedup table for <see cref="Typhon.Profiler.TraceEventKind.QueryDefinitionDescribe"/> events.
/// Ensures each distinct (kind, localId) pair emits its descriptor at most once per profiling session.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design tradeoff.</b> The original design doc (§4.6) recommended consumer-side dedup. We moved to producer-side for two reasons: (1) the consumer thread
/// has no access to the View/EcsQuery instance state — only the wire bytes it sees — so it cannot synthesize a missing descriptor; (2) "capture-on-emit"
/// requires the producer to know which identities it has already described, which is exactly this map. Producer-side dedup avoids a redundant identity
/// payload on every <see cref="Typhon.Profiler.TraceEventKind.QueryPlan"/> event.
/// </para>
/// <para>
/// <b>Key encoding.</b> The (kind, localId) pair is packed into a single <c>ulong</c>: high byte = kind, low 32 bits = localId.
/// Concurrent <see cref="TryMarkAndCheck"/> calls race via <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> — the first call returns <c>true</c> (emit),
/// subsequent calls return <c>false</c> (skip).
/// </para>
/// <para>
/// <b>Lifecycle.</b> <see cref="Reset"/> is called at <c>TyphonProfiler.Start</c>. The table grows by one entry per distinct View/EcsQuery identity over the
/// session — typically ≤ 100 entries.
/// </para>
/// </remarks>
internal static class QueryDefinitionDescribeTracker
{
    private static readonly ConcurrentDictionary<ulong, byte> Described = new();

    /// <summary>
    /// Try to mark a (<paramref name="kind"/>, <paramref name="localId"/>) identity as "described". Returns <c>true</c> on first observation (caller should
    /// emit the descriptor), <c>false</c> on subsequent observations (caller should skip emission).
    /// </summary>
    public static bool TryMarkAndCheck(byte kind, uint localId)
    {
        var key = ((ulong)kind << 32) | localId;
        return Described.TryAdd(key, 0);
    }

    /// <summary>
    /// Undo a prior <see cref="TryMarkAndCheck"/> mark. Called when emission fails downstream (e.g., the trace ring was saturated and bytes never reached the
    /// chunk) so a subsequent caller can retry. Without this rollback a single failed reservation would permanently silence the descriptor for that identity in the session.
    /// </summary>
    public static void Unmark(byte kind, uint localId)
    {
        var key = ((ulong)kind << 32) | localId;
        Described.TryRemove(key, out _);
    }

    /// <summary>Reset the tracker. Called at <c>TyphonProfiler.Start</c>.</summary>
    public static void Reset() => Described.Clear();
}
