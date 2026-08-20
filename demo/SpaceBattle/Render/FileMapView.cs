using System;
using System.Reflection;
using SFML.Graphics;
using SFML.System;

namespace SpaceBattle;

/// <summary>
/// A live map of the database file: one tile per page, coloured by what the page IS, brightened by how recently it
/// was WRITTEN.
/// </summary>
/// <remarks>
/// <para>
/// The static half is fully supported — <c>DatabaseEngine.ClassifyAllPages</c> is public and derives everything from
/// in-memory structures with no page I/O. The activity half is not: nothing public reports write recency. The
/// closest signal is <c>PagedMMF.PageInfo.DirtyCounter</c>, which lives in a <b>private</b> array, so this reaches it
/// by reflection once at startup and then reads the typed array directly (no per-page reflection).
/// </para>
/// <para>
/// If that field is ever renamed the map degrades to layout-only rather than failing — the reflection result is
/// cached as "unavailable" and the HUD says so. This is the one part of the tool built on an unsupported surface,
/// and it is deliberately the one part that is allowed to not work.
/// </para>
/// </remarks>
internal sealed class FileMapView
{
    private readonly Config _cfg;
    private readonly TyphonHost _host;

    private StoragePageType[] _types = Array.Empty<StoragePageType>();
    private float[] _heat = Array.Empty<float>();
    private int[] _lastDirty = Array.Empty<int>();

    private Array _pageInfos;                 // PagedMMF.PageInfo[] when reachable
    private FieldInfo _dirtyField;
    private bool _activityProbed;
    public string ActivityStatus { get; private set; } = "not probed";

    private readonly VertexArray _tiles = new(PrimitiveType.Triangles);
    private long _lastWalBytes;
    public double WalBytesPerSecond { get; private set; }
    private DateTime _lastWalSample = DateTime.UtcNow;

    public int PageCount => _types.Length;

    public FileMapView(Config cfg, TyphonHost host)
    {
        _cfg = cfg;
        _host = host;
    }

    private void ProbeActivitySource()
    {
        _activityProbed = true;
        try
        {
            object mmf = _host.DBE.MMF;
            // ManagedPagedMMF wraps or extends PagedMMF; walk the object graph for the private page-info array.
            var found = FindPageInfoArray(mmf, 0);
            if (found == null)
            {
                ActivityStatus = "unavailable (page-info array not found) — layout only";
                return;
            }
            _pageInfos = found;
            var elemType = found.GetType().GetElementType();
            _dirtyField = elemType?.GetField("DirtyCounter", BindingFlags.Public | BindingFlags.Instance);
            ActivityStatus = _dirtyField != null
                ? $"live (DirtyCounter over {found.Length} pages)"
                : "unavailable (DirtyCounter missing) — layout only";
        }
        catch (Exception ex)
        {
            ActivityStatus = $"unavailable ({ex.GetType().Name}) — layout only";
        }
    }

    /// <summary>
    /// Finds the page-info array. It is a PRIVATE field on <c>PagedMMF</c>, and <c>ManagedPagedMMF</c> derives from
    /// it — so the search must walk the base-type chain, because <c>GetFields</c> does not return private members of
    /// base classes. (That omission is exactly why the first version of this reported "not found".)
    /// </summary>
    private static Array FindPageInfoArray(object root, int depth)
    {
        if (root == null || depth > 2)
        {
            return null;
        }
        for (var t = root.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                object v;
                try
                {
                    v = f.GetValue(root);
                }
                catch
                {
                    continue;
                }
                if (v is Array arr && arr.GetType().GetElementType()?.Name == "PageInfo")
                {
                    return arr;
                }
                if (v != null && depth < 2 && v.GetType().Name.Contains("PagedMMF", StringComparison.Ordinal))
                {
                    var nested = FindPageInfoArray(v, depth + 1);
                    if (nested != null)
                    {
                        return nested;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Refreshes the layout and decays/boosts the heat. Called every N ticks, not every tick.</summary>
    public void Refresh()
    {
        if (!_activityProbed || (_pageInfos == null && _host.Tick % 240 == 0))
        {
            ProbeActivitySource();
        }

        var pageCount = (int)_host.DBE.MMF.StorageFilePageCount;
        if (pageCount <= 0)
        {
            return;
        }
        if (_types.Length != pageCount)
        {
            _types = new StoragePageType[pageCount];
            _heat = new float[pageCount];
            _lastDirty = new int[pageCount];
        }
        _host.DBE.ClassifyAllPages(_types);

        for (var i = 0; i < _heat.Length; i++)
        {
            _heat[i] *= _cfg.FileMapDecay;
        }

        if (_pageInfos != null && _dirtyField != null)
        {
            var n = Math.Min(_pageInfos.Length, pageCount);
            for (var i = 0; i < n; i++)
            {
                var pi = _pageInfos.GetValue(i);
                if (pi == null)
                {
                    continue;
                }
                var d = (int)_dirtyField.GetValue(pi);
                // A rising DirtyCounter means "written again since the checkpointer last looked".
                if (d != _lastDirty[i])
                {
                    _heat[i] = 1f;
                    _lastDirty[i] = d;
                }
                else if (d > 0)
                {
                    _heat[i] = MathF.Max(_heat[i], 0.35f);
                }
            }
        }

        var wal = _host.DBE.GetWalTotalBytes();
        var now = DateTime.UtcNow;
        var dt = (now - _lastWalSample).TotalSeconds;
        if (dt > 0.25)
        {
            WalBytesPerSecond = (wal - _lastWalBytes) / dt;
            _lastWalBytes = wal;
            _lastWalSample = now;
        }
    }

    private static Color BaseColor(StoragePageType t) => t switch
    {
        StoragePageType.Free => new Color(24, 26, 32),
        StoragePageType.Root => new Color(200, 90, 200),
        StoragePageType.Occupancy => new Color(90, 200, 200),
        StoragePageType.Component => new Color(60, 110, 180),
        StoragePageType.Index => new Color(200, 150, 60),
        _ => new Color(70, 80, 95),
    };

    public void Draw(RenderWindow win)
    {
        win.Clear(new Color(12, 13, 18));
        if (_types.Length == 0)
        {
            return;
        }
        _tiles.Clear();

        var w = (int)win.Size.X;
        var h = (int)win.Size.Y;
        var cols = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(_types.Length * (w / (float)Math.Max(1, h)))));
        var rows = (int)MathF.Ceiling(_types.Length / (float)cols);
        var tw = w / (float)cols;
        var th = Math.Min(tw, (h - 24f) / Math.Max(1, rows));

        for (var i = 0; i < _types.Length; i++)
        {
            var cx = i % cols;
            var cy = i / cols;
            var x0 = cx * tw;
            var y0 = 24f + cy * th;
            if (y0 > h)
            {
                break;
            }
            var b = BaseColor(_types[i]);
            var heat = _heat[i];
            var c = heat <= 0.01f
                ? b
                : new Color(
                    (byte)Math.Min(255, b.R + (int)(210 * heat)),
                    (byte)Math.Min(255, b.G + (int)(190 * heat)),
                    (byte)Math.Min(255, b.B + (int)(120 * heat)));

            var x1 = x0 + MathF.Max(1f, tw - 1f);
            var y1 = y0 + MathF.Max(1f, th - 1f);
            _tiles.Append(new Vertex(new Vector2f(x0, y0), c));
            _tiles.Append(new Vertex(new Vector2f(x1, y0), c));
            _tiles.Append(new Vertex(new Vector2f(x1, y1), c));
            _tiles.Append(new Vertex(new Vector2f(x0, y0), c));
            _tiles.Append(new Vertex(new Vector2f(x1, y1), c));
            _tiles.Append(new Vertex(new Vector2f(x0, y1), c));
        }
        win.Draw(_tiles);
    }
}
