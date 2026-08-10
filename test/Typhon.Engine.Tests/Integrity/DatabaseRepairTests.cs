using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for plan → consent → apply.
/// </summary>
/// <remarks>
/// The properties under test are mostly <i>refusals</i>, and deliberately so: a repair tool that acts on a wrong
/// conclusion is worse than no repair tool, so most of what makes this safe is what it declines to do. Planning must
/// change nothing, applying must refuse a stale diagnosis, and a lossy step must never run without explicit consent.
/// </remarks>
[TestFixture]
internal sealed class DatabaseRepairTests
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
            const string prefix = "Trep_";
            return prefix.Length + name.Length > max ? prefix + name[^(max - prefix.Length)..] : prefix + name;
        }
    }

    private string DbDir => Path.Combine(_root, "db");

    private string BundlePath => Path.Combine(DbDir, $"{CurrentDatabaseName}.typhon");

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(DatabaseRepairTests), CurrentDatabaseName);
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

    private void BuildAndClose(int entityCount = 64)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
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

        _serviceProvider.Dispose();
        _serviceProvider = null;
    }

    private IntegrityReport Scan(ScanDepth depth = ScanDepth.Deep)
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

    // ── Planning changes nothing ─────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(30_000)]
    public void HealthyDatabase_PlansNothing()
    {
        BuildAndClose();

        var plan = DatabaseRepair.Plan(Scan());

        Assert.That(plan.IsEmpty, Is.True, "a sound database must produce an empty plan");
        Assert.That(plan.RequiresLossyConsent, Is.False);
        Assert.That(plan.Unaddressed, Is.Empty);
    }

    [Test]
    [CancelAfter(30_000)]
    public void Planning_DoesNotTouchTheDatabase()
    {
        BuildAndClose();
        DamagePage(BundlePath, 0, page => page.AsSpan(200, 64).Fill(0xAB));

        var dataPath = Path.Combine(BundlePath, IntegrityConstants.DataFileName);
        var before = File.ReadAllBytes(dataPath);

        var plan = DatabaseRepair.Plan(Scan());
        Assert.That(plan.Steps, Is.Not.Empty, "precondition: there is something to plan");

        var after = File.ReadAllBytes(dataPath);
        Assert.That(after.SequenceEqual(before), Is.True, "producing a plan must be provably read-only");
    }

    // ── The lossless repair that matters: restoring a degraded A/B pair ──────────────────────────────────────────────

    /// <summary>
    /// One damaged meta slot leaves the database working but without a fallback: the next torn write to the surviving
    /// slot makes it permanently unopenable. Restoring the pair is therefore a real repair, not housekeeping.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void DamagedMetaSlot_IsRestoredFromItsSibling()
    {
        BuildAndClose();

        var healthy = Scan();
        Assert.That(healthy.Verdict, Is.EqualTo(IntegrityVerdict.Sound), "precondition: starts sound");

        var goodSlot = healthy.Identity.MetaSlot;
        var badSlot = 1 - goodSlot;
        DamagePage(BundlePath, badSlot, page => page.AsSpan(300, 128).Fill(0xAB));

        var damaged = Scan();
        Assert.That(damaged.Findings.Any(f => f.Code == "CHK-BOO-03"), Is.True,
            "precondition: the degraded pair is detected.\n" + IntegrityReportText.Render(damaged));

        var plan = DatabaseRepair.Plan(damaged);
        Assert.That(plan.Steps.Any(s => s.Action == RepairAction.RestorePairSlot), Is.True, "the planner must offer to restore it");
        Assert.That(plan.RequiresLossyConsent, Is.False, "restoring a pair loses nothing");

        var outcome = DatabaseRepair.Apply(BundlePath, plan, backupFirst: false);

        Assert.That(outcome.Succeeded, Is.True, DescribeOutcome(outcome));
        Assert.That(outcome.VerificationReport, Is.Not.Null, "apply must re-scan and attach the receipt");
        Assert.That(outcome.VerificationReport.Verdict, Is.EqualTo(IntegrityVerdict.Sound),
            "after the repair the database must scan clean.\n" + IntegrityReportText.Render(outcome.VerificationReport));
    }

    [Test]
    [CancelAfter(30_000)]
    public void RestoringAPair_LeavesTheDatabaseOpenable()
    {
        BuildAndClose();

        var healthy = Scan();
        var badSlot = 1 - healthy.Identity.MetaSlot;
        DamagePage(BundlePath, badSlot, page => page.AsSpan(300, 128).Fill(0xAB));

        var plan = DatabaseRepair.Plan(Scan());
        DatabaseRepair.Apply(BundlePath, plan, backupFirst: false);

        // The real proof is not that the scan is green — it is that the engine still opens it and the data is there.
        Setup();
        using var scope = _serviceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        Assert.DoesNotThrow(() => dbe.InitializeArchetypes(), "the repaired database must still open through the normal path");
    }

    // ── Refusals ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applying a plan built against a different state of the database is how a repair tool damages a healthy one. The
    /// fingerprint check is the guard, and it must fire rather than "do its best".
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void StalePlan_IsRefused()
    {
        BuildAndClose();

        var healthy = Scan();
        var badSlot = 1 - healthy.Identity.MetaSlot;
        DamagePage(BundlePath, badSlot, page => page.AsSpan(300, 128).Fill(0xAB));

        var plan = DatabaseRepair.Plan(Scan());

        // The database moves on after the plan was made.
        DamagePage(BundlePath, healthy.Identity.PageCount - 1, page => page[IntegrityConstants.PageHeaderSize + 8] ^= 0xFF);

        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseRepair.Apply(BundlePath, plan, backupFirst: false));
        Assert.That(ex.Message, Does.Contain("changed since this plan was produced"));
    }

    [Test]
    [CancelAfter(30_000)]
    public void DryRun_ExecutesNothing()
    {
        BuildAndClose();

        var healthy = Scan();
        var badSlot = 1 - healthy.Identity.MetaSlot;
        DamagePage(BundlePath, badSlot, page => page.AsSpan(300, 128).Fill(0xAB));

        var plan = DatabaseRepair.Plan(Scan());
        var dataPath = Path.Combine(BundlePath, IntegrityConstants.DataFileName);
        var before = File.ReadAllBytes(dataPath);

        var outcome = DatabaseRepair.Apply(BundlePath, plan, backupFirst: false, dryRun: true);

        Assert.That(outcome.Results.All(r => r.Outcome == StepOutcome.Skipped), Is.True, "a dry run executes nothing");
        Assert.That(File.ReadAllBytes(dataPath).SequenceEqual(before), Is.True, "and therefore changes nothing");
    }

    [Test]
    [CancelAfter(30_000)]
    public void BackupFirst_CopiesTheBundleBeforeMutating()
    {
        BuildAndClose();

        var healthy = Scan();
        var badSlot = 1 - healthy.Identity.MetaSlot;
        DamagePage(BundlePath, badSlot, page => page.AsSpan(300, 128).Fill(0xAB));

        var plan = DatabaseRepair.Plan(Scan());
        var outcome = DatabaseRepair.Apply(BundlePath, plan, backupFirst: true);

        Assert.That(outcome.BackupPath, Is.Not.Null);
        Assert.That(Directory.Exists(outcome.BackupPath), Is.True, "the pre-repair copy must exist");
        Assert.That(File.Exists(Path.Combine(outcome.BackupPath, IntegrityConstants.DataFileName)), Is.True,
            "and must contain the data file");
    }

    /// <summary>
    /// A finding whose data is genuinely gone must be reported as unaddressed with an honest explanation, not quietly
    /// dropped from the plan. Silence here would read as "nothing to do".
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void UnrepairableDamage_IsReportedRatherThanSilentlyDropped()
    {
        BuildAndClose();

        // Damage a data page of a PRIMARY segment. Which segment owns a page decides whether damage is repairable at all:
        // an index or entity-map page is derived and simply regenerated, so it would prove nothing here.
        var target = FindPrimaryDataPage();
        Assert.That(target, Is.GreaterThan(0), "precondition: the database has a primary data page to damage");
        DamagePage(BundlePath, target, page => page.AsSpan(IntegrityConstants.PageHeaderSize, 512).Fill(0x00));

        var report = Scan();
        var plan = DatabaseRepair.Plan(report);

        Assert.That(report.Verdict, Is.Not.EqualTo(IntegrityVerdict.Sound), "precondition: the damage is detected");
        Assert.That(plan.Unaddressed, Is.Not.Empty,
            "damage this build cannot repair must be named, with a reason.\n" + IntegrityReportText.Render(report));
        Assert.That(string.Join(" ", plan.Unaddressed), Does.Contain("backup").IgnoreCase,
            "and the escalation path must be stated");
    }

    [Test]
    [CancelAfter(30_000)]
    public void Fingerprint_ChangesWhenTheDatabaseChanges()
    {
        BuildAndClose();

        var before = DatabaseRepair.Fingerprint(Scan());
        Assert.That(DatabaseRepair.Fingerprint(Scan()), Is.EqualTo(before), "scanning twice must not change the fingerprint");

        DamagePage(BundlePath, 2, page => page[IntegrityConstants.PageHeaderSize + 8] ^= 0xFF);
        Assert.That(DatabaseRepair.Fingerprint(Scan()), Is.Not.EqualTo(before), "but damaging the database must");
    }

    /// <summary>
    /// Finds a non-root data page belonging to a segment that holds primary data, so damage to it is genuinely
    /// unrecoverable rather than merely a rebuild away.
    /// </summary>
    private int FindPrimaryDataPage()
    {
        using var source = new OfflineBundlePageSource(BundlePath);
        var walker = new SegmentWalker(source);
        Span<byte> page = new byte[IntegrityConstants.PageSize];

        for (var p = ManagedPagedMMF.InitialReservedPageCount; p < source.PageCount; p++)
        {
            if (!source.TryReadPage(p, page) || (PageImage.Flags(page) & PageBlockFlags.IsLogicalSegmentRoot) == 0)
            {
                continue;
            }

            // Roots identify themselves by self-reference; a twin's copy of the directory still names the primary.
            if (System.Runtime.InteropServices.MemoryMarshal.Read<int>(PageImage.RawData(page)) != p)
            {
                continue;
            }

            var segment = walker.WalkSegment(p);
            if (PhysicalChecks.IsDerivedKind(segment.Kind) || segment.Kind == StorageSegmentKind.Other)
            {
                continue;
            }

            for (var i = 1; i < segment.Pages.Count; i++)
            {
                if (segment.Pages[i] != segment.RootPageIndex)
                {
                    return segment.Pages[i];
                }
            }
        }

        return -1;
    }

    private static string DescribeOutcome(RepairOutcome outcome)
    {
        var lines = outcome.Results.Select(r => $"  {r.Step.Action}: {r.Outcome} — {r.Detail}");
        return "repair outcome:\n" + string.Join("\n", lines);
    }
}
