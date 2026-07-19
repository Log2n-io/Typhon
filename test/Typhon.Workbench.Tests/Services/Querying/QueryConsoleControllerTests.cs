using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;
using Typhon.Workbench.Dtos.Query;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Services.Querying;

/// <summary>
/// Integration tests for <see cref="Typhon.Workbench.Controllers.QueryConsoleController"/> +
/// <see cref="Typhon.Workbench.Services.Querying.QueryConsoleService"/> — exercises the full chip-mode loop end-to-end:
/// HTTP → controller → service → DSL parser → QuerySpecCompiler → EcsQuery → row materialisation. These also stand in
/// for AC-5's deferred compiler integration tests (the compiler's reflection-based stage emission needs a real engine,
/// and <see cref="WorkbenchFactory"/> is the natural harness).
/// </summary>
[Component("Workbench.Test.QCompA", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct QCompA
{
    [Index] public int Level;
    [Index(AllowMultiple = true)] public int Faction;
    public float Score;        // intentionally non-indexed — exercises the invalid_field rejection path
}

[Archetype(3001)]
partial class QArch : Archetype<QArch>
{
    public static readonly Comp<QCompA> A = Register<QCompA>();
}

[TestFixture]
[NonParallelizable]
public sealed class QueryConsoleControllerTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        // Force the archetype's static ctor so ArchetypeRegistry picks it up before any session creation runs.
    }

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private async Task<Guid> CreateSessionWithDataAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/sessions/file", new CreateFileSessionRequest("demo.typhon"));
        resp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json)!;

        var manager = _factory.Services.GetRequiredService<SessionManager>();
        manager.TryGet(session.SessionId, out var s);
        var engine = ((OpenSession)s).Engine.Engine;
        engine.RegisterComponentFromAccessor<QCompA>();
        engine.InitializeArchetypes();

        // Spawn a known mix: 10 entities at Level=10..19, Faction = 1 or 2 alternating, Score = 5.0.
        using var tx = engine.CreateQuickTransaction();
        for (var i = 0; i < 10; i++)
        {
            tx.Spawn<QArch>(QArch.A.Set(new QCompA { Level = 10 + i, Faction = (i % 2) + 1, Score = 5.0f }));
        }
        tx.Commit();

        return session.SessionId;
    }

    private async Task<HttpResponseMessage> PostAsync<T>(Guid sessionId, string subRoute, T body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId}/query/{subRoute}")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("X-Session-Token", sessionId.ToString());
        return await _client.SendAsync(req);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Auth
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Plan_WithoutBootstrapToken_Returns401()
    {
        using var raw = _factory.CreateClient();
        var resp = await raw.PostAsJsonAsync($"/api/sessions/{Guid.NewGuid()}/query/plan", new QueryPlanRequest("FROM X"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // /query/parse — round-trip
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Parse_ValidDsl_ReturnsSpecAndNoErrors()
    {
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "parse", new QueryParseRequest("FROM QArch WHERE QCompA.Level >= 15"));
        resp.EnsureSuccessStatusCode();
        var body = JsonSerializer.Deserialize<QueryParseResponse>(await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(body.Errors, Is.Empty);
        Assert.That(body.Spec.Archetype, Is.EqualTo("QArch"));
    }

    [Test]
    public async Task Parse_InvalidDsl_ReturnsSpecAndDiagnostics()
    {
        // /parse never fails the HTTP request on syntax — it returns errors[] (chip-mode debounce friendly).
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "parse", new QueryParseRequest("FROM QArch WHERE QCompA.Level >="));
        resp.EnsureSuccessStatusCode();
        var body = JsonSerializer.Deserialize<QueryParseResponse>(await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(body.Errors, Is.Not.Empty);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // /query/plan — cost estimate
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Plan_ValidQuery_ReturnsEstimates()
    {
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "plan", new QueryPlanRequest("FROM QArch WHERE QCompA.Level >= 15"));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"body: {body}");
        var plan = JsonSerializer.Deserialize<QueryPlanDto>(body, Json)!;
        Assert.That(plan.ArchetypesScanned, Is.EqualTo(1));
        Assert.That(plan.EstimatedTotalEntities, Is.GreaterThanOrEqualTo(1));   // selectivity heuristic gives ~30% of 10
        Assert.That(plan.EstimatedPagesRead, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task Plan_InvalidSyntax_Returns400_InvalidQuerySyntax()
    {
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "plan", new QueryPlanRequest("WHERE QCompA.Level >= 15"));   // missing FROM
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await resp.Content.ReadAsStringAsync(), Does.Contain("invalid_query_syntax"));
    }

    [Test]
    public async Task Plan_ArchetypeByNumericId_Succeeds()
    {
        // The Workbench schema browser shows archetypes as "#<id>" — users expect to paste that into FROM.
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "plan", new QueryPlanRequest("FROM #3001 WHERE QCompA.Level >= 15"));
        resp.EnsureSuccessStatusCode();
    }

    [Test]
    public async Task Plan_ArchetypeByBareNumericId_Succeeds()
    {
        // Bare numeric id without the '#' is the forgiving alternative.
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "plan", new QueryPlanRequest("FROM 3001 WHERE QCompA.Level >= 15"));
        resp.EnsureSuccessStatusCode();
    }

    [Test]
    public async Task Plan_UnknownArchetypeId_Returns400_UnknownArchetype()
    {
        // 65535 is well above any test fixture id — should surface unknown_archetype, not crash.
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "plan", new QueryPlanRequest("FROM #65535"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await resp.Content.ReadAsStringAsync(), Does.Contain("unknown_archetype"));
    }

    [Test]
    public async Task Plan_UnknownArchetype_Returns400_UnknownArchetype()
    {
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "plan", new QueryPlanRequest("FROM DoesNotExist"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await resp.Content.ReadAsStringAsync(), Does.Contain("unknown_archetype"));
    }

    [Test]
    public async Task Plan_NonIndexedField_Returns400_InvalidField()
    {
        var sessionId = await CreateSessionWithDataAsync();
        // QCompA.Score is non-indexed — engine rejects, compiler should pre-validate with a clean message.
        var resp = await PostAsync(sessionId, "plan", new QueryPlanRequest("FROM QArch WHERE QCompA.Score >= 1"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await resp.Content.ReadAsStringAsync(), Does.Contain("invalid_field"));
    }

    [Test]
    public async Task Plan_NavigateClause_Returns400_NotSupported()
    {
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "plan", new QueryPlanRequest("FROM QArch NAVIGATE Faction -> QCompA"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await resp.Content.ReadAsStringAsync(), Does.Contain("navigate_not_supported"));
    }

    [Test]
    public async Task Plan_NonHeadRevision_Returns400_UnsupportedRevisionKind()
    {
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "plan", new QueryPlanRequest("FROM QArch AT REVISION 123"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await resp.Content.ReadAsStringAsync(), Does.Contain("unsupported_revision_kind"));
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // /query/execute — run + materialize rows
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Execute_NoWhere_ReturnsAllRows()
    {
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "execute",
            new QueryExecuteRequest("FROM QArch", null, 0, 100));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"body: {body}");
        var result = JsonSerializer.Deserialize<QueryResultDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(result.Rows.Length, Is.EqualTo(10));
        Assert.That(result.TotalCountEstimate, Is.EqualTo(10));
        Assert.That(result.HasMore, Is.False);
    }

    [Test]
    public async Task Execute_WhereGreaterThan_FiltersCorrectly()
    {
        // Level = 10..19; >= 15 keeps 15,16,17,18,19 → 5 entities.
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "execute",
            new QueryExecuteRequest("FROM QArch WHERE QCompA.Level >= 15", null, 0, 100));
        resp.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<QueryResultDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(result.Rows.Length, Is.EqualTo(5));
    }

    [Test]
    public async Task Execute_WhereEquality_FiltersCorrectly()
    {
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "execute",
            new QueryExecuteRequest("FROM QArch WHERE QCompA.Faction == 1", null, 0, 100));
        resp.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<QueryResultDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        // 5 odd-index entities have Faction=1 (i=0,2,4,6,8 → faction = (0%2)+1=1).
        Assert.That(result.Rows.Length, Is.EqualTo(5));
    }

    [Test]
    public async Task Execute_ReturnsResolvedTsnAndExecutionTime()
    {
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "execute",
            new QueryExecuteRequest("FROM QArch", null, 0, 100));
        resp.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<QueryResultDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(result.ResolvedRevisionTsn, Is.GreaterThan(0));
        Assert.That(result.ExecutionWallNs, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public async Task Execute_RowsCarryEntityIdAsDecimalString()
    {
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "execute",
            new QueryExecuteRequest("FROM QArch", null, 0, 100));
        resp.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<QueryResultDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        // Every row should have a non-empty EntityId parseable as ulong (matches Data Browser's convention).
        foreach (var row in result.Rows)
        {
            Assert.That(ulong.TryParse(row.EntityId, out _), Is.True,
                $"EntityId '{row.EntityId}' is not a decimal ulong string.");
        }
    }

    [Test]
    public async Task Execute_Paged_LimitsRowsAndReportsHasMore()
    {
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "execute",
            new QueryExecuteRequest("FROM QArch", null, PageOffset: 0, PageSize: 3));
        resp.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<QueryResultDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(result.Rows.Length, Is.EqualTo(3));
        Assert.That(result.HasMore, Is.True);
        Assert.That(result.TotalCountEstimate, Is.EqualTo(10));
        Assert.That(result.Warnings, Is.Not.Empty);     // truncation warning surfaced
    }

    [Test]
    public async Task Execute_RowsContainCellsForProjectedComponent()
    {
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "execute",
            new QueryExecuteRequest("FROM QArch WHERE QCompA.Level >= 15", null, 0, 100));
        resp.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<QueryResultDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(result.Rows, Is.Not.Empty);
        // Each row should carry one cell per field of QCompA (Level, Faction, Score = 3 fields).
        var firstRow = result.Rows[0];
        Assert.That(firstRow.Cells.Length, Is.EqualTo(3));
        foreach (var cell in firstRow.Cells)
        {
            Assert.That(cell.TypeName, Is.EqualTo("Workbench.Test.QCompA"));
        }
    }

    [Test]
    public async Task Execute_CellsCarryFieldNames_NotJustIds()
    {
        // Regression: prior to the FieldName addition the client rendered "Player.0" / "Player.1" as column
        // headers (the raw field id) because cells didn't carry a human name. The server has the Field object
        // when decoding, so populating FieldName is free + lets the grid render proper headers without a
        // separate schema round-trip.
        var sessionId = await CreateSessionWithDataAsync();
        var resp = await PostAsync(sessionId, "execute",
            new QueryExecuteRequest("FROM QArch WHERE QCompA.Level >= 15", null, 0, 100));
        resp.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<QueryResultDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(result.Rows, Is.Not.Empty);

        var firstRow = result.Rows[0];
        var fieldNames = firstRow.Cells.Select(c => c.FieldName).ToArray();
        Assert.That(fieldNames, Is.EquivalentTo(new[] { "Level", "Faction", "Score" }));
        // Each FieldName non-empty + populated for every cell.
        foreach (var c in firstRow.Cells)
        {
            Assert.That(c.FieldName, Is.Not.Null.And.Not.Empty);
        }
    }
}
