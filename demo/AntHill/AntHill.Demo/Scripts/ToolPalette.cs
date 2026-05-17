using System;
using Godot;

namespace AntHill.Demo;

public enum ToolKind { Pointer, Food, Rock, Cull, Ignite, Pause }

/// <summary>
/// Horizontal tool bar at top-center. Buttons select a tool; the chosen one drives the
/// <see cref="ToolCursor3D"/>, the click handler in <c>Main</c>, and visually highlights itself.
/// </summary>
public partial class ToolPalette : CanvasLayer
{
    public event Action<ToolKind> ToolSelected;
    public ToolKind Current { get; private set; } = ToolKind.Pointer;

    private static readonly (ToolKind Kind, string Label)[] Tools =
    {
        (ToolKind.Pointer, "Pointer (1)"),
        (ToolKind.Food,    "Food (2)"),
        (ToolKind.Rock,    "Rock (3)"),
        (ToolKind.Cull,    "Cull (4)"),
        (ToolKind.Ignite,  "Ignite (5)"),
        (ToolKind.Pause,   "Pause (P)"),
    };

    private Button[] _buttons;

    public override void _Ready()
    {
        Layer = 11;

        var bg = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            OffsetLeft = -260,
            OffsetRight = 260,
            OffsetTop = 8,
            OffsetBottom = 44,
        };
        AddChild(bg);

        var row = new HBoxContainer { CustomMinimumSize = new Vector2(520, 32) };
        bg.AddChild(row);

        _buttons = new Button[Tools.Length];
        for (var i = 0; i < Tools.Length; i++)
        {
            var (kind, label) = Tools[i];
            var btn = new Button
            {
                Text = label,
                CustomMinimumSize = new Vector2(82, 28),
                ToggleMode = false,
            };
            var captured = kind;
            btn.Pressed += () => Select(captured);
            row.AddChild(btn);
            _buttons[i] = btn;
        }

        UpdateHighlights();
    }

    public void Select(ToolKind kind)
    {
        if (Current == kind) return;
        Current = kind;
        UpdateHighlights();
        ToolSelected?.Invoke(kind);
    }

    private void UpdateHighlights()
    {
        for (var i = 0; i < _buttons.Length; i++)
        {
            var selected = Tools[i].Kind == Current;
            _buttons[i].Modulate = selected ? new Color(1.0f, 0.85f, 0.4f) : Colors.White;
        }
    }
}
