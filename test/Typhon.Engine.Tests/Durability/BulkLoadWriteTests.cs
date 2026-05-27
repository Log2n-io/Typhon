using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// P2 integration tests for <see cref="BulkLoadSession"/>. Verifies the contracted behaviour:
/// <list type="bullet">
///   <item><b>Round-trip:</b> entities spawned via the bulk path are visible to subsequent transactions.</item>
///   <item><b>BL-01 (no per-row WAL):</b> a complete bulk emits exactly one <c>BulkBegin</c> + one <c>BulkEnd</c>; zero <c>Transaction</c> records.</item>
///   <item><b>Discard:</b> a bulk that is disposed without <c>CompleteBulkLoad</c> leaves no visible entities (MVCC voids the bulk's pending UoW on next
///         snapshot).</item>
/// </list>
/// </summary>
/// <remarks>
/// Source-of-truth design: <c>claude/design/Durability/BulkLoad/02-write-path.md</c>. Tracked in <a href="https://github.com/nockawa/Typhon/issues/380">#380</a>.
/// </remarks>
[TestFixture]
internal sealed class BulkLoadWriteTests
{
    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;

    private static string CurrentDatabaseName
    {
        get
        {
            var name = TestContext.CurrentContext.Test.Name;
            foreach (var c in new[] { '(', ')', ',', ' ', '"' })
            {
                name = name.Replace(c, '_');
            }
            // Truncate to fit in PagedMMFOptions.DatabaseNameMaxUtf8Size (63 bytes UTF-8).
            // Prefix "Blw_" (4 chars) + name; keep the *tail* of the name (most discriminating bytes).
            const int max = 63;
            const string prefix = "Blw_";
            if (prefix.Length + name.Length > max)
            {
                name = name[^(max - prefix.Length)..];
            }
            return prefix + name;
        }
    }

    [SetUp]
    public void Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(BulkLoadWriteTests));
        _dbDir = Path.Combine(root, "db");
        _walDir = Path.Combine(root, "wal");
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b =>
            {
                b.AddSimpleConsole();
                b.SetMinimumLevel(LogLevel.Warning);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = CurrentDatabaseName;
                opts.DatabaseDirectory = _dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = _walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;

        try { if (Directory.Exists(_walDir)) Directory.Delete(_walDir, true); } catch { }
        try { if (Directory.Exists(_dbDir)) Directory.Delete(_dbDir, true); } catch { }
    }

    private DatabaseEngine BuildEngine()
    {
        var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void Spawn_ThenCompleteBulkLoad_EntitiesVisibleToNewTransaction()
    {
        // Round-trip approximation: spawn N entities via bulk, complete, then query via a fresh UoW
        // within the same engine instance. Proves the bulk's revisions transitioned to committed state and
        // are visible to MVCC. (A literal close+reopen test is covered by P3 recovery tests.)
        var dbe = BuildEngine();

        const int count = 100;
        var ids = new EntityId[count];

        using (var bulk = dbe.BeginBulkLoad())
        {
            for (int i = 0; i < count; i++)
            {
                var comp = new CompA(i + 1, i * 1.5f, i * 2.5);
                ids[i] = bulk.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            }

            Assert.That(bulk.EntitiesSpawned, Is.EqualTo(count));
            bulk.CompleteBulkLoad();
            Assert.That(bulk.IsClosed, Is.True);
        }

        // Verify via a fresh transaction.
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            Assert.That(tx.IsAlive(ids[i]), Is.True, $"entity {i} (id={ids[i]}) should be visible after CompleteBulkLoad");
        }
    }

    [Test]
    public void CompleteBulkLoad_EmitsExactlyOneBulkBeginAndOneBulkEnd_NoTransactionRecords()
    {
        // BL-01: the bulk path emits no per-row Transaction records. The WAL should contain
        // exactly one BulkBegin + one BulkEnd chunk (plus possibly FPI / TickFence which are
        // orthogonal infrastructure — those don't count against BL-01).
        var dbe = BuildEngine();

        using (var bulk = dbe.BeginBulkLoad())
        {
            for (int i = 0; i < 50; i++)
            {
                var comp = new CompA(i + 1, i, i);
                bulk.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            }
            bulk.CompleteBulkLoad();
        }

        var counts = CountWalChunksByType();

        Assert.That(counts.GetValueOrDefault(WalChunkType.Transaction), Is.EqualTo(0),
            "BL-01: no per-row Transaction WAL records during a bulk session");
        Assert.That(counts.GetValueOrDefault(WalChunkType.BulkBegin), Is.EqualTo(1),
            "exactly one BulkBegin chunk");
        Assert.That(counts.GetValueOrDefault(WalChunkType.BulkEnd), Is.EqualTo(1),
            "exactly one BulkEnd chunk");
    }

    [Test]
    public void Dispose_WithoutCompleteBulkLoad_NoEntitiesVisible()
    {
        // The bulk's UoW remains Pending; the Transaction was rolled back. MVCC must not show any
        // bulk-spawned entities to subsequent reads.
        var dbe = BuildEngine();

        const int count = 50;
        var ids = new EntityId[count];

        var bulk = dbe.BeginBulkLoad();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var comp = new CompA(i + 1, i, i);
                ids[i] = bulk.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            }
            // Dispose WITHOUT calling CompleteBulkLoad.
        }
        finally
        {
            bulk.Dispose();
        }

        Assert.That(bulk.IsClosed, Is.True, "Dispose marks the session closed");

        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            Assert.That(tx.IsAlive(ids[i]), Is.False,
                $"entity {i} (id={ids[i]}) must NOT be visible — bulk was rolled back");
        }
    }

    [Test]
    public void Spawn_AcrossTransactionRecycleThreshold_AllEntitiesVisible()
    {
        // The bulk session recycles its underlying transaction every 5,000 spawns (TransactionRecycleThreshold).
        // Spawning 12,000 entities exercises 2 recycles + 1 final commit at CompleteBulkLoad time. All 12 k must
        // be visible after CompleteBulkLoad — verifies that revisions stamped by earlier (now-committed)
        // transactions in the recycle chain still surface as part of the bulk's WalDurable transition.
        var dbe = BuildEngine();

        const int count = 12_000;
        var ids = new EntityId[count];

        using (var bulk = dbe.BeginBulkLoad())
        {
            for (int i = 0; i < count; i++)
            {
                var comp = new CompA(i + 1, i * 1.5f, i * 2.5);
                ids[i] = bulk.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            }
            bulk.CompleteBulkLoad();
        }

        using var tx = dbe.CreateQuickTransaction();
        int visibleCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (tx.IsAlive(ids[i]))
            {
                visibleCount++;
            }
        }
        Assert.That(visibleCount, Is.EqualTo(count),
            "transaction-recycling must not lose entities across the boundary — all 12 k must be visible after CompleteBulkLoad");
    }

    [Test]
    public void BulkLoad_WithoutWal_ThrowsAtBeginBulkLoad()
    {
        // BulkLoad requires a WAL-configured engine — the manifest pair is the durability anchor.
        // Build a fresh engine WITHOUT WAL to verify the guardrail.
        var noWalServices = new ServiceCollection();
        var noWalDbDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", "BulkLoadWriteTests", "noWal");
        Directory.CreateDirectory(noWalDbDir);

        noWalServices
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = "BulkLoadNoWalGuardrail";
                opts.DatabaseDirectory = noWalDbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(_ => { });

        using var sp = noWalServices.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();

        var dbe = sp.GetRequiredService<DatabaseEngine>();

        var ex = Assert.Throws<InvalidOperationException>(() => dbe.BeginBulkLoad());
        Assert.That(ex.Message, Does.Contain("WAL"));
    }

    /// <summary>
    /// Scans every <c>*.wal</c> file in <see cref="_walDir"/> and counts chunks by <see cref="WalChunkType"/>.
    /// <para>
    /// Uses a raw byte scan over each segment instead of <see cref="WalSegmentReader"/> because the latter
    /// stops at 4096-byte inter-drain padding (its <c>AdvanceToNextFrame</c> treats <c>FrameLength == 0</c> as
    /// end-of-data; when chunks from independent producers land in separate drain cycles, the writer pads to
    /// 4096 with zeros and a single-handle reader hits that padding before the next frame). For test
    /// verification we only need to count chunk headers by type, which is a 4-byte pattern scan.
    /// </para>
    /// </summary>
    [MustUseReturnValue]
    private Dictionary<WalChunkType, int> CountWalChunksByType()
    {
        var counts = new Dictionary<WalChunkType, int>();
        var segments = Directory.GetFiles(_walDir, "*.wal").OrderBy(p => p).ToArray();

        foreach (var segmentPath in segments)
        {
            // WAL writer holds the file open exclusively; we open with FileShare.ReadWrite to coexist.
            using var fs = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[fs.Length];
            int read = 0;
            while (read < bytes.Length)
            {
                var n = fs.Read(bytes, read, bytes.Length - read);
                if (n == 0) break;
                read += n;
            }
            ScanSegmentForChunks(bytes, counts);
        }

        return counts;
    }

    /// <summary>
    /// Iterates the segment frame-by-frame (each frame = 8-byte WalFrameHeader + payload), and within each
    /// frame chunk-by-chunk. Skips 4096-byte inter-drain padding gaps: when <c>FrameLength == 0</c> at the
    /// current offset, advance to the next 4096 boundary and resume.
    /// </summary>
    private static void ScanSegmentForChunks(byte[] bytes, Dictionary<WalChunkType, int> counts)
    {
        // WalSegmentHeader is 4096 bytes; frame data begins at offset 4096.
        const int segmentHeaderSize = 4096;
        const int frameHeaderSize = 8;      // WalFrameHeader: int FrameLength + int RecordCount
        const int chunkHeaderSize = 8;      // WalChunkHeader: ushort ChunkType + ushort ChunkSize + uint PrevCRC
        const int pageSize = 4096;

        int offset = segmentHeaderSize;
        while (offset + frameHeaderSize <= bytes.Length)
        {
            // FrameLength is int (4 bytes), at offset 0 of the frame header.
            var frameLength = BitConverter.ToInt32(bytes, offset);

            if (frameLength == 0)
            {
                // Either: (a) inter-drain padding — advance to next 4096 boundary, OR
                // (b) genuine end-of-data — eventually the loop runs off the end.
                var nextAlignedOffset = (offset + pageSize) & ~(pageSize - 1);
                if (nextAlignedOffset <= offset)
                {
                    nextAlignedOffset = offset + pageSize;
                }
                offset = nextAlignedOffset;
                continue;
            }

            if (frameLength == -1)
            {
                // PaddingSentinel — end of usable data in this segment.
                break;
            }

            if (frameLength < frameHeaderSize || offset + frameLength > bytes.Length)
            {
                break;
            }

            int frameEnd = offset + frameLength;
            int chunkOffset = offset + frameHeaderSize;

            while (chunkOffset + chunkHeaderSize <= frameEnd)
            {
                var chunkType = (WalChunkType)BitConverter.ToUInt16(bytes, chunkOffset);
                var chunkSize = BitConverter.ToUInt16(bytes, chunkOffset + 2);

                if (chunkSize < chunkHeaderSize + 4 /* footer */ || chunkOffset + chunkSize > frameEnd)
                {
                    break;
                }

                if (chunkType >= WalChunkType.Transaction && chunkType <= WalChunkType.BulkEnd)
                {
                    counts[chunkType] = counts.GetValueOrDefault(chunkType) + 1;
                }

                chunkOffset += chunkSize;
            }

            offset = frameEnd;
        }
    }
}
