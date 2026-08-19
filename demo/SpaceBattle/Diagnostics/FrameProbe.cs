using System;
using System.Collections.Generic;
using System.Text;
using SFML.Graphics;
using SFML.System;

namespace SpaceBattle;

/// <summary>
/// Reads back a rendered frame and describes it in text, so the rendering can be verified without a human looking
/// at it.
/// </summary>
/// <remarks>
/// This exists because "does it draw correctly?" is otherwise unanswerable from a terminal. The report gives an
/// ASCII coverage map plus per-colour-family pixel counts, which is enough to confirm that ships, cluster boxes,
/// grid lines and heat are all actually on screen and roughly where expected — and to catch the classic failures
/// (everything black, everything one colour, geometry off-screen, camera inverted).
/// </remarks>
internal static class FrameProbe
{
    public readonly struct Rect
    {
        public readonly int X, Y, W, H;
        public Rect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }

        public static bool TryParse(string s, out Rect r)
        {
            r = default;
            if (string.IsNullOrWhiteSpace(s))
            {
                return false;
            }
            var parts = s.Split(',');
            if (parts.Length != 4)
            {
                return false;
            }
            if (int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y) &&
                int.TryParse(parts[2], out var w) && int.TryParse(parts[3], out var h))
            {
                r = new Rect(x, y, w, h);
                return true;
            }
            return false;
        }
    }

    /// <summary>Coarse colour families, chosen to match what the renderer actually emits.</summary>
    private static string Classify(Color c)
    {
        int r = c.R, g = c.G, b = c.B;
        var max = Math.Max(r, Math.Max(g, b));
        if (max < 26)
        {
            return "background";
        }
        if (Math.Abs(r - g) < 24 && Math.Abs(g - b) < 24)
        {
            return max > 190 ? "white/selection" : "grey/gridline";
        }
        if (b > r + 30 && b >= g)
        {
            return "blue/factionA";
        }
        if (r > b + 30 && r > g + 20)
        {
            return "red/factionB-or-drift";
        }
        if (g > r + 25 && g > b + 25)
        {
            return "green/clusterAABB";
        }
        if (r > 150 && g > 150 && b < 140)
        {
            return "yellow/shotAABB";
        }
        if (r > 120 && b > 150 && g < 140)
        {
            return "purple/stationAABB";
        }
        return "other";
    }

    public static string Report(Image img, Rect? rect = null, int mapW = 64, int mapH = 24)
    {
        var w = (int)img.Size.X;
        var h = (int)img.Size.Y;
        var rx = 0;
        var ry = 0;
        var rw = w;
        var rh = h;
        if (rect.HasValue)
        {
            rx = Math.Clamp(rect.Value.X, 0, w - 1);
            ry = Math.Clamp(rect.Value.Y, 0, h - 1);
            rw = Math.Clamp(rect.Value.W, 1, w - rx);
            rh = Math.Clamp(rect.Value.H, 1, h - ry);
        }

        var counts = new Dictionary<string, int>();
        long lum = 0;
        var nonBg = 0;
        var occupancy = new int[mapH, mapW];

        for (var y = 0; y < rh; y++)
        {
            for (var x = 0; x < rw; x++)
            {
                var c = img.GetPixel(new Vector2u((uint)(rx + x), (uint)(ry + y)));
                var k = Classify(c);
                counts.TryGetValue(k, out var n);
                counts[k] = n + 1;
                var l = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                lum += l;
                if (k != "background")
                {
                    nonBg++;
                    var mx = x * mapW / rw;
                    var my = y * mapH / rh;
                    occupancy[my, mx]++;
                }
            }
        }

        var total = rw * rh;
        var sb = new StringBuilder();
        sb.Append($"frame {w}x{h}, rect ({rx},{ry},{rw},{rh}) = {total} px\n");
        sb.Append($"  mean luminance : {lum / (double)total:F1}\n");
        sb.Append($"  non-background : {nonBg} px ({100.0 * nonBg / total:F2} %)\n");
        sb.Append("  colour families:\n");
        var keys = new List<string>(counts.Keys);
        keys.Sort((a, b) => counts[b].CompareTo(counts[a]));
        foreach (var k in keys)
        {
            sb.Append($"    {k,-24} {counts[k],9} px  {100.0 * counts[k] / total,6:F2} %\n");
        }

        sb.Append("  coverage map (' '=empty .:-=+*#@ = density):\n");
        var maxCell = 1;
        for (var y = 0; y < mapH; y++)
        {
            for (var x = 0; x < mapW; x++)
            {
                if (occupancy[y, x] > maxCell)
                {
                    maxCell = occupancy[y, x];
                }
            }
        }
        const string ramp = " .:-=+*#@";
        for (var y = 0; y < mapH; y++)
        {
            sb.Append("    |");
            for (var x = 0; x < mapW; x++)
            {
                var t = occupancy[y, x] / (double)maxCell;
                var idx = (int)Math.Round(t * (ramp.Length - 1));
                sb.Append(ramp[Math.Clamp(idx, 0, ramp.Length - 1)]);
            }
            sb.Append("|\n");
        }
        return sb.ToString();
    }

    /// <summary>Sanity assertions a correct frame must satisfy. Returns the failures, empty when healthy.</summary>
    public static IReadOnlyList<string> Check(Image img, Rect? rect = null)
    {
        var problems = new List<string>();
        var w = (int)img.Size.X;
        var h = (int)img.Size.Y;
        var nonBg = 0;
        var families = new HashSet<string>();
        var total = 0;
        for (var y = 0; y < h; y += 2)
        {
            for (var x = 0; x < w; x += 2)
            {
                total++;
                var c = img.GetPixel(new Vector2u((uint)x, (uint)y));
                var k = Classify(c);
                families.Add(k);
                if (k != "background")
                {
                    nonBg++;
                }
            }
        }
        var pct = 100.0 * nonBg / Math.Max(1, total);
        if (pct < 0.5)
        {
            problems.Add($"frame is essentially empty ({pct:F2} % non-background) — nothing rendered?");
        }
        if (pct > 92)
        {
            problems.Add($"frame is essentially full ({pct:F2} %) — camera too close, or a fill covering everything?");
        }
        if (families.Count <= 2)
        {
            problems.Add($"only {families.Count} colour families present — overlays probably missing");
        }
        return problems;
    }
}
