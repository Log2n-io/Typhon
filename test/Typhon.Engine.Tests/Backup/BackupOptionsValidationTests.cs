using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// <see cref="BackupOptions"/> (plan 1416 §4.2, DL-16): the load-bearing values are stated, 0 means "none" where it may, and everything else is refused
/// with a message that names what to set.
/// </summary>
[TestFixture]
internal sealed class BackupOptionsValidationTests
{
    private static readonly BackupRetentionPolicy Policy = new()
    {
        MinKeep = 2, MaxCount = 10, MaxAge = TimeSpan.Zero, MaxTotalBytes = 0, CompactThreshold = 0, KeepLastDrilled = false,
    };

    private static string Error(BackupOptions o) => o.Validate(out var e) ? null : e;

    [Test]
    public void AStatedConfiguration_IsValid_AndZeroMeansNone()
    {
        Assert.That(Error(new BackupOptions { StorePath = "/b", Interval = TimeSpan.FromHours(1), Retention = Policy }), Is.Null);
        Assert.That(Error(new BackupOptions { StorePath = "/b", Interval = TimeSpan.Zero, Retention = null }), Is.Null, "no schedule, no retention");
        Assert.That(Error(new BackupOptions { StorePath = "/b", Interval = TimeSpan.FromSeconds(1), Retention = null, DrillWindow = TimeSpan.Zero }),
            Is.Null);
    }

    [TestCase("", 3600, "StorePath")]
    [TestCase("/b", 0.5, "Interval")]
    [TestCase("/b", -1, "Interval")]
    public void AMissingStoreOrAnIntervalBelowOneSecond_IsRefused(string store, double intervalSeconds, string named)
    {
        var error = Error(new BackupOptions { StorePath = store, Interval = TimeSpan.FromSeconds(intervalSeconds), Retention = null });
        Assert.That(error, Does.Contain(named));
    }

    [Test]
    public void TheOtherValues_AndTheRetentionPolicy_AreChecked()
    {
        Assert.That(Error(new BackupOptions { StorePath = "/b", Interval = TimeSpan.Zero, Retention = null, DrillWindow = TimeSpan.FromSeconds(-1) }),
            Does.Contain("DrillWindow"));
        Assert.That(Error(new BackupOptions { StorePath = "/b", Interval = TimeSpan.Zero, Retention = null, CbwBufferPages = 0 }),
            Does.Contain("CbwBufferPages"));
        Assert.That(Error(new BackupOptions { StorePath = "/b", Interval = TimeSpan.Zero, Retention = null, CbwStallTimeout = TimeSpan.Zero }),
            Does.Contain("CbwStallTimeout"));
        var noLimit = new BackupRetentionPolicy
        {
            MinKeep = 1, MaxCount = 0, MaxAge = TimeSpan.Zero, MaxTotalBytes = 0, CompactThreshold = 0, KeepLastDrilled = false,
        };
        Assert.That(Error(new BackupOptions { StorePath = "/b", Interval = TimeSpan.Zero, Retention = noLimit }), Does.StartWith("BackupOptions.Retention"));
    }

    [Test]
    public void TheArchive_IsCheckedAgainstTheWal()
    {
        var o = new BackupOptions
        {
            StorePath = "/b", Interval = TimeSpan.Zero, Retention = null,
            Archive = new ArchiveOptions { MaxSegmentAge = TimeSpan.FromSeconds(1), BacklogLimitBytes = 1L << 40 },
        };
        Assert.That(o.Validate(out _), Is.True, "without the WAL, nothing to check the archive against");
        Assert.That(o.Validate(new WalWriterOptions(), out var error), Is.False);
        Assert.That(error, Does.Contain("MaxSegmentAge"));
    }

    [Test]
    public void TheConfiguredStore_IsRecognisedWhateverItsSpelling()
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bk-store"));
        var o = new BackupOptions { StorePath = root, Interval = TimeSpan.Zero, Retention = null };
        Assert.That(o.IsStore(root + System.IO.Path.DirectorySeparatorChar), Is.True);
        Assert.That(o.IsStore(System.IO.Path.Combine(root, "..", "bk-store")), Is.True);
        Assert.That(o.IsStore(root + "-other"), Is.False);
    }
}
