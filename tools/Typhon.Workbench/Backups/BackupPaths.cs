namespace Typhon.Workbench.Backups;

/// <summary>
/// Path identity for "is this the same bundle / store" (#1417 R10): full path, no trailing separator, no <c>\\?\</c> prefix, compared
/// case-insensitively on Windows and ordinally elsewhere. One helper, so the restore guard, the route and the job admission agree.
/// </summary>
internal static class BackupPaths
{
    private const string ExtendedPrefix = @"\\?\";

    /// <summary>The comparison the file system's names follow.</summary>
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The canonical spelling of <paramref name="path"/>: trimmed of quotes and blanks, full, without a trailing separator.</summary>
    public static string Full(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var p = path.Trim().Trim('"');
        if (p.StartsWith(ExtendedPrefix, StringComparison.Ordinal) && !p.StartsWith(ExtendedPrefix + "UNC", StringComparison.OrdinalIgnoreCase))
        {
            p = p[ExtendedPrefix.Length..];
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
    }

    /// <summary>A dictionary key for <paramref name="path"/> under <see cref="Comparison"/>.</summary>
    public static string Key(string path) => OperatingSystem.IsWindows() ? Full(path).ToUpperInvariant() : Full(path);

    /// <summary>Both name the same file system entry (null or blank never does).</summary>
    public static bool Same(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(Full(a), Full(b), Comparison);
}
