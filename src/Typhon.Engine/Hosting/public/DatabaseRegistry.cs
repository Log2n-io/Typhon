using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Typhon.Engine;

/// <summary>
/// One database this machine has seen — a row of the machine-local registry (#622, design D-7).
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Exists"/> is deliberately not serialised.</b> Whether a bundle is still on disk is true at the instant it is asked and can be falsified by the
/// next `mv`; storing it would produce a list that confidently reports databases that moved months ago. That is precisely how every tool's "recent connections"
/// menu ends up untrusted, so the verdict is recomputed by <see cref="DatabaseRegistry.List"/> on every read and the type makes storing it impossible.
/// </para>
/// </remarks>
[PublicAPI]
public sealed record DatabaseRegistryEntry
{
    /// <summary>Absolute, normalised path of the database bundle directory (<c>{name}.typhon</c>). The registry's key.</summary>
    public string BundlePath { get; init; }

    /// <summary>The database name — the bundle directory's stem.</summary>
    public string Name { get; init; }

    /// <summary>
    /// The database's durable identity (<see cref="DatabaseEngine.DatabaseId"/>, #614). Recorded so "the same path, but a different database" — deleted and
    /// recreated between two opens — is detectable rather than showing as one continuously-known row.
    /// </summary>
    public Guid DatabaseId { get; init; }

    /// <summary>When this database was first registered on this machine. Preserved across re-registration.</summary>
    public DateTime FirstSeenUtc { get; init; }

    /// <summary>When it was last opened.</summary>
    public DateTime LastOpenedUtc { get; init; }

    /// <summary>
    /// The entry assembly of the process that last opened it (e.g. <c>AntHill.Demo</c>). Not required by D-7; it is what makes a row identifiable when three
    /// databases on a machine share a name, and it is covered by the same kill-switch as the path.
    /// </summary>
    public string LastOpenedBy { get; init; }

    /// <summary>Whether the bundle is still on disk — computed at read time, never stored. See the remarks on this type.</summary>
    [JsonIgnore]
    public bool Exists { get; init; }
}

/// <summary>
/// The machine-local index of databases any Typhon process has opened — <c>%LOCALAPPDATA%\Typhon\databases\</c> (#622, design D-7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Discoverability, not correctness.</b> D-8 bounds this deliberately: an index of paths cannot tell you which capture belongs to which database — captures
/// live inside the bundle (D-1) and carry their own identity (D-2). Nothing in the engine or the Workbench may depend on this registry being complete, present
/// or right. It exists so a database created by a game server three directories away can be *found*, and for nothing else.
/// </para>
/// <para>
/// <b>A directory of small files, not one shared JSON.</b> Several engines starting at once would otherwise contend on a read-modify-write of a single
/// document; here each database owns one file, written whole and atomically. Pruning is a delete, and a file corrupted by a crash or a hand-edit costs its own
/// row rather than the list.
/// </para>
/// <para>
/// <b>Default-on, with three guards.</b> The asymmetry decides it: a discoverability failure is *silent* — an empty list teaches the user the feature is
/// useless and they stop looking — while a noisy list is irritating, self-correcting, and at least proves the mechanism works. The guards are
/// <see cref="SuppressForProcess"/> (one line per test project), the <see cref="DisableEnvironmentVariable"/> environment variable, a
/// <see cref="DisabledMarkerFileName"/> file in the registry directory, and automatic suppression of anything under the OS temp directory (which covers the
/// whole test estate without anyone remembering to). The <c>README.txt</c> written beside the entries documents the switches, so whoever finds the index finds
/// the way to turn it off without reading source.
/// </para>
/// <para>
/// <b>Every write is best-effort.</b> Opening a database must never fail because a machine-local convenience index could not be updated — an unwritable or
/// redirected <c>%LOCALAPPDATA%</c> costs the host this feature and nothing else. Same discipline as the profiler's capture destination
/// (<c>ProfilerBootstrap.ApplyDefaultCaptureDestination</c>).
/// </para>
/// </remarks>
[PublicAPI]
public sealed class DatabaseRegistry
{
    /// <summary>Environment variable that turns registration off machine-wide for a process tree. Any of <c>off</c>, <c>0</c>, <c>false</c>, <c>no</c>.</summary>
    public const string DisableEnvironmentVariable = "TYPHON_DATABASE_REGISTRY";

    /// <summary>Name of a file in the registry directory whose mere presence disables registration. The most discoverable switch: it sits with the data.</summary>
    public const string DisabledMarkerFileName = "disabled";

    /// <summary>Name of the self-documenting file written beside the entries.</summary>
    public const string ReadmeFileName = "README.txt";

    /// <summary>Extension of an entry file.</summary>
    public const string EntryExtension = ".json";

    private readonly string _directory;

    /// <summary>Creates a registry rooted at <paramref name="directory"/>. Hosts and tests supply their own root; the engine uses <see cref="EffectiveDirectory"/>.</summary>
    public DatabaseRegistry(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <summary>The directory this instance reads and writes.</summary>
    public string Directory => _directory;

    // ── Process policy ──

    /// <summary>
    /// Turns registration off for this process. One line in a shared test base or <c>[SetUpFixture]</c>, inherited by every fixture — the explicit half of
    /// D-7's guards, for suites whose databases do not live under the OS temp directory and so are not caught automatically.
    /// </summary>
    public static bool SuppressForProcess { get; set; }

    /// <summary>Redirects the registry away from <see cref="DefaultDirectory"/>. Set by tests and by hosts that keep their state elsewhere.</summary>
    public static string DirectoryOverride { get; set; }

    /// <summary>
    /// <c>%LOCALAPPDATA%\Typhon\databases\</c> on Windows, the XDG data-home equivalent on POSIX — the same root the Workbench's bootstrap token already uses.
    /// </summary>
    public static string DefaultDirectory
    {
        get
        {
            string root;
            if (OperatingSystem.IsWindows())
            {
                root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            else
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                root = !string.IsNullOrWhiteSpace(xdg) ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            }
            return Path.Combine(root, "Typhon", "databases");
        }
    }

    /// <summary>The directory the engine registers into: <see cref="DirectoryOverride"/> when set, otherwise <see cref="DefaultDirectory"/>.</summary>
    public static string EffectiveDirectory => string.IsNullOrWhiteSpace(DirectoryOverride) ? DefaultDirectory : DirectoryOverride;

    /// <summary>
    /// Whether this registry accepts registrations, and — when it does not — which switch stopped it.
    /// </summary>
    /// <remarks>
    /// The reason is produced here rather than reconstructed by a caller because a UI that renders an empty list for a disabled registry recreates exactly the
    /// failure D-7 warns about: the user concludes the feature is useless instead of learning it is switched off. "Off" and "nothing yet" must not look alike.
    /// </remarks>
    /// <param name="disabledReason">A human-readable sentence naming the responsible switch, or <c>null</c> when enabled.</param>
    public bool IsEnabled(out string disabledReason)
    {
        if (SuppressForProcess)
        {
            disabledReason = "This process opted out of the database registry (DatabaseRegistry.SuppressForProcess).";
            return false;
        }

        if (!EnvironmentAllows())
        {
            disabledReason = $"The {DisableEnvironmentVariable} environment variable is set to "
                + $"'{Environment.GetEnvironmentVariable(DisableEnvironmentVariable)}'.";
            return false;
        }

        // Checked last: it is the only one that touches the disk, and the two above already cover every case where we promised to do no I/O at all.
        if (File.Exists(Path.Combine(_directory, DisabledMarkerFileName)))
        {
            disabledReason = $"A '{DisabledMarkerFileName}' file exists in {_directory}.";
            return false;
        }

        disabledReason = null;
        return true;
    }

    // ── Write side ──

    /// <summary>
    /// Records an open. Returns <c>false</c> when a guard declined it. Preserves <see cref="DatabaseRegistryEntry.FirstSeenUtc"/> from any existing entry.
    /// </summary>
    /// <remarks>
    /// The entry is written whole, to a process-unique temporary file, then moved over the target — one atomic replace, so a concurrent open of the same
    /// database can never produce a half-written row. The two writers then race on the *content*, which is benign: they are describing the same database and
    /// disagree only about which millisecond it was opened.
    /// </remarks>
    /// <param name="bundleDirectory">The database's bundle directory. Normalised to an absolute path before use.</param>
    /// <param name="databaseName">The database name.</param>
    /// <param name="databaseId">The database's durable identity.</param>
    public bool Record(string bundleDirectory, string databaseName, Guid databaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectory);

        if (!IsEnabled(out _) || !TryNormalizePath(bundleDirectory, out var fullPath))
        {
            return false;
        }

        System.IO.Directory.CreateDirectory(_directory);
        EnsureReadme();

        var target = Path.Combine(_directory, FileNameFor(fullPath));
        var now = DateTime.UtcNow;
        var firstSeen = TryRead(target, out var existing) ? existing.FirstSeenUtc : now;

        var entry = new DatabaseRegistryEntry
        {
            BundlePath = fullPath,
            Name = string.IsNullOrWhiteSpace(databaseName) ? Path.GetFileNameWithoutExtension(fullPath) : databaseName,
            DatabaseId = databaseId,
            FirstSeenUtc = firstSeen == default ? now : firstSeen,
            LastOpenedUtc = now,
            LastOpenedBy = CurrentProcessLabel(),
        };

        var json = JsonSerializer.Serialize(entry, DatabaseRegistryJsonContext.Default.DatabaseRegistryEntry);

        // Process-unique temp name: two engines registering the same database at once must not collide on the staging file itself.
        var tmp = $"{target}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, target, overwrite: true);
        return true;
    }

    /// <summary>
    /// Writes the self-documenting <c>README.txt</c> if it is absent. This is D-7's "findable without reading source": whoever goes looking for the index
    /// because they are uneasy about it finds the instructions for switching it off sitting next to it.
    /// </summary>
    /// <remarks>
    /// Best-effort and atomic, for one reason: two engines starting at once would otherwise both try to write this file, and the loser's
    /// <see cref="Record"/> would fail on a sharing violation — costing a real registration for the sake of a help file. Failing to explain the directory is
    /// never worse than failing to populate it.
    /// </remarks>
    public void EnsureReadme()
    {
        var path = Path.Combine(_directory, ReadmeFileName);
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            var tmp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, ReadmeText);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another process is writing it, or the directory is read-only. Either way the entries matter and this does not.
        }
    }

    // ── Read side ──

    /// <summary>
    /// Every known database, most-recently-opened first, each carrying a freshly-computed <see cref="DatabaseRegistryEntry.Exists"/>.
    /// </summary>
    /// <remarks>
    /// <b>Never throws, and a bad file costs only itself.</b> Containing corruption to one row is the reason D-7 chose a directory of files over a single
    /// shared document, so an unparseable entry is skipped and its neighbours are still returned.
    /// </remarks>
    public IReadOnlyList<DatabaseRegistryEntry> List()
    {
        var results = new List<DatabaseRegistryEntry>();
        if (!System.IO.Directory.Exists(_directory))
        {
            return results;
        }

        string[] files;
        try
        {
            files = System.IO.Directory.GetFiles(_directory, "*" + EntryExtension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return results;
        }

        foreach (var file in files)
        {
            // Windows matches wildcards against 8.3 short names too, so "*.json" can also return "x.json.1234.tmp". Re-test explicitly rather than trusting
            // the pattern — a staging file surfacing as an entry would be an intermittent, machine-specific bug.
            if (!file.EndsWith(EntryExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryRead(file, out var entry) || string.IsNullOrWhiteSpace(entry.BundlePath))
            {
                continue;
            }

            results.Add(entry with { Exists = SafeDirectoryExists(entry.BundlePath) });
        }

        results.Sort(static (a, b) => b.LastOpenedUtc.CompareTo(a.LastOpenedUtc));
        return results;
    }

    /// <summary>Removes one database from the registry. Returns <c>false</c> when it was not there. Never deletes the database itself.</summary>
    public bool Forget(string bundleDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectory);

        // A path the OS cannot even parse names nothing, so it forgets nothing. Reported rather than thrown because this argument reaches us from an HTTP
        // query string, and a malformed one is a 404-shaped answer, not a server fault.
        if (!TryNormalizePath(bundleDirectory, out var fullPath))
        {
            return false;
        }

        var target = Path.Combine(_directory, FileNameFor(fullPath));
        try
        {
            if (!File.Exists(target))
            {
                return false;
            }
            File.Delete(target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Drops every entry whose bundle is no longer on disk, returning how many went. Explicit by design — D-7 asks for validate-on-listing and an *offer* to
    /// prune, so <see cref="List"/> never deletes anything on its own.
    /// </summary>
    public int PruneMissing()
    {
        var removed = 0;
        foreach (var entry in List())
        {
            if (!entry.Exists && Forget(entry.BundlePath))
            {
                removed++;
            }
        }
        return removed;
    }

    // ── Engine hook ──

    /// <summary>
    /// Records an open on behalf of a freshly-constructed <see cref="DatabaseEngine"/>. Best-effort: discoverability must never cost an open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every failure is absorbed, and reported once.</b> An unwritable or redirected <c>%LOCALAPPDATA%</c>, a service account with no profile, a read-only
    /// container, a full disk — all of them degrade to "this database is not in the index", never to a failed open. The engine does not need this feature to
    /// work; the Workbench merely finds databases faster when it does.
    /// </para>
    /// <para>
    /// Absorbed is not the same as invisible, though: a user whose databases never appear in the Workbench deserves to be able to find out why, so the failure
    /// is logged as a warning naming the directory and the reason. It fires at most once per engine open. The guards declining (temp path, suppressed process,
    /// kill-switch) are <i>not</i> failures and say nothing — they are the feature working as configured.
    /// </para>
    /// </remarks>
    /// <param name="logger">The engine's logger. May be <c>null</c> — some hosts construct an engine without one.</param>
    /// <param name="bundleDirectory">The database's bundle directory, as the engine's storage reports it (possibly relative).</param>
    /// <param name="databaseName">The database name.</param>
    /// <param name="databaseId">The database's durable identity.</param>
    internal static void TryRecordOpen(ILogger logger, string bundleDirectory, string databaseName, Guid databaseId)
    {
        // Both checks are allocation- and I/O-free, so a suppressed process and the whole temp-rooted test estate pay nothing measurable per open.
        if (SuppressForProcess || string.IsNullOrWhiteSpace(bundleDirectory) || IsUnderTempDirectory(bundleDirectory))
        {
            return;
        }

        var directory = EffectiveDirectory;
        try
        {
            new DatabaseRegistry(directory).Record(bundleDirectory, databaseName, databaseId);
        }
        catch (Exception ex)
        {
            // [LoggerMessage] methods do not tolerate a null logger, and the engine's is legitimately null in some hosts — same reason ProfilerBootstrap
            // substitutes NullLogger before reporting a capture-directory failure.
            DatabaseRegistryLog.RegistrationFailed(logger ?? NullLogger.Instance, directory, ex.GetType().Name, ex.Message, DisableEnvironmentVariable);
        }
    }

    // ── Helpers ──

    /// <summary>
    /// True when <paramref name="bundleDirectory"/> sits under the OS temp directory. Catches unit tests, POCs and throwaway fixtures automatically, which is
    /// what makes the explicit per-project opt-out a backstop rather than the primary defence.
    /// </summary>
    public static bool IsUnderTempDirectory(string bundleDirectory) => IsUnder(bundleDirectory, Path.GetTempPath());

    /// <summary>The entry file name for a bundle path — 128 bits of SHA-256, hex.</summary>
    /// <remarks>
    /// <para>
    /// A path cannot be a file name (separators, length limits), so it is hashed. Half a SHA-256 is far past the point where a collision on one machine's
    /// database paths is worth reasoning about, and the entry carries the full path anyway.
    /// </para>
    /// <para>
    /// <b>Case is folded on Windows only</b>, because there <c>C:\Games\World.typhon</c> and <c>c:\games\world.typhon</c> are the same directory and must
    /// therefore be the same row; on Linux they are two different databases and folding them would merge unrelated entries.
    /// </para>
    /// </remarks>
    internal static string FileNameFor(string normalizedBundlePath)
    {
        var key = OperatingSystem.IsWindows() ? normalizedBundlePath.ToUpperInvariant() : normalizedBundlePath;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        return Convert.ToHexStringLower(hash[..16]) + EntryExtension;
    }

    /// <summary>
    /// The absolute, separator-normalised form of a bundle path.
    /// </summary>
    /// <remarks>
    /// Load-bearing, not hygiene: <c>PagedMMFOptions.BundleDirectory</c> composes the *raw* <c>DatabaseDirectory</c>, which is frequently relative. Keying on
    /// it unnormalised would file the same database under two names when it is opened from two working directories, and the user would see it twice.
    /// </remarks>
    internal static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// <see cref="NormalizePath"/> for input the caller does not control — a path the OS refuses to parse (invalid characters, too long) yields
    /// <c>false</c> rather than an exception, so a bad HTTP query string cannot become a server fault.
    /// </summary>
    private static bool TryNormalizePath(string path, out string normalized)
    {
        try
        {
            normalized = NormalizePath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            normalized = null;
            return false;
        }
    }

    private static bool EnvironmentAllows()
    {
        var raw = Environment.GetEnvironmentVariable(DisableEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var value = raw.Trim();
        return !(string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "0", StringComparison.Ordinal)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether <paramref name="path"/> lies inside <paramref name="root"/>. Boundary-aware: <c>/tmp/x</c> is under <c>/tmp</c>, <c>/tmpfoo</c> is not — the
    /// naive prefix test would suppress registration for any path that merely starts with the temp directory's spelling.
    /// </summary>
    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string full, rootFull;
        try
        {
            full = NormalizePath(path);
            rootFull = NormalizePath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // Windows paths are case-insensitive; POSIX ones are not. Getting this wrong on POSIX would only fail to suppress (a noisy entry), never suppress a
        // real database — the safe direction for a guard whose whole purpose is to keep test fixtures out.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootFull, comparison))
        {
            return false;
        }

        return full.Length == rootFull.Length || full[rootFull.Length] == Path.DirectorySeparatorChar || full[rootFull.Length] == Path.AltDirectorySeparatorChar;
    }

    private static bool TryRead(string file, out DatabaseRegistryEntry entry)
    {
        entry = null;
        try
        {
            if (!File.Exists(file))
            {
                return false;
            }
            var json = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }
            entry = JsonSerializer.Deserialize(json, DatabaseRegistryJsonContext.Default.DatabaseRegistryEntry);
            return entry != null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            entry = null;
            return false;
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return System.IO.Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The entry assembly's name, falling back to the host executable's. Never throws — a missing label must not stop a registration.</summary>
    private static string CurrentProcessLabel()
    {
        try
        {
            var friendly = AppDomain.CurrentDomain.FriendlyName;
            if (!string.IsNullOrWhiteSpace(friendly))
            {
                return friendly;
            }
            var exe = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(exe) ? "unknown" : Path.GetFileNameWithoutExtension(exe);
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ReadmeText =>
        $"""
         Typhon — machine-local database registry
         ========================================

         Every Typhon process records the databases it opens here, one small JSON file per
         database, named after a hash of the database's absolute path. Nothing but this
         directory is written: the databases themselves are untouched, and deleting a file
         here only makes the Workbench forget where that database was.

         Its only purpose is discoverability — so a tool can list databases this machine has
         seen without you browsing for them. Nothing depends on it being complete or correct.

         Databases created under the operating system's temporary directory are never
         recorded, so test suites and throwaway fixtures do not appear here.

         To turn it off
         --------------

         Any one of these stops registration:

           1. Create an empty file named "{DisabledMarkerFileName}" in this directory.
              Machine-wide, survives upgrades, takes effect immediately.

           2. Set the environment variable {DisableEnvironmentVariable}=off
              for the process or the machine.

           3. In code, before opening a database:
              Typhon.Engine.DatabaseRegistry.SuppressForProcess = true;

         Existing files are not deleted when you disable it — remove them yourself, or use
         the Workbench's Known-databases list.
         """;
}

/// <summary>
/// Log messages for the machine-local registry. Source-generated so the level check happens before any argument is boxed.
/// </summary>
internal static partial class DatabaseRegistryLog
{
    [LoggerMessage(EventId = 6180, Level = LogLevel.Warning,
        Message = "Database registry: could not record this database in '{Directory}' ({ExceptionType}: {Reason}). The database opened normally; it simply "
                + "will not appear in the Workbench's known-databases list. Set {EnvVar}=off to stop trying.")]
    public static partial void RegistrationFailed(ILogger logger, string directory, string exceptionType, string reason, string envVar);
}

/// <summary>
/// Source-generated serialization for <see cref="DatabaseRegistryEntry"/>. Generated rather than reflection-based so the engine stays trim/AOT-clean (#409).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DatabaseRegistryEntry))]
internal sealed partial class DatabaseRegistryJsonContext : JsonSerializerContext;
