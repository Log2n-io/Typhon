using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// LOG-03 / REC-01 (#587): v2 recovery must stop at the FIRST corruption boundary and never apply records from beyond it.
/// </summary>
/// <remarks>
/// <para>
/// <c>WalSegmentReader</c> raises <c>WasTruncated</c> for a mid-log CRC break inside a <b>sealed</b> segment exactly as it does for a torn tail on the last one,
/// and LOG-03 is deliberately unconditional about both. <c>WalRecovery</c> (v1) has always honoured it; <c>RecoveryDriver</c> (v2) never read the flag, so it
/// skipped the broken segment's tail and carried on applying records from later segments. Records past the boundary have no CRC-chain guarantee — they may be
/// partially flushed, belong to a transaction that never committed, or be stale bytes left in a recycled segment — so replaying them is the atomicity violation
/// REC-01 exists to prevent. Both recovery paths run at the same open, so before the fix the frontier the engine computed (v1, with truncation-stop) and the set
/// of records it actually applied (v2, past the boundary) disagreed by construction.
/// </para>
/// <para>
/// The check has to happen per segment: <c>OpenSegment</c> resets <c>WasTruncated</c>, so a test after the loop would read a flag the next iteration had already
/// cleared.
/// </para>
/// <para>
/// Note that a normally sealed, pre-allocated segment ending in zeros does <b>not</b> set the flag — <c>AdvanceToNextFrame</c> treats a zero <c>FrameLength</c>
/// at a page boundary and the padding sentinel as clean end-of-data. Only genuinely malformed content trips it, which is what makes an unconditional stop safe.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class RecoveryCorruptionBoundaryTests
{
    /// <summary>Small enough that a few hundred small transactions roll a segment, large enough to exceed the 256 KB staging buffer comfortably.</summary>
    private const int SegmentSize = 512 * 1024;

    /// <summary>Spawns per filler transaction — bigger batches reach the rotation threshold in fewer fsyncs.</summary>
    private const int SpawnsPerTx = 32;

    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;

    /// <summary>Database names are capped at 63 UTF-8 bytes, and these test names are long — keep the distinguishing tail.</summary>
    private static string DbName
    {
        get
        {
            var name = TestContext.CurrentContext.Test.Name;
            foreach (var c in new[] { '(', ')', ',', ' ', '"' })
            {
                name = name.Replace(c, '_');
            }

            const int max = 63;
            const string prefix = "RecCorrupt_";
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
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(RecoveryCorruptionBoundaryTests), DbName);
        _dbDir = Path.Combine(root, "db");
        _walDir = Path.Combine(root, "wal");
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = DbName;
                opts.DatabaseDirectory = _dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            // No IWalFileIO registered → a real disk-backed WalFileIO, so the segments are on disk to damage and survive the reopen.
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = _walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = SegmentSize,
                    // ONE pre-allocated segment on purpose: a second .wal file then appears only when the writer genuinely rolls, which is how these tests
                    // detect the roll. Pre-allocating two makes both files exist from the start, so the roll becomes undetectable by file count and segment 1
                    // stays nearly empty — damage aimed at its middle would land in the zero tail, which the reader correctly reads as end-of-data, not corruption.
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
        try { Directory.Delete(Path.Combine(_dbDir, ".."), recursive: true); } catch { /* best-effort */ }
    }

    private string[] WalFiles() => Directory.Exists(_walDir) ? Directory.GetFiles(_walDir, "*.wal").OrderBy(p => p, StringComparer.Ordinal).ToArray() : [];

    /// <summary>Opens a WAL segment for reading while the engine still holds it — the writer keeps its handle open.</summary>
    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Bytes actually written into a segment, i.e. one past its last non-zero byte. Segments are PRE-ALLOCATED at full size and pre-created ahead of use, so
    /// neither the file's existence nor its length says anything about whether the writer has rolled into it — only real content does.
    /// </summary>
    private static long WrittenExtent(string path)
    {
        using var fs = OpenShared(path);
        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);

        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            if (bytes[i] != 0)
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Cheap "has the writer rotated into this segment yet?" probe for the fill loop. A pre-allocated segment is entirely zero-filled — pre-allocation writes no
    /// header — so any non-zero byte near the start means real content. Reads one page instead of the whole 512 KiB file, which matters when polled in a loop.
    /// </summary>
    private static bool SegmentHasData(string path)
    {
        using var fs = OpenShared(path);
        var probe = new byte[Math.Min(4096, fs.Length)];
        fs.ReadExactly(probe);

        foreach (var b in probe)
        {
            if (b != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns a file offset inside a REAL frame roughly halfway through the segment's written region — the place a mid-log break has to land to be a break.
    /// </summary>
    /// <remarks>
    /// A blind midpoint is not good enough. The writer issues every drain as its own O_DIRECT block padded up to 4096 bytes, so with small transactions most of
    /// each block is zero padding, and <c>AdvanceToNextFrame</c> deliberately treats a zero <c>FrameLength</c> as inter-block padding and jumps to the next
    /// block. Damage landing in that padding is therefore correctly ignored and proves nothing. Blocks are append-only at page-aligned offsets, so scan forward
    /// from the midpoint for the first aligned offset whose frame header is non-zero, then aim just past that header so the bytes hit the chunk itself.
    /// </remarks>
    private static long FindMidLogFrameOffset(string path, long writtenExtent)
    {
        using var fs = OpenShared(path);
        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);

        const int blockSize = 4096;
        var start = WalSegmentHeader.SizeInBytes + (((writtenExtent - WalSegmentHeader.SizeInBytes) / 2) / blockSize * blockSize);

        for (var offset = start; offset + blockSize <= writtenExtent; offset += blockSize)
        {
            // A live block begins with a frame header whose FrameLength is non-zero.
            var frameLength = BitConverter.ToInt32(bytes, (int)offset);
            if (frameLength is not 0 and not WalFrameHeader.PaddingSentinel)
            {
                return offset + WalFrameHeader.SizeInBytes + 8;   // inside the first chunk of that frame → footer CRC will not match
            }
        }

        Assert.Fail($"no live frame found in the second half of {Path.GetFileName(path)} (writtenExtent={writtenExtent})");
        return 0;
    }

    /// <summary>Commits filler transactions until the writer has genuinely rotated into a second segment (that segment holds bytes).</summary>
    private void FillUntilSecondSegmentHasData(DatabaseEngine dbe)
    {
        for (var round = 0; round < 400; round++)
        {
            for (var batch = 0; batch < 16; batch++)
            {
                using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
                for (var i = 0; i < SpawnsPerTx; i++)
                {
                    var c = new CompA(round * 1000 + batch * 32 + i, round, i);
                    tx.Spawn<CompAArch>(CompAArch.A.Set(in c));
                }
                tx.Commit();
            }

            var files = WalFiles();
            if (files.Length >= 2 && SegmentHasData(files[1]))
            {
                return;
            }
        }

        Assert.Fail("workload never rotated into a second WAL segment — the fixture cannot exercise a cross-segment boundary");
    }

    [Test]
    [CancelAfter(60_000)]
    public void MidLogCorruption_InSealedSegment_StopsRecoveryAndDropsLaterSegmentRecords()
    {
        EntityId beyondBoundary;

        // ── Session 1: fill segment 1, roll into segment 2, then write the record that must NOT survive ──
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            FillUntilSecondSegmentHasData(dbe);

            // This lands in the ACTIVE (second) segment — strictly after the boundary we are about to create in the first, sealed one.
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                var marker = new CompA(999_999, 42, 7);
                beyondBoundary = tx.Spawn<CompAArch>(CompAArch.A.Set(in marker));
                tx.Commit();
            }

            // Power cut: no shutdown checkpoint, no clean marker. Only fsynced WAL records survive, so the reopen must replay them.
            dbe.SimulateHardCrash();
        }

        // ── Damage the middle of the FIRST (now sealed) segment ──
        var segments = WalFiles();
        Assert.That(segments, Has.Length.GreaterThanOrEqualTo(2));

        var writtenExtent = WrittenExtent(segments[0]);
        Assert.That(writtenExtent, Is.GreaterThan(16 * 1024), "segment 1 must hold substantial data for a MID-log break to be meaningful");

        // Aim at the middle of the WRITTEN region — not a fixed offset, which risks landing in the pre-allocated zero tail where a zero FrameLength is
        // legitimately end-of-data rather than damage. Whatever this lands on (chunk body, footer CRC, or frame header) fails validation and raises WasTruncated.
        var corruptionOffset = FindMidLogFrameOffset(segments[0], writtenExtent);
        using (var fs = new FileStream(segments[0], FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(corruptionOffset, SeekOrigin.Begin);
            fs.Write(Enumerable.Repeat((byte)0xA5, 64).ToArray(), 0, 64);
            fs.Flush(true);
        }

        // ── Session 2: reopen and recover ──
        using var scope2 = _serviceProvider.CreateScope();
        using var dbe2 = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe2.RegisterComponentFromAccessor<CompA>();
        dbe2.InitializeArchetypes();

        var result = dbe2.LastWalV2RecoveryResult;

        Assert.Multiple(() =>
        {
            Assert.That(result.StoppedAtCorruption, Is.True,
                "recovery must record that it halted at a corruption boundary rather than at the natural end of the log");

            Assert.That(result.SegmentsScanned, Is.EqualTo(1),
                "the scan must stop at the damaged segment — continuing into segment 2 is the #587 defect");
        });

        using var readTx = dbe2.CreateQuickTransaction();
        Assert.That(readTx.IsAlive(beyondBoundary), Is.False,
            "a record written AFTER the corruption boundary has no CRC-chain guarantee and must not be replayed (REC-01)");
    }

    /// <summary>Control: an undamaged multi-segment log must scan every segment and never report a boundary stop.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void UndamagedMultiSegmentLog_ScansEverySegment_AndRecoversTheLastRecord()
    {
        EntityId lastWritten;

        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            FillUntilSecondSegmentHasData(dbe);

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                var marker = new CompA(999_999, 42, 7);
                lastWritten = tx.Spawn<CompAArch>(CompAArch.A.Set(in marker));
                tx.Commit();
            }

            dbe.SimulateHardCrash();
        }

        using var scope2 = _serviceProvider.CreateScope();
        using var dbe2 = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe2.RegisterComponentFromAccessor<CompA>();
        dbe2.InitializeArchetypes();

        var result = dbe2.LastWalV2RecoveryResult;

        Assert.Multiple(() =>
        {
            Assert.That(result.StoppedAtCorruption, Is.False,
                "a clean log must not be mistaken for a truncated one — a sealed pre-allocated segment ends in zeros, which is end-of-data, not damage");

            Assert.That(result.SegmentsScanned, Is.GreaterThanOrEqualTo(2),
                "the unconditional stop must not short-circuit normal multi-segment recovery");
        });

        using var readTx = dbe2.CreateQuickTransaction();
        Assert.That(readTx.IsAlive(lastWritten), Is.True, "the last committed record lives in the second segment and must be replayed");
    }
}
