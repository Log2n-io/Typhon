using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// The seam as wired (#1416 + #1418): with <see cref="BackupOptions.Archive"/> set, the backup scheduler builds the engine's archiver when the open completes,
/// steps it on its tick and reports its state; the close stops it. No other configuration of archiving exists.
/// </summary>
class SchedulerArchiverTests : ArchiveTestBase<SchedulerArchiverTests>
{
    protected override bool SchedulerBuildsArchiver => true;

    [Test]
    [CancelAfter(30_000)]
    public void BackupOptionsArchive_TheSchedulerBuildsTheArchiver_AndReportsIt()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);

        Assert.That(dbe.WalArchiver, Is.Not.Null, "the scheduler built the archiver at the open's end");
        Assert.That(dbe.ArchiveConfiguration, Is.SameAs(dbe.BackupOptions.Archive));
        dbe.BackupScheduler.TickForTest();
        Assert.That(dbe.Backup.Status.Archive.State, Is.Not.EqualTo(BackupArchiveState.Off), "the tick stepped it and published its state");
    }

    [Test]
    [CancelAfter(30_000)]
    public void NoArchive_NoArchiver()
    {
        SuppressArchiving = true;
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);

        Assert.That((dbe.WalArchiver, dbe.ArchiveConfiguration), Is.EqualTo(((WalArchiver)null, (ArchiveOptions)null)));
        Assert.That(dbe.Backup.Status, Is.SameAs(BackupStatus.Off));
    }
}
