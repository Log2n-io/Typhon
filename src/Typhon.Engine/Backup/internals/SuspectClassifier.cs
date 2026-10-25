using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Splits a point's suspect pages (pages the live database had damaged at capture, 02 §4) by what the point's own image says they are (DL-22 d): a
/// suspect on a page its occupancy marks allocated is data the backup faithfully kept damaged — a warning; one on a free page is nothing — a torn
/// write healed on a page no structure owns, which would otherwise keep every later point Warning for good.
/// </summary>
/// <remarks>
/// Reads the point's bootstrap and its occupancy segment only, through <see cref="PackChainPageSource"/>: a few pages, whatever the image's size. An
/// image whose bitmap cannot be read whole counts every suspect as allocated: the conservative answer.
/// </remarks>
internal static class SuspectClassifier
{
    /// <summary>The point's suspects on allocated pages and on free ones.</summary>
    public static (int Allocated, int Free) Classify(IStoreFileSystem fs, StoreView view, uint pointId)
    {
        var chain = ChainResolver.Resolve(view, pointId);
        uint[] suspects;
        using (var reader = PackReader.Open(fs, view.Paths.PackPath(chain.Point.File)))
        {
            suspects = reader.LoadSuspects();
        }

        if (suspects.Length == 0)
        {
            return (0, 0);
        }

        using var source = PackChainPageSource.Open(fs, view, chain);
        var occupancy = ReadOccupancy(source);
        if (occupancy is not { IsComplete: true })
        {
            return (suspects.Length, 0);
        }

        var allocated = 0;
        foreach (var p in suspects)
        {
            if (occupancy.IsAllocated((int)p))
            {
                allocated++;
            }
        }

        return (allocated, suspects.Length - allocated);
    }

    /// <summary>
    /// The last pack classified and its answer. A pack never changes once published (BKP-08), so neither do its suspects nor its image's occupancy: a store
    /// refresh classifies the newest point once, not every minute (opening its chain streams every index of it: ~315 MB for a 100 GiB base). Thread-safe;
    /// two refreshes that overlap may both classify a new pack once.
    /// </summary>
    internal sealed class Memo
    {
        private readonly Lock _lock = new();
        private string _file;
        private (int Allocated, int Free) _result;

        /// <summary>Pack files classified since this memo was made (a test counter).</summary>
        public int Classified { get; private set; }

        /// <summary><see cref="SuspectClassifier.Classify"/> of <paramref name="point"/>, read once per pack file.</summary>
        public (int Allocated, int Free) Classify(IStoreFileSystem fs, StoreView view, CatalogPack point)
        {
            lock (_lock)
            {
                if (string.Equals(_file, point.File, System.StringComparison.Ordinal))
                {
                    return _result;
                }
            }

            var result = SuspectClassifier.Classify(fs, view, point.BackupId);
            lock (_lock)
            {
                (_file, _result) = (point.File, result);
                Classified++;
            }

            return result;
        }
    }

    /// <summary>The image's occupancy bitmap, found through the bootstrap's pointer to its segment; <c>null</c> when it cannot be found.</summary>
    internal static OccupancyView ReadOccupancy(IPageSource source)
    {
        var bootstrap = BootstrapReader.Read(source);
        if (!bootstrap.TryGet(BootstrapView.OccupancyMapSpiKey, out var value) || value.Type != BootstrapDictionary.ValueType.Int1)
        {
            return null;
        }

        var root = value.AsInt;
        if (root <= 0 || root >= source.PageCount)
        {
            return null;
        }

        var segment = new SegmentWalker(source).WalkSegment(root);
        return segment.Kind == StorageSegmentKind.Occupancy ? OccupancyView.Read(source, segment) : null;
    }
}
