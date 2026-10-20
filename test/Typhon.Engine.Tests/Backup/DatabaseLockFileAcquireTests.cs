using NUnit.Framework;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// <see cref="DatabaseLockFile.TryAcquire"/>: the atomic acquisition an offline backup takes before reading a closed bundle (plan 1413 DC-17).
/// </summary>
[TestFixture]
internal sealed class DatabaseLockFileAcquireTests
{
    /// <summary>A pid no process has: <see cref="System.Diagnostics.Process.GetProcessById(int)"/> refuses it, so the owner reads as dead.</summary>
    private const int DeadPid = 2_000_000_000;

    private string _bundle;

    [SetUp]
    public void SetUp()
    {
        _bundle = Path.Combine(TestStorage.ForFixture(nameof(DatabaseLockFileAcquireTests)), TestContext.CurrentContext.Test.Name + ".typhon");
        Directory.CreateDirectory(_bundle);
        File.Delete(DatabaseLockFile.PathFor(_bundle));
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_bundle, true);
        }
        catch (IOException)
        {
        }
    }

    [Test]
    [CancelAfter(5_000)]
    public void AFreeBundle_IsAcquired_AndRecordsThisProcessAsNotYieldable()
    {
        Assert.That(DatabaseLockFile.TryAcquire(_bundle, yieldable: false, out var acquired), Is.True);

        Assert.That(DatabaseLockFile.TryReadLock(_bundle, out var onDisk), Is.True);
        Assert.That(onDisk.Pid, Is.EqualTo(Environment.ProcessId));
        Assert.That(onDisk.Yieldable, Is.False);
        Assert.That(onDisk.StartedAt, Is.EqualTo(acquired.StartedAt));
    }

    [Test]
    [CancelAfter(5_000)]
    public void ASecondClaimant_IsRefused_AndToldWhoHoldsIt()
    {
        Assert.That(DatabaseLockFile.TryAcquire(_bundle, false, out var first), Is.True);
        var before = File.ReadAllBytes(DatabaseLockFile.PathFor(_bundle));

        Assert.That(DatabaseLockFile.TryAcquire(_bundle, false, out var holder), Is.False, "a live holder must never be displaced");
        Assert.That(holder.Pid, Is.EqualTo(first.Pid));
        Assert.That(holder.StartedAt, Is.EqualTo(first.StartedAt));
        Assert.That(File.ReadAllBytes(DatabaseLockFile.PathFor(_bundle)), Is.EqualTo(before), "the refused claimant must not touch the lock");
    }

    [Test]
    [CancelAfter(5_000)]
    public void AStaleLock_IsReplaced()
    {
        File.WriteAllText(DatabaseLockFile.PathFor(_bundle),
            DatabaseLockFile.SerializeLock(DeadPid, DateTimeOffset.UtcNow.AddHours(-1), Environment.MachineName, yieldable: false));

        Assert.That(DatabaseLockFile.TryAcquire(_bundle, false, out var acquired), Is.True);
        Assert.That(acquired.Pid, Is.EqualTo(Environment.ProcessId));
        Assert.That(DatabaseLockFile.TryReadLock(_bundle, out var onDisk) && onDisk.Pid == Environment.ProcessId, Is.True);
    }

    [Test]
    [CancelAfter(5_000)]
    public void AnUnreadableLock_IsReplaced_AsTheEngineDoes()
    {
        File.WriteAllText(DatabaseLockFile.PathFor(_bundle), "{ not json");
        File.SetLastWriteTimeUtc(DatabaseLockFile.PathFor(_bundle), DateTime.UtcNow - DatabaseLockFile.UnreadableLockGrace - TimeSpan.FromMinutes(1));

        Assert.That(DatabaseLockFile.TryAcquire(_bundle, false, out _), Is.True);
    }

    /// <summary>
    /// M8 (b): a claimant creates the lock, then writes it; in between it reads as empty. Taking that for a corrupt lock deleted a lock its writer
    /// was about to own. A young unreadable lock is somebody's: refused, left untouched.
    /// </summary>
    [Test]
    [CancelAfter(5_000)]
    public void AnUnreadableLockStillBeingWritten_IsLeftAlone()
    {
        File.WriteAllBytes(DatabaseLockFile.PathFor(_bundle), []);

        Assert.That(DatabaseLockFile.TryAcquire(_bundle, false, out _), Is.False);
        Assert.That(File.Exists(DatabaseLockFile.PathFor(_bundle)) && new FileInfo(DatabaseLockFile.PathFor(_bundle)).Length == 0, Is.True,
            "the lock being written is not deleted");
    }

    /// <summary>
    /// M8 (a): two claimants that both read a lock as stale used to both take it over: the second deleted the first's fresh lock and both believed they
    /// held the database. Claimant A is stopped right before its delete, under the takeover; claimant B runs meanwhile and must be refused.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void TwoClaimantsOfAStaleLock_OnlyOneTakesItOver()
    {
        File.WriteAllText(DatabaseLockFile.PathFor(_bundle),
            DatabaseLockFile.SerializeLock(DeadPid, DateTimeOffset.UtcNow.AddHours(-1), Environment.MachineName, yieldable: false));
        using var aAtDelete = new ManualResetEventSlim();
        using var bDone = new ManualResetEventSlim();
        var aThread = -1;
        DatabaseLockFile.BeforeStaleLockDeletedForTest = _ =>
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref aThread))
            {
                aAtDelete.Set();
                bDone.Wait(TimeSpan.FromSeconds(5));
            }
        };

        try
        {
            var a = Task.Factory.StartNew(() =>
            {
                Volatile.Write(ref aThread, Environment.CurrentManagedThreadId);
                return DatabaseLockFile.TryAcquire(_bundle, false, out var info) ? info : (DatabaseLockFile.LockInfo?)null;
            }, TaskCreationOptions.LongRunning);
            Assert.That(aAtDelete.Wait(TimeSpan.FromSeconds(5)), Is.True, "premise: A is about to delete the stale lock");

            var bWon = DatabaseLockFile.TryAcquire(_bundle, false, out _);
            bDone.Set();
            var aWon = a.Result;

            Assert.That(bWon, Is.False, "B took over a lock A was taking over");
            Assert.That(aWon, Is.Not.Null);
            Assert.That(DatabaseLockFile.TryReadLock(_bundle, out var onDisk) && onDisk.StartedAt == aWon.Value.StartedAt, Is.True, "A holds it");
        }
        finally
        {
            DatabaseLockFile.BeforeStaleLockDeletedForTest = null;
        }
    }

    /// <summary>
    /// M8 (c): on Windows an open of a lock name that is being deleted (classic delete disposition) fails with access denied, and so does one over a
    /// name that is not a file at all; that is "not acquired", never an exception escaping to the capture or the restore. Modern NTFS deletes with
    /// POSIX semantics (the name goes at once), so the access-denied case is staged with a directory named db.lock.
    /// </summary>
    [Test]
    [Platform("Win")]
    [CancelAfter(10_000)]
    public void ACreateRefusedAccess_IsNotAcquired_AndNeverThrows()
    {
        var path = DatabaseLockFile.PathFor(_bundle);
        Directory.CreateDirectory(path);

        Assert.That(DatabaseLockFile.TryAcquire(_bundle, false, out _), Is.False);

        Directory.Delete(path);
        Assert.That(DatabaseLockFile.TryAcquire(_bundle, false, out _), Is.True, "and once the name is free, it is acquired");
    }

    [Test]
    [CancelAfter(5_000)]
    public void AnotherMachinesLock_IsNeverDisplaced()
    {
        File.WriteAllText(DatabaseLockFile.PathFor(_bundle),
            DatabaseLockFile.SerializeLock(DeadPid, DateTimeOffset.UtcNow, "some-other-machine-" + Guid.NewGuid().ToString("N"), yieldable: false));

        Assert.That(DatabaseLockFile.TryAcquire(_bundle, false, out var holder), Is.False);
        Assert.That(holder.Pid, Is.EqualTo(DeadPid));
    }

    [Test]
    [CancelAfter(5_000)]
    public void Release_DeletesOnlyTheLockItWrote()
    {
        Assert.That(DatabaseLockFile.TryAcquire(_bundle, false, out var acquired), Is.True);

        // Somebody else's lock in its place (as after a crash of ours and an engine's open): not ours to delete.
        var foreign = DatabaseLockFile.SerializeLock(Environment.ProcessId, acquired.StartedAt.AddSeconds(5), Environment.MachineName, false);
        File.WriteAllText(DatabaseLockFile.PathFor(_bundle), foreign);
        Assert.That(DatabaseLockFile.Release(_bundle, acquired), Is.False);
        Assert.That(File.ReadAllText(DatabaseLockFile.PathFor(_bundle)), Is.EqualTo(foreign));

        File.Delete(DatabaseLockFile.PathFor(_bundle));
        Assert.That(DatabaseLockFile.TryAcquire(_bundle, false, out acquired), Is.True);
        Assert.That(DatabaseLockFile.Release(_bundle, acquired), Is.True);
        Assert.That(File.Exists(DatabaseLockFile.PathFor(_bundle)), Is.False);
    }
}
