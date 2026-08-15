using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// The advisory lock file (<c>db.lock</c>) may only be removed by the instance that wrote it.
///
/// <para>A rejected open — the database is held by a live process — reaches <c>ReleaseLockFile</c> through the
/// <see cref="DatabaseLockedException"/> handler in the <c>PagedMMF</c> constructor, having never written a lock of its own.
/// It found the HOLDER's. Deleting it there meant the collision the lock exists to report destroyed the record of itself:
/// the database still held, but now advertising no owner, so the next opener could not say who had it.</para>
///
/// <para>Observed in the field: a Workbench session held a database; one rejected application open left the session running
/// with no lock file on disk.</para>
/// </summary>
[TestFixture]
[NonParallelizable]
internal sealed class AdvisoryLockOwnershipTests
{
    private string _root;
    private string _dbDir;
    private ServiceProvider _serviceProvider;

    private static string DbName
    {
        get
        {
            const string prefix = "Lock_";
            const int max = 63;
            var name = TestContext.CurrentContext.Test.Name.Replace('(', '_').Replace(')', '_');
            if (prefix.Length + name.Length > max)
            {
                name = name[^(max - prefix.Length)..];
            }
            return prefix + name;
        }
    }

    private string LockPath => Path.Combine(_dbDir, $"{DbName}.typhon", "db.lock");

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(AdvisoryLockOwnershipTests), DbName);
        _dbDir = Path.Combine(_root, "db");
        Directory.CreateDirectory(_dbDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = DbName;
                opts.DatabaseDirectory = _dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine();

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// A second open of a held database is rejected — and must leave the holder's lock exactly where it was. The rejected
    /// opener's own pid is alive (it is this very process), which is precisely the live-holder path.
    /// </summary>
    [Test]
    public void RejectedOpen_MustNotDeleteTheHoldersLockFile()
    {
        using var holderScope = _serviceProvider.CreateScope();
        using var holder = holderScope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        holder.RegisterComponentFromAccessor<CompA>();
        holder.InitializeArchetypes();

        Assert.That(File.Exists(LockPath), Is.True, "the holder must have written an advisory lock");
        var lockContentBefore = File.ReadAllText(LockPath);

        // Second open of the same database — rejected, because the recorded pid is alive.
        Assert.Throws<DatabaseLockedException>(() =>
        {
            using var intruderScope = _serviceProvider.CreateScope();
            _ = intruderScope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        });

        Assert.That(File.Exists(LockPath), Is.True,
            "the REJECTED open deleted the live holder's lock file — the database is still held but now advertises no owner, "
            + "so the next opener cannot report who has it");
        Assert.That(File.ReadAllText(LockPath), Is.EqualTo(lockContentBefore), "the holder's lock content must be untouched");
    }

    /// <summary>Control: a normal open/close pair still cleans up after itself — the ownership gate must not leak lock files.</summary>
    [Test]
    public void NormalOpenAndClose_StillRemovesItsOwnLockFile()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();
            Assert.That(File.Exists(LockPath), Is.True, "an open database must hold an advisory lock");
        }

        Assert.That(File.Exists(LockPath), Is.False, "a clean close must remove the lock it owned — otherwise every close leaks a stale lock");
    }

    /// <summary>
    /// A stale lock (dead pid) is still taken over and replaced — the ownership gate must not break crash recovery from a
    /// previous process that died holding the database.
    /// </summary>
    [Test]
    public void StaleLockFromADeadProcess_IsStillTakenOver()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath) ?? _dbDir);
        // pid 0 is never a live user process on Windows or POSIX, so this reads as a crashed previous owner.
        File.WriteAllText(LockPath, $"{{\"pid\":0,\"startedAt\":\"{DateTimeOffset.UtcNow:o}\",\"machineName\":\"{Environment.MachineName}\"}}");

        using (var scope = _serviceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();
            Assert.That(File.ReadAllText(LockPath), Does.Not.Contain("\"pid\":0"), "the stale lock must have been replaced by this process's own");
        }

        Assert.That(File.Exists(LockPath), Is.False, "having taken the stale lock over, this process owns it and must remove it on close");
    }
}
