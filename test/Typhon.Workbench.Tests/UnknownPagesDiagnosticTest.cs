using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Typhon.Engine;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Quick forensic dump: generate a small fixture, reopen it, run <see cref="DatabaseEngine.ClassifyAllPages"/>,
/// and print a per-kind breakdown plus the first N Unknown page indices. The point is to find out which pages
/// are unattributed and identify the leak source (directory-map pages? reserve pages? something else?).
/// </summary>
[TestFixture]
[Explicit("Forensic probe for file-map Unknown-pages investigation")]
public sealed class UnknownPagesDiagnosticTest
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-unknown-diag", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [TestCase(false, 1,   TestName = "std_x1")]
    [TestCase(false, 10,  TestName = "std_x10")]
    [TestCase(false, 100, TestName = "std_x100")]
    [TestCase(true,  1,   TestName = "bulk_x1")]
    [TestCase(true,  10,  TestName = "bulk_x10")]
    [TestCase(true,  100, TestName = "bulk_x100")]
    public unsafe void Dump_Unknown_Page_Distribution(bool useBulkLoad, int scale)
    {
        var d = FixtureConfig.Default;
        var cfg = new FixtureConfig(
            CompAArchCount:        d.CompAArchCount * scale,
            CompABArchCount:       d.CompABArchCount * scale,
            CompABCArchCount:      d.CompABCArchCount * scale,
            CompDArchCount:        d.CompDArchCount * scale,
            GuildArchCount:        d.GuildArchCount * scale,
            PlayerArchCount:       d.PlayerArchCount * scale,
            ParticleArchCount:     d.ParticleArchCount * scale,
            ParticleFragmentation: d.ParticleFragmentation,
            Seed:                  d.Seed);
        var result = FixtureDatabase.CreateOrReuse(
            outputDir: _tempDir,
            force: true,
            config: cfg,
            progress: null,
            ct: CancellationToken.None,
            databaseName: $"unknown-diag-{(useBulkLoad ? "bulk" : "std")}",
            useBulkLoad: useBulkLoad);

        TestContext.Out.WriteLine($"Generated: {result.TyphonFilePath} (entities={result.TotalEntities:N0})");

        // FACT-FINDING: read RAW DISK BYTES from the .bin file BEFORE reopening. Specifically the directory
        // section of every segment's root page. If disk content matches what PRE-CLOSE integrity reported,
        // the bug is in reopen. If disk content is stale, the bug is in writes (and reopen is just exposing it).
        var binPath = Path.Combine(Path.GetDirectoryName(result.TyphonFilePath)!,
            Path.GetFileNameWithoutExtension(result.TyphonFilePath) + ".bin");
        if (File.Exists(binPath))
        {
            var binLen = new FileInfo(binPath).Length;
            TestContext.Out.WriteLine($"[DISK] .bin file size: {binLen:N0} bytes ({binLen / 8192:N0} pages)");
            const int PageSize = 8192;
            const int PageHeaderSize = 192; // PageBaseHeader + LogicalSegmentHeader area
            const int DirEntries = 500;
            using var fs = File.OpenRead(binPath);
            var pageBuf = new byte[PageSize];
            // Check a few candidate segment roots
            // PageBaseHeader (16 bytes) + LogicalSegmentHeader: NextMapPBID at offset 16, NextRawDataPBID at offset 20
            const int NextMapOffset = 16;
            int[] candidateRoots =
            [
                85, 97, 101, 113, 117, 129, 133, 137, 145, 153, 157, 161, 165, 173, 177, 185, 189, 193, 205, 225, 245, 265, 285, 305, 325, 345,
            ];
            foreach (var rootIdx in candidateRoots)
            {
                fs.Seek((long)rootIdx * PageSize, SeekOrigin.Begin);
                var read = fs.Read(pageBuf, 0, PageSize);
                if (read != PageSize) continue;
                // Count nonzero directory entries
                var count = 0;
                for (var i = 0; i < DirEntries; i++)
                {
                    var off = PageHeaderSize + i * 4;
                    var v = BitConverter.ToInt32(pageBuf, off);
                    if (v == 0) break;
                    count++;
                }
                if (count > 0)
                {
                    var nextMap = BitConverter.ToInt32(pageBuf, NextMapOffset);
                    TestContext.Out.WriteLine(
                        $"[DISK] page {rootIdx}: directory has {count} non-zero entries (terminator at slot {count}); NextMapPBID={nextMap}");
                    // Follow extension map page if present
                    if (nextMap > 0)
                    {
                        fs.Seek((long)nextMap * PageSize, SeekOrigin.Begin);
                        fs.Read(pageBuf, 0, PageSize);
                        var extCount = 0;
                        for (var i = 0; i < 2000; i++)
                        {
                            var off = PageHeaderSize + i * 4;
                            var v = BitConverter.ToInt32(pageBuf, off);
                            if (v == 0) break;
                            extCount++;
                        }
                        var extNextMap = BitConverter.ToInt32(pageBuf, NextMapOffset);
                        TestContext.Out.WriteLine($"[DISK]   ext page {nextMap}: {extCount} entries (terminator at slot {extCount}); NextMapPBID={extNextMap}");
                    }
                }
            }
        }

        using var lifecycle = EngineLifecycle.OpenAsync(result.TyphonFilePath).GetAwaiter().GetResult();
        TestContext.Out.WriteLine(
            $"[FACT] Engine after reopen: MMF.FileSize={lifecycle.Engine.MMF.FileSize:N0} StorageFilePageCount={lifecycle.Engine.MMF.StorageFilePageCount}");
        if (lifecycle.State != SchemaCompatibility.State.Ready)
        {
            TestContext.Out.WriteLine("=== Reopen Diagnostics (full Detail) ===");
            foreach (var diag in lifecycle.Diagnostics)
            {
                TestContext.Out.WriteLine($"  • {diag.ComponentName} / {diag.Kind}");
                var detail = diag.Detail ?? "";
                TestContext.Out.WriteLine($"    {(detail.Length > 800 ? detail.Substring(0, 800) + "…" : detail)}");
            }
            Assert.That(lifecycle.State, Is.EqualTo(SchemaCompatibility.State.Ready),
                $"Reopen failed: {string.Join(" | ", lifecycle.Diagnostics.Select(d => $"{d.ComponentName}={d.Kind}"))}");
        }

        var engine = lifecycle.Engine;
        var pageCount = engine.MMF.StorageFilePageCount;
        var types = new StoragePageType[pageCount];
        engine.ClassifyAllPages(types);

        var byType = new Dictionary<StoragePageType, int>();
        foreach (var t in types)
        {
            byType[t] = byType.GetValueOrDefault(t) + 1;
        }

        TestContext.Out.WriteLine($"=== Page distribution (pageCount={pageCount:N0}) ===");
        foreach (var kv in byType.OrderByDescending(k => k.Value))
        {
            var pct = 100.0 * kv.Value / pageCount;
            TestContext.Out.WriteLine($"  {kv.Key,-20} {kv.Value,8:N0}  ({pct,5:F1}%)");
        }

        var segments = engine.EnumerateStorageSegments();
        TestContext.Out.WriteLine($"=== Segments registered: {segments.Count} ===");
        var bySegKind = new Dictionary<StorageSegmentKind, (int count, long totalPages)>();
        foreach (var s in segments)
        {
            var rec = bySegKind.GetValueOrDefault(s.Kind);
            bySegKind[s.Kind] = (rec.count + 1, rec.totalPages + s.Pages.Length);
        }
        foreach (var kv in bySegKind.OrderByDescending(k => k.Value.totalPages))
        {
            TestContext.Out.WriteLine($"  {kv.Key,-22} segs={kv.Value.count,5}  pages={kv.Value.totalPages,8:N0}");
        }

        // List Unknown page indices so we can correlate against segments
        var unknowns = new List<int>();
        for (var p = 0; p < pageCount; p++)
        {
            if (types[p] == StoragePageType.Unknown)
            {
                unknowns.Add(p);
            }
        }
        TestContext.Out.WriteLine($"=== Unknown page indices (showing up to 30 of {unknowns.Count:N0}) ===");
        TestContext.Out.WriteLine("  " + string.Join(", ", unknowns.Take(30)));

        // Run-length-encode Unknown indices — clusters of consecutive Unknown pages indicate a single-shot
        // allocation (segment grow, chunk-segment growth, hashmap rehash) whose pages never got attributed.
        if (unknowns.Count > 0)
        {
            TestContext.Out.WriteLine($"=== Unknown clusters (consecutive runs) ===");
            var runs = new List<(int start, int count)>();
            int rs = unknowns[0], rc = 1;
            for (var i = 1; i < unknowns.Count; i++)
            {
                if (unknowns[i] == unknowns[i - 1] + 1) { rc++; }
                else { runs.Add((rs, rc)); rs = unknowns[i]; rc = 1; }
            }
            runs.Add((rs, rc));
            TestContext.Out.WriteLine($"  {runs.Count} runs total. Top 20 by length:");
            foreach (var r in runs.OrderByDescending(x => x.count).Take(20))
            {
                TestContext.Out.WriteLine($"    [{r.start,7} .. {r.start + r.count - 1,7}]  len={r.count}");
            }
        }

        // ─── Integrity audit ─
        // The forensic gate: every issue here is a hard durability/structural bug. The popcount canary catches
        // orphan ranges (lost-write on segment Page Directory); the chain↔directory check identifies WHICH segment
        // suffered the lost append; the chunk-segment capacity check guards against free-list desync.
        var integrity = engine.RunStorageIntegrityCheck();
        TestContext.Out.WriteLine($"=== Integrity report ===");
        TestContext.Out.WriteLine($"  OccupancyBitsSet     : {integrity.OccupancyBitsSet:N0}");
        TestContext.Out.WriteLine($"  SegmentClaimedPages  : {integrity.SegmentClaimedPages:N0}");
        TestContext.Out.WriteLine($"  OrphanPageCount      : {integrity.OrphanPageCount:N0}");
        TestContext.Out.WriteLine($"  PhantomPageCount     : {integrity.PhantomPageCount:N0}");
        TestContext.Out.WriteLine($"  Issues               : {integrity.Issues.Count}");
        foreach (var issue in integrity.Issues.Take(30))
        {
            TestContext.Out.WriteLine($"    • [{issue.Kind}] {issue.Detail}");
        }

        Assert.That(integrity.IsHealthy, Is.True,
            $"Storage integrity broken — {integrity.OrphanPageCount} orphan + {integrity.PhantomPageCount} phantom pages, " +
            $"{integrity.Issues.Count} issues. See test output for the full forensic dump.");
    }
}
