using Microsoft.Extensions.DependencyInjection;

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
}
