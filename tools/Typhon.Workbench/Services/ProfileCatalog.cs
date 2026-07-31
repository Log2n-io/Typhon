using Typhon.Engine;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Services;

/// <summary>
/// Reads a database's profiling captures for the profiles list (#617, design D-5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Headers only.</b> Every field a row needs lives in the trace header — about 200 bytes per file — so listing thirty captures is thirty small reads. That
/// was the entire reason #614 put created-time, duration, tick count, TSN range and schema fingerprint there: the alternative, projecting them from
/// <c>ProfilerMetadataDto</c>, would have meant building thirty sidecar caches to populate a list the user clicks once.
/// </para>
/// <para>
/// <b>An unreadable capture still gets a row.</b> A truncated or foreign file is reported as unreadable rather than dropped, because a capture that silently
/// disappears from the list looks like a retention bug and sends the user hunting for a file that is sitting right there.
/// </para>
/// </remarks>
public static class ProfileCatalog
{
    /// <summary>Lists every capture in the session database's <c>profilings/</c> directory, newest first.</summary>
    /// <param name="session">The Open session whose database is being listed.</param>
    public static ProfileListDto List(OpenSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var profilings = TraceLocation.ProfilingsDirectoryOf(session.FilePath);
        var databaseTsn = session.Engine.Engine?.CurrentTsn ?? 0;
        var databaseId = session.Engine.Engine?.DatabaseId ?? Guid.Empty;

        if (!Directory.Exists(profilings))
        {
            // A database nobody has profiled yet. An empty list is the honest answer; the directory is created by the first capture.
            return new ProfileListDto([], databaseTsn, profilings);
        }

        // Attached captures are keyed by file name so a row can report its own profile id without a second lookup pass.
        var attachedByName = new Dictionary<string, (Guid Id, bool IsActive)>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in session.Profiles)
        {
            var name = Path.GetFileName(kv.Value.FilePath);
            if (name != null)
            {
                attachedByName[name] = (kv.Key, kv.Key == session.ActiveProfileId);
            }
        }

        var policy = RetentionPolicy.Read(profilings, out _);
        var rows = new List<ProfileDto>();

        foreach (var path in Directory.EnumerateFiles(profilings, "*" + TraceLocation.TraceExtension))
        {
            if (!TraceLocation.IsCapture(path))
            {
                continue;
            }

            var info = new FileInfo(path);
            attachedByName.TryGetValue(info.Name, out var attached);
            rows.Add(ReadRow(path, info, attached.Id == Guid.Empty ? null : attached.Id, attached.IsActive, policy.IsPinned(info.Name), databaseId));
        }

        rows.Sort(static (a, b) => b.CreatedUtcTicks.CompareTo(a.CreatedUtcTicks));
        return new ProfileListDto([.. rows], databaseTsn, profilings);
    }

    /// <summary>Reads one capture's header into a row, degrading to an unreadable row rather than throwing.</summary>
    /// <param name="databaseId">
    /// The session database's durable id, so the row can carry the same provenance verdict <see cref="BelongsToDatabase"/> gates attaching on. Decided from the
    /// header already being read here rather than by re-opening the file, so the list stays one small read per capture.
    /// </param>
    private static ProfileDto ReadRow(string path, FileInfo info, Guid? profileId, bool isActive, bool isPinned, Guid databaseId)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new TraceFileReader(stream);
            var h = reader.ReadHeader();

            return new ProfileDto(
                FileName: info.Name,
                ProfileId: profileId,
                IsActive: isActive,
                CreatedUtcTicks: h.CreatedUtcTicks,
                DurationTicks: h.DurationTicks,
                TimestampFrequency: h.TimestampFrequency,
                TickCount: h.TickCount,
                TsnMin: h.TsnMin,
                TsnMax: h.TsnMax,
                SchemaFingerprint: h.SchemaFingerprint.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DatabaseId: h.DatabaseId == Guid.Empty ? string.Empty : h.DatabaseId.ToString("D"),
                DatabaseName: h.GetDatabaseName(),
                MultipleEnginesObserved: h.MultipleEnginesObserved,
                SizeBytes: info.Length,
                IsPinned: isPinned,
                IsReadable: true,
                // Same rule as BelongsToDatabase, including the empty-id allowance: a capture predating #614 cannot answer the question either way, and calling
                // it foreign would strip the drift readout off every pre-#614 capture to enforce a check it was never able to satisfy.
                BelongsToDatabase: h.DatabaseId == Guid.Empty || h.DatabaseId == databaseId);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Truncated mid-write, written by an older build, or not a capture at all. The row survives so the user can see the file exists and act on it.
            return new ProfileDto(
                FileName: info.Name, ProfileId: profileId, IsActive: isActive,
                CreatedUtcTicks: info.LastWriteTimeUtc.Ticks, DurationTicks: 0, TimestampFrequency: 0, TickCount: 0,
                TsnMin: 0, TsnMax: 0, SchemaFingerprint: "0", DatabaseId: string.Empty, DatabaseName: string.Empty,
                MultipleEnginesObserved: false, SizeBytes: info.Length, IsPinned: isPinned, IsReadable: false,
                // Unknowable for a file that would not parse. False keeps the drift figure off a row whose TsnMax is 0 anyway, and the row already says
                // "unreadable" — the honest reading is "cannot vouch for this", not "belongs".
                BelongsToDatabase: false);
        }
    }

    /// <summary>
    /// Checks that a capture was recorded against the database it is being attached to.
    /// </summary>
    /// <remarks>
    /// D-1 is explicit that <b>co-location is not provenance</b>: a capture sitting in this bundle proves it was written here, not that it belongs to what is
    /// here now — bundles get copied, restored and migrated. #614 recorded the database id so the claim can actually be checked instead of assumed. A capture
    /// that predates that field carries an empty id and is allowed through: refusing it would make older captures unopenable to enforce a check they cannot
    /// answer either way.
    /// </remarks>
    /// <param name="capturePath">The capture being attached.</param>
    /// <param name="databaseId">The session database's durable id.</param>
    /// <param name="reason">Receives why the capture was rejected, or null when it is accepted.</param>
    public static bool BelongsToDatabase(string capturePath, Guid databaseId, out string reason)
    {
        reason = null;
        try
        {
            using var stream = File.Open(capturePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new TraceFileReader(stream);
            var h = reader.ReadHeader();

            if (h.DatabaseId == Guid.Empty || h.DatabaseId == databaseId)
            {
                return true;
            }

            var recorded = h.GetDatabaseName();
            reason = $"This capture was recorded against database {h.DatabaseId:D}"
                + (string.IsNullOrEmpty(recorded) ? "" : $" ('{recorded}')")
                + $", not the one this session has open ({databaseId:D}). It is sitting in this bundle, but it does not belong to it — "
                + "open it as a standalone trace session instead.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            reason = $"The capture could not be read ({ex.GetType().Name}: {ex.Message}).";
            return false;
        }
    }
}
