using System;

namespace Typhon.Engine.Internals;

// Read-only storage-introspection surface consumed by the Workbench Database File Map (Module 15, Track A).
// Everything here derives from in-memory engine structures — the occupancy bitmap and the segment registry —// with no data-page I/O.
public partial class ManagedPagedMMF
{
    /// <summary>
    /// Number of 8 KiB pages in the backing file (<see cref="PagedMMF.FileSize"/> ÷ page size). This is the page-index domain the Database File Map visualizes.
    /// </summary>
    public int StorageFilePageCount => (int)(FileSize >> PageSizePow2);

    /// <summary>The occupancy-bitmap segment — exposed read-only for storage introspection.</summary>
    internal LogicalSegment<PersistentStore> OccupancySegment => _occupancySegment;

    /// <summary>
    /// Bulk-reads the occupancy bitmap's level-0 words into <paramref name="dest"/>. Each <c>long</c> holds 64 page-occupancy bits — bit <c>p &amp; 63</c> of
    /// word <c>p &gt;&gt; 6</c> corresponds to page <c>p</c>, a set bit meaning the page is allocated. Touches only the resident occupancy-segment pages — no
    /// data-page I/O.
    /// <paramref name="dest"/> must hold at least <c>(OccupancyCapacityPages + 63) / 64</c> words.
    /// </summary>
    public void ReadOccupancyBits(Span<long> dest) => _occupancyMap?.ReadOccupancyBits(dest);
}
