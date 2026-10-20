using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>engine.Backup</c> (#1414, 07 §1): an application backs up its running database into a real store — Auto lets admission choose, Full forces a
/// new chain, a second call while one runs is refused, and a cancelled one publishes nothing and forgets no page.
/// </summary>
class EngineBackupServiceTests : BackupTestBase<EngineBackupServiceTests>
{
    private string _store;

    public override void Setup()
    {
        base.Setup();
        _store = Path.Combine(TestStorage.ForFixture(nameof(EngineBackupServiceTests)), Guid.NewGuid().ToString("N"));
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

    [Test]
    [CancelAfter(15_000)]
    public void Auto_StartsAChainWithAFullPoint_ThenContinuesItIncrementally()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, 200);

        var first = Create(dbe);
        Assert.That(first.Kind, Is.EqualTo(BackupPointKind.Full));
        Assert.That(first.Offline, Is.False);

        Update(dbe, ids, 7);
        var second = Create(dbe);
        Assert.That(second.Kind, Is.EqualTo(BackupPointKind.Incremental));
        Assert.That(second.ParentId, Is.EqualTo(first.BackupId));
        Assert.That(second.ChainId, Is.EqualTo(first.ChainId));
        Assert.That(second.PointLsn, Is.GreaterThan(first.PointLsn));

        Assert.That(BackupOperations.Verify(_store, BackupVerifyLevel.Full).Verdict, Is.EqualTo(BackupVerifyVerdict.Ok));
        Assert.That(BackupOperations.List(_store).Points.Select(p => p.BackupId), Is.EqualTo(new[] { first.BackupId, second.BackupId }),
            "what CreateAsync returned is what the store holds");
    }

    /// <summary>DL-15: with no BackupOptions there is no configured store to take a point into; naming one still works.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void WithoutBackupOptions_ThePointNeedsAStore()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);

        var ex = Assert.Throws<TyphonException>(() => dbe.Backup.CreateAsync());
        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.BackupNotConfigured));
        Assert.That(Create(dbe).Kind, Is.EqualTo(BackupPointKind.Full));
    }

    [Test]
    [CancelAfter(15_000)]
    public void Full_StartsANewChain_WhereAutoWouldContinue()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Spawn(dbe, 50);
        var first = Create(dbe);

        var full = Create(dbe, BackupRequestKind.Full);
        Assert.That(full.Kind, Is.EqualTo(BackupPointKind.Full));
        Assert.That(full.ChainId, Is.Not.EqualTo(first.ChainId));
        Assert.That(full.ParentId, Is.EqualTo(0u));
    }

    [Test]
    [CancelAfter(15_000)]
    public void ASecondCallWhileOneRuns_IsRefusedWithBackupInProgress()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Spawn(dbe, 50);

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

        var running = dbe.Backup.CreateAsync(_store);
        try
        {
            Assert.That(frozen.Wait(Budget), Is.True, "premise: the first backup is running");
            var second = Assert.ThrowsAsync<TyphonException>(() => dbe.Backup.CreateAsync(_store));
            Assert.That(second.ErrorCode, Is.EqualTo(TyphonErrorCode.BackupInProgress));
        }
        finally
        {
            release.Set();
        }

        Assert.That(running.Wait(Budget), Is.True);
        Assert.That(running.Result.BackupId, Is.EqualTo(1u), "the running backup is not disturbed by the refused one");
    }

    [Test]
    [CancelAfter(15_000)]
    public void Cancelled_PublishesNothing_AndTheNextPointStillContinuesTheChain()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, 200);
        var first = Create(dbe);
        Update(dbe, ids, 3);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);

        using var cts = new CancellationTokenSource();
        var imageSet = new List<int>();
        var coordinator = dbe.BackupCoordinator;
        coordinator.Probe = (phase, _) =>
        {
            if (phase == CapturePhase.AfterFreeze)
            {
                var capture = coordinator.LastCapture;
                for (var page = 0; page < capture.ImagePages; page++)
                {
                    if (capture.IsInImage(page))
                    {
                        imageSet.Add(page);
                    }
                }

                cts.Cancel();
            }
        };

        var cancelled = Assert.ThrowsAsync<BackupAbortedException>(() => dbe.Backup.CreateAsync(_store, ct: cts.Token));
        Assert.That(cancelled.Reason, Is.EqualTo(BackupAbortReason.Cancelled));
        Assert.That(cancelled.ErrorCode, Is.EqualTo(TyphonErrorCode.BackupAborted));
        Assert.That(cancelled, Is.InstanceOf<BackupException>(), "an application catches every backup failure as a BackupException");
        Assert.That(cancelled.IsTransient, Is.False, "a cancellation is not worth retrying on its own");
        coordinator.Probe = null;

        var listing = BackupOperations.List(_store);
        Assert.That(listing.Points.Select(p => p.BackupId), Is.EqualTo(new[] { first.BackupId }), "a cancelled backup publishes nothing");
        Assert.That(listing.TemporaryFiles, Is.Empty, "nor leaves its temporary pack behind");
        Assert.That(imageSet, Is.Not.Empty, "premise: the cancelled capture froze a page set");
        Assert.That(imageSet.Where(p => !dbe.MMF.ChangeMap.IsSet(p)), Is.Empty, "the cancelled capture's page set is back in the live map (BKP-07)");

        var next = Create(dbe);
        Assert.That(next.Kind, Is.EqualTo(BackupPointKind.Incremental), "a cancellation leaves the lineage trusted");
        Assert.That(next.ParentId, Is.EqualTo(first.BackupId));
    }

    /// <summary>
    /// The pack's write buffers are the engine's memory: children of the backup coordinator while a point is written, so the resource tree and the
    /// unmanaged-memory gauges see them, and released when the point completes or is abandoned.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void ThePacksWriteBuffers_AreTheCoordinatorsResources_WhileThePointIsWritten_AndReleasedAfter()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, 200);
        var coordinator = dbe.BackupCoordinator;
        string[] whileWriting = null;
        using var cts = new CancellationTokenSource();
        var cancelAt = (CapturePhase)byte.MaxValue;
        coordinator.Probe = (phase, _) =>
        {
            if (phase == CapturePhase.ScannerClaimed && whileWriting == null)
            {
                whileWriting = PackBuffers(coordinator);
            }

            if (phase == cancelAt)
            {
                cts.Cancel();
            }
        };

        var first = Create(dbe);
        Assert.That(whileWriting, Is.SupersetOf(new[] { $"Backup.Pack{first.BackupId}.Buffer0", $"Backup.Pack{first.BackupId}.Buffer1" }),
            "the write buffers are allocated under the coordinator");
        Assert.That(PackBuffers(coordinator), Is.Empty, "released once the point is published");

        whileWriting = null;
        cancelAt = CapturePhase.ScannerClaimed;
        Update(dbe, ids, 9);
        Assert.ThrowsAsync<BackupAbortedException>(() => dbe.Backup.CreateAsync(_store, ct: cts.Token));
        Assert.That(whileWriting, Is.Not.Empty, "premise: the abandoned point had its buffers");
        Assert.That(PackBuffers(coordinator), Is.Empty, "released once the point is abandoned");
    }

    private static string[] PackBuffers(IResource coordinator)
        => coordinator.Children.Select(c => c.Id).Where(id => id.StartsWith("Backup.Pack", StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// M4: once the point is published nothing may report a failure for it. The summary comes from what the store published, not from reading the
    /// store again: here the store turns unreadable the moment the pack is renamed into place, and the call still returns the published point.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void AStoreUnreadableRightAfterThePublish_StillReturnsThePublishedPoint()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Spawn(dbe, 50);
        var fs = new UnreadableAfterPublish();
        ((BackupService)dbe.Backup).StoreFileSystem = fs;

        var point = Create(dbe);
        Assert.That(fs.Published, Is.True, "premise: the store turned unreadable after the publish");
        Assert.That(point.BackupId, Is.EqualTo(1u));
        Assert.That(point.Kind, Is.EqualTo(BackupPointKind.Full));
        Assert.That(File.Exists(Path.Combine(_store, "packs", point.PackFile)), Is.True);
    }

    /// <summary>The real disk, until a pack is renamed into place; after that every read of the store fails.</summary>
    private sealed class UnreadableAfterPublish : IStoreFileSystem
    {
        private readonly IStoreFileSystem _disk = StoreFileSystem.Instance;

        public bool Published { get; private set; }

        public IStoreFile OpenCreateNew(string path, long preallocationSize = 0) => _disk.OpenCreateNew(path, preallocationSize);

        public IStoreFile OpenRead(string path) => Published ? throw new IOException("the store is gone") : _disk.OpenRead(path);

        public IDisposable TryLockExclusive(string path) => _disk.TryLockExclusive(path);

        public bool ProbeExclusive(string path) => _disk.ProbeExclusive(path);

        public void Rename(string source, string destination, bool replace)
        {
            _disk.Rename(source, destination, replace);
            Published |= destination.EndsWith(".tpack", StringComparison.Ordinal);
        }

        public void FlushDirectory(string directory) => _disk.FlushDirectory(directory);

        public bool FileExists(string path) => _disk.FileExists(path);

        public bool DirectoryExists(string path) => _disk.DirectoryExists(path);

        public void CreateDirectory(string path) => _disk.CreateDirectory(path);

        public void DeleteFile(string path) => _disk.DeleteFile(path);

        public void DeleteDirectory(string path) => _disk.DeleteDirectory(path);

        public IReadOnlyList<StoreFileEntry> EnumerateFiles(string directory) =>
            Published ? throw new IOException("the store is gone") : _disk.EnumerateFiles(directory);

        public bool IsBundleHeldByLiveProcess(string bundleDirectory) => _disk.IsBundleHeldByLiveProcess(bundleDirectory);
    }

    [Test]
    public void BadArguments_AreRejectedBeforeAnyCapture()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Assert.ThrowsAsync<ArgumentException>(() => dbe.Backup.CreateAsync(""));
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => dbe.Backup.CreateAsync(_store, (BackupRequestKind)7));
        Assert.That(Directory.Exists(_store), Is.False, "nothing was started");
    }

    private BackupPointSummary Create(DatabaseEngine dbe, BackupRequestKind kind = BackupRequestKind.Auto)
    {
        var task = dbe.Backup.CreateAsync(_store, kind);
        Assert.That(task.Wait(Budget), Is.True, "the backup did not finish");
        return task.Result;
    }
}
