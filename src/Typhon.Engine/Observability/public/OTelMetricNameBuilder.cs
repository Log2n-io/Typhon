using JetBrains.Annotations;
using System.Text;

namespace Typhon.Engine;

/// <summary>
/// Converts Typhon resource paths and metric kinds to OpenTelemetry metric names.
/// </summary>
/// <remarks>
/// <para>
/// OTel metric naming conventions:
/// </para>
/// <list type="bullet">
///   <item><description>Use dots (.) for hierarchy</description></item>
///   <item><description>Use lowercase with underscores for multi-word names</description></item>
///   <item><description>Pattern: <c>{prefix}.{path}.{kind}.{sub_metric}</c></description></item>
/// </list>
/// <para>
/// Prometheus auto-converts dots to underscores, so the same names work for both systems.
/// </para>
/// <example>
/// <code>
/// var name = OTelMetricNameBuilder.Build("typhon.resource", "Storage/PageCache", "memory", "allocated_bytes");
/// // Result: "typhon.resource.storage.page_cache.memory.allocated_bytes"
/// </code>
/// </example>
/// </remarks>
[PublicAPI]
public static class OTelMetricNameBuilder
{
    /// <summary>
    /// Build an OTel metric name from components.
    /// </summary>
    /// <param name="prefix">Metric namespace prefix (e.g., "typhon.resource").</param>
    /// <param name="nodePath">Resource path (e.g., "Storage/PageCache" or "Root/Storage/PageCache").</param>
    /// <param name="metricKind">Metric category (e.g., "memory", "capacity", "contention").</param>
    /// <param name="subMetric">Specific metric within the category (e.g., "allocated_bytes", "utilization").</param>
    /// <returns>Fully qualified OTel metric name.</returns>
    public static string Build(string prefix, string nodePath, string metricKind, string subMetric)
    {
        var normalizedPath = NormalizePath(nodePath);
        return $"{prefix}.{normalizedPath}.{metricKind}.{subMetric}";
    }

    /// <summary>
    /// Build an OTel metric name for throughput and duration metrics that have named instances.
    /// </summary>
    /// <param name="prefix">Metric namespace prefix (e.g., "typhon.resource").</param>
    /// <param name="nodePath">Resource path (e.g., "Storage/PageCache").</param>
    /// <param name="metricKind">Metric category (e.g., "throughput", "duration").</param>
    /// <param name="metricName">Named metric instance (e.g., "CacheHits", "Checkpoint").</param>
    /// <param name="subMetric">Specific metric within the instance (e.g., "count", "last_us").</param>
    /// <returns>Fully qualified OTel metric name.</returns>
    public static string BuildNamed(string prefix, string nodePath, string metricKind, string metricName, string subMetric)
    {
        var normalizedPath = NormalizePath(nodePath);
        var normalizedName = ToSnakeCase(metricName);
        return $"{prefix}.{normalizedPath}.{metricKind}.{normalizedName}.{subMetric}";
    }

    /// <summary>
    /// Normalize a resource path to OTel-compatible format.
    /// </summary>
    /// <param name="nodePath">Resource path (e.g., "Root/Storage/PageCache" or "Storage/PageCache").</param>
    /// <returns>Normalized path in lowercase with underscores and dots (e.g., "storage.page_cache").</returns>
    /// <remarks>
    /// <para>
    /// Transformations applied:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Remove "Root/" prefix if present</description></item>
    ///   <item><description>Convert "/" to "."</description></item>
    ///   <item><description>Convert PascalCase to snake_case</description></item>
    ///   <item><description>Convert to lowercase</description></item>
    /// </list>
    /// </remarks>
    public static string NormalizePath(string nodePath)
    {
        if (string.IsNullOrEmpty(nodePath))
        {
            return string.Empty;
        }

        // Remove "Root/" prefix if present
        var path = nodePath.StartsWith("Root/") ? nodePath[5..] : nodePath;

        if (string.IsNullOrEmpty(path))
        {
            return "root";
        }

        var sb = new StringBuilder(path.Length * 2);
        var first = true;

        foreach (var segment in path.Split('/'))
        {
            if (!first)
            {
                sb.Append('.');
            }
            first = false;

            AppendSnakeCase(sb, segment);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Convert a PascalCase or camelCase string to snake_case.
    /// </summary>
    /// <param name="input">Input string in PascalCase or camelCase.</param>
    /// <returns>Output string in snake_case.</returns>
    public static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length * 2);
        AppendSnakeCase(sb, input);
        return sb.ToString();
    }

    private static void AppendSnakeCase(StringBuilder sb, string input)
    {
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (char.IsUpper(c))
            {
                // Add underscore before uppercase (except at start or after another uppercase)
                if (i > 0 && !char.IsUpper(input[i - 1]))
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
    }
}
