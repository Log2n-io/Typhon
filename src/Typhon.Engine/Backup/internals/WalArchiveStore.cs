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
    public long ArchivedThroughLsn => Segments.Count == 0 ? 0 : Segments[^1].LastLsn;
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
    /// footer. Reads only: no lock, nothing written.
    /// </summary>
    public static ArchivedTimeline Load(IStoreFileSystem fs, string storeRoot, Guid timelineId, bool trustIndex = true)
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

                segments.Add(Describe(f.Name, reader));
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

    /// <summary>What a reader says about its segment: header, footer and its samples' bounds (the samples are read and checked).</summary>
    public static ArchivedSegmentInfo Describe(string fileName, ArchivedSegmentReader reader)
    {
        var samples = reader.ReadSamples();
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

    /// <summary>CRC32C of the sorted (name, length) listing: what the index must have been written for (DC-10's pattern).</summary>
    public static uint Fingerprint(IReadOnlyList<StoreFileEntry> listing)
    {
        var crc = Crc32CUtil.Start;
        Span<byte> length = stackalloc byte[8];
        foreach (var f in listing)
        {
            crc = Crc32CUtil.Append(crc, Encoding.UTF8.GetBytes(f.Name));
            BinaryPrimitives.WriteInt64LittleEndian(length, f.Length);
            crc = Crc32CUtil.Append(crc, length);
        }

        return Crc32CUtil.Finish(crc);
    }

    /// <summary>
    /// Retention of the archive (#1415, 05 §3 step 5), engine-free, under <c>store.lock</c>: per timeline, archived segments whose last LSN is below its
    /// floor are deleted oldest first, each followed by a directory flush (a crash leaves the coverage starting later, never a hole); a timeline absent from
    /// <paramref name="floors"/> is deleted whole, except <paramref name="liveTimeline"/>, kept whole. A timeline whose <c>archive.lock</c> is busy is
    /// skipped and reported.
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
                var loaded = Load(fs, storeRoot, timeline, trustIndex: false);
                foreach (var s in loaded.Segments)
                {
                    if (kept && s.LastLsn >= keepFrom)
                    {
                        break;   // ascending: nothing later is below the floor
                    }

                    fs.DeleteFile(Path.Combine(dir, s.File));
                    fs.FlushDirectory(dir);
                    deleted.Add((timeline, s.File));
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
/// abandoned temps (only the holder writes, so a temp it did not create is abandoned).
/// </summary>
internal sealed class WalArchiveTimeline : IDisposable
{
    private readonly IStoreFileSystem _fs;
    private readonly ILogger _log;
    private readonly WalArchivePaths _paths;
    private IDisposable _lock;

    private WalArchiveTimeline(IStoreFileSystem fs, ILogger log, WalArchivePaths paths, Guid timelineId, IDisposable held)
    {
        _fs = fs;
        _log = log;
        _paths = paths;
        TimelineId = timelineId;
        _lock = held;
    }

    public Guid TimelineId { get; }

    public string Directory => _paths.Timeline(TimelineId);

    /// <summary>
    /// Takes <c>wal/&lt;t&gt;/archive.lock</c> (creating the directories when absent); <c>null</c> when another holder has it (a retry, not a refusal).
    /// Throws <see cref="NotSupportedException"/> when the file system does not enforce the lock (R3): two writers could interleave.
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

        var held = fs.TryLockTransient(paths.Lock(timelineId));
        if (held == null)
        {
            return null;
        }

        try
        {
            using (var probe = fs.TryLockTransient(paths.Lock(timelineId)))
            {
                if (probe != null)
                {
                    throw new NotSupportedException(
                        $"File locking is not enforced for '{paths.Lock(timelineId)}' (DOTNET_SYSTEM_IO_DISABLEFILELOCKING set, or a file system without "
                        + "locks): the WAL archive cannot exclude a second writer, so it refuses to write.");
                }
            }

            var timeline = new WalArchiveTimeline(fs, log, paths, timelineId, held);
            held = null;
            timeline.DeleteTemps();
            return timeline;
        }
        finally
        {
            held?.Dispose();
        }
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
    /// Publishes one archived segment (BKP-10): a fresh temp written by <paramref name="writer"/>, flushed, then moved under its name without replacing
    /// anything. A name already taken is compared: the same segment (first LSN, archived length, raw CRC) is <see cref="ArchivePublishOutcome.AlreadyArchived"/>
    /// — its directory is flushed again, so a rename whose flush failed is durable before the floor rises past it — anything else is
    /// <see cref="ArchivePublishOutcome.Diverged"/>. Throws on an I/O failure, with nothing published that the caller may count on.
    /// </summary>
    public (ArchivePublishOutcome Outcome, ArchivedSegmentInfo Segment) Publish(ArchivedSegmentWriter writer, in ArchivedSegmentHeader header,
        ReadOnlySpan<byte> walHeader, ReadOnlySpan<byte> walData, ArchivedSegmentFooter footer, ReadOnlySpan<ArchiveTimeSample> samples)
    {
        ObjectDisposedException.ThrowIf(_lock == null, this);
        if (header.TimelineId != TimelineId)
        {
            throw new InvalidOperationException($"Segment {header.SegmentId} is of timeline {header.TimelineId}, not {TimelineId}.");
        }

        var final = _paths.Segment(TimelineId, header.SegmentId);
        var temp = final + ArchiveFormat.TempSuffix;
        _fs.DeleteFile(temp);
        ArchivedSegmentWritten written;
        using (var file = _fs.OpenCreateNew(temp))
        {
            written = writer.Complete(file, header, walHeader, walData, footer, samples);
            file.Flush();
        }

        if (_fs.FileExists(final))
        {
            _fs.DeleteFile(temp);
            using var existing = ArchivedSegmentReader.Open(_fs, final);
            var same = existing.Header.FirstLsn == header.FirstLsn && existing.Header.TimelineId == header.TimelineId
                                                                    && existing.Header.DatabaseId == header.DatabaseId
                                                                    && existing.Footer.ArchiveLength == written.Footer.ArchiveLength
                                                                    && existing.Footer.RawCrc == written.RawCrc;
            if (!same)
            {
                return (ArchivePublishOutcome.Diverged, WalArchiveStore.Describe(Path.GetFileName(final), existing));
            }

            _fs.FlushDirectory(Directory);
            return (ArchivePublishOutcome.AlreadyArchived, WalArchiveStore.Describe(Path.GetFileName(final), existing));
        }

        StoreFileSystem.MoveDurable(_fs, temp, final, replace: false);
        var segment = new ArchivedSegmentInfo
        {
            File = Path.GetFileName(final),
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

    /// <summary>The timeline's segments, as the index cache has them when it matches the listing, else rebuilt from the files (and the index rewritten).</summary>
    public ArchivedTimeline LoadAndRefreshIndex()
    {
        var loaded = WalArchiveStore.Load(_fs, _paths.Root, TimelineId);
        if (!loaded.FromIndex)
        {
            WriteIndex(loaded.Segments);
        }

        return loaded;
    }

    /// <summary>Rewrites the index cache for <paramref name="segments"/> (a cache: written without a flush, through a temp so it is never torn).</summary>
    public void WriteIndex(IReadOnlyList<ArchivedSegmentInfo> segments)
    {
        try
        {
            var fingerprint = WalArchiveStore.Fingerprint(WalArchiveStore.SegmentListing(_fs, Directory));
            var path = _paths.Index(TimelineId);
            var temp = path + ArchiveFormat.TempSuffix;
            _fs.DeleteFile(temp);
            using (var f = _fs.OpenCreateNew(temp))
            {
                f.Write(ArchiveIndex.Serialize(TimelineId, fingerprint, segments), 0);
            }

            _fs.Rename(temp, path, replace: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WalArchiveLog.IndexNotWritten(_log, Directory, ex.Message);
        }
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }
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

    public static byte[] Serialize(Guid timelineId, uint fingerprint, IReadOnlyList<ArchivedSegmentInfo> segments)
    {
        var bytes = new byte[HeaderSize + segments.Count * EntrySize];
        var span = bytes.AsSpan();
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], segments.Count);
        timelineId.TryWriteBytes(span[16..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], fingerprint);
        BinaryPrimitives.WriteUInt32LittleEndian(span[60..], Crc32CUtil.Compute(span[..60]));
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            var e = span.Slice(HeaderSize + i * EntrySize, EntrySize);
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

        return bytes;
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
