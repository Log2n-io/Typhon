// unset

using System.Diagnostics;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using System.Diagnostics.CodeAnalysis;

namespace Typhon.Engine;

public static class TraceIdLogExtensions
{
    public static LoggerConfiguration WithTraceId(this LoggerEnrichmentConfiguration enrichmentConfiguration) =>
        enrichmentConfiguration != null ? enrichmentConfiguration.With<TraceIdEnricher>() : throw new System.ArgumentNullException(nameof(enrichmentConfiguration));
}

/// <summary>
/// Serilog enricher that adds <c>TraceId</c> and <c>SpanId</c> properties from the current
/// <see cref="Activity"/> to log events, enabling log-to-trace correlation.
/// </summary>
/// <remarks>
/// Usage: <c>.Enrich.WithTraceId()</c> in Serilog configuration.
/// When no <see cref="Activity"/> is active, no properties are added (zero allocation).
/// </remarks>
[ExcludeFromCodeCoverage]
public class TraceIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity != null)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TraceId", activity.TraceId.ToHexString()));
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("SpanId", activity.SpanId.ToHexString()));
        }
    }
}
