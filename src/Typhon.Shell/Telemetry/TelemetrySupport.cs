using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Engine;

namespace Typhon.Shell.Telemetry;

/// <summary>Resolves user-typed flag paths against the catalog and hosts the curated presets. Feature #522 / T5.</summary>
internal static class TelemetrySupport
{
    /// <summary>
    /// Curated "show me X" bundles (a UX affordance living in the CLI, not the engine catalog). Each enables the
    /// master plus a subtree root; parent-implies-children then lights up the whole subsystem. Kept deliberately
    /// small — not exhaustive.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Presets = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["concurrency"] = new[] { "", "Concurrency" },
        ["durability"] = new[] { "", "Durability" },
        ["wal"] = new[] { "", "Durability", "Durability:WAL" },
        ["query"] = new[] { "", "Query" },
        ["query-plan"] = new[] { "", "Query", "Query:Plan" },
        ["spatial"] = new[] { "", "Spatial" },
        ["scheduler"] = new[] { "", "Scheduler" },
        ["storage"] = new[] { "", "Storage" },
    };

    /// <summary>Result of resolving a user-typed path.</summary>
    public readonly struct Resolution
    {
        public bool Ok { get; }
        public string Path { get; }
        public int Index { get; }
        public IReadOnlyList<string> Suggestions { get; }
        public Resolution(bool ok, string path, int index, IReadOnlyList<string> suggestions)
        {
            Ok = ok; Path = path; Index = index; Suggestions = suggestions;
        }
    }

    /// <summary>Resolve a user path (case-insensitive; <c>.</c>, <c>/</c> or <c>:</c> separators). Master = "" / "profiler" / "master".</summary>
    public static Resolution Resolve(string input)
    {
        var all = TelemetryFlagCatalog.All;
        var s = (input ?? "").Trim();
        if (s.Length == 0 || s.Equals("profiler", StringComparison.OrdinalIgnoreCase) || s.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            return new Resolution(true, "", 0, null);
        }
        var norm = s.Replace('.', ':').Replace('/', ':');
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Path.Equals(norm, StringComparison.OrdinalIgnoreCase))
            {
                return new Resolution(true, all[i].Path, i, null);
            }
        }
        var lastSeg = norm.Split(':').Last();
        var near = all
            .Where(d => d.Path.Length > 0 &&
                        (d.Path.IndexOf(norm, StringComparison.OrdinalIgnoreCase) >= 0 ||
                         d.Name.Equals(lastSeg, StringComparison.OrdinalIgnoreCase) ||
                         d.Name.IndexOf(lastSeg, StringComparison.OrdinalIgnoreCase) >= 0))
            .Select(d => d.Path)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();
        return new Resolution(false, null, -1, near);
    }
}
