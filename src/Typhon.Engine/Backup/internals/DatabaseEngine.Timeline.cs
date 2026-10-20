using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine;

/// <summary>One timeline of this database's WAL history as the engine knows it: its parent, and the last LSN it shares with it.</summary>
/// <param name="TimelineId">The timeline.</param>
/// <param name="ParentId">The timeline it continues; <see cref="Guid.Empty"/> for a genesis timeline, or when not known.</param>
/// <param name="OriginLsn">The last LSN it shares with its parent; 0 for a genesis timeline.</param>
/// <param name="Reason">Why it was started.</param>
internal readonly record struct TimelineEntry(Guid TimelineId, Guid ParentId, long OriginLsn, TimelineReason Reason);

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
    // ReSharper restore InconsistentNaming

    private readonly Lock _timelineLock = new();
    private List<TimelineEntry> _timelineHistory;
    private long _lastForkOrigin = -1;

    /// <summary>The timeline this database continues (<see cref="Guid.Empty"/> for a genesis timeline).</summary>
    internal Guid TimelineParentId => Current().ParentId;

    /// <summary>The last LSN the current timeline shares with its parent; 0 for a genesis timeline.</summary>
    internal long TimelineOriginLsn => Current().OriginLsn;

    /// <summary>
    /// For a database a restore started (its opening timeline's reason): the timeline's origin, the last LSN of the parent's history its WAL holds — those
    /// segments are the restored copy of the parent's, never archived again. 0 otherwise.
    /// </summary>
    internal long RestoreOriginLsn
    {
        get
        {
            lock (_timelineLock)
            {
                var opened = History()[0];
                return opened.Reason == TimelineReason.Restore ? opened.OriginLsn : 0;
            }
        }
    }

    /// <summary>Test seam: invoked by <see cref="ForkTimeline"/> right after its meta flip. Null in production.</summary>
    internal Action<Guid> AfterTimelineForkProbe { get; set; }

    private TimelineEntry Current()
    {
        lock (_timelineLock)
        {
            return History()[^1];
        }
    }

    /// <summary>The timelines this session knows, oldest first: the one it opened on (from the meta), then every fork since.</summary>
    private List<TimelineEntry> History()
    {
        if (_timelineHistory == null)
        {
            var bootstrap = MMF.Bootstrap;
            var parent = bootstrap.TryGet(BK_TimelineParent, out var p) ? UnpackDatabaseId(p) : Guid.Empty;
            var origin = bootstrap.TryGet(BK_TimelineOriginLsn, out var o) ? o.AsLong : 0;
            var reason = bootstrap.TryGet(BK_TimelineReason, out var r) ? (TimelineReason)r.AsInt : TimelineReason.Genesis;
            _timelineHistory = [new TimelineEntry(TimelineId, parent, origin, reason)];
        }

        return _timelineHistory;
    }

    /// <summary>
    /// The timeline a WAL segment ending at <paramref name="lastLsn"/> belongs to (WA-22): the newest timeline this session knows whose origin is below it,
    /// else the parent of the oldest. Forks have disjoint LSN ranges, so this is a function.
    /// </summary>
    internal TimelineEntry TimelineFor(long lastLsn)
    {
        lock (_timelineLock)
        {
            var history = History();
            for (var i = history.Count - 1; i >= 0; i--)
            {
                if (lastLsn > history[i].OriginLsn)
                {
                    return history[i];
                }
            }

            var oldest = history[0];
            return new TimelineEntry(oldest.ParentId, Guid.Empty, 0, TimelineReason.Unknown);
        }
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
    internal TimelineFile DescribeTimeline(Guid storeId) => DescribeTimeline(Current(), storeId);

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

        var bootstrap = MMF.Bootstrap;
        if (bootstrap.TryGet(BK_TimelineId, out var current) && UnpackDatabaseId(current) == timelineId)
        {
            TimelineId = timelineId;
            return;
        }

        lock (_timelineLock)
        {
            History();   // read from the meta BEFORE the flip: the timeline being left stays known, with its own parent and origin
        }

        MMF.MutateBootstrapAndPersist(() =>
        {
            bootstrap.Set(BK_TimelineId, PackDatabaseId(timelineId));
            bootstrap.Set(BK_TimelineParent, PackDatabaseId(parentId));
            bootstrap.Set(BK_TimelineOriginLsn, BootstrapDictionary.Value.FromLong(originLsn));
            bootstrap.Set(BK_TimelineReason, BootstrapDictionary.Value.FromInt((int)reason));
        });
        lock (_timelineLock)
        {
            TimelineId = timelineId;
            var entry = new TimelineEntry(timelineId, parentId, originLsn, reason);
            if (reason == TimelineReason.Restore)
            {
                _timelineHistory = [entry];   // a restored database's history starts at its restore: what came before is its parent's
            }
            else if (History()[^1].TimelineId != timelineId)
            {
                _timelineHistory.Add(entry);
            }
        }

        if (!keepLineage && BackupCoordinator != null)
        {
            BackupCoordinator.Lineage.Trusted = false;   // a new timeline starts a new lineage: its first point is full (BKP-06)
        }
    }

    /// <summary>
    /// Forks the timeline before a write the WAL cannot replay (Q4, WA-13): with archiving configured, a meta flip naming the current timeline as parent
    /// and, as origin, the LSN before the active segment's first — after a served segment switch when <paramref name="switchSegment"/>, so the parent's
    /// segments end exactly there. Idempotent while nothing was appended since the last fork (a registration changing several components forks once).
    /// Archiving off: nothing to protect, no flip.
    /// </summary>
    internal void ForkTimeline(TimelineReason reason, bool switchSegment)
    {
        if (ArchiveConfiguration == null || WalManager?.SegmentManager == null)
        {
            return;
        }

        lock (_timelineLock)
        {
            if (switchSegment)
            {
                var ticket = WalManager.RequestSegmentSwitch();
                var clock = Stopwatch.StartNew();
                while (WalManager.SwitchesServed < ticket)
                {
                    if (clock.Elapsed > TimeSpan.FromSeconds(30))
                    {
                        throw new InvalidOperationException($"The WAL writer did not serve the segment switch of a timeline fork ({reason}) within 30 s.");
                    }

                    Thread.Yield();
                }
            }

            var origin = WalManager.SegmentManager.ArchiveSnapshot(0, 0).ActiveFirstLsn - 1;
            if (origin == _lastForkOrigin)
            {
                return;
            }

            var parent = TimelineId;
            var child = Guid.NewGuid();
            AdoptTimeline(child, parent, origin, reason, keepLineage: true);
            _lastForkOrigin = origin;
            LogTimelineForked(parent, child, origin, reason);
            AfterTimelineForkProbe?.Invoke(child);
        }
    }

    [LoggerMessage(LogLevel.Information, "WAL timeline forked ({Reason}): {Parent} → {Child} at LSN {Origin}; no restore crosses it")]
    private partial void LogTimelineForked(Guid parent, Guid child, long origin, TimelineReason reason);
}
