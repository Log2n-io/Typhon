using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// The public lifecycle facade (plan 1415 §4.1) over a store on disk: Compact, PlanRetention, ApplyRetention with and without an approved plan (DL-34),
/// and the refusals a caller sees.
/// </summary>
[TestFixture]
internal sealed class BackupLifecycleOperationsTests
{
    private string _store;

    private static readonly BackupRetentionPolicy KeepTwo = new()
    {
        MinKeep = 1, MaxCount = 2, MaxAge = TimeSpan.Zero, MaxTotalBytes = 0, CompactThreshold = 0, KeepLastDrilled = false,
    };

    [SetUp]
    public void SetUp() => _store = Path.Combine(TestStorage.ForFixture(nameof(BackupLifecycleOperationsTests)), Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_store, true);
        }
        catch (IOException)
        {
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void Compact_PublishesTheBase_AndIsANoOpTheSecondTime()
    {
        WriteChain(4);

        var first = BackupOperations.Compact(_store, PointSelector.Latest);
        var second = BackupOperations.Compact(_store, PointSelector.ById(4));

        Assert.That((first.AlreadyBase, first.Point.BackupId, first.Point.Kind, first.ChainDepth), Is.EqualTo((false, 4u, BackupPointKind.Compacted, 4)));
        Assert.That(second.AlreadyBase, Is.True);
        Assert.That(second.Point.Kind, Is.EqualTo(BackupPointKind.Compacted));
        Assert.That(BackupOperations.List(_store).Chains.Single().HeadDepth, Is.EqualTo(1), "a restore of the head now reads one pack");
        Assert.That(BackupOperations.Compact(_store, PointSelector.ById(1)).AlreadyBase, Is.True, "a full point is a base");
    }

    [Test]
    [CancelAfter(15_000)]
    public void APlanApplied_DeletesExactlyWhatItListed()
    {
        WriteChain(5);

        var plan = BackupOperations.PlanRetention(_store, KeepTwo);
        var result = BackupOperations.ApplyRetention(_store, KeepTwo, plan);

        Assert.That(plan.Kept, Is.EqualTo(new uint[] { 4, 5 }));
        Assert.That(result.Deleted, Is.EqualTo(plan.PackDeletions));
        Assert.That(result.Plan.Fingerprint, Is.EqualTo(plan.Fingerprint));
        Assert.That(result.Compactions.Select(c => c.Point.BackupId), Is.EqualTo(new uint[] { 4 }));
        Assert.That(Packs(), Is.EqualTo(new[] { "00000004-C.tpack", "00000005-I.tpack" }));
        Assert.That(BackupOperations.Verify(_store, BackupVerifyLevel.Full).Verdict, Is.EqualTo(BackupVerifyVerdict.Ok));
    }

    /// <summary>DL-34: a point lands between the preview and the apply; the store now plans to delete a pack the approval never saw. Nothing changes.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void AnApprovedPlanThatNoLongerHolds_IsRefused_AndNothingChanges()
    {
        var chain = WriteChain(4);
        var approved = BackupOperations.PlanRetention(_store, KeepTwo);
        using (var store = BackupStore.OpenForWrite(_store, BackupTestImages.Clean.DatabaseId, false))
        {
            StoreTestKit.WritePoint(store, BackupTestImages.Clean, chain, pages: [9]);
        }

        var before = Packs();
        var ex = Assert.Throws<BackupPlanChangedException>(() => BackupOperations.ApplyRetention(_store, KeepTwo, approved));

        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.BackupPlanChanged));
        Assert.That(ex.Approved, Is.EqualTo(approved.Fingerprint));
        Assert.That(ex.Current, Is.Not.EqualTo(approved.Fingerprint));
        Assert.That(Packs(), Is.EqualTo(before), "nothing compacted, nothing deleted");
        Assert.That(BackupOperations.ApplyRetention(_store, KeepTwo).Deleted, Is.Not.Empty, "without an approval, the current policy applies");
    }

    [Test]
    [CancelAfter(15_000)]
    public void AnInvalidPolicy_OrAHeldStore_IsRefused()
    {
        WriteChain(2);
        var noLimit = new BackupRetentionPolicy
        {
            MinKeep = 1, MaxCount = 0, MaxAge = TimeSpan.Zero, MaxTotalBytes = 0, CompactThreshold = 0, KeepLastDrilled = false,
        };
        Assert.Throws<ArgumentException>(() => BackupOperations.PlanRetention(_store, noLimit));
        Assert.Throws<ArgumentException>(() => BackupOperations.ApplyRetention(_store, noLimit));

        using (BackupStore.OpenForWrite(_store, BackupTestImages.Clean.DatabaseId, false))
        {
            var ex = Assert.Throws<BackupStoreLockedException>(() => BackupOperations.ApplyRetention(_store, KeepTwo));
            Assert.That(ex.HolderPurpose, Is.EqualTo("backup"));
            Assert.Throws<BackupStoreLockedException>(() => BackupOperations.Compact(_store, PointSelector.Latest));
        }

        Assert.Throws<BackupChainBrokenException>(() => BackupOperations.Compact(_store, PointSelector.ById(42)));
    }

    private string[] Packs() =>
        Directory.GetFiles(Path.Combine(_store, "packs")).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>A full point and <paramref name="depth"/> − 1 incrementals, on disk; returns the chain id.</summary>
    private Guid WriteChain(int depth)
    {
        var image = BackupTestImages.Clean;
        using var store = BackupStore.OpenForWrite(_store, image.DatabaseId, true);
        var full = StoreTestKit.WritePoint(store, image);
        for (var i = 2; i <= depth; i++)
        {
            StoreTestKit.WritePoint(store, image, full.ChainId, pages: [2 + i], @override: StoreTestKit.Salted(image, i));
        }

        return full.ChainId;
    }
}
