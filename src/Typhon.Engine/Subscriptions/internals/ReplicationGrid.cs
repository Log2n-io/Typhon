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
/// when <c>c</c> is smaller than a spatial cell. The replication itself is still 2D until the deep implementation lands (10 § 12, 1.5.2); the third axis
/// is resolved and reported so the model and the API do not change when it does.
/// </para>
/// <para>
/// <b>Cells per axis are <c>⌈extent / c⌉ + 1</c></b>, one more than the half-open bounds need. Kept from the grid this replaces because <c>World</c> delivery
/// paces by cells visited in dense order, so one column fewer would change the frames a <c>World</c> session receives; it goes when pacing counts occupied
/// cells (1.5.3).
/// </para>
/// </remarks>
internal sealed class ReplicationGrid
{
    /// <summary>Cells per axis a cell key can address: 21 bits, the spatial VDB key's width.</summary>
    public const int MaxAxisCells = 1 << 21;

    /// <summary>The widest window the session state stores inline (<c>D0..D3</c>, 256 bits).</summary>
    public const int MaxWindow = 16;

    /// <summary>The most cells one window may cover (<c>W_x · W_y · W_z</c>, 10 § 4.3): what a session's gather pays per tick.</summary>
    public const int MaxWindowCells = 2809;

    public double CellM { get; private init; }

    public double OriginX { get; private init; }

    public double OriginY { get; private init; }

    public double OriginZ { get; private init; }

    public int DimX { get; private init; }

    public int DimY { get; private init; }

    public int DimZ { get; private init; }

    /// <summary>The largest sphere radius any profile declares; zero when every profile is <c>World</c>.</summary>
    public double Radius { get; private init; }

    /// <summary>How far a viewpoint may drift from its anchor before the anchor moves: <c>min(R / 48, c / 2)</c> (10 § 4.1).</summary>
    public double AnchorSlack { get; private init; }

    /// <summary>The window's half width in cells, <c>⌈R / c⌉ + 2</c>: two cells of margin, so a cell leaves only when all of it is past R from both anchors.</summary>
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
        var dimZ = spatial.GridDepth == 1 ? 1L : Dim(spatial.WorldMax.Z - spatial.WorldMin.Z, cellM);
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
        var windowCells = (long)window * window;
        if (window > MaxWindow || windowCells > MaxWindowCells)
        {
            throw new InvalidOperationException(
                $"SubscriptionsOptions.ReplicationCellM = {Format(cellM)} is too small for the largest Sphere radius, {Format(radius)}: a session's window "
                + $"would be {window} cells wide (2⌈R / c⌉ + 5), and the widest it may be is {MaxWindow}, so ⌈R / c⌉ must be at most "
                + $"{(MaxWindow - 5) / 2}. Raise the cell side to at least {Format(radius / ((MaxWindow - 5) / 2))}.");
        }

        return new ReplicationGrid
        {
            CellM = cellM,
            OriginX = spatial.WorldMin.X,
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

        static long Dim(double extent, double c) => Math.Max(1L, (long)Math.Min(Math.Ceiling(extent / c) + 1, long.MaxValue / 4));
    }

    /// <summary>The <c>Start</c> log line's grid description.</summary>
    public override string ToString()
        => $"cell {Format(CellM)}, {DimX} x {DimY} x {DimZ} cells from ({Format(OriginX)}, {Format(OriginY)}, {Format(OriginZ)}), "
           + $"window {Window} x {Window} (R {Format(Radius)}), {(Flat ? "flat" : "deep")}";

    private static string Format(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
