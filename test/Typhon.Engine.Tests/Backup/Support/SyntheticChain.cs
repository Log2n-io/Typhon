using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// A store holding one chain written through the real store and sink — a full point then incrementals over random page subsets, the image
/// growing as it goes — plus the reference model: what every point's image must be, page by page (newest write wins, never-written pages zero).
/// </summary>
internal sealed class SyntheticChain
{
    private readonly List<Dictionary<int, byte[]>> _states = [];
    private readonly List<int> _imagePages = [];

    public const string Root = "/s";

    public InMemorySparseFileSystem Fs { get; }

    public BackupTestImage Image { get; }

    public List<BackupPointInfo> Points { get; } = [];

    private SyntheticChain(InMemorySparseFileSystem fs, BackupTestImage image)
    {
        Fs = fs;
        Image = image;
    }

    /// <summary>Builds a chain of <paramref name="depth"/> points.</summary>
    public static SyntheticChain Build(int depth, int seed, int firstPages = 40, int maxPages = 220, bool withTail = false, BackupTestImage image = null)
    {
        image ??= BackupTestImages.Clean;
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        var chain = new SyntheticChain(fs, image);
        var random = new Random(seed);
        using var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs);

        var n = Math.Max(firstPages, image.PageCount);
        var state = new Dictionary<int, byte[]>();
        for (var p = 0; p < n; p++)
        {
            state[p] = PackTestKit.PageOf(image, p);
        }

        var full = StoreTestKit.WritePoint(store, image, imagePages: n, withTail: withTail);
        chain.Record(full, state, n);

        for (var i = 2; i <= depth; i++)
        {
            // The image grows now and then; a new page left unwritten is a hole, zeros in every view.
            if (random.Next(3) == 0)
            {
                n = Math.Min(maxPages, n + random.Next(1, 25));
            }

            var count = random.Next(0, Math.Min(30, n - 2) + 1);
            var pages = Enumerable.Range(0, count).Select(_ => random.Next(2, n)).Distinct().ToList();
            var salted = StoreTestKit.Salted(image, i);
            state = new Dictionary<int, byte[]>(state);
            foreach (var p in pages)
            {
                state[p] = salted(p);
            }

            var point = StoreTestKit.WritePoint(store, image, full.ChainId, pages, withTail: withTail, imagePages: n, @override: salted);
            chain.Record(point, state, n);
        }

        return chain;
    }

    private void Record(BackupPointInfo point, Dictionary<int, byte[]> state, int n)
    {
        Points.Add(point);
        _states.Add(state);
        _imagePages.Add(n);
    }

    /// <summary>Point <paramref name="index"/>'s image (0-based in the chain): its page count and every page.</summary>
    public byte[] ExpectedPage(int index, int pageId) =>
        _states[index].TryGetValue(pageId, out var page) ? page : new byte[PackFormat.PageSize];

    public int ExpectedPageCount(int index) => _imagePages[index];

    public StoreView View() => StoreView.Open(Root, Fs);

    /// <summary>The point's packs, opened, base first.</summary>
    public PackReader[] OpenChain(uint pointId)
    {
        var view = View();
        return PackChainPageSource.OpenPacks(Fs, view, ChainResolver.Resolve(view, pointId));
    }
}
