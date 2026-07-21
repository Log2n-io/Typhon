using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="PagedMMF.EnsurePageVerified"/> — the lazy CRC verification on page load.
/// Uses a standalone ManagedPagedMMF (no WAL) to isolate CRC verification behavior.
/// </summary>
[TestFixture]
public class PageCrcVerificationTests : AllocatorTestBase
{
    private ManagedPagedMMF _mmf;
    private EpochManager _epochManager;

    private static string CurrentDatabaseName => $"T_CrcVerify_{TestContext.CurrentContext.Test.Name}_db";

    public override void Setup()
    {
        base.Setup();
    }

    public override void TearDown()
    {
        _mmf?.Dispose();
        _mmf = null;
        base.TearDown();
    }

    private void CreateMmf()
    {
        _epochManager = new EpochManager("TestEpochManager", AllocationResource);

        var logger = ServiceProvider.GetRequiredService<ILogger<PagedMMF>>();
        var options = new ManagedPagedMMFOptions
        {
            DatabaseDirectory = TestDatabaseDir,
            DatabaseName = CurrentDatabaseName,
            DatabaseCacheSize = PagedMMF.MinimumCacheSize,
        };
        options.EnsureFileDeleted();

        _mmf = new ManagedPagedMMF(ResourceRegistry, _epochManager, MemoryAllocator, options, AllocationResource, "TestMMF", logger);
    }

    /// <summary>
    /// Creates a page buffer with a valid CRC32C checksum.
    /// </summary>
    private static byte[] BuildPageWithCrc(byte fillByte = 0xAA)
    {
        var page = new byte[PagedMMF.PageSize];

        ref var header = ref Unsafe.As<byte, PageBaseHeader>(ref page[0]);
        header.Flags = PageBlockFlags.None;
        header.Type = PageBlockType.None;
        header.FormatRevision = 1;
        header.ChangeRevision = 1;
        header.ModificationCounter = 0;

        for (int i = PagedMMF.PageHeaderSize; i < PagedMMF.PageSize; i++)
        {
            page[i] = fillByte;
        }

        var crc = Crc32CUtil.ComputeSkipping(page, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
        Unsafe.As<byte, uint>(ref page[PageBaseHeader.PageChecksumOffset]) = crc;

        return page;
    }

    // ═══════════════════════════════════════════════════════════════
    // Valid CRC — verification passes
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void ValidCrc_VerifiedWithoutError()
    {
        CreateMmf();

        const int filePageIndex = 3;
        var goodPage = BuildPageWithCrc(0xBB);

        // Write a valid page to disk
        _mmf.WritePageDirect(filePageIndex, goodPage);

        // Enable OnLoad verification
        _mmf.SetPageChecksumVerification(PageChecksumVerification.OnLoad);

        // Request page — should pass verification without error
        using var guard = EpochGuard.Enter(_epochManager);
        var epoch = guard.Epoch;
        _mmf.RequestPageEpoch(filePageIndex, epoch, out _);
        // If we get here without exception, the test passes
    }

    // ═══════════════════════════════════════════════════════════════
    // Zero CRC — verification skipped
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void ZeroCrc_Skipped()
    {
        CreateMmf();

        const int filePageIndex = 3;

        // Write a page with CRC = 0 (never checkpointed)
        var page = new byte[PagedMMF.PageSize];
        ref var header = ref Unsafe.As<byte, PageBaseHeader>(ref page[0]);
        header.FormatRevision = 1;
        header.ChangeRevision = 1;
        // PageChecksum = 0 by default

        // Write some data that doesn't match any CRC
        for (int i = PagedMMF.PageHeaderSize; i < PagedMMF.PageSize; i++)
        {
            page[i] = 0xCC;
        }

        _mmf.WritePageDirect(filePageIndex, page);
        _mmf.SetPageChecksumVerification(PageChecksumVerification.OnLoad);

        using var guard = EpochGuard.Enter(_epochManager);
        var epoch = guard.Epoch;
        _mmf.RequestPageEpoch(filePageIndex, epoch, out _);
        // If we get here without exception, zero CRC was correctly skipped
    }

    // ═══════════════════════════════════════════════════════════════
    // RecoveryOnly mode — verification skipped
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void RecoveryOnlyMode_Skipped()
    {
        CreateMmf();

        const int filePageIndex = 3;

        // Write a page with a WRONG CRC (would fail OnLoad verification)
        var page = BuildPageWithCrc(0xAA);
        // Corrupt data so CRC mismatches
        page[PagedMMF.PageHeaderSize] ^= 0xFF;

        _mmf.WritePageDirect(filePageIndex, page);

        // RecoveryOnly mode — should skip verification
        _mmf.SetPageChecksumVerification(PageChecksumVerification.RecoveryOnly);

        using var guard = EpochGuard.Enter(_epochManager);
        var epoch = guard.Epoch;
        _mmf.RequestPageEpoch(filePageIndex, epoch, out _);
        // If we get here without exception, RecoveryOnly mode correctly skipped verification
    }

    // ═══════════════════════════════════════════════════════════════
    // File page 0 (root page) — verification skipped
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void FilePageZero_Skipped()
    {
        CreateMmf();

        // Page 0 is always the root file header — verification is skipped
        _mmf.SetPageChecksumVerification(PageChecksumVerification.OnLoad);

        using var guard = EpochGuard.Enter(_epochManager);
        var epoch = guard.Epoch;
        _mmf.RequestPageEpoch(0, epoch, out _);
        // If we get here without exception, file page 0 was correctly skipped
    }

    // ═══════════════════════════════════════════════════════════════
    // CRC mismatch, no WAL — throws PageCorruptionException
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void CrcMismatch_NoWal_ThrowsPageCorruptionException()
    {
        CreateMmf();

        const int filePageIndex = 3;

        // Write a page with valid CRC, then corrupt the data
        var page = BuildPageWithCrc(0xAA);
        page[PagedMMF.PageHeaderSize] ^= 0xFF; // Corrupt one byte → CRC mismatch

        _mmf.WritePageDirect(filePageIndex, page);
        _mmf.SetPageChecksumVerification(PageChecksumVerification.OnLoad);

        // No WAL manager configured — FPI repair is unavailable
        PageCorruptionException caught = null;
        try
        {
            using var guard = EpochGuard.Enter(_epochManager);
            var epoch = guard.Epoch;
            _mmf.RequestPageEpoch(filePageIndex, epoch, out _);
            Assert.Fail("Expected PageCorruptionException was not thrown");
        }
        catch (PageCorruptionException ex)
        {
            caught = ex;
        }

        Assert.That(caught, Is.Not.Null);
        Assert.That(caught.PageIndex, Is.EqualTo(filePageIndex));
        Assert.That(caught.ErrorCode, Is.EqualTo(TyphonErrorCode.PageChecksumMismatch));
        Assert.That(caught.ExpectedCrc, Is.Not.EqualTo(0));
        Assert.That(caught.ComputedCrc, Is.Not.EqualTo(caught.ExpectedCrc));
    }
}
