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
    /// (see <see cref="TryOpenPdbForAssemblyFile"/>'s "do not dispose" note). Only successful opens are cached; failures land in <see cref="_pdbMissing"/>
    /// instead of being represented as null entries here.
    /// </summary>
    private static readonly ConcurrentDictionary<Module, MetadataReaderProvider> _pdbCache = new();

    /// <summary>Modules whose PDB lookup has already failed; checked first to avoid retrying.</summary>
    private static readonly ConcurrentDictionary<Module, bool> _pdbMissing = new();

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

    private static (string FilePath, int Line, string MethodName)? ResolveMethod(MethodInfo method)
    {
        var module = method.Module;

        if (_pdbMissing.ContainsKey(module))
        {
            return null;
        }

        if (!_pdbCache.TryGetValue(module, out var provider))
        {
            // Open lazily on first miss; only cache successes.
            // Failures populate _pdbMissing so we never thrash on the slow PDB-search path for the same module twice.
            provider = OpenPdb(module);
            if (provider == null)
            {
                _pdbMissing[module] = true;
                return null;
            }
            _pdbCache[module] = provider;
        }

        try
        {
            var reader = provider.GetMetadataReader();
            var handle = MetadataTokens.MethodDefinitionHandle(method.MetadataToken);
            var debugInfo = reader.GetMethodDebugInformation(handle);
            if (debugInfo.Document.IsNil && debugInfo.SequencePointsBlob.IsNil)
            {
                return null;
            }

            // First non-hidden sequence point — that's the method body's first executable line, which is what the user wants to navigate to
            // (matches what F12-Go-To-Definition does in IDEs).
            foreach (var sp in debugInfo.GetSequencePoints())
            {
                if (sp.IsHidden)
                {
                    continue;
                }
                var doc = reader.GetDocument(sp.Document);
                var path = reader.GetString(doc.Name);
                return (path, sp.StartLine, method.Name);
            }
        }
        catch
        {
            // PDB read failures are non-fatal: a system without source attribution falls back to the
            // existing "no Source row" UX rather than crashing the engine.
        }
        return null;
    }

    private static MetadataReaderProvider OpenPdb(Module module)
    {
        // Fast path: standard dotnet hosts publish a real path via Assembly.Location or Module.FullyQualifiedName. Try those first.
        var asmPath = module.Assembly?.Location;
        if (string.IsNullOrEmpty(asmPath) || !File.Exists(asmPath))
        {
            asmPath = module.FullyQualifiedName;
        }
        if (!string.IsNullOrEmpty(asmPath) && File.Exists(asmPath))
        {
            var fast = TryOpenPdbForAssemblyFile(asmPath);
            if (fast != null)
            {
                return fast;
            }
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
        var asmName = module.Assembly?.GetName();
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
        try
        {
            return File.Open(pdbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch
        {
            return null;
        }
    }
}
