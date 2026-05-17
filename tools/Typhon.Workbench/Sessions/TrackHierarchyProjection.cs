using System;
using System.Collections.Generic;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Projects a trace's Tracks + DAGs tables (<see cref="TrackRecord"/> / <see cref="DagRecord"/>, format v11+, #354)
/// into the wire <see cref="TrackDto"/> hierarchy. Shared by the trace and attach session runtimes so both produce an
/// identical <c>tracks[] → dags[] → phases[]</c> shape.
/// </summary>
internal static class TrackHierarchyProjection
{
    /// <summary>
    /// Builds the <c>tracks[] → dags[]</c> hierarchy. Tracks are emitted in <see cref="TrackRecord.OrderIndex"/> order;
    /// DAGs nest under their owning track (<see cref="DagRecord.TrackIndex"/> indexes <paramref name="trackRecords"/>)
    /// and are ordered by <see cref="DagRecord.Id"/>. Returns an empty array when the trace carries no track data.
    /// </summary>
    public static TrackDto[] Project(IReadOnlyList<TrackRecord> trackRecords, IReadOnlyList<DagRecord> dagRecords)
    {
        if (trackRecords == null || trackRecords.Count == 0)
        {
            return [];
        }

        var dagCount = dagRecords?.Count ?? 0;
        var tracks = new TrackDto[trackRecords.Count];
        for (var ti = 0; ti < trackRecords.Count; ti++)
        {
            var tr = trackRecords[ti];
            var dags = new List<DagDto>();
            for (var di = 0; di < dagCount; di++)
            {
                var dr = dagRecords[di];
                if (dr.TrackIndex == ti)
                {
                    dags.Add(new DagDto(dr.Id, dr.Name, dr.PhaseNames));
                }
            }
            dags.Sort(static (a, b) => a.Id.CompareTo(b.Id));
            tracks[ti] = new TrackDto(tr.Name, tr.OrderIndex, tr.Tags, dags.ToArray());
        }

        Array.Sort(tracks, static (a, b) => a.OrderIndex.CompareTo(b.OrderIndex));
        return tracks;
    }
}
