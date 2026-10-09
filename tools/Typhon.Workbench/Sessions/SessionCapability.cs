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

    /// <summary>
    /// The session can serve <c>/api/sessions/{id}/realms/*</c> — the realm catalog, and whatever any one realm's state can be known from here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own capability for the reason <see cref="Schema"/> is, one turn further out.</b> An <c>Open</c> session has realms because it has a
    /// <see cref="Database"/>: the catalog rows are in the file. An <c>Attach</c> session has realm <i>state</i> because it has a <see cref="Profiler"/> —
    /// spatial trace records carry a row per runnable realm — over a database it cannot browse at all. Deriving this from <c>Database</c> would hide the realm
    /// board in live attach, which is the sysops case; deriving it from <c>Profiler</c> would hide the realm navigator in a plain open, which is the developer
    /// case. Neither existing capability describes it, so it is one of its own and either route grants it.
    /// </para>
    /// <para>
    /// <b>Granted on the engine's realm CAPACITY, not on how many realms happen to exist.</b> A database configured for one realm never gets it — a single
    /// world has no realm to navigate, which is the same call <c>RuntimeStatsSnapshot.Realms</c> makes by being empty there. A database configured for
    /// thousands gets it even while only realm 0 is registered, because realms register and unregister at run time (a dungeon opens, a party disperses) and a
    /// capability derived from the live count would take the whole realm UI away and put it back as that happened.
    /// </para>
    /// </remarks>
    public const string Realms = "realms";
}
