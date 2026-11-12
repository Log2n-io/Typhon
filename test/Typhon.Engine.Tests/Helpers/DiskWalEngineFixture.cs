using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// A fixture whose engines run on a disk-backed WAL and a cache each session chooses: an unclean close then leaves WAL segments, so the next open is a
/// crash open, which <c>TestBase</c>'s in-memory WAL never produces.
/// </summary>
internal abstract class DiskWalEngineFixture
{
    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;

    private string DatabaseName => GetType().Name;

    [SetUp]
    public void DiskWalSetUp()
    {
        var root = Path.Combine(TestStorage.Root, GetType().Name);
        _dbDir = Path.Combine(root, DatabaseName, "db");
        _walDir = Path.Combine(root, DatabaseName, "wal");

        // Whatever an aborted run left: WAL segments under a fresh data file would turn its first open into a crash open of someone else's log.
        TryDelete(Path.Combine(root, DatabaseName));
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_walDir);
    }

    [TearDown]
    public void DiskWalTearDown()
    {
        OnTearDown();
        _serviceProvider?.Dispose();
        _serviceProvider = null;

        TryDelete(Directory.GetParent(_dbDir)?.FullName);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (directory != null && Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    /// <summary>Restores the statics a test changed; runs before the provider is disposed.</summary>
    protected virtual void OnTearDown()
    {
    }

    /// <summary>
    /// A provider for the next session, with a <paramref name="cacheSize"/>-byte cache; <paramref name="deleteFile"/> starts a fresh database, its WAL
    /// directory emptied too.
    /// </summary>
    protected ServiceProvider Provider(ulong cacheSize, bool deleteFile = false, Action<DatabaseEngineOptions> configure = null,
        Action<ILoggingBuilder> logging = null)
    {
        _serviceProvider?.Dispose();
        if (deleteFile)
        {
            TryDelete(_walDir);
            Directory.CreateDirectory(_walDir);
        }

        var services = new ServiceCollection();
        services
            .AddLogging(b =>
            {
                b.SetMinimumLevel(LogLevel.Warning);
                logging?.Invoke(b);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = DatabaseName;
                opts.DatabaseDirectory = _dbDir;
                opts.DatabaseCacheSize = cacheSize;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = _walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 16 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
                configure?.Invoke(opts);
            });

        _serviceProvider = services.BuildServiceProvider();
        if (deleteFile)
        {
            _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        }

        return _serviceProvider;
    }
}
