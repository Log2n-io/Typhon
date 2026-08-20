using System;
using System.Collections.Generic;
using SFML.Graphics;
using SFML.System;

namespace SpaceBattle;

/// <summary>
/// A fixed-size world overview in the corner of the screen, with the camera's footprint drawn on it.
/// </summary>
/// <remarks>
/// <para>
/// Justified by one number: a 3 km tactical view over a 100 km world shows nine millionths of the map by area. At
/// that ratio panning is not navigation, it is a search — you cannot get anywhere without a picture of where you
/// currently are.
/// </para>
/// <para>
/// It renders the SAME <see cref="DensityField"/> the far-zoom LOD uses, at a different transform. That is not
/// merely convenient: a minimap fed by its own aggregation would be free to disagree with the main view, and a
/// navigation aid that disagrees with the thing it is helping you navigate is worse than none.
/// </para>
/// <para>
/// Drawn in the screen-space overlay view, so its size is in pixels and does not move with the camera or scale with
/// the window.
/// </para>
/// </remarks>
internal sealed class Minimap
{
    private readonly VertexArray _bins = new(PrimitiveType.Triangles);

    /// <summary>Landmark geometry, kept apart from <see cref="_bins"/> — that array is already drawn by the time
    /// landmarks are built, so appending to it would render the density field a second time on top of itself.</summary>
    private readonly VertexArray _marks = new(PrimitiveType.Triangles);
    private readonly VertexArray _lines = new(PrimitiveType.Lines);

    /// <summary>Screen rectangle the map occupies. Zero-sized until the first draw — used for hit-testing clicks.</summary>
    public FloatRect Bounds { get; private set; }

    public void Draw(IRenderTarget target, Config cfg, DensityField field, Camera cam,
                     IReadOnlyList<Landmark> landmarks, uint winW, uint winH)
    {
        var size = Math.Min(cfg.MinimapSize, (int)Math.Min(winW, winH) / 3);
        if (size < 60)
        {
            Bounds = default;
            return;    // window too small to be worth the clutter
        }

        var x0 = winW - size - cfg.MinimapMargin;
        var y0 = winH - size - cfg.MinimapMargin;
        Bounds = new FloatRect(new Vector2f(x0, y0), new Vector2f(size, size));

        var panel = new RectangleShape(new Vector2f(size, size))
        {
            Position = new Vector2f(x0, y0),
            FillColor = new Color(10, 12, 18, 215),
            OutlineColor = new Color(90, 105, 130, 230),
            OutlineThickness = 1f,
        };
        target.Draw(panel);

        _bins.Clear();
        _marks.Clear();
        _lines.Clear();

        var res = field.Resolution;
        if (res > 0)
        {
            // One quad per occupied bin. Sized with a half-pixel overlap so neighbouring bins do not leave seams
            // when size/res is fractional — a grid of hairlines reads as structure that is not there.
            var step = size / (float)res;
            var pad = 0.5f;
            for (var by = 0; by < res; by++)
            {
                for (var bx = 0; bx < res; bx++)
                {
                    if (!field.TryShade(bx, by, cfg.DensityGamma, 1f, out var c))
                    {
                        continue;
                    }
                    var px = x0 + bx * step;
                    var py = y0 + by * step;
                    Quad(_bins, px, py, px + step + pad, py + step + pad, c);
                }
            }
        }
        if (_bins.VertexCount > 0)
        {
            target.Draw(_bins);
        }

        // Landmarks. Drawn from the renderer's list rather than re-read here, so the minimap marks exactly the
        // places the main view marks — the same reason it shares the density field.
        var ls = size / cfg.WorldSize;
        var lp = cfg.MinimapLandmarkPixels;
        if (landmarks != null)
        {
            for (var i = 0; i < landmarks.Count; i++)
            {
                var l = landmarks[i];
                var px = x0 + l.X * ls;
                var py = y0 + l.Y * ls;
                if (px < x0 || px > x0 + size || py < y0 || py > y0 + size)
                {
                    continue;
                }
                switch (l.Kind)
                {
                    case LandmarkKind.Station:
                    {
                        // PickupKind carries the disabled flag for stations — a wreck reads as a hollow outline.
                        var down = l.PickupKind != 0;
                        var c = l.Faction == 0 ? new Color(120, 190, 255) : new Color(255, 175, 70);
                        if (down)
                        {
                            c = new Color((byte)(c.R / 3), (byte)(c.G / 3), (byte)(c.B / 3));
                        }
                        else
                        {
                            Quad(_marks, px - lp * 0.5f, py - lp * 0.5f, px + lp * 0.5f, py + lp * 0.5f, c);
                        }
                        Box(_lines, px - lp * 0.9f, py - lp * 0.9f, px + lp * 0.9f, py + lp * 0.9f, c);
                        // Health bar under the marker: a station losing hull is the thing worth noticing on a map
                        // you are only glancing at.
                        if (l.ProgressB < 1f)
                        {
                            MiniBar(px, py + lp * 1.5f, lp * 0.9f, l.ProgressB, new Color(120, 230, 150));
                        }
                        break;
                    }

                    case LandmarkKind.Asteroid:
                    {
                        // Ore reads as a diamond, so shape alone distinguishes it from a station at 6 px.
                        var c = new Color(235, 215, 160);
                        Diamond(_marks, px, py, lp * 0.7f, c);
                        break;
                    }

                    case LandmarkKind.TheOne:
                    {
                        // White, and the only thing on this map drawn with a crosshair. Shape carries it rather than
                        // colour alone: at 6 px a white diamond and a pale station square are the same smudge, and
                        // the whole point of this marker is to be findable in one glance without hunting.
                        var mr = lp * 1.3f;
                        Diamond(_marks, px, py, mr, new Color(255, 255, 255));

                        // Faction tint on the crosshair, not the body — the body stays white to match the ship, but
                        // "which side does the invincible thing belong to" is the first question anyone asks of it.
                        var fc = l.Faction == 0 ? new Color(120, 190, 255) : new Color(255, 175, 70);
                        Line(_lines, px - mr * 2.4f, py, px - mr * 1.2f, py, fc);
                        Line(_lines, px + mr * 1.2f, py, px + mr * 2.4f, py, fc);
                        Line(_lines, px, py - mr * 2.4f, px, py - mr * 1.2f, fc);
                        Line(_lines, px, py + mr * 1.2f, px, py + mr * 2.4f, fc);
                        break;
                    }

                    default:
                    {
                        // The contested pickup gets the largest marker on the map and the race drawn beside it —
                        // at most one exists, and while it does it is the most important thing on the board.
                        var c = Renderer.PickupColor(l.PickupKind);
                        var mr = lp * 1.15f;
                        Diamond(_marks, px, py, mr, c);
                        Box(_lines, px - mr * 1.7f, py - mr * 1.7f, px + mr * 1.7f, py + mr * 1.7f, c);
                        MiniBar(px, py - mr * 2.3f, mr * 1.7f, l.ProgressA, new Color(120, 190, 255));
                        MiniBar(px, py - mr * 3.1f, mr * 1.7f, l.ProgressB, new Color(255, 175, 70));
                        break;
                    }
                }
            }
            if (_marks.VertexCount > 0)
            {
                target.Draw(_marks);
            }
        }

        // Camera footprint. Clamped to the panel so it stays visible (as a thin sliver against the edge) when the
        // camera is pushed outside the world, rather than being scissored away exactly when you are most lost.
        var v = cam.VisibleRect();
        var s = size / cfg.WorldSize;
        var rx0 = Math.Clamp(x0 + v.MinX * s, x0, x0 + size);
        var ry0 = Math.Clamp(y0 + v.MinY * s, y0, y0 + size);
        var rx1 = Math.Clamp(x0 + v.MaxX * s, x0, x0 + size);
        var ry1 = Math.Clamp(y0 + v.MaxY * s, y0, y0 + size);

        // A rectangle narrower than a pixel draws as nothing; at high zoom that is the normal case, so give it a
        // floor and let it read as a marker rather than a region.
        if (rx1 - rx0 < 3f)
        {
            var mid = (rx0 + rx1) * 0.5f;
            rx0 = mid - 1.5f;
            rx1 = mid + 1.5f;
        }
        if (ry1 - ry0 < 3f)
        {
            var mid = (ry0 + ry1) * 0.5f;
            ry0 = mid - 1.5f;
            ry1 = mid + 1.5f;
        }
        Box(_lines, rx0, ry0, rx1, ry1, new Color(255, 255, 255, 235));
        if (_lines.VertexCount > 0)
        {
            target.Draw(_lines);
        }
    }

    /// <summary>Maps a click inside the map to a world position. False when the click was elsewhere.</summary>
    public bool TryWorldAt(Vector2i screen, Config cfg, out Vector2f world)
    {
        world = default;
        if (Bounds.Size.X <= 0 || !Bounds.Contains(new Vector2f(screen.X, screen.Y)))
        {
            return false;
        }
        var u = (screen.X - Bounds.Position.X) / Bounds.Size.X;
        var v = (screen.Y - Bounds.Position.Y) / Bounds.Size.Y;
        world = new Vector2f(u * cfg.WorldSize, v * cfg.WorldSize);
        return true;
    }

    private static void Diamond(VertexArray va, float cx, float cy, float r, Color c)
    {
        va.Append(new Vertex(new Vector2f(cx, cy - r), c));
        va.Append(new Vertex(new Vector2f(cx + r, cy), c));
        va.Append(new Vertex(new Vector2f(cx, cy + r), c));
        va.Append(new Vertex(new Vector2f(cx, cy - r), c));
        va.Append(new Vertex(new Vector2f(cx, cy + r), c));
        va.Append(new Vertex(new Vector2f(cx - r, cy), c));
    }

    /// <summary>Two-pixel-tall race bar beside the pickup marker.</summary>
    private void MiniBar(float cx, float y, float halfWidth, float t, Color c)
    {
        Box(_lines, cx - halfWidth, y - 1.5f, cx + halfWidth, y + 1.5f, new Color(c.R, c.G, c.B, 150));
        if (t > 0f)
        {
            Quad(_marks, cx - halfWidth, y - 1.5f, cx - halfWidth + 2f * halfWidth * t, y + 1.5f, c);
        }
    }

    private static void Quad(VertexArray va, float x0, float y0, float x1, float y1, Color c)
    {
        va.Append(new Vertex(new Vector2f(x0, y0), c));
        va.Append(new Vertex(new Vector2f(x1, y0), c));
        va.Append(new Vertex(new Vector2f(x1, y1), c));
        va.Append(new Vertex(new Vector2f(x0, y0), c));
        va.Append(new Vertex(new Vector2f(x1, y1), c));
        va.Append(new Vertex(new Vector2f(x0, y1), c));
    }

    private static void Box(VertexArray va, float x0, float y0, float x1, float y1, Color c)
    {
        Line(va, x0, y0, x1, y0, c);
        Line(va, x1, y0, x1, y1, c);
        Line(va, x1, y1, x0, y1, c);
        Line(va, x0, y1, x0, y0, c);
    }

    private static void Line(VertexArray va, float x0, float y0, float x1, float y1, Color c)
    {
        va.Append(new Vertex(new Vector2f(x0, y0), c));
        va.Append(new Vertex(new Vector2f(x1, y1), c));
    }
}
