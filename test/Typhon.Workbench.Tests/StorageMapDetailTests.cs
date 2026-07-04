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
    public async Task GetRegionDetail_ReturnsEntropyAndByteClassBuffers()
    {
        // A3 — the detail tier carries the two decode-free per-page encodings (§4.2).
        var session = await CreateSessionAsync();
        var detail = await GetOkAsync<StorageRegionDetailDto>(session, "region/detail?node=0");

        var entropy = Convert.FromBase64String(detail.Entropy);
        var byteClass = Convert.FromBase64String(detail.ByteClass);
        Assert.That(entropy.Length, Is.EqualTo(detail.PageCount));
        Assert.That(byteClass.Length, Is.EqualTo(detail.PageCount));
        Assert.That(byteClass, Is.All.LessThanOrEqualTo(3), "byte class is one of the 4 classes");
    }

    [Test]
    public void ShannonEntropy_ZeroedPageReadsZero()
    {
        // A uniform page (all 0x00) carries no information — entropy 0.
        Assert.That(StorageMapService.ShannonEntropy(new byte[8192]), Is.EqualTo(0));
    }

    [Test]
    public void ShannonEntropy_UniformByteSpreadReadsMaximum()
    {
        // Every byte value present in equal measure — 8 bits of entropy, the scaled maximum (255).
        var body = new byte[8192];
        for (var i = 0; i < body.Length; i++)
        {
            body[i] = (byte)(i & 0xFF);
        }
        Assert.That(StorageMapService.ShannonEntropy(body), Is.EqualTo(255));
    }

    [Test]
    public async Task GetPage_ChunkSegmentRoot_ReportsHeaderAndDirectoryBytes()
    {
        // A6 — a chunk-based segment's root page carries the per-page header *and* the logical-segment directory
        // section, which is why it holds fewer chunks than later pages. The renderer reserves both bands to keep
        // the surface mapping the page's real memory layout.
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var component = Array.Find(regions.Segments, s => s.Kind == "Component");
        Assert.That(component, Is.Not.Null, "the demo database has component segments");

        var root = await GetOkAsync<StoragePageDetailDto>(session, $"page/{component!.RootPageIndex}");
        Assert.That(root.HeaderBytes, Is.GreaterThan(0), "every chunk-based page has a per-page header");
        Assert.That(root.DirectoryBytes, Is.GreaterThan(0), "the segment root page carries the directory section");
    }

    [Test]
    [Ignore("Stale vs v4 directory-root storage introspection; tracked by #426")]
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
    [Ignore("Stale vs v4 directory-root storage introspection; tracked by #426")]
    public async Task GetPage_OccupancyRoot_RendersGovernedRegionMap()
    {
        // A6 §10.2 — the occupancy root page governs the first 48,000 file pages; its detail carries a down-sampled
        // allocation region-map of that range. The bits come from ReadOccupancyBits (zero data-page I/O).
        var session = await CreateSessionAsync();
        var page = await GetOkAsync<StoragePageDetailDto>(session, "page/1");

        Assert.That(page.PageType, Is.EqualTo("Occupancy"));
        Assert.That(page.OccupancyFirstPage, Is.EqualTo(0L), "the root occupancy page governs from file page 0");
        Assert.That(page.OccupancyGovernedCount, Is.GreaterThan(0));
        Assert.That(page.OccupancyGridCols, Is.GreaterThan(0));

        var map = Convert.FromBase64String(page.OccupancyMap);
        Assert.That(map.Length, Is.GreaterThan(0), "the occupancy page renders a region map, not blank");
        Assert.That(map.Length, Is.LessThanOrEqualTo(256), "the governed range is down-sampled to <= 256 cells");
        // Reserved pages 0..3 are always allocated, so the first governed cell must show non-zero allocation.
        Assert.That(map[0], Is.GreaterThan((byte)0), "the first cell covers the always-allocated reserved pages");
    }

    [Test]
    public async Task GetPage_ClusterPage_CarriesIntraChunkFill()
    {
        // A6 §10.1 — a cluster page colours occupied chunks by intra-chunk fill (popcount/N), not binary occupancy,
        // and its chunks decode through the typed cluster decoder. Self-adapting: skips if the demo DB has no clusters.
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var cluster = Array.Find(regions.Segments, s => s.Kind == "Cluster");
        if (cluster is null)
        {
            Assert.Ignore("the demo database has no cluster segments");
            return;
        }

        var page = await GetOkAsync<StoragePageDetailDto>(session, $"page/{cluster.RootPageIndex}");
        Assert.That(page.ChunkFill, Is.Not.Empty, "a cluster page carries a per-chunk fill array");
        Assert.That(page.ChunkClass, Is.Not.Empty);
        Assert.That(Convert.FromBase64String(page.ChunkFill).Length, Is.EqualTo(page.ChunkTotal));
        Assert.That(Convert.FromBase64String(page.ChunkClass).Length, Is.EqualTo(page.ChunkTotal));

        var chunk = await GetOkAsync<StorageChunkDto>(session, $"chunk/{cluster.Id}/0");
        Assert.That(chunk.Decoder, Is.EqualTo("cluster"));
        Assert.That(chunk.Cells, Has.All.Property("Kind").EqualTo("entitySlot"));
        // A6 §10.1 — a cluster chunk carries its slot-ordered component names so the client can label the per-component overlay picker.
        Assert.That(chunk.ClusterComponents, Is.Not.Empty, "a cluster chunk reports its component names for the overlay picker");
    }

    [Test]
    public async Task GetClusterEntity_DecodesOccupiedSlotFields()
    {
        // file-map §10 Q4 override (L5) — a cluster slot drills to the entity's full component-grouped field decode, the same field-level view the legacy
        // component table shows at L4. Self-adapting: skips if the demo DB has no clusters or the root chunk has no occupied slot.
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var cluster = Array.Find(regions.Segments, s => s.Kind == "Cluster");
        if (cluster is null)
        {
            Assert.Ignore("the demo database has no cluster segments");
            return;
        }

        // The L4 slot grid tells us which slots are occupied (entitySlot cell, colorKey > 0).
        var slots = await GetOkAsync<StorageChunkDto>(session, $"chunk/{cluster.Id}/0");
        var occupied = Array.FindIndex(slots.Cells, c => c.Kind == "entitySlot" && c.ColorKey > 0);
        if (occupied < 0)
        {
            Assert.Ignore("the demo cluster's root chunk has no occupied slot");
            return;
        }

        var entity = await GetOkAsync<StorageChunkDto>(session, $"chunk/{cluster.Id}/0/entity/{occupied}");
        Assert.That(entity.Decoder, Is.EqualTo("clusterEntity"));
        Assert.That(entity.Occupied, Is.True, "the chosen slot is occupied");
        Assert.That(entity.Cells, Is.Not.Empty);
        Assert.That(entity.Cells, Has.Some.Property("Kind").EqualTo("entityPk"), "the entity decode leads with its packed id");
        Assert.That(entity.Cells, Has.Some.Property("Kind").EqualTo("componentHeader"), "fields are grouped under per-component headers");
    }

    [Test]
    public async Task GetClusterEntity_FreeSlotHasNoEntity()
    {
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var cluster = Array.Find(regions.Segments, s => s.Kind == "Cluster");
        if (cluster is null)
        {
            Assert.Ignore("the demo database has no cluster segments");
            return;
        }

        var slots = await GetOkAsync<StorageChunkDto>(session, $"chunk/{cluster.Id}/0");
        var free = Array.FindIndex(slots.Cells, c => c.Kind == "entitySlot" && c.ColorKey == 0);
        if (free < 0)
        {
            Assert.Ignore("the demo cluster's root chunk is full");
            return;
        }

        var entity = await GetOkAsync<StorageChunkDto>(session, $"chunk/{cluster.Id}/0/entity/{free}");
        Assert.That(entity.Decoder, Is.EqualTo("clusterEntity"));
        Assert.That(entity.Occupied, Is.False);
        Assert.That(entity.Cells, Is.Empty, "a free slot holds no entity");
    }

    [Test]
    public async Task GetClusterEntity_NonClusterSegmentReturns404()
    {
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var component = Array.Find(regions.Segments, s => s.Kind == "Component");
        if (component is null)
        {
            Assert.Ignore("the demo database has no component segments");
            return;
        }

        // GetClusterEntity rejects a non-cluster segment (returns null → 404), so the L5 endpoint never mis-decodes a component chunk as a cluster.
        var resp = await GetAsync(session, $"chunk/{component.Id}/0/entity/0");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
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
    public async Task GetSegmentSummary_ComponentSegment_ReportsChunkAllocation()
    {
        // A6 — every chunk-based segment's harvest card reports allocated / free / capacity chunk counts.
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var component = Array.Find(regions.Segments, s => s.Kind == "Component");
        Assert.That(component, Is.Not.Null, "the demo database has component segments");

        var summary = await GetOkAsync<StorageSegmentSummaryDto>(session, $"segment/{component!.Id}/summary");

        Assert.That(summary.Id, Is.EqualTo(component.Id));
        Assert.That(summary.Kind, Is.EqualTo("Component"));
        Assert.That(summary.ChunkCapacity, Is.GreaterThan(0), "a component segment is chunk-based");
        Assert.That(summary.AllocatedChunkCount, Is.LessThanOrEqualTo(summary.ChunkCapacity));
        Assert.That(summary.FreeChunkCount, Is.EqualTo(summary.ChunkCapacity - summary.AllocatedChunkCount));
        Assert.That(summary.EntityMap, Is.Null, "a component segment has no entity-map stats");
    }

    [Test]
    public async Task GetSegmentSummary_EntityMapSegment_ReportsHashStats()
    {
        // A6 — an entity-map segment's card carries the lazily-walked linear-hash distribution.
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var entityMap = Array.Find(regions.Segments, s => s.Kind == "EntityMap");
        if (entityMap is null)
        {
            Assert.Ignore("the demo database has no entity-map segments");
            return;
        }

        var summary = await GetOkAsync<StorageSegmentSummaryDto>(session, $"segment/{entityMap.Id}/summary");

        Assert.That(summary.Kind, Is.EqualTo("EntityMap"));
        Assert.That(summary.EntityMap, Is.Not.Null, "an entity-map segment carries linear-hash stats");
        Assert.That(summary.EntityMap!.BucketCount, Is.GreaterThan(0));
        var histogram = summary.EntityMap.FillEmpty + summary.EntityMap.FillQuarter + summary.EntityMap.FillHalf
            + summary.EntityMap.FillThreeQuarter + summary.EntityMap.FillFull;
        Assert.That(histogram, Is.EqualTo(summary.EntityMap.BucketCount), "the fill histogram partitions every bucket once");
    }

    [Test]
    public async Task GetSegmentSummary_ClusterSegment_ReportsEntityFill()
    {
        // A6 — a cluster segment's card adds the entity-level fill (entities packed into active clusters).
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var cluster = Array.Find(regions.Segments, s => s.Kind == "Cluster");
        if (cluster is null)
        {
            Assert.Ignore("the demo database has no cluster segments");
            return;
        }

        var summary = await GetOkAsync<StorageSegmentSummaryDto>(session, $"segment/{cluster.Id}/summary");

        Assert.That(summary.Kind, Is.EqualTo("Cluster"));
        Assert.That(summary.ClusterSize, Is.GreaterThan(0), "a cluster segment reports its slot count");
        Assert.That(summary.EntityCount, Is.GreaterThanOrEqualTo(0));
        Assert.That(summary.ActiveClusterCount, Is.GreaterThanOrEqualTo(0));
        Assert.That(summary.ChunkCapacity, Is.GreaterThan(0), "a cluster segment is chunk-based");
    }

    [Test]
    public async Task GetSegmentSummary_UnknownIdReturns404()
    {
        var session = await CreateSessionAsync();
        var resp = await GetAsync(session, "segment/999999/summary");
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
    public async Task GetChunk_VsbsSegmentDecodesElementsAndChain()
    {
        // A6 §10.1 — a VSBS / component-collection chunk decodes to element-count + capacity + chain link via TryGetVsbsLayout.
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var vsbs = Array.Find(regions.Segments, s => s.Kind == "Vsbs" || s.Kind == "ComponentCollection");
        if (vsbs is null)
        {
            Assert.Ignore("the demo database has no VSBS / component-collection segments");
            return;
        }

        var chunk = await GetOkAsync<StorageChunkDto>(session, $"chunk/{vsbs.Id}/0");
        Assert.That(chunk.Decoder, Is.EqualTo("vsbs"));
        Assert.That(chunk.Cells, Has.Some.Property("Label").EqualTo("Elements"));
        Assert.That(chunk.Cells, Has.Some.Property("Label").EqualTo("Next chunk"));
    }

    [Test]
    [Ignore("Stale vs v4 directory-root storage introspection; tracked by #426")]
    public async Task GetPage_VsbsPage_CarriesElementFill()
    {
        // A6 §10.1 — a VSBS page colours chunks by element fill (ElementCount / capacity), not binary occupancy.
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var vsbs = Array.Find(regions.Segments, s => s.Kind == "Vsbs" || s.Kind == "ComponentCollection");
        if (vsbs is null)
        {
            Assert.Ignore("the demo database has no VSBS / component-collection segments");
            return;
        }

        var page = await GetOkAsync<StoragePageDetailDto>(session, $"page/{vsbs.RootPageIndex}");
        Assert.That(page.ChunkFill, Is.Not.Empty, "a VSBS page carries a per-chunk element fill array");
        Assert.That(Convert.FromBase64String(page.ChunkFill).Length, Is.EqualTo(page.ChunkTotal));
    }

    [Test]
    public async Task GetChunk_StringTableSegmentDecodesPreview()
    {
        // A6 §10.1 — a string-table chunk decodes to a UTF-8 preview + chain link. Self-skips if the demo has no string table.
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var stringSeg = Array.Find(regions.Segments, s => s.Kind == "StringTable");
        if (stringSeg is null)
        {
            Assert.Ignore("the demo database has no string-table segments");
            return;
        }

        var chunk = await GetOkAsync<StorageChunkDto>(session, $"chunk/{stringSeg.Id}/0");
        Assert.That(chunk.Decoder, Is.EqualTo("string"));
        Assert.That(chunk.Cells, Has.Some.Property("Label").EqualTo("Preview"));
    }

    [Test]
    public async Task GetChunk_EntityMapSegmentDecodesMetaChunk()
    {
        // A6 §10.1 — chunk 0 of an entity-map segment is always the linear-hash meta chunk; the hash-bucket decoder
        // unpacks its bucket count / level / split pointer. Self-skips if the demo has no entity-map segments.
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var entityMap = Array.Find(regions.Segments, s => s.Kind == "EntityMap");
        if (entityMap is null)
        {
            Assert.Ignore("the demo database has no entity-map segments");
            return;
        }

        var meta = await GetOkAsync<StorageChunkDto>(session, $"chunk/{entityMap.Id}/0");
        Assert.That(meta.Decoder, Is.EqualTo("hash-bucket"));
        Assert.That(Array.Find(meta.Cells, c => c.Label == "Role")!.Value, Is.EqualTo("Meta"));
        Assert.That(meta.Cells, Has.Some.Property("Label").EqualTo("Buckets"));
    }

    [Test]
    public async Task GetPage_EntityMapPage_CarriesBucketFillAndHatchesMeta()
    {
        // A6 §10.1 — an entity-map page colours bucket chunks by EntryCount / capacity and marks the structural meta /
        // directory chunks NonData (so their headerless bytes are never read as a bucket). Self-skips if no entity-map.
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var entityMap = Array.Find(regions.Segments, s => s.Kind == "EntityMap");
        if (entityMap is null)
        {
            Assert.Ignore("the demo database has no entity-map segments");
            return;
        }

        var page = await GetOkAsync<StoragePageDetailDto>(session, $"page/{entityMap.RootPageIndex}");
        Assert.That(page.ChunkFill, Is.Not.Empty, "an entity-map page carries a per-chunk bucket-fill array");
        Assert.That(page.ChunkClass, Is.Not.Empty);
        var fill = Convert.FromBase64String(page.ChunkFill);
        var cls = Convert.FromBase64String(page.ChunkClass);
        Assert.That(fill.Length, Is.EqualTo(page.ChunkTotal));
        Assert.That(cls.Length, Is.EqualTo(page.ChunkTotal));
        // Chunk 0 (firstChunkId 0 on the root page) is the meta chunk — hatched as structural, never a bucket.
        Assert.That(cls[0], Is.EqualTo((byte)StorageChunkClass.NonData), "the meta chunk is hatched, not read as a bucket");

        // Find a data (bucket / overflow) chunk on this page and decode it through the hash-bucket decoder.
        var dataGlobalId = -1;
        for (var i = 0; i < cls.Length; i++)
        {
            if (cls[i] == (byte)StorageChunkClass.ContainerFill || cls[i] == (byte)StorageChunkClass.Overflow)
            {
                dataGlobalId = page.FirstChunkId + i;
                break;
            }
        }
        if (dataGlobalId < 0)
        {
            Assert.Ignore("no bucket chunk on the entity-map root page");
            return;
        }

        var bucket = await GetOkAsync<StorageChunkDto>(session, $"chunk/{entityMap.Id}/{dataGlobalId}");
        Assert.That(bucket.Decoder, Is.EqualTo("hash-bucket"));
        Assert.That(Array.Find(bucket.Cells, c => c.Label == "Role")!.Value, Is.AnyOf("Bucket", "Overflow"));
        Assert.That(bucket.Cells, Has.Some.Property("Label").EqualTo("Entries"));
    }

    [Test]
    public async Task GetChunk_IndexSegmentDecodesDirectoryAndNode()
    {
        // A6 §13 — chunk 0 of an index segment is the shared B-tree directory; the index decoder lists every tree. The demo has index segments, so this runs.
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var index = Array.Find(regions.Segments, s => s.Kind == "Index");
        if (index is null)
        {
            Assert.Ignore("the demo database has no index segments");
            return;
        }

        var dir = await GetOkAsync<StorageChunkDto>(session, $"chunk/{index.Id}/0");
        Assert.That(dir.Decoder, Is.EqualTo("index"));
        Assert.That(dir.Cells, Has.Some.Property("Label").EqualTo("B-trees"));
    }

    [Test]
    [Ignore("Stale vs v4 directory-root storage introspection; tracked by #426")]
    public async Task GetPage_IndexPage_ClassesLeafInternalAndHatchesDirectory()
    {
        // A6 §13 — an index page classes each node leaf vs internal (from its control word) and hatches the directory chunks (0..3) as non-data.
        var session = await CreateSessionAsync();
        var regions = await GetOkAsync<StorageRegionsDto>(session, "regions");
        var index = Array.Find(regions.Segments, s => s.Kind == "Index");
        if (index is null)
        {
            Assert.Ignore("the demo database has no index segments");
            return;
        }

        var page = await GetOkAsync<StoragePageDetailDto>(session, $"page/{index.RootPageIndex}");
        Assert.That(page.ChunkClass, Is.Not.Empty, "an index page carries a per-chunk class array");
        var cls = Convert.FromBase64String(page.ChunkClass);
        Assert.That(cls.Length, Is.EqualTo(page.ChunkTotal));
        // Chunk 0 (firstChunkId 0 on the root page) is the shared B-tree directory — hatched, not read as a node.
        Assert.That(cls[0], Is.EqualTo((byte)StorageChunkClass.NonData));

        // Find a node (leaf or internal) on the page and decode it; its decoded role must match the L3 class.
        var nodeGlobalId = -1;
        string expectedRole = null;
        for (var i = 0; i < cls.Length; i++)
        {
            if (cls[i] == (byte)StorageChunkClass.Leaf || cls[i] == (byte)StorageChunkClass.Internal)
            {
                nodeGlobalId = page.FirstChunkId + i;
                expectedRole = cls[i] == (byte)StorageChunkClass.Leaf ? "Leaf" : "Internal";
                break;
            }
        }
        if (nodeGlobalId < 0)
        {
            Assert.Ignore("no B-tree node on the index root page");
            return;
        }

        var node = await GetOkAsync<StorageChunkDto>(session, $"chunk/{index.Id}/{nodeGlobalId}");
        Assert.That(node.Decoder, Is.EqualTo("index"));
        Assert.That(Array.Find(node.Cells, c => c.Label == "Role")!.Value, Is.EqualTo(expectedRole));
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
