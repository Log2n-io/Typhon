using System;
using System.Diagnostics;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Typhon.Engine;

namespace Typhon.Profiler.AspNetCore;

/// <summary>
/// Registers request correlation (#1517): HTTP requests become roots in the Typhon profiler, and the engine work they cause carries their id.
/// </summary>
[PublicAPI]
public static class TyphonRequestCorrelationExtensions
{
    /// <summary>
    /// Turns on request correlation for this host when the profiler's correlation switch is on, and registers nothing otherwise.
    /// </summary>
    /// <remarks>
    /// Two parts:
    /// <list type="bullet">
    /// <item>An <see cref="ActivityListener"/> on <c>Microsoft.AspNetCore</c> makes the host create its per-request <see cref="Activity"/>. A host
    /// with no listener creates none, and without one there is no trace id for the engine's spans to carry.</item>
    /// <item>A middleware, inserted first in the pipeline, records each request when it completes: its route template, method and status, which the
    /// .NET 10 activity does not carry by default.</item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddTyphonRequestCorrelation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!ProfilerCorrelation.IsActive)
        {
            return services;
        }

        RequestActivityListener.EnsureRegistered();
        services.AddSingleton<IStartupFilter, RequestCorrelationStartupFilter>();
        return services;
    }
}

/// <summary>The listener that makes ASP.NET Core create its request activity. One per process, since listeners are process-wide.</summary>
internal static class RequestActivityListener
{
    internal const string AspNetCoreSourceName = "Microsoft.AspNetCore";

    private static readonly object Gate = new();
    private static ActivityListener _listener;

    internal static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_listener != null)
            {
                return;
            }

            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == AspNetCoreSourceName,
                // PropagationData: the trace id is all the engine needs. The source takes the highest answer across its listeners, so asking for more
                // would override an OpenTelemetry sampler sharing the process (recording 100 %) and force sampled=01 onto outgoing traceparents.
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ProfilerCorrelation.IsActive ? ActivitySamplingResult.PropagationData : ActivitySamplingResult.None,
            };
            ActivitySource.AddActivityListener(_listener);
        }
    }
}

internal sealed class RequestCorrelationStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<RequestCorrelationMiddleware>();
        next(app);
    };
}

/// <summary>Records each request as an <c>ExternalRequest</c> root once it completes.</summary>
internal sealed class RequestCorrelationMiddleware
{
    private readonly RequestDelegate _next;

    public RequestCorrelationMiddleware(RequestDelegate next) => _next = next;

    /// <summary>
    /// The route recorded for a request no endpoint matched. Its raw path would make routes unbounded in number and could carry personal data.
    /// </summary>
    internal const string UnmatchedRoute = "(unmatched)";

    /// <summary>The status recorded for a request its client abandoned (nginx's 499): no status was sent, and it was not the server's failure.</summary>
    internal const int ClientClosedRequest = 499;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            // A WebSocket upgrade is not a request: it lasts the session's whole life, and the commands it carries are roots of their own (#1522).
            await _next(context).ConfigureAwait(false);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var failed = false;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch
        {
            // The server answers an unhandled exception with a 500 once it leaves the pipeline, but the response still reads 200 here.
            failed = true;
            throw;
        }
        finally
        {
            Record(context, start, Stopwatch.GetTimestamp(), failed);
        }
    }

    internal static void Record(HttpContext context, long start, long end, bool failed)
    {
        try
        {
            // Activity.Current is the hosting activity here, since this middleware runs first in the pipeline, inside it.
            var activity = Activity.Current;
            var id = activity == null ? CausalId.None : CausalId.FromTraceId(activity.TraceId);
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? UnmatchedRoute;
            var status = context.RequestAborted.IsCancellationRequested ? ClientClosedRequest
                : failed && !context.Response.HasStarted ? StatusCodes.Status500InternalServerError
                : context.Response.StatusCode;
            ProfilerCorrelation.RecordRequest(start, end, id, MethodOf(context.Request.Method), status, route);
        }
        catch
        {
            // Telemetry never fails a request, nor replaces the exception a handler threw.
        }
    }

    internal static ExternalRequestMethod MethodOf(string method)
    {
        if (HttpMethods.IsGet(method))
        {
            return ExternalRequestMethod.Get;
        }

        if (HttpMethods.IsPost(method))
        {
            return ExternalRequestMethod.Post;
        }

        if (HttpMethods.IsPut(method))
        {
            return ExternalRequestMethod.Put;
        }

        if (HttpMethods.IsDelete(method))
        {
            return ExternalRequestMethod.Delete;
        }

        if (HttpMethods.IsPatch(method))
        {
            return ExternalRequestMethod.Patch;
        }

        if (HttpMethods.IsHead(method))
        {
            return ExternalRequestMethod.Head;
        }

        return HttpMethods.IsOptions(method) ? ExternalRequestMethod.Options : ExternalRequestMethod.Other;
    }
}
