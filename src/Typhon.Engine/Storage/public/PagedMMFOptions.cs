using JetBrains.Annotations;
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Typhon.Engine;

[PublicAPI]
public class PagedMMFOptions
{
    public const int DatabaseNameMaxUtf8Size = 63;
    
    private string _databaseFileName;
    public string DatabaseName { get; set; } = "TyphonDB";
    public string DatabaseAbsoluteDirectory
    {
        get => Path.GetFullPath(DatabaseDirectory);
    }
    public string DatabaseDirectory { get; set; } = Environment.CurrentDirectory;
    public string DatabaseFileName
    {
        get => _databaseFileName ?? DatabaseName;
        set => _databaseFileName = value;
    }
    public ulong DatabaseCacheSize { get; set; } = PagedMMF.DefaultMemPageCount * PagedMMF.PageSize;
    public bool PagesDebugPattern { get; set; }

    /// <summary>
    /// Factory that creates the backpressure strategy for page cache allocation.
    /// Defaults to <see cref="WaitForIOStrategy"/> (signal-driven passive wait).
    /// </summary>
    internal Func<IPageCacheBackpressureStrategy> BackpressureStrategyFactory { get; set; } = () => new WaitForIOStrategy();

    internal bool OverrideDatabaseCacheMinSize { get; set; }

    /// <summary>
    /// Deletes the entire database bundle directory (<see cref="BundleDirectory"/>) — data file, lock, and WAL. A Typhon
    /// database is one directory, so "delete the database" is a single recursive directory delete. Must only be called
    /// when the database is <b>closed</b>: an open <c>data</c> handle would block the recursive delete (the throw is
    /// swallowed, potentially leaving a half-deleted bundle).
    /// </summary>
    public void EnsureFileDeleted()
    {
        try
        {
            // "Delete the database" must also clear a legacy/foreign FILE sitting at the bundle path (e.g. the old marker):
            // DeleteDirectoryAndWait only understands directories, so without this a later open would hard-fail on the
            // File.Exists guard in PagedMMF's ctor. The caller explicitly asked to wipe, so removing the file is safe here.
            if (File.Exists(BundleDirectory))
            {
                File.Delete(BundleDirectory);
            }

            DeleteDirectoryAndWait(BundleDirectory);
        }
        catch (Exception)
        {
            // ignored
        }
    }

    /// <summary>
    /// Deletes a directory recursively and polls until the NTFS pending-delete completes. On Windows, <see cref="Directory.Delete(string, bool)"/> returns
    /// before the directory entry is actually removed — subsequent operations on the same path can fail without this wait.
    /// </summary>
    private static void DeleteDirectoryAndWait(string path, int maxWaitMs = 500)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
        var sw = new System.Diagnostics.Stopwatch();
        sw.Start();
        while (Directory.Exists(path) && sw.ElapsedMilliseconds < maxWaitMs)
        {
            Thread.Sleep(1);
        }
    }
    
    public bool IsValid => Validate(true, out _);
    internal bool Validate(bool silent, out string validation)
    {
        var sb = new StringBuilder();
        var success = true;

        // DatabaseName
        var singleWordRegEx = new Regex("^[A-Za-z0-9_-]+$");
        if (singleWordRegEx.IsMatch(DatabaseName) == false)
        {
            sb.AppendLine($"Database Name '{DatabaseName}' is invalid");
            success = false;
        }

        if (Encoding.UTF8.GetByteCount(DatabaseName) > DatabaseNameMaxUtf8Size)
        {
            sb.AppendLine($"Database Name '{DatabaseName}' is too long, must not exceed {DatabaseNameMaxUtf8Size} bytes of its UTF8 version.");
            success = false;
        }

        // DatabaseDirectory
        var absDir = DatabaseDirectory;
        var di = new DirectoryInfo(absDir);
        if (di.Exists == false)
        {
            sb.AppendLine($"Database Directory '{absDir}' does not exist or is not accessible.");
            success = false;
        }

        // DatabaseFilesPrefix
        if (singleWordRegEx.IsMatch(DatabaseFileName) == false)
        {
            sb.AppendLine($"Database Files Prefix '{DatabaseName}' is invalid");
            success = false;
        }

        if (Encoding.UTF8.GetByteCount(DatabaseFileName) > 63)
        {
            sb.AppendLine($"Database Files Prefix'{DatabaseFileName}' is too long, must not exceed 63 bytes of its UTF8 version.");
            success = false;
        }

        // DatabaseCacheSize
        var dcs = DatabaseCacheSize;
        if ((dcs & (PagedMMF.PageSize - 1)) != 0UL)
        {
            sb.AppendLine($"Database Cache Size must be a multiple of the Page Size ('{PagedMMF.PageSize}').");
            success = false;
        }
        if (dcs < PagedMMF.MinimumCacheSize && OverrideDatabaseCacheMinSize==false)
        {
            sb.AppendLine($"Database Cache Size must be at least '{PagedMMF.MinimumCacheSize/(1024*1024)}'MiB.");
            success = false;
        }

        if (dcs > 0x100000000)
        {
            sb.AppendLine($"Database Cache Size is bigger than the current limit of 4GiB");
            success = false;
        }

        // Throw exception if necessary and required
        if (success == false && silent == false)
        {
            throw new Exception(sb.ToString());
        }

        validation = sb.Length==0 ? null : sb.ToString();
        return success;
    }
    
    /// <summary>
    /// The database bundle directory — <c>{DatabaseDirectory}/{DatabaseName}.typhon</c>. A Typhon database <b>is</b> this
    /// single directory; the paged data file (<c>data</c>), the single-writer lock (<c>db.lock</c>), and the WAL segment
    /// directory (<c>wal/</c>) all live inside it. See <c>claude/design/Storage/typhon-bundle-format.md</c>.
    /// </summary>
    public string BundleDirectory => Path.Combine(DatabaseDirectory, $"{DatabaseName}.typhon");

    // Fixed internal name — the bundle directory carries the identity, so the data file inside needs no name/extension.
    internal static string BuildDatabaseFileName() => "data";
    internal string BuildDatabasePathFileName() => Path.Combine(BundleDirectory, BuildDatabaseFileName());
}