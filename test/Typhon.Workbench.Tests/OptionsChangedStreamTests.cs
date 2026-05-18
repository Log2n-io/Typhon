using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NUnit.Framework;
using Typhon.Workbench.Hosting;
using Typhon.Workbench.Tests.Fixtures;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Covers <see cref="Typhon.Workbench.Streams.OptionsChangedStream"/> — the SSE channel behind
/// <c>GET /api/options/stream</c>. The regression test guards the <see cref="System.Threading.PeriodicTimer"/>
/// keepalive bug: <c>WaitForNextTickAsync</c> permits only one in-flight consumer, so re-issuing it
/// every loop iteration threw <see cref="System.InvalidOperationException"/> right after the first
/// frame — killing the stream before any subsequent options change could be delivered.
/// </summary>
[TestFixture]
public sealed class OptionsChangedStreamTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private WorkbenchFactory _factory;
    private HttpClient _client;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
        _client.Timeout = TimeSpan.FromSeconds(30);
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    [CancelAfter(20000)]
    public async Task Stream_EmitsInitialSnapshotAsTypedEvent(CancellationToken testCt)
    {
        using var response = await _client.GetAsync(
            "/api/options/stream", HttpCompletionOption.ResponseHeadersRead, testCt);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/event-stream"));

        await using var stream = await response.Content.ReadAsStreamAsync(testCt);
        using var reader = new StreamReader(stream);

        var frame = await SseFrameReader.ReadFrameAsync(reader, testCt);
        Assert.That(frame, Is.Not.Null, "expected the initial options snapshot frame");
        Assert.That(frame.Value.EventType, Is.EqualTo("options-changed"));
        var opts = JsonSerializer.Deserialize<WorkbenchOptions>(frame.Value.Data, JsonOpts);
        Assert.That(opts, Is.Not.Null);
        Assert.That(opts.Editor.Kind, Is.EqualTo(EditorKind.VsCode), "fresh store should snapshot defaults");
    }

    /// <summary>
    /// Regression: prior to the PeriodicTimer fix the handler threw on the second loop iteration
    /// (the initial snapshot pre-loads the channel, so iteration 1 always wins on the read task,
    /// then iteration 2 re-entered <c>WaitForNextTickAsync</c> while the first call was still
    /// pending). The connection died after the snapshot — a subsequent PATCH was never delivered.
    /// </summary>
    [Test]
    [CancelAfter(20000)]
    public async Task Stream_DeliversChangeAfterInitialSnapshot(CancellationToken testCt)
    {
        using var response = await _client.GetAsync(
            "/api/options/stream", HttpCompletionOption.ResponseHeadersRead, testCt);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await using var stream = await response.Content.ReadAsStreamAsync(testCt);
        using var reader = new StreamReader(stream);

        // Frame 1: initial snapshot. Receiving it proves the handler has subscribed its listener,
        // so the PATCH below cannot race ahead of the subscription.
        var snapshot = await SseFrameReader.ReadFrameAsync(reader, testCt);
        Assert.That(snapshot, Is.Not.Null, "expected the initial options snapshot frame");

        // Mutate options out-of-band on the same factory — fires OptionsStore.OptionsChanged.
        var patch = await _client.PatchAsJsonAsync(
            "/api/options/editor",
            new EditorOptions { Kind = EditorKind.Rider, CustomCommand = "rider --line {line} {file}" },
            JsonOpts,
            testCt);
        Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Frame 2: the change event. With the bug the stream was already dead here and this
        // returns null (EOF). With the fix the keepalive task simply stays pending and the read
        // task delivers the new snapshot.
        var change = await SseFrameReader.ReadFrameAsync(reader, testCt);
        Assert.That(change, Is.Not.Null, "stream closed after the first frame — PeriodicTimer regression");
        Assert.That(change.Value.EventType, Is.EqualTo("options-changed"));
        var opts = JsonSerializer.Deserialize<WorkbenchOptions>(change.Value.Data, JsonOpts);
        Assert.That(opts.Editor.Kind, Is.EqualTo(EditorKind.Rider), "change event missing the PATCHed value");
    }
}
