using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SwgTatooine;

/// <summary>
/// A command line that refuses what it does not understand, and that can print itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the lenient parser it replaced was a measurement hazard (SWG-07).</b> Every flag used to be read with
/// <c>Array.IndexOf</c> plus <c>TryParse</c>, falling back to the default when the flag was absent, when its value would not parse, and when the flag was
/// misspelt — three different situations reported identically, which is to say not reported at all. This repo's measurement rules require interleaved
/// same-binary A/B pairs, so a mistyped <c>--target-ratio</c> produced two arms that were byte-for-byte the same run while the report claimed a difference
/// had been measured. That is not a hypothetical: it is the reason this item is scheduled before anything else in WP-3.
/// </para>
/// <para>
/// <b>Five ways to be wrong, all fatal.</b> An unknown flag; a value that will not parse; a flag at the end of the line with no value; a value that is
/// itself a flag (the shape a forgotten value takes); and the same flag given twice — that last because <c>IndexOf</c> takes the FIRST occurrence, so
/// appending <c>--hz 50</c> to a line that already said <c>--hz 10</c> ran at 10 and said nothing about it.
/// </para>
/// <para>
/// <b>The declarations are the help text.</b> Each accessor records the flag it read, with its kind and the default it fell back to, so
/// <see cref="Help"/> is generated from the same list the parse walked. A flag cannot exist without appearing in <c>--help</c>, which is the property that
/// matters: the previous generation had no <c>--help</c> at all and the only way to learn a flag's name was to read the parser.
/// </para>
/// </remarks>
internal sealed class ArgReader
{
    private readonly string[] _args;
    private readonly bool[] _used;
    private readonly List<Entry> _declared = [];

    /// <summary>
    /// Set when the line asks for help, in which case nothing is fatal: the whole point is to reach <see cref="Help"/> and print it, and a malformed
    /// value that threw on the way there would answer a request for the flag list with a complaint about one flag.
    /// </summary>
    private readonly bool _lenient;

    /// <summary>One declared flag, or a group header when <see cref="Name"/> is null.</summary>
    private readonly record struct Entry(string Name, string Kind, string Default, string Help);

    /// <summary>Wraps a command line.</summary>
    /// <param name="args">The tokens, as the process received them.</param>
    public ArgReader(string[] args)
    {
        _args = args ?? [];
        _used = new bool[_args.Length];
        _lenient = Array.IndexOf(_args, "--help") >= 0 || Array.IndexOf(_args, "-h") >= 0;
    }

    /// <summary>Whether the line asks for the flag list rather than a run.</summary>
    public bool WantsHelp => _lenient;

    /// <summary>Opens a section in <see cref="Help"/>. Affects nothing else.</summary>
    /// <param name="title">The heading.</param>
    public void Group(string title) => _declared.Add(new Entry(null, null, null, title));

    /// <summary>Reads a valueless flag.</summary>
    /// <param name="name">The flag.</param>
    /// <param name="help">One line, for <c>--help</c>.</param>
    /// <returns>Whether it is present.</returns>
    public bool Switch(string name, string help)
    {
        _declared.Add(new Entry(name, "", "off", help));
        return Locate(name) >= 0;
    }

    /// <summary>Reads a flag's <see cref="float"/> value.</summary>
    /// <param name="name">The flag.</param>
    /// <param name="fallback">The value when the flag is absent.</param>
    /// <param name="help">One line, for <c>--help</c>.</param>
    /// <returns>The value.</returns>
    public float Float(string name, float fallback, string help)
    {
        var text = Value(name, "<number>", Show(fallback), help);
        if (text == null)
        {
            return fallback;
        }

        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }

        Malformed(name, text, "a number");
        return fallback;
    }

    /// <summary>Reads a flag's <see cref="int"/> value.</summary>
    /// <param name="name">The flag.</param>
    /// <param name="fallback">The value when the flag is absent.</param>
    /// <param name="help">One line, for <c>--help</c>.</param>
    /// <returns>The value.</returns>
    public int Int(string name, int fallback, string help)
    {
        var text = Value(name, "<integer>", Show(fallback), help);
        if (text == null)
        {
            return fallback;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }

        Malformed(name, text, "a whole number");
        return fallback;
    }

    /// <summary>Reads a flag's <see cref="int"/> value, distinguishing "absent" from any value it could carry.</summary>
    /// <param name="name">The flag.</param>
    /// <param name="help">One line, for <c>--help</c>.</param>
    /// <returns>The value, or null when the flag is absent.</returns>
    /// <remarks>For a setting whose absence means "leave the engine's own default", where 0 is a legitimate value and cannot double as "unset".</remarks>
    public int? IntOrNull(string name, string help)
    {
        var text = Value(name, "<integer>", "engine default", help);
        if (text == null)
        {
            return null;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }

        Malformed(name, text, "a whole number");
        return null;
    }

    /// <summary>Reads a flag's <see cref="double"/> value.</summary>
    /// <param name="name">The flag.</param>
    /// <param name="fallback">The value when the flag is absent.</param>
    /// <param name="help">One line, for <c>--help</c>.</param>
    /// <returns>The value.</returns>
    public double Dbl(string name, double fallback, string help)
    {
        var text = Value(name, "<number>", Show(fallback), help);
        if (text == null)
        {
            return fallback;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }

        Malformed(name, text, "a number");
        return fallback;
    }

    /// <summary>Reads a flag's string value.</summary>
    /// <param name="name">The flag.</param>
    /// <param name="fallback">The value when the flag is absent.</param>
    /// <param name="help">One line, for <c>--help</c>.</param>
    /// <param name="kind">How the value is described in <c>--help</c>.</param>
    /// <returns>The value.</returns>
    public string Str(string name, string fallback, string help, string kind = "<text>")
        => Value(name, kind, fallback ?? "(none)", help) ?? fallback;

    /// <summary>Reads a flag whose value is one of a fixed set.</summary>
    /// <param name="name">The flag.</param>
    /// <param name="fallback">The value when the flag is absent; must itself be allowed.</param>
    /// <param name="allowed">The accepted values, in the order <c>--help</c> lists them.</param>
    /// <param name="help">One line, for <c>--help</c>.</param>
    /// <returns>The value.</returns>
    public string Choice(string name, string fallback, string[] allowed, string help)
    {
        var text = Value(name, string.Join("|", allowed), fallback, help);
        if (text == null)
        {
            return fallback;
        }

        if (Array.IndexOf(allowed, text) >= 0)
        {
            return text;
        }

        Malformed(name, text, "one of " + string.Join(", ", allowed));
        return fallback;
    }

    /// <summary>Reads a flag whose value is a comma-separated list of numbers.</summary>
    /// <param name="name">The flag.</param>
    /// <param name="fallback">The list when the flag is absent.</param>
    /// <param name="help">One line, for <c>--help</c>.</param>
    /// <returns>The list.</returns>
    /// <remarks>An empty list is refused rather than accepted as "sweep nothing", which would report a sweep of zero points as a success.</remarks>
    public float[] Floats(string name, float[] fallback, string help)
    {
        var text = Value(name, "<n,n,...>", string.Join(",", Array.ConvertAll(fallback, Show)), help);
        if (text == null)
        {
            return fallback;
        }

        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            Malformed(name, text, "at least one number");
            return fallback;
        }

        var values = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                Malformed(name, parts[i], "a number in a comma-separated list");
                return fallback;
            }
        }

        return values;
    }

    /// <summary>
    /// Throws unless every token on the line was claimed by a declaration.
    /// </summary>
    /// <exception cref="ArgumentException">A token is unknown. The message names every one, with the nearest declared flag when there is one.</exception>
    /// <remarks>
    /// Called last, so it sees the whole line. Under <c>--help</c> it returns: an unknown flag is exactly the situation in which someone asks for the
    /// list, and answering with a refusal instead of the list is unhelpful precisely when help was wanted.
    /// </remarks>
    public void RejectUnknown()
    {
        if (_lenient)
        {
            return;
        }

        List<string> unknown = null;
        for (var i = 0; i < _args.Length; i++)
        {
            if (!_used[i])
            {
                (unknown ??= []).Add(_args[i]);
            }
        }

        if (unknown == null)
        {
            return;
        }

        var message = new StringBuilder();
        message.Append(unknown.Count == 1 ? "unknown argument " : "unknown arguments ");
        for (var i = 0; i < unknown.Count; i++)
        {
            if (i > 0)
            {
                message.Append(", ");
            }

            message.Append('"').Append(unknown[i]).Append('"');
            var near = Nearest(unknown[i]);
            if (near != null)
            {
                message.Append(" (did you mean ").Append(near).Append("?)");
            }
        }

        message.Append(". --help lists every flag.");
        throw new ArgumentException(message.ToString());
    }

    /// <summary>The flag list, generated from the declarations this parse made.</summary>
    /// <returns>The text, ready to print.</returns>
    public string Help()
    {
        var width = 0;
        foreach (var e in _declared)
        {
            if (e.Name != null)
            {
                width = Math.Max(width, e.Name.Length + 1 + e.Kind.Length);
            }
        }

        var text = new StringBuilder();
        text.AppendLine("SwgTatooine — a server-side Star Wars Galaxies workload for the Typhon engine.");
        text.AppendLine();
        text.AppendLine("  usage: SwgTatooine [flags]          measure a fixed number of ticks and report");
        text.AppendLine("         SwgTatooine --serve <port>    serve the same world over WebSocket, forever");
        text.AppendLine("         SwgTatooine --sweep           run the partitioning matrix and write a report");
        foreach (var e in _declared)
        {
            if (e.Name == null)
            {
                text.AppendLine();
                text.Append("── ").Append(e.Help).AppendLine(" ──");
                continue;
            }

            var left = e.Kind.Length == 0 ? e.Name : e.Name + " " + e.Kind;
            text.Append("  ").Append(left.PadRight(width)).Append("  ").Append(e.Help);
            if (e.Kind.Length > 0)
            {
                text.Append("  [").Append(e.Default).Append(']');
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Finds a valueless flag and marks it read.</summary>
    /// <param name="name">The flag.</param>
    /// <returns>Its index, or -1.</returns>
    /// <exception cref="ArgumentException">It appears more than once.</exception>
    private int Locate(string name)
    {
        var at = Array.IndexOf(_args, name);
        if (at < 0)
        {
            return -1;
        }

        if (!_lenient && Array.LastIndexOf(_args, name) != at)
        {
            throw new ArgumentException($"'{name}' is given more than once. The first occurrence used to win silently, which made a corrected A/B arm run "
                + "the value it was meant to replace.");
        }

        _used[at] = true;
        return at;
    }

    /// <summary>Finds a flag and marks the flag and its value read.</summary>
    /// <param name="name">The flag.</param>
    /// <param name="kind">How the value is described in <c>--help</c>.</param>
    /// <param name="shownDefault">The default, as <c>--help</c> prints it.</param>
    /// <param name="help">One line, for <c>--help</c>.</param>
    /// <returns>The value token, or null when the flag is absent.</returns>
    /// <exception cref="ArgumentException">The flag is last on the line, or its value is itself a flag.</exception>
    private string Value(string name, string kind, string shownDefault, string help)
    {
        _declared.Add(new Entry(name, kind, shownDefault, help));
        var at = Locate(name);
        if (at < 0)
        {
            return null;
        }

        if (at + 1 >= _args.Length)
        {
            if (_lenient)
            {
                return null;
            }

            throw new ArgumentException($"'{name}' takes {kind} and is the last token on the line.");
        }

        var text = _args[at + 1];

        // A value starting with `--` is a forgotten value, not a value: nothing this program takes looks like that, and a negative number starts with one
        // dash. Accepting it consumed the NEXT flag as this one's value, so two flags went missing for the price of one omission.
        if (text.StartsWith("--", StringComparison.Ordinal))
        {
            if (_lenient)
            {
                return null;
            }

            throw new ArgumentException($"'{name}' takes {kind} but is followed by '{text}', which is a flag. Its value is missing.");
        }

        _used[at + 1] = true;
        return text;
    }

    /// <summary>Reports a value that will not parse.</summary>
    /// <param name="name">The flag.</param>
    /// <param name="text">The token.</param>
    /// <param name="expected">What would have been accepted.</param>
    /// <exception cref="ArgumentException">Unless the line asked for help, in which case the caller's fallback stands.</exception>
    private void Malformed(string name, string text, string expected)
    {
        if (_lenient)
        {
            return;
        }

        throw new ArgumentException($"'{name}' takes {expected}, not '{text}'.");
    }

    /// <summary>The declared flag closest to a token, when one is close enough to be worth suggesting.</summary>
    /// <param name="token">The unknown token.</param>
    /// <returns>The flag, or null.</returns>
    /// <remarks>
    /// Edit distance over a few dozen short names, run once on the way to exiting: the cost is irrelevant and the value is that
    /// <c>--target_ratio</c> names <c>--target-ratio</c> instead of leaving the operator to diff the parser.
    /// </remarks>
    private string Nearest(string token)
    {
        var best = (string)null;
        var bestDistance = int.MaxValue;
        foreach (var e in _declared)
        {
            if (e.Name == null)
            {
                continue;
            }

            var d = Distance(token, e.Name);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = e.Name;
            }
        }

        // A third of the name, so `--hz` does not suggest `--pop` while `--target_ratio` does suggest `--target-ratio`.
        return bestDistance <= Math.Max(1, token.Length / 3) ? best : null;
    }

    /// <summary>Levenshtein distance.</summary>
    /// <param name="a">One string.</param>
    /// <param name="b">The other.</param>
    /// <returns>The number of single-character edits between them.</returns>
    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitute = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitute);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>A number as <c>--help</c> prints it: invariant, and without a trailing <c>.0</c>.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The text.</returns>
    private static string Show(float value) => value.ToString("G", CultureInfo.InvariantCulture);

    /// <summary>A number as <c>--help</c> prints it.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The text.</returns>
    private static string Show(double value) => value.ToString("G", CultureInfo.InvariantCulture);

    /// <summary>A number as <c>--help</c> prints it.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The text.</returns>
    private static string Show(int value) => value.ToString(CultureInfo.InvariantCulture);
}
