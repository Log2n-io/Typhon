using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>The paths of a store directory (plan 1413 §2.5).</summary>
internal readonly struct StorePaths
{
    public StorePaths(string root)
    {
        Root = root;
        Packs = Path.Combine(root, "packs");
        Identity = Path.Combine(root, "store.json");
        Lock = Path.Combine(root, "store.lock");
        LockHolder = Path.Combine(root, "store.lock.json");
        HighWater = Path.Combine(root, "store.hwm");
        Catalog = Path.Combine(root, StoreCatalog.FileName);
    }

    public string Root { get; }

    public string Packs { get; }

    public string Identity { get; }

    public string Lock { get; }

    public string LockHolder { get; }

    public string HighWater { get; }

    public string Catalog { get; }

    public string PackPath(string fileName) => Path.Combine(Packs, fileName);
}

/// <summary>
/// A read-only view of a store: its identity and its packs as the catalog lists them. Readers take no lock; they use <c>catalog.json</c> only
/// when its fingerprint matches <c>packs/</c>, else rebuild it in memory from the headers (DC-10).
/// </summary>
internal sealed class StoreView
{
    public StorePaths Paths { get; init; }

    public StoreIdentityFile Identity { get; init; }

    private StoreCatalogFile _catalog;
    private Dictionary<uint, (CatalogPack Compacted, CatalogPack Original)> _byId;

    public StoreCatalogFile Catalog
    {
        get => _catalog;
        set
        {
            _catalog = value;
            _byId = null;
            Resolved.Clear();
        }
    }

    /// <summary>Chains resolved against this catalog by <see cref="ChainResolver"/>, memoised per point; cleared when the catalog changes.</summary>
    internal Dictionary<uint, ChainResolution> Resolved { get; } = [];

    /// <summary>Temporary files present in <c>packs/</c> (in-progress or abandoned).</summary>
    public IReadOnlyList<StoreFileEntry> Temps { get; init; }

    /// <summary>Whether <see cref="Catalog"/> came from <c>catalog.json</c> (true) or a rebuild (false).</summary>
    public bool CatalogFromFile { get; init; }

    /// <summary>
    /// Opens a store for reading. Throws when the directory is not a store or its identity is unreadable or newer. With
    /// <paramref name="trustCatalog"/> false the catalog is always rebuilt from the packs: verify reads what the packs say, never a cache that a
    /// same-size in-place change would leave matching.
    /// </summary>
    public static StoreView Open(string root, IStoreFileSystem fs = null, ILogger log = null, bool trustCatalog = true)
    {
        fs ??= StoreFileSystem.Instance;
        log ??= NullLogger.Instance;
        var paths = new StorePaths(root);
        var identity = ReadIdentity(fs, paths) ?? throw new DirectoryNotFoundException($"'{root}' is not a backup store: it has no store.json.");
        var listing = fs.EnumerateFiles(paths.Packs);
        var fingerprint = StoreCatalog.Fingerprint(listing);
        StoreCatalogFile catalog = null;
        if (trustCatalog && fs.FileExists(paths.Catalog))
        {
            try
            {
                catalog = StoreCatalog.TryDeserialize(StoreFileSystem.ReadAllBytes(fs, paths.Catalog));
            }
            catch (IOException)
            {
                catalog = null;
            }
        }

        var fromFile = catalog != null && catalog.Fingerprint == fingerprint && catalog.StoreId == identity.StoreId;
        if (!fromFile)
        {
            BackupLog.CatalogRebuilt(log, root, catalog == null ? "missing or unreadable" : "stale");
            catalog = Rebuild(fs, paths, identity, listing, catalog?.HighWaterBackupId ?? 0, log);
        }

        return new StoreView { Paths = paths, Identity = identity, Catalog = catalog, Temps = TempsOf(listing), CatalogFromFile = fromFile };
    }

    /// <summary>Reads <c>store.json</c>; <c>null</c> when absent. Throws for an unreadable or newer one.</summary>
    public static StoreIdentityFile ReadIdentity(IStoreFileSystem fs, in StorePaths paths)
    {
        if (!fs.FileExists(paths.Identity))
        {
            return null;
        }

        StoreIdentityFile identity;
        try
        {
            identity = JsonSerializer.Deserialize(StoreFileSystem.ReadAllBytes(fs, paths.Identity), BackupJsonContext.Default.StoreIdentityFile);
        }
        catch (JsonException ex)
        {
            throw new BackupPackCorruptException(paths.Identity, "StoreIdentity", -1, "store.json is unreadable: " + ex.Message);
        }

        if (identity == null || identity.FormatVersion <= 0 || identity.StoreId == Guid.Empty)
        {
            throw new BackupPackCorruptException(paths.Identity, "StoreIdentity", -1, "store.json is incomplete");
        }

        if (identity.FormatVersion > StoreIdentityFile.CurrentFormatVersion)
        {
            throw new BackupFormatUnsupportedException(paths.Identity, "FormatVersion", identity.FormatVersion, StoreIdentityFile.CurrentFormatVersion);
        }

        return identity;
    }

    /// <summary>The catalog of what <c>packs/</c> holds, from every pack's header and footer (384 B each).</summary>
    public static StoreCatalogFile Rebuild(IStoreFileSystem fs, in StorePaths paths, StoreIdentityFile identity, IReadOnlyList<StoreFileEntry> listing,
        uint highWater, ILogger log)
    {
        var packs = new List<CatalogPack>();
        var unreadable = new List<CatalogUnreadable>();
        foreach (var f in listing)
        {
            if (!StoreCatalog.IsPublishedPack(f.Name))
            {
                continue;
            }

            try
            {
                using var reader = PackReader.Open(fs, paths.PackPath(f.Name));
                PackFormat.TryParseFileName(f.Name, out var id, out var kind, out _);
                if (reader.Header.BackupId != id || reader.Header.Kind != kind)
                {
                    PackFormat.ThrowCorrupt(f.Name, "Header", -1, $"names point #{reader.Header.BackupId} ({reader.Header.Kind}), not its file name");
                }

                packs.Add(CatalogPack.From(f.Name, f.Length, reader.Header, reader.Footer));
            }
            catch (FileNotFoundException)
            {
                // Deleted since the listing (retention, under another process's store.lock): no longer a pack of the store, not an unreadable one (DL-11).
            }
            catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException)
            {
                BackupLog.PackUnreadable(log, paths.Root, f.Name, ex.Message);
                unreadable.Add(new CatalogUnreadable { File = f.Name, Size = f.Length, Reason = ex.Message });
            }
        }

        return StoreCatalog.Build(identity, StoreCatalog.Fingerprint(listing), highWater, packs, unreadable);
    }

    public static List<StoreFileEntry> TempsOf(IReadOnlyList<StoreFileEntry> listing)
    {
        var temps = new List<StoreFileEntry>();
        foreach (var f in listing)
        {
            if (PackFormat.TryParseFileName(f.Name, out _, out _, out var temp) && temp)
            {
                temps.Add(f);
            }
        }

        return temps;
    }

    /// <summary>
    /// The packs of point <paramref name="backupId"/>: the <c>-C</c> one first when present, then the <c>F</c>/<c>I</c> one. Indexed once per catalog:
    /// a chain walk calls this per link, and a scan of every pack per call made verify and status cubic in the store's points.
    /// </summary>
    public (CatalogPack Compacted, CatalogPack Original) Find(uint backupId)
    {
        if (_byId == null)
        {
            var byId = new Dictionary<uint, (CatalogPack Compacted, CatalogPack Original)>(Catalog.Packs.Count);
            foreach (var p in Catalog.Packs)
            {
                byId.TryGetValue(p.BackupId, out var both);
                byId[p.BackupId] = p.Kind == BackupKind.Compacted ? (p, both.Original) : (both.Compacted, p);
            }

            _byId = byId;
        }

        return _byId.TryGetValue(backupId, out var found) ? found : (null, null);
    }
}

/// <summary>The id high-water: <c>store.hwm</c>, 16 B {magic, version, reserved, highWater, crc32c} (DC-6).</summary>
internal static class StoreHighWater
{
    private const uint Magic = 0x57484254; // "TBHW"
    private const ushort Version = 1;
    public const int Size = 16;

    /// <summary>The recorded high-water, or <c>null</c> when the file is missing or damaged (a degraded guarantee).</summary>
    public static uint? Read(IStoreFileSystem fs, string path, out string problem)
    {
        problem = null;
        if (!fs.FileExists(path))
        {
            problem = "missing";
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = StoreFileSystem.ReadAllBytes(fs, path);
        }
        catch (IOException ex)
        {
            problem = ex.Message;
            return null;
        }

        if (bytes.Length != Size || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != Version
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)) != Crc32CUtil.Compute(bytes.AsSpan(0, 12)))
        {
            problem = "damaged";
            return null;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
    }

    /// <summary>Records <paramref name="highWater"/> durably (temp, flush, durable replace).</summary>
    public static void Write(IStoreFileSystem fs, string path, uint highWater)
    {
        Span<byte> bytes = stackalloc byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], highWater);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], Crc32CUtil.Compute(bytes[..12]));
        StoreFileSystem.WriteAllBytesDurable(fs, path, bytes);
    }
}

/// <summary>The configured destination of backup points: a store directory (the capture's <see cref="IBackupPointStore"/>).</summary>
internal sealed class FileSystemBackupStore : IBackupPointStore
{
    private readonly string _root;
    private readonly IStoreFileSystem _fs;
    private readonly ILogger _log;
    private readonly PackWriterOptions _writerOptions;

    public FileSystemBackupStore(string root, IStoreFileSystem fs = null, ILogger log = null, PackWriterOptions writerOptions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        _root = root;
        _fs = fs;
        _log = log;
        _writerOptions = writerOptions;
    }

    /// <summary>The last point a session of this store published, as the store recorded it: what the caller reports without reading the store again.</summary>
    public CatalogPack LastPublished { get; private set; }

    /// <summary>The store's directory.</summary>
    public string Root => _root;

    /// <summary>The file system the store is written through (the process's own unless a test gave one).</summary>
    public IStoreFileSystem FileSystem => _fs ?? StoreFileSystem.Instance;

    /// <summary>Where the store logs.</summary>
    public ILogger Log => _log;

    /// <inheritdoc />
    public IBackupStoreSession OpenSession(Guid databaseId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var session = BackupStore.OpenForWrite(_root, databaseId, createIfMissing: true, _fs, _log, _writerOptions);
        session.Published = pack => LastPublished = pack;
        return session;
    }
}

/// <summary>
/// A store opened for writing: <c>store.lock</c> held with no sharing for the session's whole life (DC-7), abandoned temps deleted, the catalog
/// rebuilt from the packs, ids allocated past a durable high-water (DC-6), packs published by a durable rename (BKP-08).
/// </summary>
internal sealed class BackupStore : IBackupStoreSession, IPackPublisher
{
    private readonly IStoreFileSystem _fs;
    private readonly ILogger _log;
    private readonly PackWriterOptions _writerOptions;
    private readonly StorePaths _paths;
    private readonly StoreIdentityFile _identity;
    private IDisposable _lock;
    private StoreView _view;
    private uint _highWater;
    private uint _lastAllocated;
    private PackWriter _open;
    private CancellationToken _compactionCancellation;

    /// <summary>Called with each point once it is durable and visible, before the catalog (a cache) is updated.</summary>
    internal Action<CatalogPack> Published { get; set; }

    private BackupStore(IStoreFileSystem fs, ILogger log, PackWriterOptions writerOptions, StorePaths paths, StoreIdentityFile identity,
        IDisposable storeLock)
    {
        _fs = fs;
        _log = log;
        _writerOptions = writerOptions;
        _paths = paths;
        _identity = identity;
        _lock = storeLock;
    }

    /// <inheritdoc />
    public Guid StoreId => _identity.StoreId;

    public Guid DatabaseId => _identity.DatabaseId;

    public string Root => _paths.Root;

    /// <summary>The file system the session writes through.</summary>
    internal IStoreFileSystem FileSystem => _fs;

    /// <summary>Where the session logs.</summary>
    internal ILogger Log => _log;

    /// <summary>The store as this session sees it (always rebuilt from the packs, then kept up to date by its own publishes).</summary>
    public StoreView View => _view;

    /// <summary>
    /// Opens a store for writing. Throws <see cref="BackupStoreLockedException"/> when another writer holds it,
    /// <see cref="BackupStoreMismatchException"/> when it belongs to another database.
    /// </summary>
    public static BackupStore OpenForWrite(string root, Guid databaseId, bool createIfMissing, IStoreFileSystem fs = null, ILogger log = null,
        PackWriterOptions writerOptions = null) =>
        Open(root, databaseId, createIfMissing, PurposeBackup, fs, log, writerOptions);

    /// <summary>
    /// Opens an existing store for maintenance (compaction, retention): its identity comes from <c>store.json</c>, nothing is created, and the lock
    /// holder is named <paramref name="purpose"/> in another writer's refusal (DL-10). Throws as <see cref="OpenForWrite"/>.
    /// </summary>
    public static BackupStore OpenForMaintenance(string root, string purpose, IStoreFileSystem fs = null, ILogger log = null,
        PackWriterOptions writerOptions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        return Open(root, Guid.Empty, createIfMissing: false, purpose, fs, log, writerOptions);
    }

    /// <summary>The lock holder's purpose for a capture.</summary>
    public const string PurposeBackup = "backup";

    /// <summary><paramref name="databaseId"/> empty: maintenance, the store's own identity is taken as it is.</summary>
    private static BackupStore Open(string root, Guid databaseId, bool createIfMissing, string purpose, IStoreFileSystem fs, ILogger log,
        PackWriterOptions writerOptions)
    {
        fs ??= StoreFileSystem.Instance;
        log ??= NullLogger.Instance;
        var paths = new StorePaths(root);
        if (!fs.DirectoryExists(root))
        {
            if (!createIfMissing)
            {
                throw new DirectoryNotFoundException($"No backup store at '{root}'.");
            }

            fs.CreateDirectory(root);
            var parent = Path.GetDirectoryName(root);
            if (!string.IsNullOrEmpty(parent))
            {
                fs.FlushDirectory(parent);
            }
        }

        var storeLock = fs.TryLockExclusive(paths.Lock);
        if (storeLock == null)
        {
            var holder = TryReadHolder(fs, paths);
            throw new BackupStoreLockedException(root, holder?.Pid ?? 0, holder?.Machine,
                holder == null ? DateTimeOffset.MinValue : new DateTimeOffset(holder.StartedUtc, TimeSpan.Zero), holder?.Purpose);
        }

        try
        {
            // R3: a lock the OS does not enforce (DOTNET_SYSTEM_IO_DISABLEFILELOCKING, a file system without locking) would let two writers
            // interleave. A second exclusive open in this very process must fail; if it does not, nothing is excluded.
            using (var probe = fs.TryLockExclusive(paths.Lock))
            {
                if (probe != null)
                {
                    throw new NotSupportedException(
                        $"File locking is not enforced for '{paths.Lock}' (DOTNET_SYSTEM_IO_DISABLEFILELOCKING set, or a file system without locks): "
                        + "the backup store cannot exclude a second writer, so it refuses to write.");
                }
            }

            WriteHolder(fs, paths, purpose);
            var identity = databaseId == Guid.Empty
                ? StoreView.ReadIdentity(fs, paths) ?? throw new DirectoryNotFoundException($"'{root}' is not a backup store: it has no store.json.")
                : OpenOrCreateIdentity(fs, paths, databaseId, createIfMissing);
            var store = new BackupStore(fs, log, writerOptions, paths, identity, storeLock);
            storeLock = null;
            store.Initialize();
            return store;
        }
        finally
        {
            storeLock?.Dispose();
        }
    }

    private static StoreIdentityFile OpenOrCreateIdentity(IStoreFileSystem fs, in StorePaths paths, Guid databaseId, bool createIfMissing)
    {
        var identity = StoreView.ReadIdentity(fs, paths);
        if (identity != null)
        {
            if (identity.DatabaseId != databaseId)
            {
                throw new BackupStoreMismatchException(paths.Root, identity.DatabaseId, databaseId);
            }

            return identity;
        }

        foreach (var f in fs.EnumerateFiles(paths.Packs))
        {
            if (StoreCatalog.IsPublishedPack(f.Name))
            {
                throw new BackupStoreMismatchException(paths.Root, Guid.Empty, databaseId,
                    "Its store.json is missing while packs/ holds packs: restore store.json from a copy of the store.");
            }
        }

        if (!createIfMissing)
        {
            throw new DirectoryNotFoundException($"'{paths.Root}' is not a backup store: it has no store.json.");
        }

        identity = new StoreIdentityFile
        {
            FormatVersion = StoreIdentityFile.CurrentFormatVersion,
            StoreId = Guid.NewGuid(),
            DatabaseId = databaseId,
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = $"Typhon {typeof(BackupStore).Assembly.GetName().Version}",
        };
        StoreFileSystem.WriteAllBytesDurable(fs, paths.Identity,
            JsonSerializer.SerializeToUtf8Bytes(identity, BackupJsonContext.Default.StoreIdentityFile));
        return identity;
    }

    private void Initialize()
    {
        if (!_fs.DirectoryExists(_paths.Packs))
        {
            _fs.CreateDirectory(_paths.Packs);
            _fs.FlushDirectory(_paths.Root);
        }

        DeleteAbandonedTemps();
        var hwm = StoreHighWater.Read(_fs, _paths.HighWater, out var problem);
        if (hwm == null && (problem != "missing" || _fs.EnumerateFiles(_paths.Packs).Count > 0))
        {
            BackupLog.HighWaterDegraded(_log, _paths.Root, _paths.HighWater, problem);
        }

        // The floor never goes down: the catalog this store last wrote recorded the high-water too, and a lost store.hwm must not make a deleted
        // newest pack's id allocatable again. Read before the rebuild below overwrites it.
        _highWater = Math.Max(hwm ?? 0, RecordedHighWater());
        var listing = _fs.EnumerateFiles(_paths.Packs);
        _view = new StoreView
        {
            Paths = _paths,
            Identity = _identity,
            Catalog = StoreView.Rebuild(_fs, _paths, _identity, listing, _highWater, _log),
            Temps = [],
            CatalogFromFile = false,
        };
        WriteCatalogIfChanged();
    }

    /// <summary>The high-water the catalog on disk recorded, when it is this store's; 0 when there is none to trust.</summary>
    private uint RecordedHighWater()
    {
        if (!_fs.FileExists(_paths.Catalog))
        {
            return 0;
        }

        try
        {
            var catalog = StoreCatalog.TryDeserialize(StoreFileSystem.ReadAllBytes(_fs, _paths.Catalog));
            return catalog != null && catalog.StoreId == _identity.StoreId ? catalog.HighWaterBackupId : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Deletes every temp in <c>packs/</c> and at the root: only the lock holder writes, so a temp it did not create is abandoned.</summary>
    public int DeleteAbandonedTemps()
    {
        var deleted = 0;
        foreach (var f in _fs.EnumerateFiles(_paths.Packs))
        {
            if (PackFormat.TryParseFileName(f.Name, out _, out _, out var temp) && temp)
            {
                _fs.DeleteFile(_paths.PackPath(f.Name));
                deleted++;
            }
        }

        foreach (var f in _fs.EnumerateFiles(_paths.Root))
        {
            if (f.Name.EndsWith(PackFormat.TempSuffix, StringComparison.Ordinal))
            {
                _fs.DeleteFile(Path.Combine(_paths.Root, f.Name));
                deleted++;
            }
        }

        if (deleted > 0)
        {
            BackupLog.AbandonedTempsDeleted(_log, _paths.Root, deleted);
        }

        return deleted;
    }

    /// <inheritdoc />
    public bool TryGetChainHead(Guid chainId, out BackupChainHead head)
    {
        CatalogPack best = null;
        foreach (var p in _view.Catalog.Packs)
        {
            // The compacted pack of an id stands for that point: it is preferred wherever both exist (05 §2).
            if (p.ChainId == chainId && (best == null || p.BackupId > best.BackupId || (p.BackupId == best.BackupId && p.Kind == BackupKind.Compacted)))
            {
                best = p;
            }
        }

        head = best == null ? default : new BackupChainHead(best.BackupId, best.Kind, best.BaseId, best.PointLsn, best.TimelineId);
        return best != null;
    }

    /// <inheritdoc />
    public IBackupPointSink BeginPoint(in BackupPointPlan plan)
    {
        ObjectDisposedException.ThrowIf(_lock == null, this);
        if (_open is { IsOpen: true })
        {
            throw new InvalidOperationException("A point is already being written in this session.");
        }

        if (plan.DatabaseId != _identity.DatabaseId)
        {
            throw new BackupStoreMismatchException(_paths.Root, _identity.DatabaseId, plan.DatabaseId);
        }

        Guid chainId;
        uint baseId;
        switch (plan.Kind)
        {
            case BackupKind.Full:
                if (plan.ParentId != 0)
                {
                    throw new InvalidOperationException($"A full point has no parent (ParentId {plan.ParentId}).");
                }

                chainId = Guid.NewGuid();
                baseId = 0;
                break;
            case BackupKind.Incremental:
                if (!TryGetChainHead(plan.ChainId, out var head) || head.BackupId != plan.ParentId)
                {
                    throw new InvalidOperationException(
                        $"Chain {plan.ChainId}'s head is {(head.BackupId == 0 ? "absent" : $"#{head.BackupId}")}, not #{plan.ParentId}: admission saw "
                        + "another store state.");
                }

                chainId = plan.ChainId;
                baseId = head.BaseId;
                break;
            default:
                throw new InvalidOperationException($"Capture writes Full or Incremental points, not {plan.Kind}.");
        }

        var id = AllocateBackupId();
        if (plan.Kind == BackupKind.Full)
        {
            baseId = id;
        }

        var identity = new PackIdentity(plan.DatabaseId, _identity.StoreId, chainId, id, plan.ParentId, baseId, plan.Kind, plan.TimelineId,
            plan.DatabaseFormatRevision, plan.EngineVersion, plan.Offline);
        var temp = _paths.PackPath(PackFormat.FileName(id, plan.Kind) + PackFormat.TempSuffix);
        _open = new PackWriter(_fs, _fs.OpenCreateNew(temp), identity, this, _writerOptions, _log);
        return _open;
    }

    /// <summary>
    /// Starts the compacted pack of <paramref name="source"/>, an incremental point of this store (05 §2, DL-10): <c>{X}-C.tpack.tmp</c> under X's
    /// own id (none is allocated, <c>store.hwm</c> is not written), its header X's with <c>Kind = Compacted</c>, <c>ParentId = 0</c> and
    /// <c>BaseId = X</c>. The caller writes X's image and tail into it, as <see cref="BackupCompactor"/> does. Its publish verifies the sealed temp in
    /// full first and makes it visible only if that passes (BKP-13): resolution prefers a <c>-C</c>, so a bad one would break every later point.
    /// </summary>
    public PackWriter BeginCompaction(CatalogPack source, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_lock == null, this);
        ArgumentNullException.ThrowIfNull(source);
        if (_open is { IsOpen: true })
        {
            throw new InvalidOperationException("A point is already being written in this session.");
        }

        if (source.Kind != BackupKind.Incremental)
        {
            throw new InvalidOperationException($"Point #{source.BackupId} is a {source.Kind} pack: only an incremental point is compacted.");
        }

        if (source.StoreId != _identity.StoreId || source.DatabaseId != _identity.DatabaseId)
        {
            throw new BackupStoreMismatchException(_paths.Root, _identity.DatabaseId, source.DatabaseId, $"Point #{source.BackupId} is not this store's.");
        }

        if (_view.Find(source.BackupId).Compacted != null)
        {
            throw new InvalidOperationException($"Point #{source.BackupId} is already compacted.");
        }

        var identity = new PackIdentity(source.DatabaseId, source.StoreId, source.ChainId, source.BackupId, 0, source.BackupId, BackupKind.Compacted,
            source.TimelineId, source.FormatRevision, source.EngineVersion, source.Offline);
        var temp = _paths.PackPath(PackFormat.FileName(source.BackupId, BackupKind.Compacted) + PackFormat.TempSuffix);
        _compactionCancellation = ct;
        _open = new PackWriter(_fs, _fs.OpenCreateNew(temp), identity, this, _writerOptions, _log);
        return _open;
    }

    /// <summary>
    /// Deletes a published pack, durably: the delete, then a flush of <c>packs/</c>, before the caller's next delete (BKP-14: a crash keeps a prefix of a
    /// descending sequence). Retention's only way to remove a pack, under this session's lock. The view is stale until <see cref="RefreshView"/>.
    /// </summary>
    public void DeletePack(string file)
    {
        ObjectDisposedException.ThrowIf(_lock == null, this);
        if (!StoreCatalog.IsPublishedPack(file))
        {
            throw new ArgumentException($"'{file}' is not a published pack name.", nameof(file));
        }

        _fs.DeleteFile(_paths.PackPath(file));
        _fs.FlushDirectory(_paths.Packs);
    }

    /// <summary>
    /// The view after deletes: the catalog keeps the packs still listed (a pack never changes under the lock, BKP-08, so no header is read again), and
    /// is written back.
    /// </summary>
    public void RefreshView()
    {
        ObjectDisposedException.ThrowIf(_lock == null, this);
        var listing = _fs.EnumerateFiles(_paths.Packs);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in listing)
        {
            names.Add(f.Name);
        }

        var packs = new List<CatalogPack>();
        foreach (var p in _view.Catalog.Packs)
        {
            if (names.Contains(p.File))
            {
                packs.Add(p);
            }
        }

        var unreadable = new List<CatalogUnreadable>();
        foreach (var u in _view.Catalog.Unreadable)
        {
            if (names.Contains(u.File))
            {
                unreadable.Add(u);
            }
        }

        _view.Catalog = StoreCatalog.Build(_identity, StoreCatalog.Fingerprint(listing), _highWater, packs, unreadable);
        WriteCatalogIfChanged();
    }

    /// <summary>
    /// <c>max(store.hwm, every pack id, every temp id, every id this session allocated) + 1</c>, made durable in <c>store.hwm</c> before the
    /// caller creates its temp: an id is never reused, even after its pack is deleted (BKP-08, DC-6).
    /// </summary>
    public uint AllocateBackupId()
    {
        var max = Math.Max(_highWater, _lastAllocated);
        foreach (var f in _fs.EnumerateFiles(_paths.Packs))
        {
            if (PackFormat.TryParseFileName(f.Name, out var id, out _, out _) && id > max)
            {
                max = id;
            }
        }

        if (max == uint.MaxValue)
        {
            throw new InvalidOperationException($"Backup store '{_paths.Root}' has no backup id left.");
        }

        var next = max + 1;
        StoreHighWater.Write(_fs, _paths.HighWater, next);
        _highWater = next;
        _lastAllocated = next;
        return next;
    }

    /// <inheritdoc />
    public string Publish(PackWriter writer)
    {
        var final = writer.TempPath[..^PackFormat.TempSuffix.Length];
        if (writer.Header.Kind == BackupKind.Compacted)
        {
            // DL-3: verified sealed and fsynced, BEFORE the rename. A failure leaves the temp to the writer's abort: nothing was ever visible.
            VerifyCompactedTemp(writer);
        }

        try
        {
            StoreFileSystem.MoveDurable(_fs, writer.TempPath, final, replace: false);
        }
        catch
        {
            // "Throws with no point visible": a rename that went through before the directory flush failed is undone. Nothing references the id yet
            // (lineage is recorded only once Complete returns), and the id stays consumed in store.hwm.
            if (!_fs.FileExists(writer.TempPath) && _fs.FileExists(final))
            {
                try
                {
                    // Durably: a flush that failed may still have committed the rename, and an undo that is not flushed comes back at a crash.
                    _fs.DeleteFile(final);
                    _fs.FlushDirectory(Path.GetDirectoryName(final));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    BackupLog.TempNotDeleted(_log, final, ex.Message);
                }
            }

            throw;
        }

        // The pack is durable and visible: from here nothing fails the publish. The catalog is a cache.
        var published = CatalogPack.From(Path.GetFileName(final), writer.FileLength, writer.Header, writer.Footer);
        Published?.Invoke(published);
        try
        {
            var packs = new List<CatalogPack>(_view.Catalog.Packs) { published };
            _view.Catalog = StoreCatalog.Build(_identity, StoreCatalog.Fingerprint(_fs.EnumerateFiles(_paths.Packs)), _highWater, packs,
                new List<CatalogUnreadable>(_view.Catalog.Unreadable));
            WriteCatalogIfChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            BackupLog.CatalogWriteFailed(_log, _paths.Root, ex.Message);
        }

        return final;
    }

    private void VerifyCompactedTemp(PackWriter writer)
    {
        var file = Path.GetFileName(writer.TempPath);
        var result = BackupVerifier.VerifyPackFile(_fs, _view, file, VerifyLevel.Full,
            new VerifyOptions { Cancellation = _compactionCancellation, Logger = _log });
        if (result.Status == PackVerifyStatus.Ok)
        {
            return;
        }

        var first = result.Findings.Find(f => f.Severity == VerifySeverity.Damage);
        BackupLog.CompactionRejected(_log, writer.BackupId, first.Code, first.Message);
        throw new BackupPackCorruptException(writer.TempPath, "Compaction", first.PageId,
            $"the compacted pack failed its full verify before publish ({first.Code}: {first.Message}); nothing was published");
    }

    private void WriteCatalogIfChanged()
    {
        try
        {
            var bytes = StoreCatalog.Serialize(_view.Catalog);
            if (_fs.FileExists(_paths.Catalog) && StoreFileSystem.ReadAllBytes(_fs, _paths.Catalog).AsSpan().SequenceEqual(bytes))
            {
                return;
            }

            StoreFileSystem.WriteAllBytesDurable(_fs, _paths.Catalog, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BackupLog.CatalogWriteFailed(_log, _paths.Root, ex.Message);
        }
    }

    private static StoreLockHolder TryReadHolder(IStoreFileSystem fs, in StorePaths paths)
    {
        try
        {
            return fs.FileExists(paths.LockHolder)
                ? JsonSerializer.Deserialize(StoreFileSystem.ReadAllBytes(fs, paths.LockHolder), BackupJsonContext.Default.StoreLockHolder)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteHolder(IStoreFileSystem fs, in StorePaths paths, string purpose)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoreLockHolder
            {
                Pid = Environment.ProcessId,
                Machine = Environment.MachineName,
                StartedUtc = DateTime.UtcNow,
                Purpose = purpose,
            }, BackupJsonContext.Default.StoreLockHolder);
            fs.DeleteFile(paths.LockHolder);
            using var file = fs.OpenCreateNew(paths.LockHolder);
            file.Write(bytes, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: it only names the holder in another writer's refusal.
        }
    }

    /// <summary>Releases the lock. A point still being written is aborted.</summary>
    public void Dispose()
    {
        if (_lock == null)
        {
            return;
        }

        if (_open is { IsOpen: true })
        {
            _open.Abort();
        }

        try
        {
            _fs.DeleteFile(_paths.LockHolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _lock.Dispose();
        _lock = null;
    }
}
