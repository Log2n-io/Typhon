using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

/// <summary>
/// Profiling harness for Write performance across storage modes.
/// Runs Write_Versioned and Write_SingleVersion in tight loops for dotTrace tracing.
/// </summary>
static class WriteProfile
{
    // Dedicated components to avoid sharing ComponentTable state with other profiles
    [Component("Typhon.Benchmark.WP.Versioned", 1, StorageMode = StorageMode.Versioned)]
    [StructLayout(LayoutKind.Sequential)]
    public struct WpVersioned
    {
        [Field] public int Value;
        [Field] public long Timestamp;
    }

    [Component("Typhon.Benchmark.WP.SingleVersion", 1, StorageMode = StorageMode.SingleVersion)]
    [StructLayout(LayoutKind.Sequential)]
    public struct WpSingleVersion
    {
        [Field] public int Value;
        [Field] public long Timestamp;
    }

    [Archetype]
    class WpVersionedArch : Archetype<WpVersionedArch>
    {
        public static readonly Comp<WpVersioned> Data = Register<WpVersioned>();
    }

    [Archetype]
    class WpSvArch : Archetype<WpSvArch>
    {
        public static readonly Comp<WpSingleVersion> Data = Register<WpSingleVersion>();
    }

    private const int EntityCount = 1000;
    private const int WriteCount = 100;
    private const int Iterations = 20_000;

    public static void Run(bool versioned)
    {
        var mode = versioned ? "Versioned" : "SingleVersion";
        Console.WriteLine($"WriteProfile [{mode}]: Setting up engine...");

        var dcs = 200 * 1024 * PagedMMF.PageSize;
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = $"WriteProfile_{Environment.ProcessId}";
              options.DatabaseCacheSize = (ulong)dcs;
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponentFromAccessor<WpVersioned>();
        dbe.RegisterComponentFromAccessor<WpSingleVersion>();
        dbe.InitializeArchetypes();

        // Pre-grow EntityMap. Committed in chunks: a single commit's WAL frame must fit the
        // commit buffer (WalRingBufferSizeBytes/2 = 32 MiB); 200K spawns in one commit is ~18 MB
        // and throws WalClaimTooLargeException. 20K keeps each commit well under.
        var pg = new EntityId[200_000];
        const int pgChunk = 20_000;
        for (int start = 0; start < pg.Length; start += pgChunk)
        {
            int end = Math.Min(start + pgChunk, pg.Length);
            using var gt = dbe.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var c = new WpVersioned { Value = i, Timestamp = 12345 };
                pg[i] = gt.Spawn<WpVersionedArch>(WpVersionedArch.Data.Set(in c));
            }
            gt.Commit();
        }
        for (int start = 0; start < pg.Length; start += pgChunk)
        {
            int end = Math.Min(start + pgChunk, pg.Length);
            using var dt = dbe.CreateQuickTransaction();
            for (int i = start; i < end; i++) dt.Destroy(pg[i]);
            dt.Commit();
        }
        dbe.FlushDeferredCleanups();

        // Pre-populate entities
        var vIds = new EntityId[EntityCount];
        var svIds = new EntityId[EntityCount];
        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < EntityCount; i++)
            {
                var v = new WpVersioned { Value = i, Timestamp = 12345 };
                vIds[i] = t.Spawn<WpVersionedArch>(WpVersionedArch.Data.Set(in v));
                var sv = new WpSingleVersion { Value = i, Timestamp = 12345 };
                svIds[i] = t.Spawn<WpSvArch>(WpSvArch.Data.Set(in sv));
            }
            t.Commit();
        }

        // Warmup
        Console.WriteLine($"WriteProfile [{mode}]: Warmup...");
        var ids = versioned ? vIds : svIds;
        if (versioned)
        {
            for (int w = 0; w < 500; w++)
            {
                using var t = dbe.CreateQuickTransaction();
                for (int i = 0; i < WriteCount; i++)
                {
                    var entity = t.OpenMut(vIds[i]);
                    var data = entity.Read(WpVersionedArch.Data);
                    data.Value = i;
                    entity.Set(WpVersionedArch.Data, data);
                }
                t.Commit();
            }
            dbe.FlushDeferredCleanups();
        }
        else
        {
            for (int w = 0; w < 500; w++)
            {
                using var t = dbe.CreateQuickTransaction();
                for (int i = 0; i < WriteCount; i++)
                {
                    var entity = t.OpenMut(svIds[i]);
                    var data = entity.Read(WpSvArch.Data);
                    data.Value = i;
                    entity.Set(WpSvArch.Data, data);
                }
                t.Commit();
            }
        }

        Console.WriteLine($"WriteProfile [{mode}]: Profiling ({Iterations} iters x {WriteCount} writes)...");
        var sw = Stopwatch.StartNew();
        if (versioned)
        {
            for (int iter = 0; iter < Iterations; iter++)
            {
                using var t = dbe.CreateQuickTransaction();
                for (int i = 0; i < WriteCount; i++)
                {
                    var opened = t.OpenMut(vIds[i]);
                    var data = opened.Read(WpVersionedArch.Data);
                    data.Value = iter + i;
                    opened.Set(WpVersionedArch.Data, data);
                }
                t.Commit();
            }
        }
        else
        {
            for (int iter = 0; iter < Iterations; iter++)
            {
                using var t = dbe.CreateQuickTransaction();
                for (int i = 0; i < WriteCount; i++)
                {
                    var entity2 = t.OpenMut(svIds[i]);
                    var data2 = entity2.Read(WpSvArch.Data);
                    data2.Value = iter + i;
                    entity2.Set(WpSvArch.Data, data2);
                }
                t.Commit();
            }
        }
        sw.Stop();
        Console.WriteLine($"WriteProfile [{mode}]: Done. {sw.ElapsedMilliseconds}ms total, {sw.Elapsed.TotalMicroseconds / Iterations:F1}us/iter, {sw.Elapsed.TotalNanoseconds / (Iterations * WriteCount):F0}ns/op");

        if (versioned)
        {
            dbe.FlushDeferredCleanups();
        }
        Console.WriteLine($"WriteProfile [{mode}]: Complete.");
        dbe.Dispose();
        sp.Dispose();
    }
}
