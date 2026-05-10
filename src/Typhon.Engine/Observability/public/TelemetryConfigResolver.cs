using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// A node in a category tree consumed by <see cref="TelemetryConfigResolver"/>. Each node represents one nested level of the <c>Typhon:Profiler:*</c> JSON
/// config and produces one effective bool entry in the resolver's flat output map.
/// </summary>
internal sealed class Node
{
    /// <summary>Single-segment category name (no colons). Combined with the parent path during walk.</summary>
    public string Name { get; }

    /// <summary>Direct children. Empty array for leaves.</summary>
    public Node[] Children { get; }

    public Node(string name, Node[] children = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Children = children ?? [];
    }
}

/// <summary>
/// Pure-function resolver that turns a nested category tree + an <see cref="IConfiguration"/> root into a flat dictionary of <c>category:full:path → effective
/// bool</c>, with parent-implies-children semantics.
/// </summary>
/// <remarks>
/// <para>
/// Effective formula per leaf (from the umbrella tracing-instrumentation design, §3.2):
/// <code>
/// effective = parent.Effective AND (this.ExplicitEnabled ?? parent.Effective)
/// </code>
/// </para>
/// <para>
/// Behavior:
/// </para>
/// <list type="bullet">
///   <item>Parent off → all descendants off (regardless of their own keys).</item>
///   <item>Parent on, leaf key explicitly false → leaf off.</item>
///   <item>Parent on, leaf key absent → leaf inherits true.</item>
/// </list>
/// <para>
/// The resolver allocates exactly one <see cref="Dictionary{TKey,TValue}"/> per call. Intended to run once inside <see cref="TelemetryConfig"/>'s static
/// constructor; the dictionary is discarded after the <c>static readonly bool</c> fields are populated.
/// </para>
/// </remarks>
internal static class TelemetryConfigResolver
{
    /// <summary>
    /// Walk <paramref name="root"/> and produce the flat effective-state map.
    /// </summary>
    /// <param name="root">Root node of the subtree being resolved.</param>
    /// <param name="rootEffective">
    /// Pre-computed effective state of the root (typically the AND of the master profiler gate and the root's own <c>Enabled</c> key — computed by the caller
    /// because the master gate has its own back-compat semantics).
    /// </param>
    /// <param name="config">Loaded configuration root.</param>
    /// <param name="newKeyPrefix">
    /// JSON-namespace prefix prepended to every node's full path, e.g. <c>"Typhon:Profiler"</c>. The full key for any node is <c>{prefix}:{fullPath}:Enabled</c>.
    /// </param>
    /// <returns>
    /// Map keyed by full category path (e.g. <c>"Concurrency:AccessControl:SharedAcquire"</c>) → effective bool.
    /// The root's own entry is keyed by <see cref="Node.Name"/> only.
    /// </returns>
    public static Dictionary<string, bool> Resolve(Node root, bool rootEffective, IConfiguration config, string newKeyPrefix)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(newKeyPrefix);

        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        result[root.Name] = rootEffective;
        foreach (var child in root.Children)
        {
            Walk(child, root.Name, rootEffective, config, newKeyPrefix, result);
        }
        return result;
    }

    private static void Walk(Node node, string parentPath, bool parentEffective, IConfiguration config, string prefix, Dictionary<string, bool> result)
    {
        var fullPath = $"{parentPath}:{node.Name}";
        var explicitVal = config[$"{prefix}:{fullPath}:Enabled"];
        bool? explicitEnabled = string.IsNullOrEmpty(explicitVal) ? null : (bool.TryParse(explicitVal, out var b) ? b : null);
        var effective = parentEffective && explicitEnabled.GetValueOrDefault(true);
        result[fullPath] = effective;
        foreach (var child in node.Children)
        {
            Walk(child, fullPath, effective, config, prefix, result);
        }
    }
}
