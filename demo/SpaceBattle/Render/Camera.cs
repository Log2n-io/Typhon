using System;
using SFML.Graphics;
using SFML.System;
using SFML.Window;

namespace SpaceBattle;

/// <summary>
/// Pan/zoom camera over world space. Middle-drag pans; the wheel zooms toward the cursor.
/// </summary>
/// <remarks>
/// Zoom-at-cursor is the one piece worth stating: keep the world point under the mouse fixed by converting the
/// cursor to world space before the scale change and back after, then translating by the difference. Doing it any
/// other way makes the view drift, which is maddening when you are trying to keep one cluster in frame.
/// </remarks>
internal sealed class Camera
{
    private readonly RenderWindow _win;
    public Vector2f Center;
    public float ViewHeight;          // world units visible vertically

    /// <summary>Tightest zoom. 60 m across a 900 px window puts a 10 m ship at 150 px — plenty for inspection.</summary>
    public float MinHeight = 60f;
    public float MaxHeight = 200000f;

    private bool _panning;
    private Vector2i _lastMouse;

    public Camera(RenderWindow win, float worldSize)
    {
        _win = win;
        Center = new Vector2f(worldSize * 0.5f, worldSize * 0.5f);
        ViewHeight = worldSize * 1.06f;
        MaxHeight = worldSize * 4f;
    }

    public float Scale => _win.Size.Y / ViewHeight;

    /// <summary>
    /// World units covered by one screen pixel. The governing quantity for level of detail: how big an entity looks
    /// depends on this and nothing else, whereas camera distance alone is meaningless without the window size.
    /// </summary>
    public float UnitsPerPixel => _win.Size.Y > 0 ? ViewHeight / _win.Size.Y : 1f;

    public View BuildView()
    {
        var aspect = _win.Size.X / (float)_win.Size.Y;
        return new View(Center, new Vector2f(ViewHeight * aspect, ViewHeight));
    }

    /// <summary>The world rectangle currently on screen, optionally grown by a fraction on every side.</summary>
    public WorldRect VisibleRect(float marginFraction = 0f)
    {
        var aspect = _win.Size.X / (float)_win.Size.Y;
        var hh = ViewHeight * 0.5f * (1f + marginFraction);
        var hw = ViewHeight * aspect * 0.5f * (1f + marginFraction);
        return new WorldRect(Center.X - hw, Center.Y - hh, Center.X + hw, Center.Y + hh);
    }

    /// <summary>Recentres without changing zoom. Used by the minimap.</summary>
    public void JumpTo(Vector2f world) => Center = world;

    public Vector2f ScreenToWorld(Vector2i p)
    {
        var aspect = _win.Size.X / (float)_win.Size.Y;
        var vw = ViewHeight * aspect;
        var nx = p.X / (float)_win.Size.X - 0.5f;
        var ny = p.Y / (float)_win.Size.Y - 0.5f;
        return new Vector2f(Center.X + nx * vw, Center.Y + ny * ViewHeight);
    }

    public Vector2f WorldToScreen(Vector2f w)
    {
        var aspect = _win.Size.X / (float)_win.Size.Y;
        var vw = ViewHeight * aspect;
        var nx = (w.X - Center.X) / vw + 0.5f;
        var ny = (w.Y - Center.Y) / ViewHeight + 0.5f;
        return new Vector2f(nx * _win.Size.X, ny * _win.Size.Y);
    }

    public void OnMouseDown(Mouse.Button b, Vector2i pos)
    {
        if (b == Mouse.Button.Middle)
        {
            _panning = true;
            _lastMouse = pos;
        }
    }

    public void OnMouseUp(Mouse.Button b)
    {
        if (b == Mouse.Button.Middle)
        {
            _panning = false;
        }
    }

    /// <summary>
    /// Set whenever the operator PANS the camera, so a follow-lock can tell it has been overridden and let go.
    /// </summary>
    /// <remarks>
    /// Panning only. Zooming deliberately does not set this: changing magnification while tracking something is a
    /// normal thing to want, and a lock that dropped on every wheel click would be unusable at exactly the moment it
    /// is useful. Cleared by whoever consumes it.
    /// </remarks>
    public bool UserPanned { get; set; }

    public void OnMouseMove(Vector2i pos)
    {
        if (!_panning)
        {
            return;
        }
        var d = pos - _lastMouse;
        _lastMouse = pos;
        UserPanned = true;
        var aspect = _win.Size.X / (float)_win.Size.Y;
        Center.X -= d.X * (ViewHeight * aspect) / _win.Size.X;
        Center.Y -= d.Y * ViewHeight / _win.Size.Y;
    }

    public void OnWheel(float delta, Vector2i mouse)
    {
        var before = ScreenToWorld(mouse);
        var factor = MathF.Pow(1.15f, -delta);
        ViewHeight = Math.Clamp(ViewHeight * factor, MinHeight, MaxHeight);
        var after = ScreenToWorld(mouse);
        Center += before - after;      // keep the point under the cursor pinned
    }

    /// <summary>Fits the whole world in view on BOTH axes — on a wide window, height alone leaves it letterboxed.</summary>
    public void FrameWorld(float worldSize)
    {
        UserPanned = true;
        Center = new Vector2f(worldSize * 0.5f, worldSize * 0.5f);
        var aspect = _win.Size.X / (float)_win.Size.Y;
        ViewHeight = aspect >= 1f ? worldSize * 1.06f : worldSize * 1.06f / aspect;
    }
}
