using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Tests.Fixtures;

namespace Typhon.Workbench.Tests;

[TestFixture]
public sealed class HeartbeatStreamTests
{
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
    public async Task Heartbeat_NoToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/sessions/{Guid.NewGuid()}/heartbeat");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    [CancelAfter(20000)]
    public async Task Heartbeat_ValidToken_ReceivesPayloadWithRevisionAndMemory(CancellationToken testCt)
    {
        var createResp = await _client.PostAsJsonAsync("/api/sessions/file", new CreateFileSessionRequest("demo.typhon"));
        createResp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(
            await createResp.Content.ReadAsStringAsync(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/heartbeat");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());

        using var response = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, testCt);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/event-stream"));

        await using var stream = await response.Content.ReadAsStreamAsync(testCt);
        using var reader = new StreamReader(stream);

        var frame = await SseFrameReader.ReadFrameAsync(reader, testCt);
        Assert.That(frame, Is.Not.Null, "expected at least one SSE frame");
        Assert.That(frame.Value.EventType, Is.EqualTo("heartbeat"),
            "post-#308 wire format: heartbeat frames carry event type on the SSE event: line");
        using var doc = JsonDocument.Parse(frame.Value.Data);
        Assert.That(doc.RootElement.TryGetProperty("revision", out _), Is.True, "payload missing 'revision'");
        Assert.That(doc.RootElement.TryGetProperty("memoryMb", out _), Is.True, "payload missing 'memoryMb'");
    }
}
