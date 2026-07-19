using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Typhon.Generators.Telemetry
{
    /// <summary>
    /// Emits <c>TelemetryConfig.g.cs</c> — the perf projection of the telemetry catalog (Feature #522, T2): one
    /// <c>public static readonly bool</c> gate per flag plus the static constructor that resolves them. Field values
    /// are computed with the SAME primitives the hand-written class used — <see cref="TelemetryConfigResolver"/> for
    /// the parent-implies-children subtrees, plain <c>ReadBool</c> for the master / composite / raw-leaf flags — so
    /// behaviour is identical by construction. Names are fully procedural (path below the prefix, PascalCase).
    /// Engine-build-only: gated on <c>AssemblyName == "Typhon.Engine"</c>; never ships to consumers.
    /// </summary>
    [Generator]
    public sealed class TelemetryConfigGenerator : IIncrementalGenerator
    {
        private static readonly DiagnosticDescriptor NameCollision = new DiagnosticDescriptor(
            id: "TYPH1001",
            title: "Telemetry flag name collision",
            messageFormat: "Two telemetry flags map to the same generated field name '{0}' (paths must be unique after PascalCase folding)",
            category: "TyphonTelemetry",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var catalog = context.AdditionalTextsProvider
                .Where(f => Path.GetFileName(f.Path) == "telemetry-flags.jsonc")
                .Select((f, ct) => f.GetText(ct)?.ToString())
                .Collect();

            var asmName = context.CompilationProvider.Select((c, _) => c.AssemblyName);

            context.RegisterSourceOutput(catalog.Combine(asmName), (spc, pair) =>
            {
                var (texts, assembly) = pair;
                if (assembly != "Typhon.Engine")
                {
                    return;
                }
                var text = texts.FirstOrDefault(t => !string.IsNullOrEmpty(t));
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }
                var cat = TelemetryCatalog.Parse(text);
                if (cat == null || cat.Root == null)
                {
                    return;
                }
                var src = Emit(cat, spc);
                spc.AddSource("TelemetryConfig.g.cs", SourceText.From(src, Encoding.UTF8));
            });
        }

        private static IEnumerable<FlagNode> Descendants(FlagNode n)
        {
            foreach (var c in n.Children)
            {
                yield return c;
                foreach (var d in Descendants(c))
                {
                    yield return d;
                }
            }
        }

        private static string Doc(string s)
        {
            // XML-escape for a one-line <summary>. Descriptions are plain text from the catalog.
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        private string Emit(Catalog cat, SourceProductionContext spc)
        {
            var seen = new HashSet<string>();
            var fields = new StringBuilder();
            var ctor = new StringBuilder();

            void Field(string name, string doc)
            {
                if (!seen.Add(name))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(NameCollision, Location.None, name));
                    return;
                }
                fields.AppendLine("    /// <summary>" + Doc(doc) + "</summary>");
                fields.AppendLine("    public static readonly bool " + name + ";");
                fields.AppendLine();
            }

            var root = cat.Root;
            var master = root.Field; // "ProfilerActive"

            // Master gate.
            Field(master, root.Desc);

            // ── raw-leaf gates (read raw, NOT parent-gated; default may be true) ──
            foreach (var n in Descendants(root).Where(x => x.Kind == "rawLeaf"))
            {
                Field(n.Field, n.Desc);
            }

            // ── composite gates (master's direct opt-in children: Enabled + Active pair) ──
            foreach (var n in root.Children.Where(x => x.Kind == "compositeActive"))
            {
                Field(n.EnabledField, n.Desc);
                Field(n.Field, "Combined gate: true only when " + master + " AND " + n.EnabledField + " are set.");
            }

            // ── subtree gates (parent-implies-children via TelemetryConfigResolver) ──
            foreach (var n in Descendants(root).Where(x => x.Kind == "subtreeResolved"))
            {
                Field(n.Field, n.Desc);
            }

            // ── static constructor ──
            ctor.AppendLine("        var (config, configPath) = BuildConfiguration();");
            ctor.AppendLine("        LoadedConfigurationFile = configPath;");
            ctor.AppendLine("        Configuration = config;");
            ctor.AppendLine("        ProfilerLaunch = ProfilerLaunchConfig.FromConfiguration(config);");
            ctor.AppendLine();
            ctor.AppendLine("        // Master switch: the file/env gate OR any declared profiler output channel implies enabled.");
            ctor.AppendLine("        " + master + " = ReadBool(config, \"" + Esc(root.Key) + "\", false) || ProfilerLaunch.IsActive;");
            ctor.AppendLine();
            ctor.AppendLine("        // Non-gate engine tuning knob read alongside the gates (independent of the gate tree; default 1 ms).");
            ctor.AppendLine("        StoragePageCacheCompletionThresholdMs = ReadInt(config, \"Typhon:Profiler:Storage:PageCache:CompletionThresholdMs\", 1);");
            ctor.AppendLine();

            ctor.AppendLine("        // Raw-leaf gates — read directly, not gated by any parent (intentional firehose opt-outs).");
            foreach (var n in Descendants(root).Where(x => x.Kind == "rawLeaf"))
            {
                ctor.AppendLine("        " + n.Field + " = ReadBool(config, \"" + Esc(n.Key) + "\", " + (n.Default ? "true" : "false") + ");");
            }
            ctor.AppendLine();

            ctor.AppendLine("        // Composite gates — master AND the flag's own Enabled key (default off).");
            foreach (var n in root.Children.Where(x => x.Kind == "compositeActive"))
            {
                ctor.AppendLine("        " + n.EnabledField + " = ReadBool(config, \"" + Esc(n.Key) + "\", false);");
                ctor.AppendLine("        " + n.Field + " = " + master + " && " + n.EnabledField + ";");
            }
            ctor.AppendLine();

            ctor.AppendLine("        // Subtree gates — parent-implies-children resolution per subtree root.");
            foreach (var subRoot in root.Children.Where(x => x.Kind == "subtreeResolved"))
            {
                var treeVar = "__tree" + subRoot.Name;
                var mapVar = "__map" + subRoot.Name;
                ctor.AppendLine("        var " + treeVar + " = " + NodeExpr(subRoot) + ";");
                ctor.AppendLine("        var " + mapVar + " = TelemetryConfigResolver.Resolve(" + treeVar +
                    ", " + master + " && ReadBool(config, \"" + Esc(subRoot.Key) + "\", false), config, \"" + Esc(cat.Prefix) + "\");");
                // assign root + every subtreeResolved descendant from the map
                var nodes = new List<FlagNode> { subRoot };
                nodes.AddRange(Descendants(subRoot).Where(x => x.Kind == "subtreeResolved"));
                foreach (var node in nodes)
                {
                    ctor.AppendLine("        " + node.Field + " = " + mapVar + "[\"" + Esc(node.Path) + "\"];");
                }
                ctor.AppendLine();
            }

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("// GENERATED by Typhon.Generators.Telemetry.TelemetryConfigGenerator from telemetry-flags.jsonc — do not edit.");
            sb.AppendLine("#nullable disable");
            sb.AppendLine();
            sb.AppendLine("namespace Typhon.Engine");
            sb.AppendLine("{");
            sb.AppendLine("    public static partial class TelemetryConfig");
            sb.AppendLine("    {");
            sb.Append(Indent(fields.ToString()));
            sb.AppendLine("        static TelemetryConfig()");
            sb.AppendLine("        {");
            sb.Append(ctor.ToString());
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string NodeExpr(FlagNode n)
        {
            var kids = n.Children.Where(c => c.Kind == "subtreeResolved" || (c.Kind == "group" && c.HasResolvedDescendant())).ToList();
            if (kids.Count == 0)
            {
                return "new Node(\"" + n.Name + "\")";
            }
            return "new Node(\"" + n.Name + "\", new Node[] { " + string.Join(", ", kids.Select(NodeExpr)) + " })";
        }

        private static string Indent(string block)
        {
            // fields are emitted with a 4-space indent already; keep as-is.
            return block;
        }
    }
}
