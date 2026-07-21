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

    private TelemetryFile(string path, Dictionary<string, bool> ov, string tracePath)
    {
        Path = path;
        _explicit = ov;
        _tracePath = tracePath;
    }

    public IReadOnlyDictionary<string, bool> Explicit => _explicit;

    /// <summary>The explicit <c>Typhon:Profiler:Trace</c> output-file path, or <c>null</c> when unset. Declaring it
    /// activates profiling even without the master <c>Enabled</c> flag (an output channel is what makes the profiler live).</summary>
    public string TracePath => _tracePath;

    /// <summary>Set the profiler trace output path (<c>Typhon:Profiler:Trace</c>).</summary>
    public void SetTrace(string path) => _tracePath = path;

    /// <summary>Remove the profiler trace output path.</summary>
    public void ClearTrace() => _tracePath = null;

    /// <summary>Load the file (or an empty model if it does not exist).</summary>
    public static TelemetryFile Load(string path)
    {
        var ov = new Dictionary<string, bool>(StringComparer.Ordinal);
        string tracePath = null;
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
            }
        }
        return new TelemetryFile(path, ov, tracePath);
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
        EmitChildren(root, sb, 2, "Profiler", _tracePath);
        sb.AppendLine("  }");
        sb.AppendLine("}");
        File.WriteAllText(Path, sb.ToString());
    }

    private static void EmitChildren(EmitNode profilerContent, StringBuilder sb, int indent, string wrapName, string tracePath)
    {
        var pad = new string(' ', indent * 2);
        sb.AppendLine(pad + "\"" + wrapName + "\": {");
        EmitBody(profilerContent, sb, indent + 1, tracePath);
        sb.AppendLine(pad + "}");
    }

    private static void EmitBody(EmitNode node, StringBuilder sb, int indent, string tracePath = null)
    {
        var pad = new string(' ', indent * 2);
        var kids = node.Children.OrderBy(k => k.Key, StringComparer.Ordinal).ToList();

        // The trace output path (a string, not a gate flag) leads the Profiler body when set. A trailing comma
        // follows only if an Enabled flag or a child subtree comes after it.
        if (!string.IsNullOrEmpty(tracePath))
        {
            var following = node.HasValue || kids.Count > 0;
            sb.AppendLine(pad + "// Profiler trace output file — declaring it activates profiling.");
            sb.AppendLine(pad + "\"Trace\": " + JsonSerializer.Serialize(tracePath) + (following ? "," : ""));
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
