using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Schema.UnitTest.FbbSingle", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct FbbSingle
{
    public int Seq;
}

[Archetype]
internal class FbbSingleArch : Archetype<FbbSingleArch>
{
    public static readonly Comp<FbbSingle> C = Register<FbbSingle>();
}

/// <summary>
/// #886 lead A. A fence-block batch is closed by whichever of two caps is reached first: <c>MaxFenceBatchBytes</c> (256 KB) or the descriptor array. The
/// array used to hold 64 descriptors while a one-entity block costs ~1 KB, so the array closed every batch and the byte cap never did — four WAL claims for
/// 200 dirty clusters where one would do. The claim is the unit this fixture counts: one record per block is emitted whatever the batching, so a record
/// count cannot see it, but each claim is one WAL chunk and the chunk boundary can.
/// </summary>
[TestFixture]
internal sealed class FenceBlockBatchTests
{
    private const int Clusters = 200;
    private const int SlotsPerCluster = 64;

    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;

    private static string CurrentDatabaseName
    {
        get
        {
            var name = TestContext.CurrentContext.Test.Name;
            foreach (var c in new[] { '(', ')', ',', ' ', '"' })
            {
                name = name.Replace(c, '_');
            }

            const int max = 63;
            const string prefix = "Fbb_";
            if (prefix.Length + name.Length > max)
            {
                name = name[^(max - prefix.Length)..];
            }

            return prefix + name;
        }
    }

    [SetUp]
    public void Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(FenceBlockBatchTests));
        _dbDir = Path.Combine(root, CurrentDatabaseName, "db");
        _walDir = Path.Combine(root, CurrentDatabaseName, "wal");
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b =>
            {
                b.AddSimpleConsole();
                b.SetMinimumLevel(LogLevel.Warning);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = CurrentDatabaseName;
                opts.DatabaseDirectory = _dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = _walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;
        var testRoot = Directory.GetParent(_dbDir)?.FullName;
        try
        {
            if (testRoot != null && Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
        catch (IOException)
        {
            // A handle the OS has not released yet; the next run's Setup recreates the directory.
        }
    }

    /// <summary>
    /// One entity written in each of 200 full clusters produces 200 one-entity blocks of well under 256 KB in total, so the byte cap does not close the
    /// batch and the descriptor cap must not either: one claim, not four.
    /// </summary>
    /// <remarks>
    /// Ablation: <c>MaxFenceBatchBlocks = 64</c> makes this read 4.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    public void OneDirtyEntityPerCluster_TwoHundredClusters_IsOneWalClaim()
    {
        const long writeTick = 2;
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<FbbSingle>();
            dbe.InitializeArchetypes();

            var ids = new EntityId[Clusters * SlotsPerCluster];
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    ids[i] = tx.Spawn<FbbSingleArch>(FbbSingleArch.C.Set(new FbbSingle { Seq = i }));
                }

                tx.Commit();
            }

            dbe.WriteTickFence(1);

            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var c = 0; c < Clusters; c++)
                {
                    tx.OpenMut(ids[c * SlotsPerCluster]).Write(FbbSingleArch.C).Seq = -1;
                }

                tx.Commit();
            }

            dbe.WriteTickFence(writeTick);
        }

        var dirtySlots = WalScanner.ScanAll(_walDir).Count(r => r.FromFenceBlock && r.Tsn == writeTick);
        Assert.That(dirtySlots, Is.EqualTo(Clusters), "sanity: exactly one dirty slot per cluster reached the log for the write tick");

        var claims = WalScanner.CountChunksCarryingFenceBlocks(_walDir, writeTick);
        Assert.That(claims, Is.EqualTo(1),
            $"{Clusters} one-entity blocks are far below MaxFenceBatchBytes, so the descriptor array must not be what closes the batch — "
            + "with a 64-descriptor array this tick made four claims");
    }
}
