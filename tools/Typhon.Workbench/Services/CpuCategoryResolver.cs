using System;
using System.Collections.Generic;

namespace Typhon.Workbench.Services;

/// <summary>
/// Maps a CPU-sample frame's <c>(file, method)</c> to an engine/host "subsystem" category (#351 §8.6). The engine category is
/// the first path segment under <c>src/Typhon.Engine/</c> — Typhon's source tree mirrors the engine's structure, so the
/// folder name <i>is</i> the subsystem. Non-engine frames bucket coarsely: <c>User</c> (host-app source), <c>BCL</c>
/// (framework, name-only), <c>Native</c> (module!symbol form), <c>Unknown</c> (unresolved).
/// </summary>
/// <remarks>
/// Category ids are assigned in discovery order as frames are resolved — the resolver is a per-session, single-threaded
/// builder used once while the trailer section loads. <see cref="Categories"/> yields the id→name table for the manifest.
/// </remarks>
public sealed class CpuCategoryResolver
{
    private const string EngineSrcToken = "src/Typhon.Engine/";

    private readonly Dictionary<string, int> _idByName = new(StringComparer.Ordinal);
    private readonly List<string> _names = [];

    /// <summary>Discovered categories, indexed by category id.</summary>
    public IReadOnlyList<string> Categories => _names;

    /// <summary>Resolves <paramref name="file"/> / <paramref name="method"/> to a category id, registering the category on first sight.</summary>
    public int Resolve(string file, string method)
    {
        var name = CategoryName(file, method);
        if (!_idByName.TryGetValue(name, out var id))
        {
            id = _names.Count;
            _idByName[name] = id;
            _names.Add(name);
        }
        return id;
    }

    private static string CategoryName(string file, string method)
    {
        if (!string.IsNullOrEmpty(file))
        {
            var norm = file.Replace('\\', '/');
            var tokenIdx = norm.IndexOf(EngineSrcToken, StringComparison.OrdinalIgnoreCase);
            if (tokenIdx >= 0)
            {
                var segStart = tokenIdx + EngineSrcToken.Length;
                var segEnd = norm.IndexOf('/', segStart);
                if (segEnd > segStart)
                {
                    return norm.Substring(segStart, segEnd - segStart);
                }
            }
            // A resolved path that isn't engine source is host-app code (e.g. an AntHill demo file).
            return "User";
        }

        // No source — classify by method name. TraceEvent renders native frames as "module!symbol".
        if (!string.IsNullOrEmpty(method))
        {
            if (method.IndexOf('!') >= 0)
            {
                return "Native";
            }
            if (method.StartsWith("System.", StringComparison.Ordinal)
                || method.StartsWith("Microsoft.", StringComparison.Ordinal)
                || method.StartsWith("Internal.", StringComparison.Ordinal))
            {
                return "BCL";
            }
        }
        return "Unknown";
    }
}
