using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Backup;

/// <summary>Thrown by <see cref="CrashModelFileSystem"/> in place of the operation a simulated power cut interrupts.</summary>
internal sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException(int operation) : base($"Simulated crash before file-system operation #{operation}.")
    {
    }
}

/// <summary>
/// An <see cref="IStoreFileSystem"/> held in memory. File content is a sparse map of 64 KiB chunks (below the large-object heap, so a test
/// that writes thousands of files does not spend its time in gen-2 collections), so a test can write at offsets past 4 GiB without a disk.
/// Paths are plain strings; both separators are accepted and compared ordinally.
/// </summary>
/// <remarks>
/// Sharing is modelled the way the real one behaves where the code depends on it: a file created or locked exclusively refuses every other open,
/// rename and delete; a file opened for reading may be deleted (<c>FileShare.Delete</c>), and its open handles keep reading the old content; a
/// directory whose files are open cannot be renamed (NTFS).
/// </remarks>
internal class InMemorySparseFileSystem : IStoreFileSystem
{
    internal const int ChunkSize = 64 << 10;

    protected readonly HashSet<string> Directories = new(StringComparer.Ordinal);
    protected readonly Dictionary<string, Node> Files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _heldBundles = new(StringComparer.Ordinal);
    private int _generation;

    /// <summary>A file's content, shared by its directory entry and every handle opened on it.</summary>
    internal sealed class Node
    {
        public Dictionary<long, byte[]> Chunks = new();
        public long Length;
        public Dictionary<long, byte[]> DurableChunks = new();
        public long DurableLength;
        public int OpenHandles;
        public bool Exclusive;

        public static Dictionary<long, byte[]> Clone(Dictionary<long, byte[]> source)
        {
            var copy = new Dictionary<long, byte[]>(source.Count);
            foreach (var kv in source)
            {
                copy[kv.Key] = (byte[])kv.Value.Clone();
            }

            return copy;
        }
    }

    /// <summary>Normalises a path to forward slashes with no trailing separator.</summary>
    internal static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        while (p.Length > 1 && p.EndsWith('/'))
        {
            p = p[..^1];
        }

        return p;
    }

    internal static string ParentOf(string path)
    {
        var p = Normalize(path);
        var slash = p.LastIndexOf('/');
        return slash <= 0 ? string.Empty : p[..slash];
    }

    /// <summary>Marks a bundle as held by a live process, for <see cref="IsBundleHeldByLiveProcess"/>.</summary>
    public void MarkBundleHeld(string bundleDirectory, bool held)
    {
        if (held)
        {
            _heldBundles.Add(Normalize(bundleDirectory));
        }
        else
        {
            _heldBundles.Remove(Normalize(bundleDirectory));
        }
    }

    /// <summary>A file's length and CRC32C, read in chunks (no whole-file copy).</summary>
    public (long Length, uint Crc) Fingerprint(string path)
    {
        var node = Files[Normalize(path)];
        var crc = Crc32CUtil.Start;
        var buffer = new byte[ChunkSize];
        for (long pos = 0; pos < node.Length; pos += ChunkSize)
        {
            var n = (int)Math.Min(ChunkSize, node.Length - pos);
            ReadNode(node, buffer.AsSpan(0, n), pos);
            crc = Crc32CUtil.Append(crc, buffer.AsSpan(0, n));
        }

        return (node.Length, Crc32CUtil.Finish(crc));
    }

    /// <summary>The whole content of a file, for assertions.</summary>
    public byte[] ReadAll(string path)
    {
        var node = Files[Normalize(path)];
        var bytes = new byte[node.Length];
        ReadNode(node, bytes, 0);
        return bytes;
    }

    /// <summary>Overwrites bytes of an existing file in place, bypassing every handle, for damage tests.</summary>
    public void Poke(string path, long offset, ReadOnlySpan<byte> bytes) => WriteNode(Files[Normalize(path)], bytes, offset);

    /// <summary>Truncates or extends an existing file in place, for damage tests.</summary>
    public void Resize(string path, long length) => SetNodeLength(Files[Normalize(path)], length);

    /// <summary>Every file path currently visible.</summary>
    public IReadOnlyCollection<string> AllFiles => Files.Keys;

    /// <summary>Every directory path currently visible.</summary>
    public IReadOnlyCollection<string> AllDirectories => Directories;

    /// <summary>Called before every mutating operation; the crash model counts them here.</summary>
    protected virtual void BeforeMutation(string what)
    {
    }

    /// <summary>Called after a rename; the crash model commits the namespace here (Windows write-through).</summary>
    protected virtual void AfterRename()
    {
    }

    /// <summary>Called when a directory is renamed, before <see cref="AfterRename"/>: its subtree moves with it, atomically.</summary>
    protected virtual void OnRenameDirectory(string source, string destination)
    {
    }

    /// <summary>Called by <see cref="FlushDirectory"/>; the crash model commits the directory's entries here (Linux).</summary>
    protected virtual void OnFlushDirectory(string directory)
    {
    }

    /// <summary>Invalidates every open handle (a crash).</summary>
    protected void InvalidateHandles()
    {
        _generation++;
        foreach (var node in Files.Values)
        {
            node.OpenHandles = 0;
            node.Exclusive = false;
        }
    }

    public IStoreFile OpenCreateNew(string path, long preallocationSize = 0)
    {
        var p = Normalize(path);
        BeforeMutation($"create {p}");
        RequireParent(p);
        if (Files.ContainsKey(p) || Directories.Contains(p))
        {
            throw new IOException($"The file '{p}' already exists.");
        }

        var node = new Node { OpenHandles = 1, Exclusive = true };
        Files[p] = node;
        return new Handle(this, p, node, writable: true, _generation);
    }

    public IStoreFile OpenRead(string path)
    {
        var p = Normalize(path);
        if (!Files.TryGetValue(p, out var node))
        {
            throw new FileNotFoundException($"Could not find file '{p}'.", p);
        }

        if (node.Exclusive)
        {
            throw new IOException($"The process cannot access the file '{p}' because it is being used by another process.");
        }

        node.OpenHandles++;
        return new Handle(this, p, node, writable: false, _generation);
    }

    public IDisposable TryLockExclusive(string path)
    {
        var p = Normalize(path);
        RequireParent(p);
        if (!Files.TryGetValue(p, out var node))
        {
            BeforeMutation($"create {p}");
            node = new Node();
            Files[p] = node;
        }

        if (node.OpenHandles > 0)
        {
            return null;
        }

        node.OpenHandles = 1;
        node.Exclusive = true;
        return new Handle(this, p, node, writable: true, _generation);
    }

    public bool ProbeExclusive(string path) => !Files.TryGetValue(Normalize(path), out var node) || node.OpenHandles == 0;

    public void Rename(string source, string destination, bool replace)
    {
        var src = Normalize(source);
        var dst = Normalize(destination);
        BeforeMutation($"rename {src} -> {dst}");
        RequireParent(dst);
        if (Directories.Contains(src))
        {
            if (Directories.Contains(dst) || Files.ContainsKey(dst))
            {
                throw new IOException($"Cannot move '{src}': '{dst}' already exists.");
            }

            var prefix = src + "/";
            foreach (var kv in Files)
            {
                if (kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Value.OpenHandles > 0)
                {
                    throw new IOException($"Cannot move '{src}': '{kv.Key}' is open.");
                }
            }

            var movedDirs = new List<string>();
            foreach (var d in Directories)
            {
                if (d == src || d.StartsWith(prefix, StringComparison.Ordinal))
                {
                    movedDirs.Add(d);
                }
            }

            foreach (var d in movedDirs)
            {
                Directories.Remove(d);
                Directories.Add(dst + d[src.Length..]);
            }

            var movedFiles = new List<KeyValuePair<string, Node>>();
            foreach (var kv in Files)
            {
                if (kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    movedFiles.Add(kv);
                }
            }

            foreach (var kv in movedFiles)
            {
                Files.Remove(kv.Key);
                Files[dst + kv.Key[src.Length..]] = kv.Value;
            }

            OnRenameDirectory(src, dst);
            AfterRename();
            return;
        }

        if (!Files.TryGetValue(src, out var node))
        {
            throw new FileNotFoundException($"Could not find file '{src}'.", src);
        }

        if (node.Exclusive)
        {
            throw new IOException($"Cannot move '{src}': it is open exclusively.");
        }

        if (Directories.Contains(dst) || (Files.ContainsKey(dst) && !replace))
        {
            throw new IOException($"Cannot move '{src}': '{dst}' already exists.");
        }

        Files.Remove(src);
        Files[dst] = node;
        AfterRename();
    }

    public void FlushDirectory(string directory)
    {
        var d = Normalize(directory);
        BeforeMutation($"flushdir {d}");
        OnFlushDirectory(d);
    }

    public bool FileExists(string path) => Files.ContainsKey(Normalize(path));

    public bool DirectoryExists(string path) => Directories.Contains(Normalize(path));

    public void CreateDirectory(string path)
    {
        var p = Normalize(path);
        if (Directories.Contains(p))
        {
            return;
        }

        var parent = ParentOf(p);
        if (parent.Length > 0 && !Directories.Contains(parent))
        {
            CreateDirectory(parent);
        }

        BeforeMutation($"mkdir {p}");
        Directories.Add(p);
    }

    public void DeleteFile(string path)
    {
        var p = Normalize(path);
        if (!Files.TryGetValue(p, out var node))
        {
            return;
        }

        if (node.Exclusive)
        {
            throw new IOException($"Cannot delete '{p}': it is open exclusively.");
        }

        BeforeMutation($"delete {p}");
        Files.Remove(p);
    }

    public void DeleteDirectory(string path)
    {
        var p = Normalize(path);
        if (!Directories.Contains(p))
        {
            return;
        }

        BeforeMutation($"rmdir {p}");
        var prefix = p + "/";
        var files = new List<string>();
        foreach (var kv in Files)
        {
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (kv.Value.Exclusive)
                {
                    throw new IOException($"Cannot delete '{p}': '{kv.Key}' is open exclusively.");
                }

                files.Add(kv.Key);
            }
        }

        foreach (var f in files)
        {
            Files.Remove(f);
        }

        Directories.RemoveWhere(d => d == p || d.StartsWith(prefix, StringComparison.Ordinal));
    }

    public IReadOnlyList<StoreFileEntry> EnumerateFiles(string directory)
    {
        var d = Normalize(directory);
        var list = new List<StoreFileEntry>();
        foreach (var kv in Files)
        {
            if (ParentOf(kv.Key) == d)
            {
                list.Add(new StoreFileEntry(kv.Key[(d.Length + 1)..], kv.Value.Length));
            }
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    public IReadOnlyList<string> EnumerateDirectories(string directory)
    {
        var d = Normalize(directory);
        var list = new List<string>();
        foreach (var dir in Directories)
        {
            if (ParentOf(dir) == d && dir.Length > d.Length + 1)
            {
                list.Add(dir[(d.Length + 1)..]);
            }
        }

        list.Sort(StringComparer.Ordinal);
        return list;
    }

    public bool IsBundleHeldByLiveProcess(string bundleDirectory) => _heldBundles.Contains(Normalize(bundleDirectory));

    /// <summary>Rooted paths ("/r/…") are already full; a relative one is rooted under <c>/cwd</c>, as the OS roots it under the current directory.</summary>
    public string GetFullPath(string path) => Normalize(path.StartsWith('/') || path.StartsWith('\\') ? path : "/cwd/" + path);

    /// <summary>Bundles whose advisory lock this file system's restore took (the lock moves with the directory; only its holding is modelled).</summary>
    public HashSet<string> AcquiredBundleLocks { get; } = new(StringComparer.Ordinal);

    public bool TryAcquireBundleLock(string bundleDirectory, out DatabaseLockFile.LockInfo acquired)
    {
        acquired = new DatabaseLockFile.LockInfo(Environment.ProcessId, Environment.MachineName, DateTimeOffset.UtcNow, false, null);
        if (_heldBundles.Contains(Normalize(bundleDirectory)))
        {
            return false;
        }

        AcquiredBundleLocks.Add(Normalize(bundleDirectory));
        return true;
    }

    public void ReleaseBundleLock(string bundleDirectory, in DatabaseLockFile.LockInfo acquired) => AcquiredBundleLocks.Remove(Normalize(bundleDirectory));

    private void RequireParent(string path)
    {
        var parent = ParentOf(path);
        if (parent.Length > 0 && !Directories.Contains(parent))
        {
            throw new DirectoryNotFoundException($"Could not find a part of the path '{path}'.");
        }
    }

    private static void ReadNode(Node node, Span<byte> destination, long offset)
    {
        var done = 0;
        while (done < destination.Length)
        {
            var pos = offset + done;
            var chunk = pos / ChunkSize;
            var inChunk = (int)(pos % ChunkSize);
            var n = Math.Min(destination.Length - done, ChunkSize - inChunk);
            if (node.Chunks.TryGetValue(chunk, out var bytes))
            {
                bytes.AsSpan(inChunk, n).CopyTo(destination.Slice(done, n));
            }
            else
            {
                destination.Slice(done, n).Clear();
            }

            done += n;
        }
    }

    private static void WriteNode(Node node, ReadOnlySpan<byte> source, long offset)
    {
        var done = 0;
        while (done < source.Length)
        {
            var pos = offset + done;
            var chunk = pos / ChunkSize;
            var inChunk = (int)(pos % ChunkSize);
            var n = Math.Min(source.Length - done, ChunkSize - inChunk);
            if (!node.Chunks.TryGetValue(chunk, out var bytes))
            {
                bytes = new byte[ChunkSize];
                node.Chunks[chunk] = bytes;
            }

            source.Slice(done, n).CopyTo(bytes.AsSpan(inChunk, n));
            done += n;
        }

        node.Length = Math.Max(node.Length, offset + source.Length);
    }

    private static void SetNodeLength(Node node, long length)
    {
        if (length < node.Length)
        {
            var lastChunk = length / ChunkSize;
            var keys = new List<long>(node.Chunks.Keys);
            foreach (var k in keys)
            {
                if (k > lastChunk || (k == lastChunk && length % ChunkSize == 0))
                {
                    node.Chunks.Remove(k);
                }
            }

            if (length % ChunkSize != 0 && node.Chunks.TryGetValue(lastChunk, out var tail))
            {
                tail.AsSpan((int)(length % ChunkSize)).Clear();
            }
        }

        node.Length = length;
    }

    private sealed class Handle : IStoreFile
    {
        private readonly InMemorySparseFileSystem _fs;
        private readonly Node _node;
        private readonly bool _writable;
        private readonly int _generation;
        private bool _disposed;

        public Handle(InMemorySparseFileSystem fs, string path, Node node, bool writable, int generation)
        {
            _fs = fs;
            Path = path;
            _node = node;
            _writable = writable;
            _generation = generation;
        }

        public string Path { get; }

        public long Length
        {
            get
            {
                Check();
                return _node.Length;
            }
        }

        public int Read(Span<byte> destination, long offset)
        {
            Check();
            if (offset >= _node.Length)
            {
                return 0;
            }

            var n = (int)Math.Min(destination.Length, _node.Length - offset);
            ReadNode(_node, destination[..n], offset);
            return n;
        }

        public void Write(ReadOnlySpan<byte> source, long offset)
        {
            CheckWritable();
            _fs.BeforeMutation($"write {Path} @{offset} +{source.Length}");
            WriteNode(_node, source, offset);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> source, long offset)
        {
            Write(source.Span, offset);
            return Task.CompletedTask;
        }

        public void SetLength(long length)
        {
            CheckWritable();
            _fs.BeforeMutation($"setlength {Path} {length}");
            SetNodeLength(_node, length);
        }

        public void Flush()
        {
            CheckWritable();
            _fs.BeforeMutation($"flush {Path}");
            _node.DurableChunks = Node.Clone(_node.Chunks);
            _node.DurableLength = _node.Length;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_generation != _fs._generation)
            {
                return;
            }

            _node.OpenHandles--;
            if (_node.OpenHandles <= 0)
            {
                _node.OpenHandles = 0;
                _node.Exclusive = false;
            }
        }

        private void Check()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_generation != _fs._generation)
            {
                throw new IOException($"Handle on '{Path}' did not survive the crash.");
            }
        }

        private void CheckWritable()
        {
            Check();
            if (!_writable)
            {
                throw new UnauthorizedAccessException($"'{Path}' was opened read-only.");
            }
        }
    }
}

/// <summary>Which operating system's directory durability <see cref="CrashModelFileSystem"/> models.</summary>
internal enum CrashModelKind
{
    /// <summary>NTFS: a write-through rename flushes the metadata log, making every earlier namespace change durable with it.</summary>
    Windows,

    /// <summary>POSIX: a directory's entries are durable only once that directory is fsynced.</summary>
    Linux,
}

/// <summary>
/// <see cref="InMemorySparseFileSystem"/> plus durability: file content is durable only up to its last <see cref="IStoreFile.Flush"/>, and
/// directory entries only after a write-through rename (<see cref="CrashModelKind.Windows"/>) or a flush of their directory
/// (<see cref="CrashModelKind.Linux"/>). <see cref="Crash"/> reverts everything that is not durable; <see cref="CrashBefore"/> arms a crash
/// in place of the k-th mutating operation.
/// </summary>
internal sealed class CrashModelFileSystem : InMemorySparseFileSystem
{
    private readonly CrashModelKind _kind;
    private readonly List<(string Source, string Destination)> _pendingDirectoryRenames = [];
    private HashSet<string> _durableDirectories = new(StringComparer.Ordinal);
    private Dictionary<string, Node> _durableFiles = new(StringComparer.Ordinal);
    private int _crashAt = -1;

    public CrashModelFileSystem(CrashModelKind kind) => _kind = kind;

    /// <summary>A directory whose next flush commits its entries and then throws, once: the flush that "failed" after reaching the disk.</summary>
    public string FailNextFlushAfterCommit { get; set; }

    /// <summary>Mutating operations performed so far.</summary>
    public int Operations { get; private set; }

    /// <summary>The log of mutating operations, for diagnosing a failure.</summary>
    public List<string> Log { get; } = [];

    /// <summary>Makes everything that exists now durable, as after a clean sync of the whole volume. For test setup.</summary>
    public void SyncAll()
    {
        foreach (var node in Files.Values)
        {
            node.DurableChunks = Node.Clone(node.Chunks);
            node.DurableLength = node.Length;
        }

        CommitAll();
    }

    /// <summary>Arms a crash: operation number <paramref name="operation"/> (0-based, counted from now) throws instead of running.</summary>
    public void CrashBefore(int operation)
    {
        Operations = 0;
        Log.Clear();
        _crashAt = operation;
    }

    /// <summary>The power cut: every handle dies, and the namespace and every file's content fall back to what was durable.</summary>
    public void Crash()
    {
        _crashAt = -1;
        _pendingDirectoryRenames.Clear();
        InvalidateHandles();
        Directories.Clear();
        foreach (var d in _durableDirectories)
        {
            // An entry whose parent directory never became durable went with it.
            if (IsReachable(d))
            {
                Directories.Add(d);
            }
        }

        Files.Clear();
        foreach (var kv in _durableFiles)
        {
            var parent = ParentOf(kv.Key);
            if (parent.Length > 0 && !Directories.Contains(parent))
            {
                continue;
            }

            var node = kv.Value;
            node.Chunks = Node.Clone(node.DurableChunks);
            node.Length = node.DurableLength;
            node.OpenHandles = 0;
            node.Exclusive = false;
            Files[kv.Key] = node;
        }
    }

    protected override void BeforeMutation(string what)
    {
        if (_crashAt >= 0 && Operations == _crashAt)
        {
            throw new SimulatedCrashException(Operations);
        }

        Operations++;
        Log.Add(what);
    }

    protected override void AfterRename()
    {
        if (_kind == CrashModelKind.Windows)
        {
            CommitAll();
        }
    }

    protected override void OnRenameDirectory(string source, string destination)
    {
        if (_kind == CrashModelKind.Linux)
        {
            _pendingDirectoryRenames.Add((source, destination));
        }
    }

    protected override void OnFlushDirectory(string directory)
    {
        if (_kind != CrashModelKind.Linux)
        {
            return;
        }

        // A directory renamed into this one carries its durable subtree with it: on POSIX the entries live in the directory itself, so the
        // rename moves them all at once, and it is this directory's flush that makes the new name durable.
        for (var i = 0; i < _pendingDirectoryRenames.Count; i++)
        {
            var (src, dst) = _pendingDirectoryRenames[i];
            if (ParentOf(dst) != directory)
            {
                continue;
            }

            var prefix = src + "/";
            var dirs = new List<string>();
            foreach (var d in _durableDirectories)
            {
                if (d == src || d.StartsWith(prefix, StringComparison.Ordinal))
                {
                    dirs.Add(d);
                }
            }

            foreach (var d in dirs)
            {
                _durableDirectories.Remove(d);
                _durableDirectories.Add(dst + d[src.Length..]);
            }

            var files = new List<KeyValuePair<string, Node>>();
            foreach (var kv in _durableFiles)
            {
                if (kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    files.Add(kv);
                }
            }

            foreach (var kv in files)
            {
                _durableFiles.Remove(kv.Key);
                _durableFiles[dst + kv.Key[src.Length..]] = kv.Value;
            }

            _pendingDirectoryRenames.RemoveAt(i--);
        }

        _durableDirectories.RemoveWhere(d => ParentOf(d) == directory && !Directories.Contains(d));
        foreach (var d in Directories)
        {
            if (ParentOf(d) == directory)
            {
                _durableDirectories.Add(d);
            }
        }

        var gone = new List<string>();
        foreach (var kv in _durableFiles)
        {
            if (ParentOf(kv.Key) == directory && (!Files.TryGetValue(kv.Key, out var now) || !ReferenceEquals(now, kv.Value)))
            {
                gone.Add(kv.Key);
            }
        }

        foreach (var g in gone)
        {
            _durableFiles.Remove(g);
        }

        foreach (var kv in Files)
        {
            if (ParentOf(kv.Key) == directory)
            {
                _durableFiles[kv.Key] = kv.Value;
            }
        }

        if (FailNextFlushAfterCommit != null && Normalize(FailNextFlushAfterCommit) == directory)
        {
            FailNextFlushAfterCommit = null;
            throw new IOException($"fsync of '{directory}' failed (after its entries reached the disk)");
        }
    }

    private bool IsReachable(string directory)
    {
        for (var p = ParentOf(directory); p.Length > 0; p = ParentOf(p))
        {
            if (!_durableDirectories.Contains(p))
            {
                // A root the test never created as a directory (e.g. "M:") counts as the volume itself.
                return ParentOf(p).Length == 0;
            }
        }

        return true;
    }

    private void CommitAll()
    {
        _pendingDirectoryRenames.Clear();
        _durableDirectories = new HashSet<string>(Directories, StringComparer.Ordinal);
        _durableFiles = new Dictionary<string, Node>(Files, StringComparer.Ordinal);
    }
}
