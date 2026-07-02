# Storage Introspection & Integrity Diagnostics
> Read-only APIs that expose the database file's page/segment topology and audit it for corruption.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Storage](./README.md)

## 🎯 What it solves

Typhon's file is a graph of pages, segments and chunks that's normally opaque to application code — there's no SQL-style `pg_class` to ask "what owns page 1024?" or "is this file internally consistent?". Operators and tooling need that visibility for two distinct reasons: to *inspect* the file's physical layout (capacity planning, fragmentation, debugging a specific page/segment) and to *audit* it (verify occupancy bookkeeping, segment directories and chunk allocators agree with each other after a crash, a migration, or just as a health check). This feature exposes both without requiring any data-page I/O or blocking writers.

## ⚙️ How it works (in brief)

Every page in the file is classified into a `StoragePageType` (Root, Occupancy, Component, Revision, Index, Cluster, Vsbs, StringTable, Spatial, EntityMap, System, Free, Unknown), and every logical segment is described by a `StorageSegmentDescriptor` carrying its kind (`StorageSegmentKind`), the file pages it owns, and — for chunk-based segments — the stride/chunk-count layout constants needed to decode it further. All of this is derived purely from in-memory structures (the segment registry, the occupancy bitmap) — no page bodies are read. A separate integrity pass cross-checks these structures against each other (occupancy popcount vs. segment-claimed pages, forward chain vs. page directory, chunk free-list vs. chunk bitmaps) and reports every disagreement as a named, localized issue rather than a generic "corrupt" flag.

## 💻 Usage

```csharp
var dbe = services.BuildServiceProvider().GetRequiredService<DatabaseEngine>();

// 1. Segment topology — every live segment's kind, root page, and owned pages.
IReadOnlyList<StorageSegmentDescriptor> segments = dbe.EnumerateStorageSegments();
foreach (var seg in segments)
{
    Console.WriteLine($"root={seg.RootPageIndex} kind={seg.Kind} pages={seg.Pages.Length} " +
                       $"chunks alloc={seg.AllocatedChunkCount}/{seg.ChunkCapacity}");
}

// 2. Per-page classification — one StoragePageType per file page.
var pageCount = dbe.MMF.StorageFilePageCount;
Span<StoragePageType> pageTypes = new StoragePageType[pageCount];
dbe.ClassifyAllPages(pageTypes);

// 3. Raw occupancy bits — one bit per file page, 1 = allocated.
var words = new long[(dbe.MMF.OccupancyCapacityPages + 63) / 64];
dbe.MMF.ReadOccupancyBits(words);

// 4. Whole-engine integrity audit.
StorageIntegrityReport report = dbe.RunStorageIntegrityCheck();
if (!report.IsHealthy)
{
    foreach (var issue in report.Issues)
    {
        log.LogCritical("storage integrity: {Kind} segment={Segment} pages=[{First}..+{Count}] {Detail}",
            issue.Kind, issue.SegmentRootPageIndex, issue.FirstPageIndex, issue.PageCount, issue.Detail);
    }
}
```

## ⚠️ Guarantees & limits

- All four entry points (`EnumerateStorageSegments`, `ClassifyAllPages`, `MMF.ReadOccupancyBits`, `RunStorageIntegrityCheck`) are read-only and touch only resident in-memory structures — no data-page I/O, safe to call on a live engine at any time, no write blocking.
- `EnumerateStorageSegments` is authoritative: it walks the engine's segment registry directly, so every persistent segment kind (component, revision, index, cluster, VSBS, string-table, spatial, entity-map, occupancy, system) is attributed — no page should ever classify as `Unknown` on a healthy engine.
- `ClassifyAllPages` requires a destination span of at least `MMF.StorageFilePageCount` entries; it throws `ArgumentException` otherwise.
- `RunStorageIntegrityCheck` is the canary for two independent invariant classes: a **popcount canary** (occupancy bitmap set-bit count vs. every page claimed by a segment, its directory-extension pages, reserved roots, occupancy reserves, and directory twins) and **chunk-segment capacity** (`AllocatedChunkCount + FreeChunkCount == ChunkCapacity` per chunk-based segment). A non-empty `Issues` list is a hard structural/durability bug, not a soft warning — `IsHealthy` is the single assertion callers need.
- The audit is best-effort under concurrency for chain/directory cross-checks (it runs under an epoch guard) but the popcount and chunk-capacity checks are point-in-time snapshots; running it against a database under heavy concurrent allocation can occasionally observe a transient mismatch that resolves on a re-run — a persistent mismatch is the signal to act on.
- This is diagnostic surface, not a repair API: it reports issues, it does not fix them. Recovery / rebuild (occupancy re-derive, EntityMap rebuild, etc.) is a separate, crash-path mechanism.

## 🧪 Tests

- [StorageIntrospectionTests](../../../test/Typhon.Engine.Tests/Storage/StorageIntrospectionTests.cs) — segment enumeration, page classification (no page ever `Unknown`), occupancy-bit reads, and the zero-disk-read guarantee

## 🔗 Related

- Source: `src/Typhon.Engine/Storage/public/StorageMapTypes.cs` (`StoragePageType`, `StorageSegmentKind`, `StorageSegmentDescriptor`, `StorageIntegrityIssueKind`, `StorageIntegrityIssue`, `StorageIntegrityReport`)
- Source: `src/Typhon.Engine/Ecs/public/DatabaseEngine.StorageIntrospection.cs` (`EnumerateStorageSegments`, `ClassifyAllPages`, `RunStorageIntegrityCheck`)
- Source: `src/Typhon.Engine/Storage/internals/ManagedPagedMMF.StorageMap.cs` (`StorageFilePageCount`, `StoragePageSize`, `ReadOccupancyBits`)
- Related feature: [Page Allocation & Occupancy Tracking](./page-allocation-occupancy.md) — the occupancy bitmap this feature reads and audits

<!-- Deep dive: claude/design/Apps/Workbench/views/file-map.md — the Workbench Database File Map view this API powers -->
<!-- Deep dive: claude/design/Storage/database-file-format.md — on-disk page/segment/chunk layout these types describe -->
