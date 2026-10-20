using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// T9 / AC-R4 / BKP-05: an offline backup of a closed database — cleanly closed, hard-crashed, or crashed with nothing in its WAL past the
/// checkpoint — restores to a bundle the engine opens with every committed entity, through the crash path whenever the image needs it; a clean image
/// keeps its lineage, so the next point is incremental; and a database an engine holds, or one whose WAL is elsewhere, is refused.
/// </summary>
[TestFixture]
internal sealed class OfflineCaptureRestoreTests
{
    private string _root;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestStorage.ForFixture(nameof(OfflineCaptureRestoreTests)), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DbDir);
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

    private enum Close
    {
        Clean,
        HardCrash,
    }

    private string DbDir => Path.Combine(_root, "db");

    private string Store => Path.Combine(_root, "store");

    private string Bundle(string name) => Path.Combine(DbDir, name + ".typhon");

    /// <summary>Opens (creating) the database, spawns <paramref name="spawn"/> entities, optionally checkpoints between, and closes as asked.</summary>
    private void Session(string name, int spawnBefore, bool checkpoint, int spawnAfter, Close close, string walDir = null, bool onlineBackup = false)
    {
        using var provider = BackupTestImages.BuildProvider(name, DbDir, walDir ?? Path.Combine(Bundle(name), "wal"));
        using var scope = provider.CreateScope();
        var dbe = Open(scope);
        Spawn(dbe, spawnBefore);
        if (checkpoint)
        {
            dbe.ForceCheckpoint();
        }

        if (onlineBackup)
        {
            var task = dbe.BackupCoordinator.CaptureAsync(new FileSystemBackupStore(Store), BackupKind.Incremental, System.Threading.CancellationToken.None);
            Assert.That(task.Wait(TimeSpan.FromSeconds(10)), Is.True, "the online capture did not finish");
        }

        Spawn(dbe, spawnAfter);
        if (close == Close.HardCrash)
        {
            dbe.SimulateHardCrash();
        }
    }

    private static DatabaseEngine Open(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void Spawn(DatabaseEngine dbe, int count)
    {
        using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
        for (var i = 0; i < count; i++)
        {
            using var tx = uow.CreateTransaction();
            tx.Spawn<CompAArch>(CompAArch.A.Set(new CompA(i, i, i)));
            tx.Commit();
        }

        uow.Flush();
    }

    /// <summary>Restores point <paramref name="id"/> and opens it; returns what the open saw.</summary>
    private (int Entities, bool CrashPath, ChunkSummaryReadResult Summary, string Bundle) RestoreAndOpen(string name, uint id)
    {
        var target = Path.Combine(_root, "restored-" + id);
        var outcome = RestoreStager.Run(Store, PointSelector.ById(id), target);
        using var provider = BackupTestImages.BuildProvider(name, target, Path.Combine(outcome.Target, "wal"));
        using var scope = provider.CreateScope();
        var dbe = Open(scope);
        using var tx = dbe.CreateQuickTransaction();
        return (tx.Query<CompAArch>().Execute().Count, dbe.LastOpenRanCrashRecovery, dbe.LastOpenChunkSummaryResult, outcome.Target);
    }

    [Test]
    [CancelAfter(60_000)]
    public void ACleanlyClosedBundle_RestoresToAnOpenThatTrustsNothingBesideTheImage()
    {
        Session("BkT9a", 10, checkpoint: true, 4, Close.Clean);
        Assert.That(File.Exists(Path.Combine(Bundle("BkT9a"), "chunk-summary")), Is.True, "the precondition: a clean close beside a chunk-summary");

        var point = OfflineCapture.Create(Bundle("BkT9a"), Store);

        Assert.That(point.Kind, Is.EqualTo(BackupKind.Full));
        var opened = RestoreAndOpen("BkT9a", point.BackupId);
        Assert.That(opened.Entities, Is.EqualTo(14));
        Assert.That(opened.CrashPath, Is.False);
        Assert.That(opened.Summary, Is.EqualTo(ChunkSummaryReadResult.Missing), "OQ-2: the restored clean image scans; no summary travels with it");
    }

    [Test]
    [CancelAfter(60_000)]
    public void AHardCrashedBundle_RestoresThroughTheCrashPath_WithEveryDurableCommit()
    {
        Session("BkT9b", 10, checkpoint: true, 5, Close.HardCrash);

        var point = OfflineCapture.Create(Bundle("BkT9b"), Store);

        var opened = RestoreAndOpen("BkT9b", point.BackupId);
        Assert.That(opened.CrashPath, Is.True);
        Assert.That(opened.Entities, Is.EqualTo(15), "the five commits after the checkpoint live only in the tail");
        Assert.That(point.PointLsn, Is.GreaterThan(point.CheckpointLsn));
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("BKP-05")]
    public void ACrashedBundleWithNothingPastItsCheckpoint_StillCarriesASegment_AndTakesTheCrashPath()
    {
        // Staged, not hoped for: a clean close makes C cover every record; a session that writes nothing and crashes leaves the image unclean.
        Session("BkT9c", 10, checkpoint: true, 0, Close.Clean);
        Session("BkT9c", 0, checkpoint: false, 0, Close.HardCrash);

        var point = OfflineCapture.Create(Bundle("BkT9c"), Store);

        Assert.That(point.PointLsn, Is.EqualTo(point.CheckpointLsn), "the staging failed: a record past C");
        var opened = RestoreAndOpen("BkT9c", point.BackupId);
        Assert.That(opened.CrashPath, Is.True, "DC-3: without a segment the open would skip recovery over an unclean image");
        Assert.That(opened.Entities, Is.EqualTo(10));
        Assert.That(Directory.GetFiles(Path.Combine(opened.Bundle, "wal")), Is.Not.Empty);
    }

    [Test]
    [CancelAfter(90_000)]
    public void ACleanImage_KeepsItsLineage_SoTheNextPointIsIncremental_AndRestoresTheFileByteForByte()
    {
        // The lineage starts online: an engine backs up, then closes cleanly with a backup-state its meta vouches for (a nonce).
        Session("BkT9d", 8, checkpoint: true, 2, Close.Clean, onlineBackup: true);
        var second = OfflineCapture.Create(Bundle("BkT9d"), Store);
        Assert.That((second.BackupId, second.Kind), Is.EqualTo((2u, BackupKind.Incremental)), "offline continues the online chain");

        // The offline capture rewrote the backup-state under the same nonce: the next session adopts it, writes, closes with its map.
        Session("BkT9d", 3, checkpoint: false, 0, Close.Clean);
        var third = OfflineCapture.Create(Bundle("BkT9d"), Store);

        Assert.That(third.Kind, Is.EqualTo(BackupKind.Incremental));
        Assert.That(third.ChainId, Is.EqualTo(second.ChainId));
        Assert.That(third.PageCount, Is.LessThan(third.ImagePages), "only the pages the session wrote, and the meta pair");

        var dataNow = File.ReadAllBytes(Path.Combine(Bundle("BkT9d"), "data"));
        var opened = RestoreAndOpen("BkT9d", third.BackupId);
        Assert.That(opened.Entities, Is.EqualTo(13));

        var target = Path.Combine(_root, "byte-exact");
        var outcome = RestoreStager.Run(Store, PointSelector.ById(third.BackupId), target);
        Assert.That(File.ReadAllBytes(Path.Combine(outcome.Target, "data")).AsSpan().SequenceEqual(dataNow), Is.True,
            "BKP-02: the restored image is the data file at the capture");
    }

    [Test]
    [CancelAfter(60_000)]
    public void AFirstOfflineBackup_IsFull_AndCannotStartALineage()
    {
        // A database never backed up closed with no backup-state nonce in its meta, and offline capture never writes the data file to add one.
        Session("BkT9i", 4, checkpoint: false, 0, Close.Clean);
        var first = OfflineCapture.Create(Bundle("BkT9i"), Store);
        var second = OfflineCapture.Create(Bundle("BkT9i"), Store);

        Assert.That((first.Kind, second.Kind), Is.EqualTo((BackupKind.Full, BackupKind.Full)));
        Assert.That(File.Exists(BackupStateFile.PathIn(Bundle("BkT9i"))), Is.False);
    }

    [Test]
    [CancelAfter(60_000)]
    public void AnUncleanImage_IsAlwaysFull_AndLeavesNoBackupState()
    {
        Session("BkT9e", 6, checkpoint: true, 2, Close.HardCrash);
        var first = OfflineCapture.Create(Bundle("BkT9e"), Store);
        var second = OfflineCapture.Create(Bundle("BkT9e"), Store);

        Assert.That((first.Kind, second.Kind), Is.EqualTo((BackupKind.Full, BackupKind.Full)));
        Assert.That(second.ChainId, Is.Not.EqualTo(first.ChainId));
        Assert.That(File.Exists(BackupStateFile.PathIn(Bundle("BkT9e"))), Is.False);
    }

    [Test]
    [CancelAfter(60_000)]
    public void AWalKeptElsewhere_IsRefused_UntilItIsNamed()
    {
        var external = Path.Combine(_root, "external-wal");
        Session("BkT9f", 6, checkpoint: true, 3, Close.HardCrash, walDir: external);

        var ex = Assert.Throws<StorageException>(() => OfflineCapture.Create(Bundle("BkT9f"), Store));
        Assert.That(ex.Message, Does.Contain("needs its WAL"));
        Assert.That(Directory.Exists(Path.Combine(Store, "packs")) && Directory.GetFiles(Path.Combine(Store, "packs")).Length > 0, Is.False);

        var point = OfflineCapture.Create(Bundle("BkT9f"), Store, new CreateOptions { WalDirectory = external });
        var opened = RestoreAndOpen("BkT9f", point.BackupId);
        Assert.That((opened.CrashPath, opened.Entities), Is.EqualTo((true, 9)), "the restored bundle carries the tail in its own wal/");
    }

    [Test]
    [CancelAfter(60_000)]
    public void ADatabaseAnEngineHolds_IsRefused()
    {
        Session("BkT9g", 2, checkpoint: false, 0, Close.Clean);
        using var provider = BackupTestImages.BuildProvider("BkT9g", DbDir, Path.Combine(Bundle("BkT9g"), "wal"));
        using var scope = provider.CreateScope();
        var dbe = Open(scope);

        Assert.Throws<DatabaseLockedException>(() => OfflineCapture.Create(Bundle("BkT9g"), Store));
        Spawn(dbe, 1);
        Assert.That(DatabaseLockFile.TryReadLock(Bundle("BkT9g"), out var holder) && holder.Pid == Environment.ProcessId, Is.True,
            "the engine's lock is untouched");
    }

    [Test]
    [CancelAfter(60_000)]
    public void AnEngineWhoseLockFileWasDeleted_IsStillKeptOut_ByTheDataFile()
    {
        // db.lock is advisory and closed while the engine runs (#1423): the data file opened with no sharing is the guard that holds.
        Session("BkT9j", 2, checkpoint: false, 0, Close.Clean);
        using var provider = BackupTestImages.BuildProvider("BkT9j", DbDir, Path.Combine(Bundle("BkT9j"), "wal"));
        using var scope = provider.CreateScope();
        Open(scope);
        File.Delete(DatabaseLockFile.PathFor(Bundle("BkT9j")));

        var ex = Assert.Throws<DatabaseLockedException>(() => OfflineCapture.Create(Bundle("BkT9j"), Store));
        Assert.That(ex.InnerException, Is.InstanceOf<IOException>());
        Assert.That(File.Exists(DatabaseLockFile.PathFor(Bundle("BkT9j"))), Is.False, "the refused capture released its own advisory lock");
    }

    /// <summary>
    /// Only a sharing violation on the data file means another holder ("locked"). Any other failure to open it surfaces as the I/O failure it is: it
    /// used to become a DatabaseLockedException naming process 0, sending the operator looking for a process that does not exist.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void OnlyASharingViolationOnTheDataFile_ReadsAsLocked()
    {
        Session("BkT9io", 5, checkpoint: true, 0, Close.Clean);
        try
        {
            OfflineCapture.OpenDataFileForTest = _ => throw new IOException("The device is not ready.", unchecked((int)0x80070015));
            var io = Assert.Throws<IOException>(() => OfflineCapture.Create(Bundle("BkT9io"), Store));
            Assert.That(io, Is.Not.InstanceOf<DatabaseLockedException>());

            OfflineCapture.OpenDataFileForTest = _ => throw new IOException("in use", StoreFileSystem.SharingViolationHResult);
            Assert.Throws<DatabaseLockedException>(() => OfflineCapture.Create(Bundle("BkT9io"), Store));
        }
        finally
        {
            OfflineCapture.OpenDataFileForTest = null;
        }

        Assert.That(OfflineCapture.Create(Bundle("BkT9io"), Store).Kind, Is.EqualTo(BackupKind.Full), "and nothing was left held");
    }

    /// <summary>The real thing, measured: a second exclusive open of a file reads as a sharing violation, a missing file does not.</summary>
    [Test]
    public void TheSharingViolationPredicate_RecognisesARealOne()
    {
        var path = Path.Combine(_root, "held");
        using (File.Open(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            var held = Assert.Catch<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None).Dispose());
            Assert.That(StoreFileSystem.IsSharingViolation(held), Is.True, $"HResult 0x{held.HResult:X8}");
        }

        var missing = Assert.Catch<IOException>(() => File.Open(path + ".nope", FileMode.Open).Dispose());
        Assert.That(StoreFileSystem.IsSharingViolation(missing), Is.False);
    }

    /// <summary>
    /// A restored bundle not opened yet still carries restore.json: its meta has the OLD timeline until its first open adopts the new one (#1440). A
    /// point taken from it would record the pre-adoption timeline, so the capture refuses it; once opened, it backs up.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void ARestoreNotOpenedYet_IsRefused_UntilItsFirstOpenAdoptsItsTimeline()
    {
        Session("BkT9r", 6, checkpoint: true, 0, Close.Clean);
        var point = OfflineCapture.Create(Bundle("BkT9r"), Store);
        var restored = RestoreStager.Run(Store, PointSelector.ById(point.BackupId), Path.Combine(_root, "restored"));

        var refused = Assert.Throws<StorageException>(() => OfflineCapture.Create(restored.Target, Path.Combine(_root, "store2")));
        Assert.That(refused.Message, Does.Contain("restore.json"));

        using (var provider = BackupTestImages.BuildProvider("BkT9r", Path.GetDirectoryName(restored.Target), Path.Combine(restored.Target, "wal")))
        using (var scope = provider.CreateScope())
        {
            Open(scope);
        }

        Assert.That(OfflineCapture.Create(restored.Target, Path.Combine(_root, "store2")).Kind, Is.EqualTo(BackupKind.Full));
    }

    /// <summary>
    /// BKP-VER-12 on a real point: a hard-crashed bundle with commits past its checkpoint, so its tail carries a <c>P</c> above <c>C</c>. A full
    /// verify decodes the stored tail and replays it as recovery will: it yields the footer's <c>P</c>, and the check runs instead of being skipped.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void AFullVerify_RecomputesARealPointsP_FromItsStoredTail()
    {
        Session("BkT9v", 10, checkpoint: true, 6, Close.HardCrash);
        var point = OfflineCapture.Create(Bundle("BkT9v"), Store);
        Assert.That(point.PointLsn, Is.GreaterThan(point.CheckpointLsn), "premise: the tail carries commits past C");

        using (var pack = PackReader.Open(StoreFileSystem.Instance, point.PackPath))
        {
            var tail = pack.LoadTailDirectory();
            Assert.That(BackupVerifier.TailPointLsn(pack, tail, pack.Header.CheckpointLsn), Is.EqualTo(point.PointLsn));
        }

        var report = BackupVerifier.VerifyStore(Store, VerifyLevel.Full);
        Assert.That(report.SkippedChecks, Is.Empty, "BKP-VER-12 ran");
        Assert.That(report.AllFindings, Is.Empty, string.Join("\n", report.AllFindings));
        Assert.That(report.Verdict, Is.EqualTo(VerifyVerdict.Ok));
    }

    [Test]
    [CancelAfter(60_000)]
    public void TheCapture_LeavesNoLockBehind()
    {
        Session("BkT9h", 2, checkpoint: false, 0, Close.Clean);
        OfflineCapture.Create(Bundle("BkT9h"), Store);

        Assert.That(File.Exists(DatabaseLockFile.PathFor(Bundle("BkT9h"))), Is.False);
        Assert.That(Directory.GetFiles(Path.Combine(Store, "packs")).Select(Path.GetFileName), Is.EqualTo(new[] { "00000001-F.tpack" }));
    }
}
