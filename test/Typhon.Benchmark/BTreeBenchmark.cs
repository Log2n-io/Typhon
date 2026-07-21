using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Diagnostics;

namespace Typhon.Benchmark;

[SimpleJob(warmupCount: 3, iterationCount: 15)]
[JsonExporterAttribute.Full]
[BenchmarkCategory("BTree")]
public class BTreeBenchmark
{
    private string CurrentDatabaseName => $"BTreeBenchmark_database";
    private ServiceCollection _serviceCollection;
    private ServiceProvider _serviceProvider;
    private ManagedPagedMMF _pmmf;
    private EpochManager _epochManager;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var dcs = 200 * 1024;
        dcs *= PagedMMF.PageSize;

        _serviceCollection = new ServiceCollection();
        _serviceCollection
            .AddLogging(builder =>
            {
                builder.AddSimpleConsole();
                builder.SetMinimumLevel(LogLevel.Critical);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)dcs;
                options.PagesDebugPattern = false;
            });
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        _epochManager = _serviceProvider.GetRequiredService<EpochManager>();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _epochManager?.Dispose();
        _epochManager = null;
        _pmmf?.Dispose();
        _pmmf = null;
        _serviceProvider?.Dispose();
        _serviceProvider = null;
    }

    public void Run()
    {
        CheckMultipleTreeBigAmount();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        // Per-iteration cleanup handles _epochManager, _pmmf, _serviceProvider
    }

    [Benchmark]
    unsafe public void CheckMultipleTreeBigAmount()
    {
        const int itemCount = 400;

        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 300, sizeof(Index32Chunk));
        var depth = _epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntMultipleBTree<PersistentStore>(segment);

            var chunkCapacity = segment.ChunkCapacity;
            var freeChunkCount = segment.FreeChunkCount;

            var elemIdDic = new Dictionary<int, List<int>>(itemCount);

            var sw = new Stopwatch();

            int gc = 0;
            for (int i = 0; i < itemCount; i++)
            {
                var idList = new List<int>(i);
                elemIdDic.Add(i, idList);

                for (int j = 0; j < i; j++, gc++)
                {
                    sw.Start();
                    var item = tree.Add(i, 10 + j, ref accessor);
                    sw.Stop();
                    idList.Add(item);
                }
            }

            // Parse every element buffers
            for (int i = 1; i < itemCount; i++)
            {
                var c = 0;
                using var a = tree.TryGetMultiple(i, ref accessor);
                do
                {
                    c += a.ReadOnlyElements.Length;
                } while (a.NextChunk());
            }

            // Now this is the nasty part, we delete half of the chunk of the buffer to create fragmentation that
            //  will be solved during the next parsing...
            for (int i = 0; i < itemCount; i++)
            {
                var idList = elemIdDic[i];

                for (int j = 0; j < i; j++, gc++)
                {
                    var elemId = idList[j];
                    if (((elemId + i) & 1) != 0)                // Use 'i'  to alternate deleting either odd or even chunks
                    {
                        tree.RemoveValue(i, elemId, 10 + j, ref accessor);
                    }
                }
            }

            // Parse every element buffers
            for (int i = 1; i < itemCount; i++)
            {
                var c = 0;
                using var a = tree.TryGetMultiple(i, ref accessor);
                if (a.IsValid == false) continue;

                //Assert.That(a.IsValid, Is.True);
                do
                {
                    c += a.ReadOnlyElements.Length;
                } while (a.NextChunk());

                //Assert.That(c, Is.EqualTo(i));
            }

            // Delete the rest
            for (int i = 0; i < itemCount; i++)
            {
                var idList = elemIdDic[i];

                for (int j = 0; j < i; j++, gc++)
                {
                    var elemId = idList[j];
                    if (((elemId + i) & 1) == 0)                // Use 'i'  to alternate deleting either odd or even chunks
                    {
                        tree.RemoveValue(i, elemId, 10 + j, ref accessor);
                    }
                }
            }

            accessor.Dispose();
        }
        finally
        {
            _epochManager.ExitScope(depth);
        }
    }
}
