using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using SFML.Graphics;
using SFML.System;

namespace SpaceBattle;

/// <summary>
/// Remembers where each window was, across runs.
/// </summary>
/// <remarks>
/// <para>
/// Stored per-user rather than beside the executable: <c>bin/</c> is wiped by a clean build, and losing your window
/// arrangement because you rebuilt is exactly the kind of small annoyance this is meant to remove.
/// </para>
/// <para>
/// A restored position is sanity-checked before use. Monitor layouts change — undock a laptop and a window saved on
/// the second screen would come back at coordinates that no longer exist, invisible and unreachable. When the saved
/// rectangle fails the check it is discarded rather than applied.
/// </para>
/// </remarks>
internal sealed class WindowLayout
{
    private sealed class Rect
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
    }

    private readonly Dictionary<string, Rect> _rects = new(StringComparer.Ordinal);
    private readonly string _path;
    private bool _dirty;

    public WindowLayout()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpaceBattle");
        _path = Path.Combine(dir, "window-layout.json");
        Load();
    }

    public string Path_ => _path;

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Rect>>(File.ReadAllText(_path));
            if (parsed == null)
            {
                return;
            }
            foreach (var (k, v) in parsed)
            {
                _rects[k] = v;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[layout] could not read {_path}: {ex.Message}");
        }
    }

    public void Save()
    {
        if (!_dirty)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path));
            File.WriteAllText(_path, JsonSerializer.Serialize(_rects, new JsonSerializerOptions { WriteIndented = true }));
            _dirty = false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[layout] could not write {_path}: {ex.Message}");
        }
    }

    /// <summary>Applies a saved rectangle to a freshly created window, if one exists and still looks sane.</summary>
    public bool Apply(RenderWindow win, string name)
    {
        if (!_rects.TryGetValue(name, out var r) || !IsPlausible(r))
        {
            return false;
        }
        win.Size = new Vector2u((uint)r.W, (uint)r.H);
        win.Position = new Vector2i(r.X, r.Y);
        // Size is applied before Position because moving first can be undone by the resize on some window managers.
        win.SetView(new View(new FloatRect(new Vector2f(0, 0), new Vector2f(r.W, r.H))));
        return true;
    }

    public void Capture(RenderWindow win, string name)
    {
        if (win is not { IsOpen: true })
        {
            return;
        }
        try
        {
            var p = win.Position;
            var s = win.Size;
            var r = new Rect { X = p.X, Y = p.Y, W = (int)s.X, H = (int)s.Y };
            if (!IsPlausible(r))
            {
                return;
            }
            _rects[name] = r;
            _dirty = true;
        }
        catch
        {
            // Querying a window that is mid-teardown can throw; a lost layout is not worth propagating.
        }
    }

    /// <summary>
    /// Loose bounds. Deliberately permissive about negative coordinates — a monitor placed left of the primary one
    /// has them legitimately — while rejecting the absurd values that mean the saved layout no longer applies.
    /// </summary>
    private static bool IsPlausible(Rect r) =>
        r != null &&
        r.W >= 240 && r.H >= 180 && r.W <= 16384 && r.H <= 16384 &&
        r.X > -32768 && r.X < 32768 && r.Y > -32768 && r.Y < 32768;

    public string Describe()
    {
        if (_rects.Count == 0)
        {
            return "none saved";
        }
        var sb = new StringBuilder();
        foreach (var (k, v) in _rects)
        {
            if (sb.Length > 0)
            {
                sb.Append("  ");
            }
            sb.Append(CultureInfo.InvariantCulture, $"{k} {v.W}x{v.H}@{v.X},{v.Y}");
        }
        return sb.ToString();
    }
}
