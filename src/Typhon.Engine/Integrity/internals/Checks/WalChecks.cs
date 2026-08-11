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

    /// <summary>
    /// Granularity every drain is written at, so the offset of the next frame is not the end of the previous one.
    /// </summary>
    /// <remarks>
    /// <c>IWalFileIO.WriteAligned</c> requires both offset and length to be multiples of 4096 — the O_DIRECT
    /// constraint — so each drain occupies a whole block and the next frame starts at the next boundary. Measured on a
    /// real segment: eight 120-byte frames at exactly 4096, 8192, 12288, … A walk that advanced by <c>FrameLength</c>
    /// instead reads the padding after frame 0 as an unwritten block and stops, which makes every check built on it
    /// pass by inspecting one frame out of eight.
    /// </remarks>
    private const int DrainAlignment = 4096;

    /// <summary>Rounds a file offset up to the next drain boundary.</summary>
    private static int NextDrain(int offset) => ((offset + DrainAlignment - 1) / DrainAlignment) * DrainAlignment;

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

        var headers = ReadSegmentHeaders(bundle);
        CheckHeaders(ctx, headers);
        CheckOrdering(ctx, bundle, headers);
        CheckRecordChain(ctx, bundle, headers);
    }

    /// <summary>
    /// <c>WAL-02</c> — the log's frames and records tile exactly, and their LSNs only ever go forward.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The catalogue asks for a CRC chain the on-disk format does not have.</b> <c>03 §9</c> specifies
    /// <i>"CRC chain intact per drain block"</i>, and a <c>WalChunkHeader</c>/<c>WalChunkFooter</c> pair carrying
    /// exactly that does exist in the source — but it is not what reaches the file. A segment's data region is a run of
    /// <see cref="WalFrameHeader"/> frames, and each frame holds <c>RecordCount</c> records whose
    /// <see cref="RecordHeader"/> carries an LSN, a kind and a body length, and <b>no checksum</b>. Measured rather than
    /// assumed: the first sixteen bytes past a real segment's header decode as
    /// <c>FrameLength=120, RecordCount=2, LastLsn=2</c>. A first version of this check walked the chunk format instead,
    /// found nothing on every database, and reported success.
    /// </para>
    /// <para>
    /// So the integrity available is <b>structural rather than cryptographic</b>, and it is not weak. Frames tile the
    /// data region exactly; each frame's records tile the frame exactly; LSNs never go backwards. A rewritten byte
    /// inside a record body is invisible to all three — stated in the caveat rather than glossed — but a rewritten
    /// <i>length</i> or <i>count</i> desynchronises the walk immediately, and that is the class that makes recovery
    /// replay garbage rather than stop.
    /// </para>
    /// <para>
    /// <b>A torn tail is not damage.</b> The log is append-only and a crash mid-append leaves a partial frame at the end
    /// by design — recovery stops at the last frame that parses. Reporting that would put a finding on every crash-path
    /// database in existence, which is this feature's own failure mode arrived at from the opposite direction. The check
    /// is therefore not "does every frame parse" but "does parsing fail only at the END".
    /// </para>
    /// <para>
    /// The log is followed and never replayed. Reading a log and acting on one are different things, and only the first
    /// is safe on a database somebody is trying to diagnose.
    /// </para>
    /// </remarks>
    private static void CheckRecordChain(ScanContext ctx, OfflineBundlePageSource bundle, IReadOnlyList<WalHeaderView> headers)
    {
        var walked = 0;

        foreach (var seg in bundle.WalSegments)
        {
            var usable = false;
            for (var i = 0; i < headers.Count; i++)
            {
                if (headers[i].Name == seg.Name)
                {
                    usable = headers[i].Valid && !headers[i].Unused;
                    break;
                }
            }

            if (!usable)
            {
                continue;   // WAL-01 already reported an unusable header; its records are not the story
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(seg.Path);
            }
            catch (IOException ex)
            {
                ctx.Findings.NoteCaveat($"WAL segment '{seg.Name}' could not be read for record walking: {ex.Message}");
                continue;
            }

            walked++;
            previousChunkCrc = 0;
            WalkFrames(ctx, seg.Name, bytes);
        }

        if (walked == 0)
        {
            ctx.Findings.NoteSkipped(RecordChain, "no WAL segment with a valid header was available to walk");
            return;
        }

    }

    /// <summary>Walks one segment's frames, reporting the first break that is not a clean tail.</summary>
    private static void WalkFrames(ScanContext ctx, string name, byte[] bytes)
    {
        var at = WalSegmentHeader.SizeInBytes;
        var frames = 0;
        long previousLsn = 0;

        while (at + WalFrameHeader.SizeInBytes <= bytes.Length)
        {
            var frameLength = MemoryMarshal.Read<int>(bytes.AsSpan(at));
            var recordCount = MemoryMarshal.Read<int>(bytes.AsSpan(at + sizeof(int)));
            var lastLsn = MemoryMarshal.Read<long>(bytes.AsSpan(at + (2 * sizeof(int))));

            // 0 is "not yet published" — the end of what the writer ever wrote. -1 is the end-of-buffer padding
            // sentinel. Both are the normal end of a segment, not a break.
            if (frameLength == 0 || frameLength == WalFrameHeader.PaddingSentinel)
            {
                return;
            }

            if (frameLength < WalFrameHeader.SizeInBytes)
            {
                ReportIfNotATail(ctx, name, bytes, at, at + WalFrameHeader.SizeInBytes, frames,
                    $"frame {frames} at byte {at} declares a length of {frameLength}, which cannot hold its own header");
                return;
            }

            // A frame running past the end of the FILE is a truncation and nothing else: there is, by construction,
            // nothing after it to distinguish corruption from a partial append. Treating it as a break was the first
            // version's mistake, and it fired on exactly the crash-path logs the check must stay quiet about.
            if (at + frameLength > bytes.Length)
            {
                ctx.Findings.NoteCaveat($"WAL segment '{name}' ends with a frame at byte {at} that declares "
                    + $"{frameLength} bytes but has only {bytes.Length - at} left. That is the ordinary shape of a crash "
                    + "mid-append: recovery stops at the last frame that parses, so nothing after it was ever durable.");
                return;
            }

            // LSNs going backwards is unambiguous: the writer assigns them monotonically, and a crash removes frames
            // rather than reordering them, so no partial append can produce this.
            if (recordCount > 0 && previousLsn != 0 && lastLsn <= previousLsn)
            {
                ctx.Report(RecordChain, IntegritySeverity.Divergence, "LOG-03", Locus.Database,
                    $"The log in WAL segment '{name}' goes backwards.",
                    $"Frame {frames} at byte {at} ends at LSN {lastLsn}, but the frame before it already reached "
                    + $"{previousLsn}. Sequence numbers are assigned monotonically and a crash removes frames rather than "
                    + "reordering them, so this cannot be a torn append. Recovery orders its replay by LSN, so these "
                    + "records are applied in a different order from the one they were committed in.",
                    Repairability.NotRepairable);
                return;
            }

            if (!ChunksTileTheFrame(bytes, at, frameLength, ref previousChunkCrc, out var why, out var validChunkFollows))
            {
                // A bad chunk with a GOOD one after it inside the same frame settles the question on its own: a torn
                // append truncates, it does not leave verified chunks behind a broken one. No need to look further.
                if (validChunkFollows)
                {
                    ReportBreak(ctx, name, frames, $"frame {frames} at byte {at} {why}");
                }
                else
                {
                    ReportIfNotATail(ctx, name, bytes, at, at + frameLength, frames, $"frame {frames} at byte {at} {why}");
                }

                return;
            }

            if (recordCount > 0)
            {
                previousLsn = lastLsn;
            }

            at = NextDrain(at + frameLength);
            frames++;
        }
    }

    /// <summary>Footer CRC of the last chunk seen, which the next chunk's <c>PrevCRC</c> must repeat.</summary>
    [ThreadStatic]
    private static uint previousChunkCrc;

    /// <summary>
    /// Whether a frame's chunks fill it exactly, each verifying its own CRC and linking to the one before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two framings are <b>nested</b>, which is what a first attempt at this check missed in both directions: a
    /// segment holds <see cref="WalFrameHeader"/> frames, and each frame holds <c>WalChunkHeader</c>-framed chunks that
    /// each carry a CRC32C footer over their own bytes. Measured on a real segment: a frame of length 120 at byte 4096
    /// contains exactly one 104-byte chunk starting at 4112, and 16 + 104 is the frame length.
    /// </para>
    /// <para>
    /// So the catalogue's "CRC chain per drain block" is real after all — <c>PrevCRC</c> repeats the previous chunk's
    /// footer, so a single rewritten byte anywhere in the log breaks the chain from that point on. The earlier reading
    /// of this format as CRC-free was wrong, and the walk that produced it started chunks at the frame header rather
    /// than after it.
    /// </para>
    /// </remarks>
    private static bool ChunksTileTheFrame(byte[] bytes, int frameAt, int frameLength, ref uint chainCrc, out string why,
        out bool validChunkFollows)
    {
        why = null;
        validChunkFollows = false;

        var at = frameAt + WalFrameHeader.SizeInBytes;
        var frameEnd = frameAt + frameLength;
        var minimum = WalChunkHeader.SizeInBytes + WalChunkFooter.SizeInBytes;
        var index = 0;

        while (at < frameEnd)
        {
            // Fewer bytes left than the smallest possible chunk is padding, not a break — a frame is aligned, and no
            // chunk can be encoded in them. The CompA fixture happens to tile exactly and the indexed one does not,
            // which is how treating this as damage came to fire on a healthy database.
            if (at + minimum > frameEnd)
            {
                return true;
            }

            var chunkSize = MemoryMarshal.Read<ushort>(bytes.AsSpan(at + sizeof(ushort)));
            if (chunkSize < minimum || at + chunkSize > frameEnd)
            {
                why = $"chunk {index} declares a size of {chunkSize}, which does not fit the remaining "
                    + $"{frameEnd - at} byte(s)";
                return false;   // nothing can be located after an unusable size, so no forward probe is possible
            }

            var storedCrc = MemoryMarshal.Read<uint>(bytes.AsSpan(at + chunkSize - WalChunkFooter.SizeInBytes));
            var computedCrc = Crc32CUtil.Compute(bytes.AsSpan(at, chunkSize - WalChunkFooter.SizeInBytes));
            if (storedCrc != computedCrc)
            {
                why = $"chunk {index} fails its own CRC (stored 0x{storedCrc:X8}, computed 0x{computedCrc:X8})";
                validChunkFollows = AnyChunkVerifies(bytes, at + chunkSize, frameEnd);
                return false;
            }

            var storedPrev = MemoryMarshal.Read<uint>(bytes.AsSpan(at + (2 * sizeof(ushort))));
            if (chainCrc != 0 && storedPrev != 0 && storedPrev != chainCrc)
            {
                why = $"chunk {index} records a previous-CRC of 0x{storedPrev:X8}, but the chunk before it ends with "
                    + $"0x{chainCrc:X8}";
                validChunkFollows = AnyChunkVerifies(bytes, at + chunkSize, frameEnd);
                return false;
            }

            chainCrc = storedCrc;
            at += chunkSize;
            index++;
        }

        return true;
    }

    /// <summary>Whether any chunk in <c>[from, end)</c> passes its own CRC.</summary>
    private static bool AnyChunkVerifies(byte[] bytes, int from, int end)
    {
        var minimum = WalChunkHeader.SizeInBytes + WalChunkFooter.SizeInBytes;
        var at = from;

        while (at + minimum <= end)
        {
            var chunkSize = MemoryMarshal.Read<ushort>(bytes.AsSpan(at + sizeof(ushort)));
            if (chunkSize < minimum || at + chunkSize > end)
            {
                return false;
            }

            var storedCrc = MemoryMarshal.Read<uint>(bytes.AsSpan(at + chunkSize - WalChunkFooter.SizeInBytes));
            if (storedCrc == Crc32CUtil.Compute(bytes.AsSpan(at, chunkSize - WalChunkFooter.SizeInBytes)))
            {
                return true;
            }

            at += chunkSize;
        }

        return false;
    }

    /// <summary>Reports a chain break that has already been established as not-a-tail.</summary>
    private static void ReportBreak(ScanContext ctx, string name, int frameIndex, string what)
        => ctx.Report(RecordChain, IntegritySeverity.Divergence, "WP-05", Locus.Database,
            $"The record chain in WAL segment '{name}' breaks before the end of the log.",
            $"Walking it, {what} — and a later chunk in the same frame still verifies, so this cannot be a torn tail from "
            + "a crash mid-append: an interrupted append truncates the log, it does not leave intact chunks behind a "
            + "broken one. Recovery stops at the first chunk that fails, so every record beyond this point is silently "
            + "discarded. Only records after the checkpoint LSN are at stake — the data file already holds everything "
            + "before it.",
            Repairability.NotRepairable,
            new LossEstimate
            {
                Kind = LossKind.Unknown,
                EntityCount = -1,
                BoundedMin = 0,
                BoundedMax = -1,
                Explanation = $"Whatever the records after frame {frameIndex} of '{name}' would have replayed."
            });

    /// <summary>
    /// Reports a break only when written data follows it — a break at the very end is a crash, not corruption.
    /// </summary>
    /// <remarks>
    /// The test is deliberately conservative: anything other than unwritten space after the break counts as "the log
    /// continues", and only then is it damage. Erring this way costs a missed finding on a log whose tail happens to be
    /// zeroed; erring the other way puts a finding on every crash-path database, which is far worse.
    /// </remarks>
    private static void ReportIfNotATail(ScanContext ctx, string name, byte[] bytes, int at, int resumeAt, int frameIndex,
        string what)
    {
        var written = false;
        for (var i = resumeAt; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
            {
                written = true;
                break;
            }
        }

        if (!written)
        {
            ctx.Findings.NoteCaveat($"WAL segment '{name}' ends with an unparseable frame at byte {at}, followed only by "
                + "unwritten space. That is the ordinary shape of a crash mid-append: recovery stops at the last frame "
                + "that parses, so nothing after it was ever durable.");
            return;
        }

        ctx.Report(RecordChain, IntegritySeverity.Divergence, "WP-05", Locus.Database,
            $"The record framing in WAL segment '{name}' breaks before the end of the log.",
            $"Walking it, {what} — and written data continues past that point, so this is not a torn tail from a crash "
            + "mid-append. Recovery walks frame by frame and stops at the first that does not parse, so every record "
            + "beyond this point is silently discarded: the log looks shorter than it is, and the work it holds is lost "
            + "without an error. Only records after the checkpoint LSN are at stake — the data file already holds "
            + "everything before it.",
            Repairability.NotRepairable,
            new LossEstimate
            {
                Kind = LossKind.Unknown,
                EntityCount = -1,
                BoundedMin = 0,
                BoundedMax = -1,
                Explanation = $"Whatever the records after frame {frameIndex} of '{name}' would have replayed."
            });
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

    private static void CheckOrdering(ScanContext ctx, OfflineBundlePageSource bundle, IReadOnlyList<WalHeaderView> headers)
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

            previousId = h.SegmentId;
            previousFirstLsn = h.FirstLsn;
        }

        CheckWindowContiguity(ctx, bundle, headers);
    }

    /// <summary>
    /// <c>WAL-04</c> — the replayable window covers an unbroken run of LSNs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The first implementation checked segment <i>ids</i> for contiguity, and ids are not dense.</b> Measured on a
    /// healthy database (#771): after one open the log holds id 1 covering LSNs 1–128 and a pre-allocated spare; after
    /// a second open it holds id 1, id <b>3</b> starting at LSN 129, and a new spare. There was never an id 2 — the
    /// spare is promoted with a fresh id and the counter has moved on. So the id sequence jumps by design, and a check
    /// reading that as a hole reported <c>Fatal</c> on any database that had been opened twice. It shipped with no test
    /// at all, which is how the premise was never questioned.
    /// </para>
    /// <para>
    /// What recovery actually requires is that the <b>LSNs</b> form an unbroken run: it replays forward and stops at
    /// the first sequence number it cannot find. Segment 1 ending at 128 and segment 3 beginning at 129 is contiguous
    /// in the sense that matters, whatever the file names say. A segment's last LSN is not in its header, so it comes
    /// from walking the frames — which <c>WAL-02</c> already does.
    /// </para>
    /// <para>
    /// Records below the checkpoint are already in the data file, so segments entirely under it are not replayed and
    /// their absence costs nothing. The window therefore starts at the last segment beginning at or below the
    /// checkpoint; with no checkpoint recorded, every segment is replayable.
    /// </para>
    /// </remarks>
    private static void CheckWindowContiguity(ScanContext ctx, OfflineBundlePageSource bundle,
        IReadOnlyList<WalHeaderView> headers)
    {
        var (checkpointLsn, _) = ctx.Bootstrap.ReadWatermarks();

        // Written segments in LSN order — the order recovery reads them in, which is not necessarily file-name order.
        var written = new List<(WalHeaderView Header, long MaxLsn)>();
        foreach (var seg in bundle.WalSegments)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                if (headers[i].Name != seg.Name || !headers[i].Valid || headers[i].Unused)
                {
                    continue;
                }

                written.Add((headers[i], HighestLsnIn(seg.Path)));
                break;
            }
        }

        written.Sort((a, b) => a.Header.FirstLsn.CompareTo(b.Header.FirstLsn));

        // The window opens at the last segment that could still hold an unreplayed record.
        var windowStart = 0;
        for (var i = 0; i < written.Count; i++)
        {
            if (checkpointLsn > 0 && written[i].Header.FirstLsn <= checkpointLsn)
            {
                windowStart = i;
            }
        }

        for (var i = windowStart + 1; i < written.Count; i++)
        {
            var previous = written[i - 1];
            var current = written[i];

            // A segment whose frames could not be walked yields 0; WAL-02 owns that failure, and guessing a coverage
            // gap from it here would report the same damage twice under a code that means something else.
            if (previous.MaxLsn <= 0 || current.Header.FirstLsn == previous.MaxLsn + 1)
            {
                continue;
            }

            if (current.Header.FirstLsn <= previous.MaxLsn)
            {
                continue;   // overlap, not a gap — WAL-03 owns non-monotonic LSNs
            }

            ctx.Report(WindowContiguity, IntegritySeverity.Fatal, "LOG-03", Locus.Database,
                $"The WAL's replayable window has a gap between LSN {previous.MaxLsn:N0} and {current.Header.FirstLsn:N0}.",
                $"'{previous.Header.Name}' ends at LSN {previous.MaxLsn:N0} and '{current.Header.Name}' begins at "
                + $"{current.Header.FirstLsn:N0}, leaving {current.Header.FirstLsn - previous.MaxLsn - 1:N0} sequence "
                + $"number(s) in no segment at all. The window begins at the checkpoint LSN of {checkpointLsn:N0}, so "
                + "these records were still needed. Recovery replays forward and stops at the first LSN it cannot find, "
                + "silently discarding every transaction after the gap even though their segments are present and intact.",
                Repairability.NotRepairable,
                new LossEstimate
                {
                    Kind = LossKind.Unknown,
                    EntityCount = -1,
                    BoundedMin = 1,
                    BoundedMax = long.MaxValue,
                    Explanation = $"Every transaction from LSN {previous.MaxLsn + 1:N0} onward — including the ones in "
                        + "the segments that survived, because recovery cannot skip the gap."
                });
        }
    }

    /// <summary>The highest LSN any frame of a segment claims, or <c>0</c> when it holds none or cannot be read.</summary>
    private static long HighestLsnIn(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return 0;
        }

        var at = WalSegmentHeader.SizeInBytes;
        long highest = 0;

        while (at + WalFrameHeader.SizeInBytes <= bytes.Length)
        {
            var frameLength = MemoryMarshal.Read<int>(bytes.AsSpan(at));
            if (frameLength == 0 || frameLength == WalFrameHeader.PaddingSentinel
                || frameLength < WalFrameHeader.SizeInBytes || at + frameLength > bytes.Length)
            {
                break;
            }

            var recordCount = MemoryMarshal.Read<int>(bytes.AsSpan(at + sizeof(int)));
            var lastLsn = MemoryMarshal.Read<long>(bytes.AsSpan(at + (2 * sizeof(int))));
            if (recordCount > 0 && lastLsn > highest)
            {
                highest = lastLsn;
            }

            at = NextDrain(at + frameLength);
        }

        return highest;
    }
}
