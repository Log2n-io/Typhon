using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Engine.Tests;

/// <summary>
/// Assembly-level warmup fixture.
/// Runs once before any test fixture (including parallel workers) to JIT-compile
/// all hot code paths. Without this, the first batch of parallel fixtures hits cold JIT
/// and can exceed their [CancelAfter] deadlines.
/// </summary>
[SetUpFixture]
public class AssemblyWarmup
{
    [OneTimeSetUp]
    public void JitWarmup()
    {
        // Install a last-chance handler so unhandled exceptions on background threads
        // dump their stack trace before the process dies.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            var msg = $"\n=== UNHANDLED EXCEPTION (IsTerminating={args.IsTerminating}) ===\n{ex}\n===\n";
            Console.Error.Write(msg);
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "typhon_test_crash.log");
                File.AppendAllText(path, $"[{DateTime.UtcNow:O}] {msg}");
                Console.Error.WriteLine($"Crash log written to: {path}");
            }
            catch { /* best effort */ }
        };


        // Pre-grow the thread pool so concurrent test fixtures don't starve waiting for
        // on-demand thread creation (default growth: 1 thread per 500ms under starvation).
        ThreadPool.SetMinThreads(96, 32);

        // Phase 1: Full engine stack with WAL — DI, PagedMMF, DatabaseEngine, transactions,
        // components, BTree, WAL writer, checkpoint, durability pipeline
        WarmupFullStackWithWal();

        // Phase 2: Concurrency primitives — AccessControl, EpochManager, latches
        WarmupConcurrencyPrimitives();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WarmupFullStackWithWal()
    {
        const string dbName = "jit_warmup_db";
        var walDir = Path.Combine(Path.GetTempPath(), $"typhon_warmup_wal_{Guid.NewGuid():N}");
        var dbDir = Path.Combine(Path.GetTempPath(), $"typhon_warmup_db_{Guid.NewGuid():N}");

        Directory.CreateDirectory(walDir);
        Directory.CreateDirectory(dbDir);

        try
        {
            var sc = new ServiceCollection();
            sc.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
                .AddResourceRegistry()
                .AddMemoryAllocator()
                .AddEpochManager()
                .AddHighResolutionSharedTimer()
                .AddDeadlineWatchdog()
                .AddSingleton<IWalFileIO>(new WalFileIO())
                .AddScopedManagedPagedMemoryMappedFile(options =>
                {
                    options.DatabaseName = dbName;
                    options.DatabaseDirectory = dbDir;
                    options.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize;
                    options.PagesDebugPattern = false;
                })
                .AddScopedDatabaseEngine(opts =>
                {
                    opts.Wal = new WalWriterOptions
                    {
                        WalDirectory = walDir,
                        GroupCommitIntervalMs = 5,
                        UseFUA = false,
                        SegmentSize = 4 * 1024 * 1024,
                        PreAllocateSegments = 1,
                    };
                });

            var sp = sc.BuildServiceProvider();
            sp.EnsureFileDeleted<ManagedPagedMMFOptions>();

            try
            {
                using var scope = sp.CreateScope();
                var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

                // Register all common test component types to JIT the reflection/schema code paths
                dbe.RegisterComponentFromAccessor<CompA>();
                dbe.RegisterComponentFromAccessor<CompB>();
                dbe.RegisterComponentFromAccessor<CompC>();
                dbe.RegisterComponentFromAccessor<CompD>();
                dbe.RegisterComponentFromAccessor<CompF>();

                // Initialize ECS archetypes — connects archetype slots to ComponentTables
                dbe.InitializeArchetypes();

                // Spawn, read, update, destroy — exercises transaction lifecycle, MVCC, page cache,
                // BTree indexes, WAL writer, dirty page tracking, checkpoint path
                EntityId warmupId1;
                {
                    using var t = dbe.CreateQuickTransaction();
                    var a = new CompA(1, 2.0f, 3.0);
                    warmupId1 = t.Spawn<CompAArch>(CompAArch.A.Set(in a));
                    var a2 = new CompA(2, 3.0f, 4.0);
                    var id2 = t.Spawn<CompAArch>(CompAArch.A.Set(in a2));

                    // CompD has indexed fields — triggers BTree insert paths
                    var d = new CompD(1.0f, 100, 2.0);
                    t.Spawn<CompDArch>(CompDArch.D.Set(in d));

                    t.Open(warmupId1).Read(CompAArch.A);
                    ref var wa = ref t.OpenMut(warmupId1).Write(CompAArch.A);
                    wa.A = 999;
                    t.Destroy(id2);
                    t.Commit();
                }

                // Second transaction — exercises MVCC snapshot isolation, revision chain traversal
                {
                    using var t = dbe.CreateQuickTransaction();
                    var a = new CompA(10, 20.0f, 30.0);
                    t.Spawn<CompAArch>(CompAArch.A.Set(in a));
                    t.Open(warmupId1).Read(CompAArch.A);
                    t.Commit();
                }

                dbe.Dispose();
            }
            finally
            {
                sp.Dispose();
            }
        }
        finally
        {
            // Clean up warmup files
            try { if (Directory.Exists(walDir)) Directory.Delete(walDir, true); } catch { }
            try { if (Directory.Exists(dbDir)) Directory.Delete(dbDir, true); } catch { }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WarmupConcurrencyPrimitives()
    {
        // AccessControl — JIT the Enter/Exit paths for shared and exclusive access
        var ac = new AccessControl();
        ac.EnterSharedAccess(ref WaitContext.Null);
        ac.ExitSharedAccess();
        ac.EnterExclusiveAccess(ref WaitContext.Null);
        ac.ExitExclusiveAccess();

        // AccessControlSmall — same pattern
        var acs = new AccessControlSmall();
        acs.EnterSharedAccess(ref WaitContext.Null);
        acs.ExitSharedAccess();
        acs.EnterExclusiveAccess(ref WaitContext.Null);
        acs.ExitExclusiveAccess();

        // ResourceAccessControl — JIT the Enter/Exit paths
        var rac = new ResourceAccessControl();
        rac.EnterAccessing(ref WaitContext.Null);
        rac.ExitAccessing();
    }

}
