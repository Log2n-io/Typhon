using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>Shared harness of the capture fixtures: an engine on a disk WAL whose clock-driven checkpoint stays out of the way of staged scenarios.</summary>
abstract class BackupTestBase<T> : TestBase<T> where T : BackupTestBase<T>
{
    protected static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = 600_000;   // staged scenarios force their checkpoints; none may run on its own

        // A WAL directory per test, not per fixture: these fixtures write real segments, and a fixture-wide directory hands one test's leftovers to the
        // next test's fresh database (the round trip broke the moment a test that ran before it left segments behind).
        o.Wal.WalDirectory = Path.Combine(Path.GetDirectoryName(o.Wal.WalDirectory), CurrentDatabaseName + ".wal");
    }

    // TestBase removes the bundle only; this directory sits beside it, so a repeated or retried test would otherwise open a fresh file over old segments.
    private static string PerTestWalDirectory => Path.Combine(TestStorage.Root, typeof(T).Name, CurrentDatabaseName + ".wal");

    public override void Setup()
    {
        DeletePerTestWal();
        base.Setup();
    }

    public override void TearDown()
    {
        base.TearDown();
        DeletePerTestWal();
    }

    private static void DeletePerTestWal()
    {
        if (Directory.Exists(PerTestWalDirectory))
        {
            Directory.Delete(PerTestWalDirectory, true);
        }
    }

    /// <summary>
    /// A provider like the fixture's, over another directory, whose engine reads the WAL from the bundle's own <c>wal/</c>: how a restored bundle is
    /// opened (DC-4: a restore marker beside any other WAL directory refuses the open).
    /// </summary>
    protected ServiceProvider ProviderFor(string databaseDirectory)
    {
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = CurrentDatabaseName;
                o.DatabaseDirectory = databaseDirectory;
                o.DatabaseCacheSize = 8UL * 1024 * 1024;
                o.TestMode = true;
                o.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine(o =>
            {
                ConfigureEngineOptions(o);
                o.Wal.WalDirectory = null;
            });
        services.AddScoped<IWalFileIO>(_ => new WalFileIO());
        return services.BuildServiceProvider();
    }

    protected static DatabaseEngine Open(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Spawns <paramref name="count"/> entities durably; returns their ids.</summary>
    protected static List<EntityId> Spawn(DatabaseEngine dbe, int count, int seed = 0)
    {
        var ids = new List<EntityId>(count);
        using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        for (var i = 0; i < count; i++)
        {
            ids.Add(t.Spawn<CompAArch>(CompAArch.A.Set(new CompA(seed + i))));
        }

        t.Commit();
        return ids;
    }

    /// <summary>Rewrites every entity of <paramref name="ids"/>: their pages go dirty in the cache, unwritten until a checkpoint.</summary>
    protected static void Update(DatabaseEngine dbe, IEnumerable<EntityId> ids, int value, DurabilityMode durability = DurabilityMode.Immediate)
    {
        using var t = dbe.CreateQuickTransaction(durability);
        foreach (var id in ids)
        {
            var entity = t.OpenMut(id);
            entity.Set(CompAArch.A, new CompA(value));
        }

        t.Commit();
    }

    /// <summary>Runs one capture into <paramref name="store"/> and waits for it.</summary>
    protected static BackupPointInfo Capture(DatabaseEngine dbe, FakeBackupStore store, BackupKind kind = BackupKind.Incremental)
    {
        var task = dbe.BackupCoordinator.CaptureAsync(store, kind, CancellationToken.None);
        Assert.That(task.Wait(Budget), Is.True, "the capture did not finish");
        return task.Result;
    }

    /// <summary>Starts a capture without waiting.</summary>
    protected static Task<BackupPointInfo> StartCapture(DatabaseEngine dbe, FakeBackupStore store, BackupKind kind = BackupKind.Incremental,
        CancellationToken ct = default) => dbe.BackupCoordinator.CaptureAsync(store, kind, ct);

    /// <summary>The data file's bytes now, read beside the engine.</summary>
    protected static byte[] ReadDataFile(DatabaseEngine dbe)
    {
        using var fs = new FileStream(dbe.MMF.DataFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>The file pages dirty in the cache now: the ones the next checkpoint writes.</summary>
    protected static SortedSet<int> DirtyFilePages(DatabaseEngine dbe)
    {
        var pages = new SortedSet<int>();
        foreach (var mem in dbe.MMF.CollectDirtyMemPageIndices())
        {
            pages.Add(dbe.MMF.GetFilePageIndex(mem));
        }

        return pages;
    }

    /// <summary>Asserts every page the point holds is the data file's page at the freeze (BKP-02 / BKP-03), naming the first that is not.</summary>
    protected static void AssertImageIsTheFileAtTheFreeze(CapturingSink point, byte[] fileAtFreeze, string marker)
    {
        var differing = new List<int>();
        foreach (var (page, image) in point.Pages)
        {
            var offset = page * PagedMMF.PageSize;
            var expected = offset + PagedMMF.PageSize <= fileAtFreeze.Length
                ? fileAtFreeze.AsSpan(offset, PagedMMF.PageSize)
                : new byte[PagedMMF.PageSize];
            if (!expected.SequenceEqual(image))
            {
                differing.Add(page);
            }
        }

        Assert.That(differing, Is.Empty, $"{marker}: pages of the point differ from the data file at the freeze: {string.Join(", ", differing)}");
    }
}
