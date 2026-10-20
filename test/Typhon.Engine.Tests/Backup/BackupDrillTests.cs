using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Integrity;

namespace Typhon.Engine.Tests;

/// <summary>
/// The drill (04 §6, plan 1416 §3.6, AC-D1): restore into a scratch directory, open (recovery runs) and close, check deep, record, delete the scratch. A
/// point that does not restore, open or check is <see cref="BackupDrillOutcome.Failed"/> at its stage; the check never runs on an unrecovered image (§8.3).
/// </summary>
/// <remarks>
/// The drills run in-process with an opener that registers the fixture's archetypes (the host path, <see cref="BackupDrillOptions.OpenAndClose"/>): a
/// process that does not register them cannot replay their WAL records (#1309), which the default-opener tests below pin down.
/// </remarks>
class BackupDrillTests : BackupTestBase<BackupDrillTests>
{
    private const ulong DrillCache = 16UL << 20;
    private string _root;
    private string _store;
    private string _scratch;
    private IServiceScope _scope;

    public override void Setup()
    {
        _root = Path.Combine(TestStorage.ForFixture(nameof(BackupDrillTests)), Guid.NewGuid().ToString("N"));
        _store = Path.Combine(_root, "store");
        _scratch = Path.Combine(_root, "scratch");
        base.Setup();
    }

    public override void TearDown()
    {
        _scope?.Dispose();
        _scope = null;
        base.TearDown();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void AGoodPoint_Passes_IsRecorded_AndItsScratchIsGone()
    {
        var point = Backup(out _);
        var stages = new List<BackupDrillStage>();

        var report = BackupOperations.Drill(_store, PointSelector.Latest, Options(progress: (s, _, _) =>
        {
            if (stages.Count == 0 || stages[^1] != s)
            {
                stages.Add(s);
            }
        }));

        Assert.That((report.Outcome, report.FailedStage, report.Error), Is.EqualTo((BackupDrillOutcome.Passed, BackupDrillStage.None, (string)null)));
        Assert.That(report.Point.BackupId, Is.EqualTo(point.BackupId));
        Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Sound));
        Assert.That(report.Recovery.RecordsReplayed, Is.GreaterThan(0), "the point's WAL tail is replayed by the drill's open");
        Assert.That(stages, Is.EqualTo(new[] { BackupDrillStage.Restore, BackupDrillStage.Open, BackupDrillStage.Check }), "progress stages in order");
        Assert.That(Directory.EnumerateFileSystemEntries(_scratch), Is.Empty, "the scratch bundle is deleted");
        Assert.That(report.ScratchBundle, Is.Null);

        Assert.That(File.Exists(report.RecordFile), Is.True);
        var history = DrillRecords.ReadNewestPerPoint(StoreFileSystem.Instance, _store);
        Assert.That(history.Passed, Is.EqualTo(new[] { point.BackupId }));
        Assert.That(BackupOperations.List(_store).Drills[point.BackupId].Outcome, Is.EqualTo(BackupDrillOutcome.Passed), "list shows the drill");
    }

    /// <summary>The default opener (no application schema) settles a point whose tail holds no record: there is nothing it must replay.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void TheDefaultOpener_SettlesAPointWithNoRecordToReplay()
    {
        var point = Backup(out _, tail: false);

        var report = BackupOperations.Drill(_store, PointSelector.Latest, Options(opener: null, registering: false));

        Assert.That(report.Outcome, Is.EqualTo(BackupDrillOutcome.Passed), report.Error);
        Assert.That(report.Point.BackupId, Is.EqualTo(point.BackupId));
    }

    /// <summary>
    /// The default opener cannot replay the records of archetypes it does not register (#1309): that is the drill's limit, not the point's — an Error,
    /// which health does not count, naming the archetype and the in-process way out.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void TheDefaultOpener_OfAPointWhoseTailNeedsTheApplication_IsAnError()
    {
        Backup(out _);

        var report = BackupOperations.Drill(_store, PointSelector.Latest, Options(opener: null, registering: false));

        Assert.That((report.Outcome, report.FailedStage), Is.EqualTo((BackupDrillOutcome.Error, BackupDrillStage.Open)));
        Assert.That(report.Error, Does.Contain("CompAArch").And.Contain("OpenAndClose"));
        Assert.That(DrillRecords.ReadNewestPerPoint(StoreFileSystem.Instance, _store).Newest, Is.Null);
    }

    /// <summary>
    /// The application's way past #1309: the public opener with its registrations replays the tail the default opener refuses, and reports the recovery
    /// honestly, so the drill's guard holds and the point passes.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void ThePublicOpener_WithTheApplicationsRegistrations_DrillsAPointWithATail()
    {
        Backup(out _);

        var report = BackupOperations.Drill(_store, PointSelector.Latest, Options(opener: b =>
            BackupOperations.OpenAndCloseRestored(b, o => o.PageCacheSize(DrillCache).Register<CompA>().Register<CompSmSingleVersion>())));

        Assert.That((report.Outcome, report.FailedStage), Is.EqualTo((BackupDrillOutcome.Passed, BackupDrillStage.None)), report.Error);
        Assert.That(report.Recovery.RecordsReplayed, Is.GreaterThan(0), "the tail's records were replayed");
    }

    [Test]
    [CancelAfter(30_000)]
    public void KeepScratch_LeavesTheBundleOpenable()
    {
        Backup(out _);

        var report = BackupOperations.Drill(_store, PointSelector.Latest, Options(keep: true));

        Assert.That(report.Outcome, Is.EqualTo(BackupDrillOutcome.Passed));
        Assert.That(Directory.Exists(report.ScratchBundle), Is.True, report.ScratchBundle);
        Assert.That(report.ScratchBundle, Does.StartWith(_scratch));
    }

    [Test]
    [CancelAfter(30_000)]
    public void APointWithACorruptPage_FailsAtRestore()
    {
        var point = Backup(out _);
        using (var file = new FileStream(Path.Combine(_store, "packs", point.PackFile), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            // The first page frame, just past the header and the section's prefixes.
            file.Position = 256 + 8 + 4 + 64;
            var b = file.ReadByte();
            file.Position -= 1;
            file.WriteByte((byte)(b ^ 0x5A));
        }

        var report = BackupOperations.Drill(_store, PointSelector.Latest, Options());

        Assert.That((report.Outcome, report.FailedStage), Is.EqualTo((BackupDrillOutcome.Failed, BackupDrillStage.Restore)), report.Error);
        Assert.That(DrillRecords.ReadNewestPerPoint(StoreFileSystem.Instance, _store).Newest.Outcome, Is.EqualTo(BackupDrillOutcome.Failed));
        Assert.That(Directory.EnumerateFileSystemEntries(_scratch), Is.Empty);
    }

    /// <summary>§8.3: the open's failure is staged with a throwing opener; the classification (stage, outcome) is what is under test.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void AnOpenerThatThrows_FailsAtOpen()
    {
        Backup(out _);

        var report = BackupOperations.Drill(_store, PointSelector.Latest, Options(opener: _ => throw new InvalidOperationException("no open today")));

        Assert.That((report.Outcome, report.FailedStage), Is.EqualTo((BackupDrillOutcome.Failed, BackupDrillStage.Open)));
        Assert.That(report.Error, Does.Contain("no open today"));
    }

    /// <summary>
    /// Guard (a): an opener that never runs <c>InitializeArchetypes</c> replays nothing, and its close leaves the bundle unclean. The check never runs on
    /// such an image: it would verify the image at C, not the database at P.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AnOpenerThatSkipsRecovery_FailsAtOpen_AndTheCheckNeverRuns()
    {
        Backup(out _);
        var stages = new List<BackupDrillStage>();

        var report = BackupOperations.Drill(_store, PointSelector.Latest, Options(opener: SchemalessOpenWithoutInitialize,
            progress: (s, _, _) => stages.Add(s)));

        Assert.That((report.Outcome, report.FailedStage), Is.EqualTo((BackupDrillOutcome.Failed, BackupDrillStage.Open)));
        Assert.That(report.Error, Does.Contain("ran no crash recovery"));
        Assert.That(stages, Does.Not.Contain(BackupDrillStage.Check));
    }

    /// <summary>Guard (b): an opener that claims a recovery but leaves the bundle unclean (here: does nothing at all) is not believed.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void AnOpenerThatLeavesTheBundleUnclean_FailsAtOpen()
    {
        Backup(out _);

        var report = BackupOperations.Drill(_store, PointSelector.Latest, Options(opener: _ => new BackupSettleResult(true, default, TimeSpan.Zero)));

        Assert.That((report.Outcome, report.FailedStage), Is.EqualTo((BackupDrillOutcome.Failed, BackupDrillStage.Open)));
        Assert.That(report.Error, Does.Contain("not left cleanly closed"));
    }

    /// <summary>The check's verdict decides: a restored database that does not check sound fails the drill at its check.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void ARestoredDatabaseThatChecksDamaged_FailsAtCheck()
    {
        Backup(out _);

        var report = BackupOperations.Drill(_store, PointSelector.Latest, Options(opener: b =>
        {
            var settled = RegisteringOpener(b);
            var pages = (int)(new FileInfo(Path.Combine(b, "data")).Length / PagedMMF.PageSize);
            DamageKit.FlipByteInPage(b, pages - 1, IntegrityVerdict.Divergent);
            return settled;
        }));

        Assert.That((report.Outcome, report.FailedStage), Is.EqualTo((BackupDrillOutcome.Failed, BackupDrillStage.Check)), report.Error);
        Assert.That(report.Verdict, Is.GreaterThanOrEqualTo(IntegrityVerdict.Divergent));
        Assert.That(report.Findings, Is.GreaterThan(0));
    }

    [Test]
    [CancelAfter(30_000)]
    public void AScratchInsideTheStore_IsRefused_AndNothingIsWritten()
    {
        Backup(out _);

        var ex = Assert.Throws<RestoreRefusedException>(() =>
            BackupOperations.Drill(_store, PointSelector.Latest, new BackupDrillOptions { ScratchDirectory = Path.Combine(_store, "scratch") }));

        Assert.That(ex.Reason, Is.EqualTo(RestoreRefusalReason.ScratchInsideStore));
        Assert.That(Directory.Exists(Path.Combine(_store, "scratch")), Is.False);
        Assert.That(Directory.Exists(Path.Combine(_store, DrillRecords.Directory)), Is.False, "a refusal is not a drill");
    }

    /// <summary>A cancelled drill is an Error: recorded, but not counted — it says nothing about its point.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void ACancelledDrill_IsAnError_ThatHealthDoesNotCount()
    {
        Backup(out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var report = BackupOperations.Drill(_store, PointSelector.Latest, Options(ct: cts.Token));

        Assert.That(report.Outcome, Is.EqualTo(BackupDrillOutcome.Error));
        Assert.That(File.Exists(report.RecordFile), Is.True);
        var history = DrillRecords.ReadNewestPerPoint(StoreFileSystem.Instance, _store);
        Assert.That(history.Newest, Is.Null, "an Error drill is not the newest drill");
    }

    /// <summary>KeepLastDrilled's input is the store's drill records: the facade's plan pins the newest drill-passed point.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void TheRetentionPlan_PinsTheDrilledPoint()
    {
        var first = Backup(out var dbe, keepOpen: true);
        Assert.That(BackupOperations.Drill(_store, PointSelector.ById(first.BackupId), Options()).Outcome, Is.EqualTo(BackupDrillOutcome.Passed));
        for (var i = 0; i < 3; i++)
        {
            Assert.That(dbe.Backup.CreateAsync(_store, BackupRequestKind.Full, CancellationToken.None).Wait(Budget), Is.True);
        }

        var policy = new BackupRetentionPolicy
        {
            MaxAge = TimeSpan.Zero, MaxCount = 1, MaxTotalBytes = 0, MinKeep = 1, CompactThreshold = 0, KeepLastDrilled = true,
        };
        var plan = BackupOperations.PlanRetention(_store, policy);

        Assert.That(plan.Kept, Does.Contain(first.BackupId), "the drilled point is kept beyond MaxCount");
    }

    private BackupDrillOptions Options(bool keep = false, Func<string, BackupSettleResult> opener = null, Action<BackupDrillStage, long, long> progress = null,
        CancellationToken ct = default, bool registering = true) => new()
    {
        ScratchDirectory = _scratch,
        KeepScratch = keep,
        PageCacheBytes = DrillCache,
        OpenAndClose = opener ?? (registering ? RegisteringOpener : null),
        Progress = progress,
        Cancellation = ct,
    };

    /// <summary>A point whose image is unclean and whose tail holds committed spawns past its checkpoint.</summary>
    private BackupPointSummary Backup(out DatabaseEngine dbe, bool keepOpen = false, bool tail = true)
    {
        Directory.CreateDirectory(_scratch);
        _scope = ServiceProvider.CreateScope();
        dbe = Open(_scope);
        Spawn(dbe, 200);
        if (!tail)
        {
            var full = dbe.Backup.CreateAsync(_store, BackupRequestKind.Full, CancellationToken.None);
            Assert.That(full.Wait(Budget), Is.True, "the backup did not finish");
            Assert.That(full.Result.PointLsn, Is.EqualTo(full.Result.CheckpointLsn), "premise: the capture's checkpoint left no record past it");
            _scope.Dispose();
            _scope = null;
            return full.Result;
        }

        // Commits while the image is frozen land in the point's tail, past its checkpoint: the drill's open has a WAL to replay.
        using var frozen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        dbe.BackupCoordinator.Probe = (phase, _) =>
        {
            if (phase == CapturePhase.AfterFreeze)
            {
                frozen.Set();
                release.Wait(Budget);
            }
        };
        var task = dbe.Backup.CreateAsync(_store, BackupRequestKind.Full, CancellationToken.None);
        try
        {
            Assert.That(frozen.Wait(Budget), Is.True, "premise: the capture froze");
            Spawn(dbe, 50, seed: 1000);
        }
        finally
        {
            release.Set();
        }

        Assert.That(task.Wait(Budget), Is.True, "the backup did not finish");
        dbe.BackupCoordinator.Probe = null;
        Assert.That(task.Result.PointLsn, Is.GreaterThan(task.Result.CheckpointLsn), "premise: the point has a tail to replay");
        if (!keepOpen)
        {
            _scope.Dispose();
            _scope = null;
        }

        return task.Result;
    }

    /// <summary>The host's opener: the fixture's archetypes registered, so its open replays their records.</summary>
    private BackupSettleResult RegisteringOpener(string bundle)
    {
        using var provider = ProviderFor(Path.GetDirectoryName(bundle));
        using var scope = provider.CreateScope();
        return BackupDrillRunner.SettleResultOf(Open(scope), TimeSpan.Zero);
    }

    /// <summary>
    /// A generic opener with no schema that never calls <c>InitializeArchetypes</c> (the power-user registration; <c>AddTyphon</c> always runs it): no
    /// replay, an unclean close.
    /// </summary>
    private BackupSettleResult SchemalessOpenWithoutInitialize(string bundle)
    {
        using var provider = ProviderFor(Path.GetDirectoryName(bundle));
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        return new BackupSettleResult(engine.LastOpenRanCrashRecovery, default, TimeSpan.Zero);
    }
}
