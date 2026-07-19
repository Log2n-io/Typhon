using System.Collections.Generic;
using System.Text;

namespace Typhon.Generators.Telemetry
{
    /// <summary>
    /// One node of the telemetry flag catalog (<c>telemetry-flags.jsonc</c>). The catalog is the single source
    /// of truth for Typhon's telemetry gate flags (Feature #522); both <see cref="TelemetryConfigGenerator"/>
    /// (perf projection) and <see cref="TelemetryFlagCatalogGenerator"/> (runtime tree) are emitted from it.
    /// </summary>
    internal sealed class FlagNode
    {
        public string Name;
        /// <summary>master | compositeActive | rawLeaf | subtreeResolved | group.</summary>
        public string Kind;
        public bool Default;
        public string Desc;
        /// <summary>Exact C# field name to emit (the resolved <c>*Active</c> gate). Null for group nodes.</summary>
        public string Field;
        /// <summary>Composite raw <c>*Enabled</c> field name (compositeActive only); null otherwise.</summary>
        public string EnabledField;
        public readonly List<FlagNode> Children = new List<FlagNode>();

        // ── computed during Finalize ──────────────────────────────────────────────
        public FlagNode Parent;
        /// <summary>Config-path segments below the prefix, colon-joined (e.g. "Concurrency:AccessControl:Contention"); "" for the root.</summary>
        public string Path;
        /// <summary>Full config key (e.g. "Typhon:Profiler:Concurrency:AccessControl:Contention:Enabled").</summary>
        public string Key;

        public bool IsKeyed => Kind != "group";

        /// <summary>True if this node or any descendant is subtreeResolved (i.e. participates in the resolver tree).</summary>
        public bool HasResolvedDescendant()
        {
            foreach (var c in Children)
            {
                if (c.Kind == "subtreeResolved" || c.HasResolvedDescendant())
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Parsed catalog: the JSON prefix (e.g. "Typhon:Profiler") plus the root node.</summary>
    internal sealed class Catalog
    {
        public string Prefix;
        public FlagNode Root;

        /// <summary>Depth-first enumeration (root first, then children in order).</summary>
        public IEnumerable<FlagNode> All()
        {
            return Walk(Root);
        }

        private static IEnumerable<FlagNode> Walk(FlagNode n)
        {
            yield return n;
            foreach (var c in n.Children)
            {
                foreach (var d in Walk(c))
                {
                    yield return d;
                }
            }
        }
    }

    /// <summary>
    /// Minimal, dependency-free JSONC reader for the telemetry catalog. Hand-rolled (rather than System.Text.Json)
    /// so the generator carries no extra assemblies into the compiler's analyzer load context. Handles objects,
    /// arrays, strings, <c>true</c>/<c>false</c>, <c>//</c> line comments and trailing commas — the exact subset the
    /// catalog uses.
    /// </summary>
    internal static class TelemetryCatalog
    {
        public static Catalog Parse(string text)
        {
            var json = StripComments(text);
            int pos = 0;
            var rootObj = ParseValue(json, ref pos) as Dictionary<string, object>;
            if (rootObj == null)
            {
                return null;
            }

            var cat = new Catalog
            {
                Prefix = rootObj.TryGetValue("prefix", out var p) ? p as string : "Typhon:Profiler",
                Root = ToNode(rootObj.TryGetValue("root", out var r) ? r as Dictionary<string, object> : null),
            };
            if (cat.Root == null)
            {
                return null;
            }
            Finalize(cat.Root, null, cat.Prefix, "");
            return cat;
        }

        private static FlagNode ToNode(Dictionary<string, object> o)
        {
            if (o == null)
            {
                return null;
            }
            var n = new FlagNode
            {
                Name = o.TryGetValue("name", out var nm) ? nm as string : null,
                Kind = o.TryGetValue("kind", out var k) ? k as string : "group",
                Default = o.TryGetValue("default", out var d) && d is bool b && b,
                Desc = o.TryGetValue("desc", out var ds) ? ds as string : null,
                Field = o.TryGetValue("field", out var fld) ? fld as string : null,
                EnabledField = o.TryGetValue("enabledField", out var ef) ? ef as string : null,
            };
            if (o.TryGetValue("children", out var ch) && ch is List<object> list)
            {
                foreach (var item in list)
                {
                    var childNode = ToNode(item as Dictionary<string, object>);
                    if (childNode != null)
                    {
                        n.Children.Add(childNode);
                    }
                }
            }
            return n;
        }

        private static void Finalize(FlagNode n, FlagNode parent, string prefix, string path)
        {
            n.Parent = parent;
            n.Path = path;
            n.Key = path.Length == 0 ? prefix + ":Enabled" : prefix + ":" + path + ":Enabled";
            foreach (var c in n.Children)
            {
                var childPath = path.Length == 0 ? c.Name : path + ":" + c.Name;
                Finalize(c, n, prefix, childPath);
            }
        }

        // ── comment stripping (respects string literals) ──────────────────────────
        private static string StripComments(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool inStr = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < s.Length)
                    {
                        sb.Append(s[++i]);
                    }
                    else if (c == '"')
                    {
                        inStr = false;
                    }
                    continue;
                }
                if (c == '"')
                {
                    inStr = true;
                    sb.Append(c);
                }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n')
                    {
                        i++;
                    }
                    if (i < s.Length)
                    {
                        sb.Append('\n');
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        // ── tiny recursive-descent JSON value parser (trailing-comma tolerant) ────
        private static object ParseValue(string s, ref int pos)
        {
            SkipWs(s, ref pos);
            char c = s[pos];
            if (c == '{')
            {
                return ParseObject(s, ref pos);
            }
            if (c == '[')
            {
                return ParseArray(s, ref pos);
            }
            if (c == '"')
            {
                return ParseString(s, ref pos);
            }
            if (c == 't' || c == 'f')
            {
                return ParseBool(s, ref pos);
            }
            // null or number — not used by the catalog; skip a token
            return ParseBareToken(s, ref pos);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int pos)
        {
            var o = new Dictionary<string, object>();
            pos++; // {
            SkipWs(s, ref pos);
            while (s[pos] != '}')
            {
                var key = ParseString(s, ref pos);
                SkipWs(s, ref pos);
                pos++; // :
                var val = ParseValue(s, ref pos);
                o[key] = val;
                SkipWs(s, ref pos);
                if (s[pos] == ',')
                {
                    pos++;
                    SkipWs(s, ref pos);
                }
            }
            pos++; // }
            return o;
        }

        private static List<object> ParseArray(string s, ref int pos)
        {
            var a = new List<object>();
            pos++; // [
            SkipWs(s, ref pos);
            while (s[pos] != ']')
            {
                a.Add(ParseValue(s, ref pos));
                SkipWs(s, ref pos);
                if (s[pos] == ',')
                {
                    pos++;
                    SkipWs(s, ref pos);
                }
            }
            pos++; // ]
            return a;
        }

        private static string ParseString(string s, ref int pos)
        {
            var sb = new StringBuilder();
            pos++; // opening quote
            while (s[pos] != '"')
            {
                char c = s[pos++];
                if (c == '\\')
                {
                    char e = s[pos++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        default: sb.Append(e); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            pos++; // closing quote
            return sb.ToString();
        }

        private static bool ParseBool(string s, ref int pos)
        {
            if (s[pos] == 't')
            {
                pos += 4;
                return true;
            }
            pos += 5;
            return false;
        }

        private static object ParseBareToken(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && s[pos] != ',' && s[pos] != '}' && s[pos] != ']' && !char.IsWhiteSpace(s[pos]))
            {
                pos++;
            }
            return s.Substring(start, pos - start);
        }

        private static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos]))
            {
                pos++;
            }
        }
    }
}
