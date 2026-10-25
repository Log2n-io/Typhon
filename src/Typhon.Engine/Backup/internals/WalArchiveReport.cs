using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace Typhon.Engine.Internals;

/// <summary>A range of a timeline's archive no restore crosses, derived from its files (gaps are never stored on their own, WA-7).</summary>
internal readonly record struct ArchiveGap(long FromLsn, long ToLsn, BackupArchiveGapReason Reason, long RecordedUtcTicks, long SegmentId);

/// <summary>
/// What the store's WAL archive says to a reader (impl/1418 §4.3, WA-26): the listing, a range of the log with its frames, the coverage <c>status</c>
/// reports and the checks <c>verify</c> runs (BKP-VER-15…18). Engine-free, read-only, no lock.
/// </summary>
internal static class WalArchiveReport
{
    /// <summary>The ranges <paramref name="timeline"/>'s archive misses: recorded gaps, holes no footer explains, and the ranges a segment dropped.</summary>
    public static List<ArchiveGap> Gaps(ArchivedTimeline timeline)
    {
        var gaps = new List<ArchiveGap>();
        long prevEnd = 0;
        foreach (var s in timeline.Segments)
        {
            var f = s.Footer;
            if (f.GapBefore)
            {
                gaps.Add(new ArchiveGap(f.GapFromLsn, f.GapToLsn, Public(f.GapReason), f.ArchivedUtcTicks, s.SegmentId));
            }
            else if (prevEnd > 0 && !s.Continues(prevEnd))
            {
                gaps.Add(new ArchiveGap(prevEnd + 1, Math.Max(s.FirstLsn, f.FirstRecordLsn) - 1, BackupArchiveGapReason.Unknown, f.ArchivedUtcTicks,
                    s.SegmentId));
            }

            if (f.DroppedToLsn > 0)
            {
                var reason = (f.Flags & ArchiveFormat.FooterFlagCorruptMidSegment) != 0 ? BackupArchiveGapReason.CorruptSegment : BackupArchiveGapReason.TornAtCrash;
                gaps.Add(new ArchiveGap(f.DroppedFromLsn, f.DroppedToLsn, reason, f.ArchivedUtcTicks, s.SegmentId));
            }

            prevEnd = Math.Max(prevEnd, s.End);
        }

        return gaps;
    }

    /// <summary>Whether a restorable point of <paramref name="timeline"/> lies past <paramref name="gap"/>: its <c>C</c> above the gap's end.</summary>
    public static bool Covered(ArchiveGap gap, IReadOnlyList<CatalogPack> restorable, Guid timeline)
    {
        foreach (var p in restorable)
        {
            if (p.TimelineId == timeline && p.CheckpointLsn > gap.ToLsn)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The restorable points of the store, as the packs that stand for them.</summary>
    public static List<CatalogPack> RestorablePacks(StoreView view)
    {
        var result = new List<CatalogPack>();
        foreach (var id in ChainResolver.RestorablePoints(view))
        {
            var (compacted, original) = view.Find(id);
            if ((compacted ?? original) is { } pack)
            {
                result.Add(pack);
            }
        }

        return result;
    }

    /// <summary><see cref="BackupOperations.ListArchive"/>: every timeline, parents first, from the index cache where it holds.</summary>
    public static BackupArchiveListing List(IStoreFileSystem fs, StoreView view)
    {
        var restorable = RestorablePacks(view);
        var byId = new Dictionary<Guid, BackupArchiveTimeline>();
        foreach (var t in WalArchiveStore.ListTimelines(fs, view.Paths.Root))
        {
            var archived = WalArchiveStore.Load(fs, view.Paths.Root, t);
            var segments = new List<BackupArchivedSegment>(archived.Segments.Count);
            var samples = new List<BackupTimeSample>(archived.Segments.Count);
            foreach (var s in archived.Segments)
            {
                segments.Add(Segment(s));
                if (s.LastSampleLsn > 0)
                {
                    samples.Add(new BackupTimeSample(s.LastSampleLsn, new DateTime(s.LastSampleUtcTicks, DateTimeKind.Utc)));
                }
            }

            var unreadable = new List<KeyValuePair<string, string>>(archived.Unreadable.Count);
            foreach (var (unreadableFile, reason) in archived.Unreadable)
            {
                unreadable.Add(new KeyValuePair<string, string>(unreadableFile, reason));
            }

            var file = archived.Timeline;
            byId[t] = new BackupArchiveTimeline(t, file?.ParentTimelineId, file?.OriginLsn ?? 0, Public(file?.Reason ?? TimelineReason.Unknown),
                archived.ArchivedThroughLsn, segments, PublicGaps(Gaps(archived), restorable, t), samples, unreadable);
        }

        // Parents before their children; a timeline whose parent the archive does not hold is a root.
        var ordered = new List<BackupArchiveTimeline>(byId.Count);
        var placed = new HashSet<Guid>();
        while (ordered.Count < byId.Count)
        {
            var progress = false;
            foreach (var (id, t) in byId)
            {
                if (!placed.Contains(id) && (t.ParentTimelineId is not { } parent || !byId.ContainsKey(parent) || placed.Contains(parent)))
                {
                    ordered.Add(t);
                    placed.Add(id);
                    progress = true;
                }
            }

            if (!progress)
            {
                foreach (var (id, t) in byId)
                {
                    if (placed.Add(id))
                    {
                        ordered.Add(t);   // a parent cycle no engine writes: listed, never looped on
                    }
                }
            }
        }

        return new BackupArchiveListing(view.Identity.StoreId, view.Identity.DatabaseId, ordered);
    }

    /// <summary>
    /// <see cref="BackupOperations.ReadLog(string, Guid, long, long, bool, int)"/>: the segments, gaps and samples of <c>[fromLsn, toLsn]</c> on
    /// <paramref name="timelineId"/>; with <paramref name="commits"/>, every frame ending inside it, up to <paramref name="max"/>, each timed by
    /// interpolation between the samples around it (WA-25).
    /// </summary>
    public static BackupArchiveLog Log(IStoreFileSystem fs, StoreView view, Guid timelineId, long fromLsn, long toLsn, bool commits, int max)
    {
        var root = view.Paths.Root;
        var archived = WalArchiveStore.Load(fs, root, timelineId, trustIndex: false);
        var restorable = RestorablePacks(view);
        var segments = new List<BackupArchivedSegment>();
        var inRange = new List<ArchivedSegmentInfo>();
        foreach (var s in archived.Segments)
        {
            if (s.End >= fromLsn && s.FirstLsn <= toLsn)
            {
                segments.Add(Segment(s));
                inRange.Add(s);
            }
        }

        var gaps = new List<ArchiveGap>();
        foreach (var g in Gaps(archived))
        {
            if (g.ToLsn >= fromLsn && g.FromLsn <= toLsn)
            {
                gaps.Add(g);
            }
        }

        // The samples of the segments whose samples reach into the range, and of the nearest segment on each side of it, for the frames' times.
        var all = new List<ArchiveTimeSample>();
        var dir = new WalArchivePaths(root).Timeline(timelineId);
        int before = -1, after = -1;
        for (var i = 0; i < archived.Segments.Count; i++)
        {
            var s = archived.Segments[i];
            if (s.LastSampleLsn > 0 && s.LastSampleLsn < fromLsn)
            {
                before = i;
            }
            else if (after < 0 && s.FirstSampleLsn > toLsn)
            {
                after = i;
            }
        }

        for (var i = 0; i < archived.Segments.Count; i++)
        {
            var s = archived.Segments[i];
            if (s.LastSampleLsn == 0 || (i != before && i != after && (s.LastSampleLsn < fromLsn || s.FirstSampleLsn > toLsn)))
            {
                continue;
            }

            using var reader = ArchivedSegmentReader.Open(fs, Path.Combine(dir, s.File));
            all.AddRange(reader.ReadSamples());
        }

        all.Sort(static (a, b) => a.Lsn.CompareTo(b.Lsn));
        var samples = new List<BackupTimeSample>();
        foreach (var x in all)
        {
            if (x.Lsn >= fromLsn && x.Lsn <= toLsn)
            {
                samples.Add(new BackupTimeSample(x.Lsn, new DateTime(x.UtcTicks, DateTimeKind.Utc)));
            }
        }

        var frames = new List<BackupLogFrame>();
        var truncated = false;
        if (commits)
        {
            var summaries = new List<WalFrameSummary>();
            foreach (var s in inRange)
            {
                summaries.Clear();
                ReadFrames(fs, Path.Combine(dir, s.File), s, summaries);
                foreach (var f in summaries)
                {
                    if (f.LastLsn < fromLsn || f.LastLsn > toLsn)
                    {
                        continue;
                    }

                    if (frames.Count == max)
                    {
                        truncated = true;
                        break;
                    }

                    frames.Add(new BackupLogFrame(f.FirstLsn, f.LastLsn, f.Tsn, (BackupLogFrameKind)f.Kind, f.Records, f.CommitStamp, Interpolate(all, f.LastLsn),
                        s.SegmentId));
                }

                if (truncated)
                {
                    break;
                }
            }
        }

        return new BackupArchiveLog(timelineId, fromLsn, toLsn, segments, PublicGaps(gaps, restorable, timelineId), samples, frames, truncated);
    }

    /// <summary>The coverage of every timeline, for <see cref="BackupOperations.Status"/>.</summary>
    public static List<BackupArchiveCoverage> Coverage(IStoreFileSystem fs, StoreView view)
    {
        var restorable = RestorablePacks(view);
        var result = new List<BackupArchiveCoverage>();
        foreach (var t in WalArchiveStore.ListTimelines(fs, view.Paths.Root))
        {
            var archived = WalArchiveStore.Load(fs, view.Paths.Root, t);
            long from = 0;
            foreach (var p in restorable)
            {
                if (p.TimelineId == t && (from == 0 || p.PointLsn < from))
                {
                    from = p.PointLsn;
                }
            }

            var uncovered = new List<BackupArchiveGap>();
            foreach (var g in PublicGaps(Gaps(archived), restorable, t))
            {
                if (!g.Covered)
                {
                    uncovered.Add(g);
                }
            }

            DateTime? last = archived.Segments.Count == 0 ? null : new DateTime(archived.Segments[^1].Footer.ArchivedUtcTicks, DateTimeKind.Utc);
            result.Add(new BackupArchiveCoverage(t, from, archived.ArchivedThroughLsn, last, uncovered));
        }

        return result;
    }

    /// <summary>
    /// BKP-VER-15…18 over every timeline of the archive: each <c>.twal</c> readable, and at <paramref name="level"/> Full decoded and under its
    /// <c>FileCRC</c> (15); its header naming this store's database and the timeline it lies in, the timeline's file agreeing (16); the timeline
    /// continuous, a gap a warning (17); each point's copied tail one history with the archived copy under its ids (18).
    /// </summary>
    public static void Verify(IStoreFileSystem fs, StoreView view, VerifyLevel level, List<VerifyFinding> findings, ref long bytesRead)
    {
        var root = view.Paths.Root;
        var paths = new WalArchivePaths(root);
        foreach (var t in WalArchiveStore.ListTimelines(fs, root))
        {
            var archived = WalArchiveStore.Load(fs, root, t, trustIndex: false);
            var dir = paths.Timeline(t);
            foreach (var (file, reason) in archived.Unreadable)
            {
                findings.Add(new VerifyFinding("BKP-VER-15", VerifySeverity.Damage, Rel(t, file), -1, reason));
            }

            var timelineFile = archived.Timeline;
            if (timelineFile == null)
            {
                findings.Add(new VerifyFinding("BKP-VER-16", VerifySeverity.Damage, Rel(t, ArchiveFormat.TimelineFileName), -1,
                    "the timeline's timeline.json is missing or unreadable"));
            }
            else if (timelineFile.TimelineId != t || timelineFile.DatabaseId != view.Identity.DatabaseId)
            {
                findings.Add(new VerifyFinding("BKP-VER-16", VerifySeverity.Damage, Rel(t, ArchiveFormat.TimelineFileName), -1,
                    $"timeline.json names timeline {timelineFile.TimelineId} of database {timelineFile.DatabaseId}"));
            }

            long prevEnd = 0;
            foreach (var s in archived.Segments)
            {
                var name = Rel(t, s.File);
                if (s.Header.DatabaseId != view.Identity.DatabaseId)
                {
                    findings.Add(new VerifyFinding("BKP-VER-16", VerifySeverity.Damage, name, -1,
                        $"a segment of database {s.Header.DatabaseId}, in a store of {view.Identity.DatabaseId}"));
                }

                if (level == VerifyLevel.Full)
                {
                    Full(fs, Path.Combine(dir, s.File), s, name, findings, ref bytesRead);
                }

                // Records at or below the archive's end: two histories in one timeline (a hole is a gap, listed below as a warning).
                if (prevEnd > 0 && s.Footer.FirstRecordLsn != 0 && s.Footer.FirstRecordLsn <= prevEnd)
                {
                    findings.Add(new VerifyFinding("BKP-VER-17", VerifySeverity.Damage, name, -1,
                        $"its records from LSN {s.Footer.FirstRecordLsn} overlap the archive ending at LSN {prevEnd}: two histories in one timeline"));
                }

                prevEnd = Math.Max(prevEnd, s.End);
            }

            foreach (var g in Gaps(archived))
            {
                findings.Add(new VerifyFinding("BKP-VER-17", VerifySeverity.Advisory, Rel(t, ArchiveFormat.FileName(g.SegmentId)), -1,
                    $"LSN {g.FromLsn}..{g.ToLsn} is not archived ({g.Reason}): no restore crosses it"));
            }

            TailsAgree(fs, view, archived, findings);
        }
    }

    private static void Full(IStoreFileSystem fs, string path, ArchivedSegmentInfo s, string name, List<VerifyFinding> findings, ref long bytesRead)
    {
        var bytes = ArrayPool<byte>.Shared.Rent((int)s.Footer.ArchiveLength);
        try
        {
            using var reader = ArchivedSegmentReader.Open(fs, path);
            bytesRead += reader.FileLength;
            if (!reader.CheckFileCrc())
            {
                findings.Add(new VerifyFinding("BKP-VER-15", VerifySeverity.Damage, name, -1, "FileCRC mismatch"));
                return;
            }

            reader.Decode(bytes);
        }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException)
        {
            findings.Add(new VerifyFinding("BKP-VER-15", VerifySeverity.Damage, name, -1, ex.Message));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>BKP-VER-18: every point of the timeline whose tail segment has an archived copy under its id agrees with it (WA-11).</summary>
    private static void TailsAgree(IStoreFileSystem fs, StoreView view, ArchivedTimeline archived, List<VerifyFinding> findings)
    {
        foreach (var pack in view.Catalog.Packs)
        {
            if (pack.TimelineId != archived.TimelineId || pack.TailSegments == 0)
            {
                continue;
            }

            try
            {
                using var reader = PackReader.Open(fs, view.Paths.PackPath(pack.File));
                var tail = reader.LoadTailDirectory();
                for (var i = 0; i < tail.Segments.Length; i++)
                {
                    var copy = ArchiveRestorePlanner.Find(archived, tail.Segments[i].SegmentId);
                    if (copy != null && !ArchiveRestorePlanner.TailAgrees(fs, view, reader, tail, i, copy))
                    {
                        findings.Add(new VerifyFinding("BKP-VER-18", VerifySeverity.Damage, Rel(archived.TimelineId, copy.File), -1,
                            $"point #{pack.BackupId}'s copy of segment {copy.SegmentId} is another history: no restore to an LSN starts from it"));
                    }
                }
            }
            catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException)
            {
                // The pack's own checks (BKP-VER-02…09) report it.
            }
        }
    }

    /// <summary>Appends every frame of an archived segment read whole.</summary>
    private static void ReadFrames(IStoreFileSystem fs, string path, ArchivedSegmentInfo s, List<WalFrameSummary> into)
    {
        var length = (int)s.Footer.ArchiveLength;
        var bytes = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            using (var r = ArchivedSegmentReader.Open(fs, path))
            {
                r.Decode(bytes);
            }

            var name = Path.Combine("log", s.SegmentId.ToString("D16") + ".wal");
            var walView = WalTailView.OverPrefixes(new Dictionary<string, ReadOnlyMemory<byte>> { [name] = bytes.AsMemory(0, length) });
            using var reader = new WalSegmentReader(walView);
            if (reader.OpenSegment(name))
            {
                WalArchiveScanner.Frames(reader, into);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>When <paramref name="lsn"/> became durable, interpolated between the samples around it (WA-25); null with no sample at all.</summary>
    internal static DateTime? Interpolate(List<ArchiveTimeSample> samples, long lsn)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        var hi = samples.BinarySearch(new ArchiveTimeSample(lsn, 0), LsnOrder.Instance);
        if (hi >= 0)
        {
            return new DateTime(samples[hi].UtcTicks, DateTimeKind.Utc);
        }

        hi = ~hi;
        if (hi == 0 || hi == samples.Count)
        {
            return new DateTime(samples[hi == 0 ? 0 : samples.Count - 1].UtcTicks, DateTimeKind.Utc);
        }

        var a = samples[hi - 1];
        var b = samples[hi];
        var ticks = a.UtcTicks + (long)((double)(b.UtcTicks - a.UtcTicks) * (lsn - a.Lsn) / (b.Lsn - a.Lsn));
        return new DateTime(ticks, DateTimeKind.Utc);
    }


    private static BackupArchivedSegment Segment(ArchivedSegmentInfo s)
    {
        var flags = BackupArchivedSegmentFlags.None;
        flags |= s.Header.Adopted ? BackupArchivedSegmentFlags.Adopted : 0;
        flags |= (s.Footer.Flags & ArchiveFormat.FooterFlagTornTrimmed) != 0 ? BackupArchivedSegmentFlags.SealedAtCrash : 0;
        flags |= s.Footer.GapBefore ? BackupArchivedSegmentFlags.GapBefore : 0;
        flags |= (s.Footer.Flags & ArchiveFormat.FooterFlagCorruptMidSegment) != 0 ? BackupArchivedSegmentFlags.Corrupt : 0;
        return new BackupArchivedSegment(s.SegmentId, s.FirstLsn, s.End, s.Header.SealedUtcTicks == 0 ? null : new DateTime(s.Header.SealedUtcTicks, DateTimeKind.Utc),
            new DateTime(s.Footer.ArchivedUtcTicks, DateTimeKind.Utc), s.FileLength, s.Footer.ArchiveLength, flags, s.SessionStart);
    }

    private static List<BackupArchiveGap> PublicGaps(List<ArchiveGap> gaps, IReadOnlyList<CatalogPack> restorable, Guid timeline)
    {
        var result = new List<BackupArchiveGap>(gaps.Count);
        foreach (var g in gaps)
        {
            result.Add(new BackupArchiveGap(g.FromLsn, g.ToLsn, new DateTime(g.RecordedUtcTicks, DateTimeKind.Utc), g.Reason, Covered(g, restorable, timeline)));
        }

        return result;
    }

    internal static BackupArchiveGapReason Public(ArchiveGapReason reason) => reason switch
    {
        ArchiveGapReason.BacklogReleased => BackupArchiveGapReason.BacklogReleased,
        ArchiveGapReason.ArchivingOff => BackupArchiveGapReason.ArchivingOff,
        ArchiveGapReason.TornAtCrash => BackupArchiveGapReason.TornAtCrash,
        _ => BackupArchiveGapReason.Unknown,
    };

    internal static BackupTimelineReason Public(TimelineReason reason) => reason switch
    {
        TimelineReason.Genesis => BackupTimelineReason.Genesis,
        TimelineReason.Restore => BackupTimelineReason.Restore,
        TimelineReason.SchemaChange => BackupTimelineReason.SchemaChange,
        TimelineReason.BulkLoad => BackupTimelineReason.BulkLoad,
        _ => BackupTimelineReason.Unknown,
    };

    private static string Rel(Guid timeline, string file) => Path.Combine("wal", timeline.ToString("D"), file);

    private sealed class LsnOrder : IComparer<ArchiveTimeSample>
    {
        public static readonly LsnOrder Instance = new();

        public int Compare(ArchiveTimeSample x, ArchiveTimeSample y) => x.Lsn.CompareTo(y.Lsn);
    }
}
