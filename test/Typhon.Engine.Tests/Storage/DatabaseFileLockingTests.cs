using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Typhon.Engine.Tests;

class DatabaseFileLockingTests
{
    private string _testDir;

    [SetUp]
    public void Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(DatabaseFileLockingTests));
        Directory.CreateDirectory(_testDir);
    }

    [TearDown]
    public void TearDown()
    {
        Log.CloseAndFlush();

        // Clean up test files — best effort
        try
        {
            if (Directory.Exists(_testDir))
            {
                foreach (var file in Directory.GetFiles(_testDir))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Creates a DI service provider configured for the given database name.
    /// Calls EnsureFileDeleted to start clean.
    /// </summary>
    private IServiceProvider BuildServiceProvider(string dbName)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = dbName;
                options.DatabaseDirectory = _testDir;
                options.DatabaseCacheSize = PagedMMF.MinimumCacheSize;
            })
            .AddScopedDatabaseEngine(_ => { });

        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        return sp;
    }

    [Test]
    public void LockFile_Created_Deleted()
    {
        var dbName = "lock_create";
        var lockPath = Path.Combine(_testDir, $"{dbName}.lock");

        var sp = BuildServiceProvider(dbName);
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        // Lock file should exist while database is open
        Assert.That(File.Exists(lockPath), Is.True, "Lock file should be created on database open");

        // Verify lock file content
        var json = File.ReadAllText(lockPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.That(root.GetProperty("pid").GetInt32(), Is.EqualTo(Environment.ProcessId));
        Assert.That(root.GetProperty("machineName").GetString(), Is.EqualTo(Environment.MachineName));
        Assert.That(root.TryGetProperty("startedAt", out _), Is.True);

        // Dispose — lock file should be deleted
        (sp as IDisposable)?.Dispose();
        Assert.That(File.Exists(lockPath), Is.False, "Lock file should be deleted on database close");
    }

    [Test]
    public void StaleLock_DeadPid()
    {
        var dbName = "lock_stale";
        var lockPath = Path.Combine(_testDir, $"{dbName}.lock");

        // Build provider first (cleans up), then plant lock file before engine creation
        var sp = BuildServiceProvider(dbName);

        // Find a dead PID
        var deadPid = 99999;
        while (IsProcessAlive(deadPid))
        {
            deadPid++;
        }

        // Plant stale lock file AFTER EnsureFileDeleted but BEFORE engine creation
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new
        {
            pid = deadPid,
            startedAt = DateTimeOffset.UtcNow.AddHours(-1).ToString("o"),
            machineName = Environment.MachineName
        }));

        // Opening database should succeed (stale lock detected and removed)
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        // New lock file should have current PID
        var json = File.ReadAllText(lockPath);
        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("pid").GetInt32(), Is.EqualTo(Environment.ProcessId));

        (sp as IDisposable)?.Dispose();
    }

    [Test]
    public void LiveLock_Throws()
    {
        var dbName = "lock_live";
        var lockPath = Path.Combine(_testDir, $"{dbName}.lock");

        // Build provider first, then plant lock file
        var sp = BuildServiceProvider(dbName);

        File.WriteAllText(lockPath, JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            startedAt = DateTimeOffset.UtcNow.ToString("o"),
            machineName = Environment.MachineName
        }));

        // Opening database should throw DatabaseLockedException
        var ex = Assert.Throws<DatabaseLockedException>(() => sp.GetRequiredService<DatabaseEngine>());
        Assert.That(ex.OwnerPid, Is.EqualTo(Environment.ProcessId));
        Assert.That(ex.OwnerMachine, Is.EqualTo(Environment.MachineName));

        (sp as IDisposable)?.Dispose();

        // Clean up
        if (File.Exists(lockPath))
        {
            File.Delete(lockPath);
        }
    }

    [Test]
    public void CrossMachineLock_Throws()
    {
        var dbName = "lock_remote";
        var lockPath = Path.Combine(_testDir, $"{dbName}.lock");

        // Build provider first, then plant lock file
        var sp = BuildServiceProvider(dbName);

        File.WriteAllText(lockPath, JsonSerializer.Serialize(new
        {
            pid = 1,
            startedAt = DateTimeOffset.UtcNow.ToString("o"),
            machineName = "REMOTE-SERVER-42"
        }));

        // Opening database should throw — can't verify remote PID, treat as live
        var ex = Assert.Throws<DatabaseLockedException>(() => sp.GetRequiredService<DatabaseEngine>());
        Assert.That(ex.OwnerMachine, Is.EqualTo("REMOTE-SERVER-42"));

        (sp as IDisposable)?.Dispose();

        // Clean up
        if (File.Exists(lockPath))
        {
            File.Delete(lockPath);
        }
    }

    [Test]
    [Category("Sensitive")] // file-lock/IO timing — flaky under parallel CPU load; runs in the gate's serial quiet pass
    public void CorruptLockFile_Removed()
    {
        var dbName = "lock_corrupt";
        var lockPath = Path.Combine(_testDir, $"{dbName}.lock");

        // Build provider first, then plant corrupt lock file
        var sp = BuildServiceProvider(dbName);
        File.WriteAllText(lockPath, "this is not valid json {{{");

        // Opening database should succeed (corrupt lock file removed with warning)
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        // New lock file should have current PID
        var json = File.ReadAllText(lockPath);
        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("pid").GetInt32(), Is.EqualTo(Environment.ProcessId));

        (sp as IDisposable)?.Dispose();
    }

    [Test]
    public void FileShare_AllowsRead()
    {
        var dbName = "lock_share_r";
        var dbPath = Path.Combine(_testDir, $"{dbName}.bin");

        var sp = BuildServiceProvider(dbName);
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        // Another reader should be able to open the file for reading
        using var readHandle = File.OpenHandle(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.That(readHandle.IsInvalid, Is.False, "Read-only access should succeed while database is open");

        (sp as IDisposable)?.Dispose();
    }

    [Test]
    // Windows-only: the OS enforces mandatory file-share locking, so a second ReadWrite open of the held .bin throws.
    // POSIX file sharing is advisory (the open succeeds), so this OS-level guard does not exist on Linux/macOS. Typhon's
    // cross-platform single-writer protection is the .lock file (see LiveLock_Throws / CrossMachineLock_Throws); this
    // test covers only the extra Windows mandatory-lock layer.
    [Platform("Win")]
    public void FileShare_PreventsWrite()
    {
        var dbName = "lock_share_w";
        var dbPath = Path.Combine(_testDir, $"{dbName}.bin");

        var sp = BuildServiceProvider(dbName);
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        // Another writer should be blocked
        Assert.Throws<IOException>(() =>
        {
            using var writeHandle = File.OpenHandle(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        });

        (sp as IDisposable)?.Dispose();
    }

    [Test]
    public void EnsureDeleted_RemovesLock()
    {
        var dbName = "lock_ensure";
        var lockPath = Path.Combine(_testDir, $"{dbName}.lock");
        var dbPath = Path.Combine(_testDir, $"{dbName}.bin");

        // Create both files manually
        File.WriteAllText(dbPath, "dummy");
        File.WriteAllText(lockPath, "dummy");

        var options = new PagedMMFOptions
        {
            DatabaseName = dbName,
            DatabaseDirectory = _testDir
        };
        options.EnsureFileDeleted();

        Assert.That(File.Exists(dbPath), Is.False, "Database file should be deleted");
        Assert.That(File.Exists(lockPath), Is.False, "Lock file should be deleted");
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
