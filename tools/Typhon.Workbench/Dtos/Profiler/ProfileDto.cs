namespace Typhon.Workbench.Dtos.Profiler;

/// <summary>
/// One capture in a database's <c>profilings/</c> directory, as a profiles-list row (#617, design D-5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every field here comes from the trace header</b> (plus the directory entry for <paramref name="SizeBytes"/>). That is the whole point of D-5: opening a
/// database with thirty captures must render thirty rows without building thirty sidecar caches, which is what surfacing this data only through
/// <c>ProfilerMetadataDto</c> would have required. The header was given these fields in #614 precisely so this list is cheap.
/// </para>
/// <para>
/// <paramref name="ProfileId"/> is null until the capture is attached — the list shows every capture on disk, attached or not.
/// </para>
/// </remarks>
/// <param name="FileName">Capture file name, unique within the directory and the key used for pinning.</param>
/// <param name="ProfileId">Its id while attached to the session, or null when it is merely present on disk.</param>
/// <param name="IsActive">True when this is the profile currently driving the session's profiler panels.</param>
/// <param name="CreatedUtcTicks">When the capture was recorded.</param>
/// <param name="DurationTicks">Wall-clock length in <c>Stopwatch</c> ticks; divide by <paramref name="TimestampFrequency"/> for seconds.</param>
/// <param name="TimestampFrequency">Ticks per second for <paramref name="DurationTicks"/>.</param>
/// <param name="TickCount">Runtime ticks the capture spans; 0 when it ran without a scheduler.</param>
/// <param name="TsnMin">Engine transaction number when the capture started.</param>
/// <param name="TsnMax">Engine transaction number when it closed — the right-hand side of the drift measure.</param>
/// <param name="SchemaFingerprint">Digest of the schema the capture ran against, as a decimal string.</param>
/// <param name="DatabaseId">Durable id of the database it was recorded against; empty when the capture predates that field or had no engine.</param>
/// <param name="DatabaseName">That database's bundle name, for display.</param>
/// <param name="MultipleEnginesObserved">True when the capture saw more than one engine — its archetype routing ids were withheld and correlation is name-based.</param>
/// <param name="SizeBytes">Size on disk, read from the directory entry rather than stored in the header.</param>
/// <param name="IsPinned">True when the retention policy pins it — counted in the budget, never evicted.</param>
/// <param name="IsReadable">False when the file could not be parsed; the row still appears, saying so, rather than vanishing.</param>
/// <param name="BelongsToDatabase">
/// Whether the capture was recorded against the database this session has open — the same verdict
/// <see cref="Typhon.Workbench.Services.ProfileCatalog.BelongsToDatabase"/> gates attaching on, decided once here rather than left to the client to re-derive.
/// <para>
/// It is on the row because it governs how the row READS, not only whether it can be opened. <paramref name="TsnMax"/> is only comparable to the session
/// database's transaction number when both come from the same database; subtracting across two TSN spaces produces a confident number that means nothing. A
/// false here tells the UI to withhold the drift figure instead of inventing one.
/// </para>
/// </param>
public record ProfileDto(
    string FileName,
    Guid? ProfileId,
    bool IsActive,
    long CreatedUtcTicks,
    long DurationTicks,
    long TimestampFrequency,
    uint TickCount,
    long TsnMin,
    long TsnMax,
    string SchemaFingerprint,
    string DatabaseId,
    string DatabaseName,
    bool MultipleEnginesObserved,
    long SizeBytes,
    bool IsPinned,
    bool IsReadable,
    bool BelongsToDatabase);

/// <summary>Request body for <c>POST /api/sessions/{id}/profile</c>.</summary>
/// <param name="FileName">
/// A capture in the session database's <c>profilings/</c> directory. A bare file name rather than a path: the directory is derived from the session, so a path
/// would let a caller attach anything on disk and call it this database's profile.
/// </param>
public record AttachProfileRequest(string FileName = "");

/// <summary>Response for <c>GET /api/sessions/{id}/profiles</c>.</summary>
/// <param name="Profiles">Every capture in the database's <c>profilings/</c> directory, newest first.</param>
/// <param name="DatabaseTsn">
/// The database's current transaction number. Paired with each profile's <see cref="ProfileDto.TsnMax"/> this is the drift readout — "this profile is N
/// transactions behind the current database state" — which §4.6 calls out as frequently the answer someone debugging a regression is looking for.
/// </param>
/// <param name="ProfilingsDirectory">Where the captures live, so the UI can say so and offer to open the folder.</param>
public record ProfileListDto(ProfileDto[] Profiles, long DatabaseTsn, string ProfilingsDirectory);
