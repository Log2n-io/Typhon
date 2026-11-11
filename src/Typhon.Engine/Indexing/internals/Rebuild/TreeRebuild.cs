using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>Receives the element id each multi-value entry got, for the cluster tail write-back (#1588, design §D7).</summary>
internal interface IElementIdSink
{
    void Add(int multiFieldIndex, int location, int elementId);
}

/// <summary>What one tree's build reads: its runs and the buffers the merge may use.</summary>
internal sealed unsafe class TreeBuildInput
{
    public RebuildTempFile File;
    public List<RunDescriptor> Runs;
    public byte* ReadBuffer;
    public long ReadBufferBytes;
    public int MaxRunBufferBytes;
    public byte* WriteBuffer;
    public int WriteBufferBytes;
    public byte* Staging;
    public int StagingBytes;
    public ChangeSet ChangeSet;
    public Action BatchPoint;
    public IElementIdSink Sink;
    public int MultiFieldIndex;
    public int NextRunId;

    /// <summary>The leaf fill of the bottom-up build, 50..100 (design §D2).</summary>
    public int LeafFillPercent = 100;
}

/// <summary>What one tree's build did.</summary>
internal struct TreeBuildResult
{
    public long Entries;
    public int UniqueConflicts;
    public int ExtraMergePasses;
    public long NodesWritten;
}

/// <summary>
/// The typed half of one tree's sorted rebuild (#1588): the stride and sort of its staged entries, and the build of the tree from its merged runs. Made by
/// the tree itself (<see cref="BTreeBase{TStore}.CreateRebuild"/>), so everything below the interface runs on the tree's own key type.
/// </summary>
internal unsafe interface ITreeRebuild
{
    /// <summary>The bytes one staged entry takes.</summary>
    int Stride { get; }

    /// <summary>The key's bytes, at the start of the entry.</summary>
    int KeySize { get; }

    /// <summary>Where the location sits in the entry.</summary>
    int LocationOffset { get; }

    /// <summary>False for a key type the staged order is not exact for; the rebuild refuses it rather than build a wrong tree.</summary>
    bool Supported { get; }

    /// <summary>Sorts <paramref name="count"/> staged entries in place; false when they already were in order.</summary>
    bool Sort(byte* entries, int count, byte* scratch, Span<int> counts);

    /// <summary>Compares two staged entries' keys in the tree's order.</summary>
    int CompareKeys(byte* a, byte* b);

    /// <summary>Merges the tree's runs and fills the (empty) tree from them, in key then location order.</summary>
    TreeBuildResult Build(TreeBuildInput input);
}

/// <summary>
/// The build of one tree from its merged runs (#1588, design §D1): the bottom-up <see cref="BTree{TKey,TStore}.SortedBuilder"/>, which writes every node
/// once. The ordered insert through the tree's own <c>Add</c> — fed in key order it fills each leaf before moving right — is the builder's differential
/// oracle in <c>SortedBuilderTests</c>; it is never a runtime path.
/// </summary>
/// <remarks>
/// A unique tree keeps the first entry of each key, the lowest location (RB-07): the rest are counted as conflicts and never reach the tree. The batch
/// point runs every <see cref="DatabaseEngine.RecoveryBatchSize"/> entries, after the accessor's changes are committed (CK-08).
/// </remarks>
internal sealed unsafe class TreeRebuild<TKey, TStore> : ITreeRebuild where TKey : unmanaged where TStore : struct, IPageStore
{
    private readonly BTree<TKey, TStore> _tree;

    public TreeRebuild(BTree<TKey, TStore> tree) => _tree = tree;

    public int Stride => StagedOrder<TKey>.Stride;

    public int KeySize => sizeof(TKey);

    public bool Supported => StagedOrder<TKey>.IsSupported;

    public int LocationOffset
    {
        get
        {
            var e = default(StagedEntry<TKey>);
            return (int)Unsafe.ByteOffset(ref Unsafe.As<StagedEntry<TKey>, byte>(ref e), ref Unsafe.As<int, byte>(ref e.Location));
        }
    }

    public bool Sort(byte* entries, int count, byte* scratch, Span<int> counts)
        => StagedOrder<TKey>.Sort(new Span<StagedEntry<TKey>>(entries, count), new Span<StagedEntry<TKey>>(scratch, count), counts);

    public int CompareKeys(byte* a, byte* b) => StagedOrder<TKey>.CompareKeys(in *(StagedEntry<TKey>*)a, in *(StagedEntry<TKey>*)b);

    public TreeBuildResult Build(TreeBuildInput input)
    {
        var result = new TreeBuildResult();
        var runs = input.Runs;
        if (runs.Count == 0)
        {
            return result;
        }

        var maxFanIn = (int)(input.ReadBufferBytes / RebuildTempFile.BlockSize);
        var fileRuns = 0;
        foreach (var r in runs)
        {
            fileRuns += r.InMemory ? 0 : 1;
        }

        if (fileRuns > 0 && maxFanIn < 2)
        {
            ThrowHelper.ThrowArgument($"Index rebuild: a {input.ReadBufferBytes}-byte read buffer cannot merge runs; it needs two blocks at least.");
        }

        if (fileRuns > maxFanIn)
        {
            result.ExtraMergePasses = RunMerger<TKey>.MergeDown(input.File, runs, maxFanIn, input.ReadBuffer, input.ReadBufferBytes, input.WriteBuffer,
                input.WriteBufferBytes, new Span<byte>(input.Staging, input.StagingBytes), ref input.NextRunId);
        }

        var merger = new RunMerger<TKey>(input.File, runs.ToArray(), input.ReadBuffer, input.ReadBufferBytes, input.MaxRunBufferBytes);
        var multi = _tree.AllowMultiple;
        var accessor = _tree.Segment.CreateChunkAccessor(input.ChangeSet);
        try
        {
            var builder = _tree.BeginSortedBuild(input.LeafFillPercent, input.ChangeSet);
            var previous = default(StagedEntry<TKey>);
            var hasPrevious = false;
            var sinceBatchPoint = 0;
            while (merger.Next(out var entry))
            {
                result.Entries++;
                if (!multi && hasPrevious && StagedOrder<TKey>.CompareKeys(in previous, in entry) == 0)
                {
                    // RB-07: two entities hold one unique key; the lower location already won it.
                    result.UniqueConflicts++;
                    continue;
                }

                var elementId = builder.Append(entry.Key, entry.Location, ref accessor);
                if (multi)
                {
                    input.Sink?.Add(input.MultiFieldIndex, entry.Location, elementId);
                }

                previous = entry;
                hasPrevious = true;
                if (++sinceBatchPoint >= DatabaseEngine.RecoveryBatchSize && input.BatchPoint != null)
                {
                    accessor.CommitChanges();
                    input.BatchPoint();
                    sinceBatchPoint = 0;
                }
            }

            builder.Finish(ref accessor);
            result.NodesWritten = builder.NodesWritten;
        }
        finally
        {
            accessor.Dispose();
        }

        return result;
    }
}
