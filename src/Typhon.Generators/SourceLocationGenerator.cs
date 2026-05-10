using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Typhon.Generators;

/// <summary>
/// Source generator that attributes every <c>TyphonEvent.BeginXxx(...)</c> call site to a deterministic <c>ushort</c> id and emits per-call-site
/// <c>[InterceptsLocation]</c> wrappers that forward to the matching <c>BeginXxxWithSiteId</c> overload, baking the literal id into the IL.
///
/// Design: see `claude/design/observability/10-profiler-source-attribution.md`.
///
/// Output:
///   - One generated source unit holding all interceptor methods (file-local class).
///   - One generated source unit holding the static <c>SourceLocations</c> table (id → file/line/method/kind).
///
/// Determinism: discovered call sites are sorted by <c>(filePath, line, column)</c> before id assignment, so every build of the same source produces a
/// byte-identical generated table.
///
/// Scope: factories that have a corresponding <c>BeginXxxWithSiteId</c> overload on <c>TyphonEvent</c> are intercepted. Factories without that overload are
/// left alone (they fall through to the default zero siteId pass-through path); a build info diagnostic is emitted with attributed/skipped counts so a
/// regression in coverage is loud.
/// </summary>
[Generator(LanguageNames.CSharp)]
public class SourceLocationGenerator : IIncrementalGenerator
{
    private const string TyphonEventFqn = "Typhon.Engine.Internals.TyphonEvent";
    private const string GeneratedNamespace = "Typhon.Generators.Generated";
    private const string TraceEventAttributeFqn = "Typhon.Engine.Profiler.TraceEventAttribute";
    private const string BeginParamAttributeFqn = "Typhon.Engine.Profiler.BeginParamAttribute";
    
    /// <summary>
    /// Per design Q3 (claude/design/observability/10-profiler-source-attribution.md §4.1): the generator's interceptor scope is Typhon.Engine *only*.
    /// Other consumers (tests, tools) pay through siteId=0 ("unknown source"). The SourceLocations table is also engine-only; tests can read it via the
    /// engine's assembly.
    /// </summary>
    private const string TargetAssemblyName = "Typhon.Engine";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Repo root from MSBuild — used to rewrite absolute paths to "/_/..." form (matches the design's
        // PathMap convention; the C# compiler's PathMap doesn't affect SyntaxTree.FilePath, so we do it here).
        var repoRoot = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) => 
            provider.GlobalOptions.TryGetValue("build_property.TyphonRepoRoot", out var v) ? v : "");

        // v2 (#302) — factory metadata pipeline.
        //
        // Roslyn's parallel generator pipeline doesn't expose other generators' emitted methods to a generator's SemanticModel. So when SourceLocationGenerator
        // runs against TyphonEvent.BeginXxx call sites, the GetSymbolInfo on the invocation returns null (TraceEventGenerator's BeginXxx + BeginXxx_WithSiteId
        // factories aren't visible yet). To recover the factory signatures (return type + parameter types) we duplicate TraceEventGenerator's discovery:
        // enumerate every [TraceEvent]-attributed partial struct, extract the kindName/factoryName + the [BeginParam] field types, and build a map keyed by
        // factory name. The map gives the interceptor emission step the exact signature it needs.
        //
        // Hand-written factories (with visible symbols on TyphonEvent) are picked up too — the symbol-based path stays as a fallback for those.
        var factoryMap = context.SyntaxProvider.ForAttributeWithMetadataName(
            TraceEventAttributeFqn, static (node, _) => node is StructDeclarationSyntax, ExtractFactoryMetadata
        ).Where(static f => f != null).Collect();

        // Discover candidate invocations: any TyphonEvent.BeginXxx(...) call. Returns the call-site syntax info
        // without inferring parameter types (those come from the factory map, joined later).
        var candidates = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => IsCandidateInvocation(node), TryExtractCallSiteRaw
        ).Where(static c => c != null).Collect();

        var assemblyName = context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName ?? "");

        var combined = candidates.Combine(factoryMap).Combine(assemblyName).Combine(repoRoot);

        context.RegisterSourceOutput(combined, static (spc, input) =>
        {
            var (((sites, factories), asmName), repoRootValue) = input;
            if (asmName != TargetAssemblyName)
            {
                return;
            }
            EmitOutputs(spc, sites, factories, repoRootValue);
        });
    }

    // ───────────────────────────────────────────────────────────────────────
    // Factory metadata: enumerate [TraceEvent] partial structs and capture
    // factory name + parameter list (from the struct's [BeginParam] fields).
    // ───────────────────────────────────────────────────────────────────────

    private sealed class FactoryInfo
    {
        public string FactoryName { get; }
        public string ReturnTypeFqn { get; }
        public ImmutableArray<ParamInfo> Parameters { get; }
        public FactoryInfo(string factoryName, string returnTypeFqn, ImmutableArray<ParamInfo> parameters)
        {
            FactoryName = factoryName;
            ReturnTypeFqn = returnTypeFqn;
            Parameters = parameters;
        }
    }

    private static FactoryInfo ExtractFactoryMetadata(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol structSymbol)
        {
            return null;
        }
        var attr = ctx.Attributes.FirstOrDefault();
        if (attr == null || attr.ConstructorArguments.Length < 1)
        {
            return null;
        }

        // Resolve kind name from the enum constant — used to derive the default factory name (Begin + kindName).
        var kindArg = attr.ConstructorArguments[0];
        string kindName = null;
        if (kindArg.Type is INamedTypeSymbol enumType && enumType.TypeKind == TypeKind.Enum && kindArg.Value != null)
        {
            long ordinal = System.Convert.ToInt64(kindArg.Value);
            foreach (var m in enumType.GetMembers())
            {
                if (m is IFieldSymbol fs && fs.IsConst && fs.HasConstantValue && fs.ConstantValue != null
                    && System.Convert.ToInt64(fs.ConstantValue) == ordinal)
                {
                    kindName = fs.Name;
                    break;
                }
            }
        }
        if (kindName == null)
        {
            return null;
        }

        // Named args: FactoryName override, GenerateFactory off-switch.
        string factoryName = "Begin" + kindName;
        bool generateFactory = true;
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == "FactoryName" && named.Value.Value is string fn && !string.IsNullOrEmpty(fn))
            {
                factoryName = fn;
            }
            else if (named.Key == "GenerateFactory" && named.Value.Value is bool gf)
            {
                generateFactory = gf;
            }
        }
        if (!generateFactory)
        {
            return null;
        }

        // Walk fields to find [BeginParam]-attributed ones — these are the factory's parameter list.
        var paramsBuilder = ImmutableArray.CreateBuilder<ParamInfo>();
        foreach (var member in structSymbol.GetMembers())
        {
            if (member is not IFieldSymbol field)
            {
                continue;
            }
            AttributeData beginParamAttr = null;
            foreach (var a in field.GetAttributes())
            {
                if (a.AttributeClass?.ToDisplayString() == BeginParamAttributeFqn)
                {
                    beginParamAttr = a;
                    break;
                }
            }
            if (beginParamAttr == null)
            {
                continue;
            }
            // Per [BeginParam(ParamType = ...)] override or default to field's declared type.
            string paramTypeFqn = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            foreach (var na in beginParamAttr.NamedArguments)
            {
                if (na.Key == "ParamType" && na.Value.Value is string ptOverride && !string.IsNullOrEmpty(ptOverride))
                {
                    // Resolve unqualified override names to a fully-qualified form via the compilation —
                    // the interceptor file is `file static class` and doesn't have user usings, so unqualified
                    // type names won't compile. Try the user-provided name first; if not found, leave as-is.
                    var resolved = ctx.SemanticModel.Compilation.GetTypeByMetadataName(ptOverride);
                    if (resolved == null && !ptOverride.Contains('.'))
                    {
                        // Common case: short name in Typhon.Profiler. Try that namespace before giving up.
                        resolved = ctx.SemanticModel.Compilation.GetTypeByMetadataName("Typhon.Profiler." + ptOverride);
                    }
                    paramTypeFqn = resolved?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? ptOverride;
                }
            }
            // Match TraceEventGenerator's parameter-name derivation: lowercase first char.
            string paramName = field.Name.Length > 0 ? char.ToLowerInvariant(field.Name[0]) + field.Name.Substring(1) : field.Name;
            paramsBuilder.Add(new ParamInfo(paramTypeFqn, paramName));
        }

        var returnTypeFqn = "global::" + structSymbol.ContainingNamespace.ToDisplayString() + "." + structSymbol.Name;
        return new FactoryInfo(factoryName, returnTypeFqn, paramsBuilder.ToImmutable());
    }

    // ───────────────────────────────────────────────────────────────────────
    // Discovery
    // ───────────────────────────────────────────────────────────────────────

    private static bool IsCandidateInvocation(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax invocation)
        {
            return false;
        }
        // Cheap textual prefilter — `TyphonEvent.Begin*(...)` appears as MemberAccess `<expr>.BeginXxx`.
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }
        return memberAccess.Name.Identifier.Text.StartsWith("Begin", StringComparison.Ordinal);
    }

    private sealed class CallSite
    {
        public InterceptableLocation Location { get; }
        public string BaseName { get; }
        public string ReturnTypeFqn { get; }
        public ImmutableArray<ParamInfo> Parameters { get; }
        public string FilePath { get; }
        public int Line { get; }
        public int Column { get; }
        public string MethodName { get; }

        public CallSite(
            InterceptableLocation location,
            string baseName,
            string returnTypeFqn,
            ImmutableArray<ParamInfo> parameters,
            string filePath,
            int line,
            int column,
            string methodName)
        {
            Location = location;
            BaseName = baseName;
            ReturnTypeFqn = returnTypeFqn;
            Parameters = parameters;
            FilePath = filePath;
            Line = line;
            Column = column;
            MethodName = methodName;
        }
    }

    private readonly struct ParamInfo
    {
        public string TypeFqn { get; }
        public string Name { get; }
        public ParamInfo(string typeFqn, string name)
        {
            TypeFqn = typeFqn;
            Name = name;
        }
    }

    /// <summary>
    /// v2 (#302): syntax-only call-site extraction. The factory's actual signature (return type + parameter
    /// types) is supplied by the factory map joined later — see the Initialize pipeline. Don't depend on
    /// GetSymbolInfo here because TraceEventGenerator's emitted methods may not be visible yet (parallel-
    /// generator design).
    /// </summary>
    private static CallSite TryExtractCallSiteRaw(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        // Receiver check — accept "TyphonEvent" or fully-qualified forms (legacy + post-namespace-migration).
        var receiverText = memberAccess.Expression.ToString();
        if (receiverText != "TyphonEvent"
            && receiverText != "Typhon.Engine.Profiler.TyphonEvent"
            && receiverText != "global::Typhon.Engine.Profiler.TyphonEvent"
            && receiverText != "Typhon.Engine.Internals.TyphonEvent"
            && receiverText != "global::Typhon.Engine.Internals.TyphonEvent")
        {
            return null;
        }

        var methodNameText = memberAccess.Name.Identifier.Text;
        if (!methodNameText.StartsWith("Begin", StringComparison.Ordinal))
        {
            return null;
        }
        if (methodNameText.EndsWith("_WithSiteId", StringComparison.Ordinal))
        {
            return null;
        }
        // Hand-written internal emitters (private TLS-stash pattern, not user-callable factories) — they
        // pay siteId = 0 by design. Detected by syntactic naming convention.
        if (methodNameText.EndsWith("Tls", StringComparison.Ordinal))
        {
            return null;
        }

        var location = ctx.SemanticModel.GetInterceptableLocation(invocation, ct);
        if (location == null)
        {
            return null;
        }

        var enclosingMethod = invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        var methodName = enclosingMethod?.Identifier.Text ?? "<unknown>";

        var span = invocation.GetLocation().GetLineSpan();

        return new CallSite(
            location,
            methodNameText,
            "", // filled in by EmitOutputs from the factory map
            ImmutableArray<ParamInfo>.Empty, // ditto
            span.Path ?? "<unknown>",
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            methodName);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Emit
    // ───────────────────────────────────────────────────────────────────────

    private static void EmitOutputs(SourceProductionContext spc, ImmutableArray<CallSite> sites, ImmutableArray<FactoryInfo> factories, string repoRoot)
    {
        if (sites.IsDefaultOrEmpty)
        {
            return;
        }

        // Build a lookup: factory name → FactoryInfo. The factory metadata comes from enumerating
        // [TraceEvent] partial structs (a separate pipeline step) — its content is independent of generator
        // ordering, so we always have correct signatures even when TraceEventGenerator hasn't run yet.
        var factoryByName = new Dictionary<string, FactoryInfo>(StringComparer.Ordinal);
        if (!factories.IsDefaultOrEmpty)
        {
            foreach (var f in factories)
            {
                if (f != null)
                {
                    factoryByName[f.FactoryName] = f;
                }
            }
        }

        var attributable = new List<CallSite>(sites.Length);
        var skippedCount = 0;
        foreach (var site in sites)
        {
            if (site == null)
            {
                continue;
            }
            // Look up the factory metadata; skip if not found (likely a hand-written factory we don't know about).
            // Skipping is safe — the call site falls through to siteId = 0.
            if (!factoryByName.TryGetValue(site.BaseName, out var factory))
            {
                skippedCount++;
                continue;
            }
            // Replace the empty signature with the factory's actual signature.
            var enriched = new CallSite(site.Location, site.BaseName, factory.ReturnTypeFqn, factory.Parameters, site.FilePath, site.Line, site.Column, site.MethodName);
            attributable.Add(NormalizeFilePath(enriched, repoRoot));
        }

        // Deterministic order: sort by (filePath, line, column).
        attributable.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.FilePath, b.FilePath);
            if (c != 0) return c;
            c = a.Line.CompareTo(b.Line);
            if (c != 0) return c;
            return a.Column.CompareTo(b.Column);
        });

        // Cap enforcement: ushort max is 65535, id 0 is reserved for "unknown source".
        if (attributable.Count > 65535)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                new DiagnosticDescriptor(
                    "TPH9002",
                    "Too many TyphonEvent emission sites",
                    $"SourceLocationGenerator: {attributable.Count} sites exceed the 65535 ushort cap; id width must be widened.",
                    "Typhon.Profiler",
                    DiagnosticSeverity.Error,
                    true),
                Location.None));
            return;
        }

        // Emit interceptor file.
        var interceptorSource = BuildInterceptorSource(attributable);
        spc.AddSource("SourceLocations.Interceptors.g.cs", interceptorSource);

        // Emit static SourceLocations table.
        var tableSource = BuildSourceLocationsTable(attributable);
        spc.AddSource("SourceLocations.g.cs", tableSource);

        // Build-time summary diagnostic — loud if a regression cuts attribution coverage.
        spc.ReportDiagnostic(Diagnostic.Create(
            new DiagnosticDescriptor(
                "TPH9000",
                "SourceLocationGenerator summary",
                $"SourceLocationGenerator: {attributable.Count} sites attributed, {skippedCount} skipped (no WithSiteId factory).",
                "Typhon.Profiler",
                DiagnosticSeverity.Info,
                true),
            Location.None));
    }

    private static string BuildInterceptorSource(List<CallSite> sites)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> SourceLocationGenerator — interceptor wrappers.");
        sb.AppendLine("// Each method redirects a `TyphonEvent.Begin*` call site to the matching `BeginXxxWithSiteId`,");
        sb.AppendLine("// passing a literal ushort siteId baked into the IL by the C# 14 interceptors feature.");
        sb.AppendLine("#nullable disable");
        sb.AppendLine();
        sb.AppendLine("namespace System.Runtime.CompilerServices");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]");
        sb.AppendLine("    file sealed class InterceptsLocationAttribute : global::System.Attribute");
        sb.AppendLine("    {");
        sb.AppendLine("        public InterceptsLocationAttribute(int version, string data) { _ = version; _ = data; }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"namespace {GeneratedNamespace}");
        sb.AppendLine("{");
        sb.AppendLine("    file static class TyphonEventInterceptors");
        sb.AppendLine("    {");

        for (int i = 0; i < sites.Count; i++)
        {
            var site = sites[i];
            var siteId = (ushort)(i + 1); // ids start at 1; 0 = unknown.

            // Build the parameter list for the interceptor (must exactly match the original factory's signature).
            var paramSb = new StringBuilder();
            for (int p = 0; p < site.Parameters.Length; p++)
            {
                if (p > 0) paramSb.Append(", ");
                paramSb.Append(site.Parameters[p].TypeFqn).Append(' ').Append(site.Parameters[p].Name);
            }
            // Build the forwarded argument list for the WithSiteId call: literal siteId, then the param names.
            var forwardSb = new StringBuilder();
            forwardSb.Append("0x").Append(siteId.ToString("X4"));
            foreach (var p in site.Parameters)
            {
                forwardSb.Append(", ").Append(p.Name);
            }

            sb.Append("        ");
            sb.AppendLine(site.Location.GetInterceptsLocationAttributeSyntax());
            sb.Append("        [global::System.Runtime.CompilerServices.MethodImpl(")
              .Append("global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]")
              .AppendLine();
            sb.Append("        public static ")
              .Append(site.ReturnTypeFqn)
              .Append(" Intercepted_")
              .Append(site.BaseName)
              .Append("_0x")
              .Append(siteId.ToString("X4"))
              .Append('(')
              .Append(paramSb)
              .AppendLine(")");
            sb.AppendLine("        {");
            sb.Append("            return global::Typhon.Engine.Internals.TyphonEvent.")
              .Append(site.BaseName)
              .Append("_WithSiteId(")
              .Append(forwardSb)
              .AppendLine(");");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildSourceLocationsTable(List<CallSite> sites)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> SourceLocationGenerator — id → (file, line, method, kind) table.");
        sb.AppendLine("// Wire-format manifest emitters dump these arrays straight to the trace stream / file / cache.");
        sb.AppendLine("#nullable disable");
        sb.AppendLine();
        sb.AppendLine("namespace Typhon.Engine.Profiler.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Compile-time source-location table built by SourceLocationGenerator.");
        sb.AppendLine("    /// Indexed by the ushort siteId carried in span records (when SpanFlags bit 1 is set).");
        sb.AppendLine("    /// Public so wire-format manifest emitters in Typhon.Profiler and tests in Typhon.Engine.Tests can read it.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static class SourceLocations");
        sb.AppendLine("    {");

        // File table (string[] indexed by FileId starting at 0).
        var files = sites.Select(s => s.FilePath).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var fileIdMap = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < files.Count; i++)
        {
            fileIdMap[files[i]] = i;
        }

        sb.AppendLine("        public static readonly string[] Files = new string[]");
        sb.AppendLine("        {");
        foreach (var f in files)
        {
            sb.Append("            ");
            sb.Append(EncodeStringLiteral(f));
            sb.AppendLine(",");
        }
        sb.AppendLine("        };");
        sb.AppendLine();

        // Entries: id → (fileId, line, method, kind-byte derived from base name).
        sb.AppendLine("        public readonly record struct Entry(ushort Id, ushort FileId, int Line, string Method, byte KindByte);");
        sb.AppendLine();
        sb.AppendLine("        public static readonly Entry[] All = new Entry[]");
        sb.AppendLine("        {");
        for (int i = 0; i < sites.Count; i++)
        {
            var site = sites[i];
            var siteId = (ushort)(i + 1);
            var fileId = (ushort)fileIdMap[site.FilePath];
            // KindByte is derived from BaseName: BeginBTreeInsert → "BTreeInsert" → matches enum member name.
            // We don't reference the enum at gen time (would couple Typhon.Generators to Typhon.Profiler);
            // we emit the bare name and let a runtime helper resolve to the byte if needed. For v1 we leave
            // this 0 and rely on the wire's existing kind byte for decoding; the table just carries the name.
            sb.Append("            new Entry(0x")
              .Append(siteId.ToString("X4"))
              .Append(", ")
              .Append(fileId)
              .Append(", ")
              .Append(site.Line)
              .Append(", ")
              .Append(EncodeStringLiteral(site.MethodName))
              .Append(", 0)")
              .AppendLine(",");
        }
        sb.AppendLine("        };");

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EncodeStringLiteral(string value)
    {
        if (value == null)
        {
            return "null";
        }
        // SymbolDisplay covers escaping for both regular and verbatim strings.
        return SymbolDisplay.FormatLiteral(value, true);
    }

    /// <summary>
    /// Rewrite absolute file paths from <c>SyntaxTree.FilePath</c> into the design's repo-relative
    /// "/_/..." form, using the <c>TyphonRepoRoot</c> MSBuild property as the prefix to strip.
    /// Backslashes are normalized to forward slashes so the manifest is platform-agnostic.
    /// If the repo root isn't configured or the path doesn't sit under it, the path is left unchanged.
    /// </summary>
    private static CallSite NormalizeFilePath(CallSite site, string repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot))
        {
            return site;
        }
        var path = site.FilePath;
        // Tolerate trailing-slash variations and case differences on Windows filesystems.
        if (path.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            var rel = path.Substring(repoRoot.Length).Replace('\\', '/').TrimStart('/');
            path = "/_/" + rel;
        }
        else
        {
            // Already mapped (e.g., compiler PathMap kicked in) or external — only normalize separators.
            path = path.Replace('\\', '/');
        }
        return new CallSite(site.Location, site.BaseName, site.ReturnTypeFqn, site.Parameters, path, site.Line, site.Column, site.MethodName);
    }
}
