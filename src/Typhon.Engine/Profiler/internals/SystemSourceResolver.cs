using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Typhon.Engine.Internals;

/// <summary>
/// Maps a registered system <see cref="Delegate"/> to its source location by reading sequence points from the assembly's portable PDB (#302 system-side
/// attribution). The resolver is best-effort: when the delegate's assembly is missing a PDB, has only Windows-PDB symbols, or is dynamically generated
/// the resolver returns <c>null</c> and the system silently has no Source row in the Workbench (same fallback as a span with siteId = 0).
/// </summary>
/// <remarks>
/// We attribute the delegate's <see cref="MethodInfo"/> directly. Method-group references resolve to the user's named method
/// (e.g. <c>TyphonBridge.MoveAllAnts</c>); lambdas resolve to the compiler-synthesized lambda method, whose first sequence point is the lambda body line —
/// which is exactly what the user wrote inline at the registration site. Both shapes give a useful "click → see code" target.
/// </remarks>
internal static class SystemSourceResolver
{
    /// <summary>
    /// Per-module cache of opened PDB providers. Entries are kept for the process lifetime — module identity is process-stable, system-registration assemblies
    /// are few (typically 1–2 user assemblies + Typhon.Engine), and the underlying file streams must outlive the cache
    /// (see <see cref="TryOpenPdbForAssemblyFile"/>'s "do not dispose" note). Only successful opens are cached; failures land in <see cref="PdbMissing"/>
    /// instead of being represented as null entries here.
    /// </summary>
    private static readonly ConcurrentDictionary<Module, MetadataReaderProvider> PdbCache = new();

    /// <summary>Modules whose PDB lookup has already failed; checked first to avoid retrying.</summary>
    private static readonly ConcurrentDictionary<Module, bool> PdbMissing = new();

    /// <summary>
    /// Per-method sequence-point cache, keyed on <c>(module, metadataToken)</c>. The CPU-sampling parser calls <see cref="ResolveByToken"/> once per stack
    /// frame — millions of times for a real trace — and each call has to map an IL offset to a source line. Resolving that from the PDB on every call
    /// (open the metadata reader, fetch the method debug info, enumerate the sequence-point blob, allocate a document-name string per point) is the
    /// dominant epilogue cost. Keying a *result* cache on <c>(module, token, ilOffset)</c> barely helps — a hot method is sampled at many distinct IL
    /// offsets that all collapse to a handful of source lines only *after* resolution. So the cache holds the parsed sequence-point table per method
    /// instead: it is built once (cardinality = distinct sampled methods, a few hundred), and the per-frame cost drops to a dictionary lookup plus a short
    /// in-memory scan for the covering point — no metadata access, no allocation. An empty array caches the "no sequence points" miss so it isn't retried.
    /// </summary>
    private static readonly ConcurrentDictionary<(Module Module, int Token), SequencePointRow[]> SeqPointCache = new();

    /// <summary>Shared empty result for methods with no usable sequence points — avoids a per-miss allocation.</summary>
    private static readonly SequencePointRow[] NoSequencePoints = [];

    /// <summary>One non-hidden sequence point, flattened from the portable-PDB blob: IL offset → source line. Emitted in IL-offset-ascending order.</summary>
    private readonly struct SequencePointRow(int offset, int line, string path)
    {
        /// <summary>IL offset the point covers from.</summary>
        public readonly int Offset = offset;
        /// <summary>1-based source line.</summary>
        public readonly int Line = line;
        /// <summary>Source document path.</summary>
        public readonly string Path = path;
    }

    /// <summary>
    /// Resolves the given delegate to <c>(filePath, line, methodName)</c>, or null when the PDB is unavailable or the method has no sequence points (purely
    /// synthesized methods, native, etc.).
    /// </summary>
    public static (string FilePath, int Line, string MethodName)? Resolve(Delegate del) => del?.Method == null ? null : ResolveMethod(del.Method);

    /// <summary>
    /// Resolves a class-based system (any <see cref="ISystem"/>) to the source location of its concrete
    /// <c>Execute</c> override. Walking the type hierarchy starting at the runtime instance type lets the user click into the override body — e.g.
    /// <c>AntUpdateSystem.Execute</c> at line 53 — instead of the lambda created at the registration call site in <c>RuntimeSchedule</c>.
    /// </summary>
    public static (string FilePath, int Line, string MethodName)? ResolveOverride(ISystem systemInstance, string methodName)
    {
        if (systemInstance == null || string.IsNullOrEmpty(methodName))
        {
            return null;
        }
        // Walk the declared type chain so we land on the most-derived override that actually carries sequence points. The base abstract declaration on
        // QuerySystem / PipelineSystem has none and would surface as a fallback "<no source>" when the override fails for any reason.
        var type = systemInstance.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        while (type != null && type != typeof(object))
        {
            var method = type.GetMethod(methodName, flags);
            if (method != null && !method.IsAbstract)
            {
                var resolved = ResolveMethod(method);
                if (resolved.HasValue)
                {
                    return resolved;
                }
            }
            type = type.BaseType;
        }
        return null;
    }

    /// <summary>
    /// Resolves a method identified by its declaring <paramref name="module"/> and metadata token to <c>(filePath, line)</c> — the entry point used by
    /// the CPU-sampling parser, which has only a <c>(module, token)</c> pair per stack frame (no live <see cref="MethodInfo"/>). When
    /// <paramref name="ilOffset"/> is non-negative, the resolver returns the *covering* sequence point (the exact line the sample's instruction pointer
    /// falls on); otherwise it returns the method's first executable line. Returns null when the module's PDB is unavailable or the method carries no
    /// sequence points.
    /// </summary>
    public static (string FilePath, int Line)? ResolveByToken(Module module, int metadataToken, int ilOffset = -1) =>
        module == null ? null : ResolveCore(module, metadataToken, ilOffset);

    private static (string FilePath, int Line, string MethodName)? ResolveMethod(MethodInfo method)
    {
        var core = ResolveCore(method.Module, method.MetadataToken, -1);
        if (!core.HasValue)
        {
            return null;
        }
        return (core.Value.FilePath, core.Value.Line, method.Name);
    }

    private static (string FilePath, int Line)? ResolveCore(Module module, int metadataToken, int ilOffset)
    {
        var rows = GetSequencePoints(module, metadataToken);
        if (rows.Length == 0)
        {
            return null;
        }

        // No IL offset (delegate / system attribution): the method body's first executable line — what the user wants to navigate to (matches
        // F12-Go-To-Definition). With an IL offset (a CPU sample's in-method position): the *covering* sequence point — the one with the greatest
        // Offset <= ilOffset. Rows are IL-offset-ascending, so a forward scan that stops on the first row past the offset finds it; an offset preceding
        // every row falls back to the first row.
        if (ilOffset < 0)
        {
            return (rows[0].Path, rows[0].Line);
        }
        var cover = rows[0];
        for (var i = 0; i < rows.Length && rows[i].Offset <= ilOffset; i++)
        {
            cover = rows[i];
        }
        return (cover.Path, cover.Line);
    }

    /// <summary>
    /// Returns the cached non-hidden sequence-point table for a method, building it on first request. Returns {@link _noSequencePoints} (empty) when the
    /// module's PDB is unavailable or the method carries no sequence points — that miss is cached so it isn't retried per frame.
    /// </summary>
    private static SequencePointRow[] GetSequencePoints(Module module, int metadataToken)
    {
        var key = (module, metadataToken);
        if (SeqPointCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        // A benign race can build the same method twice; the result is identical, so last-write-wins is fine (matches the _pdbCache double-open race).
        var built = BuildSequencePoints(module, metadataToken);
        SeqPointCache[key] = built;
        return built;
    }

    private static SequencePointRow[] BuildSequencePoints(Module module, int metadataToken)
    {
        if (PdbMissing.ContainsKey(module))
        {
            return NoSequencePoints;
        }

        if (!PdbCache.TryGetValue(module, out var provider))
        {
            // Open lazily on first miss; only cache successes.
            // Failures populate _pdbMissing so we never thrash on the slow PDB-search path for the same module twice.
            provider = OpenPdb(module);
            if (provider == null)
            {
                PdbMissing[module] = true;
                return NoSequencePoints;
            }
            PdbCache[module] = provider;
        }

        try
        {
            var reader = provider.GetMetadataReader();
            var handle = MetadataTokens.MethodDefinitionHandle(metadataToken);
            var debugInfo = reader.GetMethodDebugInformation(handle);
            if (debugInfo.Document.IsNil && debugInfo.SequencePointsBlob.IsNil)
            {
                return NoSequencePoints;
            }

            var rows = new System.Collections.Generic.List<SequencePointRow>();
            foreach (var sp in debugInfo.GetSequencePoints())
            {
                if (sp.IsHidden)
                {
                    continue;
                }
                var doc = reader.GetDocument(sp.Document);
                rows.Add(new SequencePointRow(sp.Offset, sp.StartLine, reader.GetString(doc.Name)));
            }
            return rows.Count == 0 ? NoSequencePoints : rows.ToArray();
        }
        catch
        {
            // PDB read failures are non-fatal: a system without source attribution falls back to the
            // existing "no Source row" UX rather than crashing the engine.
            return NoSequencePoints;
        }
    }

    private static MetadataReaderProvider OpenPdb(Module module)
    {
        // Fast path: standard dotnet hosts publish a real path via Assembly.Location or Module.FullyQualifiedName. Try those first.
        var asmPath = module.Assembly.Location;
        if (string.IsNullOrEmpty(asmPath) || !File.Exists(asmPath))
        {
            asmPath = module.FullyQualifiedName;
        }
        if (!string.IsNullOrEmpty(asmPath) && File.Exists(asmPath))
        {
            // The assembly path is known, so TryOpenPdbForAssemblyFile is authoritative: if it finds no PDB embedded in or beside the assembly, there is
            // none. A CPU-sample trace's stacks span hundreds of modules (the BCL, the framework, NuGet packages), almost none of which ship a local PDB;
            // falling through to the recursive filesystem search below for every one of those was the dominant epilogue cost (~4 ms/module — a recursive
            // EnumerateFiles per miss). The search is reserved for the case where the path itself is unavailable.
            return TryOpenPdbForAssemblyFile(asmPath);
        }

        // Slow path: Godot mono (and a few other hosts) report "<Unknown>" for both paths. Search the filesystem for an assembly whose AssemblyDefinition
        // matches the loaded module's name and version. The first match yields the PE file whose associated PDB we want.
        return SearchPdbByAssemblyName(module);
    }

    private static MetadataReaderProvider TryOpenPdbForAssemblyFile(string asmPath)
    {
        try
        {
            var stream = File.Open(asmPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var peReader = new PEReader(stream);
            if (peReader.TryOpenAssociatedPortablePdb(asmPath, OpenPdbStream, out var pdbProvider, out _))
            {
                // peReader is intentionally not disposed — disposing tears down the underlying stream that pdbProvider is mapped over. One PEReader per
                // assembly for process lifetime; cache misses are rare (one per system delegate's assembly).
                return pdbProvider;
            }
            peReader.Dispose();
            stream.Dispose();
        }
        catch
        {
            // Fall through to "no PDB" — assembly is unreadable or PDB is malformed.
        }
        return null;
    }

    private static MetadataReaderProvider SearchPdbByAssemblyName(Module module)
    {
        var asmName = module.Assembly.GetName();
        if (string.IsNullOrEmpty(asmName.Name))
        {
            return null;
        }
        var targetName = asmName.Name;
        var targetVersion = asmName.Version;

        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in GetSearchRoots())
        {
            if (string.IsNullOrEmpty(root) || !seen.Add(root) || !Directory.Exists(root))
            {
                continue;
            }

            System.Collections.Generic.IEnumerable<string> candidates;
            try
            {
                candidates = Directory.EnumerateFiles(root, $"{targetName}.dll", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }
            var checkedCount = 0;
            foreach (var dllPath in candidates)
            {
                if (++checkedCount > 50)
                {
                    break; // sanity cap; typical projects have <5 candidates.
                }

                if (!IsAssemblyMatch(dllPath, targetName, targetVersion))
                {
                    continue;
                }

                var found = TryOpenPdbForAssemblyFile(dllPath);
                if (found != null)
                {
                    return found;
                }
            }
        }
        return null;
    }

    private static bool IsAssemblyMatch(string dllPath, string expectedName, Version expectedVersion)
    {
        try
        {
            using var stream = File.Open(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return false;
            }

            var mr = pe.GetMetadataReader();
            var def = mr.GetAssemblyDefinition();
            var defName = mr.GetString(def.Name);
            if (!string.Equals(defName, expectedName, StringComparison.Ordinal))
            {
                return false;
            }

            return expectedVersion == null || def.Version == expectedVersion;
        }
        catch
        {
            return false;
        }
    }

    private static System.Collections.Generic.IEnumerable<string> GetSearchRoots()
    {
        var custom = Environment.GetEnvironmentVariable("TYPHON_PDB_SEARCH_PATHS");
        if (!string.IsNullOrEmpty(custom))
        {
            foreach (var p in custom.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return p;
            }
        }
        yield return Environment.CurrentDirectory;
        yield return AppDomain.CurrentDomain.BaseDirectory;
    }

    private static Stream OpenPdbStream(string pdbPath)
    {
        // The PDB path comes from the PE's CodeView debug entry — for BCL / Godot / NuGet assemblies it points at the path on the *build* machine, which does
        // not exist here. That absent-PDB case is the common, expected one (symbol resolution is best-effort), so check existence first instead of letting
        // File.Open throw a FileNotFoundException per module: exceptions are expensive and surface as noisy first-chance breaks under a debugger.
        // The catch still covers the rarer cases — a TOCTOU removal mid-open, sharing/ACL failures.
        try
        {
            if (string.IsNullOrEmpty(pdbPath) || !File.Exists(pdbPath))
            {
                return null;
            }
            return File.Open(pdbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch
        {
            return null;
        }
    }
}
