using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// BKP-05's shape clauses: what a restore leaves on disk is exactly what the engine's crash path needs — the image at its full length, every
/// tail segment under its own name at its DECLARED size (DC-2), nothing another session wrote beside the data — and the engine opens it.
/// </summary>
[TestFixture]
internal sealed class RestoredBundleShapeTests
{
    private string _root;

    [SetUp]
    public void SetUp() => _root = Path.Combine(TestStorage.ForFixture(nameof(RestoredBundleShapeTests)), Guid.NewGuid().ToString("N"));

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

    private RestoreOutcome BackupAndRestore(BackupTestImage image, out string bundle)
    {
        var store = Path.Combine(_root, "store");
        using (var session = BackupStore.OpenForWrite(store, image.DatabaseId, true))
        {
            StoreTestKit.WritePoint(session, image, withTail: true);
        }

        var outcome = RestoreStager.Run(store, PointSelector.Latest, Path.Combine(_root, "restored"));
        bundle = outcome.Target;
        return outcome;
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-05")]
    public void TheRestoredBundle_IsTheImagePlusItsTailAtDeclaredSizes_AndNothingElse()
    {
        var image = BackupTestImages.Unclean;
        var outcome = BackupAndRestore(image, out var bundle);

        Assert.That(Path.GetFileName(bundle), Is.EqualTo(image.DatabaseName + ".typhon"));
        Assert.That(File.ReadAllBytes(Path.Combine(bundle, "data")), Is.EqualTo(image.Pages.SelectMany(p => p).ToArray()));
        Assert.That(Directory.GetFiles(bundle).Select(Path.GetFileName).OrderBy(n => n), Is.EqualTo(new[] { "data", "restore.json" }),
            "no chunk-summary, backup-state, db.lock or staging marker: every nonce in the image then matches nothing (CS-05)");

        var wal = Directory.GetFiles(Path.Combine(bundle, "wal")).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.That(wal, Is.EqualTo(image.WalSegments.Select(s => $"{s.SegmentId:D16}.wal")));
        foreach (var s in image.WalSegments)
        {
            var bytes = File.ReadAllBytes(Path.Combine(bundle, "wal", $"{s.SegmentId:D16}.wal"));
            Assert.That(bytes.Length, Is.EqualTo(s.SegmentSize), "a segment shorter than its header declares is read past its end (DC-2)");
            Assert.That(bytes.AsSpan(0, s.Bytes.Length).SequenceEqual(s.Bytes), Is.True);
            Assert.That(bytes.AsSpan(s.Bytes.Length).IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
        }

        Assert.That(RestoreMarker.TryRead(StoreFileSystem.Instance, bundle, out var marker, out _), Is.EqualTo(RestoreMarkerState.Valid));
        Assert.That(marker.DatabaseId, Is.EqualTo(image.DatabaseId));
        Assert.That(marker.NewTimelineId, Is.EqualTo(outcome.NewTimelineId).And.Not.EqualTo(StoreTestKit.Timeline));
        Assert.That(marker.RestoredFrom.BackupId, Is.EqualTo(outcome.PointId));
        Assert.That((outcome.PagesWritten, outcome.TailSegments, outcome.ChainDepth), Is.EqualTo((image.PageCount, image.WalSegments.Count, 1)));
    }

    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(60_000)]
    public void TheEngine_OpensTheRestoredBundle_WithEveryCommittedEntity(bool unclean)
    {
        var image = BackupTestImages.Build(unclean ? "BkShapeUnclean" : "BkShapeClean", unclean, entities: 24, root: Path.Combine(_root, "src"));
        BackupAndRestore(image, out var bundle);

        using var provider = BackupTestImages.BuildProvider(image.DatabaseName, Path.GetDirectoryName(bundle), Path.Combine(bundle, "wal"));
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();

        Assert.That(dbe.LastOpenRanCrashRecovery, Is.EqualTo(unclean), "an unclean image opens through recovery, a clean one does not");
        if (!unclean)
        {
            Assert.That(dbe.LastOpenChunkSummaryResult, Is.EqualTo(ChunkSummaryReadResult.Missing), "OQ-2: no chunk-summary travels with a point");
        }

        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<CompAArch>().Execute().Count, Is.EqualTo(24));
    }
}
