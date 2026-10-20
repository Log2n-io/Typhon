using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>engine.Backup</c> with <see cref="DatabaseEngineOptions.Backup"/> set (plan 1416 DL-15/16): points go into the configured store, a point into any
/// other store is refused (it would cost the configured one its chain), and the capture tuning the options carry reaches the coordinator.
/// </summary>
class ConfiguredBackupServiceTests : BackupTestBase<ConfiguredBackupServiceTests>
{
    private string _store;

    public override void Setup()
    {
        _store = Path.Combine(TestStorage.ForFixture(nameof(ConfiguredBackupServiceTests)), Guid.NewGuid().ToString("N"));
        base.Setup();
    }

    public override void TearDown()
    {
        base.TearDown();
        try
        {
            Directory.Delete(_store, true);
        }
        catch (IOException)
        {
        }
    }

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Backup = new BackupOptions
        {
            StorePath = _store, Interval = TimeSpan.Zero, Retention = null, CbwBufferPages = 64, CbwStallTimeout = TimeSpan.FromMilliseconds(75),
        };
    }

    [Test]
    [CancelAfter(15_000)]
    public void APoint_GoesIntoTheConfiguredStore_AndNoOther()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Spawn(dbe, 50);

        var point = Wait(dbe.Backup.CreateAsync());
        Assert.That(point.Kind, Is.EqualTo(BackupPointKind.Full));
        Assert.That(BackupOperations.List(_store).Points, Has.Count.EqualTo(1));

        var same = Wait(dbe.Backup.CreateAsync(_store + Path.DirectorySeparatorChar));
        Assert.That(same.Kind, Is.EqualTo(BackupPointKind.Incremental), "the configured store, however spelt, continues its chain");

        var other = Path.Combine(Path.GetDirectoryName(_store), Guid.NewGuid().ToString("N"));
        var ex = Assert.Throws<TyphonException>(() => dbe.Backup.CreateAsync(other));
        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.BackupNotConfigured));
        Assert.That(ex.Message, Does.Contain(_store));
        Assert.That(Directory.Exists(other), Is.False, "nothing was written there");
    }

    [Test]
    [CancelAfter(15_000)]
    public void TheCaptureTuning_ReachesTheCoordinator()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);

        Assert.That(dbe.BackupCoordinator.Options.CbwBufferPages, Is.EqualTo(64));
        Assert.That(dbe.BackupCoordinator.Options.CbwStallTimeout, Is.EqualTo(TimeSpan.FromMilliseconds(75)));
    }

    private static BackupPointSummary Wait(System.Threading.Tasks.Task<BackupPointSummary> task)
    {
        Assert.That(task.Wait(Budget), Is.True, "the backup did not finish");
        return task.Result;
    }
}
