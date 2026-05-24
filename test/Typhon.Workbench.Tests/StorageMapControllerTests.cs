using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Dtos.Storage;

namespace Typhon.Workbench.Tests;

// Covers the Database File Map REST surface (Module 15 Track A, A1): auth gating, the coarse-map shape, the
// total-bytes invariant (AC10), and the pyramid overview. The zero-disk-I/O invariant (AC1) is asserted at the
// engine layer (StorageIntrospectionTests.Introspection_PerformsZeroDiskReads) — StorageMapService builds the
// map exclusively from those same zero-I/O engine accessors.
[TestFixture]
public sealed class StorageMapControllerTests
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

    private async Task<T> GetAsync<T>(SessionDto session, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/dbmap/{path}");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"GET dbmap/{path}");
        return JsonSerializer.Deserialize<T>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    [Test]
    public async Task GetHealth_ValidToken_ReturnsRollup()
    {
        var session = await CreateSessionAsync();
        var dto = await GetAsync<StorageHealthDto>(session, "health");

        Assert.That(dto.DataFilePageCount, Is.GreaterThan(0), "a real database has pages");
        Assert.That(dto.UsedPageCount + dto.FreePageCount, Is.EqualTo(dto.DataFilePageCount), "used + free == total");
        Assert.That(dto.UsedPageCount, Is.GreaterThan(0), "a real database has used pages");
        Assert.That(dto.DataFileBytes, Is.GreaterThan(0L));
        Assert.That(dto.SegmentCount, Is.GreaterThan(0).And.EqualTo(dto.Segments.Length));
        Assert.That(dto.FragmentationPct, Is.InRange(0.0, 100.0));
        foreach (var s in dto.Segments)
        {
            Assert.That(s.PageCount, Is.GreaterThanOrEqualTo(0));
            Assert.That(s.OccupancyPct, Is.InRange(0.0, 100.0), $"segment {s.Id} occupancy in range");
            Assert.That(s.ReclaimableBytes, Is.GreaterThanOrEqualTo(0L));
        }
    }

    [Test]
    public async Task GetRegions_NoToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/sessions/{Guid.NewGuid()}/dbmap/regions");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task GetRegions_UnknownToken_Returns401()
    {
        var session = await CreateSessionAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/dbmap/regions");
        req.Headers.Add("X-Session-Token", Guid.NewGuid().ToString());
        var response = await _client.SendAsync(req);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task GetRegions_ValidToken_ReturnsRealMap()
    {
        var session = await CreateSessionAsync();
        var dto = await GetAsync<StorageRegionsDto>(session, "regions");

        Assert.That(dto.DataFilePageCount, Is.GreaterThan(0), "a real database has pages");
        Assert.That(dto.HilbertOrder, Is.GreaterThanOrEqualTo(0));
        Assert.That(dto.DownSampleFactor, Is.EqualTo(1), "A1 never down-samples");
        Assert.That(dto.WalBytes, Is.GreaterThanOrEqualTo(0L));
        Assert.That(dto.Segments, Is.Not.Null.And.Not.Empty, "a real database has segments");
        // AC10 — total mapped bytes equal the on-disk file size.
        Assert.That(dto.DataFileBytes, Is.EqualTo((long)dto.DataFilePageCount * 8192));
        // Hilbert grid must be large enough to hold every page.
        Assert.That(1L << (2 * dto.HilbertOrder), Is.GreaterThanOrEqualTo(dto.DataFilePageCount));
    }

    [Test]
    public async Task GetRegion_ReturnsCoarsePageDescriptors()
    {
        var session = await CreateSessionAsync();
        var regions = await GetAsync<StorageRegionsDto>(session, "regions");
        var region = await GetAsync<StorageRegionDto>(session, "region");

        Assert.That(region.PageCount, Is.EqualTo(regions.DataFilePageCount));

        var pageTypes = Convert.FromBase64String(region.PageTypes);
        var ownerIds = Convert.FromBase64String(region.OwnerSegmentIds);
        Assert.That(pageTypes.Length, Is.EqualTo(region.PageCount), "one type byte per page");
        Assert.That(ownerIds.Length, Is.EqualTo(region.PageCount * 2), "one ushort owner-id per page");

        // Page 0 is the reserved root header page; its classified type is Root (ordinal 2).
        Assert.That(pageTypes[0], Is.EqualTo((byte)2));
    }

    [Test]
    public async Task GetOverview_ReturnsPyramidLevels()
    {
        var session = await CreateSessionAsync();
        var overview = await GetAsync<StorageOverviewDto>(session, "overview");

        Assert.That(overview.Levels, Is.Not.Null.And.Not.Empty);
        Assert.That(overview.Levels[0].Level, Is.EqualTo(0));
        Assert.That(overview.Levels[0].NodeCount, Is.EqualTo(1), "level 0 is the single root node");
        foreach (var level in overview.Levels)
        {
            Assert.That(level.NodeCount, Is.EqualTo(1 << (2 * level.Level)));
            Assert.That(level.UsedCounts.Length, Is.EqualTo(level.NodeCount));
        }
    }
}
