using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Exception thrown when a bounded resource has reached its capacity limit.
/// </summary>
/// <remarks>
/// <para>
/// This exception is thrown by components using the <see cref="ExhaustionPolicy.FailFast"/> policy.
/// It provides detailed information about which resource is exhausted and its current state.
/// </para>
/// <example>
/// <code>
/// if (_activeCount >= _options.MaxActiveTransactions)
/// {
///     throw new ResourceExhaustedException(
///         "DataEngine/TransactionPool",
///         ResourceType.Service,
///         _activeCount,
///         _options.MaxActiveTransactions);
/// }
/// </code>
/// </example>
/// </remarks>
[PublicAPI]
public class ResourceExhaustedException : TyphonException
{
    /// <summary>
    /// Full path of the exhausted resource in the resource tree.
    /// </summary>
    /// <example>"DataEngine/TransactionPool", "Storage/PageCache"</example>
    public string ResourcePath { get; }

    /// <summary>
    /// Type of the exhausted resource.
    /// </summary>
    public ResourceType ResourceType { get; }

    /// <summary>
    /// Current usage count when the exception was thrown.
    /// </summary>
    public long CurrentUsage { get; }

    /// <summary>
    /// Maximum allowed capacity for this resource.
    /// </summary>
    public long Limit { get; }

    /// <summary>
    /// Current utilization as a ratio (0.0 to 1.0+).
    /// </summary>
    public double Utilization => Limit > 0 ? (double)CurrentUsage / Limit : 1.0;

    /// <summary>
    /// Creates a new ResourceExhaustedException with full details.
    /// </summary>
    /// <param name="resourcePath">Full path of the exhausted resource.</param>
    /// <param name="resourceType">Type of the resource.</param>
    /// <param name="currentUsage">Current usage count.</param>
    /// <param name="limit">Maximum allowed capacity.</param>
    public ResourceExhaustedException(string resourcePath, ResourceType resourceType, long currentUsage, long limit)
        : base(TyphonErrorCode.ResourceExhausted, FormatMessage(resourcePath, currentUsage, limit))
    {
        ResourcePath = resourcePath;
        ResourceType = resourceType;
        CurrentUsage = currentUsage;
        Limit = limit;
    }

    /// <summary>
    /// Creates a new ResourceExhaustedException with a custom message.
    /// </summary>
    /// <param name="message">Custom message describing the exhaustion.</param>
    /// <param name="resourcePath">Full path of the exhausted resource.</param>
    /// <param name="resourceType">Type of the resource.</param>
    /// <param name="currentUsage">Current usage count.</param>
    /// <param name="limit">Maximum allowed capacity.</param>
    public ResourceExhaustedException(string message, string resourcePath, ResourceType resourceType, long currentUsage, long limit)
        : base(TyphonErrorCode.ResourceExhausted, message)
    {
        ResourcePath = resourcePath;
        ResourceType = resourceType;
        CurrentUsage = currentUsage;
        Limit = limit;
    }

    /// <summary>
    /// Creates a new ResourceExhaustedException with a custom message and inner exception.
    /// </summary>
    /// <param name="message">Custom message describing the exhaustion.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    /// <param name="resourcePath">Full path of the exhausted resource.</param>
    /// <param name="resourceType">Type of the resource.</param>
    /// <param name="currentUsage">Current usage count.</param>
    /// <param name="limit">Maximum allowed capacity.</param>
    public ResourceExhaustedException(string message, Exception innerException, string resourcePath, ResourceType resourceType, long currentUsage, long limit)
        : base(TyphonErrorCode.ResourceExhausted, message, innerException)
    {
        ResourcePath = resourcePath;
        ResourceType = resourceType;
        CurrentUsage = currentUsage;
        Limit = limit;
    }

    /// <summary>
    /// Resource exhaustion is transient — the resource may self-heal (eviction, pool drain).
    /// </summary>
    public override bool IsTransient => true;

    private static string FormatMessage(string resourcePath, long currentUsage, long limit) =>
        $"Resource '{resourcePath}' exhausted: {currentUsage:N0} / {limit:N0} ({(limit > 0 ? (double)currentUsage / limit * 100 : 100):F1}% utilization)";
}
