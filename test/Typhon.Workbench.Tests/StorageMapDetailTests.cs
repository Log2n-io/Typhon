using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Dtos.Storage;
using Typhon.Workbench.Sessions;
using Typhon.Workbench.Storage;

namespace Typhon.Workbench.Tests;

// Covers the Database File Map detail-tier REST surface (Module 15 Track A, A2): the detail region tiles, the
// page / segment / chunk decodes, the field-level component decoder, and the AC3 no-full-file-scan invariant.
[TestFixture]
public sealed class StorageMapDetailTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private async Task<SessionDto> CreateSessionAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/sessions/file", new CreateFileSessionRequest("demo.typhon"));
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    private async Task<HttpResponseMessage> GetAsync(SessionDto session, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/dbmap/{path}");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        return await _client.SendAsync(req);
    }

    private async Task<T> GetOkAsync<T>(SessionDto session, string path)
    {
        var resp = await GetAsync(session, path);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"GET dbmap/{path}");
        return JsonSerializer.Deserialize<T>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    [Test]
    public async Task GetRegions_ExposesDetailTileSize()
    {
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        Assert.That(regions.DetailTileSize, Is.EqualTo(StorageMapService.DetailTileSize));
    }

    [Test]
    public async Task GetRegionDetail_ReturnsPerPageDetailBuffers()
    {
        var session = await CreateSessionAsync();
        var detail = await GetOkAsync<StorageRegionDetailDto>(session, "region/detail?node=0");

        Assert.That(detail.FirstPage, Is.EqualTo(0));
        Assert.That(detail.PageCount, Is.GreaterThan(0));
        Assert.That(detail.PageCount, Is.LessThanOrEqualTo(StorageMapService.DetailTileSize));

        Assert.That(Convert.FromBase64String(detail.FillRatio).Length, Is.EqualTo(detail.PageCount));
        Assert.That(Convert.FromBase64String(detail.ChangeRevision).Length, Is.EqualTo(detail.PageCount * 4));
        Assert.That(Convert.FromBase64String(detail.CrcStatus).Length, Is.EqualTo(detail.PageCount));
        Assert.That(Convert.FromBase64String(detail.Residency).Length, Is.EqualTo(detail.PageCount));
        Assert.That(Convert.FromBase64String(detail.ChunkUsed).Length, Is.EqualTo(detail.PageCount * 2));
        Assert.That(Convert.FromBase64String(detail.ChunkTotal).Length, Is.EqualTo(detail.PageCount * 2));
        Assert.That(detail.MaxChangeRevision, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public async Task GetRegionDetail_BeyondEofIsEmpty()
    {
        var session = await CreateSessionAsync();
        var detail = await GetOkAsync<StorageRegionDetailDto>(session, "region/detail?node=100000");
        Assert.That(detail.PageCount, Is.EqualTo(0));
    }

    [Test]
    public async Task GetPage_DecodesOccupancyRoot()
    {
        var session = await CreateSessionAsync();
        var page = await GetOkAsync<StoragePageDetailDto>(session, "page/1");

        Assert.That(page.PageIndex, Is.EqualTo(1));
        Assert.That(page.ByteOffset, Is.EqualTo(8192L));
        Assert.That(page.PageType, Is.EqualTo("Occupancy"));
        Assert.That(page.CrcStatus, Is.AnyOf("Unverified", "Verified", "Failed"));
        Assert.That(page.Residency, Is.AnyOf("OnDiskOnly", "ResidentClean", "ResidentDirty"));
    }

    [Test]
    public async Task GetPage_OutOfRangeReturns404()
    {
        var session = await CreateSessionAsync();
        var resp = await GetAsync(session, "page/999999999");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task GetSegment_ReturnsPageDirectory()
    {
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var first = regions.Segments[0];

        var segment = await GetOkAsync<StorageSegmentDetailDto>(session, $"segment/{first.Id}");

        Assert.That(segment.Id, Is.EqualTo(first.Id));
        Assert.That(segment.RootPageIndex, Is.EqualTo(first.RootPageIndex));
        Assert.That(segment.Pages, Is.Not.Empty);
        Assert.That(segment.Pages[0], Is.EqualTo(first.RootPageIndex), "the first directory entry is the root page");
    }

    [Test]
    public async Task GetSegment_UnknownIdReturns404()
    {
        var session = await CreateSessionAsync();
        var resp = await GetAsync(session, "segment/999999");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task GetChunk_ComponentSegmentDecodesFields()
    {
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var component = Array.Find(regions.Segments, s => s.Kind == "Component");
        Assert.That(component, Is.Not.Null, "the demo database has component segments");

        var chunk = await GetOkAsync<StorageChunkDto>(session, $"chunk/{component!.Id}/0");

        Assert.That(chunk.Decoder, Is.EqualTo("component"));
        Assert.That(chunk.ComponentType, Is.Not.Empty);
        Assert.That(chunk.Cells, Is.Not.Empty, "a component chunk decodes to field cells");
        Assert.That(chunk.Cells, Has.Some.Property("Kind").EqualTo("field"), "field-level decode produces field cells");
    }

    [Test]
    public async Task GetChunk_UnknownSegmentReturns404()
    {
        var session = await CreateSessionAsync();
        var resp = await GetAsync(session, "chunk/999999/0");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task GetRegionDetail_ReadsOnlyTheRequestedTile()
    {
        // AC3 — a detail-tile request is viewport-scoped: it reads at most one tile's worth of page bodies,
        // never the whole file. Asserted against the engine's PageBodyReadCount.
        var session = await CreateSessionAsync();
        var manager = _factory.Services.GetRequiredService<SessionManager>();
        Assert.That(manager.TryGet(session.SessionId, out var raw), Is.True);
        var engine = ((OpenSession)raw).Engine.Engine;
        var service = _factory.Services.GetRequiredService<StorageMapService>();

        var before = engine.PageBodyReadCount;
        var tile = service.GetRegionDetail(engine, "demo.typhon", 0);
        var delta = engine.PageBodyReadCount - before;

        Assert.That(delta, Is.GreaterThan(0), "the detail tier reads page bodies");
        Assert.That(delta, Is.LessThanOrEqualTo(StorageMapService.DetailTileSize), "a tile request never scans beyond one tile");
        Assert.That(delta, Is.LessThanOrEqualTo(tile.PageCount), "reads are bounded by the tile's page range");
    }
}
