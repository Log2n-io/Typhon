using System;
using System.Collections.Generic;
using System.IO;
using SFML.Graphics;
using SFML.System;

namespace SpaceBattle;

/// <summary>Screen-space text overlay. Deliberately plain — the world view is the interesting part.</summary>
internal sealed class Hud
{
    private readonly Font _font;
    private readonly Text _text;
    private readonly RectangleShape _panel = new();

    public bool Available => _font != null;

    public Hud()
    {
        foreach (var candidate in new[]
                 {
                     @"C:\Windows\Fonts\consola.ttf",
                     @"C:\Windows\Fonts\arial.ttf",
                     "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
                     "/System/Library/Fonts/Menlo.ttc",
                 })
        {
            if (File.Exists(candidate))
            {
                try
                {
                    _font = new Font(candidate);
                    break;
                }
                catch
                {
                    // try the next one
                }
            }
        }
        if (_font != null)
        {
            _text = new Text(_font, "", 13u);
        }
    }

    public void DrawPanel(IRenderTarget t, float x, float y, float w, float h, byte alpha = 190)
    {
        _panel.Position = new Vector2f(x, y);
        _panel.Size = new Vector2f(w, h);
        _panel.FillColor = new Color(10, 12, 18, alpha);
        _panel.OutlineColor = new Color(70, 80, 100, 220);
        _panel.OutlineThickness = 1f;
        t.Draw(_panel);
    }

    /// <summary>
    /// Width of the widest line, in pixels, so the panel can be sized to its contents.
    /// </summary>
    /// <remarks>
    /// Measured from the font rather than estimated from character counts. A hardcoded panel width was correct for
    /// one set of HUD lines and silently wrong the moment a longer one was added — the text simply ran off the
    /// background and onto the world. Only the longest CANDIDATE by character count is laid out, so this costs one
    /// text measurement per frame, not one per line.
    /// </remarks>
    public float MeasureMaxWidth(IReadOnlyList<(string text, Color color)> lines)
    {
        if (_text == null)
        {
            return 0f;
        }
        var longest = "";
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].text.Length > longest.Length)
            {
                longest = lines[i].text;
            }
        }
        _text.DisplayedString = longest;
        return _text.GetLocalBounds().Size.X;
    }

    public void DrawLines(IRenderTarget t, float x, float y, IReadOnlyList<(string text, Color color)> lines, float lineH = 16f)
    {
        if (_text == null)
        {
            return;
        }
        for (var i = 0; i < lines.Count; i++)
        {
            _text.DisplayedString = lines[i].text;
            _text.FillColor = lines[i].color;
            _text.Position = new Vector2f(x, y + i * lineH);
            t.Draw(_text);
        }
    }

    public void DrawLine(IRenderTarget t, float x, float y, string s, Color c, uint size = 13u)
    {
        if (_text == null)
        {
            return;
        }
        _text.CharacterSize = size;
        _text.DisplayedString = s;
        _text.FillColor = c;
        _text.Position = new Vector2f(x, y);
        t.Draw(_text);
        _text.CharacterSize = 13u;
    }
}
