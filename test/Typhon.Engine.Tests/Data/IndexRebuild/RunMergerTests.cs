using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The k-way merge of the sorted index rebuild (#1588, design §D3): any number of runs, from the file or from memory, merges into the reference order, equal
/// keys across runs by location, and a forced multi-pass merge equals a single pass.
/// </summary>
[TestFixture]
internal sealed unsafe class RunMergerTests
{
    private string _dir;
    private readonly List<IntPtr> _native = [];

    [SetUp]
    public void SetUp() => _dir = Path.Combine(TestStorage.ForFixture(nameof(RunMergerTests)), TestContext.CurrentContext.Test.Name);

    [TearDown]
    public void TearDown()
    {
        foreach (var p in _native)
        {
            NativeMemory.Free((void*)p);
        }

        _native.Clear();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(64)]
    public void FileRuns_MergeIntoTheReferenceOrder(int k) => Check(k, entriesPerRunMax: 3_000, inMemoryEvery: 0, maxFanIn: 0);

    [Test]
    public void MemoryAndFileRuns_Mixed() => Check(9, entriesPerRunMax: 4_000, inMemoryEvery: 2, maxFanIn: 0);

    [Test]
    public void AForcedMultiPassMerge_EqualsTheSinglePass() => Check(1_000, entriesPerRunMax: 60, inMemoryEvery: 0, maxFanIn: 4);

    [Test]
    public void AThousandRuns_MergeDownThenOnce() => Check(1_000, entriesPerRunMax: 200, inMemoryEvery: 0, maxFanIn: 32);

    [Test]
    public void EmptyRuns_AreSkipped()
    {
        using var file = RebuildTempFile.Create(_dir, "e.tmp");
        var runs = new[] { new RunDescriptor(0, 0, 1, 0), WriteRun(file, [new StagedEntry<long>(5, 1)], 2), new RunDescriptor(0, 0, 3, 0) };
        var merged = Merge(file, runs.ToList(), 0);
        Assert.That(merged.Select(e => e.Location), Is.EqualTo(new[] { 1 }));
    }

    private void Check(int k, int entriesPerRunMax, int inMemoryEvery, int maxFanIn)
    {
        var random = new Random(k * 31 + 1588);
        using var file = RebuildTempFile.Create(_dir, $"m-{k}.tmp");
        var all = new List<StagedEntry<long>>();
        var runs = new List<RunDescriptor>();
        var sizes = Enumerable.Range(0, k).Select(_ => random.Next(1, entriesPerRunMax + 1)).ToArray();

        // Locations unique, shuffled across the runs: a run's index says nothing of its locations, so only the location tie-break orders equal keys.
        var locations = Enumerable.Range(0, sizes.Sum()).Select(i => i * 2).OrderBy(_ => random.Next()).ToArray();
        var taken = 0;
        for (var r = 0; r < k; r++)
        {
            // Few distinct keys, so equal keys meet across runs.
            var n = sizes[r];
            var mine = locations.AsSpan(taken, n).ToArray();
            Array.Sort(mine);   // the stage appends each partition in ascending location order, which the stable sort relies on
            taken += n;
            var run = new StagedEntry<long>[n];
            for (var i = 0; i < n; i++)
            {
                run[i] = new StagedEntry<long>(random.Next(-20, 20), mine[i]);
            }

            StagedOrder<long>.Sort(run, new StagedEntry<long>[n], new int[RadixSort.Buckets]);
            all.AddRange(run);
            runs.Add(inMemoryEvery > 0 && r % inMemoryEvery == 0 ? InMemory(run, r) : WriteRun(file, run, r));
        }

        var expected = all.OrderBy(e => e.Key).ThenBy(e => e.Location).Select(e => e.Location).ToArray();
        var merged = Merge(file, runs, maxFanIn);
        Assert.That(merged.Select(e => e.Location).ToArray(), Is.EqualTo(expected));
    }

    private List<StagedEntry<long>> Merge(RebuildTempFile file, List<RunDescriptor> runs, int maxFanIn)
    {
        const int block = RebuildTempFile.BlockSize;
        if (maxFanIn > 0)
        {
            var nextRunId = 100_000;
            var passes = RunMerger<long>.MergeDown(file, runs, maxFanIn, Native(maxFanIn * block), maxFanIn * block, Native(2 * block), 2 * block,
                new byte[2 * block], ref nextRunId);
            Assert.That(passes, Is.GreaterThan(0), "premise: the merge needed extra passes");
            Assert.That(runs, Has.Count.LessThanOrEqualTo(maxFanIn));
        }

        var fileRuns = runs.Count(r => !r.InMemory);
        var readBytes = Math.Max(1, fileRuns) * 2L * block;
        var merger = new RunMerger<long>(file, runs.ToArray(), Native((int)readBytes), readBytes, 2 * block);
        var got = new List<StagedEntry<long>>();
        while (merger.Next(out var e))
        {
            got.Add(e);
        }

        return got;
    }

    private RunDescriptor WriteRun(RebuildTempFile file, StagedEntry<long>[] run, int owner)
    {
        fixed (StagedEntry<long>* p = run)
        {
            return file.Write(new ReadOnlySpan<byte>(p, run.Length * 12), 12, run.Length, RebuildBlockKind.Run, owner, new byte[RebuildTempFile.BlockSize]);
        }
    }

    private RunDescriptor InMemory(StagedEntry<long>[] run, int owner)
    {
        var p = Native(run.Length * 12);
        run.AsSpan().CopyTo(new Span<StagedEntry<long>>(p, run.Length));
        return new RunDescriptor(p, run.Length, owner);
    }

    private byte* Native(int bytes)
    {
        var p = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, bytes));
        _native.Add((IntPtr)p);
        return p;
    }
}
