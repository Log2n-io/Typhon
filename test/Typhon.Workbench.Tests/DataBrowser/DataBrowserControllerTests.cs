using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;
using Typhon.Workbench.Dtos.Data;
using Typhon.Workbench.Dtos.Schema;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.DataBrowser;

// Integration tests for DataBrowserController + DataBrowserService. A demo file session is created, then test components +
// archetype are registered and entities spawned directly on the session's engine (the demo session carries no schema DLLs),
// mirroring SchemaControllerTests. Covers the read loop, paging/cap, decode, and error mapping (AC1–7).
[Component("Workbench.Test.DbPos", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct DbPos
{
    public float X, Y, Z;
    public DbPos(float x, float y, float z) { X = x; Y = y; Z = z; }
}

[Component("Workbench.Test.DbHealth", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct DbHealth
{
    public int Current, Max;
    public DbHealth(int current, int max) { Current = current; Max = max; }
}

[Archetype(2001)]
partial class DbThing : Archetype<DbThing>
{
    public static readonly Comp<DbPos> Pos = Register<DbPos>();
    public static readonly Comp<DbHealth> Health = Register<DbHealth>();
}

// Exercises the geometric value-type decode path (AABB / point / bounding-sphere / quaternion) end-to-end.
[Component("Workbench.Test.DbBounds", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct DbBounds
{
    public AABB2F Box;
    public Point2F Center;
    public BSphere2F Sphere;
    public QuaternionF Rot;

    public DbBounds(AABB2F box, Point2F center, BSphere2F sphere, QuaternionF rot)
    {
        Box = box;
        Center = center;
        Sphere = sphere;
        Rot = rot;
    }
}

[Archetype(2002)]
partial class DbShape : Archetype<DbShape>
{
    public static readonly Comp<DbBounds> Bounds = Register<DbBounds>();
}

[TestFixture]
[NonParallelizable]
public sealed class DataBrowserControllerTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    // The [Archetype(2001)] attribute id IS the runtime ArchetypeId (see EntitySpawnTests). Engine internals
    // (Archetype<T>.Metadata) aren't visible to this test assembly, so use the literal.
    private const string ArchId = "2001";
    private const string ShapeArchId = "2002";

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private async Task<Guid> CreateEntityEngineSessionAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/sessions/file", new CreateFileSessionRequest("demo.typhon"));
        resp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json)!;

        var manager = _factory.Services.GetRequiredService<SessionManager>();
        manager.TryGet(session.SessionId, out var s);
        var engine = ((OpenSession)s).Engine.Engine;
        engine.RegisterComponentFromAccessor<DbPos>();
        engine.RegisterComponentFromAccessor<DbHealth>();
        engine.RegisterComponentFromAccessor<DbBounds>();
        engine.InitializeArchetypes();
        return session.SessionId;
    }

    private async Task<HttpResponseMessage> GetAsync(Guid sessionId, string route)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionId}/data/{route}");
        req.Headers.Add("X-Session-Token", sessionId.ToString());
        return await _client.SendAsync(req);
    }

    private static void SpawnThings(WorkbenchFactory factory, Guid sessionId, int count, DbPos pos, DbHealth? health = null)
    {
        var manager = factory.Services.GetRequiredService<SessionManager>();
        manager.TryGet(sessionId, out var s);
        var engine = ((OpenSession)s).Engine.Engine;
        using var tx = engine.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            if (health is { } hp)
            {
                tx.Spawn<DbThing>(DbThing.Pos.Set(in pos), DbThing.Health.Set(in hp));
            }
            else
            {
                tx.Spawn<DbThing>(DbThing.Pos.Set(in pos));
            }
        }
        tx.Commit();
    }

    private static void SpawnShape(WorkbenchFactory factory, Guid sessionId, in DbBounds bounds)
    {
        var manager = factory.Services.GetRequiredService<SessionManager>();
        manager.TryGet(sessionId, out var s);
        var engine = ((OpenSession)s).Engine.Engine;
        using var tx = engine.CreateQuickTransaction();
        tx.Spawn<DbShape>(DbShape.Bounds.Set(in bounds));
        tx.Commit();
    }

    // ── Auth / errors ───────────────────────────────────────────────────────

    [Test]
    public async Task Entities_WithoutBootstrapToken_Returns401()
    {
        using var raw = _factory.CreateClient();
        var resp = await raw.GetAsync($"/api/sessions/{Guid.NewGuid()}/data/archetypes/1/entities");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Entities_NonOpenSession_Returns404_DataUnavailable()
    {
        var manager = _factory.Services.GetRequiredService<SessionManager>();
        var fakeId = Guid.NewGuid();
        manager.Create(new FakeAttachSession(fakeId));

        var resp = await GetAsync(fakeId, "archetypes/1/entities");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await resp.Content.ReadAsStringAsync(), Does.Contain("data_unavailable"));
    }

    [Test]
    public async Task Entities_UnknownArchetype_Returns404()
    {
        var sessionId = await CreateEntityEngineSessionAsync();
        var resp = await GetAsync(sessionId, "archetypes/4094/entities");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task EntityDetail_UnknownEntity_Returns404()
    {
        var sessionId = await CreateEntityEngineSessionAsync();
        var resp = await GetAsync(sessionId, $"archetypes/{ArchId}/entities/999999999");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ── Read loop ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Entities_EmptyArchetype_ReturnsEmptyPage_ZeroTotal()
    {
        var sessionId = await CreateEntityEngineSessionAsync();
        var resp = await GetAsync(sessionId, $"archetypes/{ArchId}/entities");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var page = JsonSerializer.Deserialize<EntityPageDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(page.TotalCount, Is.EqualTo(0));
        Assert.That(page.Entities, Is.Empty);
        Assert.That(page.HasMore, Is.False);
        Assert.That(page.ArchetypeId, Is.EqualTo(ArchId));
    }

    [Test]
    public async Task Entities_ReturnsAllSpawned_WithTotalCount()
    {
        var sessionId = await CreateEntityEngineSessionAsync();
        SpawnThings(_factory, sessionId, 25, new DbPos(1, 2, 3), new DbHealth(10, 10));

        var resp = await GetAsync(sessionId, $"archetypes/{ArchId}/entities");
        var page = JsonSerializer.Deserialize<EntityPageDto>(await resp.Content.ReadAsStringAsync(), Json)!;

        Assert.That(page.TotalCount, Is.EqualTo(25));
        Assert.That(page.Entities.Length, Is.EqualTo(25));
        Assert.That(page.Offset, Is.EqualTo(0));
        Assert.That(page.HasMore, Is.False);
        // Entity ids are unique decimal strings.
        var ids = page.Entities.Select(e => e.EntityId).ToHashSet();
        Assert.That(ids, Has.Count.EqualTo(25));
    }

    [Test]
    public async Task Entities_OffsetLimit_PagesAcrossSnapshot()
    {
        var sessionId = await CreateEntityEngineSessionAsync();
        SpawnThings(_factory, sessionId, 50, new DbPos(0, 0, 0));

        var p0 = JsonSerializer.Deserialize<EntityPageDto>(
            await (await GetAsync(sessionId, $"archetypes/{ArchId}/entities?offset=0&limit=20")).Content.ReadAsStringAsync(), Json)!;
        var p1 = JsonSerializer.Deserialize<EntityPageDto>(
            await (await GetAsync(sessionId, $"archetypes/{ArchId}/entities?offset=20&limit=20")).Content.ReadAsStringAsync(), Json)!;
        var p2 = JsonSerializer.Deserialize<EntityPageDto>(
            await (await GetAsync(sessionId, $"archetypes/{ArchId}/entities?offset=40&limit=20")).Content.ReadAsStringAsync(), Json)!;

        Assert.That(p0.Entities.Length, Is.EqualTo(20));
        Assert.That(p0.HasMore, Is.True);
        Assert.That(p1.Entities.Length, Is.EqualTo(20));
        Assert.That(p2.Entities.Length, Is.EqualTo(10));
        Assert.That(p2.HasMore, Is.False);

        // Pages are disjoint and cover the whole snapshot (stable order).
        var all = p0.Entities.Concat(p1.Entities).Concat(p2.Entities).Select(e => e.EntityId).ToHashSet();
        Assert.That(all, Has.Count.EqualTo(50));
    }

    [Test]
    public async Task EntityDetail_ReturnsComponents_DecodedFields_AndEnabledState()
    {
        var sessionId = await CreateEntityEngineSessionAsync();
        // Only Pos provided → Health is disabled but still present.
        SpawnThings(_factory, sessionId, 1, new DbPos(5, 6, 7));

        var page = JsonSerializer.Deserialize<EntityPageDto>(
            await (await GetAsync(sessionId, $"archetypes/{ArchId}/entities")).Content.ReadAsStringAsync(), Json)!;
        var entityId = page.Entities[0].EntityId;

        var detail = JsonSerializer.Deserialize<EntityDetailDto>(
            await (await GetAsync(sessionId, $"archetypes/{ArchId}/entities/{entityId}")).Content.ReadAsStringAsync(), Json)!;

        Assert.That(detail.EntityId, Is.EqualTo(entityId));
        Assert.That(detail.Components.Length, Is.EqualTo(2));

        var pos = detail.Components.Single(c => c.TypeName == "Workbench.Test.DbPos");
        var health = detail.Components.Single(c => c.TypeName == "Workbench.Test.DbHealth");

        Assert.That(pos.Enabled, Is.True);
        Assert.That(health.Enabled, Is.False, "Health was not set at spawn → disabled");

        // Pos fields are ordered by offset: X, Y, Z.
        Assert.That(((JsonElement)pos.Fields[0].Value).GetSingle(), Is.EqualTo(5f));
        Assert.That(((JsonElement)pos.Fields[1].Value).GetSingle(), Is.EqualTo(6f));
        Assert.That(((JsonElement)pos.Fields[2].Value).GetSingle(), Is.EqualTo(7f));
        // Raw hex is always present.
        Assert.That(pos.Fields[0].Raw, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task MultiThousandEntities_DecodeCorrect_AndLimitCapEnforced()
    {
        var sessionId = await CreateEntityEngineSessionAsync();
        SpawnThings(_factory, sessionId, 1200, new DbPos(11, 22, 33), new DbHealth(7, 9));

        // limit is capped at 1000 even when the client asks for more.
        var page = JsonSerializer.Deserialize<EntityPageDto>(
            await (await GetAsync(sessionId, $"archetypes/{ArchId}/entities?offset=0&limit=100000")).Content.ReadAsStringAsync(), Json)!;
        Assert.That(page.TotalCount, Is.EqualTo(1200));
        Assert.That(page.Entities.Length, Is.EqualTo(1000));
        Assert.That(page.HasMore, Is.True);

        // Decode is correct at scale: a sampled entity round-trips its values.
        var detail = JsonSerializer.Deserialize<EntityDetailDto>(
            await (await GetAsync(sessionId, $"archetypes/{ArchId}/entities/{page.Entities[500].EntityId}")).Content.ReadAsStringAsync(), Json)!;
        var pos = detail.Components.Single(c => c.TypeName == "Workbench.Test.DbPos");
        var health = detail.Components.Single(c => c.TypeName == "Workbench.Test.DbHealth");
        Assert.That(((JsonElement)pos.Fields[0].Value).GetSingle(), Is.EqualTo(11f));
        Assert.That(((JsonElement)health.Fields[0].Value).GetInt32(), Is.EqualTo(7));
        Assert.That(((JsonElement)health.Fields[1].Value).GetInt32(), Is.EqualTo(9));
    }

    [Test]
    public async Task EntityDetail_GeometricFields_DecodeToReadableStrings()
    {
        var sessionId = await CreateEntityEngineSessionAsync();
        var bounds = new DbBounds(
            new AABB2F { MinX = 1, MinY = 2, MaxX = 3, MaxY = 4 },
            new Point2F { X = 5, Y = 6 },
            new BSphere2F { CenterX = 7, CenterY = 8, Radius = 9 },
            new QuaternionF { X = 0, Y = 1, Z = 0, W = 1 });
        SpawnShape(_factory, sessionId, in bounds);

        var page = JsonSerializer.Deserialize<EntityPageDto>(
            await (await GetAsync(sessionId, $"archetypes/{ShapeArchId}/entities")).Content.ReadAsStringAsync(), Json)!;
        var entityId = page.Entities[0].EntityId;

        var detail = JsonSerializer.Deserialize<EntityDetailDto>(
            await (await GetAsync(sessionId, $"archetypes/{ShapeArchId}/entities/{entityId}")).Content.ReadAsStringAsync(), Json)!;

        var comp = detail.Components.Single(c => c.TypeName == "Workbench.Test.DbBounds");
        // Fields are ordered by offset: Box (AABB2F), Center (Point2F), Sphere (BSphere2F), Rot (QuaternionF).
        Assert.That(((JsonElement)comp.Fields[0].Value).GetString(), Is.EqualTo("min(1, 2)\nmax(3, 4)"));
        Assert.That(((JsonElement)comp.Fields[1].Value).GetString(), Is.EqualTo("(5, 6)"));
        Assert.That(((JsonElement)comp.Fields[2].Value).GetString(), Is.EqualTo("center(7, 8) r=9"));
        Assert.That(((JsonElement)comp.Fields[3].Value).GetString(), Is.EqualTo("(0, 1, 0, 1)"));
    }

    [Test]
    public async Task Entities_WithPreview_ReturnsDecodedColumnValues()
    {
        var sessionId = await CreateEntityEngineSessionAsync();
        SpawnThings(_factory, sessionId, 1, new DbPos(5, 6, 7), new DbHealth(75, 100));

        var xFieldId = await GetFieldIdAsync(sessionId, "Workbench.Test.DbPos", "X");
        var curFieldId = await GetFieldIdAsync(sessionId, "Workbench.Test.DbHealth", "Current");
        var preview = Uri.EscapeDataString($"Workbench.Test.DbPos:{xFieldId},Workbench.Test.DbHealth:{curFieldId}");

        var resp = await GetAsync(sessionId, $"archetypes/{ArchId}/entities?preview={preview}");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var page = JsonSerializer.Deserialize<EntityPageDto>(await resp.Content.ReadAsStringAsync(), Json)!;

        Assert.That(page.Entities, Has.Length.EqualTo(1));
        var cols = page.Entities[0].Preview;
        Assert.That(cols, Has.Length.EqualTo(2), "two preview columns requested, in request order");
        Assert.That(((JsonElement)cols[0].Value).GetSingle(), Is.EqualTo(5f), "DbPos.X");
        Assert.That(((JsonElement)cols[1].Value).GetInt32(), Is.EqualTo(75), "DbHealth.Current");
    }

    private async Task<int> GetFieldIdAsync(Guid sessionId, string typeName, string fieldName)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionId}/schema/components/{typeName}");
        req.Headers.Add("X-Session-Token", sessionId.ToString());
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var schema = JsonSerializer.Deserialize<ComponentSchemaDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        return schema.Fields.Single(f => f.Name == fieldName).FieldId;
    }

    /// <summary>Test fake — a non-Open session (no in-process engine), so the Data Browser must 404 with data_unavailable.</summary>
    private sealed record FakeAttachSession(Guid Id) : ISession
    {
        public SessionKind Kind => SessionKind.Attach;
        public SessionState State => SessionState.Attached;
        public string FilePath => string.Empty;
        public Typhon.Workbench.Schema.IStaticSchemaProvider StaticSchemaProvider => null;
    }
}
