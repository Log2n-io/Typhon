using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.IO;

namespace Typhon.Engine;

/// <summary>One WAL segment file found beside the data file, described without being parsed.</summary>
/// <param name="Path">Absolute path to the segment file.</param>
/// <param name="SizeBytes">Size of the file in bytes.</param>
/// <param name="Name">File name only.</param>
[PublicAPI]
public readonly record struct WalSegmentRef(string Path, long SizeBytes, string Name);

/// <summary>
/// Reads a <c>.typhon</c> bundle directly as bytes: no <c>DatabaseEngine</c>, no page cache, no lock acquisition, no WAL
/// replay. The page source the checker uses when it matters most — when the database will not open.
/// </summary>
/// <remarks>
/// <para>
/// Booting the engine to inspect a database is self-defeating three ways. It <b>destroys the evidence</b> (on the crash
/// path the rebuild net clears and re-derives indexes, chains, entity maps and occupancy before a caller gets a handle, so
/// a post-open check can only ever verify that <i>rebuild worked</i>). It <b>cannot reach the cases that matter most</b>
/// (an open that fails loudly by design leaves no handle to ask questions through). And it <b>mutates</b> — acquiring the
/// lock, clearing the clean-shutdown flag, replaying the log, checkpointing on close.
/// </para>
/// <para>
/// Opened <c>FileAccess.Read</c> with <c>FileShare.ReadWrite</c> so a live database can still be scanned; a scan of a
/// live database yields <see cref="IntegrityConfidence.Suspected"/> cross-structure findings, never
/// <see cref="IntegrityConfidence.Confirmed"/> ones.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class OfflineBundlePageSource : IPageSource
{
    private readonly SafeFileHandleWrapper _data;
    private readonly string _describe;

    /// <summary>Opens a bundle for reading.</summary>
    /// <param name="bundlePath">
    /// Path to the <c>{name}.typhon</c> bundle directory, or to the <c>data</c> file inside one. A path whose extension is
    /// missing gets <c>.typhon</c> appended when that directory exists.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="bundlePath"/> is <c>null</c>.</exception>
    /// <exception cref="DirectoryNotFoundException">No bundle directory could be resolved from <paramref name="bundlePath"/>.</exception>
    /// <exception cref="FileNotFoundException">The bundle has no <c>data</c> file.</exception>
    public OfflineBundlePageSource(string bundlePath)
    {
        ArgumentNullException.ThrowIfNull(bundlePath);

        BundlePath = ResolveBundleDirectory(bundlePath);
        var dataPath = Path.Combine(BundlePath, IntegrityConstants.DataFileName);
        if (!File.Exists(dataPath))
        {
            throw new FileNotFoundException(
                $"'{BundlePath}' has no '{IntegrityConstants.DataFileName}' file — it is not a Typhon database bundle.", dataPath);
        }

        _data = new SafeFileHandleWrapper(dataPath);
        SizeBytes = _data.Length;
        PageCount = (int)(SizeBytes / IntegrityConstants.PageSize);
        TrailingBytes = (int)(SizeBytes % IntegrityConstants.PageSize);
        _describe = BundlePath;

        var lockPath = Path.Combine(BundlePath, IntegrityConstants.LockFileName);
        LockFilePresent = File.Exists(lockPath);
        LockHeld = LockFilePresent && IsLocked(lockPath);

        WalSegments = EnumerateWal(Path.Combine(BundlePath, IntegrityConstants.WalDirectoryName));
    }

    /// <summary>Absolute path to the bundle directory.</summary>
    public string BundlePath { get; }

    /// <summary>Size of the <c>data</c> file in bytes.</summary>
    public long SizeBytes { get; }

    /// <summary>
    /// Bytes past the last whole page. Non-zero means the data file is truncated mid-page — itself a finding, because
    /// every write the engine performs is whole-page.
    /// </summary>
    public int TrailingBytes { get; }

    /// <summary>Whether a <c>db.lock</c> file exists in the bundle.</summary>
    public bool LockFilePresent { get; }

    /// <summary>Whether <c>db.lock</c> is held by another process — i.e. a live engine has this database open.</summary>
    public bool LockHeld { get; }

    /// <summary>WAL segment files found in the bundle's <c>wal/</c> directory, unparsed, ordered by name.</summary>
    public IReadOnlyList<WalSegmentRef> WalSegments { get; }

    /// <inheritdoc />
    public int PageCount { get; }

    /// <inheritdoc />
    public bool TryReadPage(int index, Span<byte> destination)
    {
        if (index < 0 || index >= PageCount)
        {
            return false;
        }

        if (destination.Length < IntegrityConstants.PageSize)
        {
            throw new ArgumentException($"Destination must be at least {IntegrityConstants.PageSize} bytes.", nameof(destination));
        }

        return _data.ReadExactly(destination[..IntegrityConstants.PageSize], index * (long)IntegrityConstants.PageSize);
    }

    /// <inheritdoc />
    public string Describe() => _describe;

    /// <inheritdoc />
    public void Dispose() => _data.Dispose();

    /// <summary>
    /// Resolves a user-supplied path to a bundle directory. Accepts the directory itself, the <c>data</c> file inside it,
    /// or a stem with no extension.
    /// </summary>
    /// <param name="path">The user-supplied path.</param>
    /// <exception cref="DirectoryNotFoundException">Nothing resolvable was found.</exception>
    public static string ResolveBundleDirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.GetFullPath(path);

        if (Directory.Exists(full))
        {
            return full;
        }

        // Pointed at the data file inside a bundle.
        if (File.Exists(full) && string.Equals(Path.GetFileName(full), IntegrityConstants.DataFileName, StringComparison.Ordinal))
        {
            return Path.GetDirectoryName(full);
        }

        // A stem without the canonical extension.
        var withExt = full + IntegrityConstants.BundleExtension;
        if (Directory.Exists(withExt))
        {
            return withExt;
        }

        throw new DirectoryNotFoundException(
            $"No Typhon bundle at '{path}'. Expected a '{IntegrityConstants.BundleExtension}' directory containing "
            + $"'{IntegrityConstants.DataFileName}'.");
    }

    private static bool IsLocked(string lockPath)
    {
        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static IReadOnlyList<WalSegmentRef> EnumerateWal(string walDir)
    {
        if (!Directory.Exists(walDir))
        {
            return [];
        }

        var files = Directory.GetFiles(walDir);
        Array.Sort(files, StringComparer.Ordinal);
        var refs = new List<WalSegmentRef>(files.Length);
        for (var i = 0; i < files.Length; i++)
        {
            var fi = new FileInfo(files[i]);
            refs.Add(new WalSegmentRef(files[i], fi.Length, fi.Name));
        }

        return refs;
    }

    /// <summary>
    /// Minimal read-only file handle wrapper. Separate so the read path stays a single positional read with no stream
    /// state, which is what lets a scan run against a file another process is actively writing.
    /// </summary>
    private sealed class SafeFileHandleWrapper : IDisposable
    {
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _handle;

        public SafeFileHandleWrapper(string path)
        {
            _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Length = RandomAccess.GetLength(_handle);
        }

        public long Length { get; }

        public bool ReadExactly(Span<byte> destination, long offset)
        {
            var total = 0;
            while (total < destination.Length)
            {
                var read = RandomAccess.Read(_handle, destination[total..], offset + total);
                if (read == 0)
                {
                    return false;
                }

                total += read;
            }

            return true;
        }

        public void Dispose() => _handle.Dispose();
    }
}
