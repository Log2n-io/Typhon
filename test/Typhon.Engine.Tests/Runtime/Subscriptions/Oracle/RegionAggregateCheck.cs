using NUnit.Framework;
using System;
using Typhon.Client;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// A ClientRegion session's aggregate (09 § 8) against an expectation derived here, not from the engine's own region test: a tile is in the region when
/// some cell of it meets the hull and was not delivered to the near tier. Its count must be the server's; every other tile must read zero on the client —
/// never sent, or zeroed when it left the region.
/// </summary>
internal static class RegionAggregateCheck
{
    /// <summary>Asserts one session's aggregate; returns the counts it checked in the region.</summary>
    /// <param name="push">The push path.</param>
    /// <param name="session">The session, gathered this tick.</param>
    /// <param name="hull">The region the session last sent.</param>
    /// <param name="grid">The server's aggregate grid.</param>
    /// <param name="client">The session's client-side grid.</param>
    /// <param name="plan">The archetype whose counts are compared.</param>
    /// <param name="deep">Whether the replication grid is deep.</param>
    /// <param name="label">What the assertion names.</param>
    public static long Assert(PushReplication push, SessionId session, in ClientRegionCommand hull, AggregateCounts grid, AggregateGrid client, int plan,
        bool deep, string label)
    {
        var cell = push.CellSize;
        var k = Math.Max(1, (int)Math.Round(grid.TileM / cell));

        // The hull's bounding box: the half-spaces alone cannot prove a box outside near a corner (they classify it straddling), the box can.
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
        for (var v = 0; v < hull.VertexCount; v++)
        {
            var vertex = hull.Vertices[v];
            (minX, maxX) = (Math.Min(minX, vertex.X), Math.Max(maxX, vertex.X));
            (minY, maxY) = (Math.Min(minY, vertex.Y), Math.Max(maxY, vertex.Y));
            (minZ, maxZ) = (Math.Min(minZ, vertex.Z), Math.Max(maxZ, vertex.Z));
        }

        var counted = 0L;
        for (var r = 0; r < grid.Rows; r++)
        {
            var tile = grid.Tiles[r];
            var tx = (int)(tile % (uint)grid.DimX);
            var rest = tile / (uint)grid.DimX;
            var ty = (int)(rest % (uint)grid.DimY);
            var tz = (int)(rest / (uint)grid.DimY);
            var expected = false;
            for (var dz = 0; dz < (deep ? k : 1) && !expected; dz++)
            {
                for (var dy = 0; dy < k && !expected; dy++)
                {
                    for (var dx = 0; dx < k && !expected; dx++)
                    {
                        var (cx, cy, cz) = ((tx * k) + dx, (ty * k) + dy, deep ? (tz * k) + dz : 0);
                        var cells = push.GridCellsForTest;
                        if (cx >= cells.X || cy >= cells.Y || cz >= cells.Z)
                        {
                            continue;
                        }

                        var x0 = grid.OriginX + (cx * cell);
                        var y0 = grid.OriginY + (cy * cell);
                        var z0 = deep ? grid.OriginZ + (cz * cell) : 0;

                        var inBox = x0 <= maxX && x0 + cell >= minX && y0 <= maxY && y0 + cell >= minY && (!deep || (z0 <= maxZ && z0 + cell >= minZ));
                        var meets = inBox && hull.Classify(x0, y0, z0, x0 + cell, y0 + cell, deep ? z0 + cell : 0) != RegionOverlap.Outside;
                        expected = meets && !push.RegionDelivers(session, x0 + (cell / 2), y0 + (cell / 2), deep ? z0 + (cell / 2) : 0);
                    }
                }
            }

            NUnit.Framework.Assert.That(push.RegionAggregates(session, grid, tile), Is.EqualTo(expected), $"{label}: tile {tile} in the aggregate region");
            var got = client.Counts.Length == 0 ? 0 : client.Counts[((int)tile * client.ArchetypeCount) + grid.Columns[plan]];
            if (expected)
            {
                var count = grid.CountAt(tile, plan);
                NUnit.Framework.Assert.That(got, Is.EqualTo(count), $"{label}: tile {tile}'s count");
                counted += count;
            }
            else
            {
                NUnit.Framework.Assert.That(got, Is.Zero, $"{label}: tile {tile} is not in the aggregate region and still counts on the client");
            }
        }

        return counted;
    }
}
