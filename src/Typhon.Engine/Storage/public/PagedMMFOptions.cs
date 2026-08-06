using JetBrains.Annotations;
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Configuration for a <see cref="PagedMMF"/> / <see cref="ManagedPagedMMF"/> store: which database (name + directory), how large the page cache is, and a
/// couple of diagnostics. A Typhon database is a single on-disk bundle directory (<see cref="BundleDirectory"/>); these options locate and size it.
/// </summary>
[PublicAPI]
public class PagedMMFOptions
{
    /// <summary>Maximum length, in UTF-8 bytes, allowed for <see cref="DatabaseName"/> and <see cref="DatabaseFileName"/>.</summary>
    public const int DatabaseNameMaxUtf8Size = 63;

    private string _databaseFileName;
    /// <summary>
    /// The database name — also the stem of the bundle directory (<see cref="BundleDirectory"/>). Must match <c>^[A-Za-z0-9_-]+$</c> and fit within
    /// <see cref="DatabaseNameMaxUtf8Size"/> UTF-8 bytes. Default: <c>TyphonDB</c>.
    /// </summary>
    public string DatabaseName { get; set; } = "TyphonDB";
    /// <summary>The absolute form of <see cref="DatabaseDirectory"/>.</summary>
    public string DatabaseAbsoluteDirectory
    {
        get => Path.GetFullPath(DatabaseDirectory);
    }
    /// <summary>Directory that contains the database bundle. Default: the current working directory.</summary>
    public string DatabaseDirectory { get; set; } = Environment.CurrentDirectory;
    /// <summary>
    /// Configured database file-name prefix; falls back to <see cref="DatabaseName"/> when unset. Subject to the same character and length rules.
    /// </summary>
    public string DatabaseFileName
    {
        get => _databaseFileName ?? DatabaseName;
        set => _databaseFileName = value;
    }
    /// <summary>The page size in bytes (8 KiB). <see cref="DatabaseCacheSize"/> must be a multiple of this.</summary>
    public const int PageSizeBytes = PagedMMF.PageSize;

    /// <summary>The minimum permitted <see cref="DatabaseCacheSize"/> in bytes (8 MiB). Values below this fail validation.</summary>
    public const ulong MinimumCacheSizeBytes = PagedMMF.MinimumCacheSize;

    /// <summary>The default <see cref="DatabaseCacheSize"/> in bytes (256 MiB) — the value used when it is not set explicitly.</summary>
    public const ulong DefaultCacheSizeBytes = PagedMMF.DefaultDatabaseCacheSize;

    /// <summary>
    /// Page-cache size, in bytes. Must be a multiple of <see cref="PageSizeBytes"/>, at least <see cref="MinimumCacheSizeBytes"/>,
    /// and at most 4 GiB. Default: <see cref="DefaultCacheSizeBytes"/> (256 MiB). The cache is a GCHandle-pinned byte array, so
    /// size it for one primary engine per process; a workload whose transaction working set exceeds the cache hits
    /// <see cref="PageCacheBackpressureTimeoutException"/>. Prefer the fluent <c>TyphonOptions.PageCacheSize(...)</c> to set it.
    /// </summary>
    public ulong DatabaseCacheSize { get; set; } = DefaultCacheSizeBytes;
    /// <summary>When <c>true</c>, fills newly-allocated pages with a recognizable debug pattern (development/testing). Default <c>false</c>.</summary>
    public bool PagesDebugPattern { get; set; }

    /// <summary>
    /// How much of the database to verify when opening it. Defaults to <see cref="OpenVerification.Spine"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The clean-shutdown flag records that the last process <i>closed properly</i>. It does not record that the bytes are
    /// still correct. Damage that happens while a database is closed — bit rot, a truncated copy, a restore from the wrong
    /// place, a file-level backup tool writing through — is invisible to a clean open, which then proceeds to serve it.
    /// </para>
    /// <para>
    /// <see cref="OpenVerification.Spine"/> is the tier that earns being always on: it is bounded by the number of
    /// segments rather than the size of the database, so it reads kilobytes and costs well under a millisecond, and it
    /// catches the entire structurally-broken class — which is precisely the damage where opening anyway does the most
    /// harm, because the engine then follows directory pointers into garbage.
    /// </para>
    /// </remarks>
    public OpenVerification VerifyOnOpen { get; set; } = OpenVerification.Spine;

    /// <summary>
    /// Factory that creates the backpressure strategy for page cache allocation.
    /// Defaults to <see cref="WaitForIOStrategy"/> (signal-driven passive wait).
    /// </summary>
    internal Func<IPageCacheBackpressureStrategy> BackpressureStrategyFactory { get; set; } = () => new WaitForIOStrategy();

    /// <summary>
    /// Test-only minimal-cache profile. When <c>true</c>: allows a <see cref="DatabaseCacheSize"/> below the 8 MiB minimum
    /// (so unit tests can stress eviction with a tiny cache) AND suppresses the below-recommended-size warning. Off in
    /// production, where the small-cache warning and the 8 MiB floor apply. Consolidates the former OverrideDatabaseCacheMinSize.
    /// </summary>
    internal bool TestMode { get; set; }

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
    
    /// <summary>Whether the current configuration passes validation (name, directory, and cache-size rules). <c>true</c> when every rule holds.</summary>
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
        if (dcs < PagedMMF.MinimumCacheSize && TestMode==false)
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
    /// Advertise this holder as willing to <b>release the database on request</b> (#621). Default <c>false</c>.
    /// </summary>
    /// <remarks>
    /// <para>Set by long-lived read-only observers — the Workbench — and by nothing else. It is written into <c>db.lock</c> as <c>yieldable</c>, and it is the
    /// <i>holder's</i> advertisement that enables handoff, never the claimant's configuration. That asymmetry is what makes the feature safe to have on by
    /// default: two ordinary application instances contend exactly as they always did, because the incumbent advertised nothing.</para>
    /// <para>Advertising it is a promise. A process that sets this must actually watch for <see cref="DatabaseLockFile.RequestFileName"/> and dispose its
    /// engine when one appears; one that advertises and then ignores requests merely converts an immediate failure into a short wait followed by the same
    /// failure.</para>
    /// </remarks>
    public bool YieldableLock { get; set; }

    /// <summary>
    /// How long to wait for a <i>yieldable</i> holder to release before giving up. Default 5 seconds.
    /// </summary>
    /// <remarks>
    /// Only ever consumed when the incumbent advertised <see cref="YieldableLock"/>; a non-yieldable lock still fails instantly. Kept short deliberately — the
    /// cost of the feature being on is that a <i>hung</i> observer turns an instant failure into this much waiting, so it must stay below the threshold where a
    /// developer starts wondering whether their app has frozen. Set to <see cref="TimeSpan.Zero"/> to opt out and fail fast even against an observer.
    /// </remarks>
    public TimeSpan LockHandoffTimeout { get; set; } = TimeSpan.FromSeconds(5);

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