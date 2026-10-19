using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Typhon.Benchmark;

/// <summary>
/// The data-file write path with and without online backup's change map and write gate (#1412, plan §9): the checkpoint's per-page write
/// (<c>WritePagesForCheckpoint</c>, 1024 resident pages, one or four writer threads as the checkpoint's waves run) and a structural <c>SavePages</c>
/// batch (1, 10, 100 pages). Test mode: real <c>pwrite</c>, no fsync, so the gate's cost is measured against the write it sits beside.
/// </summary>
/// <remarks>
/// Same binary, A/B by environment (the two ops escape hatches): run it with <c>TYPHON_DATAFILE_TRACKING=0</c> and <c>TYPHON_DATAFILE_GATE=0</c>
/// set (Off) and unset (On), in interleaved pairs; BenchmarkDotNet's child process inherits them, and both switches are <c>static readonly</c>,
/// folded by the JIT in Tier 1. Gate: On within 1 % of Off per page.
/// </remarks>
[BenchmarkCategory("Storage", "Backup")]
public abstract class DataFileWritePathBenchmarks
{
    private const int ResidentPages = 1024;

    private ServiceProvider _serviceProvider;
    private EpochManager _epochManager;
    private int _epochDepth;
    private string _databaseName;

    private protected ManagedPagedMMF Mmf;
    private protected StagingBufferPool Staging;
    private protected int[] MemPages;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _databaseName = $"DataFileWriteBench_{Environment.ProcessId}";
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = _databaseName;
              options.DatabaseCacheSize = (ulong)(4 * ResidentPages * PagedMMF.PageSize);
              options.PagesDebugPattern = false;
              options.TestMode = true;
          });

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        Mmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        _epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var allocator = _serviceProvider.GetRequiredService<IMemoryAllocator>();
        Staging = new StagingBufferPool(allocator, _serviceProvider.GetRequiredService<IResourceRegistry>().Allocation);

        var cs = Mmf.CreateChangeSet();
        var segment = Mmf.AllocateSegment(PageBlockType.None, ResidentPages, cs);
        cs.SaveChanges();

        _epochDepth = _epochManager.EnterScope();
        var pages = segment.Pages.ToArray();
        MemPages = new int[pages.Length];
        for (var i = 0; i < pages.Length; i++)
        {
            Mmf.RequestPageEpoch(pages[i], _epochManager.GlobalEpoch, out MemPages[i]);
        }

        AfterSetup();
    }

    protected virtual void AfterSetup()
    {
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _epochManager.ExitScope(_epochDepth);
        Staging?.Dispose();
        Mmf?.Dispose();
        _serviceProvider?.Dispose();
    }

    /// <summary>Per page: the checkpoint's write of 1024 resident pages, split over <see cref="Writers"/> threads as its waves are.</summary>
    public class CheckpointWrite : DataFileWritePathBenchmarks
    {
        private int[][] _waves;

        [Params(1, 4)]
        public int Writers { get; set; }

        protected override void AfterSetup()
            => _waves = Enumerable.Range(0, Writers).Select(w => MemPages.Where((_, i) => i % Writers == w).ToArray()).ToArray();

        [Benchmark(OperationsPerInvoke = ResidentPages)]
        public void WritePages()
        {
            if (Writers == 1)
            {
                Mmf.WritePagesForCheckpoint(_waves[0], Staging, out _);
                return;
            }

            Parallel.For(0, Writers, w => Mmf.WritePagesForCheckpoint(_waves[w], Staging, out _));
        }
    }

    /// <summary>A structural save of <see cref="SaveBatch"/> pages: one async batch, its gate token, its map updates.</summary>
    public class SaveBatchOf : DataFileWritePathBenchmarks
    {
        [Params(1, 10, 100)]
        public int SaveBatch { get; set; }

        [Benchmark]
        public void SavePages() => Mmf.SavePages(MemPages[..SaveBatch]).Wait();
    }
}
