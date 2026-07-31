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

    /// <summary>The session has a live database behind it — schema, data browser, storage map. Open sessions only.</summary>
    public const string Database = "database";
}
