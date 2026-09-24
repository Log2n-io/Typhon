using System;
using System.Globalization;

namespace Typhon.Engine;

/// <summary>
/// The replication grid, resolved once at <c>Start</c> from the declared cell side and the spatial world (design/Subscriptions/10 § 3): what the push
/// index buckets events by, what a session's delivered window is counted in, and what the <c>Start</c> log reports.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declared, never derived</b> (L3): the cell side is <see cref="SubscriptionsOptions.ReplicationCellM"/>, and a runtime that observes an archetype
/// without it is refused. The bounds are the spatial config's, which every position codec already quantizes over.
/// </para>
/// <para>
/// <b>Three axes always</b> (L2). A spatial grid one cell deep gives a replication grid one cell deep whatever the side, so a flat world keeps its metric
/// when <c>c</c> is smaller than a spatial cell. The depth selects the implementation (10 § 3.5): the flat one for one cell, the deep one otherwise.
/// </para>
/// <para>
/// <b>Cells per axis are <c>⌈extent / c⌉</c></b>, the half-open bounds exactly; a position on the upper bound is clamped into the last cell.
/// </para>
/// </remarks>
internal sealed class ReplicationGrid
{
    /// <summary>Cells per axis a cell key can address: 21 bits, the spatial VDB key's width.</summary>
    public const int MaxAxisCells = 1 << 21;

    /// <summary>The widest window a row of the session state holds (one <see cref="ushort"/> per row).</summary>
    public const int MaxWindow = 16;

    /// <summary>The most cells one window may cover (<c>W_x · W_y · W_z</c>, 10 § 4.3): what a session's gather pays per tick.</summary>
    public const int MaxWindowCells = 2809;

    public double CellM { get; private init; }

    public double OriginX { get; private init; }

    public double OriginY { get; private init; }

    public double OriginZ { get; private init; }

    /// <summary>The spatial world's upper bounds, which every position codec quantizes up to: a decoded position never lies beyond them.</summary>
    public double WorldMaxX { get; private init; }

    public double WorldMaxY { get; private init; }

    public double WorldMaxZ { get; private init; }

    public int DimX { get; private init; }

    public int DimY { get; private init; }

    public int DimZ { get; private init; }

    /// <summary>The largest sphere radius any profile declares; zero when every profile is <c>World</c>.</summary>
    public double Radius { get; private init; }

    /// <summary>How far a viewpoint may drift from its anchor before the anchor moves: <c>min(R / 48, c / 2)</c> (10 § 4.1).</summary>
    public double AnchorSlack { get; private init; }

    /// <summary>
    /// The window's half width in cells, <c>⌈R / c⌉ + 2</c>: two cells of margin, so a cell leaves only when all of it is past R from both anchors.
    /// </summary>
    public int Half { get; private init; }

    /// <summary>The window's width in cells, <c>2 · Half + 1</c>.</summary>
    public int Window { get; private init; }

    /// <summary>Whether the grid is one cell deep.</summary>
    public bool Flat => DimZ == 1;

    /// <summary>Resolves the grid, refusing a configuration the replication cannot serve.</summary>
    /// <param name="cellM">The declared cell side, <see cref="SubscriptionsOptions.ReplicationCellM"/>.</param>
    /// <param name="spatial">The spatial world the grid covers.</param>
    /// <param name="maxRadius">The largest sphere radius any profile declares, zero when every profile is <c>World</c>.</param>
    /// <exception cref="InvalidOperationException">No cell side, a grid too wide for the cell key, or a window past its bound.</exception>
    public static ReplicationGrid Resolve(double cellM, in SpatialGridConfig spatial, double maxRadius)
    {
        if (!double.IsFinite(cellM) || cellM <= 0)
        {
            throw new InvalidOperationException(
                "SubscriptionsOptions.ReplicationCellM is not set. A profile observes an archetype, and replication tracks what each session was given "
                + "cell by cell over a grid of this side, so it must be declared: it sets every session's per-tick work (a window of 2⌈R / c⌉ + 5 cells "
                + "per axis). About a third of the largest Sphere radius is the usual choice.");
        }

        var dimX = Dim(spatial.WorldMax.X - spatial.WorldMin.X, cellM);
        var dimY = Dim(spatial.WorldMax.Y - spatial.WorldMin.Y, cellM);
        var dimZ = IsFlat(spatial, cellM) ? 1L : Dim(spatial.WorldMax.Z - spatial.WorldMin.Z, cellM);
        var widest = Math.Max(dimX, Math.Max(dimY, dimZ));
        if (widest > MaxAxisCells)
        {
            throw new InvalidOperationException(
                $"SubscriptionsOptions.ReplicationCellM = {Format(cellM)} gives a replication grid of {dimX} x {dimY} x {dimZ} cells over the spatial "
                + $"world; a cell key addresses at most {MaxAxisCells} cells per axis. Raise the cell side.");
        }

        var radius = Math.Max(0, maxRadius);
        var half = (int)Math.Ceiling(radius / cellM) + 2;
        var window = (2 * half) + 1;
        // The bound is on the cells a session's gather pays for (10 § 4.3): W² in a flat grid, W³ in a deep one — 15 and 13 cells per axis at most.
        var deep = dimZ > 1;
        var windowCells = (long)window * window * (deep ? window : 1);
        if (window > MaxWindow || windowCells > MaxWindowCells)
        {
            var widestWindow = deep ? 13 : 15;
            var reach = (widestWindow - 5) / 2;

            // The side named is rounded UP to the precision it is printed at, so the value the message suggests is one this check accepts.
            var smallest = Math.Ceiling(radius / reach * 1000d) / 1000d;
            throw new InvalidOperationException(
                $"SubscriptionsOptions.ReplicationCellM = {Format(cellM)} is too small for the largest Sphere radius, {Format(radius)}: a session's window "
                + $"would be {window} cells wide (2⌈R / c⌉ + 5), and the widest it may be is {widestWindow} in a {(deep ? "deep" : "flat")} grid "
                + $"({(deep ? "W³" : "W²")} ≤ {MaxWindowCells} cells), so ⌈R / c⌉ must be at most {reach}. Raise the cell side to at least "
                + $"{Format(smallest)}.");
        }

        return new ReplicationGrid
        {
            CellM = cellM,
            OriginX = spatial.WorldMin.X,
            WorldMaxX = spatial.WorldMax.X,
            WorldMaxY = spatial.WorldMax.Y,
            WorldMaxZ = spatial.WorldMax.Z,
            OriginY = spatial.WorldMin.Y,
            OriginZ = spatial.WorldMin.Z,
            DimX = (int)dimX,
            DimY = (int)dimY,
            DimZ = (int)dimZ,
            Radius = radius,
            AnchorSlack = Math.Min(radius / 48d, cellM / 2d),
            Half = half,
            Window = window,
        };

        static long Dim(double extent, double c) => Math.Max(1L, (long)Math.Min(Math.Ceiling(extent / c), long.MaxValue / 4));
    }

    /// <summary>
    /// Whether the replication grid over <paramref name="spatial"/> at cell side <paramref name="cellM"/> is one cell deep (10 § 3.1): a spatial world one
    /// cell deep, or one whose Z extent is a single replication cell. The catalog's region codec follows the same rule, so a flat grid's regions are
    /// polygons whatever the spatial world's depth. An undeclared side takes the spatial depth alone.
    /// </summary>
    public static bool IsFlat(in SpatialGridConfig spatial, double cellM) =>
        spatial.GridDepth == 1 || (double.IsFinite(cellM) && cellM > 0 && Math.Ceiling((spatial.WorldMax.Z - spatial.WorldMin.Z) / cellM) <= 1);

    /// <summary>The <c>Start</c> log line's grid description.</summary>
    public override string ToString()
        => $"cell {Format(CellM)}, {DimX} x {DimY} x {DimZ} cells from ({Format(OriginX)}, {Format(OriginY)}, {Format(OriginZ)}), "
           + $"window {Window} x {Window}{(Flat ? "" : $" x {Window}")} (R {Format(Radius)}), {(Flat ? "flat" : "deep")}";

    private static string Format(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
