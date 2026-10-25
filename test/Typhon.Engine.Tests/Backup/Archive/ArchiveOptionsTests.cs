using NUnit.Framework;
using System;
using System.IO;

namespace Typhon.Engine.Tests.Backup.Archive;

/// <summary><see cref="ArchiveOptions.Validate"/>: the two load-bearing values are required and bounded, 0 is "unset", never a value (impl/1418 §4.1).</summary>
[TestFixture]
internal sealed class ArchiveOptionsTests
{
    private static readonly WalWriterOptions Wal = new() { SegmentSize = 4 << 20, WalDirectory = Path.Combine(Path.GetTempPath(), "db", "wal") };
    private static readonly string Store = Path.Combine(Path.GetTempPath(), "store");

    private static ArchiveOptions Valid(TimeSpan? age = null, long backlog = 0, TimeSpan? sample = null, TimeSpan? close = null) => new()
    {
        MaxSegmentAge = age ?? TimeSpan.FromSeconds(30),
        BacklogLimitBytes = backlog == 0 ? 2L * Wal.SegmentSize : backlog,
        TimeSampleInterval = sample ?? TimeSpan.FromSeconds(1),
        CloseTimeout = close ?? TimeSpan.FromSeconds(10),
    };

    [Test]
    public void TheDefaults_AndTheBoundsThemselves_Validate()
    {
        Assert.That(Valid().Validate(Wal, Store, out var error), Is.True, error);
        Assert.That(Valid(age: ArchiveOptions.MinMaxSegmentAge, sample: ArchiveOptions.MaxTimeSampleInterval, close: TimeSpan.Zero).Validate(Wal, Store,
            out error), Is.True, error);
    }

    [Test]
    public void AnUnsetMaxSegmentAge_IsRefused_NamingTheRpo()
    {
        var options = new ArchiveOptions { MaxSegmentAge = TimeSpan.Zero, BacklogLimitBytes = 2L * Wal.SegmentSize };
        Assert.That(options.Validate(Wal, Store, out var error), Is.False);
        Assert.That(error, Does.Contain("MaxSegmentAge must be set"));
    }

    [Test]
    public void AMaxSegmentAgeBelowFiveSeconds_IsRefused()
    {
        Assert.That(Valid(age: TimeSpan.FromSeconds(4.9)).Validate(Wal, Store, out var error), Is.False);
        Assert.That(error, Does.Contain("at least"));
    }

    [Test]
    public void AnUnsetBacklogLimit_IsRefused_AndOneSegmentIsTooLittle()
    {
        var unset = new ArchiveOptions { MaxSegmentAge = TimeSpan.FromSeconds(30), BacklogLimitBytes = 0 };
        Assert.That(unset.Validate(Wal, Store, out var error), Is.False);
        Assert.That(error, Does.Contain("BacklogLimitBytes must be set"));

        Assert.That(Valid(backlog: 2L * Wal.SegmentSize - 1).Validate(Wal, Store, out error), Is.False);
        Assert.That(error, Does.Contain("two"));
    }

    [TestCase(0.5)]
    [TestCase(61.0)]
    public void ASampleIntervalOutsideOneToSixtySeconds_IsRefused(double seconds)
    {
        Assert.That(Valid(sample: TimeSpan.FromSeconds(seconds)).Validate(Wal, Store, out var error), Is.False);
        Assert.That(error, Does.Contain("TimeSampleInterval"));
    }

    [Test]
    public void ANegativeCloseTimeout_IsRefused()
    {
        Assert.That(Valid(close: TimeSpan.FromSeconds(-1)).Validate(Wal, Store, out var error), Is.False);
        Assert.That(error, Does.Contain("CloseTimeout"));
    }

    [Test]
    public void AStoreInsideTheWalDirectory_OrNoStore_IsRefused()
    {
        Assert.That(Valid().Validate(Wal, Path.Combine(Wal.WalDirectory, "archive"), out var error), Is.False);
        Assert.That(error, Does.Contain("inside the WAL directory"));
        Assert.That(Valid().Validate(Wal, "", out error), Is.False);
        Assert.That(Valid().Validate(Wal, Wal.WalDirectory + "-store", out error), Is.True, "a sibling sharing a prefix is not inside it");
    }
}
