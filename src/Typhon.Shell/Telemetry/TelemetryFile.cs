using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Typhon.Engine;

namespace Typhon.Shell.Telemetry;

/// <summary>
/// In-memory model of a <c>typhon.telemetry.json</c> file: the set of EXPLICIT gate keys the user has set,
/// keyed by catalog path (segments below <c>Typhon:Profiler</c>; <c>""</c> = the master). Loads and writes
/// minimal JSONC (only explicit keys), and resolves the effective state through the same parent-implies-children
/// semantics as the engine's <c>TelemetryConfigResolver</c> — including the three intentional exceptions
/// (un-gated default-true gauges; composite/subtree roots default off). Feature #522 / T5.
/// </summary>
internal sealed class TelemetryFile
{
    public const string DefaultFileName = "typhon.telemetry.json";

    public string Path { get; }

    // path-below-prefix ("" = master) -> explicit bool
    private readonly Dictionary<string, bool> _explicit;
    // Typhon:Profiler:Trace — a string output path, not a gate flag; null when unset.
    private string _tracePath;

    // Typhon:Profiler:Live / :LiveWaitMs — the live TCP channel. Scalars like Trace, not gate flags.
    private int? _livePort;
    private int? _liveWaitMs;

    private TelemetryFile(string path, Dictionary<string, bool> ov, string tracePath, int? livePort, int? liveWaitMs)
    {
        Path = path;
        _explicit = ov;
        _tracePath = tracePath;
        _livePort = livePort;
        _liveWaitMs = liveWaitMs;
    }

    public IReadOnlyDictionary<string, bool> Explicit => _explicit;

    /// <summary>The explicit <c>Typhon:Profiler:Trace</c> output-file path, or <c>null</c> when unset. Declaring it
    /// activates profiling even without the master <c>Enabled</c> flag (an output channel is what makes the profiler live).</summary>
    public string TracePath => _tracePath;

    /// <summary>Set the profiler trace output path (<c>Typhon:Profiler:Trace</c>).</summary>
    public void SetTrace(string path) => _tracePath = path;

    /// <summary>Remove the profiler trace output path.</summary>
    public void ClearTrace() => _tracePath = null;

    /// <summary>
    /// The explicit <c>Typhon:Profiler:Live</c> TCP port, or <c>null</c> when unset. Like <see cref="TracePath"/>,
    /// declaring it activates profiling on its own — an output channel is what makes the profiler live.
    /// </summary>
    /// <remarks>
    /// Since #805 this is not merely a launch detail: the engine publishes it in the database's <c>db.lock</c>, so the
    /// Workbench opening a bundle its application holds can discover where to watch without being told. That makes the
    /// port a contract between two processes, which is why it belongs in the tool that writes this file rather than in
    /// hand-edited JSON.
    /// </remarks>
    public int? LivePort => _livePort;

    /// <summary>
    /// The explicit <c>Typhon:Profiler:LiveWaitMs</c> — how long startup blocks waiting for the first viewer to
    /// connect, or <c>null</c> when unset. Meaningless without <see cref="LivePort"/>.
    /// </summary>
    public int? LiveWaitMs => _liveWaitMs;

    /// <summary>Set the live TCP port (<c>Typhon:Profiler:Live</c>).</summary>
    public void SetLive(int port, int? waitMs = null)
    {
        _livePort = port;
        if (waitMs.HasValue)
        {
            _liveWaitMs = waitMs;
        }
    }

    /// <summary>Remove the live channel, including its wait — a wait without a port configures nothing.</summary>
    public void ClearLive()
    {
        _livePort = null;
        _liveWaitMs = null;
    }

    /// <summary>Load the file (or an empty model if it does not exist).</summary>
    public static TelemetryFile Load(string path)
    {
        var ov = new Dictionary<string, bool>(StringComparer.Ordinal);
        string tracePath = null;
        int? livePort = null;
        int? liveWaitMs = null;
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.TryGetProperty("Typhon", out var typhon) &&
                typhon.TryGetProperty("Profiler", out var profiler))
            {
                Collect(profiler, "", ov);
                if (profiler.TryGetProperty("Trace", out var traceEl) && traceEl.ValueKind == JsonValueKind.String)
                {
                    tracePath = traceEl.GetString();
                }
                // Accept a number or a numeric string: the configuration binder stringifies either, and a file written
                // by hand is as likely to quote the port as not. Anything that is not a number is left unset rather
                // than coerced — ProfilerLaunchConfig would silently read a junk value as the default port, and a
                // config tool that quietly rewrites nonsense into 9100 is worse than one that leaves it alone.
                livePort = ReadInt(profiler, "Live");
                liveWaitMs = ReadInt(profiler, "LiveWaitMs");
            }
        }
        return new TelemetryFile(path, ov, tracePath, livePort, liveWaitMs);
    }

    /// <summary>Reads a scalar int property written either as a JSON number or as a numeric string; null otherwise.</summary>
    private static int? ReadInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el))
        {
            return null;
        }
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
        {
            return n;
        }
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s))
        {
            return s;
        }
        return null;
    }

    private static void Collect(JsonElement node, string path, Dictionary<string, bool> ov)
    {
        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Name == "Enabled")
            {
                if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                {
                    ov[path] = prop.Value.GetBoolean();
                }
            }
            else if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                Collect(prop.Value, path.Length == 0 ? prop.Name : path + ":" + prop.Name, ov);
            }
        }
    }

    public bool TryGetExplicit(string path, out bool value) => _explicit.TryGetValue(path, out value);

    public void Set(string path, bool value) => _explicit[path] = value;

    public void Reset(string path) => _explicit.Remove(path);

    /// <summary>Resolve the effective (what-would-actually-emit) state of every catalog node, by catalog index.</summary>
    public bool[] ResolveEffective()
    {
        var all = TelemetryFlagCatalog.All;
        var eff = new bool[all.Count];
        for (int i = 0; i < all.Count; i++)
        {
            var d = all[i];
            bool? ex = _explicit.TryGetValue(d.Path, out var v) ? v : (bool?)null;
            bool parentEff = d.ParentIndex >= 0 && eff[d.ParentIndex];
            switch (d.Kind)
            {
                case TelemetryFlagKind.Master:
                    eff[i] = ex ?? false; // (file view ignores ProfilerLaunch output-channel activation)
                    break;
                case TelemetryFlagKind.RawLeaf:
                    eff[i] = ex ?? d.Default; // un-gated: independent of parent
                    break;
                case TelemetryFlagKind.CompositeActive:
                    eff[i] = eff[0] && (ex ?? false); // master AND own (default off)
                    break;
                case TelemetryFlagKind.SubtreeResolved:
                    eff[i] = all[d.ParentIndex].Kind == TelemetryFlagKind.Master
                        ? eff[0] && (ex ?? false)      // subtree root: explicit opt-in, default off
                        : parentEff && (ex ?? true);   // descendant: inherit-true
                    break;
                default: // Group — resolver intermediate: inherit-true
                    eff[i] = (d.ParentIndex < 0 || parentEff) && (ex ?? true);
                    break;
            }
        }
        return eff;
    }

    /// <summary>Write the model back as minimal JSONC — only explicit keys, with a description comment on each enabled flag.</summary>
    public void Save()
    {
        var root = new EmitNode();
        var descByPath = TelemetryFlagCatalog.All.ToDictionary(d => d.Path, d => d.Description);
        foreach (var kv in _explicit)
        {
            var node = root;
            if (kv.Key.Length > 0)
            {
                foreach (var seg in kv.Key.Split(':'))
                {
                    node = node.Child(seg);
                }
            }
            node.HasValue = true;
            node.Value = kv.Value;
            node.Desc = descByPath.TryGetValue(kv.Key, out var ds) ? ds : null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("// typhon.telemetry.json — written by `typhon telemetry`. Only explicit flags are listed;");
        sb.AppendLine("// everything else inherits (see the telemetry flags reference). Env vars override this file.");
        sb.AppendLine("{");
        sb.AppendLine("  \"Typhon\": {");
        EmitChildren(root, sb, 2, "Profiler", new OutputChannels(_tracePath, _livePort, _liveWaitMs));
        sb.AppendLine("  }");
        sb.AppendLine("}");
        File.WriteAllText(Path, sb.ToString());
    }

    /// <summary>
    /// The profiler's output channels — scalars rather than gate flags, so they are emitted ahead of the flag tree
    /// rather than within it. Grouped because there are now three: threading them as separate parameters through the
    /// emitter made the signature say less each time one was added.
    /// </summary>
    private readonly record struct OutputChannels(string TracePath, int? LivePort, int? LiveWaitMs)
    {
        public bool Any => !string.IsNullOrEmpty(TracePath) || LivePort.HasValue || LiveWaitMs.HasValue;
    }

    private static void EmitChildren(EmitNode profilerContent, StringBuilder sb, int indent, string wrapName, OutputChannels channels)
    {
        var pad = new string(' ', indent * 2);
        sb.AppendLine(pad + "\"" + wrapName + "\": {");
        EmitBody(profilerContent, sb, indent + 1, channels);
        sb.AppendLine(pad + "}");
    }

    private static void EmitBody(EmitNode node, StringBuilder sb, int indent, OutputChannels channels = default)
    {
        var pad = new string(' ', indent * 2);
        var kids = node.Children.OrderBy(k => k.Key, StringComparer.Ordinal).ToList();

        // Output channels (strings and numbers, not gate flags) lead the Profiler body when set. A trailing comma
        // follows each only if something else comes after it — an Enabled flag, a child subtree, or another channel.
        if (channels.Any)
        {
            var tail = node.HasValue || kids.Count > 0;
            if (!string.IsNullOrEmpty(channels.TracePath))
            {
                var following = tail || channels.LivePort.HasValue || channels.LiveWaitMs.HasValue;
                sb.AppendLine(pad + "// Profiler trace output file — declaring it activates profiling.");
                sb.AppendLine(pad + "\"Trace\": " + JsonSerializer.Serialize(channels.TracePath) + (following ? "," : ""));
            }
            if (channels.LivePort.HasValue)
            {
                var following = tail || channels.LiveWaitMs.HasValue;
                sb.AppendLine(pad + "// Live TCP port for the Workbench to attach to — also published in the database's db.lock,");
                sb.AppendLine(pad + "// so opening that database while this app holds it can offer to watch it.");
                sb.AppendLine(pad + "\"Live\": " + channels.LivePort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + (following ? "," : ""));
            }
            if (channels.LiveWaitMs.HasValue)
            {
                sb.AppendLine(pad + "// Block startup up to this many ms waiting for the first viewer to connect.");
                sb.AppendLine(pad + "\"LiveWaitMs\": " + channels.LiveWaitMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + (tail ? "," : ""));
            }
        }

        if (node.HasValue)
        {
            if (node.Value && !string.IsNullOrEmpty(node.Desc))
            {
                sb.AppendLine(pad + "// " + node.Desc);
            }
            sb.AppendLine(pad + "\"Enabled\": " + (node.Value ? "true" : "false") + (kids.Count > 0 ? "," : ""));
        }
        for (int i = 0; i < kids.Count; i++)
        {
            var last = i == kids.Count - 1;
            sb.AppendLine(pad + "\"" + kids[i].Key + "\": {");
            EmitBody(kids[i].Value, sb, indent + 1);
            sb.AppendLine(pad + "}" + (last ? "" : ","));
        }
    }

    private sealed class EmitNode
    {
        public bool HasValue;
        public bool Value;
        public string Desc;
        public readonly SortedDictionary<string, EmitNode> Children = new SortedDictionary<string, EmitNode>(StringComparer.Ordinal);
        public EmitNode Child(string name)
        {
            if (!Children.TryGetValue(name, out var c))
            {
                c = new EmitNode();
                Children[name] = c;
            }
            return c;
        }
    }
}
