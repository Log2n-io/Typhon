using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// End-to-end tests for the offline integrity scanner: it must call a healthy database sound, and it must find damage that
/// the engine's own load path either cannot see or can only respond to by refusing to open.
/// </summary>
/// <remarks>
/// Every test here builds a <b>real</b> database through the normal engine path, closes it, and then reads the bytes back
/// with no engine at all. That separation is the point of the feature and therefore the point of the fixture: a test that
/// scanned through a live engine would be testing something else.
/// </remarks>
[TestFixture]
internal sealed class IntegrityScannerTests
{
    private string _root;
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

            const int max = 63;
            const string prefix = "Tint_";
            if (prefix.Length + name.Length > max)
            {
                name = name[^(max - prefix.Length)..];
            }

            return prefix + name;
        }
    }

    private string DbDir => Path.Combine(_root, "db");

    private string BundlePath => Path.Combine(DbDir, $"{CurrentDatabaseName}.typhon");

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(IntegrityScannerTests), CurrentDatabaseName);
        Directory.CreateDirectory(DbDir);
        Directory.CreateDirectory(Path.Combine(_root, "wal"));

        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = CurrentDatabaseName;
                opts.DatabaseDirectory = DbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = Path.Combine(_root, "wal"),
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1
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

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    /// <summary>Builds a small, fully-committed database and closes it cleanly.</summary>
    private void BuildHealthyDatabase(int entityCount = 64)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();

        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            for (var i = 0; i < entityCount; i++)
            {
                using var tx = uow.CreateTransaction();
                var comp = new CompA(i + 1, i, i);
                tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                tx.Commit();
            }

            uow.Flush();
        }

        dbe.ForceCheckpoint();
    }

    private IntegrityReport Scan(ScanDepth depth = ScanDepth.Standard)
    {
        using var source = new OfflineBundlePageSource(BundlePath);
        return IntegrityScanner.Scan(source, new IntegrityOptions { Depth = depth });
    }

    private static void DamagePage(string bundlePath, int filePageIndex, Action<byte[]> mutate)
    {
        var dataPath = Path.Combine(bundlePath, IntegrityConstants.DataFileName);
        var page = new byte[IntegrityConstants.PageSize];
        using var fs = new FileStream(dataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek(filePageIndex * (long)IntegrityConstants.PageSize, SeekOrigin.Begin);
        fs.ReadExactly(page);
        mutate(page);
        fs.Seek(filePageIndex * (long)IntegrityConstants.PageSize, SeekOrigin.Begin);
        fs.Write(page);
    }

    // ── The baseline: a healthy database must be called sound ────────────────────────────────────────────────────────
    // This is the test that matters most. A checker that reports findings on a healthy database is worse than no checker,
    // because every real finding then arrives inside a haystack of noise nobody trusts.

    [Test]
    [CancelAfter(30_000)]
    public void HealthyDatabase_ScansSound()
    {
        BuildHealthyDatabase();
        _serviceProvider.Dispose();
        _serviceProvider = null;

        var report = Scan();

        Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Sound),
            "A cleanly-closed database must scan clean.\n" + IntegrityReportText.Render(report));
        Assert.That(report.Identity.Name, Is.EqualTo(CurrentDatabaseName), "the scanner must read the database name from page 0");
        Assert.That(report.Identity.MetaSlot, Is.AnyOf(0, 1), "one of the two meta slots must have been selected");
        Assert.That(report.Totals.SegmentsWalked, Is.GreaterThan(0), "a real database has segments; walking none means discovery failed");
        Assert.That(report.Totals.PagesScanned, Is.GreaterThan(0));
    }

    [Test]
    [CancelAfter(30_000)]
    public void HealthyDatabase_DeepScanIsAlsoSound()
    {
        BuildHealthyDatabase();
        _serviceProvider.Dispose();
        _serviceProvider = null;

        var report = Scan(ScanDepth.Deep);

        Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Sound),
            "Deep depth adds the occupancy cross-check, which must agree on a healthy database.\n" + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// The limits block is not optional and not suppressible, including on a green report. A report that says "Sound"
    /// without stating what it could not have seen is telling the operator something materially untrue.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void GreenReport_StillCarriesTheLimitsBlock()
    {
        BuildHealthyDatabase();
        _serviceProvider.Dispose();
        _serviceProvider = null;

        var report = Scan();
        var text = IntegrityReportText.Render(report);

        Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Sound));
        Assert.That(text, Does.Contain("LIMITS OF THIS SCAN"));
        Assert.That(text, Does.Contain("INTERNALLY CONSISTENT"), "the structural blind spot must be stated verbatim");
    }

    // ── Damage the scanner must find ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(30_000)]
    public void TornDataPage_IsReportedWithItsChecksums()
    {
        BuildHealthyDatabase();
        _serviceProvider.Dispose();
        _serviceProvider = null;

        // Pick an allocated, non-reserved page from the healthy scan so the damage lands somewhere that matters.
        var baseline = Scan();
        Assert.That(baseline.Verdict, Is.EqualTo(IntegrityVerdict.Sound), "precondition: the database starts healthy");

        var target = baseline.Identity.PageCount - 1;
        DamagePage(BundlePath, target, page => page[IntegrityConstants.PageHeaderSize + 16] ^= 0xFF);

        var report = Scan();

        Assert.That(report.Verdict, Is.Not.EqualTo(IntegrityVerdict.Sound), "a flipped byte inside a page must not scan clean");
        Assert.That(report.Findings.Any(f => f.Code == "CHK-PHY-01"), Is.True,
            "the checksum check must fire.\n" + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// The scenario that most justifies offline-first: both meta slots damaged. The engine refuses to open at all, so an
    /// engine-hosted checker is unavailable in exactly the case where someone most wants one. The offline scanner turns a
    /// total loss into a diagnosis.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void BothMetaSlotsCorrupt_IsDiagnosedRatherThanUnopenable()
    {
        BuildHealthyDatabase();
        _serviceProvider.Dispose();
        _serviceProvider = null;

        DamagePage(BundlePath, 0, page => page.AsSpan(200, 64).Fill(0xAB));
        DamagePage(BundlePath, 1, page => page.AsSpan(200, 64).Fill(0xCD));

        var report = Scan();

        Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Unopenable));
        var finding = report.Findings.FirstOrDefault(f => f.Code == "CHK-BOO-03");
        Assert.That(finding, Is.Not.Null, "the meta-pair check must fire.\n" + IntegrityReportText.Render(report));
        Assert.That(finding.Detail, Does.Contain("Slot 0"), "the report must say what was wrong with EACH slot, not just that it failed");
        Assert.That(finding.Detail, Does.Contain("Slot 1"));
    }

    /// <summary>
    /// One damaged meta slot is survivable by design — that is what the A/B pair is for — so the scan must still succeed
    /// and pick the good slot, while saying out loud that the database is now running without a fallback.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void OneMetaSlotCorrupt_StillReadsTheOther()
    {
        BuildHealthyDatabase();
        _serviceProvider.Dispose();
        _serviceProvider = null;

        var baseline = Scan();
        var goodSlot = baseline.Identity.MetaSlot;
        var badSlot = 1 - goodSlot;

        DamagePage(BundlePath, badSlot, page => page.AsSpan(200, 64).Fill(0xAB));

        var report = Scan();

        Assert.That(report.Identity.MetaSlot, Is.EqualTo(goodSlot), "the surviving slot must still be selected");
        Assert.That(report.Identity.Name, Is.EqualTo(CurrentDatabaseName), "identity must still be readable from the good slot");
        Assert.That(report.Verdict, Is.Not.EqualTo(IntegrityVerdict.Unopenable), "one bad slot is not fatal");
        Assert.That(report.Findings.Any(f => f.Code == "CHK-BOO-03"), Is.True, "but it must be reported");
    }

    [Test]
    [CancelAfter(30_000)]
    public void NotATyphonDatabase_IsRejectedWithoutCrashing()
    {
        BuildHealthyDatabase();
        _serviceProvider.Dispose();
        _serviceProvider = null;

        // Overwrite the signature in both slots but keep each page's checksum valid, so the scanner reaches the identity
        // check rather than stopping at the pair check.
        for (var slot = 0; slot <= 1; slot++)
        {
            DamagePage(BundlePath, slot, page =>
            {
                page.AsSpan(PagedMMF.PageBaseHeaderSize, 32).Clear();
                System.Text.Encoding.UTF8.GetBytes("NotTyphon").CopyTo(page.AsSpan(PagedMMF.PageBaseHeaderSize));
                var crc = Crc32CUtil.ComputeSkipping(page, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
                BitConverter.GetBytes(crc).CopyTo(page.AsSpan(PageBaseHeader.PageChecksumOffset));
            });
        }

        var report = Scan();

        Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Unopenable));
        Assert.That(report.Findings.Any(f => f.Code == "CHK-BOO-02"), Is.True,
            "the identity check must fire.\n" + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// A checker that crashes on the input it exists to diagnose is worse than useless, so garbage must produce a
    /// <i>report</i>, never an exception. This is the traversal-safety requirement stated as a test.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void TotalGarbage_ProducesAReportRatherThanAnException()
    {
        BuildHealthyDatabase();
        _serviceProvider.Dispose();
        _serviceProvider = null;

        var dataPath = Path.Combine(BundlePath, IntegrityConstants.DataFileName);
        var length = new FileInfo(dataPath).Length;
        var random = new Random(20260809);
        var noise = new byte[length];
        random.NextBytes(noise);
        File.WriteAllBytes(dataPath, noise);

        IntegrityReport report = null;
        Assert.DoesNotThrow(() => report = Scan(ScanDepth.Deep), "a scan of pure noise must not throw");
        Assert.That(report, Is.Not.Null);
        Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Unopenable));
    }

    [Test]
    [CancelAfter(30_000)]
    public void TruncatedFile_IsReported()
    {
        BuildHealthyDatabase();
        _serviceProvider.Dispose();
        _serviceProvider = null;

        var dataPath = Path.Combine(BundlePath, IntegrityConstants.DataFileName);
        using (var fs = new FileStream(dataPath, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(fs.Length - 1024);
        }

        var report = Scan();

        Assert.That(report.Findings.Any(f => f.Code == "CHK-BOO-01"), Is.True,
            "a file truncated mid-page must be reported.\n" + IntegrityReportText.Render(report));
    }

    // ── Report shape ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(30_000)]
    public void Report_RendersAsValidJson()
    {
        BuildHealthyDatabase();
        _serviceProvider.Dispose();
        _serviceProvider = null;

        var report = Scan();
        var json = IntegrityReportJson.Render(report);

        Assert.DoesNotThrow(() => System.Text.Json.JsonDocument.Parse(json), "the report's JSON form must parse:\n" + json);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("verdict").GetString(), Is.EqualTo("Sound"));
        Assert.That(doc.RootElement.GetProperty("reportVersion").GetInt32(), Is.EqualTo(IntegrityReport.ReportVersion));
        Assert.That(doc.RootElement.GetProperty("limits").GetProperty("structural").GetString(), Is.Not.Empty,
            "the limits block must survive into the machine-readable form too");
    }

    [Test]
    [CancelAfter(30_000)]
    public void ExitCodes_DistinguishDivergenceFromDataLoss()
    {
        BuildHealthyDatabase();
        _serviceProvider.Dispose();
        _serviceProvider = null;

        var sound = Scan();
        Assert.That(sound.ExitCode, Is.EqualTo(0), "sound must be 0 so a CI gate passes");

        DamagePage(BundlePath, 0, page => page.AsSpan(200, 64).Fill(0xAB));
        DamagePage(BundlePath, 1, page => page.AsSpan(200, 64).Fill(0xCD));

        Assert.That(Scan().ExitCode, Is.EqualTo(4), "unopenable must be distinguishable from every lesser verdict");
    }

    // ── Verify on open ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The clean-shutdown flag records that the last process closed properly, not that the bytes survived. A database
    /// damaged while it was closed must be refused rather than served — that is the whole decision behind verify-on-open.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void DatabaseDamagedWhileClosed_IsRefusedOnOpen()
    {
        BuildHealthyDatabase();
        _serviceProvider.Dispose();
        _serviceProvider = null;

        DamagePage(BundlePath, 0, page => page.AsSpan(200, 64).Fill(0xAB));
        DamagePage(BundlePath, 1, page => page.AsSpan(200, 64).Fill(0xCD));

        using var reopened = BuildProviderWithoutDeleting();
        var ex = Assert.Catch<Exception>(() =>
        {
            using var scope = reopened.CreateScope();
            scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        });

        var integrity = FindIntegrityException(ex);
        Assert.That(integrity, Is.Not.Null, "the open must fail with the integrity report attached, not a bare error");
        Assert.That(integrity.Report.Verdict, Is.EqualTo(IntegrityVerdict.Unopenable));
        Assert.That(integrity.Message, Does.Contain("typhon check"), "and must tell the operator what to run next");
    }

    /// <summary>
    /// A database with a merely degraded structure still opens. Refusing it would be the cure being worse than the
    /// disease — the point of the tier is to catch what makes opening actively harmful, not to be maximally strict.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void NonFatalFindings_DoNotBlockTheOpen()
    {
        BuildHealthyDatabase();
        _serviceProvider.Dispose();
        _serviceProvider = null;

        var baseline = Scan();
        DamagePage(BundlePath, 1 - baseline.Identity.MetaSlot, page => page.AsSpan(300, 128).Fill(0xAB));

        Assert.That(Scan().Findings.Any(f => f.Severity == IntegritySeverity.Divergence), Is.True, "precondition: a non-fatal finding exists");

        using var reopened = BuildProviderWithoutDeleting();
        Assert.DoesNotThrow(() =>
        {
            using var scope = reopened.CreateScope();
            scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        }, "one degraded meta slot must not stop the database opening from the other");
    }

    [Test]
    [CancelAfter(30_000)]
    public void VerificationCanBeTurnedOff()
    {
        BuildHealthyDatabase();
        _serviceProvider.Dispose();
        _serviceProvider = null;

        DamagePage(BundlePath, 0, page => page.AsSpan(200, 64).Fill(0xAB));
        DamagePage(BundlePath, 1, page => page.AsSpan(200, 64).Fill(0xCD));

        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = CurrentDatabaseName;
                opts.DatabaseDirectory = DbDir;
                opts.VerifyOnOpen = OpenVerification.None;
            });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // With verification off the open still fails — but through the engine's own meta-pair selection, not the checker.
        // The escape hatch removes the instrument, not the underlying protection.
        var ex = Assert.Catch<Exception>(() => scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>());
        Assert.That(FindIntegrityException(ex), Is.Null, "with verification off, the checker must not be what refuses the open");
    }

    /// <summary>
    /// Builds a provider over the SAME on-disk database without deleting it first. <see cref="Setup"/> deliberately wipes
    /// the file so each test starts clean; a reopen test must not, or it would be opening a database it just recreated.
    /// </summary>
    private ServiceProvider BuildProviderWithoutDeleting()
    {
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = CurrentDatabaseName;
                opts.DatabaseDirectory = DbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            });

        return services.BuildServiceProvider();
    }

    private static DatabaseIntegrityException FindIntegrityException(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is DatabaseIntegrityException integrity)
            {
                return integrity;
            }
        }

        return null;
    }

    /// <summary>
    /// The spine tier is what runs on every open, so it must be bounded by segment count rather than database size. This
    /// asserts the shape (it does not read page bodies) rather than a wall-clock number, which would be flaky.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void SpineTier_DoesNotSweepPages()
    {
        BuildHealthyDatabase(256);
        _serviceProvider.Dispose();
        _serviceProvider = null;

        using var source = new OfflineBundlePageSource(BundlePath);
        var report = IntegrityScanner.VerifySpine(source);

        Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Sound));
        Assert.That(report.Totals.PagesScanned, Is.Zero, "the spine tier must not sweep page bodies");
        Assert.That(report.Totals.SegmentsWalked, Is.GreaterThan(0), "but it must still resolve every segment");
        Assert.That(report.Limits.ChecksSkipped, Is.Not.Empty, "and it must say which checks it skipped");
    }
}
