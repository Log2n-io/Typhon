using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Typhon.Benchmark;

/// <summary>
/// Shared engine setup for benchmarks. With the no-WAL engine mode removed, benchmarks run the real WAL + checkpoint pipeline, but against an in-memory WAL
/// backend with FUA disabled and the checkpoint idle timer effectively off — so there is zero disk I/O and the background checkpoint thread stays dormant for
/// the short benchmark iterations, keeping interference with CPU measurements minimal. Caches in the benchmarks are large, so back-pressure never fires.
/// </summary>
internal static class BenchmarkEngine
{
    /// <summary>Registers an in-memory WAL file-IO backend and a scoped engine configured for low-interference benchmarking.</summary>
    public static IServiceCollection AddInMemoryWalEngine(this IServiceCollection sc) =>
        sc.AddSingleton<IWalFileIO>(_ => new InMemoryWalFileIO())
          .AddScopedDatabaseEngine(o =>
          {
              o.Wal = new WalWriterOptions { UseFUA = false };
              o.Resources.CheckpointIntervalMs = int.MaxValue;
          });

    /// <summary>
    /// Builds the standard ECS benchmark engine: the full DI stack (allocator, epoch, hi-res timer, deadline watchdog, paged MMF)
    /// on top of <see cref="AddInMemoryWalEngine"/>, with the backing file pre-deleted. Every ECS benchmark class should use this
    /// so the whole suite measures ONE engine configuration — several classes previously hand-rolled the same stack with the
    /// default (disk-backed) WAL instead, which silently put two different configurations into the same tracked trend.
    /// </summary>
    /// <param name="cachePages">Page-cache size expressed in pages (e.g. <c>200 * 1024</c> for a 200K-page cache).</param>
    /// <param name="name">Database name stem; the current process id is appended so parallel benchmark processes never collide.</param>
    public static ServiceProvider BuildEcsEngine(int cachePages, string name)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = $"{name}_{Environment.ProcessId}";
              o.DatabaseCacheSize = (ulong)((long)cachePages * PagedMMF.PageSize);
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        return sp;
    }
}
