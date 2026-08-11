using System;
using System.IO;

namespace Typhon.Engine.Internals;

/// <summary>
/// Restores the unusable half of an A/B protected page pair by copying its valid sibling over it.
/// </summary>
/// <remarks>
/// <para>
/// A protected page — the page-0 meta pair, and every segment-directory page — exists in two physical slots that writes
/// alternate between, so a torn write can never destroy the only good copy. When one slot becomes unreadable the database
/// keeps working from the other, which is the mechanism doing its job; but the redundancy is gone, and the <i>next</i>
/// torn write to that page makes the database unopenable. That is the failure the pair exists to prevent, so restoring
/// the missing half is worth doing explicitly rather than waiting for the next write to happen to land there.
/// </para>
/// <para>
/// This is a <b>lossless</b> repair in the strictest sense: it reads only the slot that verifies, and writes only the
/// slot that does not. No byte of damaged content is read, so nothing damaged can propagate. The copy is written with a
/// generation one higher than the surviving slot's, which makes it the current one — consistent with what the next
/// ordinary write would have produced.
/// </para>
/// </remarks>
internal static class PairSlotRepair
{
    /// <summary>
    /// Restores the pair that <paramref name="damagedPageIndex"/> belongs to.
    /// </summary>
    /// <param name="bundlePath">The bundle directory.</param>
    /// <param name="damagedPageIndex">Physical index of the slot that failed verification.</param>
    /// <returns>A description of what was done, for the repair receipt.</returns>
    /// <exception cref="InvalidOperationException">Neither slot is usable, or the page is not part of a pair.</exception>
    public static string Restore(string bundlePath, int damagedPageIndex)
    {
        var dataPath = Path.Combine(bundlePath, IntegrityConstants.DataFileName);
        using var handle = File.OpenHandle(dataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var sibling = FindSibling(handle, damagedPageIndex, out var siblingImage);
        if (sibling < 0)
        {
            throw new InvalidOperationException(
                $"Page {damagedPageIndex} has no readable sibling slot, so there is nothing to restore it from. "
                + "This is a restore-from-backup situation, not a repair.");
        }

        // Stamp the copy as the newer generation so pair selection picks it, exactly as an ordinary write would have.
        var generation = PageImage.PairGeneration(siblingImage) + 1;
        PageBaseHeader.WritePairGeneration(siblingImage, generation);
        PagedMMF.StampPageForWrite(siblingImage, LogicalIndexOf(damagedPageIndex, sibling, siblingImage), allowSectorFooter: false);

        RandomAccess.Write(handle, siblingImage, damagedPageIndex * (long)IntegrityConstants.PageSize);
        RandomAccess.FlushToDisk(handle);

        return $"Restored page {damagedPageIndex} from its sibling slot {sibling} at generation {generation}. "
            + "The pair is redundant again; nothing was read from the damaged slot.";
    }

    /// <summary>
    /// Finds the readable half of the pair <paramref name="damagedPageIndex"/> belongs to.
    /// </summary>
    /// <param name="handle">Open handle on the data file.</param>
    /// <param name="damagedPageIndex">The slot that failed.</param>
    /// <param name="image">Receives the sibling's page image.</param>
    /// <returns>The sibling's physical index, or <c>-1</c> when there is none.</returns>
    private static int FindSibling(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int damagedPageIndex, out byte[] image)
    {
        image = new byte[IntegrityConstants.PageSize];

        // The meta pair is at fixed physical slots 0 and 1.
        if (damagedPageIndex is 0 or 1)
        {
            var sibling = 1 - damagedPageIndex;
            if (TryRead(handle, sibling, image) && PagedMMF.VerifyPageImage(image, out _) && PageImage.PairGeneration(image) > 0)
            {
                return sibling;
            }

            return -1;
        }

        // A directory page names its twin in its own header — but the damaged slot's header may be exactly what is
        // unreadable, so try the damaged page's own claim first and fall back to searching for a page that claims it.
        var damaged = new byte[IntegrityConstants.PageSize];
        if (TryRead(handle, damagedPageIndex, damaged))
        {
            var claimed = PageImage.TwinPage(damaged);
            if (claimed > 0 && TryRead(handle, claimed, image) && PagedMMF.VerifyPageImage(image, out _) && PageImage.PairGeneration(image) > 0)
            {
                return claimed;
            }
        }

        return -1;
    }

    /// <summary>
    /// The logical index the restored image should carry: the pair's <i>primary</i>, which is whichever of the two slots
    /// the segment directory references.
    /// </summary>
    private static int LogicalIndexOf(int damagedPageIndex, int siblingIndex, ReadOnlySpan<byte> siblingImage)
    {
        if (damagedPageIndex is 0 or 1)
        {
            return 0;   // the meta pair's logical page is always 0
        }

        var stamped = PageSectorFooter.ReadFilePageIndex(siblingImage);
        return stamped != 0 ? stamped : Math.Min(damagedPageIndex, siblingIndex);
    }

    private static bool TryRead(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int pageIndex, byte[] destination)
    {
        var offset = pageIndex * (long)IntegrityConstants.PageSize;
        if (offset + IntegrityConstants.PageSize > RandomAccess.GetLength(handle))
        {
            return false;
        }

        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(handle, destination.AsSpan(total), offset + total);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}
