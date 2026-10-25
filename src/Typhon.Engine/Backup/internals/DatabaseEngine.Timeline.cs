using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Text;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>One timeline of this database's WAL history as the engine knows it: its parent, and the last LSN it shares with it.</summary>
/// <param name="TimelineId">The timeline.</param>
/// <param name="ParentId">The timeline it continues; <see cref="Guid.Empty"/> for a genesis timeline, or when not known.</param>
/// <param name="OriginLsn">The last LSN it shares with its parent; 0 for a genesis timeline.</param>
/// <param name="Reason">Why it was started.</param>
internal readonly record struct TimelineEntry(Guid TimelineId, Guid ParentId, long OriginLsn, TimelineReason Reason);

/// <summary>
/// The timelines this database knows, oldest first, as ONE immutable value: the engine publishes a new one whole (a release store) and every reader takes it
/// with one acquire load, so no reader sees a timeline id from one fork and a parent or an origin from another — nor a torn 16-byte id (x64 and arm64).
/// </summary>
internal sealed class TimelineHistory
{
    private readonly TimelineEntry[] _entries;

    private TimelineHistory(TimelineEntry[] entries) => _entries = entries;

    /// <summary>A history holding only <paramref name="entry"/>.</summary>
    public static TimelineHistory Of(in TimelineEntry entry) => new([entry]);

    /// <summary>A history of <paramref name="ancestors"/> (oldest first) then <paramref name="current"/>.</summary>
    public static TimelineHistory Of(ReadOnlySpan<TimelineEntry> ancestors, in TimelineEntry current)
    {
        var entries = new TimelineEntry[ancestors.Length + 1];
        ancestors.CopyTo(entries);
        entries[^1] = current;
        return new TimelineHistory(entries);
    }

    /// <summary>The timeline the database is on.</summary>
    public TimelineEntry Current => _entries[^1];

    /// <summary>The timelines before <see cref="Current"/>, oldest first.</summary>
    public ReadOnlySpan<TimelineEntry> Ancestors => _entries.AsSpan(0, _entries.Length - 1);

    /// <summary>This history then <paramref name="next"/>.</summary>
    public TimelineHistory With(in TimelineEntry next)
    {
        var entries = new TimelineEntry[_entries.Length + 1];
        _entries.CopyTo(entries, 0);
        entries[^1] = next;
        return new TimelineHistory(entries);
    }

    /// <summary>
    /// The timeline a WAL segment ending at <paramref name="lastLsn"/> belongs to (WA-22): the newest whose origin is below it, else the parent of the
    /// oldest (its own parent and origin unknown). Forks have disjoint LSN ranges, so this is a function.
    /// </summary>
    public TimelineEntry For(long lastLsn)
    {
        for (var i = _entries.Length - 1; i >= 0; i--)
        {
            if (lastLsn > _entries[i].OriginLsn)
            {
                return _entries[i];
            }
        }

        return new TimelineEntry(_entries[0].ParentId, Guid.Empty, 0, TimelineReason.Unknown);
    }

    /// <summary>
    /// For a database a restore started: the restore's origin, the last LSN of the parent's history its WAL holds — those segments are the restored copy
    /// of the parent's, never archived again. 0 when no known timeline was started by a restore.
    /// </summary>
    public long RestoreOriginLsn
    {
        get
        {
            foreach (var e in _entries)
            {
                if (e.Reason == TimelineReason.Restore)
                {
                    return e.OriginLsn;
                }
            }

            return 0;
        }
    }
}

/// <summary>
/// WAL timelines (impl/1418 §3.6, Q4): a restore starts one (its WAL reuses LSNs the original wrote), and so does a write the WAL cannot replay — a schema
/// change, a completed bulk load — or a restore to an LSN after it, replayed onto a base from before it, would be a state no run produced. The timeline,
/// its parent and its origin flip together in one meta write; a restore never crosses a timeline.
/// </summary>
public partial class DatabaseEngine
{
    // ReSharper disable InconsistentNaming
    internal const string BK_TimelineParent    = "TimelineParent";
    internal const string BK_TimelineOriginLsn = "TimelineOriginLsn";
    internal const string BK_TimelineReason    = "TimelineReason";
    internal const string BK_TimelineAncestry  = "TimelineAncestry";
    // ReSharper restore InconsistentNaming

    /// <summary>
    /// At most this many ancestors are kept in the meta (<see cref="BK_TimelineAncestry"/>, ~80 B each in the bootstrap page). Past it the oldest are
    /// dropped: a held segment of one of them is then filed under the oldest kept one's parent, a coverage gap for the archive, never a wrong restore.
    /// </summary>
    private const int MaxPersistedAncestors = 8;

    /// <summary>Serialises the writers of <see cref="_timelines"/> (adoption, fork, a point's stamp); readers take <see cref="Timelines"/>, lock-free.</summary>
    private readonly Lock _timelineLock = new();

    private TimelineHistory _timelines = TimelineHistory.Of(default(TimelineEntry));
    private long _lastForkOrigin = -1;
    private long _pointStamps;
    private long _pointStampsAtLastFork = -1;

    /// <summary>The timelines this database knows, published whole (D4): read once, it is one consistent value.</summary>
    internal TimelineHistory Timelines => Volatile.Read(ref _timelines);

    /// <summary>The timeline this database continues (<see cref="Guid.Empty"/> for a genesis timeline).</summary>
    internal Guid TimelineParentId => Timelines.Current.ParentId;

    /// <summary>The last LSN the current timeline shares with its parent; 0 for a genesis timeline.</summary>
    internal long TimelineOriginLsn => Timelines.Current.OriginLsn;

    /// <inheritdoc cref="TimelineHistory.RestoreOriginLsn"/>
    internal long RestoreOriginLsn => Timelines.RestoreOriginLsn;

    /// <summary>Test seam: invoked by <see cref="ForkTimeline"/> right after its meta flip. Null in production.</summary>
    internal Action<Guid> AfterTimelineForkProbe { get; set; }

    /// <inheritdoc cref="TimelineHistory.For"/>
    internal TimelineEntry TimelineFor(long lastLsn) => Timelines.For(lastLsn);

    /// <summary>
    /// The current timeline, for a point about to be stamped with it: counted, so the next fork is never folded into the last one while a point may lie
    /// between them (<see cref="ForkTimeline"/>).
    /// </summary>
    internal TimelineEntry StampTimelineForPoint()
    {
        lock (_timelineLock)
        {
            _pointStamps++;
            return _timelines.Current;
        }
    }

    /// <summary>
    /// Publishes the timeline the database opens on, <paramref name="timelineId"/>, with what the meta says of it — its parent, origin and reason, and the
    /// ancestors it kept (<see cref="BK_TimelineAncestry"/>). The open's identity step calls it, single-threaded.
    /// </summary>
    private void PublishOpenedTimeline(Guid timelineId)
    {
        var bootstrap = MMF.Bootstrap;
        var parent = bootstrap.TryGet(BK_TimelineParent, out var p) ? UnpackDatabaseId(p) : Guid.Empty;
        var origin = bootstrap.TryGet(BK_TimelineOriginLsn, out var o) ? o.AsLong : 0;
        var reason = bootstrap.TryGet(BK_TimelineReason, out var r) ? (TimelineReason)r.AsInt : TimelineReason.Genesis;
        var ancestors = bootstrap.TryGet(BK_TimelineAncestry, out var a) ? ParseAncestry(a.AsString) : [];
        Volatile.Write(ref _timelines, TimelineHistory.Of(ancestors, new TimelineEntry(timelineId, parent, origin, reason)));
    }

    /// <summary>What <c>wal/&lt;t&gt;/timeline.json</c> says about timeline <paramref name="timeline"/> (impl/1418 §3.6).</summary>
    internal TimelineFile DescribeTimeline(in TimelineEntry timeline, Guid storeId) => new()
    {
        FormatVersion = TimelineFile.CurrentFormatVersion,
        TimelineId = timeline.TimelineId,
        DatabaseId = DatabaseId,
        StoreId = storeId,
        ParentTimelineId = timeline.ParentId == Guid.Empty ? null : timeline.ParentId,
        OriginLsn = timeline.OriginLsn,
        Reason = timeline.Reason,
        CreatedUtc = DateTime.UtcNow,
    };

    /// <summary>The current timeline's <c>timeline.json</c>.</summary>
    internal TimelineFile DescribeTimeline(Guid storeId) => DescribeTimeline(Timelines.Current, storeId);

    /// <summary>
    /// Starts a new WAL timeline: persists <paramref name="timelineId"/>, its parent and its origin in ONE meta flip (durable on return) and adopts it.
    /// Idempotent: adopting the current timeline writes nothing. A restore's first open calls it before accepting transactions, so a crash in between
    /// replays the adoption rather than losing it; a fork calls it before the write the WAL cannot replay.
    /// </summary>
    /// <param name="timelineId">The new timeline; never <see cref="Guid.Empty"/>.</param>
    /// <param name="parentId">The timeline it continues; <see cref="Guid.Empty"/> when unknown.</param>
    /// <param name="originLsn">The last LSN it shares with its parent.</param>
    /// <param name="reason">Why it starts.</param>
    /// <param name="keepLineage">
    /// A fork keeps the backup lineage (the change map tracked every write: the next point may be incremental, BKP-06 accepting a head on the parent); a
    /// restore does not (its first point is full).
    /// </param>
    internal void AdoptTimeline(Guid timelineId, Guid parentId, long originLsn, TimelineReason reason, bool keepLineage)
    {
        if (timelineId == Guid.Empty)
        {
            throw new ArgumentException("A timeline id cannot be empty.", nameof(timelineId));
        }

        lock (_timelineLock)
        {
            var bootstrap = MMF.Bootstrap;
            if (bootstrap.TryGet(BK_TimelineId, out var current) && UnpackDatabaseId(current) == timelineId)
            {
                return;   // the open published it from the meta already
            }

            // A restored database's history starts at its restore: what came before is its parent's. A fork keeps the timelines a local segment may still
            // belong to, so a restart files every held segment under its own timeline (WA-22), however many forks ago it was written.
            var entry = new TimelineEntry(timelineId, parentId, originLsn, reason);
            var next = reason == TimelineReason.Restore ? TimelineHistory.Of(entry) : KeptAncestry(_timelines).With(entry);
            var ancestry = FormatAncestry(next.Ancestors);
            MMF.MutateBootstrapAndPersist(() =>
            {
                bootstrap.Set(BK_TimelineId, PackDatabaseId(timelineId));
                bootstrap.Set(BK_TimelineParent, PackDatabaseId(parentId));
                bootstrap.Set(BK_TimelineOriginLsn, BootstrapDictionary.Value.FromLong(originLsn));
                bootstrap.Set(BK_TimelineReason, BootstrapDictionary.Value.FromInt((int)reason));
                bootstrap.Set(BK_TimelineAncestry, BootstrapDictionary.Value.FromString(ancestry));
            });
            Volatile.Write(ref _timelines, next);
        }

        if (!keepLineage && BackupCoordinator != null)
        {
            BackupCoordinator.Lineage.Trusted = false;   // a new timeline starts a new lineage: its first point is full (BKP-06)
        }
    }

    /// <summary>
    /// Forks the timeline before a write the WAL cannot replay (Q4, WA-13): with archiving configured, a meta flip naming the current timeline as parent
    /// and, as origin, the LSN before the active segment's first — after a served segment switch when <paramref name="switchSegment"/>, so the parent's
    /// segments end exactly there. Idempotent while nothing could tell the two forks apart: the same origin and no point stamped since the last fork (a
    /// registration changing several components forks once). A point between them, whose capture's switch skips an empty segment, keeps the origin the
    /// same, yet a restore past the second change must not replay its records onto that point (D3). Archiving off: nothing to protect, no flip.
    /// </summary>
    internal void ForkTimeline(TimelineReason reason, bool switchSegment)
    {
        if (ArchiveConfiguration == null || WalManager?.SegmentManager == null)
        {
            return;
        }

        if (switchSegment)
        {
            // Outside the lock: the writer serves the switch on its next pass, and a stalled writer must not hold up a point's stamp behind the fork.
            var ticket = WalManager.RequestSegmentSwitch();
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(30));
            var waiter = new AdaptiveWaiter();
            while (WalManager.SwitchesServed < ticket)
            {
                if (!waiter.Wait(ref ctx))
                {
                    throw new InvalidOperationException($"The WAL writer did not serve the segment switch of a timeline fork ({reason}) within 30 s.");
                }
            }
        }

        Guid child;
        lock (_timelineLock)
        {
            var origin = WalManager.SegmentManager.ArchiveSnapshot(0, 0).ActiveFirstLsn - 1;
            if (origin == _lastForkOrigin && _pointStamps == _pointStampsAtLastFork)
            {
                return;
            }

            var parent = _timelines.Current.TimelineId;
            child = Guid.NewGuid();
            AdoptTimeline(child, parent, origin, reason, keepLineage: true);
            _lastForkOrigin = origin;
            _pointStampsAtLastFork = _pointStamps;
            LogTimelineForked(parent, child, origin, reason);
        }

        // Outside the lock: the probe stands for whatever runs between the flip and the change's first write (a point's capture, in a test).
        AfterTimelineForkProbe?.Invoke(child);
    }

    /// <summary>
    /// <paramref name="history"/> without the ancestors no local segment can belong to any more: a timeline owns the LSNs past its origin up to the next
    /// one's, so one whose successor's origin lies below every local segment's last LSN holds nothing on disk. At most <see cref="MaxPersistedAncestors"/>
    /// - 1 are kept with the current timeline, the newest.
    /// </summary>
    private TimelineHistory KeptAncestry(TimelineHistory history)
    {
        var all = history.Ancestors;
        Span<TimelineEntry> chain = stackalloc TimelineEntry[all.Length + 1];
        all.CopyTo(chain);
        chain[^1] = history.Current;

        var oldestLocal = OldestLocalSegmentLastLsn();
        var first = 0;
        while (first < chain.Length - 1 && chain[first + 1].OriginLsn < oldestLocal)
        {
            first++;
        }

        first = Math.Max(first, chain.Length - MaxPersistedAncestors);
        return TimelineHistory.Of(chain[first..^1], chain[^1]);
    }

    /// <summary>The last LSN of the oldest WAL segment on disk (the active one's first LSN − 1 when none is sealed); 0 without a WAL.</summary>
    private long OldestLocalSegmentLastLsn()
    {
        var segments = WalManager?.SegmentManager;
        if (segments == null)
        {
            return 0;
        }

        var snap = segments.ArchiveSnapshot(-1, 0);
        return snap.PendingCount > 0 ? snap.Next.LastLsn : snap.ActiveFirstLsn - 1;
    }

    /// <summary><c>id,parent,origin,reason</c> per ancestor, oldest first, separated by <c>;</c>.</summary>
    private static string FormatAncestry(ReadOnlySpan<TimelineEntry> ancestors)
    {
        var sb = new StringBuilder(ancestors.Length * 80);
        foreach (var e in ancestors)
        {
            sb.Append(sb.Length > 0 ? ";" : "").Append(e.TimelineId.ToString("N")).Append(',').Append(e.ParentId.ToString("N")).Append(',')
                .Append(e.OriginLsn.ToString(CultureInfo.InvariantCulture)).Append(',').Append(((int)e.Reason).ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static TimelineEntry[] ParseAncestry(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var parts = text.Split(';');
        var result = new TimelineEntry[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var f = parts[i].Split(',');
            result[i] = new TimelineEntry(Guid.ParseExact(f[0], "N"), Guid.ParseExact(f[1], "N"), long.Parse(f[2], CultureInfo.InvariantCulture),
                (TimelineReason)int.Parse(f[3], CultureInfo.InvariantCulture));
        }

        return result;
    }

    [LoggerMessage(LogLevel.Information, "WAL timeline forked ({Reason}): {Parent} → {Child} at LSN {Origin}; no restore crosses it")]
    private partial void LogTimelineForked(Guid parent, Guid child, long origin, TimelineReason reason);
}
