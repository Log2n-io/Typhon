using NUnit.Framework;
using System;
using System.IO;
using Typhon.Engine.Tests.Backup;

namespace Typhon.Engine.Tests.Backup.Archive;

/// <summary>The archive targets of <see cref="PointSelector"/> (WA-21): one type for every restore caller, refused by every point-scoped call.</summary>
[TestFixture]
internal sealed class PointSelectorTests
{
    [Test]
    public void ArchiveTargets_CarryTheirLsnTimeAndTimeline_AndCompareByValue()
    {
        var timeline = Guid.NewGuid();
        var lsn = PointSelector.AtLsn(42, timeline);
        Assert.That(lsn.IsArchiveTarget && !lsn.IsTimeTarget && lsn.Lsn == 42 && lsn.TimelineId == timeline && lsn.BackupId == 0 && !lsn.IsLatest, Is.True);
        var at = new DateTime(2026, 10, 20, 14, 32, 6, DateTimeKind.Utc);
        var time = PointSelector.AtTime(at);
        Assert.That(time.IsArchiveTarget && time.IsTimeTarget && time.TimeUtc == at && time.TimelineId == null && time.Lsn == 0, Is.True);

        Assert.That(PointSelector.AtLsn(42, timeline), Is.EqualTo(lsn));
        Assert.That(PointSelector.AtLsn(42), Is.Not.EqualTo(lsn));
        Assert.That(PointSelector.AtLsn(42), Is.Not.EqualTo(PointSelector.ById(42)));
        Assert.That(PointSelector.Latest.IsArchiveTarget || PointSelector.ById(3).IsArchiveTarget, Is.False);
        Assert.That(lsn.ToString(), Does.Contain("LSN 42"));
    }

    [Test]
    public void AtTime_RefusesALocalOrUnspecifiedKind_AndAtLsn_ANonPositiveLsn()
    {
        Assert.Throws<ArgumentException>(() => PointSelector.AtTime(new DateTime(2026, 10, 20, 14, 0, 0, DateTimeKind.Local)));
        Assert.Throws<ArgumentException>(() => PointSelector.AtTime(new DateTime(2026, 10, 20, 14, 0, 0, DateTimeKind.Unspecified)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PointSelector.AtLsn(0));
    }

    [Test]
    public void VerifyAndThePointScopedCalls_RefuseAnArchiveTarget()
    {
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        using (var session = BackupStore.OpenForWrite("/s", BackupTestImages.Clean.DatabaseId, createIfMissing: true, fs))
        {
            StoreTestKit.WritePoint(session, BackupTestImages.Clean);
        }

        var target = PointSelector.AtLsn(1);
        Assert.Throws<ArgumentException>(() => BackupVerifier.VerifyPoint("/s", target, VerifyLevel.Quick, null, fs));
        Assert.Throws<ArgumentException>(() => ChainResolver.Select(StoreView.Open("/s", fs), PointSelector.AtTime(DateTime.UtcNow)));
        Assert.Throws<ArgumentException>(() => BackupOperations.Verify(Path.Combine(Path.GetTempPath(), "no-store"), target, BackupVerifyLevel.Quick));
    }
}
