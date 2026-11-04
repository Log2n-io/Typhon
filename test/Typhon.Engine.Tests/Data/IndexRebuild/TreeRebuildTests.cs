using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The ordered build of one tree from merged runs (#1588): a unique tree keeps, of every duplicated key, the entry at the LOWEST location (RB-07),
/// whatever run each duplicate came from and however many merge passes it took; the rest are counted.
/// </summary>
[TestFixture]
internal sealed unsafe class TreeRebuildTests
{
    private IServiceProvider _serviceProvider;
    private string _dir;
    private readonly List<IntPtr> _native = [];

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(TestStorage.ForFixture(nameof(TreeRebuildTests)), TestContext.CurrentContext.Test.Name.Replace("(", "_").Replace(")", ""));
        var services = new ServiceCollection();
        services
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                var raw = $"treerebuild_{TestContext.CurrentContext.Test.Name}".Replace("(", "_").Replace(")", "").Replace(",", "_");
                options.DatabaseName = raw[..Math.Min(63, raw.Length)];
                options.DatabaseCacheSize = (ulong)(4_096 * PagedMMF.PageSize);
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var p in _native)
        {
            NativeMemory.Free((void*)p);
        }

        _native.Clear();
        (_serviceProvider as IDisposable)?.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    [VerifiesRule("RB-07")]
    public void DuplicatesAcrossRuns_KeepTheLowestLocation(bool mergeDown)
    {
        const int runCount = 6;
        const int keys = 300;
        var random = new Random(1588);

        // Every key held by three to five entities, their locations shuffled across the runs, so the first run to hold a key rarely holds its lowest.
        var entries = new List<StagedEntry<long>>();
        var location = 0;
        for (var k = 0; k < keys; k++)
        {
            var holders = random.Next(3, 6);
            for (var h = 0; h < holders; h++)
            {
                entries.Add(new StagedEntry<long>(k * 11L - 1_000, location));
                location += 1 + random.Next(3);
            }
        }

        var runs = entries.GroupBy(_ => random.Next(runCount)).Select(g =>
        {
            var run = g.OrderBy(e => e.Location).ToArray();   // the stage appends each partition in location order
            StagedOrder<long>.Sort(run, new StagedEntry<long>[run.Length], new int[RadixSort.Buckets]);
            return run;
        }).ToList();

        using var mmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var depth = epochManager.EnterScope();
        try
        {
            var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index64Chunk));
            var tree = new LongSingleBTree<PersistentStore>(segment);
            using var file = RebuildTempFile.Create(_dir, "ix-1.tmp");
            var staging = new byte[RebuildTempFile.BlockSize];
            var descriptors = new List<RunDescriptor>();
            for (var r = 0; r < runs.Count; r++)
            {
                fixed (StagedEntry<long>* p = runs[r])
                {
                    descriptors.Add(file.Write(new ReadOnlySpan<byte>(p, runs[r].Length * 12), 12, runs[r].Length, RebuildBlockKind.Run, r, staging));
                }
            }

            var readBytes = (mergeDown ? 2 : runCount) * RebuildTempFile.BlockSize;
            var input = new TreeBuildInput
            {
                File = file,
                Runs = descriptors,
                ReadBuffer = Native(readBytes),
                ReadBufferBytes = readBytes,
                MaxRunBufferBytes = RebuildTempFile.BlockSize,
                WriteBuffer = Native(RebuildTempFile.BlockSize),
                WriteBufferBytes = RebuildTempFile.BlockSize,
                Staging = Native(RebuildTempFile.BlockSize),
                StagingBytes = RebuildTempFile.BlockSize,
                NextRunId = 100,
            };

            var result = tree.CreateRebuild().Build(input);
            Assert.That(result.ExtraMergePasses > 0, Is.EqualTo(mergeDown), "premise: the merge-down ran only when asked");

            var lowest = entries.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Min(e => e.Location));
            var accessor = segment.CreateChunkAccessor();
            try
            {
                Assert.Multiple(() =>
                {
                    Assert.That(result.UniqueConflicts, Is.EqualTo(entries.Count - keys), "every duplicate but one per key is counted");
                    Assert.That(tree.EntryCount, Is.EqualTo(keys));
                    foreach (var (key, min) in lowest)
                    {
                        var k = key;
                        var found = tree.TryGet(&k, ref accessor);
                        Assert.That(found.IsSuccess, Is.True, $"key {key}");
                        Assert.That(found.Value, Is.EqualTo(min), $"key {key} keeps its lowest location");
                    }
                });
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    private byte* Native(int bytes)
    {
        var p = (byte*)NativeMemory.Alloc((nuint)bytes);
        _native.Add((IntPtr)p);
        return p;
    }
}
