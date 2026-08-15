using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// P5 — the client's test for "can this paused database be watched?" is <see cref="SessionDto.ProfilerEndpoint"/>.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is read from the holder's <c>db.lock</c>, so a non-null value means the process holding this database
/// is advertising a profiler port. Before P5 that fact lived only on <c>DatabaseHolder.IsWatchable</c>, server-side,
/// which is why the paused banner could not offer to watch anything.
/// </para>
/// <para>
/// These register a session directly with <see cref="SessionManager.Create"/> rather than driving a real pause: the
/// mapping under test reads <c>PausedBy</c>, and a genuine pause needs a second process holding a real database — which
/// is what makes <c>DatabasePauseTests</c> heavy enough to be quarantined (#811). The paused <c>OpenSession</c>
/// constructor takes the holder directly and opens no engine, so the DTO can be exercised end-to-end through the real
/// endpoint for the cost of a dictionary insert.
/// </para>
/// </remarks>
[TestFixture]
public sealed class WatchableSessionDtoTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private WorkbenchFactory _factory;
    private HttpClient _client;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private OpenSession RegisterPaused(string profilerEndpoint)
    {
        var session = new OpenSession(
            Guid.NewGuid(),
            @"C:\nowhere\world-shard.typhon",
            new DatabaseHolder(Environment.ProcessId, Environment.MachineName, DateTimeOffset.UtcNow, profilerEndpoint),
            []);
        _factory.Services.GetRequiredService<SessionManager>().Create(session);
        return session;
    }

    private async Task<SessionDto> GetDtoAsync(Guid id)
    {
        // Session-scoped routes are gated by RequireSession on top of the bootstrap token: the per-session token must
        // match the id in the route, so session A's token cannot address session B.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{id}");
        req.Headers.Add("X-Session-Token", id.ToString());
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json);
    }

    [Test]
    public async Task PausedByAHolderAdvertisingAPort_ReportsTheEndpoint()
    {
        var session = RegisterPaused("localhost:9100");

        var dto = await GetDtoAsync(session.Id);
        Assert.Multiple(() =>
        {
            Assert.That(dto.IsPaused, Is.True, "the session must still present as paused");
            Assert.That(dto.ProfilerEndpoint, Is.EqualTo("localhost:9100"), "the client offers 'Watch live' on exactly this field");
            Assert.That(dto.IsWatchingLive, Is.False, "advertising a port is not the same as watching it");
        });
    }

    /// <summary>
    /// The ordinary pause: something holds the database but is not a Typhon app with a live port — an editor, a backup,
    /// an older build. Offering to watch it would produce a connection refused, so the field must stay null.
    /// </summary>
    [Test]
    public async Task PausedByAHolderWithNoPort_ReportsNoEndpoint()
    {
        var session = RegisterPaused(null);

        var dto = await GetDtoAsync(session.Id);
        Assert.Multiple(() =>
        {
            Assert.That(dto.IsPaused, Is.True);
            Assert.That(dto.ProfilerEndpoint, Is.Null);
            Assert.That(dto.IsWatchingLive, Is.False);
        });
    }

    /// <summary>
    /// A blank endpoint is the same as none. `DatabaseLockFile` omits the property when null, but a hand-edited or
    /// truncated lock can still yield whitespace, and `IsWatchable` already treats that as unwatchable.
    /// </summary>
    [Test]
    public async Task PausedByAHolderWithABlankPort_ReportsItAsUnwatchable()
    {
        var session = RegisterPaused("   ");

        var dto = await GetDtoAsync(session.Id);
        Assert.That(session.PausedBy.IsWatchable, Is.False, "blank is not an endpoint");
        Assert.That(string.IsNullOrWhiteSpace(dto.ProfilerEndpoint), Is.True);
    }

    /// <summary>
    /// The fields are applied in `ToDto` for every session, so a non-Open session must map cleanly rather than throw on
    /// the cast — an attach session has no holder by construction.
    /// </summary>
    [Test]
    public async Task AnAttachSession_ReportsNeitherEndpointNorWatching()
    {
        await using var server = new Workbench.Fixtures.MockTcpProfilerServer();
        server.Start();

        var resp = await _client.PostAsJsonAsync(
            "/api/sessions/attach", new CreateAttachSessionRequest($"127.0.0.1:{server.Port}"));
        resp.EnsureSuccessStatusCode();
        var created = JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json);

        var dto = await GetDtoAsync(created.SessionId);
        Assert.Multiple(() =>
        {
            Assert.That(dto.ProfilerEndpoint, Is.Null);
            Assert.That(dto.IsWatchingLive, Is.False);
        });
    }
}
