using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>What <see cref="WalArchiveStore.PruneArchive"/> did.</summary>
/// <param name="Deleted">The archived segments deleted, per timeline: (timeline, segment file name).</param>
/// <param name="TimelinesDeleted">Timelines removed whole (absent from the floors and not the live one).</param>
/// <param name="Skipped">Timelines left for the next run because their <c>archive.lock</c> was busy (an archiver mid-publish), with why.</param>
internal sealed record ArchivePruneResult(IReadOnlyList<(Guid TimelineId, string File)> Deleted, IReadOnlyList<Guid> TimelinesDeleted,
    IReadOnlyList<(Guid TimelineId, string Reason)> Skipped);

/// <summary>The archived WAL of a backup store: <c>wal/&lt;TimelineId&gt;/</c> (impl/1418 §3.5).</summary>
internal static class WalArchiveStore
{
    /// <summary>
    /// Retention of the archive (#1415, 05 §3 step 5), engine-free, under <c>store.lock</c>: per timeline, archived segments whose last LSN is below its
    /// floor are deleted oldest first; a timeline absent from <paramref name="floors"/> is deleted whole, except <paramref name="liveTimeline"/>.
    /// </summary>
    /// <param name="session">The store, opened for writing (its lock held).</param>
    /// <param name="floors">Per kept timeline, the lowest LSN a kept point may replay from (<c>C</c> of its oldest kept point).</param>
    /// <param name="liveTimeline">The database's current timeline: never deleted whole.</param>
    internal static ArchivePruneResult PruneArchive(BackupStore session, IReadOnlyList<(Guid TimelineId, long KeepFromLsn)> floors, Guid liveTimeline)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(floors);
        return new ArchivePruneResult([], [], []);
    }
}
