using Typhon.Workbench.Dtos.Fs;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Fs;

/// <summary>
/// Thin wrapper over the filesystem for the Workbench's in-app file browser. No security — local
/// dev tool, user is the only client (documented in 02-architecture.md).
/// </summary>
public sealed class FileBrowserService
{
    /// <summary>Hard cap on directory listing size — guards against 100k-entry dumps that would
    /// stall JSON serialization and blow up the client. Large directories surface with
    /// <see cref="DirectoryListingDto.Truncated"/> = true so the UI can hint the user.</summary>
    private const int MaxEntries = 5000;

    public string Home()
    {
        var fromEnv = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
        {
            return fromEnv;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public DirectoryListingDto List(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new WorkbenchException(400, "invalid_path", "Path is required.");
        }
        if (!Directory.Exists(path))
        {
            throw new WorkbenchException(404, "path_not_found", $"Directory not found: {path}");
        }

        var full = Path.GetFullPath(path);
        var parent = Directory.GetParent(full)?.FullName;
        var entries = new List<FileEntryDto>();

        var truncated = false;
        foreach (var entry in EnumerateSafe(full))
        {
            if (entries.Count >= MaxEntries)
            {
                truncated = true;
                break;
            }
            try
            {
                var attr = File.GetAttributes(entry);
                var isDir = (attr & FileAttributes.Directory) == FileAttributes.Directory;
                var name = Path.GetFileName(entry);
                if (isDir && name.EndsWith(".typhon", StringComparison.OrdinalIgnoreCase))
                {
                    // A .typhon directory IS a database bundle — present it as one openable "file" row (sized by its data
                    // file), not a folder to descend into (the user never navigates data/db.lock/wal directly).
                    var (size, lastWrite) = BundleSizeAndTime(entry);
                    entries.Add(new FileEntryDto(name, entry, "file", size, lastWrite, false));
                }
                else if (isDir)
                {
                    entries.Add(new FileEntryDto(name, entry, "dir", null, null, false));
                }
                else
                {
                    var info = new FileInfo(entry);
                    var isSchema = name.EndsWith(".schema.dll", StringComparison.OrdinalIgnoreCase);
                    entries.Add(new FileEntryDto(name, entry, "file", info.Length, info.LastWriteTimeUtc, isSchema));
                }
            }
            catch
            {
                // Skip entries we can't stat (permission, lock, etc.) — partial listings are fine.
            }
        }

        // Sort: directories first, then files, both alphabetical (case-insensitive).
        entries.Sort((a, b) =>
        {
            if (a.Kind != b.Kind) return a.Kind == "dir" ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return new DirectoryListingDto(full, parent, entries.ToArray(), truncated);
    }

    public FileEntryDto Stat(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new WorkbenchException(400, "invalid_path", "Path is required.");
        }

        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);

        if (Directory.Exists(full))
        {
            if (name.EndsWith(".typhon", StringComparison.OrdinalIgnoreCase))
            {
                // A .typhon directory is a database bundle — report it as an openable "file" sized by its data file.
                var (bundleSize, bundleWrite) = BundleSizeAndTime(full);
                return new FileEntryDto(name, full, "file", bundleSize, bundleWrite, false);
            }
            return new FileEntryDto(name, full, "dir", null, null, false);
        }
        if (File.Exists(full))
        {
            var info = new FileInfo(full);
            var isSchema = name.EndsWith(".schema.dll", StringComparison.OrdinalIgnoreCase);
            return new FileEntryDto(name, full, "file", info.Length, info.LastWriteTimeUtc, isSchema);
        }
        throw new WorkbenchException(404, "path_not_found", $"Path not found: {path}");
    }

    /// <summary>
    /// Size + last-write time to report for a <c>.typhon</c> database bundle directory — "the database size" the user
    /// means is the size of the bundle's <c>data</c> file. Falls back to the directory's own mtime when the data file is
    /// absent (an empty or partially-created bundle).
    /// </summary>
    private static (long? Size, DateTime LastWrite) BundleSizeAndTime(string bundleDir)
    {
        var dataPath = Path.Combine(bundleDir, "data");
        if (File.Exists(dataPath))
        {
            var dataInfo = new FileInfo(dataPath);
            return (dataInfo.Length, dataInfo.LastWriteTimeUtc);
        }
        return (null, Directory.GetLastWriteTimeUtc(bundleDir));
    }

    private static IEnumerable<string> EnumerateSafe(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory);
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }
}
