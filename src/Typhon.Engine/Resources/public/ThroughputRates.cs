using JetBrains.Annotations;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Throughput rates (ops/sec) computed from two consecutive snapshots.
/// </summary>
/// <remarks>
/// <para>
/// Rates are automatically computed by <see cref="IResourceGraph.GetSnapshot"/> by
/// comparing throughput counters between the current and previous snapshot.
/// </para>
/// <para>
/// The first snapshot has <see cref="ResourceSnapshot.Rates"/> = null since there's
/// no previous snapshot to compare against.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var snapshot = resourceGraph.GetSnapshot();
/// if (snapshot.Rates != null)
/// {
///     var hitRate = snapshot.Rates["Storage/PageCache"]["CacheHits"];
///     Console.WriteLine($"Cache hits per second: {hitRate:F1}");
/// }
/// </code>
/// </example>
[PublicAPI]
public sealed class ThroughputRates
{
    private readonly Dictionary<string, Dictionary<string, double>> _rates;
    private static readonly IReadOnlyDictionary<string, double> EmptyRates = new Dictionary<string, double>();

    /// <summary>
    /// Creates a new ThroughputRates instance with the computed rates.
    /// </summary>
    /// <param name="rates">Dictionary of node path to metric name to rate (ops/sec).</param>
    internal ThroughputRates(Dictionary<string, Dictionary<string, double>> rates)
    {
        _rates = rates;
    }

    /// <summary>
    /// Get rates for a specific node path.
    /// </summary>
    /// <param name="nodePath">Path to the node (e.g., "Storage/PageCache").</param>
    /// <returns>Dictionary of metric name to rate (ops/sec). Empty if node not found.</returns>
    public IReadOnlyDictionary<string, double> this[string nodePath] => 
        _rates.TryGetValue(nodePath, out var nodeRates) ? nodeRates : EmptyRates;

    /// <summary>
    /// All node paths with rate data.
    /// </summary>
    public IEnumerable<string> NodePaths => _rates.Keys;

    /// <summary>
    /// Get a specific rate value.
    /// </summary>
    /// <param name="nodePath">Path to the node.</param>
    /// <param name="metricName">Name of the throughput metric.</param>
    /// <returns>Rate in ops/sec, or 0 if not found.</returns>
    public double GetRate(string nodePath, string metricName)
        => _rates.TryGetValue(nodePath, out var nodeRates) && nodeRates.TryGetValue(metricName, out var rate) ? rate : 0.0;

    /// <summary>
    /// Checks if rates exist for a specific node path.
    /// </summary>
    /// <param name="nodePath">Path to the node.</param>
    /// <returns>True if the node has rate data.</returns>
    public bool ContainsNode(string nodePath) => _rates.ContainsKey(nodePath);
}
