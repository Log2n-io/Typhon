using System;

namespace SwgTatooine.Server;

/// <summary>
/// A craft as numbers (crafting-catalog.md § 3.4): the ceilings of its lines from its inputs, then how far each line got — the assembly, the
/// experimentation points, the enhancement — every roll <see cref="Schematics.Roll"/>. Pure: the workshop applies it step by step, the checks run it whole,
/// and the client's twin computes the same ceilings.
/// </summary>
/// <remarks>
/// <b>Fractions are kept in millionths</b> (<see cref="Scale"/>), rounded after every step, as the workshop stores them between commands: a craft resumed
/// after a restart, or replayed by the checks, reaches the same numbers bit for bit.
/// </remarks>
public static class Crafting
{
    /// <summary>A ceiling's or a progress's unit: 1 = <see cref="Scale"/>.</summary>
    public const int Scale = 1_000_000;

    /// <summary>The most lines a schematic has (a weapon's four).</summary>
    public const int MaxLines = 4;

    /// <summary>Each line's ceiling, in millionths.</summary>
    public static void Ceilings(Schematic s, ReadOnlySpan<Schematics.Input> inputs, Span<int> q)
    {
        for (var i = 0; i < s.Lines.Length; i++)
        {
            q[i] = ToScale(Schematics.Ceiling(in s.Lines[i], inputs, s.Slots));
        }
    }

    /// <summary>The assembly (step 0): one roll sets every line's start.</summary>
    public static void Assemble(Schematic s, long crafter, long serial, Span<int> f)
    {
        var f0 = ToScale(Schematics.Assemble(Schematics.Roll(crafter, serial, 0)));
        for (var i = 0; i < s.Lines.Length; i++)
        {
            f[i] = f0;
        }
    }

    /// <summary>The <paramref name="point"/>-th experimentation point (1-based: its step), spent on line <paramref name="line"/>.</summary>
    public static void Experiment(long crafter, long serial, int point, Span<int> f, int line)
        => f[line] = ToScale(Schematics.Experiment(f[line] / (double)Scale, Schematics.Roll(crafter, serial, point)));

    /// <summary>The life factor (crafting-catalog.md § 9.1): 900..1 100 per mille, rolled with the assembly (its own stream of step 0).</summary>
    public static short LifeMilli(long crafter, long serial) => (short)(900 + (int)(Schematics.Roll(crafter ^ 0x5A5A, serial, 0) * 201));

    /// <summary>Each line's value, from its ceiling, how far it got and the add-on's Mk.</summary>
    public static double ValueOf(Schematic s, int line, int q, int f, byte addonMk)
        => Schematics.ValueOf(in s.Lines[line], q / (double)Scale, f / (double)Scale, addonMk);

    /// <summary>
    /// A whole craft in one call: the ceilings, the assembly, the points spent as <paramref name="pointsPerLine"/> says (line 0's first, in order), and the
    /// values. What the checks and the bots' planning use; the workshop runs the same steps one command at a time.
    /// </summary>
    public static void Run(Schematic s, ReadOnlySpan<Schematics.Input> inputs, long crafter, long serial, ReadOnlySpan<int> pointsPerLine, byte addonMk,
        Span<double> values)
    {
        Span<int> q = stackalloc int[MaxLines];
        Span<int> f = stackalloc int[MaxLines];
        Ceilings(s, inputs, q);
        Assemble(s, crafter, serial, f);
        var point = 1;
        for (var line = 0; line < s.Lines.Length && line < pointsPerLine.Length; line++)
        {
            for (var p = 0; p < pointsPerLine[line]; p++)
            {
                Experiment(crafter, serial, point++, f, line);
            }
        }

        for (var line = 0; line < s.Lines.Length; line++)
        {
            values[line] = ValueOf(s, line, q[line], f[line], addonMk);
        }
    }

    private static int ToScale(double x) => (int)Math.Round(Math.Clamp(x, 0d, 1d) * Scale, MidpointRounding.AwayFromZero);
}
