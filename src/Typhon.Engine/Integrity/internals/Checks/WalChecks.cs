using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>One WAL segment file's header, as read without replaying anything.</summary>
/// <param name="Name">File name of the segment.</param>
/// <param name="FileBytes">Size of the file on disk.</param>
/// <param name="Valid">Whether the header is well-formed and checksum-valid.</param>
/// <param name="Unused">
/// Whether the segment is pre-allocated but never written. The writer creates segments ahead of need so a commit never
/// waits on file allocation, so an all-zero header is the normal state of a spare — not damage.
/// </param>
/// <param name="SegmentId">Monotonic segment identifier.</param>
/// <param name="FirstLsn">LSN of the segment's first record.</param>
/// <param name="PrevSegmentLsn">Last LSN of the previous segment.</param>
/// <param name="DeclaredSize">Total size the header declares.</param>
/// <param name="Problem">What is wrong with the header, when anything is.</param>
internal readonly record struct WalHeaderView(string Name, long FileBytes, bool Valid, bool Unused, long SegmentId, long FirstLsn,
    long PrevSegmentLsn, uint DeclaredSize, string Problem);

/// <summary>
/// <c>WAL</c> — write-ahead log window checks.
/// </summary>
/// <remarks>
/// The log is read <b>as bytes and never replayed</b>: replay is a mutation, and mutation is what a checker must not do.
/// This family reports that the window holds <i>n</i> committed transactions not yet in the data file; it does not apply
/// them.
/// </remarks>
internal static class WalChecks
{
    /// <summary>Check code: segment headers are valid and sized as declared.</summary>
    public const string SegmentHeaders = "CHK-WAL-01";

    /// <summary>Check code: per-record CRC chain integrity.</summary>
    public const string RecordChain = "CHK-WAL-02";

    /// <summary>Check code: LSNs are strictly monotonic across segments.</summary>
    public const string LsnOrder = "CHK-WAL-03";

    /// <summary>Check code: the replayable window has no gap.</summary>
    public const string WindowContiguity = "CHK-WAL-04";

    /// <summary>Runs the WAL family.</summary>
    /// <param name="ctx">The scan context.</param>
    public static void Run(ScanContext ctx)
    {
        var bundle = ctx.Bundle;
        if (bundle == null || bundle.WalSegments.Count == 0)
        {
            ctx.Findings.NoteSkipped(SegmentHeaders, "no WAL segments present");
            return;
        }

        // Record-level walking needs the drain-block record layout, which this scan deliberately does not decode: a
        // half-understood parse of a log it must not replay would produce findings nobody can act on. Saying so is the
        // point of the limits block.
        ctx.Findings.NoteSkipped(RecordChain, "per-record CRC chain walking is not implemented; only segment headers are verified");
        ctx.Findings.NoteCaveat(
            "WAL contents were not parsed. Segment headers, ordering and window contiguity are verified; the records inside "
            + "each segment are not. A torn record tail inside an otherwise-valid segment would not be reported.");

        var headers = ReadSegmentHeaders(bundle);
        CheckHeaders(ctx, headers);
        CheckOrdering(ctx, headers);
    }

    /// <summary>Reads every WAL segment file's 4 KiB header without touching its records.</summary>
    /// <param name="bundle">The bundle whose <c>wal/</c> directory is read.</param>
    public static IReadOnlyList<WalHeaderView> ReadSegmentHeaders(OfflineBundlePageSource bundle)
    {
        var result = new List<WalHeaderView>(bundle.WalSegments.Count);
        var buffer = new byte[WalSegmentHeader.SizeInBytes];

        for (var i = 0; i < bundle.WalSegments.Count; i++)
        {
            var seg = bundle.WalSegments[i];
            if (seg.SizeBytes < WalSegmentHeader.SizeInBytes)
            {
                result.Add(new WalHeaderView(seg.Name, seg.SizeBytes, false, false, 0, 0, 0, 0,
                    $"the file is {seg.SizeBytes:N0} bytes, shorter than the {WalSegmentHeader.SizeInBytes:N0}-byte header"));
                continue;
            }

            try
            {
                using var handle = File.OpenHandle(seg.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                RandomAccess.Read(handle, buffer, 0);
            }
            catch (IOException ex)
            {
                result.Add(new WalHeaderView(seg.Name, seg.SizeBytes, false, false, 0, 0, 0, 0, $"the file could not be read: {ex.Message}"));
                continue;
            }

            // A pre-allocated spare has an all-zero header. The writer creates segments ahead of need so a commit never
            // blocks on file allocation, so this is the normal resting state of a spare rather than a damaged segment —
            // and reporting it as damage would put a finding on every healthy database that pre-allocates.
            if (IsAllZero(buffer))
            {
                result.Add(new WalHeaderView(seg.Name, seg.SizeBytes, false, true, long.MaxValue, 0, 0, 0, null));
                continue;
            }

            var magic = MemoryMarshal.Read<uint>(buffer);
            var version = MemoryMarshal.Read<uint>(buffer.AsSpan(4));
            var segmentId = MemoryMarshal.Read<long>(buffer.AsSpan(8));
            var firstLsn = MemoryMarshal.Read<long>(buffer.AsSpan(16));
            var prevLsn = MemoryMarshal.Read<long>(buffer.AsSpan(24));
            var declaredSize = MemoryMarshal.Read<uint>(buffer.AsSpan(32));
            var storedCrc = MemoryMarshal.Read<uint>(buffer.AsSpan(WalSegmentHeader.HeaderCrcOffset));
            var computedCrc = Crc32CUtil.ComputeSkipping(buffer, WalSegmentHeader.HeaderCrcOffset, sizeof(uint));

            string problem = null;
            if (magic != WalSegmentHeader.MagicValue)
            {
                problem = $"the magic number is 0x{magic:X8}, not the expected 0x{WalSegmentHeader.MagicValue:X8}";
            }
            else if (version != WalSegmentHeader.CurrentVersion)
            {
                problem = $"the format version is {version}, not {WalSegmentHeader.CurrentVersion}";
            }
            else if (storedCrc != computedCrc)
            {
                problem = $"the header checksum does not match (stored 0x{storedCrc:X8}, computed 0x{computedCrc:X8})";
            }

            result.Add(new WalHeaderView(seg.Name, seg.SizeBytes, problem == null, false, segmentId, firstLsn, prevLsn, declaredSize, problem));
        }

        // Unused spares sort last (their id is MaxValue), so the ordering checks below walk only real segments.
        result.Sort(static (x, y) => x.SegmentId.CompareTo(y.SegmentId));
        return result;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void CheckHeaders(ScanContext ctx, IReadOnlyList<WalHeaderView> headers)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            var h = headers[i];
            if (h.Unused)
            {
                continue;   // a pre-allocated spare, waiting to be used
            }

            if (!h.Valid)
            {
                ctx.Report(SegmentHeaders, IntegritySeverity.Divergence, "N7", Locus.Database,
                    $"WAL segment '{h.Name}' has an unusable header.",
                    $"Specifically, {h.Problem}. Recovery cannot order this segment against the others, so any transactions "
                    + "it holds are effectively outside the replayable window.");
                continue;
            }

            if (h.DeclaredSize != 0 && h.FileBytes != h.DeclaredSize)
            {
                ctx.Report(SegmentHeaders, IntegritySeverity.Divergence, "N7", Locus.Database,
                    $"WAL segment '{h.Name}' is not the size its header declares.",
                    $"The header declares {h.DeclaredSize:N0} bytes; the file is {h.FileBytes:N0}. "
                    + (h.FileBytes < h.DeclaredSize
                        ? "A short file is the silent-truncation shape: records that were written are simply not there any more."
                        : "A long file means something appended past the segment's declared end."));
            }
        }
    }

    private static void CheckOrdering(ScanContext ctx, IReadOnlyList<WalHeaderView> headers)
    {
        long previousId = long.MinValue;
        long previousFirstLsn = long.MinValue;

        for (var i = 0; i < headers.Count; i++)
        {
            var h = headers[i];
            if (!h.Valid || h.Unused)
            {
                continue;
            }

            if (h.SegmentId == previousId)
            {
                ctx.Report(LsnOrder, IntegritySeverity.Fatal, "LOG-08", Locus.Database,
                    $"Two WAL segments share id {h.SegmentId}.",
                    $"'{h.Name}' duplicates an earlier segment's identifier. Recovery orders segments by id, so it cannot "
                    + "decide which of the two to replay — and replaying the wrong one applies a different set of "
                    + "transactions than the one that committed.");
            }
            else if (previousFirstLsn != long.MinValue && h.FirstLsn <= previousFirstLsn)
            {
                ctx.Report(LsnOrder, IntegritySeverity.Divergence, "LOG-08", Locus.Database,
                    $"WAL segment '{h.Name}' does not start after the segment before it.",
                    $"Its first LSN is {h.FirstLsn:N0}, but the previous segment already started at {previousFirstLsn:N0}. "
                    + "LSNs are strictly monotonic across the log, so the sequence is either out of order or a segment "
                    + "carries a stale header.");
            }

            if (previousId != long.MinValue && h.SegmentId > previousId + 1)
            {
                ctx.Report(WindowContiguity, IntegritySeverity.Fatal, "LOG-03", Locus.Database,
                    $"The WAL is missing segment(s) between id {previousId} and {h.SegmentId}.",
                    $"{h.SegmentId - previousId - 1} segment file(s) are absent from the middle of the log. Recovery replays "
                    + "a contiguous window; a hole in the middle means it must stop at the hole, silently discarding every "
                    + "transaction after it even though those segments are present and intact.",
                    Repairability.NotRepairable,
                    new LossEstimate
                    {
                        Kind = LossKind.Unknown,
                        EntityCount = -1,
                        BoundedMin = 1,
                        BoundedMax = long.MaxValue,
                        Explanation = $"Every transaction from segment {previousId + 1} onward — including the ones in the "
                            + "segments that survived, because recovery cannot skip the gap."
                    });
            }

            previousId = h.SegmentId;
            previousFirstLsn = h.FirstLsn;
        }
    }
}
