namespace Typhon.Workbench.Dtos.Realms;

/// <summary>
/// One realm, as the current session can know it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The nullable fields are the ones a database FILE cannot answer, and they are nullable rather than zero for that reason.</b> The persisted realm catalog
/// (<c>RealmR1</c>, Realms D-1) is <i>identity only</i> — the fields that decide which cell a position files into — because policy and replication "decide when
/// things run, never where an entity is filed, so they are the application's to supply at each open". A Workbench that merely opened the file is not that
/// application, so it has bounds and generations and no divisors. Reporting 0 or "Active" there would be inventing an answer; <see langword="null"/> says the
/// session does not know, and <see cref="RealmListDto.LiveStateReason"/> says why.
/// </para>
/// <para>
/// <b><see cref="Registered"/> and <see cref="Lifecycle"/> are different questions.</b> Lifecycle is what the catalog row says the realm is across opens
/// (live, closing, retired); Registered is whether this engine has it in its realm table right now. A realm can be catalogued and unregistered — that is
/// exactly a database the Workbench opened without the application's registrations.
/// </para>
/// </remarks>
/// <param name="Id">The realm id. Realm 0 is the primary realm and is reported from the single-world grid record, not from the catalog it is deliberately not in.</param>
/// <param name="Generation">Which incarnation of the id this is: ids are reused across opens, so identity is the pair <c>(id, generation)</c> (12-realms § 1.1).</param>
/// <param name="Source">Where this row came from: <c>grid</c> (realm 0's single-world record), <c>catalog</c> (a persisted row) or <c>registered</c> (the live realm table only).</param>
/// <param name="Registered">Whether the engine holds this realm in its realm table right now.</param>
/// <param name="Lifecycle">The catalog's own lifecycle word: <c>live</c>, <c>closing</c>, <c>retired</c>, or <c>unknown</c> for a realm with no catalog row.</param>
/// <param name="Grid">The spatial identity: bounds, cell size, dimensionality.</param>
/// <param name="RunState">The policy state this tick — <c>Active</c>, <c>Simulated</c>, <c>Dormant</c>, <c>Closing</c>, <c>Divided</c>. Null when nothing is ticking.</param>
/// <param name="Divisor">How often an unobserved realm runs, in ticks. Null when the session cannot know the policy.</param>
/// <param name="Sessions">Replication sessions served out of this realm. Null when the session cannot know, 0 when it can and there are none.</param>
/// <param name="Kind">The replication kind the application declared (<c>planet</c>, <c>interior</c>, …), which picks its sessions' profile variants. Empty when unknown — it is not persisted.</param>
public sealed record RealmDto(
    int Id,
    int Generation,
    string Source,
    bool Registered,
    string Lifecycle,
    RealmGridDto Grid,
    string RunState,
    int? Divisor,
    int? Sessions,
    string Kind);
