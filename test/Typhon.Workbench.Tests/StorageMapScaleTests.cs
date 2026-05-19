using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Dtos.Storage;
using Typhon.Workbench.Storage;

namespace Typhon.Workbench.Tests;

// Covers the Database File Map down-sampling + approximate-mode scale path (Module 15 Track A, A4 — §5.5). The
// pure factor / aggregation functions are tested directly; the end-to-end down-sampled response is exercised by
// lowering the coarse-cell budget so a normal fixture database triggers down-sampling — no multi-GB fixture.
[TestFixture]
public sealed class StorageMapScaleTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Test]
    public void CellCountFor_RoundsUp()
    {
        Assert.That(StorageMapService.CellCountFor(10, 4), Is.EqualTo(3));
        Assert.That(StorageMapService.CellCountFor(16, 4), Is.EqualTo(4));
        Assert.That(StorageMapService.CellCountFor(0, 4), Is.EqualTo(0));
    }

    [Test]
    public void DownSampleFactorFor_StaysExactBelowBudget()
    {
        Assert.That(StorageMapService.DownSampleFactorFor(1000, 1 << 20), Is.EqualTo(1));
        Assert.That(StorageMapService.DownSampleFactorFor(1 << 20, 1 << 20), Is.EqualTo(1));
    }

    [Test]
    public void DownSampleFactorFor_EscalatesByPowersOfFour()
    {
        // Just past the budget → factor 4 (cell count then fits); far past → 16.
        Assert.That(StorageMapService.DownSampleFactorFor((1 << 20) + 1, 1 << 20), Is.EqualTo(4));
        Assert.That(StorageMapService.DownSampleFactorFor((1 << 22) + 1, 1 << 20), Is.EqualTo(16));
    }

    [Test]
    public void DownSampleArrays_PicksDominantTypeAndOwner()
    {
        // 8 pages, factor 4 → 2 cells. Cell 0: 3×Component + 1×Free → Component, owner 7 (covers 3 of 4).
        // Cell 1: 3×Index + 1×Free → Index (dominant non-free), owner 9 (covers 3 of 4).
        var pageType = new[]
        {
            StoragePageType.Component, StoragePageType.Component, StoragePageType.Component, StoragePageType.Free,
            StoragePageType.Index, StoragePageType.Index, StoragePageType.Index, StoragePageType.Free,
        };
        var owner = new ushort[]
        {
            7, 7, 7, StructuralMap.NoSegment,
            9, 9, 9, StructuralMap.NoSegment,
        };

        StorageMapService.DownSampleArrays(pageType, owner, 4, out var cellType, out var cellOwner);

        Assert.That(cellType.Length, Is.EqualTo(2));
        Assert.That(cellType[0], Is.EqualTo(StoragePageType.Component));
        Assert.That(cellType[1], Is.EqualTo(StoragePageType.Index));
        Assert.That(cellOwner[0], Is.EqualTo((ushort)7), "owner 7 covers 3 of cell 0's 4 pages");
        Assert.That(cellOwner[1], Is.EqualTo((ushort)9), "owner 9 covers 3 of cell 1's 4 pages");
    }

    [Test]
    public async Task DownSampledMap_RendersBoundedAndServesApproximateDetail()
    {
        var saved = StorageMapService.MaxCoarseCells;
        StorageMapService.MaxCoarseCells = 64; // force any real database past the budget
        try
        {
            using var factory = new WorkbenchFactory();
            using var client = factory.CreateAuthenticatedClient();
            var session = await CreateSessionAsync(client);

            var regions = await GetOkAsync<StorageRegionsDto>(client, session, "regions");
            Assert.That(regions.DownSampleFactor, Is.GreaterThan(1), "a real database exceeds the lowered budget");
            Assert.That(regions.DataFilePageCount, Is.GreaterThan(0));
            // AC10 still holds — total mapped bytes equal the on-disk file size, independent of down-sampling.
            Assert.That(regions.DataFileBytes, Is.EqualTo((long)regions.DataFilePageCount * 8192));

            var cellCount = StorageMapService.CellCountFor(regions.DataFilePageCount, regions.DownSampleFactor);
            var region = await GetOkAsync<StorageRegionDto>(client, session, "region");
            Assert.That(region.PageCount, Is.EqualTo(cellCount), "the coarse array is in cell space");
            Assert.That(region.PageCount, Is.LessThanOrEqualTo(64), "down-sampling keeps the coarse map within budget");
            Assert.That(Convert.FromBase64String(region.PageTypes).Length, Is.EqualTo(cellCount));
            Assert.That(Convert.FromBase64String(region.OwnerSegmentIds).Length, Is.EqualTo(cellCount * 2));
            // The Hilbert grid is sized for the cell count, not the real page count.
            Assert.That(1L << (2 * regions.HilbertOrder), Is.GreaterThanOrEqualTo(cellCount));

            var detail = await GetOkAsync<StorageRegionDetailDto>(client, session, "region/detail?node=0");
            Assert.That(detail.Approximate, Is.True, "a down-sampled map serves sampled detail");
            Assert.That(detail.SampleStride, Is.EqualTo(regions.DownSampleFactor));
            Assert.That(detail.PageCount, Is.LessThanOrEqualTo(StorageMapService.DetailTileSize));
            Assert.That(detail.PageCount, Is.EqualTo(cellCount), "the detail tile spans the down-sampled cells");

            var overview = await GetOkAsync<StorageOverviewDto>(client, session, "overview");
            Assert.That(overview.Levels, Is.Not.Empty, "the pyramid builds on a down-sampled map");
        }
        finally
        {
            StorageMapService.MaxCoarseCells = saved;
        }
    }

    private static async Task<SessionDto> CreateSessionAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/sessions/file", new CreateFileSessionRequest("demo.typhon"));
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    private static async Task<T> GetOkAsync<T>(HttpClient client, SessionDto session, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/dbmap/{path}");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var resp = await client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"GET dbmap/{path}");
        return JsonSerializer.Deserialize<T>(await resp.Content.ReadAsStringAsync(), Json)!;
    }
}
