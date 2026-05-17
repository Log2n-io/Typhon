using System.Collections.Generic;
using AntHill.Core;
using Godot;

namespace AntHill.Demo;

/// <summary>
/// Right-side scrolling event log. Latest entry on top, capped at <see cref="MaxRows"/> rows.
/// Each row is a flat <c>Button</c> so the click handler is trivial — click → fly the camera
/// to the event's recorded world position via the existing <c>SetFollowTarget</c>.
/// </summary>
public partial class EventLogHud : CanvasLayer
{
    private const int MaxRows = 20;

    private GameCamera _camera;
    private VBoxContainer _list;
    private readonly Queue<Button> _rows = new();

    public void Initialize(GameCamera camera) => _camera = camera;

    public override void _Ready()
    {
        Layer = 10;

        var panel = new PanelContainer
        {
            AnchorLeft = 1.0f,
            AnchorRight = 1.0f,
            AnchorTop = 0.0f,
            AnchorBottom = 1.0f,
            OffsetLeft = -340,
            OffsetRight = -10,
            OffsetTop = 80,        // sits below the existing _hudRight perf line
            OffsetBottom = -10,
        };
        AddChild(panel);

        _list = new VBoxContainer { AnchorRight = 1.0f, AnchorBottom = 1.0f };
        panel.AddChild(_list);
    }

    public void AddEntry(LogEntry entry)
    {
        var btn = new Button
        {
            Text = Format(entry),
            Alignment = HorizontalAlignment.Left,
            ClipText = true,
            Flat = true,
            CustomMinimumSize = new Vector2(320, 22),
        };
        btn.AddThemeColorOverride("font_color", ColorFor(entry.Severity));
        btn.AddThemeFontSizeOverride("font_size", 12);

        var x = entry.WorldX;
        var z = entry.WorldZ;
        btn.Pressed += () => _camera?.SetFollowTarget(new Vector3(x, 0f, z));

        _list.AddChild(btn);
        _list.MoveChild(btn, 0);   // newest on top
        _rows.Enqueue(btn);

        while (_rows.Count > MaxRows)
        {
            var oldest = _rows.Dequeue();
            oldest.QueueFree();
        }
    }

    private static string Format(LogEntry e)
    {
        var mins = (int)(e.TimeSec / 60f);
        var secs = e.TimeSec - mins * 60f;
        return mins > 0
            ? $"T+{mins}m{secs:00.0}s  {e.Text}"
            : $"T+{e.TimeSec,4:0.0}s  {e.Text}";
    }

    private static Color ColorFor(LogSeverity s) => s switch
    {
        LogSeverity.Tool => new Color(0.55f, 0.85f, 1.00f),
        LogSeverity.Action => new Color(1.00f, 0.55f, 0.55f),
        LogSeverity.Depletion => new Color(1.00f, 0.90f, 0.45f),
        _ => new Color(0.75f, 0.75f, 0.75f),
    };
}
