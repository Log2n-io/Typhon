using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// The facade additions the Workbench's jobs need (#1417 task 1): a verify that can be cancelled and reports its progress, an offline create that
/// reports its pages, and the one exception classification <c>typhon backup</c> and the Workbench share.
/// </summary>
[TestFixture]
internal sealed class BackupFacadeProgressTests
{
    private string _root;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestStorage.ForFixture(nameof(BackupFacadeProgressTests)), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private string Store => Path.Combine(_root, "store");

    private BackupTestImage Image(string name) => BackupTestImages.Build(name, unclean: false, entities: 7, root: Path.Combine(_root, name));

    [Test]
    [CancelAfter(60_000)]
    public void OfflineCreate_ReportsItsPages_MonotonicToTheImagesTotal()
    {
        var image = Image("BkProgCreate");
        var seen = new List<(long Done, long Total)>();

        var point = BackupOperations.Create(image.BundlePath, Store, new BackupCreateOptions { Full = true, Progress = (d, t) => seen.Add((d, t)) });

        Assert.That(seen, Is.Not.Empty);
        Assert.That(seen.Select(s => s.Total).Distinct(), Is.EqualTo(new long[] { point.ImagePages }), "one total: the image's pages");
        Assert.That(seen.Select(s => s.Done), Is.Ordered, "never goes back");
        Assert.That(seen[0].Done, Is.Zero);
        Assert.That(seen[^1].Done, Is.EqualTo(point.ImagePages), "ends at its total");
    }

    [Test]
    [CancelAfter(60_000)]
    public void Verify_ReportsEveryPack_MonotonicToItsTotal()
    {
        var image = Image("BkProgVerify");
        BackupOperations.Create(image.BundlePath, Store, new BackupCreateOptions { Full = true });
        BackupOperations.Create(image.BundlePath, Store, new BackupCreateOptions { Full = true });
        var seen = new List<(string Phase, long Done, long Total)>();

        var report = BackupOperations.Verify(Store, BackupVerifyLevel.Full, new BackupVerifyOptions { Progress = (p, d, t) => seen.Add((p, d, t)) });

        Assert.That(report.Verdict, Is.EqualTo(BackupVerifyVerdict.Ok));
        var packs = seen.Where(s => s.Phase == "packs").ToList();
        Assert.That(packs.Select(s => s.Done), Is.EqualTo(new long[] { 0, 1, 2 }));
        Assert.That(packs.Select(s => s.Total).Distinct(), Is.EqualTo(new long[] { 2 }));
        Assert.That(seen.Where(s => s.Phase != "packs"), Is.Empty, "a full verify scans no image");
    }

    [Test]
    [CancelAfter(60_000)]
    public void DeepVerify_ForwardsTheImageScansProgress_AfterThePacks()
    {
        var image = Image("BkProgDeep");
        BackupOperations.Create(image.BundlePath, Store, new BackupCreateOptions { Full = true });
        var seen = new List<string>();

        var report = BackupOperations.Verify(Store, PointSelector.Latest, BackupVerifyLevel.Deep,
            new BackupVerifyOptions { Progress = (p, _, _) => seen.Add(p) });

        Assert.That(report.Verdict, Is.EqualTo(BackupVerifyVerdict.Ok));
        var lastPack = seen.LastIndexOf("packs");
        Assert.That(seen.Skip(lastPack + 1), Is.Not.Empty, "the scan's phases follow the packs");
        Assert.That(seen.Take(lastPack + 1), Is.All.EqualTo("packs"));
    }

    [Test]
    [CancelAfter(60_000)]
    public void Verify_Cancelled_Throws_AndReportsNothing()
    {
        var image = Image("BkProgCancel");
        BackupOperations.Create(image.BundlePath, Store, new BackupCreateOptions { Full = true });
        BackupOperations.Create(image.BundlePath, Store, new BackupCreateOptions { Full = true });
        using var cts = new CancellationTokenSource();
        var packsDone = 0L;

        // Cancelled by the first pack's own progress: the second pack is never verified.
        Assert.Throws<OperationCanceledException>(() => BackupOperations.Verify(Store, BackupVerifyLevel.Full, new BackupVerifyOptions
        {
            Cancellation = cts.Token,
            Progress = (_, d, _) =>
            {
                packsDone = d;
                if (d == 1)
                {
                    cts.Cancel();
                }
            },
        }));
        Assert.That(packsDone, Is.EqualTo(1));
    }

    [Test]
    public void Classify_MapsEachExceptionToTheCategoryTheCliExitCodeFollows()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BackupOperations.Classify(new BackupPlanChangedException("s", "a", "b")), Is.EqualTo(BackupFailureKind.PlanChanged));
            Assert.That(BackupOperations.Classify(new RestoreRefusedException(RestoreRefusalReason.TargetExists, "t", "exists")),
                Is.EqualTo(BackupFailureKind.Refused));
            Assert.That(BackupOperations.Classify(new DatabaseLockedException("b", 1, "m", DateTimeOffset.UtcNow)), Is.EqualTo(BackupFailureKind.Refused));
            Assert.That(BackupOperations.Classify(new TyphonException(TyphonErrorCode.BackupNotConfigured, "off")), Is.EqualTo(BackupFailureKind.Refused));
            Assert.That(BackupOperations.Classify(new NotSupportedException()), Is.EqualTo(BackupFailureKind.Refused));
            Assert.That(BackupOperations.Classify(new BackupChainBrokenException(3, 2, [1], "gone")), Is.EqualTo(BackupFailureKind.StoreDamaged));
            Assert.That(BackupOperations.Classify(new BackupPackCorruptException("p", "INDEX", -1, "crc")), Is.EqualTo(BackupFailureKind.StoreDamaged));
            Assert.That(BackupOperations.Classify(new BackupAbortedException(BackupAbortReason.EngineStopping, "closing")),
                Is.EqualTo(BackupFailureKind.Failed));
            Assert.That(BackupOperations.Classify(new TyphonException(TyphonErrorCode.BackupInProgress, "busy")), Is.EqualTo(BackupFailureKind.Failed));
            Assert.That(BackupOperations.Classify(new IOException()), Is.EqualTo(BackupFailureKind.Failed));
            Assert.That(BackupOperations.Classify(new OperationCanceledException()), Is.EqualTo(BackupFailureKind.Failed));
            Assert.That(BackupOperations.Classify(new ArgumentException()), Is.EqualTo(BackupFailureKind.Defect));
            Assert.That(BackupOperations.Classify(null), Is.EqualTo(BackupFailureKind.Defect));
        });
    }
}
