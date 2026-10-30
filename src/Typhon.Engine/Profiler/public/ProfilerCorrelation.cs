using JetBrains.Annotations;
using Typhon.Profiler;

namespace Typhon.Engine;

/// <summary>
/// What a host records into the profiler about the requests it serves, so their engine work can be tied back to them (#1517). The ASP.NET Core
/// wiring (<c>Typhon.Profiler.AspNetCore</c>) is one caller; a host on another stack calls this directly.
/// </summary>
[PublicAPI]
public static class ProfilerCorrelation
{
    /// <summary>True while correlation is recorded: the profiler is active and <c>Typhon:Profiler:Correlation</c> is on.</summary>
    public static bool IsActive => TelemetryConfig.ProfilerCorrelationActive;

    /// <summary>
    /// Records one served request as an <see cref="TraceEventKind.ExternalRequest"/> span. Call it once the request has completed, on any thread.
    /// </summary>
    /// <param name="startTimestamp">When the request started, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
    /// <param name="endTimestamp">When it completed, in the same clock.</param>
    /// <param name="id">The request's causal id — the trace id its engine spans carry. <see cref="CausalId.None"/> records an uncorrelated request.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="status">The response status code.</param>
    /// <param name="route">The endpoint's route template (<c>/swg/inventory</c>), not the raw path, so requests group by endpoint.</param>
    public static void RecordRequest(long startTimestamp, long endTimestamp, CausalId id, ExternalRequestMethod method, int status, string route) =>
        TyphonEvent.EmitExternalRequest(startTimestamp, endTimestamp, id, method, (ushort)status, route);
}
