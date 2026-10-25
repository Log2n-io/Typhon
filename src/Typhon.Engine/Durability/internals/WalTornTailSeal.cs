using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>What <see cref="WalTornTailSeal.Seal"/> did at a crash open.</summary>
/// <param name="SegmentId">The segment that stopped recovery at a break; 0 when none did.</param>
/// <param name="Offset">File offset of the first invalid frame, where the sentinel was written; 0 when nothing was sealed.</param>
/// <param name="FenceLsn">The highest fence LSN of the torn frame's valid chunks, which recovery applied and the sentinel records; 0 = none.</param>
/// <param name="Sealed">Whether the sentinel was written: the break lay in the last segment holding data.</param>
/// <param name="Unconsolidated">
/// Not written because the torn frame's valid fence chunks lie past the consolidated CheckpointLSN: hidden, a second recovery would replay without them.
/// </param>
internal readonly record struct WalTornTailSealResult(long SegmentId, int Offset, long FenceLsn, bool Sealed, bool Unconsolidated = false);

/// <summary>
/// BKP-10's torn-tail seal (impl/1418 WA-9). Recovery reads every segment on disk and stops for good at the first break (REC-01). A crash open used to
/// reclaim a torn segment through its seal checkpoint before anything could follow it; a floor that keeps it past the open (the WAL archive's) would turn
/// its tear into a break in the middle of the log the next time, dropping every later segment. So once recovery has read it, the crash open writes a
/// <see cref="WalFrameHeader.PaddingSentinel"/> over the first invalid frame of a torn LAST data-bearing segment and flushes it, before the session's first
/// append: the segment then ends cleanly for recovery, captures and the archiver. A break followed by a segment holding data is mid-log corruption (#587)
/// and is left as it is. The sentinel hides the torn frame's valid fence chunks, so it is written only once CheckpointLSN covers them.
/// </summary>
internal static class WalTornTailSeal
{
    private const int PageSize = 4096;

    /// <summary>
    /// Seals the torn tail of the WAL in <paramref name="walDir"/>, if its break lies in the last segment holding data and the fence records recovery
    /// applied from the torn frame are at or below <paramref name="checkpointLsn"/> (in the data file, never replayed again).
    /// </summary>
    internal static unsafe WalTornTailSealResult Seal(IWalFileIO io, string walDir, long checkpointLsn)
    {
        ArgumentNullException.ThrowIfNull(io);
        string tornPath = null;
        WalSegmentScan torn = default;
        using (var reader = new WalSegmentReader(io))
        {
            foreach (var path in io.EnumerateSegmentPaths(walDir).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!reader.OpenSegment(path))
                {
                    continue;
                }

                var scan = WalArchiveScanner.Scan(reader);
                if (tornPath != null)
                {
                    if (scan.LastRecordLsn > 0 || scan.StoppedAtCorruption)
                    {
                        return new WalTornTailSealResult(torn.SegmentId, 0, torn.DroppedToLsn, false);   // a break mid-log: recovery stops there, as it must
                    }

                    continue;
                }

                if (scan.StoppedAtCorruption)
                {
                    tornPath = path;
                    torn = scan;
                }
            }
        }

        if (tornPath == null)
        {
            return default;
        }

        if (torn.DroppedToLsn > checkpointLsn)
        {
            return new WalTornTailSealResult(torn.SegmentId, 0, torn.DroppedToLsn, false, Unconsolidated: true);
        }

        // The sentinel may straddle a page: read, patch and write back the page(s) it lands in, then flush before anything else is appended.
        var offset = torn.FirstInvalidFrameOffset;
        var page = offset & ~(PageSize - 1);
        var length = ((offset + WalFrameHeader.SizeInBytes + PageSize - 1) & ~(PageSize - 1)) - page;
        // native-alloc: one or two pages for the duration of the seal (the WAL's handles are unbuffered: an aligned buffer), freed below.
        var buffer = (byte*)NativeMemory.AlignedAlloc((nuint)length, PageSize);
        try
        {
            var bytes = new Span<byte>(buffer, length);
            using var handle = io.OpenSegment(tornPath, false);
            io.ReadAligned(handle, page, bytes);
            MemoryMarshal.Write(bytes[(offset - page)..], new WalFrameHeader
            {
                FrameLength = WalFrameHeader.PaddingSentinel, RecordCount = WalFrameHeader.CrashSealRecordCount, LastLsn = torn.DroppedToLsn,
            });
            io.WriteAligned(handle, page, bytes);
            io.FlushBuffers(handle);
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }

        return new WalTornTailSealResult(torn.SegmentId, offset, torn.DroppedToLsn, true);
    }
}
