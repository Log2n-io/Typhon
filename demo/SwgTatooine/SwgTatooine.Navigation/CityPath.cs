using System;
using System.Collections.Generic;

namespace SwgTatooine.Navigation;

/// <summary>One collider of a structure as the server publishes it in <c>demo.json</c>: its plan rectangle and its height band, in the asset's frame.</summary>
/// <param name="MinX">West edge, already mirrored into the world's convention.</param>
/// <param name="MinZ">South edge.</param>
/// <param name="MaxX">East edge.</param>
/// <param name="MaxZ">North edge.</param>
/// <param name="MinY">Underside, above the structure's base.</param>
/// <param name="MaxY">Top, above the structure's base.</param>
/// <param name="Sloped">Whether its top is tilted: a ramp or a flight of steps — or a leaning strut, which <see cref="CityPath"/> tells apart by
/// height.</param>
/// <param name="Radius">The radius of its plan when that plan is a circle about the box's centre (a landing pad, a lamp post); 0 for the rectangle.</param>
public readonly record struct PlanBox(float MinX, float MinZ, float MaxX, float MaxZ, float MinY, float MaxY, bool Sloped, float Radius = 0f);

/// <summary>Where a structure asset's walls are: the boxes the server collides a body with, by the asset index a structure carries.</summary>
/// <remarks>
/// A bot reads them from <c>demo.json</c> (<c>BotAssets</c>); the server has them in hand. One interface so the search cannot tell the two apart.
/// </remarks>
public interface IWallPlans
{
    /// <summary>The asset's colliders in its own frame, mirrored into the world's convention; null for an index the plans do not know.</summary>
    /// <param name="asset">The structure's asset index.</param>
    PlanBox[] WallsOf(int asset);
}

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
        // The thread's own buffer, grown and cleared rather than allocated: a search is a megabyte at a city's size, and a fleet of thousands searching
        // through the large object heap was tens of megabytes a second of garbage (review).
        var n = cols * rows;
        if (t_blocked == null || t_blocked.Length < n)
        {
            t_blocked = new byte[Math.Max(n, (t_blocked?.Length ?? 0) * 2)];
        }

        Array.Clear(t_blocked, 0, n);
        Blocked = t_blocked;
    }

    /// <summary>The cell's edge, metres.</summary>
    public float Cell { get; }

    public float MinX { get; }

    public float MinZ { get; }

    public int Cols { get; }

    public int Rows { get; }

    /// <summary>
    /// 1 where a body standing in the cell would touch a wall; the first <see cref="Cols"/> × <see cref="Rows"/> entries are this grid's. The thread's
    /// reused buffer: a grid is good until the same thread builds the next one.
    /// </summary>
    public byte[] Blocked { get; }

    [ThreadStatic]
    private static byte[] t_blocked;
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
    public static bool OnRaisedFloor(IReadOnlyList<SeenStructure> structures, IWallPlans assets, float x, float z)
    {
        for (var i = 0; i < structures.Count; i++)
        {
            var s = structures[i];
            if (assets.WallsOf(s.Asset) is not { } walls)
            {
                continue;
            }

            foreach (var wall in walls)
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

    /// <summary>Whether a collider is a wall to a body: not a ramp or a flight of steps, not a floor under <paramref name="floorTopM"/>, not overhead.</summary>
    public static bool IsWall(in PlanBox wall, float floorTopM = StepM) =>
        !((wall.Sloped && wall.MaxY <= RampTopM) || wall.MaxY <= floorTopM || wall.MinY >= HeadM);

    /// <summary>The cell, metres.</summary>
    public const float CellM = 1f;

    /// <summary>The body's half-extent the walls are grown by — <c>demo.json</c>'s <c>playerBodyHalfM</c>.</summary>
    public const float BodyHalfM = 0.3f;

    /// <summary>
    /// The air a pulled-straight leg keeps from a wall beyond the body's own half, metres: the grid's walls are grown by the body alone, so a leg pulled
    /// taut round a corner grazed it, and a body steered a few times a second rather than continuously touched it (live, 2026-10-27).
    /// </summary>
    /// <remarks>
    /// <b>On the pulled legs, not on the grid.</b> Grown into the grid it would close the gaps the grid allows — a gate, a porch's way off, the alleys the
    /// crowd shares. Here it only decides where a leg may be pulled straight: when the straight leg to a far point would pass closer, the corner before it
    /// is kept, and the walk still goes everywhere the grid lets it.
    /// </remarks>
    public const float AirM = 0.15f;

    /// <summary>
    /// How far round a city's disc its buildings stand, metres: its port, the houses on its edge. A walk in a city is searched out to here, and a walk out
    /// of one leaves it <see cref="CityLeaveM"/> out — the server's crowd and the bots by the same figure.
    /// </summary>
    public const float CityMarginM = 60f;

    /// <summary>How far past a city's disc a walk out of it ends and the open desert starts, metres: a little past <see cref="CityMarginM"/>.</summary>
    public const float CityLeaveM = CityMarginM + 10f;

    /// <summary>Rasterises the walls of the structures that touch a rectangle.</summary>
    /// <param name="floorTopM">
    /// A collider topping out at or under this is walked on, not walked into: <see cref="StepM"/> from the ground, and <see cref="PorchTopM"/> for a body
    /// already standing up on a porch, which leaves it by walking off the edge.
    /// </param>
    public static WalkGrid Walls(
        IReadOnlyList<SeenStructure> structures,
        IWallPlans assets,
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
            var walls = assets.WallsOf(s.Asset);
            if (walls == null)
            {
                continue;
            }

            foreach (var wall in walls)
            {
                if (!IsWall(in wall, floorTopM))
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

    /// <summary>
    /// The walls of the structures that touch a rectangle, placed, turned and grown by <paramref name="half"/>, appended to <paramref name="into"/>: what
    /// <see cref="SegmentClear"/> tests a straight step against. The same rule as <see cref="Walls"/>; only the raster is missing.
    /// </summary>
    public static void CollectWalls(
        IReadOnlyList<SeenStructure> structures,
        IWallPlans assets,
        float minX,
        float minZ,
        float maxX,
        float maxZ,
        List<PlanBox> into,
        float half = BodyHalfM,
        float floorTopM = StepM)
    {
        for (var i = 0; i < structures.Count; i++)
        {
            var s = structures[i];
            if (assets.WallsOf(s.Asset) is not { } walls)
            {
                continue;
            }

            foreach (var wall in walls)
            {
                if (!IsWall(in wall, floorTopM))
                {
                    continue;
                }

                var b = Turn(s.Yaw, wall);
                var x0 = s.X + b.MinX - half;
                var x1 = s.X + b.MaxX + half;
                var z0 = s.Z + b.MinZ - half;
                var z1 = s.Z + b.MaxZ + half;
                if (x1 >= minX && x0 <= maxX && z1 >= minZ && z0 <= maxZ)
                {
                    into.Add(b with { MinX = x0, MinZ = z0, MaxX = x1, MaxZ = z1, Radius = b.Radius > 0f ? b.Radius + half : 0f });
                }
            }
        }
    }

    /// <summary>
    /// Whether a straight step between two points enters none of <paramref name="walls"/> (as <see cref="CollectWalls"/> grew them): exact, not sampled
    /// on a grid — a rectangle by its slabs, a disc by its distance to the step.
    /// </summary>
    /// <param name="grownBy">
    /// What the walls were grown by, for the air check; 0 for a plain test. Given it, a step may come as close to a wall as its nearer end already
    /// stands, and never closer: a body standing in a wall's air, or a stand point a body's width from a terminal, would otherwise fail every step to or
    /// from it — and passing over the whole wall for such a step let it graze the wall's far corner, or a disc's other side (review 6).
    /// </param>
    public static bool SegmentClear(List<PlanBox> walls, float ax, float az, float bx, float bz, float grownBy = 0f)
    {
        var (lx, hx) = ax < bx ? (ax, bx) : (bx, ax);
        var (lz, hz) = az < bz ? (az, bz) : (bz, az);
        var dx = bx - ax;
        var dz = bz - az;
        foreach (var w in walls)
        {
            if (hx <= w.MinX || lx >= w.MaxX || hz <= w.MinZ || lz >= w.MaxZ)
            {
                continue;
            }

            // How much of the growth this step is held to: all of it, or as much as its nearer end already keeps.
            var shrink = 0f;
            if (grownBy > 0f)
            {
                var near = MathF.Min(Outside(in w, grownBy, ax, az), Outside(in w, grownBy, bx, bz));
                if (near <= 0f)
                {
                    continue;   // an end on the wall itself: the grid's test is the only one that applies
                }

                shrink = near < grownBy ? grownBy - near + 1e-3f : 0f;
            }

            if (w.Radius > 0f)
            {
                var cx = (w.MinX + w.MaxX) * 0.5f;
                var cz = (w.MinZ + w.MaxZ) * 0.5f;
                var len2 = (dx * dx) + (dz * dz);
                var t = len2 > 0f ? Math.Clamp((((cx - ax) * dx) + ((cz - az) * dz)) / len2, 0f, 1f) : 0f;
                var ex = ax + (dx * t) - cx;
                var ez = az + (dz * t) - cz;
                var r = w.Radius - shrink;
                if ((ex * ex) + (ez * ez) < r * r)
                {
                    return false;
                }

                continue;
            }

            var t0 = 0f;
            var t1 = 1f;
            if (Slab(ax, dx, w.MinX + shrink, w.MaxX - shrink, ref t0, ref t1) && Slab(az, dz, w.MinZ + shrink, w.MaxZ - shrink, ref t0, ref t1) && t0 < t1)
            {
                return false;
            }
        }

        return true;

        // How far a point stands outside the wall itself — the box or disc before it was grown — metres; 0 or less on or inside it.
        static float Outside(in PlanBox w, float grownBy, float x, float z)
        {
            if (w.Radius > 0f)
            {
                var ox = x - ((w.MinX + w.MaxX) * 0.5f);
                var oz = z - ((w.MinZ + w.MaxZ) * 0.5f);
                return MathF.Sqrt((ox * ox) + (oz * oz)) - (w.Radius - grownBy);
            }

            var gx = MathF.Max(MathF.Max(w.MinX + grownBy - x, 0f), x - (w.MaxX - grownBy));
            var gz = MathF.Max(MathF.Max(w.MinZ + grownBy - z, 0f), z - (w.MaxZ - grownBy));
            return gx > 0f || gz > 0f ? MathF.Sqrt((gx * gx) + (gz * gz)) : -1f;
        }

        // Narrows [t0, t1] to the part of the step inside one axis's open interval; false when none of it is.
        static bool Slab(float a, float d, float min, float max, ref float t0, ref float t1)
        {
            if (MathF.Abs(d) < 1e-6f)
            {
                return a > min && a < max;
            }

            var ta = (min - a) / d;
            var tb = (max - a) / d;
            if (ta > tb)
            {
                (ta, tb) = (tb, ta);
            }

            t0 = MathF.Max(t0, ta);
            t1 = MathF.Min(t1, tb);
            return t0 < t1;
        }
    }

    /// <summary>How far a blocked start or goal is moved to open ground, metres: a body pressed into a corner, a goal a few metres into a building.</summary>
    public const float SnapM = 8f;

    /// <summary>
    /// How far a search that found no way is allowed to move them on its second try, metres: a body cloned onto the middle of a shuttleport's pad, a goal
    /// in the middle of a large building. A shuttleport's plaza is 21.6 m in radius.
    /// </summary>
    public const float WideSnapM = 30f;

    /// <summary>The longest side of a search searched on half-metre cells, metres; a longer one is searched on metre cells.</summary>
    public const float FineSearchM = 220f;

    /// <summary>Room round both ends of a search for a detour round a building, metres: the largest, a shuttleport, is 46 m across.</summary>
    public const float SearchMarginM = 30f;

    /// <summary>
    /// A walk between two points round the structures given: the window, the cell size and the porch rule a walker needs, then <see cref="Find"/>.
    /// </summary>
    /// <param name="structures">The structures near the walk.</param>
    /// <param name="assets">Their walls.</param>
    /// <param name="from">Where the walker stands.</param>
    /// <param name="to">Where it is going.</param>
    /// <param name="maxExpansions">How far the search may go before giving up.</param>
    /// <returns>The points to walk, or null when the goal is walled off.</returns>
    /// <remarks>
    /// <para>
    /// <b>Moved here from the bot's brain</b> so the server's scripted crowd walks a city exactly as a bot does (Loïc, 2026-10-24).
    /// </para>
    /// <para>
    /// Half-metre cells where the search is small enough to afford them — a street, a porch, a terminal's side — and metre cells across a city. A metre
    /// grid laid at an arbitrary origin closes a one-metre passage half the time: the large house's porch is left by one.
    /// </para>
    /// <para>
    /// Standing up on a porch — a house's foundation, 0.7 m above the street — the porch is a wall to everything on the ground and the floor under this
    /// body: the search snapped its start out past the walls round the porch and the first leg ran into them. Three live bots stood on one for a whole
    /// run. Searched as floor, the walk crosses the porch and steps off its edge. Only ON the porch: a body on the ground beside one stands in the same
    /// walled-off cells, and searched as floor its walk crossed the porch's corner — which the server, rightly, does not let a body on the ground walk
    /// into.
    /// </para>
    /// </remarks>
    public static List<(float X, float Z)> Route(
        IReadOnlyList<SeenStructure> structures,
        IWallPlans assets,
        (float X, float Z) from,
        (float X, float Z) to,
        int maxExpansions)
    {
        var minX = MathF.Min(from.X, to.X) - SearchMarginM;
        var minZ = MathF.Min(from.Z, to.Z) - SearchMarginM;
        var maxX = MathF.Max(from.X, to.X) + SearchMarginM;
        var maxZ = MathF.Max(from.Z, to.Z) + SearchMarginM;
        var cell = MathF.Max(maxX - minX, maxZ - minZ) <= FineSearchM ? CellM * 0.5f : CellM;
        var grid = Walls(structures, assets, minX, minZ, maxX, maxZ, cell: cell);
        var floorTopM = StepM;
        if (IsBlocked(grid, from.X, from.Z) && OnRaisedFloor(structures, assets, from.X, from.Z))
        {
            floorTopM = PorchTopM;
            grid = Walls(structures, assets, minX, minZ, maxX, maxZ, floorTopM: floorTopM, cell: cell);
        }

        // The same walls grown by the body and AirM, for where a leg may be pulled straight; the thread's list, as the grid is the thread's buffer.
        var air = t_air ??= [];
        air.Clear();
        CollectWalls(structures, assets, minX, minZ, maxX, maxZ, air, BodyHalfM + AirM, floorTopM);

        // Once more with a wide snap when the ends were deeper in than the usual one reaches. Measured on the crowd at --pop 5: 29 of 123 searches failed,
        // every one with an end inside a building — most on the middle of a shuttleport's pad.
        return Find(grid, from.X, from.Z, to.X, to.Z, maxExpansions, air: air)
            ?? Find(grid, from.X, from.Z, to.X, to.Z, maxExpansions, WideSnapM, air);
    }

    [ThreadStatic]
    private static List<PlanBox> t_air;

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

    /// <summary>
    /// The open cell nearest a walled-off one — of the first ring that has any, the one nearest <paramref name="towardX"/>, <paramref name="towardZ"/> —
    /// or -1. The first in scan order was the south-west one, so a walk to a kiosk could end on its far side (review).
    /// </summary>
    private static int NearestOpen(WalkGrid grid, int cell, int reach, float towardX, float towardZ)
    {
        if (grid.Blocked[cell] == 0)
        {
            return cell;
        }

        var c0 = cell % grid.Cols;
        var r0 = cell / grid.Cols;
        for (var ring = 1; ring <= reach; ring++)
        {
            var best = -1;
            var bestD = float.PositiveInfinity;
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
                    if (r < 0 || r >= grid.Rows || c < 0 || c >= grid.Cols || grid.Blocked[(r * grid.Cols) + c] != 0)
                    {
                        continue;
                    }

                    var (x, z) = CentreOf(grid, (r * grid.Cols) + c);
                    var d = ((x - towardX) * (x - towardX)) + ((z - towardZ) * (z - towardZ));
                    if (d < bestD)
                    {
                        best = (r * grid.Cols) + c;
                        bestD = d;
                    }
                }
            }

            if (best >= 0)
            {
                return best;
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
    /// <param name="maxExpansions">Cells it may expand before it gives up; a walled-off goal otherwise costs the whole grid.</param>
    /// <param name="air">
    /// The walls grown by the body and <see cref="AirM"/>: a leg is pulled straight only where it keeps that air as well as the grid's clearance. Null
    /// pulls on the grid alone.
    /// </param>
    public static List<(float X, float Z)> Find(
        WalkGrid grid,
        float fromX,
        float fromZ,
        float toX,
        float toZ,
        int maxExpansions = int.MaxValue,
        float snapM = SnapM,
        List<PlanBox> air = null)
    {
        // Eight metres each way: a body pressed into a street corner stands in cells its own width has walled off, and a goal can be a few metres into
        // a building — four cells left a bot wedged in Dry Wells with no search that could start.
        var reach = (int)MathF.Ceiling(snapM / grid.Cell);
        var start = NearestOpen(grid, CellOf(grid, fromX, fromZ), reach, fromX, fromZ);
        var startOpen = start == CellOf(grid, fromX, fromZ);
        var goal = NearestOpen(grid, CellOf(grid, toX, toZ), reach, fromX, fromZ);
        var goalOpen = goal == CellOf(grid, toX, toZ);
        if (start < 0 || goal < 0)
        {
            return null;
        }

        // The thread's buffers, stamped rather than cleared: a cell's cost and parent are this search's only when its stamp is this search's, so a
        // search touches the cells it expands and nothing else — no megabyte fills, no allocation (review).
        var n = grid.Cols * grid.Rows;
        Scratch.Ensure(n);
        var stamp = ++Scratch.Generation;
        if (stamp == int.MaxValue)
        {
            Array.Clear(Scratch.Seen);
            Array.Clear(Scratch.Closed);
            Scratch.Generation = stamp = 1;
        }

        var g = Scratch.G;
        var came = Scratch.Came;
        var seen = Scratch.Seen;
        var closed = Scratch.Closed;
        var (gx, gz) = CentreOf(grid, goal);
        var open = Scratch.Open;
        open.Clear();
        g[start] = 0f;
        came[start] = -1;
        seen[start] = stamp;
        open.Enqueue(start, Heuristic(grid, start, gx, gz));
        var cols = grid.Cols;
        var blocked = grid.Blocked;
        var expanded = 0;
        var reached = start == goal;
        while (open.TryDequeue(out var cell, out _))
        {
            if (closed[cell] == stamp)
            {
                continue;
            }

            if (cell == goal)
            {
                reached = true;
                break;
            }

            if (++expanded > maxExpansions)
            {
                return null;
            }

            closed[cell] = stamp;
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
                    if (seen[next] != stamp || cost < g[next])
                    {
                        seen[next] = stamp;
                        g[next] = cost;
                        came[next] = cell;
                        open.Enqueue(next, cost + Heuristic(grid, next, gx, gz));
                    }
                }
            }
        }

        if (!reached)
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
            // The goal itself when its cell is open; the open cell the search ended on when it is not — a walk that ended on a point inside a
            // building ended against its wall. A caller that needs the exact point (a stand point beside a terminal) walks it as its exact part.
            var j = cells.Count - 1;
            while (j > i)
            {
                var (bx, bz) = j == cells.Count - 1 && goalOpen ? (toX, toZ) : CentreOf(grid, cells[j]);
                if (LineClear(grid, ax, az, bx, bz) && (air == null || SegmentClear(air, ax, az, bx, bz, BodyHalfM + AirM)))
                {
                    break;
                }

                j--;
            }

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

    /// <summary>One thread's search buffers, grown as needed and never cleared between searches; see the generation stamp in <see cref="Find"/>.</summary>
    private static class Scratch
    {
        [ThreadStatic]
        private static float[] t_g;

        [ThreadStatic]
        private static int[] t_came;

        [ThreadStatic]
        private static int[] t_seen;

        [ThreadStatic]
        private static int[] t_closed;

        [ThreadStatic]
        private static PriorityQueue<int, float> t_open;

        [ThreadStatic]
        private static int t_generation;

        public static float[] G => t_g;

        public static int[] Came => t_came;

        public static int[] Seen => t_seen;

        public static int[] Closed => t_closed;

        public static PriorityQueue<int, float> Open => t_open ??= new PriorityQueue<int, float>();

        public static int Generation
        {
            get => t_generation;
            set => t_generation = value;
        }

        public static void Ensure(int n)
        {
            if (t_g != null && t_g.Length >= n)
            {
                return;
            }

            var size = Math.Max(n, (t_g?.Length ?? 0) * 2);
            t_g = new float[size];
            t_came = new int[size];

            // Fresh arrays are all zero, which no live generation is: stamps start at 1.
            t_seen = new int[size];
            t_closed = new int[size];
        }
    }

    private static float Heuristic(WalkGrid grid, int cell, float gx, float gz)
    {
        var (x, z) = CentreOf(grid, cell);
        var dx = MathF.Abs(x - gx);
        var dz = MathF.Abs(z - gz);
        return MathF.Max(dx, dz) + ((MathF.Sqrt(2f) - 1f) * MathF.Min(dx, dz));
    }
}
