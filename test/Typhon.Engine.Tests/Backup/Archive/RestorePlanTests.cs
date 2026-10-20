using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// AC-A11: a restore's plan (<see cref="RestoreStager.Preview"/>, the facade's <c>PlanRestore</c>) reports every refusal the restore would raise and
/// writes nothing; its fingerprint follows what would be restored; a restore held to a stale approved plan refuses, writing nothing.
/// </summary>
[TestFixture]
internal sealed class RestorePlanTests
{
    private const string Store = "/store";
    private const string Parent = "/restored";

    private static (InMemorySparseFileSystem Fs, BackupTestImage Image) StoreWithOnePoint(BackupTestImage image = null)
    {
        image ??= BackupTestImages.Clean;
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        fs.CreateDirectory(Parent);
        using var store = BackupStore.OpenForWrite(Store, image.DatabaseId, true, fs);
        StoreTestKit.WritePoint(store, image, withTail: !image.Clean);
        return (fs, image);
    }

    private static Dictionary<string, uint> Everything(InMemorySparseFileSystem fs) =>
        fs.AllFiles.OrderBy(f => f, StringComparer.Ordinal).ToDictionary(f => f, f => Crc32CUtil.Compute(fs.ReadAll(f)));

    private static RestorePlanPreview Preview(InMemorySparseFileSystem fs, PointSelector point, string target, bool replace = false)
    {
        var before = Everything(fs);
        var directories = fs.AllDirectories.ToHashSet();
        var preview = RestoreStager.Preview(Store, point, target, new RestoreOptions { Replace = replace }, fs);
        Assert.That(Everything(fs), Is.EqualTo(before), "a plan writes nothing");
        Assert.That(fs.AllDirectories.ToHashSet().SetEquals(directories), Is.True, "a plan creates no directory");
        return preview;
    }

    [Test]
    [CancelAfter(15_000)]
    public void EveryRefusalTheRestoreWouldRaise_IsReported_WithNothingWritten()
    {
        var (fs, image) = StoreWithOnePoint();
        var bundle = Parent + "/" + image.DatabaseName + ".typhon";
        var clear = Preview(fs, PointSelector.Latest, Parent);
        Assert.That((clear.Refusal, clear.ResolvedTarget.Replace('\\', '/')), Is.EqualTo(((RestoreRefusedException)null, bundle)));
        Assert.That(clear.Fingerprint, Is.Not.Null.And.Length.EqualTo(16));

        Assert.That(Preview(fs, PointSelector.Latest, Parent + "/someone-else.typhon").Refusal?.Reason, Is.EqualTo(RestoreRefusalReason.NameMismatch));

        fs.CreateDirectory(bundle + ".restoring");
        Assert.That(Preview(fs, PointSelector.Latest, Parent).Refusal?.Reason, Is.EqualTo(RestoreRefusalReason.ForeignStaging));
        fs.DeleteDirectory(bundle + ".restoring");

        fs.CreateDirectory(bundle);
        using (var f = fs.OpenCreateNew(bundle + "/data"))
        {
            f.Write(new byte[100], 0);
        }

        Assert.That(Preview(fs, PointSelector.Latest, Parent).Refusal?.Reason, Is.EqualTo(RestoreRefusalReason.TargetExists));
        var replacing = Preview(fs, PointSelector.Latest, Parent, replace: true);
        Assert.That(replacing.Refusal, Is.Null);
        Assert.That(replacing.DisplacedBundle, Does.Contain(".pre-restore-"));
        fs.MarkBundleHeld(bundle, true);
        Assert.That(Preview(fs, PointSelector.Latest, Parent, replace: true).Refusal?.Reason, Is.EqualTo(RestoreRefusalReason.TargetLocked));

        var (forgedFs, _) = StoreWithOnePoint(BackupTestImages.WithFormatRevision(BackupTestImages.Clean, 6));
        Assert.That(Preview(forgedFs, PointSelector.Latest, Parent).Refusal?.Reason, Is.EqualTo(RestoreRefusalReason.FormatRevision));

        var unreachable = Preview(fs, PointSelector.AtLsn(image.CheckpointLsn + 1_000_000), Parent);
        Assert.That(unreachable.Unreachable?.Reason, Is.EqualTo(RestoreUnreachableReason.BeyondArchive));
    }

    [Test]
    [CancelAfter(15_000)]
    public void TheFingerprint_FollowsThePointAndTheTarget()
    {
        var (fs, image) = StoreWithOnePoint();
        var a = Preview(fs, PointSelector.Latest, Parent).Fingerprint;
        Assert.That(Preview(fs, PointSelector.Latest, Parent).Fingerprint, Is.EqualTo(a), "deterministic");
        Assert.That(Preview(fs, PointSelector.Latest, "/elsewhere").Fingerprint, Is.Not.EqualTo(a), "another target");
        using (var store = BackupStore.OpenForWrite(Store, image.DatabaseId, false, fs))
        {
            StoreTestKit.WritePoint(store, image);
        }

        Assert.That(Preview(fs, PointSelector.Latest, Parent).Fingerprint, Is.Not.EqualTo(a), "another point");
        Assert.That(Preview(fs, PointSelector.ById(1), Parent).Fingerprint, Is.EqualTo(a), "the same point");
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-11")]
    public void ARestoreHeldToAStalePlan_RefusesWritingNothing_AndToTheCurrentOne_Restores()
    {
        var (fs, image) = StoreWithOnePoint();
        var approved = Preview(fs, PointSelector.Latest, Parent).Fingerprint;
        using (var store = BackupStore.OpenForWrite(Store, image.DatabaseId, false, fs))
        {
            StoreTestKit.WritePoint(store, image);
        }

        var before = Everything(fs);
        var ex = Assert.Throws<BackupPlanChangedException>(() =>
            RestoreStager.Run(Store, PointSelector.Latest, Parent, new RestoreOptions { ApprovedPlan = approved }, fs));
        Assert.That((ex.Approved, ex.ErrorCode), Is.EqualTo((approved, TyphonErrorCode.BackupPlanChanged)));
        Assert.That(Everything(fs).Keys.Where(k => !k.EndsWith(".restoring.lock", StringComparison.Ordinal)), Is.EqualTo(before.Keys), "nothing written");

        var current = Preview(fs, PointSelector.Latest, Parent).Fingerprint;
        var outcome = RestoreStager.Run(Store, PointSelector.Latest, Parent, new RestoreOptions { ApprovedPlan = current }, fs);
        Assert.That(outcome.PointId, Is.EqualTo(2u));
    }
}
