using System;
using System.Text;

namespace SwgTatooine.Server;

/// <summary>A character's own waypoints' rules (client.md C38), as pure functions the Workshop, the endpoint and the checks share.</summary>
public static class Waypoints
{
    /// <summary>The most a character keeps [EST]: SWG's datapad held about this many.</summary>
    public const int MaxPerCharacter = 100;

    /// <summary>A name's longest, UTF-8 bytes.</summary>
    public const int NameMaxBytes = 32;

    /// <summary>
    /// A name as stored: trimmed, inner runs of white space made one space, control characters refused; null when nothing is left or it is past
    /// <see cref="NameMaxBytes"/>.
    /// </summary>
    public static string CleanName(ReadOnlySpan<byte> utf8)
    {
        string raw;
        try
        {
            raw = new UTF8Encoding(false, true).GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var name = new StringBuilder(raw.Length);
        var space = false;
        foreach (var ch in raw.Trim())
        {
            if (char.IsControl(ch))
            {
                return null;
            }

            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }

            if (space && name.Length > 0)
            {
                name.Append(' ');
            }

            space = false;
            name.Append(ch);
        }

        var text = name.ToString();
        return text.Length == 0 || Encoding.UTF8.GetByteCount(text) > NameMaxBytes ? null : text;
    }

    /// <summary>Whether a colour is one: 0 (the kind's) or <c>0xRRGGBB</c>.</summary>
    public static bool ColorOk(uint color) => color <= 0xFFFFFF;

    /// <summary>Whether a point is on the planet.</summary>
    public static bool OnPlanet(float x, float z)
        => float.IsFinite(x) && float.IsFinite(z) && MathF.Abs(x) <= TatooineData.PlanetHalfExtentM && MathF.Abs(z) <= TatooineData.PlanetHalfExtentM;
}
