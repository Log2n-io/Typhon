using System.Reflection;

namespace Typhon.Workbench.Schema;

/// <summary>
/// Finds the schema assemblies a database names in its persisted manifest (ADR-055, <c>AssemblyR1</c>,
/// <see cref="Engine.DatabaseEngine.GetRequiredAssemblies"/>) — the one resolver of the Workbench's sessions, its drill and settle children and
/// <c>typhon backup drill</c> (#1485).
/// </summary>
/// <remarks>
/// <para>A file matches a manifest entry only at its <b>exact identity</b>: simple name, version and public-key token. A file of the right name at another
/// version or token is not the assembly the database names — another build — and is reported as a <see cref="VersionMismatch"/>, never loaded in its place:
/// the database names the build that last wrote it (an ordinary open records it), so a different one would migrate it, or be taken for the one that
/// wrote it.</para>
/// <para>Search order (ADR-055): user-registered directories → the host's own deployment directory ("bundled") → the database file's directory
/// ("legacy-adjacent") → <c>bin/*/*</c> beside it ("app-build-output"). In each, the fast path <c>{SimpleName}.dll</c>, then a metadata name-match over
/// every <c>*.dll</c> (a versioned or <c>*.schema.dll</c>-named file). The first exact match wins; a mismatch is reported only when no directory holds the
/// exact assembly.</para>
/// </remarks>
public static class SchemaAssemblyResolver
{
    /// <summary>A file of a manifest assembly's name that is another build of it.</summary>
    /// <param name="Name">The assembly's simple name.</param>
    /// <param name="Expected">The identity the database names (version and token).</param>
    /// <param name="Found">The file's.</param>
    /// <param name="Path">The file.</param>
    public sealed record VersionMismatch(string Name, string Expected, string Found, string Path);

    /// <summary>What a resolution found.</summary>
    /// <param name="Paths">The exact files, in manifest order.</param>
    /// <param name="Status">Where they came from: <c>schemaless</c>, <c>bundled</c>, <c>registered</c> or <c>legacy-adjacent</c> (the most notable).</param>
    /// <param name="Missing">Manifest assemblies no directory holds under their name.</param>
    /// <param name="Mismatches">Manifest assemblies only another build of which was found.</param>
    public sealed record Resolution(string[] Paths, string Status, IReadOnlyList<string> Missing, IReadOnlyList<VersionMismatch> Mismatches);

    /// <summary>The ADR-055 search order for a database in <paramref name="databaseDirectory"/>, <paramref name="registeredDirs"/> first.</summary>
    public static List<(string Dir, string Tier)> SearchOrder(string databaseDirectory, string[] registeredDirs)
    {
        var dirs = new List<(string Dir, string Tier)>((registeredDirs?.Length ?? 0) + 2);
        if (registeredDirs != null)
        {
            foreach (var d in registeredDirs)
            {
                if (!string.IsNullOrWhiteSpace(d))
                {
                    dirs.Add((d, "registered"));
                }
            }
        }

        dirs.Add((AppContext.BaseDirectory, "bundled"));
        dirs.Add((databaseDirectory, "legacy-adjacent"));

        // A database created by a .NET app sits in that app's working directory, and the app's own assembly — the one the database names as its
        // schema — is one level down in bin/{Config}/{tfm}/: the golden path `typhon new` → `dotnet run` → open in the Workbench. Fixed depth, only
        // under the bundle's own parent, ranked last, and still matched by exact identity.
        foreach (var probe in EnumerateAdjacentBuildOutputs(databaseDirectory))
        {
            dirs.Add((probe, "app-build-output"));
        }

        return dirs;
    }

    /// <summary>Resolves <paramref name="required"/> (the manifest) over <paramref name="searchDirs"/>, each assembly at its exact identity.</summary>
    public static Resolution Resolve(IReadOnlyList<AssemblyName> required, IReadOnlyList<(string Dir, string Tier)> searchDirs)
    {
        if (required.Count == 0)
        {
            return new Resolution([], "schemaless", [], []);
        }

        var nameIndex = new Dictionary<string, List<string>>[searchDirs.Count];
        var paths = new List<string>(required.Count);
        var missing = new List<string>();
        var mismatches = new List<VersionMismatch>();
        var usedLegacy = false;
        var usedRegistered = false;
        foreach (var expected in required)
        {
            if (string.IsNullOrEmpty(expected.Name))
            {
                continue;
            }

            string resolved = null;
            string tier = null;
            VersionMismatch other = null;
            for (var i = 0; i < searchDirs.Count && resolved == null; i++)
            {
                // The fast path {Name}.dll first: the directory is indexed only when it holds no exact match under that name.
                var direct = string.IsNullOrEmpty(searchDirs[i].Dir) ? null : Path.Combine(searchDirs[i].Dir, expected.Name + ".dll");
                if (direct != null && File.Exists(direct) && TryGetName(direct) is { } directName && SameIdentity(expected, directName))
                {
                    resolved = direct;
                    tier = searchDirs[i].Tier;
                    break;
                }

                foreach (var candidate in Candidates(searchDirs[i].Dir, expected.Name, direct, ref nameIndex[i]))
                {
                    var found = TryGetName(candidate);
                    if (found == null || !string.Equals(found.Name, expected.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (SameIdentity(expected, found))
                    {
                        resolved = candidate;
                        tier = searchDirs[i].Tier;
                        break;
                    }

                    other ??= new VersionMismatch(expected.Name, Describe(expected), Describe(found), candidate);
                }
            }

            if (resolved != null)
            {
                paths.Add(resolved);
                usedLegacy |= tier == "legacy-adjacent";
                usedRegistered |= tier == "registered";
            }
            else if (other != null)
            {
                mismatches.Add(other);
            }
            else
            {
                missing.Add(expected.Name);
            }
        }

        // Provenance precedence (most notable wins): "legacy-adjacent" the moment a per-DB copy supplied anything (the UI flags a still-copied database);
        // else "registered" if a user-pointed directory did; else "bundled".
        var status = paths.Count == 0 ? "schemaless" : usedLegacy ? "legacy-adjacent" : usedRegistered ? "registered" : "bundled";
        return new Resolution(paths.ToArray(), status, missing, mismatches);
    }

    /// <summary>
    /// The check of a file the caller names (a session's or a drill's <c>--schema</c>): <c>null</c> when it is no manifest assembly (a dependency, or one
    /// whose types the database never stored) or the manifest's exact one; else why it is another build.
    /// </summary>
    public static VersionMismatch CheckNamedFile(IReadOnlyList<AssemblyName> required, string path)
    {
        var found = TryGetName(path);
        if (found == null)
        {
            return null;
        }

        foreach (var expected in required)
        {
            if (string.Equals(expected.Name, found.Name, StringComparison.OrdinalIgnoreCase))
            {
                return SameIdentity(expected, found) ? null : new VersionMismatch(expected.Name, Describe(expected), Describe(found), path);
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="found"/> is the assembly <paramref name="expected"/> names: its name, version (unset parts as 0) and token.</summary>
    public static bool SameIdentity(AssemblyName expected, AssemblyName found) =>
        string.Equals(expected.Name, found.Name, StringComparison.OrdinalIgnoreCase)
        && Normalize(expected.Version) == Normalize(found.Version)
        && Token(expected).AsSpan().SequenceEqual(Token(found));

    /// <summary>
    /// The <c>bin/{Configuration}/{tfm}/</c> directories directly under <paramref name="databaseParent"/>, newest first. Empty when there is no <c>bin</c> —
    /// every non-development database — so this costs one failed <see cref="Directory.Exists"/> in production.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose: exactly <c>bin/*/*</c>, never a recursive walk. A deep sweep of an arbitrary directory a database happens to live in is both slow
    /// and a way to load an assembly the user never meant to expose. Ordered by write time so a fresh <c>Debug</c> build outranks a stale <c>Release</c> one.
    /// </remarks>
    internal static IEnumerable<string> EnumerateAdjacentBuildOutputs(string databaseParent)
    {
        if (string.IsNullOrEmpty(databaseParent))
        {
            yield break;
        }

        var bin = Path.Combine(databaseParent, "bin");
        if (!Directory.Exists(bin))
        {
            yield break;
        }

        List<string> tfmDirs = [];
        try
        {
            foreach (var configDir in Directory.EnumerateDirectories(bin))
            {
                tfmDirs.AddRange(Directory.EnumerateDirectories(configDir));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable bin/ is not an error — it just means this probe contributes nothing.
            yield break;
        }

        tfmDirs.Sort(static (a, b) => Directory.GetLastWriteTimeUtc(b).CompareTo(Directory.GetLastWriteTimeUtc(a)));
        foreach (var d in tfmDirs)
        {
            yield return d;
        }
    }

    /// <summary>The files of <paramref name="directory"/> that may be <paramref name="name"/>: <c>{name}.dll</c>, then dlls whose metadata say so.</summary>
    private static IEnumerable<string> Candidates(string directory, string name, string direct, ref Dictionary<string, List<string>> index)
    {
        var result = new List<string>(2);
        if (string.IsNullOrEmpty(directory))
        {
            return result;
        }

        if (File.Exists(direct))
        {
            result.Add(direct);   // read again only to report it as another build: the exact fast path above did not take it
        }

        index ??= BuildAssemblyNameIndex(directory);
        if (index.TryGetValue(name, out var files))
        {
            foreach (var f in files)
            {
                if (!string.Equals(f, direct, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(f);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Indexes every managed <c>*.dll</c> in <paramref name="directory"/> by its assembly simple name (metadata only — no assembly is loaded). Empty for a
    /// missing or blank directory (a registered one may not exist yet).
    /// </summary>
    private static Dictionary<string, List<string>> BuildAssemblyNameIndex(string directory)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
        {
            return map;
        }

        try
        {
            foreach (var dll in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                if (TryGetName(dll)?.Name is { } name)
                {
                    if (!map.TryGetValue(name, out var files))
                    {
                        map[name] = files = [];
                    }

                    files.Add(dll);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable directory contributes nothing; the open goes on with the next one.
        }

        return map;
    }

    private static AssemblyName TryGetName(string path)
    {
        try
        {
            return AssemblyName.GetAssemblyName(path);
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Not a managed assembly (a native DLL, a corrupt file), or gone: not a candidate.
            return null;
        }
    }

    private static Version Normalize(Version v) =>
        v == null ? new Version(0, 0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static byte[] Token(AssemblyName name) => name.GetPublicKeyToken() ?? [];

    private static string Describe(AssemblyName name)
    {
        var token = Token(name);
        return $"{Normalize(name.Version)} ({(token.Length == 0 ? "unsigned" : "token " + Convert.ToHexString(token).ToLowerInvariant())})";
    }
}
