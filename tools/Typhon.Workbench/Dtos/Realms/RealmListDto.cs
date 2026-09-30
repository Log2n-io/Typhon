namespace Typhon.Workbench.Dtos.Realms;

/// <summary>
/// Every realm this session can see, and an honest statement of which columns it could not fill.
/// </summary>
/// <remarks>
/// <b>The coverage fields exist so a panel can say "not known here" instead of rendering a zero.</b> An Open session that merely opened a database file has the
/// realm catalog and nothing that is ticking, so run state, divisor, session counts and replication kind are all absent — not zero. A panel handed zeros would
/// show every realm as Dormant with no sessions, which is a plausible-looking lie about a database that is not running at all. This is the same call WB-02 made
/// by putting <c>reportedSessions</c> on its own record rather than letting a capped list read as a total.
/// </remarks>
/// <param name="MaxRealms">The realm capacity this database was opened with: valid ids are <c>[0, MaxRealms)</c>. One means a single-world database.</param>
/// <param name="Realms">One row per realm, ascending by id. Realm 0 is always first when present.</param>
/// <param name="LiveState">Whether <see cref="RealmDto.RunState"/>, <see cref="RealmDto.Divisor"/> and <see cref="RealmDto.Sessions"/> are answerable at all.</param>
/// <param name="LiveStateReason">Why the live half is absent, for a panel to show verbatim. Empty when <paramref name="LiveState"/> is true.</param>
/// <param name="Catalogued">How many rows came from the persisted catalog, as opposed to the live realm table or realm 0's own grid record.</param>
public sealed record RealmListDto(
    int MaxRealms,
    RealmDto[] Realms,
    bool LiveState,
    string LiveStateReason,
    int Catalogued);
