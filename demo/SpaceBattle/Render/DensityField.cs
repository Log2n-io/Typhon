using System;
using System.Diagnostics;
using System.Numerics;
using SFML.Graphics;

namespace SpaceBattle;

/// <summary>Where <see cref="DensityField"/> gets its numbers.</summary>
internal enum DensitySource
{
    /// <summary>Bin every live entity. O(N), knows which faction each one belongs to. Ground truth.</summary>
    Entities = 0,

    /// <summary>Read the engine's per-cell occupancy counters. O(cells) — independent of population — but no faction split.</summary>
    Cells = 1,
}

/// <summary>
/// The far-zoom aggregate: a coarse grid of "how much is here", so that a view too wide to draw entities in still
/// answers the only question that matters at that range — is this empty space, or is something happening?
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an aggregate rather than smaller dots.</b> Points do not saturate a screen globally — a thousand of them
/// cover under 1% of the pixels — but they saturate exactly where the information is. Five hundred ships knotted
/// around a station occupy about nine pixels square with the whole world in view, so drawing them as points renders
/// 500 and 5 identically. Density has to be ENCODED once it can no longer be resolved.
/// </para>
/// <para>
/// <b>Two sources, deliberately.</b> <see cref="DensitySource.Cells"/> is what the aggregate should eventually be
/// built from: it reads counters the engine already maintains, so its cost does not grow with population at all.
/// <see cref="DensitySource.Entities"/> visits every entity and is therefore the thing to check it against — both
/// count all five archetypes, so their totals are directly comparable and any disagreement is a real defect in the
/// engine's occupancy accounting rather than a rendering artefact. Moving the default from one to the other is a
/// step-2 decision that should be made on a measurement, which is why both are kept.
/// </para>
/// </remarks>
internal sealed class DensityField
{
    private float[] _factionA;
    private float[] _factionB;
    private float[] _neutral;

    public int Resolution { get; private set; }
    public DensitySource Source { get; private set; }

    /// <summary>Largest single-bin total. The normaliser.</summary>
    public float Max { get; private set; }

    public float Total { get; private set; }
    public int OccupiedBins { get; private set; }
    public double BuildMs { get; private set; }

    /// <summary>True when the source cannot attribute counts to a faction, so everything lands in the neutral lane.</summary>
    public bool Factionless => Source == DensitySource.Cells;

    private float _worldSize;
    private float _binSize;

    public static DensitySource ParseSource(string s) =>
        string.Equals(s, "cells", StringComparison.OrdinalIgnoreCase) ? DensitySource.Cells : DensitySource.Entities;

    public void Build(Config cfg, TyphonHost host)
    {
        var t0 = Stopwatch.GetTimestamp();

        var res = Math.Clamp(cfg.DensityResolution, 8, 1024);
        if (_factionA == null || Resolution != res)
        {
            Resolution = res;
            _factionA = new float[res * res];
            _factionB = new float[res * res];
            _neutral = new float[res * res];
        }
        else
        {
            Array.Clear(_factionA);
            Array.Clear(_factionB);
            Array.Clear(_neutral);
        }

        _worldSize = cfg.WorldSize;
        _binSize = cfg.WorldSize / Resolution;
        Source = ParseSource(cfg.DensitySource);
        Max = 0;
        Total = 0;
        OccupiedBins = 0;

        if (Source == DensitySource.Cells)
        {
            BuildFromCells(host);
        }
        else
        {
            BuildFromEntities(host);
        }

        for (var i = 0; i < _neutral.Length; i++)
        {
            var n = _factionA[i] + _factionB[i] + _neutral[i];
            if (n <= 0)
            {
                continue;
            }
            OccupiedBins++;
            Total += n;
            if (n > Max)
            {
                Max = n;
            }
        }

        BuildMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>
    /// O(cells). Each engine cell's occupancy is spread evenly over the bins it covers, which keeps the field
    /// correct whether the bin grid is finer or coarser than the cell grid — the two resolutions are independent
    /// knobs and neither should have to know about the other.
    /// </summary>
    private void BuildFromCells(TyphonHost host)
    {
        var g = host.GridConfig;
        for (var cy = 0; cy < g.GridHeight; cy++)
        {
            for (var cx = 0; cx < g.GridWidth; cx++)
            {
                var n = host.CellEntityCount(host.Grid.ComputeCellKey(cx, cy));
                if (n <= 0)
                {
                    continue;
                }
                var x0 = g.WorldMin.X + cx * g.CellSize;
                var y0 = g.WorldMin.Y + cy * g.CellSize;
                var bx0 = BinIndex(x0);
                var by0 = BinIndex(y0);
                var bx1 = BinIndex(x0 + g.CellSize - 0.001f);
                var by1 = BinIndex(y0 + g.CellSize - 0.001f);
                var covered = (bx1 - bx0 + 1) * (by1 - by0 + 1);
                var share = n / (float)covered;
                for (var by = by0; by <= by1; by++)
                {
                    for (var bx = bx0; bx <= bx1; bx++)
                    {
                        _neutral[by * Resolution + bx] += share;
                    }
                }
            }
        }
    }

    /// <summary>
    /// O(N). Counts every archetype so the total is directly comparable with the cell-counter total, and splits by
    /// faction where the entity has one (rocks and loot are neutral by nature, not by omission).
    /// </summary>
    private void BuildFromEntities(TyphonHost host)
    {
        using var tx = host.DBE.CreateQuickTransaction();

        {
            using var acc = tx.For<Ship>();
            using var e = acc.GetClusterEnumerator();
            foreach (var cluster in e)
            {
                var bits = cluster.OccupancyBits;
                if (bits == 0)
                {
                    continue;
                }
                var pos = cluster.GetReadOnlySpan(Ship.Position);
                var com = cluster.GetReadOnlySpan(Ship.Combat);
                while (bits != 0)
                {
                    var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    if (com[i].Dead != 0)
                    {
                        continue;
                    }
                    Add(pos[i].Bounds.MinX, pos[i].Bounds.MinY, com[i].Faction, 1f);
                }
            }
        }
        {
            using var acc = tx.For<Shot>();
            using var e = acc.GetClusterEnumerator();
            foreach (var cluster in e)
            {
                var bits = cluster.OccupancyBits;
                if (bits == 0)
                {
                    continue;
                }
                var pos = cluster.GetReadOnlySpan(Shot.Position);
                var bul = cluster.GetReadOnlySpan(Shot.Bullet);
                while (bits != 0)
                {
                    var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    if (bul[i].Dead != 0)
                    {
                        continue;
                    }
                    Add(pos[i].Bounds.MinX, pos[i].Bounds.MinY, bul[i].Faction, 1f);
                }
            }
        }
        {
            using var acc = tx.For<Station>();
            using var e = acc.GetClusterEnumerator();
            foreach (var cluster in e)
            {
                var bits = cluster.OccupancyBits;
                if (bits == 0)
                {
                    continue;
                }
                var pos = cluster.GetReadOnlySpan(Station.Position);
                var inf = cluster.GetReadOnlySpan(Station.Info);
                while (bits != 0)
                {
                    var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    Add(pos[i].Bounds.MinX, pos[i].Bounds.MinY, inf[i].Faction, 1f);
                }
            }
        }
        {
            using var acc = tx.For<Rock>();
            using var e = acc.GetClusterEnumerator();
            foreach (var cluster in e)
            {
                var bits = cluster.OccupancyBits;
                if (bits == 0)
                {
                    continue;
                }
                var pos = cluster.GetReadOnlySpan(Rock.Position);
                var ast = cluster.GetReadOnlySpan(Rock.Asteroid);
                while (bits != 0)
                {
                    var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    if (ast[i].Dead != 0)
                    {
                        continue;
                    }
                    Add(pos[i].Bounds.MinX, pos[i].Bounds.MinY, 255, 1f);
                }
            }
        }
        {
            using var acc = tx.For<Loot>();
            using var e = acc.GetClusterEnumerator();
            foreach (var cluster in e)
            {
                var bits = cluster.OccupancyBits;
                if (bits == 0)
                {
                    continue;
                }
                var pos = cluster.GetReadOnlySpan(Loot.Position);
                var inf = cluster.GetReadOnlySpan(Loot.Info);
                while (bits != 0)
                {
                    var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    if (inf[i].Dead != 0)
                    {
                        continue;
                    }
                    Add(pos[i].Bounds.MinX, pos[i].Bounds.MinY, 255, 1f);
                }
            }
        }
    }

    private void Add(float x, float y, byte faction, float weight)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            return;
        }
        var idx = BinIndex(y) * Resolution + BinIndex(x);
        if (faction == 0)
        {
            _factionA[idx] += weight;
        }
        else if (faction == 1)
        {
            _factionB[idx] += weight;
        }
        else
        {
            _neutral[idx] += weight;
        }
    }

    private int BinIndex(float world)
    {
        var i = (int)(world / _binSize);
        return i < 0 ? 0 : i >= Resolution ? Resolution - 1 : i;
    }

    /// <summary>
    /// Where the fighting is: bins holding entities of BOTH factions, and how spread out those bins are.
    /// </summary>
    /// <remarks>
    /// The question "is this one big war or several small ones?" needs a number, not an impression. A bin holding
    /// both factions is a place where a fight can happen; counting them gives the number of simultaneous fronts,
    /// and the RMS distance of those bins from their own centroid gives the scale over which they are spread. One
    /// central scrum reads as a low count with a small spread whatever the screenshot looks like.
    /// </remarks>
    public (int contested, float spreadMetres, float centroidX, float centroidY) ContestedSpread()
    {
        double sx = 0, sy = 0;
        var n = 0;
        for (var by = 0; by < Resolution; by++)
        {
            for (var bx = 0; bx < Resolution; bx++)
            {
                var i = by * Resolution + bx;
                if (_factionA[i] <= 0f || _factionB[i] <= 0f)
                {
                    continue;
                }
                n++;
                sx += (bx + 0.5f) * _binSize;
                sy += (by + 0.5f) * _binSize;
            }
        }
        if (n == 0)
        {
            return (0, 0f, 0f, 0f);
        }
        var cx = (float)(sx / n);
        var cy = (float)(sy / n);

        double var2 = 0;
        for (var by = 0; by < Resolution; by++)
        {
            for (var bx = 0; bx < Resolution; bx++)
            {
                var i = by * Resolution + bx;
                if (_factionA[i] <= 0f || _factionB[i] <= 0f)
                {
                    continue;
                }
                var dx = (bx + 0.5f) * _binSize - cx;
                var dy = (by + 0.5f) * _binSize - cy;
                var2 += dx * dx + dy * dy;
            }
        }
        return (n, MathF.Sqrt((float)(var2 / n)), cx, cy);
    }

    /// <summary>Colour and opacity for one bin, or false when the bin is empty.</summary>
    public bool TryShade(int bx, int by, float gamma, float maxAlpha, out Color color)
    {
        color = default;
        var i = by * Resolution + bx;
        var a = _factionA[i];
        var b = _factionB[i];
        var n = _neutral[i];
        var total = a + b + n;
        if (total <= 0f || Max <= 0f)
        {
            return false;
        }

        // Gamma below 1 lifts sparse bins. Without it a single hot knot sets Max so high that everything else
        // rounds to black, and "there is one ship out here" — the whole point of the far view — is lost.
        var t = MathF.Pow(Math.Clamp(total / Max, 0f, 1f), gamma);

        // Hue from the faction mix; the neutral lane pulls toward grey rather than toward either side.
        var wa = a / total;
        var wb = b / total;
        var wn = n / total;
        var r = 90f * wa + 255f * wb + 170f * wn;
        var g = 170f * wa + 130f * wb + 165f * wn;
        var bl = 255f * wa + 95f * wb + 150f * wn;

        // Floor the alpha so an occupied bin is never invisible: distinguishing "one ship" from "nothing" matters
        // more at this range than showing the ratio between one ship and five hundred.
        var alpha = Math.Clamp(30f + t * (maxAlpha * 255f - 30f), 30f, 255f);
        color = new Color((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(bl, 0, 255), (byte)alpha);
        return true;
    }

    /// <summary>Emits the field as world-space quads, clipped to the visible rectangle.</summary>
    public void AppendWorldQuads(VertexArray va, Config cfg, in WorldRect visible, float alphaScale)
    {
        if (Max <= 0f || alphaScale <= 0f)
        {
            return;
        }
        var bx0 = Math.Max(0, BinIndex(visible.MinX) - 1);
        var by0 = Math.Max(0, BinIndex(visible.MinY) - 1);
        var bx1 = Math.Min(Resolution - 1, BinIndex(visible.MaxX) + 1);
        var by1 = Math.Min(Resolution - 1, BinIndex(visible.MaxY) + 1);

        for (var by = by0; by <= by1; by++)
        {
            for (var bx = bx0; bx <= bx1; bx++)
            {
                if (!TryShade(bx, by, cfg.DensityGamma, cfg.DensityAlpha, out var c))
                {
                    continue;
                }
                var x0 = bx * _binSize;
                var y0 = by * _binSize;
                var faded = new Color(c.R, c.G, c.B, (byte)Math.Clamp(c.A * alphaScale, 0f, 255f));
                Quad(va, x0, y0, x0 + _binSize, y0 + _binSize, faded);
            }
        }
    }

    private static void Quad(VertexArray va, float x0, float y0, float x1, float y1, Color c)
    {
        var a = new Vector2(x0, y0);
        var b = new Vector2(x1, y0);
        var d = new Vector2(x1, y1);
        var e = new Vector2(x0, y1);
        va.Append(new Vertex(new SFML.System.Vector2f(a.X, a.Y), c));
        va.Append(new Vertex(new SFML.System.Vector2f(b.X, b.Y), c));
        va.Append(new Vertex(new SFML.System.Vector2f(d.X, d.Y), c));
        va.Append(new Vertex(new SFML.System.Vector2f(a.X, a.Y), c));
        va.Append(new Vertex(new SFML.System.Vector2f(d.X, d.Y), c));
        va.Append(new Vertex(new SFML.System.Vector2f(e.X, e.Y), c));
    }
}
