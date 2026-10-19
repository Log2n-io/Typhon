using NUnit.Framework;
using System;
using System.IO;

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

        Assert.That(DatabaseLockFile.TryAcquire(_bundle, false, out _), Is.True);
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
