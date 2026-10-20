using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>What <see cref="WalArchiveScanner.Scan"/> found in one WAL segment: how much of it the archive keeps, and what it holds.</summary>
/// <param name="SegmentId">The header's segment id.</param>
/// <param name="FirstLsn">The header's first LSN.</param>
/// <param name="PrevSegmentLsn">The header's previous-segment LSN: 0 for the first segment of an engine session.</param>
/// <param name="DeclaredSize">The header's declared size.</param>
/// <param name="ArchiveLength">
/// Bytes <c>[0, ArchiveLength)</c> to archive, header included: the end of the last frame read whole, rounded up to a 4 KiB page. The rest of that page is
/// zero in the archived copy (the archiver zeroes it in its buffer), so no byte of a torn frame is ever archived.
/// </param>
/// <param name="LastRecordLsn">The highest LSN of the frames read whole; 0 = none.</param>
/// <param name="LastUnitLsn">The highest LSN recovery applies as a unit (a TxCommit or a fence record) in those frames; 0 = none.</param>
/// <param name="StoppedAtCorruption">The walk stopped at a broken frame or chunk (<see cref="WalSegmentReader.WasTruncated"/>).</param>
/// <param name="FirstInvalidFrameOffset">File offset of the frame where the walk stopped at a corruption; 0 when it did not.</param>
/// <param name="DroppedFromLsn">
/// With <paramref name="StoppedAtCorruption"/>: the lowest LSN of a record recovery applies alone (a fence record) in the incomplete frame's valid
/// chunks — applied by the crash recovery that saw them, absent from the archive; 0 = none.
/// </param>
/// <param name="DroppedToLsn">
/// The highest such LSN; 0 = none. For a segment a crash open sealed, the highest fence LSN its seal recorded (the torn frame's chunks are no longer read).
/// </param>
/// <param name="SealedAtCrash">The walk ended at the padding sentinel a crash open writes over a torn frame (<see cref="WalFrameHeader.CrashSealRecordCount"/>).</param>
/// <param name="FirstRecordLsn">The lowest LSN of the frames read whole; 0 = none.</param>
internal readonly record struct WalSegmentScan(long SegmentId, long FirstLsn, long PrevSegmentLsn, uint DeclaredSize, int ArchiveLength, long LastRecordLsn,
    long LastUnitLsn, bool StoppedAtCorruption, int FirstInvalidFrameOffset, long DroppedFromLsn, long DroppedToLsn, bool SealedAtCrash, long FirstRecordLsn);

/// <summary>What a WAL frame holds, for <c>typhon backup log --commits</c>.</summary>
internal enum WalFrameKind : byte
{
    /// <summary>A transaction: its last record is its TxCommit.</summary>
    Transaction,

    /// <summary>One fence emission: fence records, or a tick-fence chunk.</summary>
    Fence,

    /// <summary>A bulk load's begin or end manifest.</summary>
    Bulk,

    /// <summary>Anything else: records of no known kind.</summary>
    Other,
}

/// <summary>One frame read whole by <see cref="WalArchiveScanner.Frames"/>.</summary>
/// <param name="FirstLsn">Its lowest LSN.</param>
/// <param name="LastLsn">Its highest LSN.</param>
/// <param name="Tsn">The transaction's TSN (its first record's); 0 when it holds no transaction record.</param>
/// <param name="Kind">What it holds.</param>
/// <param name="Records">Its records (chunks, for a chunk type holding no v2 records).</param>
/// <param name="CommitStamp">The TxCommit's stamp; 0 when none.</param>
internal readonly record struct WalFrameSummary(long FirstLsn, long LastLsn, long Tsn, WalFrameKind Kind, int Records, long CommitStamp);

/// <summary>Where <see cref="WalArchiveScanner.FindCut"/> cuts a segment for a restore to an LSN.</summary>
/// <param name="CutOffset">File offset of the end of the last frame kept; the restored segment holds <c>[0, CutOffset)</c> then zeros.</param>
/// <param name="EffectiveLsn"><c>L'</c>: the last LSN of that frame, the highest frame-final LSN at or below the target; 0 when no frame of the segment ends there.</param>
/// <param name="EndsWithinSegment">Whether a frame of the segment ends past the target (the cut drops it); false when every frame is kept.</param>
internal readonly record struct WalCut(int CutOffset, long EffectiveLsn, bool EndsWithinSegment);

/// <summary>
/// The WAL archive's view of a segment's bytes (impl/1418 §3.4), through the WAL's own reader and codec: record bytes stay inside
/// <c>Durability/internals</c> (LOG-02), the archive and the restore see offsets and LSNs.
/// </summary>
/// <remarks>
/// A frame holds one batch (one claim): a transaction, whose TxCommit is its last record, or one fence emission (AC-A3). Cutting at a frame end is therefore
/// cutting between units; a chunk boundary inside a frame is not, and the reader would read the zeros after it as a torn frame (WA-10).
/// </remarks>
internal static class WalArchiveScanner
{
    private const int PageSize = 4096;

    /// <summary>
    /// Scans the segment <paramref name="reader"/> has open: every chunk, as recovery reads it, stopping where recovery stops. Leaves the reader's data
    /// past the last complete frame zeroed (<see cref="WalSegmentReader.ZeroFrom"/>): its loaded bytes are then exactly what the archive keeps.
    /// </summary>
    internal static WalSegmentScan Scan(WalSegmentReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        long lastRecord = 0, lastUnit = 0, firstRecord = 0;
        long frameRecord = 0, frameUnit = 0, frameFenceLow = 0, frameFenceHigh = 0, frameFirst = 0;
        var frameStart = -1;
        var frameEnd = 0;
        while (reader.TryReadNext(out var chunk, out var body))
        {
            if (reader.FrameStart != frameStart)
            {
                // A new frame: the previous one was read whole.
                lastRecord = Math.Max(lastRecord, frameRecord);
                lastUnit = Math.Max(lastUnit, frameUnit);
                firstRecord = firstRecord == 0 ? frameFirst : firstRecord;
                frameStart = reader.FrameStart;
                frameEnd = reader.FrameEnd;
                frameRecord = frameUnit = frameFenceLow = frameFenceHigh = frameFirst = 0;
            }

            if (chunk.ChunkType != (ushort)WalChunkType.Transaction)
            {
                // Every chunk type carries its LSN at body offset 0 (WP-07).
                if (body.Length >= sizeof(long))
                {
                    var lsn = MemoryMarshal.Read<long>(body);
                    frameRecord = Math.Max(frameRecord, lsn);
                    frameFirst = frameFirst == 0 ? lsn : Math.Min(frameFirst, lsn);
                }

                continue;
            }

            var offset = 0;
            while (RecordCodec.TryReadRecord(body, offset, out var consumed, out var view))
            {
                offset += consumed;
                if (view.IsUnknownKind)
                {
                    continue;
                }

                frameRecord = Math.Max(frameRecord, view.Lsn);
                frameFirst = frameFirst == 0 ? view.Lsn : Math.Min(frameFirst, view.Lsn);
                if (view.IsTxCommit || view.IsFence)
                {
                    frameUnit = Math.Max(frameUnit, view.Lsn);
                }

                if (view.IsFence)
                {
                    frameFenceLow = frameFenceLow == 0 ? view.Lsn : Math.Min(frameFenceLow, view.Lsn);
                    frameFenceHigh = Math.Max(frameFenceHigh, view.Lsn);
                }
            }
        }

        long droppedFrom = 0, droppedTo = 0;
        if (frameStart >= 0 && frameEnd <= reader.LastCompleteFrameEnd)
        {
            lastRecord = Math.Max(lastRecord, frameRecord);
            lastUnit = Math.Max(lastUnit, frameUnit);
            firstRecord = firstRecord == 0 ? frameFirst : firstRecord;
        }
        else if (frameStart >= 0)
        {
            // The walk stopped inside this frame: its valid chunks are not archived, and a fence among them was applied by the recovery that read them.
            droppedFrom = frameFenceLow;
            droppedTo = frameFenceHigh;
        }

        // A crash open's seal: the sentinel over the torn frame names the fence records its recovery applied from that frame (BKP-10, torn-tail clause).
        var sealedAtCrash = false;
        var data = reader.LoadedData;
        var stop = reader.FrameStart;
        if (!reader.WasTruncated && stop >= 0 && stop + WalFrameHeader.SizeInBytes <= data.Length)
        {
            var sentinel = MemoryMarshal.Read<WalFrameHeader>(data[stop..]);
            if (sentinel.FrameLength == WalFrameHeader.PaddingSentinel && sentinel.RecordCount == WalFrameHeader.CrashSealRecordCount)
            {
                sealedAtCrash = true;
                droppedFrom = sentinel.LastLsn > 0 ? lastRecord + 1 : 0;
                droppedTo = sentinel.LastLsn;
            }
        }

        var complete = reader.LastCompleteFrameEnd;
        reader.ZeroFrom(complete);
        ref readonly var header = ref reader.SegmentHeader;
        var archiveLength = WalSegmentHeader.SizeInBytes + AlignUp(complete);
        var truncated = reader.WasTruncated;
        return new WalSegmentScan(header.SegmentId, header.FirstLSN, header.PrevSegmentLSN, header.SegmentSize, archiveLength, lastRecord, lastUnit, truncated,
            truncated ? WalSegmentHeader.SizeInBytes + reader.FrameStart : 0, droppedFrom, droppedTo, sealedAtCrash, firstRecord);
    }

    /// <summary>
    /// Where a restore to <paramref name="targetLsn"/> cuts the segment <paramref name="reader"/> has open (BKP-11): at the end of the last frame whose
    /// last LSN is at or below the target. <see cref="WalCut.EffectiveLsn"/> is 0 when the segment's first frame already ends past it.
    /// </summary>
    internal static WalCut FindCut(WalSegmentReader reader, long targetLsn)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var cut = 0;
        long effective = 0;
        var frameStart = -1;
        long frameLast = 0;
        var frameEnd = 0;
        var past = false;
        while (reader.TryReadNext(out _, out _))
        {
            if (reader.FrameStart == frameStart)
            {
                continue;
            }

            // The previous frame was read whole: keep it if it ends at or below the target.
            if (frameStart >= 0 && !Keep(frameLast, frameEnd, targetLsn, ref cut, ref effective))
            {
                past = true;
                break;
            }

            frameStart = reader.FrameStart;
            frameEnd = reader.FrameEnd;
            frameLast = reader.FrameLastLsn;
        }

        if (!past && frameStart >= 0 && frameEnd <= reader.LastCompleteFrameEnd)
        {
            past = !Keep(frameLast, frameEnd, targetLsn, ref cut, ref effective);
        }

        return new WalCut(WalSegmentHeader.SizeInBytes + cut, effective, past);
    }

    /// <summary>
    /// Appends every frame of the segment <paramref name="reader"/> has open that recovery would read whole to <paramref name="into"/>, ascending; a
    /// frame broken by a corruption is not listed (WA-25). Record bytes stay here (LOG-02): the caller sees LSNs, TSNs and kinds.
    /// </summary>
    internal static void Frames(WalSegmentReader reader, List<WalFrameSummary> into)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(into);
        var frameStart = -1;
        var frameEnd = 0;
        var current = default(FrameAccumulator);
        while (reader.TryReadNext(out var chunk, out var body))
        {
            if (reader.FrameStart != frameStart)
            {
                if (frameStart >= 0)
                {
                    into.Add(current.Summary());
                }

                frameStart = reader.FrameStart;
                frameEnd = reader.FrameEnd;
                current = default;
            }

            if (chunk.ChunkType != (ushort)WalChunkType.Transaction)
            {
                var lsn = body.Length >= sizeof(long) ? MemoryMarshal.Read<long>(body) : 0;
                current.Add(lsn, 0, chunk.ChunkType is (ushort)WalChunkType.BulkBegin or (ushort)WalChunkType.BulkEnd ? WalFrameKind.Bulk : WalFrameKind.Fence,
                    0);
                continue;
            }

            var offset = 0;
            while (RecordCodec.TryReadRecord(body, offset, out var consumed, out var view))
            {
                offset += consumed;
                current.Add(view.Lsn, view.IsFence ? 0 : view.Tsn,
                    view.IsUnknownKind ? WalFrameKind.Other : view.IsFence ? WalFrameKind.Fence : WalFrameKind.Transaction, view.IsTxCommit ? view.CommitStamp : 0);
            }
        }

        if (frameStart >= 0 && frameEnd <= reader.LastCompleteFrameEnd)
        {
            into.Add(current.Summary());
        }
    }

    private struct FrameAccumulator
    {
        private long _first, _last, _tsn, _stamp;
        private int _records;
        private WalFrameKind _kind;
        private bool _any;

        public void Add(long lsn, long tsn, WalFrameKind kind, long stamp)
        {
            _first = _records == 0 || lsn < _first ? lsn : _first;
            _last = Math.Max(_last, lsn);
            _tsn = _tsn == 0 ? tsn : _tsn;
            _stamp = stamp != 0 ? stamp : _stamp;
            _kind = !_any || kind < _kind ? kind : _kind;
            _any = true;
            _records++;
        }

        public readonly WalFrameSummary Summary() => new(_first, _last, _tsn, _any ? _kind : WalFrameKind.Other, _records, _stamp);
    }

    private static bool Keep(long frameLast, int frameEnd, long targetLsn, ref int cut, ref long effective)
    {
        if (frameLast > targetLsn)
        {
            return false;
        }

        cut = frameEnd;
        effective = Math.Max(effective, frameLast);
        return true;
    }

    private static int AlignUp(int value) => (value + PageSize - 1) & ~(PageSize - 1);
}
