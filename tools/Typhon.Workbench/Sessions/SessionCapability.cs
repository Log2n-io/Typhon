namespace Typhon.Workbench.Sessions;

/// <summary>
/// What a session can <i>do</i>, as opposed to what it <i>is</i> (#617, design D-10).
/// </summary>
/// <remarks>
/// <para>
/// Panels used to decide their own visibility from <see cref="SessionKind"/> — <c>kind === 'trace' || kind === 'attach'</c>. That stopped working the moment a
/// profile could attach to an Open session: the session is still an Open session, and it can now profile. No kind enum can express that, because the
/// capability is acquired and released during the session's life while its kind never changes.
/// </para>
/// <para>
/// Deliberately just two names. The design calls out exactly one capability; adding a richer taxonomy before there is a second consumer would be inventing a
/// vocabulary nobody speaks yet.
/// </para>
/// </remarks>
public static class SessionCapability
{
    /// <summary>
    /// The session can serve <c>/api/sessions/{id}/profiler/*</c> — it has a trace runtime, either because it <i>is</i> a capture (Trace), because it is
    /// streaming one (Attach), or because a capture is attached to it as a profile (Open).
    /// </summary>
    public const string Profiler = "profiler";

    /// <summary>The session has a live database behind it — data browser, storage map, query console. Open sessions only.</summary>
    public const string Database = "database";

    /// <summary>
    /// The session can serve <c>/api/sessions/{id}/schema/*</c> — component layouts, archetype composition, the index catalog.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="Database"/> in #WB-01, when schema stopped implying a reachable database. An Open session reads it from its live engine;
    /// an Attach session reads it from the static-structure tables in the Init frame, and so has schema over a database it cannot browse (blocker B1).
    /// Deriving the Schema Inspector's availability from <see cref="Database"/> would have hidden it in exactly the mode the engine was changed to serve.
    /// </remarks>
    public const string Schema = "schema";
}
