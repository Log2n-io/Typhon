using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;

namespace Typhon.Engine.Internals;

/// <summary>One WAL segment of an archive restore: where its bytes come from and how many of them the restored segment keeps.</summary>
/// <param name="SegmentId">Its id, the restored file's name.</param>
/// <param name="FirstLsn">Its header's first LSN.</param>
/// <param name="DeclaredSize">Its header's size: the restored file's length, zeros past the kept bytes.</param>
/// <param name="TailIndex">Its index in the base point's TAILDIR when its bytes come from the tail; -1 when they come from the archive.</param>
/// <param name="Archived">Its archived copy when its bytes come from the archive; null otherwise.</param>
/// <param name="KeepBytes">The bytes kept from offset 0: the whole copy, or the cut (the end of the last frame at or below the target).</param>
/// <param name="RawCrc">The CRC of its source's bytes (the tail's or the archive's): what the plan's fingerprint names.</param>
internal sealed record PlannedWalSegment(long SegmentId, long FirstLsn, uint DeclaredSize, int TailIndex, ArchivedSegmentInfo Archived, int KeepBytes,
    uint RawCrc);

/// <summary>What a restore to an LSN or a time restores (impl/1418 §3.7).</summary>
internal sealed class ArchiveRestorePlan
{
    /// <summary>The base point <c>X</c>: the newest restorable point of the timeline at or below the target whose tail agrees with the archive.</summary>
    public required ResolvedChain Chain { get; init; }

    /// <summary>The timeline <c>X</c> and the target are on.</summary>
    public required Guid TimelineId { get; init; }

    /// <summary><c>L</c>: the LSN asked for, or the one an instant resolved to.</summary>
    public required long TargetLsn { get; init; }

    /// <summary><c>L'</c>: the highest LSN at or below <c>L</c> that ends a frame — the restored database's last LSN.</summary>
    public required long EffectiveLsn { get; init; }

    /// <summary>The sample an instant resolved through; null for an LSN target.</summary>
    public ArchiveTimeSample? SampleUsed { get; init; }

    /// <summary>The restored WAL, ascending id.</summary>
    public required IReadOnlyList<PlannedWalSegment> Segments { get; init; }

    /// <summary>The archived segments the restore reads.</summary>
    public int ArchivedSegments
    {
        get
        {
            var n = 0;
            foreach (var s in Segments)
            {
                n += s.Archived != null ? 1 : 0;
            }

            return n;
        }
    }

    /// <summary>The bytes of the archived segment files the restore reads.</summary>
    public long ArchivedBytes
    {
        get
        {
            long n = 0;
            foreach (var s in Segments)
            {
                n += s.Archived?.FileLength ?? 0;
            }

            return n;
        }
    }
}

/// <summary>
/// Plans a restore to an LSN or a time (impl/1418 §3.7, BKP-11): resolves the target, the timeline that holds it through the ancestry, the base point
/// whose tail agrees with the archive, the archived segments that continue it without a gap, and the cut at the last frame at or below the target. Reads
/// only; refuses with <see cref="RestoreTargetUnreachableException"/>.
/// </summary>
internal static class ArchiveRestorePlanner
{
    /// <summary>Plans the restore of <paramref name="target"/> (an archive target) from the store <paramref name="view"/> describes.</summary>
    public static ArchiveRestorePlan Plan(IStoreFileSystem fs, StoreView view, PointSelector target, ILogger log = null)
    {
        if (!target.IsArchiveTarget)
        {
            throw new ArgumentException($"{target} is a point, not an archive target.", nameof(target));
        }

        log ??= NullLogger.Instance;
        var timelines = LoadTimelines(fs, view.Paths.Root);
        var t = target.TimelineId ?? NewestTimeline(view, timelines);
        if (t == Guid.Empty)
        {
            Refuse(RestoreUnreachableReason.TimelineUnknown, Guid.Empty, 0, 0, [], "the store holds no timeline: no point and no archived WAL");
        }

        if (!timelines.ContainsKey(t) && !HasPointOn(view, t))
        {
            Refuse(RestoreUnreachableReason.TimelineUnknown, t, 0, 0, [], $"the store holds nothing of timeline {t}: no point and no archived WAL");
        }

        long l;
        ArchiveTimeSample? sample = null;
        if (target.IsTimeTarget)
        {
            var (resolved, used, on) = ResolveTime(fs, view.Paths.Root, timelines, t, target.TimeUtc);
            l = resolved;
            sample = used;
            t = on;
        }
        else
        {
            l = target.Lsn;
        }

        t = Ancestor(timelines, t, l);
        var archive = timelines.TryGetValue(t, out var a) ? a : null;

        // 3 BASE: the newest restorable point of t at or below L whose tail agrees with t's archive.
        var nearest = NearestRestorable(view, t, archive);
        var candidates = new List<CatalogPack>();
        foreach (var id in ChainResolver.RestorablePoints(view))
        {
            var (compacted, original) = view.Find(id);
            var pack = compacted ?? original;
            if (pack != null && pack.TimelineId == t && pack.PointLsn <= l)
            {
                candidates.Add(pack);
            }
        }

        candidates.Sort(static (x, y) => x.PointLsn != y.PointLsn ? y.PointLsn.CompareTo(x.PointLsn) : y.BackupId.CompareTo(x.BackupId));
        if (candidates.Count == 0)
        {
            Refuse(RestoreUnreachableReason.NoBase, t, l, 0, nearest, $"no restorable point of timeline {t} lies at or below LSN {l}");
        }

        RestoreTargetUnreachableException last = null;
        foreach (var candidate in candidates)
        {
            var chain = ChainResolver.Resolve(view, candidate.BackupId);
            try
            {
                var segments = PlanSegments(fs, view, chain, archive, t, l, nearest, log, out var effective);
                if (segments == null)
                {
                    continue;   // its tail disagrees with the archive: another history (WA-11, WA-14)
                }

                return new ArchiveRestorePlan
                {
                    Chain = chain, TimelineId = t, TargetLsn = l, EffectiveLsn = effective, SampleUsed = sample, Segments = segments,
                };
            }
            catch (RestoreTargetUnreachableException e) when (e.Reason == RestoreUnreachableReason.Gap)
            {
                // An older base may reach L where this one cannot (a gap between them and L is the same gap, but a gap before the older base's C is not).
                last ??= e;
            }
        }

        if (last != null)
        {
            throw last;
        }

        Refuse(RestoreUnreachableReason.NoBase, t, l, 0, nearest, $"every point of timeline {t} at or below LSN {l} disagrees with the archive");
        return null;
    }

    /// <summary>
    /// The base's WAL for a target (steps 4 and 5): its tail segments, each replaced by its archived copy of <paramref name="t"/> once that agrees with
    /// it, then <paramref name="t"/>'s archived segments by LSN continuity; then the cut. Null when the tail disagrees with the archive.
    /// </summary>
    private static List<PlannedWalSegment> PlanSegments(IStoreFileSystem fs, StoreView view, ResolvedChain chain, ArchivedTimeline archive, Guid t, long l,
        IReadOnlyList<long> nearest, ILogger log, out long effective)
    {
        effective = 0;
        var point = chain.Point;
        var candidates = new List<PlannedWalSegment>();
        using (var reader = PackReader.Open(fs, view.Paths.PackPath(point.File)))
        {
            var tail = reader.LoadTailDirectory();
            for (var i = 0; i < tail.Segments.Length; i++)
            {
                var seg = tail.Segments[i];
                var copy = Find(archive, seg.SegmentId);
                if (copy == null)
                {
                    candidates.Add(new PlannedWalSegment(seg.SegmentId, seg.FirstLsn, 0, i, null, seg.RawLength, seg.RawCrc));
                    continue;
                }

                if (!TailAgrees(fs, view, reader, tail, i, copy))
                {
                    WalArchiveLog.BaseSkipped(log, point.BackupId, seg.SegmentId, t);
                    return null;
                }

                candidates.Add(new PlannedWalSegment(seg.SegmentId, seg.FirstLsn, copy.Header.DeclaredSize, -1, copy, (int)copy.Footer.ArchiveLength,
                    copy.Footer.RawCrc));
            }

            // Trailing tail segments holding no record (the active segment of a clean close, an offline point copying it whole) are never archived — the
            // next open deletes them — and carry nothing: they neither end the WAL nor stop the archive continuing it.
            while (candidates.Count > 0 && candidates[^1].Archived == null && TailSegmentEmpty(reader, tail, candidates[^1].TailIndex))
            {
                candidates.RemoveAt(candidates.Count - 1);
            }
        }

        // The archive continues the tail: segments of t past its last one, contiguous by LSN (WA-7, WA-8), as far as L. A tail whose last segment is not
        // archived cannot be continued (its copy is a prefix); a point with no tail (a clean image) starts at the segment ending at or after its C (C2).
        var lastArchived = candidates.Count > 0 ? candidates[^1].Archived : null;
        if (archive != null && (candidates.Count == 0 || lastArchived != null))
        {
            var prevEnd = lastArchived?.End ?? 0;
            foreach (var s in archive.Segments)
            {
                if (candidates.Count == 0 && s.End < point.CheckpointLsn)
                {
                    continue;   // wholly below C: recovery would skip it
                }

                if (s.End <= prevEnd)
                {
                    continue;   // already in: a tail segment's archived copy
                }

                // A hole before s refuses a target past what precedes it — inside the hole too, though s itself starts past L.
                var end = prevEnd > 0 ? prevEnd : point.CheckpointLsn;
                if (!s.Continues(end) || (prevEnd > 0 && s.Footer.GapBefore))
                {
                    if (l <= end)
                    {
                        break;
                    }

                    var to = Math.Max(s.FirstLsn, s.Footer.FirstRecordLsn) - 1;
                    Refuse(RestoreUnreachableReason.Gap, t, end + 1, to, nearest, $"the archive of timeline {t} misses LSN {end + 1}..{to}");
                }

                if (s.FirstLsn > l)
                {
                    break;
                }

                candidates.Add(new PlannedWalSegment(s.SegmentId, s.FirstLsn, s.Header.DeclaredSize, -1, s, (int)s.Footer.ArchiveLength, s.Footer.RawCrc));
                prevEnd = s.End;
            }
        }

        // Records a crash's recovery applied that the archive lost, or a break in a segment, inside (C_X, L]: no restore crosses them.
        foreach (var c in candidates)
        {
            var f = c.Archived?.Footer;
            if (f is { DroppedToLsn: > 0 } footer && footer.DroppedFromLsn <= l && footer.DroppedToLsn > point.CheckpointLsn)
            {
                Refuse(RestoreUnreachableReason.Gap, t, footer.DroppedFromLsn, footer.DroppedToLsn, nearest,
                    $"archived segment {c.SegmentId} lacks LSN {footer.DroppedFromLsn}..{footer.DroppedToLsn}");
            }
        }

        for (var i = 1; i < candidates.Count; i++)
        {
            if (candidates[i].SegmentId <= candidates[i - 1].SegmentId)
            {
                Refuse(RestoreUnreachableReason.Gap, t, candidates[i].FirstLsn, 0, nearest,
                    $"segment ids do not ascend with LSNs ({candidates[i - 1].SegmentId} then {candidates[i].SegmentId}): the WAL cannot be laid out");
            }
        }

        // 5 CUT: each segment in order, kept whole while every frame of it ends at or below L, cut at the first that holds a frame past L.
        var plan = new List<PlannedWalSegment>(candidates.Count);
        long coverage = 0;
        var cutDone = false;
        using (var readers = new ChainReaders(fs, view, point))
        {
            foreach (var c in candidates)
            {
                if (c.Archived != null && c.Archived.End <= l)
                {
                    plan.Add(c);
                    effective = Math.Max(effective, c.Archived.Footer.LastRecordLsn);
                    coverage = Math.Max(coverage, c.Archived.End);
                    continue;
                }

                var (cut, declared) = readers.Cut(c, l);
                plan.Add(c with { KeepBytes = cut.CutOffset, DeclaredSize = declared });
                effective = Math.Max(effective, cut.EffectiveLsn);
                coverage = Math.Max(coverage, c.Archived?.End ?? cut.EffectiveLsn);
                if (cut.EndsWithinSegment)
                {
                    cutDone = true;
                    break;
                }
            }
        }

        if (!cutDone && l > coverage)
        {
            var reason = archive == null || candidates.Count == 0 || candidates[^1].Archived != null
                ? RestoreUnreachableReason.BeyondArchive
                : RestoreUnreachableReason.Gap;
            Refuse(reason, t, coverage + 1, l, nearest,
                reason == RestoreUnreachableReason.Gap
                    ? $"the base's last WAL segment is not archived: its copy in point #{point.BackupId} ends at LSN {coverage}"
                    : $"the WAL of timeline {t} is archived through LSN {coverage}");
        }

        if (effective < point.PointLsn)
        {
            throw new InvalidOperationException($"BKP-11: the cut at LSN {effective} lies below point #{point.BackupId}'s P {point.PointLsn}.");
        }

        return plan;
    }

    /// <summary>
    /// Whether the tail's copy and the archived copy are one history (WA-11): equal over the bytes both hold, and nothing in the tail past the archived
    /// bytes but zeros (an offline point copies a segment whole, at its declared size; the archive keeps it to its last frame).
    /// </summary>
    /// <remarks>
    /// The segment-sized buffers (64 MiB by default) are native and freed on return: rented from the shared array pool, they stayed in it for the life of
    /// a long-lived host (the Workbench), two per tail segment per candidate base.
    /// </remarks>
    internal static bool TailAgrees(IStoreFileSystem fs, StoreView view, PackReader reader, TailDirectory tail, int index, ArchivedSegmentInfo copy)
    {
        var raw = tail.Segments[index].RawLength;
        var archivedLength = (int)copy.Footer.ArchiveLength;
        using var tailBuffer = NativeBuffer.Allocate(Math.Max(raw, 1), null, null);   // the planner runs with no engine
        using var archivedBuffer = NativeBuffer.Allocate(Math.Max(archivedLength, 1), null, null);
        var tailBytes = tailBuffer.Span;
        var archived = archivedBuffer.Span;
        reader.DecodeTailSegment(tail, index, tailBytes);
        using (var r = ArchivedSegmentReader.Open(fs, SegmentPath(view, copy)))
        {
            r.Decode(archived);
        }

        var common = Math.Min(raw, archivedLength);
        return tailBytes[..common].SequenceEqual(archived[..common]) && (raw <= archivedLength || tailBytes[archivedLength..raw].IndexOfAnyExcept((byte)0) < 0);
    }

    private static bool TailSegmentEmpty(PackReader reader, TailDirectory tail, int index)
    {
        var raw = tail.Segments[index].RawLength;
        using var buffer = NativeBuffer.Allocate(Math.Max(raw, 1), null, null);
        reader.DecodeTailSegment(tail, index, buffer.Span);
        var path = Path.Combine("tail", tail.Segments[index].SegmentId.ToString("D16") + ".wal");
        using var walReader = new WalSegmentReader(WalTailView.OverPrefixes(new Dictionary<string, ReadOnlyMemory<byte>> { [path] = buffer.Memory[..raw] }));
        return walReader.OpenSegment(path) && !walReader.TryReadNext(out _, out _) && !walReader.WasTruncated;
    }

    internal static string SegmentPath(StoreView view, ArchivedSegmentInfo segment) =>
        Path.Combine(new WalArchivePaths(view.Paths.Root).Timeline(segment.Header.TimelineId), segment.File);

    internal static ArchivedSegmentInfo Find(ArchivedTimeline archive, long segmentId)
    {
        if (archive == null)
        {
            return null;
        }

        foreach (var s in archive.Segments)
        {
            if (s.SegmentId == segmentId)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>
    /// Every timeline of the store's archive, read from its files (restore re-derives continuity from them, never from the index): each file's header
    /// and footer only — its samples are read by <see cref="ResolveTime"/>, for the segments it visits.
    /// </summary>
    internal static Dictionary<Guid, ArchivedTimeline> LoadTimelines(IStoreFileSystem fs, string root)
    {
        var result = new Dictionary<Guid, ArchivedTimeline>();
        foreach (var t in WalArchiveStore.ListTimelines(fs, root))
        {
            result[t] = WalArchiveStore.Load(fs, root, t, trustIndex: false, sampleBounds: false);
        }

        return result;
    }

    /// <summary>The timeline with the latest activity: its newest archived segment or point.</summary>
    internal static Guid NewestTimeline(StoreView view, Dictionary<Guid, ArchivedTimeline> timelines)
    {
        var best = Guid.Empty;
        long bestTicks = -1;
        foreach (var (id, t) in timelines)
        {
            foreach (var s in t.Segments)
            {
                if (s.Footer.ArchivedUtcTicks > bestTicks)
                {
                    bestTicks = s.Footer.ArchivedUtcTicks;
                    best = id;
                }
            }
        }

        foreach (var p in view.Catalog.Packs)
        {
            if (p.TailEndUtc.Ticks > bestTicks)
            {
                bestTicks = p.TailEndUtc.Ticks;
                best = p.TimelineId;
            }
        }

        return best;
    }

    /// <summary>The timeline holding <paramref name="lsn"/>, walking back from <paramref name="t"/> while it lies at or below the origin (§3.6).</summary>
    internal static Guid Ancestor(Dictionary<Guid, ArchivedTimeline> timelines, Guid t, long lsn)
    {
        for (var hops = 0; hops < 1024; hops++)
        {
            if (!timelines.TryGetValue(t, out var archived) || archived.Timeline is not { ParentTimelineId: { } parent } file || lsn > file.OriginLsn)
            {
                return t;
            }

            t = parent;
        }

        return t;
    }

    /// <summary>
    /// <c>L</c> for an instant (06 §3, BKP-11): the highest LSN sampled durable at or before <paramref name="utc"/> on <paramref name="t"/>'s ancestry —
    /// a timeline's own samples first, then its parent's at or below its origin. Never a frame durable after the instant. Samples are read newest segment
    /// first, and only until the instant is found; a segment whose samples cannot be read is passed over, as an unreadable one is.
    /// </summary>
    internal static (long Lsn, ArchiveTimeSample Sample, Guid Timeline) ResolveTime(IStoreFileSystem fs, string root,
        Dictionary<Guid, ArchivedTimeline> timelines, Guid t, DateTime utc)
    {
        var ticks = utc.Ticks;
        var upper = long.MaxValue;
        for (var hops = 0; hops < 1024 && timelines.TryGetValue(t, out var archive); hops++)
        {
            for (var i = archive.Segments.Count - 1; i >= 0; i--)
            {
                var s = archive.Segments[i];
                if (s.Header.FirstLsn - 1 > upper)
                {
                    continue;   // wholly past the child's origin: none of its samples may be used
                }

                ArchiveTimeSample[] samples;
                try
                {
                    using var reader = ArchivedSegmentReader.Open(fs, Path.Combine(new WalArchivePaths(root).Timeline(t), s.File));
                    samples = reader.ReadSamples();
                }
                catch (Exception e) when (e is BackupException or IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                ArchiveTimeSample? best = null;
                foreach (var sample in samples)
                {
                    if (sample.UtcTicks <= ticks && sample.Lsn <= upper)
                    {
                        best = sample;
                    }
                }

                if (best is { } found)
                {
                    return (found.Lsn, found, t);
                }
            }

            if (archive.Timeline is not { ParentTimelineId: { } parent } file)
            {
                break;
            }

            upper = file.OriginLsn;
            t = parent;
        }

        Refuse(RestoreUnreachableReason.BeforeCoverage, t, 0, 0, [], $"no time sample of the archive lies at or before {utc:O}");
        return default;
    }

    private static bool HasPointOn(StoreView view, Guid t)
    {
        foreach (var p in view.Catalog.Packs)
        {
            if (p.TimelineId == t)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What can be restored on <paramref name="t"/>: its restorable points' <c>P</c>, and the archive's end.</summary>
    internal static List<long> NearestRestorable(StoreView view, Guid t, ArchivedTimeline archive)
    {
        var result = new SortedSet<long>();
        foreach (var id in ChainResolver.RestorablePoints(view))
        {
            var (compacted, original) = view.Find(id);
            var pack = compacted ?? original;
            if (pack != null && pack.TimelineId == t)
            {
                result.Add(pack.PointLsn);
            }
        }

        if (archive is { Segments.Count: > 0 })
        {
            result.Add(archive.Segments[^1].End);
        }

        return [.. result];
    }

    private static void Refuse(RestoreUnreachableReason reason, Guid t, long from, long to, IReadOnlyList<long> nearest, string detail) =>
        throw new RestoreTargetUnreachableException(reason, t, from, to, nearest, detail);

    /// <summary>Decodes a planned segment's bytes and finds its cut; holds the base pack's reader open across segments.</summary>
    private sealed class ChainReaders(IStoreFileSystem fs, StoreView view, CatalogPack point) : IDisposable
    {
        private PackReader _pack;
        private TailDirectory _tail;

        public (WalCut Cut, uint Declared) Cut(PlannedWalSegment segment, long target)
        {
            var length = segment.KeepBytes;
            using var buffer = NativeBuffer.Allocate(Math.Max(length, 1), null, null);   // native, freed on return (see TailAgrees)
            uint declared;
            if (segment.Archived != null)
            {
                using var r = ArchivedSegmentReader.Open(fs, SegmentPath(view, segment.Archived));
                r.Decode(buffer.Span);
                declared = segment.Archived.Header.DeclaredSize;
            }
            else
            {
                _pack ??= PackReader.Open(fs, view.Paths.PackPath(point.File));
                _tail ??= _pack.LoadTailDirectory();
                declared = _pack.DecodeTailSegment(_tail, segment.TailIndex, buffer.Span);
            }

            var path = Path.Combine("restore", segment.SegmentId.ToString("D16") + ".wal");
            var walView = WalTailView.OverPrefixes(new Dictionary<string, ReadOnlyMemory<byte>> { [path] = buffer.Memory[..length] });
            using var reader = new WalSegmentReader(walView);
            if (!reader.OpenSegment(path))
            {
                PackFormat.ThrowCorrupt(path, "Tail", -1, $"WAL segment {segment.SegmentId} has no valid header");
            }

            return (WalArchiveScanner.FindCut(reader, target), declared);
        }

        public void Dispose() => _pack?.Dispose();
    }
}
