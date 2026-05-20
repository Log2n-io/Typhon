using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;
using Typhon.Workbench.Dtos.Schema;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Schema;

/// <summary>
/// Integration tests for <see cref="Typhon.Workbench.Controllers.SchemaController"/>. Exercises both HTTP wiring
/// (auth, status codes, serialization) and the underlying <see cref="Typhon.Workbench.Schema.SchemaService"/> logic.
/// Real components are registered directly on the session's engine after session creation since the test session
/// carries no schema DLLs.
/// </summary>
[TestFixture]
public sealed class SchemaControllerTests
{
    // Two deliberately-shaped test components. WbCompA has padding between byte-sized and int-sized fields so
    // the "fields ordered by offset + gaps reveal padding" invariant is testable.
    [Component("Workbench.Test.WbCompA", 1)]
    [StructLayout(LayoutKind.Sequential)]
    public struct WbCompA
    {
        public byte Tag;        // offset 0, size 1 — padding (3 bytes) follows to align Counter
        public int Counter;     // offset 4, size 4
        public double Ratio;    // offset 8, size 8
    }

    [Component("Workbench.Test.WbCompB", 1)]
    [StructLayout(LayoutKind.Sequential)]
    public struct WbCompB
    {
        [Index] public int Key;
        public float Value;
    }

    private WorkbenchFactory _factory;
    private HttpClient _client;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private async Task<SessionDto> CreateSessionAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/sessions/file", new CreateFileSessionRequest("demo.typhon"));
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    private DatabaseEngine GetEngineForSession(Guid sessionId)
    {
        var manager = _factory.Services.GetRequiredService<SessionManager>();
        Assert.That(manager.TryGet(sessionId, out var session), Is.True, "Session not found in manager");
        var open = session as OpenSession;
        Assert.That(open, Is.Not.Null, "Session is not an OpenSession");
        return open!.Engine.Engine;
    }

    private HttpRequestMessage BuildGet(Guid sessionId, string route)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionId}/schema/{route}");
        req.Headers.Add("X-Session-Token", sessionId.ToString());
        return req;
    }

    [Test]
    public async Task ListComponents_WithoutBootstrapToken_Returns401()
    {
        using var rawClient = _factory.CreateClient(); // no bootstrap-token handler
        var sessionId = Guid.NewGuid();
        var resp = await rawClient.GetAsync($"/api/sessions/{sessionId}/schema/components");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ListComponents_WithoutSessionToken_Returns401()
    {
        var session = await CreateSessionAsync();
        // Bootstrap token attached by CreateAuthenticatedClient, but no X-Session-Token header.
        var resp = await _client.GetAsync($"/api/sessions/{session.SessionId}/schema/components");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ListComponents_ReturnsJsonArray()
    {
        var session = await CreateSessionAsync();
        var resp = await _client.SendAsync(BuildGet(session.SessionId, "components"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var summaries = JsonSerializer.Deserialize<ComponentSummaryDto[]>(
            await resp.Content.ReadAsStringAsync(), Json);
        Assert.That(summaries, Is.Not.Null);
        // A fresh demo session may register internal engine components (metadata tables etc.); we
        // don't pin to a specific count. The key assertion is that the endpoint returns a valid
        // array of well-formed DTOs.
        foreach (var s in summaries!)
        {
            Assert.That(s.TypeName, Is.Not.Null.And.Not.Empty);
            Assert.That(s.StorageSize, Is.GreaterThanOrEqualTo(0));
            Assert.That(s.FieldCount, Is.GreaterThanOrEqualTo(0));
        }
    }

    [Test]
    public async Task ListComponents_AfterRegistration_IncludesNewTypesWithRichSummary()
    {
        var session = await CreateSessionAsync();
        var engine = GetEngineForSession(session.SessionId);
        var before = engine.GetAllComponentTables().Count();

        engine.RegisterComponentFromAccessor<WbCompA>();
        engine.RegisterComponentFromAccessor<WbCompB>();

        var resp = await _client.SendAsync(BuildGet(session.SessionId, "components"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var summaries = JsonSerializer.Deserialize<ComponentSummaryDto[]>(
            await resp.Content.ReadAsStringAsync(), Json)!;

        Assert.That(summaries.Length, Is.EqualTo(before + 2), "Endpoint should report the two new components");

        var a = summaries.Single(s => s.TypeName == "Workbench.Test.WbCompA");
        Assert.That(a.FieldCount, Is.EqualTo(3), "WbCompA has 3 fields");
        Assert.That(a.StorageSize, Is.GreaterThanOrEqualTo(16), "byte + int + double ≥ 16 bytes");
        Assert.That(a.ArchetypeCount, Is.Null, "Phase 1 returns null for ArchetypeCount");
        Assert.That(a.EntityCount, Is.EqualTo(0));

        var b = summaries.Single(s => s.TypeName == "Workbench.Test.WbCompB");
        Assert.That(b.FieldCount, Is.EqualTo(2));
        Assert.That(b.IndexCount, Is.GreaterThanOrEqualTo(1), "Key has [Index]");
    }

    [Test]
    public async Task GetComponentSchema_UnknownType_Returns404()
    {
        var session = await CreateSessionAsync();
        var resp = await _client.SendAsync(BuildGet(session.SessionId, "components/Does.Not.Exist"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task GetComponentSchema_FieldsOrderedByOffset_AndGapsRevealPadding()
    {
        var session = await CreateSessionAsync();
        var engine = GetEngineForSession(session.SessionId);
        engine.RegisterComponentFromAccessor<WbCompA>();

        var resp = await _client.SendAsync(BuildGet(session.SessionId, "components/Workbench.Test.WbCompA"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var schema = JsonSerializer.Deserialize<ComponentSchemaDto>(
            await resp.Content.ReadAsStringAsync(), Json)!;

        Assert.That(schema.TypeName, Is.EqualTo("Workbench.Test.WbCompA"));
        Assert.That(schema.Fields.Length, Is.EqualTo(3));

        // Fields must be ordered by offset ascending — the Layout view depends on this.
        for (int i = 1; i < schema.Fields.Length; i++)
        {
            Assert.That(schema.Fields[i].Offset, Is.GreaterThan(schema.Fields[i - 1].Offset),
                $"Field {schema.Fields[i].Name} out of order");
        }

        // Tag at offset 0 size 1, Counter at offset ≥4 (padding gap between them is how the client
        // renders wasted bytes — we just verify the raw data exposes the gap).
        var tag = schema.Fields[0];
        var counter = schema.Fields[1];
        Assert.That(tag.Name, Is.EqualTo("Tag"));
        Assert.That(tag.Offset, Is.EqualTo(0));
        Assert.That(tag.Size, Is.EqualTo(1));
        Assert.That(counter.Offset, Is.GreaterThanOrEqualTo(tag.Offset + tag.Size + 1),
            "Layout should leave at least 1 byte of padding between byte Tag and int Counter");
    }

    [Test]
    public async Task GetComponentSchema_IndexedField_FlaggedAsIndexed()
    {
        var session = await CreateSessionAsync();
        var engine = GetEngineForSession(session.SessionId);
        engine.RegisterComponentFromAccessor<WbCompB>();

        var resp = await _client.SendAsync(BuildGet(session.SessionId, "components/Workbench.Test.WbCompB"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var schema = JsonSerializer.Deserialize<ComponentSchemaDto>(
            await resp.Content.ReadAsStringAsync(), Json)!;

        var key = schema.Fields.Single(f => f.Name == "Key");
        var value = schema.Fields.Single(f => f.Name == "Value");
        Assert.That(key.IsIndexed, Is.True);
        Assert.That(value.IsIndexed, Is.False);
    }

    // ── Phase 2 endpoints ───────────────────────────────────────────────────

    [Test]
    public async Task GetArchetypes_UnknownType_Returns404()
    {
        var session = await CreateSessionAsync();
        var resp = await _client.SendAsync(BuildGet(session.SessionId, "components/Does.Not.Exist/archetypes"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task GetArchetypes_ComponentNotInAnyArchetype_ReturnsEmptyArray()
    {
        var session = await CreateSessionAsync();
        var engine = GetEngineForSession(session.SessionId);
        engine.RegisterComponentFromAccessor<WbCompA>();

        var resp = await _client.SendAsync(BuildGet(session.SessionId, "components/Workbench.Test.WbCompA/archetypes"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var archetypes = JsonSerializer.Deserialize<ArchetypeInfoDto[]>(
            await resp.Content.ReadAsStringAsync(), Json)!;
        // No archetype is registered for WbCompA in this test session.
        Assert.That(archetypes, Is.Empty);
    }

    [Test]
    public async Task GetIndexes_ComponentWithoutIndexes_ReturnsEmpty()
    {
        var session = await CreateSessionAsync();
        var engine = GetEngineForSession(session.SessionId);
        engine.RegisterComponentFromAccessor<WbCompA>();

        var resp = await _client.SendAsync(BuildGet(session.SessionId, "components/Workbench.Test.WbCompA/indexes"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var indexes = JsonSerializer.Deserialize<IndexInfoDto[]>(
            await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(indexes, Is.Empty, "WbCompA has no [Index]-decorated fields");
    }

    [Test]
    public async Task GetIndexes_ComponentWithIndexedField_ReturnsIndexInfo()
    {
        var session = await CreateSessionAsync();
        var engine = GetEngineForSession(session.SessionId);
        engine.RegisterComponentFromAccessor<WbCompB>();

        var resp = await _client.SendAsync(BuildGet(session.SessionId, "components/Workbench.Test.WbCompB/indexes"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var indexes = JsonSerializer.Deserialize<IndexInfoDto[]>(
            await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(indexes.Length, Is.GreaterThanOrEqualTo(1));
        var keyIdx = indexes.Single(i => i.FieldName == "Key");
        Assert.That(keyIdx.AllowsMultiple, Is.False);
        Assert.That(keyIdx.IndexType, Is.EqualTo("BTree"));
        Assert.That(keyIdx.FieldOffset, Is.EqualTo(0));
    }

    [Test]
    public async Task GetIndexes_UnknownType_Returns404()
    {
        var session = await CreateSessionAsync();
        var resp = await _client.SendAsync(BuildGet(session.SessionId, "components/Does.Not.Exist/indexes"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task GetSystems_WithoutRuntime_ReturnsRuntimeHostedFalseAndEmpty()
    {
        var session = await CreateSessionAsync();
        var engine = GetEngineForSession(session.SessionId);
        engine.RegisterComponentFromAccessor<WbCompA>();

        var resp = await _client.SendAsync(BuildGet(session.SessionId, "components/Workbench.Test.WbCompA/systems"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var envelope = JsonSerializer.Deserialize<SystemRelationshipsResponseDto>(
            await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(envelope.RuntimeHosted, Is.False,
            "Workbench does not host a TyphonRuntime in Phase 2 — runtime hosting is separate post-bootstrap work");
        Assert.That(envelope.Systems, Is.Empty);
    }

    [Test]
    public async Task GetSystems_UnknownType_Returns404()
    {
        var session = await CreateSessionAsync();
        var resp = await _client.SendAsync(BuildGet(session.SessionId, "components/Does.Not.Exist/systems"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task SchemaEndpoints_OnSessionWithoutProvider_Return404_SchemaUnavailable()
    {
        // Sessions whose StaticSchemaProvider is null (live AttachSession is the canonical case — engine doesn't push
        // schema over the live attach socket today) must surface as 404 with the "schema_unavailable" ProblemDetails
        // title, not as a crash. Inject a fake session with null provider directly into the SessionManager so we
        // exercise the controller without spinning up a real attach socket.
        var manager = _factory.Services.GetRequiredService<SessionManager>();
        var fakeId = Guid.NewGuid();
        manager.Create(new FakeSchemaUnavailableSession(fakeId));

        foreach (var route in new[] { "components", "archetypes", "components/Foo", "components/Foo/indexes", "components/Foo/systems" })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{fakeId}/schema/{route}");
            req.Headers.Add("X-Session-Token", fakeId.ToString());
            var resp = await _client.SendAsync(req);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), $"route={route}");

            var body = await resp.Content.ReadAsStringAsync();
            Assert.That(body, Does.Contain("schema_unavailable"), $"route={route} body={body}");
        }
    }

    [Test]
    public async Task SchemaEndpoints_WhileTraceBuildInProgress_Return202_Accepted()
    {
        // A freshly created trace session reports IsSchemaBuilding=true until its background cache build finishes.
        // Every schema endpoint must surface that as 202 Accepted (mirrors the /profiler/metadata pattern) so the SPA
        // hooks poll quietly via refetchInterval instead of logging Query failed every time a schema panel mounts.
        // We use the IsSchemaBuilding=true / StaticSchemaProvider=null fake to exercise the controller's 202 branch
        // without racing the real cache builder.
        var manager = _factory.Services.GetRequiredService<SessionManager>();
        var fakeId = Guid.NewGuid();
        manager.Create(new FakeSchemaBuildingSession(fakeId));

        foreach (var route in new[] { "components", "archetypes", "components/Foo", "components/Foo/archetypes", "components/Foo/indexes", "components/Foo/systems" })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{fakeId}/schema/{route}");
            req.Headers.Add("X-Session-Token", fakeId.ToString());
            var resp = await _client.SendAsync(req);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), $"route={route}");
            // 202 must carry no body — customFetch keys off the status code, the SPA hook treats body-less 202 as
            // "still building" and schedules the next poll.
            var body = await resp.Content.ReadAsStringAsync();
            Assert.That(body, Is.Empty.Or.EqualTo("null"), $"route={route} expected empty body, got: {body}");
        }
    }

    /// <summary>
    /// Test fake — minimal ISession implementation with <c>StaticSchemaProvider = null</c>. Mirrors what an
    /// AttachSession produces today (live engine doesn't push schema over the wire).
    /// </summary>
    private sealed record FakeSchemaUnavailableSession(Guid Id) : ISession
    {
        public SessionKind Kind => SessionKind.Attach;
        public SessionState State => SessionState.Attached;
        public string FilePath => string.Empty;
        public Typhon.Workbench.Schema.IStaticSchemaProvider StaticSchemaProvider => null;
    }

    /// <summary>
    /// Test fake — schema provider is null AND <c>IsSchemaBuilding</c> is true. Mirrors a trace session whose
    /// background cache build is still in flight (the controller must respond with 202 Accepted, not 404).
    /// </summary>
    private sealed record FakeSchemaBuildingSession(Guid Id) : ISession
    {
        public SessionKind Kind => SessionKind.Trace;
        public SessionState State => SessionState.Trace;
        public string FilePath => string.Empty;
        public Typhon.Workbench.Schema.IStaticSchemaProvider StaticSchemaProvider => null;
        public bool IsSchemaBuilding => true;
    }
}
