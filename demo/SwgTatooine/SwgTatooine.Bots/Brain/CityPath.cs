using System;
using System.Collections.Generic;

namespace SwgTatooine.Bots.Brain;

/// <summary>A structure in a bot's store: where it stands, what it is and which asset draws it.</summary>
/// <param name="X">Where it stands, east.</param>
/// <param name="Z">North.</param>
/// <param name="Kind">Its <c>StructureKind</c>.</param>
/// <param name="Asset">The replicated asset index (<c>demo.json</c>'s <c>structures</c>).</param>
/// <param name="Yaw">Its quarter turns.</param>
/// <param name="Region">The city it belongs to, plus one; 0 in the wild.</param>
public readonly record struct SeenStructure(float X, float Z, int Kind, int Asset, int Yaw, int Region);

/// <summary>A walk grid over a rectangle of the world: 1 where a body standing in the cell would touch a wall.</summary>
public sealed class WalkGrid
{
    internal WalkGrid(float minX, float minZ, int cols, int rows, float cell)
    {
        Cell = cell;
        MinX = minX;
        MinZ = minZ;
        Cols = cols;
        Rows = rows;
        Blocked = new byte[cols * rows];
    }

    /// <summary>The cell's edge, metres.</summary>
    public float Cell { get; }

    public float MinX { get; }

    public float MinZ { get; }

    public int Cols { get; }

    public int Rows { get; }

    public byte[] Blocked { get; }
}

/// <summary>
/// A walk across a city around its buildings: the browser's <c>city-path.ts</c>, ported, so a bot and a person clicking a facility take the same way.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a grid and not the colliders.</b> A city is a few hundred boxes and a walk's question is "which way round"; rasterising the walls into a metre
/// grid and searching it (A*, eight neighbours, no corner cutting) answers it in milliseconds, and the cells are then pulled straight wherever the grid
/// says the line is clear — a diagonal, not a staircase.
/// </para>
/// <para>
/// <b>A wall is what a body cannot step onto</b>: a collider whose top is more than a step above its structure's base and whose bottom is under a head.
/// Steps, ramps (sloped tops) and floors are walked. The same rule as the browser's, on the same published boxes, so the two cannot disagree about a
/// doorway.
/// </para>
/// </remarks>
public static class CityPath
{
    /// <summary>A step a body climbs without noticing, metres (the server's <c>PlayerStepM</c>).</summary>
    public const float StepM = 0.4f;

    /// <summary>Anything that starts above this is overhead, metres.</summary>
    public const float HeadM = 1.8f;

    /// <summary>A sloped collider whose top is under this is a ramp or a flight of steps, metres; a taller one is a leaning strut, and a wall.</summary>
    /// <remarks>The published ramps top out between 0.2 m and 1.26 m (the port's stairs to its pad); the port's ring of struts lean at 2.3 m.</remarks>
    public const float RampTopM = 1.5f;

    /// <summary>A raised floor a body may be standing on, metres: a house's porch, on its foundation 0.7 m up.</summary>
    public const float PorchTopM = 1f;

    /// <summary>
    /// Whether a point stands ON a raised floor — inside the plan of a flat collider whose top is above a step and no higher than a porch — and not merely
    /// beside one, in the margin its walls are grown by.
    /// </summary>
    public static bool OnRaisedFloor(IReadOnlyList<SeenStructure> structures, BotAssets assets, float x, float z)
    {
        for (var i = 0; i < structures.Count; i++)
        {
            var s = structures[i];
            if (assets.At(s.Asset) is not { } plan)
            {
                continue;
            }

            foreach (var wall in plan.Walls)
            {
                if (wall.Sloped || wall.MaxY <= StepM || wall.MaxY > PorchTopM)
                {
                    continue;
                }

                var b = Turn(s.Yaw, wall);
                if (x > s.X + b.MinX && x < s.X + b.MaxX && z > s.Z + b.MinZ && z < s.Z + b.MaxZ)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether a point's cell is walled off.</summary>
    public static bool IsBlocked(WalkGrid grid, float x, float z) => grid.Blocked[CellOf(grid, x, z)] != 0;

    /// <summary>The cell, metres.</summary>
    public const float CellM = 1f;

    /// <summary>The body's half-extent the walls are grown by — <c>demo.json</c>'s <c>playerBodyHalfM</c>.</summary>
    public const float BodyHalfM = 0.3f;

    /// <summary>Rasterises the walls of the structures that touch a rectangle.</summary>
    /// <param name="floorTopM">
    /// A collider topping out at or under this is walked on, not walked into: <see cref="StepM"/> from the ground, and <see cref="PorchTopM"/> for a body
    /// already standing up on a porch, which leaves it by walking off the edge.
    /// </param>
    public static WalkGrid Walls(
        IReadOnlyList<SeenStructure> structures,
        BotAssets assets,
        float minX,
        float minZ,
        float maxX,
        float maxZ,
        float half = BodyHalfM,
        float floorTopM = StepM,
        float cell = CellM)
    {
        var cols = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / cell));
        var rows = Math.Max(1, (int)MathF.Ceiling((maxZ - minZ) / cell));
        var grid = new WalkGrid(minX, minZ, cols, rows, cell);
        var blocked = grid.Blocked;
        for (var i = 0; i < structures.Count; i++)
        {
            var s = structures[i];
            var plan = assets.At(s.Asset);
            if (plan == null)
            {
                continue;
            }

            foreach (var wall in plan.Walls)
            {
                if ((wall.Sloped && wall.MaxY <= RampTopM) || wall.MaxY <= floorTopM || wall.MinY >= HeadM)
                {
                    continue;
                }

                var b = Turn(s.Yaw, wall);
                var x0 = s.X + b.MinX - half;
                var x1 = s.X + b.MaxX + half;
                var z0 = s.Z + b.MinZ - half;
                var z1 = s.Z + b.MaxZ + half;
                if (x1 < minX || x0 > maxX || z1 < minZ || z0 > maxZ)
                {
                    continue;
                }

                var c0 = Math.Max(0, (int)MathF.Floor((x0 - minX) / cell));
                var c1 = Math.Min(cols - 1, (int)MathF.Floor((x1 - minX) / cell));
                var r0 = Math.Max(0, (int)MathF.Floor((z0 - minZ) / cell));
                var r1 = Math.Min(rows - 1, (int)MathF.Floor((z1 - minZ) / cell));
                if (b.Radius > 0f)
                {
                    // A round collider as the disc it is: its bounding square closed the corridor round the port's landing pad, and the server
                    // collides with the cylinder.
                    var cx = s.X + ((b.MinX + b.MaxX) * 0.5f);
                    var cz = s.Z + ((b.MinZ + b.MaxZ) * 0.5f);
                    var reach = b.Radius + half;
                    for (var r = r0; r <= r1; r++)
                    {
                        for (var c = c0; c <= c1; c++)
                        {
                            var dx = minX + ((c + 0.5f) * cell) - cx;
                            var dz = minZ + ((r + 0.5f) * cell) - cz;
                            if ((dx * dx) + (dz * dz) <= reach * reach)
                            {
                                blocked[(r * cols) + c] = 1;
                            }
                        }
                    }

                    continue;
                }

                for (var r = r0; r <= r1; r++)
                {
                    Array.Fill(blocked, (byte)1, (r * cols) + c0, c1 - c0 + 1);
                }
            }
        }

        return grid;
    }

    /// <summary>A box turned by quarter turns about the structure's origin — the browser's <c>turn</c> and the server's <c>QuarterTurn</c>.</summary>
    public static PlanBox Turn(int quarters, PlanBox b) => (quarters & 3) switch
    {
        1 => b with { MinX = b.MinZ, MinZ = -b.MaxX, MaxX = b.MaxZ, MaxZ = -b.MinX },
        2 => b with { MinX = -b.MaxX, MinZ = -b.MaxZ, MaxX = -b.MinX, MaxZ = -b.MinZ },
        3 => b with { MinX = -b.MaxZ, MinZ = b.MinX, MaxX = -b.MinZ, MaxZ = b.MaxX },
        _ => b,
    };

    private static int CellOf(WalkGrid grid, float x, float z)
    {
        var c = Math.Clamp((int)MathF.Floor((x - grid.MinX) / grid.Cell), 0, grid.Cols - 1);
        var r = Math.Clamp((int)MathF.Floor((z - grid.MinZ) / grid.Cell), 0, grid.Rows - 1);
        return (r * grid.Cols) + c;
    }

    private static (float X, float Z) CentreOf(WalkGrid grid, int cell)
    {
        var c = cell % grid.Cols;
        var r = cell / grid.Cols;
        return (grid.MinX + ((c + 0.5f) * grid.Cell), grid.MinZ + ((r + 0.5f) * grid.Cell));
    }

    private static int NearestOpen(WalkGrid grid, int cell, int reach)
    {
        if (grid.Blocked[cell] == 0)
        {
            return cell;
        }

        var c0 = cell % grid.Cols;
        var r0 = cell / grid.Cols;
        for (var ring = 1; ring <= reach; ring++)
        {
            for (var dr = -ring; dr <= ring; dr++)
            {
                for (var dc = -ring; dc <= ring; dc++)
                {
                    if (Math.Max(Math.Abs(dr), Math.Abs(dc)) != ring)
                    {
                        continue;
                    }

                    var r = r0 + dr;
                    var c = c0 + dc;
                    if (r >= 0 && r < grid.Rows && c >= 0 && c < grid.Cols && grid.Blocked[(r * grid.Cols) + c] == 0)
                    {
                        return (r * grid.Cols) + c;
                    }
                }
            }
        }

        return -1;
    }

    /// <summary>Whether the straight line between two points crosses no blocked cell, sampled at a third of a cell.</summary>
    public static bool LineClear(WalkGrid grid, float ax, float az, float bx, float bz)
    {
        var steps = (int)MathF.Ceiling(MathF.Sqrt(((bx - ax) * (bx - ax)) + ((bz - az) * (bz - az))) / (grid.Cell / 3f));
        for (var i = 1; i < steps; i++)
        {
            var t = (float)i / steps;
            if (grid.Blocked[CellOf(grid, ax + ((bx - ax) * t), az + ((bz - az) * t))] != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A path around the walls as waypoints — the start excluded, the goal last — or null when there is none inside the grid.</summary>
    public static List<(float X, float Z)> Find(WalkGrid grid, float fromX, float fromZ, float toX, float toZ)
    {
        // Eight metres each way: a body pressed into a street corner stands in cells its own width has walled off, and a goal can be a few metres into
        // a building — four cells left a bot wedged in Dry Wells with no search that could start.
        var reach = (int)MathF.Ceiling(8f / grid.Cell);
        var start = NearestOpen(grid, CellOf(grid, fromX, fromZ), reach);
        var startOpen = start == CellOf(grid, fromX, fromZ);
        var goal = NearestOpen(grid, CellOf(grid, toX, toZ), reach);
        var goalOpen = goal == CellOf(grid, toX, toZ);
        if (start < 0 || goal < 0)
        {
            return null;
        }

        var n = grid.Cols * grid.Rows;
        var g = new float[n];
        Array.Fill(g, float.PositiveInfinity);
        var came = new int[n];
        Array.Fill(came, -1);
        var closed = new bool[n];
        var (gx, gz) = CentreOf(grid, goal);
        var open = new PriorityQueue<int, float>();
        g[start] = 0f;
        open.Enqueue(start, Heuristic(grid, start, gx, gz));
        var cols = grid.Cols;
        var blocked = grid.Blocked;
        while (open.TryDequeue(out var cell, out _))
        {
            if (closed[cell])
            {
                continue;
            }

            if (cell == goal)
            {
                break;
            }

            closed[cell] = true;
            var c = cell % cols;
            var r = cell / cols;
            for (var dr = -1; dr <= 1; dr++)
            {
                for (var dc = -1; dc <= 1; dc++)
                {
                    if (dr == 0 && dc == 0)
                    {
                        continue;
                    }

                    var nr = r + dr;
                    var nc = c + dc;
                    if (nr < 0 || nr >= grid.Rows || nc < 0 || nc >= cols)
                    {
                        continue;
                    }

                    var next = (nr * cols) + nc;
                    // No corner cutting: a diagonal needs both of the cells it passes between to be open.
                    if (blocked[next] != 0 || (dr != 0 && dc != 0 && (blocked[(r * cols) + nc] != 0 || blocked[(nr * cols) + c] != 0)))
                    {
                        continue;
                    }

                    var cost = g[cell] + ((dr != 0 && dc != 0 ? MathF.Sqrt(2f) : 1f) * grid.Cell);
                    if (cost < g[next])
                    {
                        g[next] = cost;
                        came[next] = cell;
                        open.Enqueue(next, cost + Heuristic(grid, next, gx, gz));
                    }
                }
            }
        }

        if (start != goal && came[goal] < 0)
        {
            return null;
        }

        // Back from the goal, then pulled straight: each kept point is the furthest along the cell path still in sight.
        var cells = new List<int>();
        for (var cell = goal; cell != start && cell >= 0; cell = came[cell])
        {
            cells.Add(cell);
        }

        cells.Reverse();
        var path = new List<(float X, float Z)>();
        var ax = fromX;
        var az = fromZ;
        if (!startOpen)
        {
            // Out of the wall's margin first, to the open cell the search began on: pulling straight from a body pressed against a house drew the
            // first leg across the house's corner.
            (ax, az) = CentreOf(grid, start);
            path.Add((ax, az));
        }
        var i = 0;
        while (i < cells.Count)
        {
            var j = cells.Count - 1;
            while (j > i)
            {
                var (bx, bz) = CentreOf(grid, cells[j]);
                if (LineClear(grid, ax, az, bx, bz))
                {
                    break;
                }

                j--;
            }

            // The goal itself when its cell is open; the open cell the search ended on when it is not — a walk that ended on a point inside a
            // building ended against its wall. A caller that needs the exact point (a stand point beside a terminal) walks it as its exact part.
            var p = j == cells.Count - 1 ? (goalOpen ? (toX, toZ) : CentreOf(grid, goal)) : CentreOf(grid, cells[j]);
            path.Add(p);
            (ax, az) = p;
            i = j + 1;
        }

        if (path.Count == 0)
        {
            path.Add((toX, toZ));
        }

        return path;
    }

    private static float Heuristic(WalkGrid grid, int cell, float gx, float gz)
    {
        var (x, z) = CentreOf(grid, cell);
        var dx = MathF.Abs(x - gx);
        var dz = MathF.Abs(z - gz);
        return MathF.Max(dx, dz) + ((MathF.Sqrt(2f) - 1f) * MathF.Min(dx, dz));
    }
}
