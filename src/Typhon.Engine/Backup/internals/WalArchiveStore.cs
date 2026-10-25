using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Typhon.Engine.Internals;

/// <summary>What <see cref="WalArchiveStore.PruneArchive"/> did.</summary>
/// <param name="Deleted">The archived segments deleted, per timeline: (timeline, segment file name).</param>
/// <param name="TimelinesDeleted">Timelines removed whole (absent from the floors and not the live one).</param>
/// <param name="Skipped">Timelines left for the next run because their <c>archive.lock</c> was busy (an archiver mid-publish), with why.</param>
internal sealed record ArchivePruneResult(IReadOnlyList<(Guid TimelineId, string File)> Deleted, IReadOnlyList<Guid> TimelinesDeleted,
    IReadOnlyList<(Guid TimelineId, string Reason)> Skipped);

/// <summary>The paths of a store's WAL archive (impl/1418 §3.5).</summary>
internal readonly struct WalArchivePaths
{
    public WalArchivePaths(string storeRoot)
    {
        Root = storeRoot;
        Wal = Path.Combine(storeRoot, ArchiveFormat.WalDirectoryName);
    }

    public string Root { get; }

    /// <summary><c>{store}/wal</c>.</summary>
    public string Wal { get; }

    public string Timeline(Guid timelineId) => Path.Combine(Wal, ArchiveFormat.TimelineDirectoryName(timelineId));

    public string Lock(Guid timelineId) => Path.Combine(Timeline(timelineId), ArchiveFormat.LockFileName);

    public string LockHolder(Guid timelineId) => Path.Combine(Timeline(timelineId), ArchiveFormat.LockHolderFileName);

    public string TimelineFile(Guid timelineId) => Path.Combine(Timeline(timelineId), ArchiveFormat.TimelineFileName);

    public string Index(Guid timelineId) => Path.Combine(Timeline(timelineId), ArchiveFormat.IndexFileName);

    public string Segment(Guid timelineId, long segmentId) => Path.Combine(Timeline(timelineId), ArchiveFormat.FileName(segmentId));
}

/// <summary>A timeline's archived segments as a reader sees them: the readable ones by LSN, and the files that could not be read.</summary>
internal sealed class ArchivedTimeline
{
    public required Guid TimelineId { get; init; }

    /// <summary>The timeline file, or null when absent or unreadable.</summary>
    public TimelineFile Timeline { get; init; }

    /// <summary>The readable segments, ascending first LSN.</summary>
    public required IReadOnlyList<ArchivedSegmentInfo> Segments { get; init; }

    /// <summary>Files that could not be read, with why.</summary>
    public required IReadOnlyList<(string File, string Reason)> Unreadable { get; init; }

    /// <summary>Whether the segments came from the index cache.</summary>
    public bool FromIndex { get; init; }

    /// <summary>The last LSN archived: the newest segment's; 0 when none.</summary>
    public long ArchivedThroughLsn => Segments.Count == 0 ? 0 : Segments[^1].End;
}

/// <summary>How a publish ended.</summary>
internal enum ArchivePublishOutcome : byte
{
    /// <summary>The segment is durable under its name.</summary>
    Published,

    /// <summary>The same bytes were already archived under that name (a crash before the floor rose): nothing to do.</summary>
    AlreadyArchived,

    /// <summary>The name holds other bytes: another history shares this timeline (WA-14).</summary>
    Diverged,
}

/// <summary>
/// The WAL archive of a backup store: <c>wal/&lt;TimelineId&gt;/</c> per timeline (impl/1418 §3.5). Every <c>.twal</c> is the authority; the index is a
/// cache rebuilt from their headers and footers whenever its fingerprint disagrees with the listing (WA-6). Archive writes take no <c>store.lock</c>: a
/// timeline's <c>archive.lock</c>, held for one operation (WA-24, WA-14).
/// </summary>
internal static class WalArchiveStore
{
    /// <summary>The timelines a store's archive holds (every directory of <c>wal/</c> named as a timeline id).</summary>
    public static List<Guid> ListTimelines(IStoreFileSystem fs, string storeRoot)
    {
        var result = new List<Guid>();
        var wal = new WalArchivePaths(storeRoot).Wal;
        if (!fs.DirectoryExists(wal))
        {
            return result;
        }

        foreach (var dir in fs.EnumerateDirectories(wal))
        {
            if (Guid.TryParseExact(dir, "D", out var id))
            {
                result.Add(id);
            }
        }

        result.Sort();
        return result;
    }

    /// <summary>Reads <c>timeline.json</c>; null when absent or unreadable.</summary>
    public static TimelineFile ReadTimelineFile(IStoreFileSystem fs, string storeRoot, Guid timelineId)
    {
        var path = new WalArchivePaths(storeRoot).TimelineFile(timelineId);
        if (!fs.FileExists(path))
        {
            return null;
        }

        try
        {
            var file = JsonSerializer.Deserialize(StoreFileSystem.ReadAllBytes(fs, path), ArchiveJsonContext.Default.TimelineFile);
            return file != null && file.FormatVersion > 0 && file.TimelineId == timelineId ? file : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A timeline's archived segments, from the index when it matches the listing (and <paramref name="trustIndex"/>), else from every file's header and
    /// footer — and its samples' bounds, read and checked, unless <paramref name="sampleBounds"/> is false (continuity needs only the header and the
    /// footer; the restore reads a segment's samples when it resolves an instant). Reads only: no lock, nothing written.
    /// </summary>
    public static ArchivedTimeline Load(IStoreFileSystem fs, string storeRoot, Guid timelineId, bool trustIndex = true, bool sampleBounds = true)
    {
        var paths = new WalArchivePaths(storeRoot);
        var dir = paths.Timeline(timelineId);
        var listing = SegmentListing(fs, dir);
        var fingerprint = Fingerprint(listing);
        if (trustIndex && ArchiveIndex.TryRead(fs, paths.Index(timelineId), timelineId, fingerprint, out var cached))
        {
            return new ArchivedTimeline
            {
                TimelineId = timelineId, Timeline = ReadTimelineFile(fs, storeRoot, timelineId), Segments = cached, Unreadable = [], FromIndex = true,
            };
        }

        var segments = new List<ArchivedSegmentInfo>(listing.Count);
        var unreadable = new List<(string, string)>();
        foreach (var f in listing)
        {
            try
            {
                using var reader = ArchivedSegmentReader.Open(fs, Path.Combine(dir, f.Name));
                ArchiveFormat.TryParseFileName(f.Name, out var id, out _);
                if (reader.Header.SegmentId != id || reader.Header.TimelineId != timelineId)
                {
                    PackFormat.ThrowCorrupt(f.Name, "Header", -1, $"names segment {reader.Header.SegmentId} of timeline {reader.Header.TimelineId}");
                }

                segments.Add(Describe(f.Name, reader, sampleBounds));
            }
            catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException)
            {
                unreadable.Add((f.Name, ex.Message));
            }
        }

        segments.Sort(static (a, b) => a.FirstLsn != b.FirstLsn ? a.FirstLsn.CompareTo(b.FirstLsn) : a.SegmentId.CompareTo(b.SegmentId));
        return new ArchivedTimeline
        {
            TimelineId = timelineId, Timeline = ReadTimelineFile(fs, storeRoot, timelineId), Segments = segments, Unreadable = unreadable, FromIndex = false,
        };
    }

    /// <summary>
    /// What a reader says about its segment: header, footer and, with <paramref name="sampleBounds"/>, its samples' bounds (the samples are read and
    /// checked); without, the bounds are 0.
    /// </summary>
    public static ArchivedSegmentInfo Describe(string fileName, ArchivedSegmentReader reader, bool sampleBounds = true)
    {
        var samples = sampleBounds ? reader.ReadSamples() : [];
        return new ArchivedSegmentInfo
        {
            File = fileName,
            FileLength = reader.FileLength,
            Header = reader.Header,
            Footer = reader.Footer,
            FirstSampleLsn = samples.Length > 0 ? samples[0].Lsn : 0,
            FirstSampleUtcTicks = samples.Length > 0 ? samples[0].UtcTicks : 0,
            LastSampleLsn = samples.Length > 0 ? samples[^1].Lsn : 0,
            LastSampleUtcTicks = samples.Length > 0 ? samples[^1].UtcTicks : 0,
        };
    }

    /// <summary>The published <c>.twal</c> files of a timeline directory, by name.</summary>
    public static List<StoreFileEntry> SegmentListing(IStoreFileSystem fs, string timelineDirectory)
    {
        var result = new List<StoreFileEntry>();
        foreach (var f in fs.EnumerateFiles(timelineDirectory))
        {
            if (ArchiveFormat.TryParseFileName(f.Name, out _, out var temp) && !temp)
            {
                result.Add(f);
            }
        }

        return result;
    }

    /// <summary>
    /// CRC32C of the (name, length) listing in ordinal name order: what the index must have been written for (DC-10's pattern). The order is the names',
    /// not the file system's (alphabetical on NTFS, hashed on ext4), so the archiver can extend it one published segment at a time
    /// (<see cref="FingerprintAppend"/>): segment ids, and so names, ascend.
    /// </summary>
    public static uint Fingerprint(IReadOnlyList<StoreFileEntry> listing) => Crc32CUtil.Finish(FingerprintState(listing, out _));

    /// <summary>The unfinished CRC of <paramref name="listing"/> in name order, and its last name (null when empty).</summary>
    public static uint FingerprintState(IReadOnlyList<StoreFileEntry> listing, out string lastName)
    {
        var sorted = new StoreFileEntry[listing.Count];
        for (var i = 0; i < sorted.Length; i++)
        {
            sorted[i] = listing[i];
        }

        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        var crc = Crc32CUtil.Start;
        foreach (var f in sorted)
        {
            crc = FingerprintAppend(crc, f.Name, f.Length);
        }

        lastName = sorted.Length > 0 ? sorted[^1].Name : null;
        return crc;
    }

    /// <summary>Extends an unfinished fingerprint CRC by one entry; allocates nothing for a segment's name.</summary>
    public static uint FingerprintAppend(uint crc, string name, long length)
    {
        Span<byte> bytes = stackalloc byte[256];
        crc = name.Length <= 64 ? Crc32CUtil.Append(crc, bytes[..Encoding.UTF8.GetBytes(name, bytes)]) : Crc32CUtil.Append(crc, Encoding.UTF8.GetBytes(name));
        BinaryPrimitives.WriteInt64LittleEndian(bytes, length);
        return Crc32CUtil.Append(crc, bytes[..8]);
    }

    /// <summary>
    /// Retention of the archive (#1415, 05 §3 step 5), engine-free, under <c>store.lock</c>: per timeline, archived segments whose last LSN is below its
    /// floor are deleted oldest first, then the directory flushed once (a crash persists any subset of them: every one lies below every kept point's floor,
    /// so none decides a restore, and a hole among them is covered); a timeline absent from <paramref name="floors"/> is deleted whole, except
    /// <paramref name="liveTimeline"/>, kept whole. A timeline whose <c>archive.lock</c> is busy is skipped and reported.
    /// </summary>
    /// <param name="session">The store, opened for writing (its lock held).</param>
    /// <param name="floors">Per kept timeline, the lowest LSN a kept point may replay from (<c>C</c> of its oldest kept point).</param>
    /// <param name="liveTimeline">The database's current timeline: never deleted whole.</param>
    internal static ArchivePruneResult PruneArchive(BackupStore session, IReadOnlyList<(Guid TimelineId, long KeepFromLsn)> floors, Guid liveTimeline)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(floors);
        return Prune(session.FileSystem, session.Root, floors, liveTimeline, session.Log);
    }

    internal static ArchivePruneResult Prune(IStoreFileSystem fs, string storeRoot, IReadOnlyList<(Guid TimelineId, long KeepFromLsn)> floors,
        Guid liveTimeline, ILogger log)
    {
        log ??= NullLogger.Instance;
        var paths = new WalArchivePaths(storeRoot);
        var deleted = new List<(Guid, string)>();
        var removed = new List<Guid>();
        var skipped = new List<(Guid, string)>();
        var floorOf = new Dictionary<Guid, long>();
        foreach (var (t, keep) in floors)
        {
            floorOf[t] = keep;
        }

        foreach (var timeline in ListTimelines(fs, storeRoot))
        {
            var kept = floorOf.TryGetValue(timeline, out var keepFrom);
            if (!kept && timeline == liveTimeline)
            {
                continue;   // the live timeline with no kept point: kept whole
            }

            var dir = paths.Timeline(timeline);
            var held = fs.TryLockTransient(paths.Lock(timeline));
            if (held == null)
            {
                skipped.Add((timeline, "its archive.lock is held (an archiver is publishing)"));
                WalArchiveLog.PruneSkipped(log, timeline);
                continue;
            }

            using (held)
            {
                var loaded = Load(fs, storeRoot, timeline, trustIndex: false, sampleBounds: false);
                var deletedHere = 0;
                foreach (var s in loaded.Segments)
                {
                    if (kept && s.LastLsn >= keepFrom)
                    {
                        break;   // ascending: nothing later is below the floor
                    }

                    fs.DeleteFile(Path.Combine(dir, s.File));
                    deleted.Add((timeline, s.File));
                    deletedHere++;
                }

                if (deletedHere > 0)
                {
                    fs.FlushDirectory(dir);
                }

                fs.DeleteFile(paths.Index(timeline));
                if (!kept)
                {
                    foreach (var (file, _) in loaded.Unreadable)
                    {
                        fs.DeleteFile(Path.Combine(dir, file));
                    }

                    fs.DeleteFile(paths.TimelineFile(timeline));
                    fs.DeleteFile(paths.LockHolder(timeline));
                }
            }

            if (!kept)
            {
                fs.DeleteDirectory(dir);
                fs.FlushDirectory(paths.Wal);
                removed.Add(timeline);
            }
        }

        return new ArchivePruneResult(deleted, removed, skipped);
    }
}

/// <summary>
/// One timeline's archive under its <c>archive.lock</c>, for one operation (WA-14, WA-24): one publish by the archiver, or one prune. Opening deletes
/// abandoned temps (only the holder writes, so a temp it did not create is abandoned). The archiver keeps one for its life and holds the lock per segment
/// (<see cref="TryReacquire"/>, <see cref="ReleaseLock"/>): a retention run gets the timeline between two publishes.
/// </summary>
internal sealed class WalArchiveTimeline : IDisposable
{
    private readonly IStoreFileSystem _fs;
    private readonly ILogger _log;
    private readonly WalArchivePaths _paths;
    private readonly string _lockPath;
    private readonly string _indexPath;
    private IDisposable _lock;

    // The listing's fingerprint as the archiver extends it publish by publish (unfinished CRC, last name), so an index rewrite does not enumerate the
    // directory; unknown until a load, or after a publish out of name order.
    private bool _listingKnown;
    private uint _listingCrc;
    private string _listingLast;

    private WalArchiveTimeline(IStoreFileSystem fs, ILogger log, WalArchivePaths paths, Guid timelineId, string directory, string lockPath, IDisposable held)
    {
        _fs = fs;
        _log = log;
        _paths = paths;
        TimelineId = timelineId;
        Directory = directory;
        _lockPath = lockPath;
        _indexPath = paths.Index(timelineId);
        _lock = held;
    }

    public Guid TimelineId { get; }

    public string Directory { get; }

    /// <summary>Whether this handle holds the timeline's lock now.</summary>
    public bool Held => _lock != null;

    /// <summary>
    /// Takes <c>wal/&lt;t&gt;/archive.lock</c> (creating the directories when absent); <c>null</c> when another holder has it (a retry, not a refusal).
    /// Checks that the file system enforces the lock, throwing <see cref="NotSupportedException"/> when it does not (R3: two writers could interleave),
    /// and deletes the temps a dead holder left.
    /// </summary>
    public static WalArchiveTimeline TryOpen(IStoreFileSystem fs, string storeRoot, Guid timelineId, ILogger log = null)
    {
        fs ??= StoreFileSystem.Instance;
        log ??= NullLogger.Instance;
        var paths = new WalArchivePaths(storeRoot);
        var dir = paths.Timeline(timelineId);
        if (!fs.DirectoryExists(dir))
        {
            if (!fs.DirectoryExists(paths.Wal))
            {
                fs.CreateDirectory(paths.Wal);
                fs.FlushDirectory(storeRoot);
            }

            fs.CreateDirectory(dir);
            fs.FlushDirectory(paths.Wal);
        }

        var lockPath = paths.Lock(timelineId);
        var held = fs.TryLockTransient(lockPath);
        if (held == null)
        {
            return null;
        }

        try
        {
            using (var probe = fs.TryLockTransient(lockPath))
            {
                if (probe != null)
                {
                    throw new NotSupportedException(
                        $"File locking is not enforced for '{lockPath}' (DOTNET_SYSTEM_IO_DISABLEFILELOCKING set, or a file system without "
                        + "locks): the WAL archive cannot exclude a second writer, so it refuses to write.");
                }
            }

            var timeline = new WalArchiveTimeline(fs, log, paths, timelineId, dir, lockPath, held);
            held = null;
            timeline.DeleteTemps();
            return timeline;
        }
        finally
        {
            held?.Dispose();
        }
    }

    /// <summary>Takes the lock again after <see cref="ReleaseLock"/>; false when another holder has it (a retry).</summary>
    public bool TryReacquire()
    {
        _lock ??= _fs.TryLockTransient(_lockPath);
        return _lock != null;
    }

    /// <summary>Lets the lock go, keeping the handle for <see cref="TryReacquire"/>.</summary>
    public void ReleaseLock()
    {
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>Deletes every temp of the directory: abandoned, since only the lock holder writes.</summary>
    public int DeleteTemps()
    {
        var deleted = 0;
        foreach (var f in _fs.EnumerateFiles(Directory))
        {
            if (f.Name.EndsWith(ArchiveFormat.TempSuffix, StringComparison.Ordinal))
            {
                _fs.DeleteFile(Path.Combine(Directory, f.Name));
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>
    /// Writes <c>timeline.json</c> when absent (tmp, flush, durable rename); returns the file now in place. One written by another history (a different
    /// database) is returned as is: the caller compares.
    /// </summary>
    public TimelineFile EnsureTimelineFile(TimelineFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var existing = WalArchiveStore.ReadTimelineFile(_fs, _paths.Root, TimelineId);
        if (existing != null)
        {
            return existing;
        }

        var path = _paths.TimelineFile(TimelineId);
        var temp = path + ArchiveFormat.TempSuffix;
        _fs.DeleteFile(temp);
        using (var f = _fs.OpenCreateNew(temp))
        {
            f.Write(JsonSerializer.SerializeToUtf8Bytes(file, ArchiveJsonContext.Default.TimelineFile), 0);
            f.Flush();
        }

        StoreFileSystem.MoveDurable(_fs, temp, path, replace: true);
        return file;
    }

    /// <summary>
    /// Publishes one archived segment (BKP-10): a fresh temp written by <paramref name="writer"/>, flushed, then renamed under its name without replacing
    /// anything, and the directory flushed. A name already taken is compared: the same segment (first LSN, archived length, raw CRC) is
    /// <see cref="ArchivePublishOutcome.AlreadyArchived"/> — its directory is flushed again, so a rename whose flush failed is durable before the floor
    /// rises past it — anything else is <see cref="ArchivePublishOutcome.Diverged"/>. Throws on an I/O failure, its temp removed, with nothing published
    /// that the caller may count on.
    /// </summary>
    public (ArchivePublishOutcome Outcome, ArchivedSegmentInfo Segment) Publish(ArchivedSegmentWriter writer, in ArchivedSegmentHeader header,
        ReadOnlySpan<byte> walHeader, ReadOnlySpan<byte> walData, ArchivedSegmentFooter footer, ReadOnlySpan<ArchiveTimeSample> samples)
    {
        if (_lock == null)
        {
            throw new InvalidOperationException($"Timeline {TimelineId}'s archive lock is not held.");
        }

        if (header.TimelineId != TimelineId)
        {
            throw new InvalidOperationException($"Segment {header.SegmentId} is of timeline {header.TimelineId}, not {TimelineId}.");
        }

        var name = ArchiveFormat.FileName(header.SegmentId);
        var final = Path.Combine(Directory, name);
        var temp = final + ArchiveFormat.TempSuffix;
        ArchivedSegmentWritten written;
        try
        {
            using (var file = _fs.OpenCreateNew(temp))
            {
                written = writer.Complete(file, header, walHeader, walData, footer, samples);
                file.Flush();
            }

            // Never over an existing name (BKP-08's discipline): the rename refuses one, and the compare below decides what it is.
            _fs.Rename(temp, final, replace: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var collided = _fs.FileExists(final) && _fs.FileExists(temp);
            TryDelete(temp);
            if (!collided)
            {
                throw;
            }

            using var existing = ArchivedSegmentReader.Open(_fs, final);
            var same = existing.Header.FirstLsn == header.FirstLsn && existing.Header.TimelineId == header.TimelineId
                                                                    && existing.Header.DatabaseId == header.DatabaseId
                                                                    && existing.Footer.ArchiveLength == walHeader.Length + walData.Length
                                                                    && existing.Footer.RawCrc == RawCrc(walHeader, walData);
            if (!same)
            {
                return (ArchivePublishOutcome.Diverged, WalArchiveStore.Describe(name, existing));
            }

            _fs.FlushDirectory(Directory);
            return (ArchivePublishOutcome.AlreadyArchived, WalArchiveStore.Describe(name, existing));
        }

        _fs.FlushDirectory(Directory);
        if (_listingKnown && string.CompareOrdinal(name, _listingLast) > 0)
        {
            _listingCrc = WalArchiveStore.FingerprintAppend(_listingCrc, name, written.FileLength);
            _listingLast = name;
        }
        else
        {
            _listingKnown = false;
        }

        var segment = new ArchivedSegmentInfo
        {
            File = name,
            FileLength = written.FileLength,
            Header = header,
            Footer = written.Footer,
            FirstSampleLsn = samples.Length > 0 ? samples[0].Lsn : 0,
            FirstSampleUtcTicks = samples.Length > 0 ? samples[0].UtcTicks : 0,
            LastSampleLsn = samples.Length > 0 ? samples[^1].Lsn : 0,
            LastSampleUtcTicks = samples.Length > 0 ? samples[^1].UtcTicks : 0,
        };
        return (ArchivePublishOutcome.Published, segment);
    }

    private static uint RawCrc(ReadOnlySpan<byte> walHeader, ReadOnlySpan<byte> walData) =>
        Crc32CUtil.Finish(Crc32CUtil.Append(Crc32CUtil.Append(Crc32CUtil.Start, walHeader), walData));

    private void TryDelete(string path)
    {
        try
        {
            _fs.DeleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next first open, which deletes every temp.
        }
    }

    /// <summary>The timeline's segments, as the index cache has them when it matches the listing, else rebuilt from the files (and the index rewritten).</summary>
    public ArchivedTimeline LoadAndRefreshIndex()
    {
        var loaded = WalArchiveStore.Load(_fs, _paths.Root, TimelineId);
        _listingCrc = WalArchiveStore.FingerprintState(WalArchiveStore.SegmentListing(_fs, Directory), out _listingLast);
        _listingKnown = true;
        if (!loaded.FromIndex)
        {
            WriteIndex(loaded.Segments);
        }

        return loaded;
    }

    /// <summary>
    /// Rewrites the index cache for <paramref name="segments"/> (a cache: written without a flush, through a temp so it is never torn). Streamed, with the
    /// listing's fingerprint as the publishes extended it: a rewrite costs the archiver no allocation in proportion to the archive.
    /// </summary>
    public void WriteIndex(IReadOnlyList<ArchivedSegmentInfo> segments)
    {
        try
        {
            if (!_listingKnown)
            {
                _listingCrc = WalArchiveStore.FingerprintState(WalArchiveStore.SegmentListing(_fs, Directory), out _listingLast);
                _listingKnown = true;
            }

            var temp = _indexPath + ArchiveFormat.TempSuffix;
            _fs.DeleteFile(temp);
            using (var f = _fs.OpenCreateNew(temp))
            {
                ArchiveIndex.Write(f, TimelineId, Crc32CUtil.Finish(_listingCrc), segments);
            }

            _fs.Rename(temp, _indexPath, replace: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WalArchiveLog.IndexNotWritten(_log, Directory, ex.Message);
        }
    }

    public void Dispose() => ReleaseLock();
}

/// <summary>
/// <c>archive-index.bin</c>, a cache (WA-6): a 64 B header (magic, version, count, timeline, the listing's fingerprint, CRC) then one entry per
/// segment — its <c>.twal</c> header and footer verbatim, its samples' bounds, its file length, an entry CRC. Any doubt, and the reader rebuilds from the
/// files.
/// </summary>
internal static class ArchiveIndex
{
    private const int HeaderSize = 64;
    private const int EntrySize = ArchiveFormat.HeaderSize + ArchiveFormat.FooterSize + 48;
    private const ushort Version = 1;
    private static ReadOnlySpan<byte> Magic => "TYWALIDX"u8;

    /// <summary>
    /// Writes the index of <paramref name="segments"/> to <paramref name="file"/> from offset 0, a few entries at a time: no array in proportion to the
    /// archive (the archiver rewrites the index as the archive grows).
    /// </summary>
    public static void Write(IStoreFile file, Guid timelineId, uint fingerprint, IReadOnlyList<ArchivedSegmentInfo> segments)
    {
        const int perChunk = 8;
        Span<byte> chunk = stackalloc byte[perChunk * EntrySize];
        var header = chunk[..HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], segments.Count);
        timelineId.TryWriteBytes(header[16..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[32..], fingerprint);
        BinaryPrimitives.WriteUInt32LittleEndian(header[60..], Crc32CUtil.Compute(header[..60]));
        file.Write(header, 0);
        long offset = HeaderSize;
        for (var i = 0; i < segments.Count; i += perChunk)
        {
            var n = Math.Min(perChunk, segments.Count - i);
            for (var j = 0; j < n; j++)
            {
                WriteEntry(segments[i + j], chunk.Slice(j * EntrySize, EntrySize));
            }

            file.Write(chunk[..(n * EntrySize)], offset);
            offset += n * EntrySize;
        }
    }

    private static void WriteEntry(ArchivedSegmentInfo s, Span<byte> e)
    {
        e.Clear();
        s.Header.WriteTo(e);
        s.Footer.WriteTo(e[ArchiveFormat.HeaderSize..]);
        var tail = e[(ArchiveFormat.HeaderSize + ArchiveFormat.FooterSize)..];
        BinaryPrimitives.WriteInt64LittleEndian(tail, s.FileLength);
        BinaryPrimitives.WriteInt64LittleEndian(tail[8..], s.FirstSampleLsn);
        BinaryPrimitives.WriteInt64LittleEndian(tail[16..], s.FirstSampleUtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(tail[24..], s.LastSampleLsn);
        BinaryPrimitives.WriteInt64LittleEndian(tail[32..], s.LastSampleUtcTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(tail[44..], Crc32CUtil.Compute(e[..(EntrySize - 4)]));
    }

    /// <summary>The cached segments when the index is intact and was written for <paramref name="fingerprint"/>; false otherwise.</summary>
    public static bool TryRead(IStoreFileSystem fs, string path, Guid timelineId, uint fingerprint, out List<ArchivedSegmentInfo> segments)
    {
        segments = null;
        if (!fs.FileExists(path))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = StoreFileSystem.ReadAllBytes(fs, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var span = bytes.AsSpan();
        if (bytes.Length < HeaderSize || !span[..8].SequenceEqual(Magic) || BinaryPrimitives.ReadUInt16LittleEndian(span[8..]) != Version
            || BinaryPrimitives.ReadUInt32LittleEndian(span[60..]) != Crc32CUtil.Compute(span[..60]) || new Guid(span.Slice(16, 16)) != timelineId
            || BinaryPrimitives.ReadUInt32LittleEndian(span[32..]) != fingerprint)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
        if (count < 0 || bytes.Length != HeaderSize + (long)count * EntrySize)
        {
            return false;
        }

        var result = new List<ArchivedSegmentInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var e = span.Slice(HeaderSize + i * EntrySize, EntrySize);
            var tail = e[(ArchiveFormat.HeaderSize + ArchiveFormat.FooterSize)..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail[44..]) != Crc32CUtil.Compute(e[..(EntrySize - 4)]))
            {
                return false;
            }

            var header = ArchivedSegmentHeader.Read(e);
            result.Add(new ArchivedSegmentInfo
            {
                File = ArchiveFormat.FileName(header.SegmentId),
                FileLength = BinaryPrimitives.ReadInt64LittleEndian(tail),
                Header = header,
                Footer = ArchivedSegmentFooter.Read(e[ArchiveFormat.HeaderSize..]),
                FirstSampleLsn = BinaryPrimitives.ReadInt64LittleEndian(tail[8..]),
                FirstSampleUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(tail[16..]),
                LastSampleLsn = BinaryPrimitives.ReadInt64LittleEndian(tail[24..]),
                LastSampleUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(tail[32..]),
            });
        }

        segments = result;
        return true;
    }
}
