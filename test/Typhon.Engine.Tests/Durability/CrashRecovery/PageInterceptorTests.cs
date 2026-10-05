using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Phase-A unit tests for the <see cref="PagedMMF"/> crash-injection interceptors and <see cref="ChaosPageIO"/> (P1.5, T-4).
/// Verifies the record-and-throw mechanism in isolation (no engine): writes are recorded in order, a crash count throws,
/// the null path is unaffected (real write→read round-trips), and <see cref="ChaosPageIO.DamagePageOnDisk"/> corrupts a real page.
/// </summary>
[TestFixture]
public class PageInterceptorTests : AllocatorTestBase
{
    private EpochManager _epochManager;
    private ManagedPagedMMFOptions _options;
    private ManagedPagedMMF _mmf;

    private static string DbName => $"T_PageIntercept_{TestSeed.StableHash(TestContext.CurrentContext.Test.Name):X8}";

    public override void Setup()
    {
        base.Setup();
        _epochManager = new EpochManager("PageInterceptEpoch", AllocationResource);
        _options = new ManagedPagedMMFOptions
        {
            DatabaseDirectory = TestDatabaseDir,
            DatabaseName = DbName,
            DatabaseCacheSize = PagedMMF.MinimumCacheSize,
        };
    }

    public override void TearDown()
    {
        _mmf?.Dispose();
        _mmf = null;
        try
        {
            _options.EnsureFileDeleted();
        }
        catch
        {
            // best-effort cleanup
        }

        base.TearDown();
    }

    private ManagedPagedMMF Open(bool fresh)
    {
        if (fresh)
        {
            _options.EnsureFileDeleted();
        }

        var logger = ServiceProvider.GetRequiredService<ILogger<PagedMMF>>();
        return new ManagedPagedMMF(ResourceRegistry, _epochManager, MemoryAllocator, _options, AllocationResource, "PageInterceptMMF", logger);
    }

    private static byte[] Page(byte fill)
    {
        var p = new byte[PagedMMF.PageSize];
        p.AsSpan().Fill(fill);
        return p;
    }

    [Test]
    [CancelAfter(5000)]
    public void Interceptor_RecordsWritesInPhysicalOrder()
    {
        _mmf = Open(fresh: true);
        var chaos = new ChaosPageIO();
        chaos.WireTo(_mmf);   // wire AFTER genesis so only our explicit writes are recorded

        _mmf.WritePageDirect(40, Page(0x11));
        _mmf.WritePageDirect(50, Page(0x22));
        _mmf.WritePageDirect(45, Page(0x33));

        Assert.That(chaos.WrittenPages, Is.EqualTo(new[] { 40, 50, 45 }), "interceptor records every physical write in order");
        Assert.That(chaos.HasCrashed, Is.False);
    }

    [Test]
    [CancelAfter(5000)]
    public void Interceptor_CrashAtNthWrite_ThrowsAndSkipsThatWrite()
    {
        _mmf = Open(fresh: true);
        var chaos = new ChaosPageIO();
        chaos.WireTo(_mmf);
        chaos.SetCrashAtPageWrite(2);   // the 2nd write must abort

        _mmf.WritePageDirect(40, Page(0x11));   // 1st — lands

        Assert.That(
            () => _mmf.WritePageDirect(50, Page(0x22)),
            Throws.TypeOf<ChaosSimulatedCrashException>(),
            "the 2nd physical write throws the simulated crash");

        Assert.That(chaos.HasCrashed, Is.True);
        Assert.That(chaos.WrittenPages, Is.EqualTo(new[] { 40 }), "only the pre-crash write was recorded; the crashing write never landed");
    }

    [Test]
    [CancelAfter(5000)]
    public void NullInterceptor_RealWriteReadRoundTrips()
    {
        _mmf = Open(fresh: true);
        // No ChaosPageIO wired → PageWriteInterceptor is null → the real RandomAccess path runs.
        var written = Page(0xAB);
        _mmf.WritePageDirect(60, written);

        var readBack = new byte[PagedMMF.PageSize];
        _mmf.ReadPageDirect(60, readBack);

        Assert.That(readBack, Is.EqualTo(written), "with no interceptor the real write/read path is unaffected");
    }

    private static int ChangeRevisionOnDisk(ManagedPagedMMF mmf, int filePageIndex)
    {
        var buf = new byte[PagedMMF.PageSize];
        mmf.ReadPageDirect(filePageIndex, buf);
        return MemoryMarshal.AsRef<PageBaseHeader>(buf.AsSpan(PageBaseHeader.Offset)).ChangeRevision;
    }

    /// <summary>
    /// #978: a crash injected between two writes of one <c>SavePages</c> batch must not leave the first write running behind the throw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An issued write reads its page from the cache when it RUNS. On Linux a pool thread runs the pwrite, so a write abandoned by the throw could
    /// run after the engine was torn down and copy whatever then lived at the freed cache address. The seeded nightly hit it: a zeroed page landed
    /// over the first data page of <c>ArchetypeR1</c>'s segment, and the reopen failed "integrity check failed at Load: root=40 ... directory=4
    /// chain=2".
    /// </para>
    /// <para>
    /// Staged, not raced: a large write from another handle holds the file's inode lock, which every Linux buffered write takes before copying
    /// from its buffer, so the batch's first write is parked until that write ends. The handshake is the file length, which grows only while the
    /// blocker is inside the kernel holding the lock. The revision check reads the page while the blocker may still run, which ext4 — the gate's
    /// and the nightly's filesystem — serves without the inode lock. Linux only because Windows has no such lock to stage with; the hazard itself
    /// is not Linux-only, since Windows may also complete a cached overlapped write later, on a worker thread.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(10_000)]
    [Platform("Linux")]
    public void CrashBetweenSavePagesWrites_IssuedWritesLandBeforeTheThrow()
    {
        _mmf = Open(fresh: true);

        // Two pages that cannot share a write run, so the batch issues two writes; both on disk already, so each has a revision to compare with.
        var cs0 = _mmf.CreateChangeSet();
        var segment = _mmf.AllocateChunkBasedSegment(PageBlockType.None, 4, 64, cs0);
        cs0.SaveChanges();
        var cs = _mmf.CreateChangeSet();
        using (var guard = EpochGuard.Enter(_epochManager))
        {
            foreach (var filePageIndex in new[] { segment.Pages[1], segment.Pages[3] })
            {
                _mmf.RequestPageEpoch(filePageIndex, guard.Epoch, out var memPageIndex);
                cs.AddByMemPageIndex(memPageIndex);
            }
        }

        var path = _options.BuildDatabasePathFileName();
        var originalLength = new FileInfo(path).Length;
        using var blockerHandle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var chunk = new ReadOnlyMemory<byte>(new byte[1 << 20]);
        var gather = new List<ReadOnlyMemory<byte>>();
        // One 64 MiB gather write — one syscall, one lock hold — from a 1 MiB buffer. Measured on ext4: it failed the unfixed engine 16 times out of
        // 16, 8 of them on a single CPU; with the fix the test takes 50–250 ms.
        for (var i = 0; i < 64; i++)
        {
            gather.Add(chunk);
        }

        // A dedicated thread, so it must not throw: an unhandled exception there would take the test host down, not fail this test.
        using var blockerDone = new ManualResetEventSlim();
        Exception blockerError = null;
        var blockerStarted = false;
        var blocker = new Thread(() =>
        {
            try
            {
                RandomAccess.Write(blockerHandle, gather, 64L << 30);
            }
            catch (Exception ex)
            {
                blockerError = ex;
            }
            finally
            {
                blockerDone.Set();
            }
        });

        var firstWrite = -1;
        var firstWriteRevisionBefore = 0;
        var blockerInKernel = false;
        var blockerHeldAtSecondWrite = false;
        _mmf.PageWriteInterceptor = filePageIndex =>
        {
            if (firstWrite < 0)
            {
                firstWrite = filePageIndex;
                firstWriteRevisionBefore = ChangeRevisionOnDisk(_mmf, filePageIndex);
                blocker.Start();
                blockerStarted = true;
                var deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
                while (!blockerDone.IsSet && Stopwatch.GetTimestamp() < deadline)
                {
                    if (RandomAccess.GetLength(blockerHandle) > originalLength)
                    {
                        blockerInKernel = true;
                        break;
                    }
                    Thread.Yield();
                }
                return;
            }

            blockerHeldAtSecondWrite = !blockerDone.IsSet;
            throw new ChaosSimulatedCrashException(2, IoSubsystem.DataFile);
        };

        ChaosSimulatedCrashException crash = null;
        var firstWriteRevisionAtThrow = 0;
        try
        {
            cs.SaveChanges();
        }
        catch (ChaosSimulatedCrashException ex)
        {
            // First thing after the throw: the blocker may release the lock at any moment from here on.
            firstWriteRevisionAtThrow = ChangeRevisionOnDisk(_mmf, firstWrite);
            crash = ex;
        }
        finally
        {
            _mmf.PageWriteInterceptor = null;
            if (blockerStarted)
            {
                Assert.That(blocker.Join(TimeSpan.FromSeconds(5)), Is.True, "the blocker's write must end");
            }
        }

        Assert.That(crash, Is.Not.Null, "the second write's crash surfaces from SaveChanges");
        Assert.That(blockerError, Is.Null, "the blocker's write must succeed, or nothing was staged");
        Assert.That(blockerInKernel && blockerHeldAtSecondWrite, Is.True,
            "precondition: the blocker was inside its write, holding the inode lock, from before the first write was issued until the batch reached the second");
        Assert.That(firstWriteRevisionAtThrow, Is.Not.EqualTo(firstWriteRevisionBefore),
            $"page {firstWrite}'s write was issued before the crash but had not landed when SaveChanges threw — it was left running behind the throw");
    }

    [Test]
    [CancelAfter(5000)]
    public void DamagePageOnDisk_TornPage_CorruptsSecondHalf()
    {
        _mmf = Open(fresh: true);
        _mmf.WritePageDirect(70, Page(0x5A));
        _mmf.FlushToDisk();
        _mmf.Dispose();
        _mmf = null;

        var path = _options.BuildDatabasePathFileName();
        ChaosPageIO.DamagePageOnDisk(path, 70, PageDamageType.TornPage, PagedMMF.PageSize);

        var raw = File.ReadAllBytes(path);
        var pageStart = 70 * PagedMMF.PageSize;
        var half = PagedMMF.PageSize / 2;
        Assert.That(raw[pageStart], Is.EqualTo(0x5A), "first half is preserved");
        Assert.That(raw[pageStart + half], Is.EqualTo(0xFF), "second half is torn to 0xFF");
        Assert.That(raw[pageStart + PagedMMF.PageSize - 1], Is.EqualTo(0xFF), "torn through the end of the page");
    }
}
