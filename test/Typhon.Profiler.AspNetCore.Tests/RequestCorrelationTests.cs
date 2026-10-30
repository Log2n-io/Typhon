using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Profiler;
using Typhon.Profiler;

namespace Typhon.Profiler.AspNetCore.Tests;

/// <summary>
/// Turns the profiler and its correlation switch on for this whole process, before anything reads <see cref="TelemetryConfig"/>: the flags are read
/// once, when the engine loads, and environment variables win over every configuration file. This suite exists to test the ON path, so it owns its
/// process and needs no gated pass.
/// </summary>
internal static class CorrelationOn
{
    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("TYPHON__PROFILER__ENABLED", "true");
        Environment.SetEnvironmentVariable("TYPHON__PROFILER__TRACE", "none");
        Environment.SetEnvironmentVariable("TYPHON__PROFILER__CORRELATION__ENABLED", "true");
    }
}

/// <summary>
/// <see cref="TyphonRequestCorrelationExtensions.AddTyphonRequestCorrelation"/> through a real hosting pipeline (#1517, step 1): each request is
/// recorded as one root, and the engine spans it causes carry its trace id on whatever thread its <c>await</c>s land.
/// </summary>
[TestFixture]
[NonParallelizable] // the profiler and its slot registry are process-global
public class RequestCorrelationTests
{
    private IHost _host;
    private ResourceRegistry _registry;
    private HttpClient _client;
    private TraceRingObserver _observer;
    private readonly ConcurrentQueue<(ulong Hi, ulong Lo, string Activity)> _spans = new();
    private readonly ConcurrentQueue<bool> _allDataRequested = new();

    [SetUp]
    public async Task SetUp()
    {
        Assert.That(ProfilerCorrelation.IsActive, Is.True, "the module initializer must have turned correlation on");
        // One fixture instance serves every test: what a test left in the queues must not reach the next.
        _spans.Clear();
        _allDataRequested.Clear();

        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "correlation-tests" });
        _observer = new TraceRingObserver(_registry.Profiler, captureRawBytes: true);
        TyphonProfiler.AttachExporter(_observer);
        TyphonProfiler.Start(_registry.Profiler, new ProfilerSessionMetadata(systems: [], archetypes: [], componentTypes: [], workerCount: 0,
            baseTickRate: 60.0f, startTimestamp: Stopwatch.GetTimestamp(), stopwatchFrequency: Stopwatch.Frequency, startedUtc: DateTime.UtcNow));

        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddTyphonRequestCorrelation();
                })
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.UseRouting();
                    app.UseEndpoints(e =>
                    {
                        e.MapGet("/items/{id}", async (int id) =>
                        {
                            // Engine work after an await, so on whatever thread the continuation lands: the request's id must follow it.
                            await Task.Yield();
                            using var span = TyphonEvent.BeginBTreeInsert();
                            _spans.Enqueue((span.Header.TraceIdHi, span.Header.TraceIdLo, Activity.Current?.TraceId.ToHexString()));
                            _allDataRequested.Enqueue(Activity.Current?.IsAllDataRequested ?? true);
                            return Results.Text($"item {id}");
                        });
                        e.MapPost("/fail/{code:int}", (int code) => Results.StatusCode(code));
                        e.MapGet("/boom", (Func<string>)(() => throw new InvalidOperationException("handler failure")));
                        e.Map("/ws", async (HttpContext ctx) =>
                        {
                            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
                            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", default);
                        });
                    });
                }))
            .StartAsync();
        _client = _host.GetTestClient();
    }

    [TearDown]
    public async Task TearDown()
    {
        _client?.Dispose();
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        try { TyphonProfiler.Stop(); } catch { /* already stopped */ }
        TyphonProfiler.ResetForTests();
        _observer?.Dispose();
        _registry?.Dispose();
    }

    [Test]
    public async Task A_request_is_one_root_with_its_route_template_and_its_engine_spans_carry_its_id()
    {
        var response = await _client.GetAsync("/items/42");
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("item 42"));

        var requests = ExternalRequests();
        Assert.That(requests, Has.Length.EqualTo(1));
        var root = requests[0];
        Assert.Multiple(() =>
        {
            Assert.That(root.Route, Is.EqualTo("/items/{id}"), "the template, so requests group by endpoint");
            Assert.That(root.Method, Is.EqualTo(ExternalRequestMethod.Get));
            Assert.That(root.Status, Is.EqualTo(200));
            Assert.That(root.DurationTicks, Is.GreaterThan(0));
            Assert.That((root.TraceIdHi | root.TraceIdLo), Is.Not.Zero, "the hosting activity exists, so the request has a trace id");
        });

        Assert.That(_spans.TryDequeue(out var span), Is.True);
        Assert.That((span.Hi, span.Lo), Is.EqualTo((root.TraceIdHi, root.TraceIdLo)), "the engine span after the await carries the request's id");
        Assert.That(_allDataRequested.TryDequeue(out var allData) && !allData, Is.True,
            "the listener asks for propagation only, so it never raises the sampling of an exporter sharing the process");
    }

    [Test]
    public async Task An_incoming_traceparent_is_the_id_recorded()
    {
        const string traceId = "0af7651916cd43dd8448eb211c80319c";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/items/7");
        request.Headers.Add("traceparent", $"00-{traceId}-b7ad6b7169203331-01");
        await _client.SendAsync(request);

        var root = ExternalRequests().Single();
        Assert.That(CausalHex(root.TraceIdHi, root.TraceIdLo), Is.EqualTo(traceId));
        Assert.That(_spans.TryDequeue(out var span) && span.Activity == traceId, Is.True);
    }

    [Test]
    public async Task Method_and_status_are_recorded_for_a_failed_post()
    {
        await _client.PostAsync("/fail/409", null);

        var root = ExternalRequests().Single();
        Assert.Multiple(() =>
        {
            Assert.That(root.Method, Is.EqualTo(ExternalRequestMethod.Post));
            Assert.That(root.Status, Is.EqualTo(409));
            Assert.That(root.Route, Is.EqualTo("/fail/{code:int}"), "the template, not the path");
        });
    }

    [Test]
    public async Task A_handler_that_throws_is_recorded_as_a_500_and_its_exception_still_surfaces()
    {
        // TestServer rethrows the pipeline's exception to the client, which is how we see it was not swallowed.
        await Assert.ThatAsync(() => _client.GetAsync("/boom"), Throws.InstanceOf<InvalidOperationException>());

        var root = ExternalRequests().Single();
        Assert.That((root.Status, root.Route), Is.EqualTo(((ushort)500, "/boom")));
    }

    [Test]
    public async Task A_websocket_session_is_not_recorded_as_a_request()
    {
        var socket = await _host.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri(_host.GetTestServer().BaseAddress, "ws"), default);
        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", default);
        socket.Dispose();

        // A request after the session, so the wait below ends on it rather than timing out: the only root recorded must be that one.
        await _client.GetAsync("/items/1");
        Assert.That(ExternalRequests().Select(r => r.Route), Is.EqualTo(new[] { "/items/{id}" }));
    }

    [Test]
    public async Task A_request_no_endpoint_matched_is_recorded_under_one_constant_route()
    {
        var response = await _client.GetAsync("/no/such/place/42");
        Assert.That((int)response.StatusCode, Is.EqualTo(404));
        Assert.That(ExternalRequests().Single().Route, Is.EqualTo(RequestCorrelationMiddleware.UnmatchedRoute), "never the raw path");
    }

    [Test]
    public void A_long_non_ascii_route_is_cut_to_whole_characters_within_the_route_budget()
    {
        // Two-byte characters and four-byte surrogate pairs, far past MaxRouteBytes of UTF-8: cut, never thrown, never inside a pair.
        var route = "/" + string.Concat(Enumerable.Repeat("é\U0001F600", 100));
        Assert.DoesNotThrow(() => ProfilerCorrelation.RecordRequest(Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp(), new CausalId(1, 2),
            ExternalRequestMethod.Get, 200, route));

        var root = ExternalRequests().Single();
        Assert.Multiple(() =>
        {
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(root.Route),
                Is.LessThanOrEqualTo(ExternalRequestEventCodec.MaxRouteBytes).And.GreaterThan(ExternalRequestEventCodec.MaxRouteBytes - 2));
            Assert.That(route, Does.StartWith(root.Route), "cut on a character boundary, so no replacement character");
        });
    }

    private ExternalRequestEventData[] ExternalRequests()
    {
        _observer.WaitFor(TraceEventKind.ExternalRequest, 1, TimeSpan.FromSeconds(5));
        return _observer.GetRecords()
            .Where(r => r.Kind == TraceEventKind.ExternalRequest)
            .Select(r => ExternalRequestEventCodec.Decode(r.Bytes))
            .ToArray();
    }

    private static string CausalHex(ulong hi, ulong lo)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, hi);
        BitConverter.TryWriteBytes(bytes[8..], lo);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
