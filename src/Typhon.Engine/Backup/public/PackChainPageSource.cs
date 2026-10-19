using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>What a pack page source does with a page that fails its check.</summary>
[PublicAPI]
public enum PackDamageMode
{
    /// <summary>Throw <see cref="BackupPackCorruptException"/>.</summary>
    Throw,

    /// <summary>Report the page as absent (<see cref="IPageSource.TryReadPage"/> returns <c>false</c>) and list it in the source's damage.</summary>
    ReportAbsent,
}

/// <summary>Options of <see cref="PackChainPageSource"/> and <see cref="PackPageSource"/>.</summary>
[PublicAPI]
public sealed class PackSourceOptions
{
    /// <summary>What a damaged page does.</summary>
    public PackDamageMode OnDamage { get; init; } = PackDamageMode.Throw;

    /// <summary>INDEX blocks (4 096 entries, 96 KiB each) kept in memory.</summary>
    public int IndexBlockCacheSize { get; init; } = 16;

    /// <summary>Read-window memory, split across the chain's packs.</summary>
    public long ReadBudgetBytes { get; init; } = 64L << 20;
}

/// <summary>A page that failed its check in a pack page source.</summary>
/// <param name="PageId">The page.</param>
/// <param name="PackFile">The pack holding the damaged frame.</param>
/// <param name="Reason">What failed.</param>
[PublicAPI]
public readonly record struct PackPageDamage(int PageId, string PackFile, string Reason);

/// <summary>
/// The image of a backup point, page by page, without materialising it (plan 1413 §2.10, 04 §3): the <see cref="IPageSource"/> through which
/// <see cref="IntegrityScanner"/> checks a point. A page no pack of the chain holds is a hole and reads as zeros, as it would in the restored file.
/// </summary>
/// <remarks>
/// <b>Single-threaded</b>, as the scanner uses it, and side-effect free (PR-2): read-only handles, no store lock. Memory: 2 B per page for the source
/// map, a fence per 4 096 INDEX entries, an LRU of INDEX blocks and one read window per pack.
/// </remarks>
[PublicAPI]
public sealed class PackChainPageSource : IPageSource
{
    private const int BlockEntries = 4096;

    private readonly PackReader[] _packs;
    private readonly FrameReader[] _frames;
    private readonly uint[][] _fences;
    private readonly ushort[] _map;
    private readonly bool _sparse;
    private readonly PackSourceOptions _options;
    private readonly string _describe;
    private readonly List<PackPageDamage> _damaged = [];
    private readonly LinkedList<(int Pack, int Block, PackPageEntry[] Entries)> _lru = new();
    private long _pagesDecoded;

    private PackChainPageSource(PackReader[] packs, bool sparse, PackSourceOptions options, string describe)
    {
        _packs = packs;
        _sparse = sparse;
        _options = options ?? new PackSourceOptions();
        _describe = describe;
        PageCount = packs[^1].Header.ImagePages;
        PointId = packs[^1].Header.BackupId;
        PointLsn = packs[^1].Footer.PointLsn;
        var files = new string[packs.Length];
        for (var i = 0; i < packs.Length; i++)
        {
            files[i] = packs[i].Path;
        }

        ChainFiles = files;
        var window = (int)Math.Clamp(_options.ReadBudgetBytes / packs.Length, 256 << 10, 64 << 20);
        _frames = new FrameReader[packs.Length];
        _fences = new uint[packs.Length][];
        for (var i = 0; i < packs.Length; i++)
        {
            _frames[i] = new FrameReader(packs[i], window);
        }

        _map = new ushort[PageCount];
        BuildMap();
    }

    /// <summary>
    /// Opens the point <paramref name="point"/> of the store at <paramref name="store"/>. Throws <see cref="BackupChainBrokenException"/> when its
    /// chain does not resolve, <see cref="BackupPackCorruptException"/> when a pack's header, footer or INDEX fails its check.
    /// </summary>
    /// <param name="store">The store directory.</param>
    /// <param name="point">Which point.</param>
    /// <param name="options">Damage handling and memory.</param>
    public static PackChainPageSource Open(string store, PointSelector point, PackSourceOptions options = null)
    {
        var view = StoreView.Open(store);
        var chain = ChainResolver.Resolve(view, ChainResolver.Select(view, point));
        return Open(StoreFileSystem.Instance, view, chain, options);
    }

    internal static PackChainPageSource Open(IStoreFileSystem fs, StoreView view, ResolvedChain chain, PackSourceOptions options = null)
    {
        var packs = OpenPacks(fs, view, chain);
        try
        {
            return new PackChainPageSource(packs, sparse: false, options,
                $"{view.Paths.Root}#{chain.Point.BackupId} (chain {chain.Base.BackupId}…{chain.Point.BackupId}, P={chain.Point.PointLsn})");
        }
        catch
        {
            foreach (var p in packs)
            {
                p.Dispose();
            }

            throw;
        }
    }

    internal static PackChainPageSource OpenSinglePack(IStoreFileSystem fs, string path, PackSourceOptions options)
    {
        var pack = PackReader.Open(fs, path);
        try
        {
            return new PackChainPageSource([pack], sparse: true, options, $"{path} (pack #{pack.Header.BackupId} alone)");
        }
        catch
        {
            pack.Dispose();
            throw;
        }
    }

    internal static PackReader[] OpenPacks(IStoreFileSystem fs, StoreView view, ResolvedChain chain)
    {
        var packs = new PackReader[chain.Depth];
        try
        {
            for (var i = 0; i < packs.Length; i++)
            {
                packs[i] = PackReader.Open(fs, view.Paths.PackPath(chain.Packs[i].File));
            }

            return packs;
        }
        catch
        {
            foreach (var p in packs)
            {
                p?.Dispose();
            }

            throw;
        }
    }

    /// <inheritdoc />
    public int PageCount { get; }

    /// <summary>The point's id.</summary>
    public uint PointId { get; }

    /// <summary>The point's <c>P</c>.</summary>
    public long PointLsn { get; }

    /// <summary>The packs of the chain, base first.</summary>
    public IReadOnlyList<string> ChainFiles { get; }

    /// <summary>Pages decoded so far.</summary>
    public long PagesDecoded => _pagesDecoded;

    /// <summary>Damaged pages met so far under <see cref="PackDamageMode.ReportAbsent"/>.</summary>
    public IReadOnlyList<PackPageDamage> Damaged => _damaged;

    /// <inheritdoc />
    public bool TryReadPage(int index, Span<byte> destination)
    {
        if ((uint)index >= (uint)PageCount)
        {
            return false;
        }

        var source = _map[index];
        if (source == 0)
        {
            if (_sparse)
            {
                return false;
            }

            destination[..PackFormat.PageSize].Clear();
            return true;
        }

        var pack = source - 1;
        if (!TryFindEntry(pack, (uint)index, out var entry))
        {
            // The map was built from these very INDEX blocks: absent here means the file changed under us.
            return Damage(pack, index, "INDEX entry vanished");
        }

        if (!_frames[pack].TryRead(entry, destination, out var error))
        {
            return Damage(pack, index, error);
        }

        _pagesDecoded++;
        return true;
    }

    /// <inheritdoc />
    public string Describe() => _describe;

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var f in _frames)
        {
            f?.Dispose();
        }

        foreach (var p in _packs)
        {
            p.Dispose();
        }
    }

    private bool Damage(int pack, int pageId, string error)
    {
        if (_options.OnDamage == PackDamageMode.Throw)
        {
            PackFormat.ThrowCorrupt(_packs[pack].Path, "Page", pageId, error);
        }

        _damaged.Add(new PackPageDamage(pageId, _packs[pack].Path, error));
        return false;
    }

    /// <summary>One k-way pass over the INDEX streams: the newest pack holding each page, a fence per INDEX block, every IndexCRC checked.</summary>
    private void BuildMap()
    {
        for (var i = 0; i < _packs.Length; i++)
        {
            var fences = new uint[(_packs[i].Footer.PageCount + BlockEntries - 1) / BlockEntries];
            using var stream = _packs[i].OpenIndexStream();
            long position = 0;
            while (stream.TryNext(out var e))
            {
                if (position % BlockEntries == 0)
                {
                    fences[position / BlockEntries] = e.PageId;
                }

                // Packs are visited oldest first, so a newer pack's entry overwrites an older one's.
                _map[e.PageId] = (ushort)(i + 1);
                position++;
            }

            _fences[i] = fences;
        }
    }

    private bool TryFindEntry(int pack, uint pageId, out PackPageEntry entry)
    {
        var fences = _fences[pack];
        var lo = 0;
        var hi = fences.Length - 1;
        var block = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (fences[mid] <= pageId)
            {
                block = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (block < 0)
        {
            entry = default;
            return false;
        }

        var entries = LoadBlock(pack, block);
        lo = 0;
        hi = entries.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var id = entries[mid].PageId;
            if (id == pageId)
            {
                entry = entries[mid];
                return true;
            }

            if (id < pageId)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        entry = default;
        return false;
    }

    private PackPageEntry[] LoadBlock(int pack, int block)
    {
        for (var node = _lru.First; node != null; node = node.Next)
        {
            if (node.Value.Pack == pack && node.Value.Block == block)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Entries;
            }
        }

        var reader = _packs[pack];
        var count = (int)Math.Min(BlockEntries, reader.Footer.PageCount - ((long)block * BlockEntries));
        var entries = new PackPageEntry[count];
        var offset = reader.Footer.IndexOffset + ((long)block * BlockEntries * PackFormat.PageEntrySize);
        if (!reader.ReadAt(System.Runtime.InteropServices.MemoryMarshal.AsBytes(entries.AsSpan()), offset))
        {
            PackFormat.ThrowCorrupt(reader.Path, "Index", -1, "runs past the end of the file");
        }

        _lru.AddFirst((pack, block, entries));
        if (_lru.Count > Math.Max(1, _options.IndexBlockCacheSize))
        {
            _lru.RemoveLast();
        }

        return entries;
    }
}

/// <summary>
/// One pack on its own (an incremental, say) as a sparse <see cref="IPageSource"/>: a page the pack does not hold is absent
/// (<see cref="TryReadPage"/> returns <c>false</c>), the sparse case <see cref="IPageSource"/> documents.
/// </summary>
[PublicAPI]
public sealed class PackPageSource : IPageSource
{
    private readonly PackChainPageSource _inner;

    private PackPageSource(PackChainPageSource inner) => _inner = inner;

    /// <summary>Opens the pack file at <paramref name="path"/>.</summary>
    /// <param name="path">A <c>.tpack</c> file.</param>
    /// <param name="options">Damage handling and memory.</param>
    public static PackPageSource Open(string path, PackSourceOptions options = null) =>
        new(PackChainPageSource.OpenSinglePack(StoreFileSystem.Instance, path, options));

    internal static PackPageSource Open(IStoreFileSystem fs, string path, PackSourceOptions options = null) =>
        new(PackChainPageSource.OpenSinglePack(fs, path, options));

    /// <inheritdoc />
    public int PageCount => _inner.PageCount;

    /// <summary>Damaged pages met so far under <see cref="PackDamageMode.ReportAbsent"/>.</summary>
    public IReadOnlyList<PackPageDamage> Damaged => _inner.Damaged;

    /// <inheritdoc />
    public bool TryReadPage(int index, Span<byte> destination) => _inner.TryReadPage(index, destination);

    /// <inheritdoc />
    public string Describe() => _inner.Describe();

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
}
