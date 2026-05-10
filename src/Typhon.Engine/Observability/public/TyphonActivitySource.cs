using JetBrains.Annotations;
using System.Diagnostics;

namespace Typhon.Engine;

/// <summary>
/// Provides the centralized ActivitySource for Typhon distributed tracing.
/// </summary>
/// <remarks>
/// <para>
/// Use this ActivitySource to create spans for database operations:
/// </para>
/// <example>
/// <code>
/// using var activity = TyphonActivitySource.Instance.StartActivity("Transaction.Commit");
/// activity?.SetTag("entity.count", entityCount);
/// // ... do work ...
/// </code>
/// </example>
/// </remarks>
[PublicAPI]
public static class TyphonActivitySource
{
    /// <summary>
    /// The name of the Typhon ActivitySource, used for OpenTelemetry configuration.
    /// </summary>
    public const string Name = "Typhon.Engine";

    /// <summary>
    /// The version of the ActivitySource.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// The singleton ActivitySource instance for all Typhon tracing.
    /// </summary>
    public static ActivitySource Instance { get; } = new(Name, Version);

    /// <summary>
    /// Starts an activity (span) for a database operation.
    /// </summary>
    /// <param name="operationName">The name of the operation (e.g., "Transaction.Commit").</param>
    /// <param name="kind">The kind of activity (default: Internal).</param>
    /// <returns>The started Activity, or null if no listener is registered.</returns>
    public static Activity StartActivity(string operationName, ActivityKind kind = ActivityKind.Internal)
        => Instance.StartActivity(operationName, kind);
}
