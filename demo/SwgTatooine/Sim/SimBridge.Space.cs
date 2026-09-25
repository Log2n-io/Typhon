using System;
using System.Numerics;
using System.Threading;

namespace SwgTatooine;

/// <summary>
/// The space realm (Realms G1c): AI starships flying between waypoints in a deep 3D grid, with f64 bounds, and scanning a sphere around themselves.
/// </summary>
/// <remarks>
/// The demo's only deep realm and only 3D archetype — what proves a realm can hold another geometry than the planets': its own cell size, its own depth,
/// its own field type, and queries that never see a planet's entities. Players do not fly yet (launch and land need a pilot model; F1).
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>Distance at which a starship has reached its waypoint and picks the next, metres.</summary>
    private const double WaypointReachedM = 50d;

    /// <summary>Radius a starship scans for others, metres.</summary>
    private const double ShipScanRadiusM = 1_000d;

    /// <summary>Ticks between two scans of one starship; staggered by slot so each tick scans a tenth of the fleet.</summary>
    private const int ShipScanPeriodTicks = 10;

    private long _shipScans;
    private long _shipContacts;
    private long _shipWaypoints;

    /// <summary>Integrate every starship one tick towards its waypoint, picking a new one on arrival, and write the moved ones in one batch per cluster.</summary>
    public void ShipMoveTick(TickContext ctx)
    {
        var tick = ctx.TickNumber;
        var perTick = 1d / _config.TickRateHz;
        Span<ShipPlacement> next = stackalloc ShipPlacement[64];
        long waypoints = 0;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Starship>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Starship>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }

            var places = cluster.GetReadOnlySpan(Starship.Bounds);
            var motions = cluster.GetSpan(Starship.Move);
            var chunk = cluster.ChunkId;
            var moved = bits;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                ref var move = ref motions[idx];
                var p = places[idx];
                var (x, y, z) = (p.X, p.Y, p.Z);
                var dx = move.DestX - x;
                var dy = move.DestY - y;
                var dz = move.DestZ - z;
                var len = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
                if (len < WaypointReachedM)
                {
                    move.DestX = (Hash01(Salt(tick, chunk, idx, 0x4CF5AD43u)) - 0.5f) * (WorldBuilder.SpaceEdgeM - (4 * WorldBuilder.ShipHalfExtentM));
                    move.DestY = (Hash01(Salt(tick, chunk, idx, 0x2F8B6E1Du)) - 0.5f) * (WorldBuilder.SpaceEdgeM - (4 * WorldBuilder.ShipHalfExtentM));
                    move.DestZ = (Hash01(Salt(tick, chunk, idx, 0x9D2C5680u)) - 0.5f) * (WorldBuilder.SpaceEdgeM - (4 * WorldBuilder.ShipHalfExtentM));
                    dx = move.DestX - x;
                    dy = move.DestY - y;
                    dz = move.DestZ - z;
                    len = Math.Max(1e-6, Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)));
                    waypoints++;
                }

                var step = Math.Min(len, move.SpeedMps * perTick);
                move.VelX = dx / len * step;
                move.VelY = dy / len * step;
                move.VelZ = dz / len * step;
                next[idx].SetAt(x + move.VelX, y + move.VelY, z + move.VelZ, p.HalfExtent);
            }

            cluster.WriteSpatial(Starship.Bounds, moved, next);
        }

        if (waypoints != 0)
        {
            Interlocked.Add(ref _shipWaypoints, waypoints);
        }
    }

    /// <summary>A tenth of the fleet each tick scans a sphere around itself, in its own realm — a deep-grid 3D query.</summary>
    public void ShipScanTick(TickContext ctx)
    {
        var tick = ctx.TickNumber;
        long scans = 0;
        long contacts = 0;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Starship>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Starship>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }

            var places = cluster.GetReadOnlySpan(Starship.Bounds);
            var chunk = cluster.ChunkId;
            var realm = cluster.Realm;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if ((tick + (chunk * 64) + idx) % ShipScanPeriodTicks != 0)
                {
                    continue;
                }

                var p = places[idx];
                var sphere = new BSphere3D { CenterX = p.X, CenterY = p.Y, CenterZ = p.Z, Radius = ShipScanRadiusM };
                var e = Dbe.ClusterSpatialQuery<Starship>(realm).Radius(in sphere);
                try
                {
                    contacts += e.Count() - 1;   // itself
                }
                finally
                {
                    e.Dispose();
                }

                scans++;
            }
        }

        Interlocked.Add(ref _shipScans, scans);
        Interlocked.Add(ref _shipContacts, contacts);
    }

    /// <summary>What the starships did over the run.</summary>
    public void PrintSpaceReport()
    {
        if (ShipView == null)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  space: {_shipScans:N0} scans, {(double)_shipContacts / Math.Max(1, _shipScans):F2} contacts per scan, "
            + $"{_shipWaypoints:N0} waypoints reached");
    }
}
