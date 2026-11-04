using System;
using System.Text;

namespace SwgTatooine.Server;

/// <summary>The inbox's fixed points (mail.md § 2, D3).</summary>
public static class Mail
{
    /// <summary>The most mails a character keeps: past it, the oldest is dropped (Loïc, 2026-11-04).</summary>
    public const int MaxPerCharacter = 100;

    /// <summary>How long a mail is kept, days of world time.</summary>
    public const int LifeDays = 30;

    /// <summary>A subject's longest, UTF-8 bytes: what a <c>String64</c> holds.</summary>
    public const int SubjectMaxBytes = 63;

    /// <summary>A text's longest, UTF-8 bytes, within a <c>String1024</c>.</summary>
    public const int TextMaxBytes = 1000;

    /// <summary>A /tell's longest, UTF-8 bytes: a chat line's.</summary>
    public const int TellMaxBytes = 256;

    /// <summary>
    /// A subject as stored: trimmed, control characters refused, inner white space kept as one space; null when nothing is left or it is past
    /// <see cref="SubjectMaxBytes"/>.
    /// </summary>
    public static string CleanSubject(string raw) => Clean(raw, SubjectMaxBytes, lines: false);

    /// <summary>
    /// A text as stored: trimmed, line breaks kept and other control characters refused; null past <see cref="TextMaxBytes"/>. Empty is allowed.
    /// </summary>
    public static string CleanText(string raw)
    {
        if (raw == null || raw.Trim().Length == 0)
        {
            return string.Empty;
        }

        return Clean(raw, TextMaxBytes, lines: true);
    }

    private static string Clean(string raw, int maxBytes, bool lines)
    {
        if (raw == null)
        {
            return null;
        }

        var text = new StringBuilder(raw.Length);
        var space = false;
        foreach (var ch in raw.Trim())
        {
            if (ch == '\n' && lines)
            {
                text.Append('\n');
                space = false;
                continue;
            }

            if (ch == '\r' && lines)
            {
                continue;
            }

            if (char.IsControl(ch))
            {
                return null;
            }

            if (char.IsWhiteSpace(ch) && !lines)
            {
                space = true;
                continue;
            }

            if (space && text.Length > 0)
            {
                text.Append(' ');
            }

            space = false;
            text.Append(ch);
        }

        var cleaned = text.ToString();
        return cleaned.Length == 0 || Encoding.UTF8.GetByteCount(cleaned) > maxBytes ? null : cleaned;
    }

    /// <summary>A mail's age limit in world ticks at <paramref name="tickRateHz"/>.</summary>
    public static long LifeTicks(int tickRateHz) => (long)LifeDays * 86_400 * Math.Max(1, tickRateHz);
}
