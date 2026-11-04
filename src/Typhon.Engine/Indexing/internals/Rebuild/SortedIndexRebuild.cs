using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>What the open's index rebuilds did, summed over archetypes (#1588). Diagnostic and test oracle.</summary>
internal sealed class IndexRebuildStats
{
    public int Archetypes;
    public int Trees;
    public long Entries;
    public int UniqueConflicts;
    public int Runs;
    public int InMemoryTrees;
    public int ExtraMergePasses;
    public int SortsSkipped;
    public long TempBytesWritten;
    public long TempBytesRead;
    public long ElementIdTails;
    public int ElementIdRuns;
    public long PeakNativeBytes;
}

/// <summary>
/// One archetype's sorted index rebuild (#1588, design <c>Indexing/sorted-index-rebuild.md</c>): every persistent tree of the archetype is filled from
/// the cluster data by an external merge sort, in a fixed memory budget whatever the archetype's size.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stage.</b> One ascending walk of the clusters, through a scan accessor (EP-02), copies each occupied slot's indexed keys, with the slot's cluster
/// location, into one buffer per field. A full buffer is sorted (radix, or byte compare for String64; skipped when already ordered) and spilled to the
/// temp file as a run. Every <see cref="DatabaseEngine.RecoveryBatchSize"/> entities the open's batch point runs (CK-08).
/// </para>
/// <para>
/// <b>Build.</b> Per tree, a k-way merge of its runs feeds its build in (key, location) order; a field that never spilled is built from its buffer, with
/// no temp file at all. <b>Write-back.</b> The multi-value element ids go back to the cluster tails in location order (<see cref="ElementIdWriteBack"/>).
/// </para>
/// <para>
/// <b>Memory (RB-09)</b>: every native buffer goes through one <see cref="RebuildMemory"/>, planned against the budget phase by phase: the stage's field
/// buffers and sort scratch; then the kept in-memory runs, the element-id pair buffers and the merge's read and write buffers; then the write-back's read
/// buffer. Nothing is proportional to the data: more data is more runs. Phase 4 of the design: one worker, the calling thread, which holds the open's
/// epoch scope and makes the batch points.
/// </para>
/// </remarks>
internal static unsafe class SortedIndexRebuild
{
    /// <summary>The temp file's write staging and the multi-pass merge's output buffer: 16 blocks each.</summary>
    private const int IoBufferBytes = 16 * RebuildTempFile.BlockSize;

    /// <summary>The fewest entries a stage buffer holds, whatever the budget.</summary>
    private const int MinimumStageEntries = 4096;

    /// <summary>The temp directory's name inside a bundle, which a bundle copy skips.</summary>
    internal const string TempDirectoryName = "rebuild-tmp";

    /// <summary>Test hook: when positive, every stage buffer holds this many entries, so a small archetype spills many runs. A test sets it,
    /// [NonParallelizable], and restores 0.</summary>
    internal static int StageEntriesForTest { get; set; }

    /// <summary>Test hook: when positive, the merge reads this many bytes at once (at least two blocks), forcing extra merge passes.</summary>
    internal static int MergeReadBytesForTest { get; set; }

    /// <summary>Test hook: when positive, the budget instead of the options', below their 16 MiB floor, so a small archetype is larger than it.</summary>
    internal static long MemoryBudgetForTest { get; set; }

    /// <summary>Test hook: called after each run is spilled, with the temp file and the run, to stage a corruption.</summary>
    internal static Action<RebuildTempFile, RunDescriptor> AfterSpillForTest { get; set; }

    /// <summary>Everything one archetype's rebuild needs from the engine.</summary>
    internal sealed class Context
    {
        public ArchetypeClusterState State;
        public IndexRebuildOptions Options;
        public BufferOwner Owner;
        public string TempDirectory;
        public string TempFileName;
        public ChangeSet ChangeSet;
        public Action BatchPoint;
        public IndexRebuildStats Stats;
    }

    private sealed class FieldPlan
    {
        public BTreeBase<PersistentStore> Tree;
        public ITreeRebuild Rebuild;
        public int KeyOffset;       // component offset + field offset within the cluster, before the slot's stride
        public int ComponentSize;
        public int KeySize;
        public int Stride;
        public int LocationOffset;
        public bool Sparse;
        public int Section = -1;    // the tail section of a multi-value field
        public int Column = -1;     // its element-id column
        public NativeBuffer Buffer;
        public byte* Base;
        public int Count;
        public List<RunDescriptor> Runs = [];
    }

    /// <summary>The temp directory of a bundle: <c>{bundle}/rebuild-tmp</c>, or <c>{TempDirectory}/{bundle name}.rebuild-tmp</c> when the options name
    /// one, a directory of this database's own either way, which is what lets the sweep delete what it finds there.</summary>
    public static string TempDirectoryOf(IndexRebuildOptions options, string bundleDirectory)
    {
        if (options.TempDirectory == null)
        {
            return Path.Combine(bundleDirectory, TempDirectoryName);
        }

        var name = Path.GetFileName(bundleDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Path.Combine(options.TempDirectory, $"{name}.{TempDirectoryName}");
    }

    /// <summary>Rebuilds every persistent tree of <see cref="Context.State"/>, all of them empty. Returns the unique-key conflicts dropped (RB-07).</summary>
    public static int Run(Context context)
    {
        var state = context.State;
        var options = context.Options;
        var stats = context.Stats;
        var clusterCount = state.ActiveClusterCount;
        if (state.IndexSlots == null || clusterCount == 0)
        {
            return 0;
        }

        var layout = state.Layout;
        var plans = BuildPlans(state, layout);
        if (plans.Count == 0)
        {
            return 0;
        }

        var clusters = new int[clusterCount];
        Array.Copy(state.ActiveClusterIds, clusters, clusterCount);
        Array.Sort(clusters);

        var budget = MemoryBudgetForTest > 0 ? MemoryBudgetForTest : options.MemoryBudgetBytes;
        var memory = new RebuildMemory(budget, context.Owner, stats);
        RebuildTempFile file = null;
        NativeBuffer staging = null;
        NativeBuffer scratch = null;
        NativeBuffer readBuffer = null;
        NativeBuffer writeBuffer = null;
        ElementIdWriteBack writeBack = null;
        var conflicts = 0;
        var nextRunId = 0;

        RebuildTempFile OpenFile() => file ??= RebuildTempFile.Create(context.TempDirectory, context.TempFileName);

        IntPtr Staging()
        {
            staging ??= memory.Allocate(IoBufferBytes, "IndexRebuild.Staging");
            return (IntPtr)staging.Pointer;
        }

        try
        {
            stats.Archetypes++;
            stats.Trees += plans.Count;

            // ── Stage: the field buffers and one sort scratch share the budget, less the staging buffer a spill needs ───────────────────────────────────
            var strideSum = 0;
            var maxStride = 0;
            foreach (var p in plans)
            {
                strideSum += p.Stride;
                maxStride = Math.Max(maxStride, p.Stride);
            }

            var capacity = Math.Max(MinimumStageEntries, (budget - IoBufferBytes) / (strideSum + maxStride));
            capacity = Math.Min(capacity, (long)clusterCount * layout.ClusterSize);
            capacity = Math.Min(capacity, int.MaxValue / maxStride);
            if (StageEntriesForTest > 0)
            {
                capacity = StageEntriesForTest;
            }

            foreach (var p in plans)
            {
                p.Buffer = memory.Allocate(capacity * p.Stride, "IndexRebuild.Stage");
                p.Base = p.Buffer.Pointer;
            }

            scratch = memory.Allocate(capacity * maxStride, "IndexRebuild.SortScratch");
            var counts = new int[RadixSort.Buckets];

            void Spill(FieldPlan p)
            {
                if (!p.Rebuild.Sort(p.Base, p.Count, scratch.Pointer, counts))
                {
                    stats.SortsSkipped++;
                }

                p.Runs.Add(OpenFile().Write(new ReadOnlySpan<byte>(p.Base, p.Count * p.Stride), p.Stride, p.Count, RebuildBlockKind.Run, nextRunId++,
                    new Span<byte>((void*)Staging(), IoBufferBytes)));
                p.Count = 0;
                AfterSpillForTest?.Invoke(file, p.Runs[^1]);
            }

            var planArray = plans.ToArray();
            var accessor = state.ClusterSegment.CreateScanAccessor();
            try
            {
                var since = 0;
                foreach (var chunkId in clusters)
                {
                    var clusterBase = accessor.GetChunkAddress(chunkId);
                    var occupancy = *(ulong*)clusterBase;
                    since += BitOperations.PopCount(occupancy);
                    while (occupancy != 0)
                    {
                        var slot = BitOperations.TrailingZeroCount(occupancy);
                        occupancy &= occupancy - 1;
                        var location = chunkId * 64 + slot;
                        foreach (var p in planArray)
                        {
                            var key = clusterBase + p.KeyOffset + slot * p.ComponentSize;
                            if (p.Sparse && p.Tree.SkipsKey(key))
                            {
                                continue;
                            }

                            var entry = p.Base + (long)p.Count * p.Stride;
                            if (p.KeySize < sizeof(int))
                            {
                                *(int*)entry = 0;   // the padding a narrow key leaves, so the run's bytes are deterministic
                            }

                            Unsafe.CopyBlockUnaligned(entry, key, (uint)p.KeySize);
                            *(int*)(entry + p.LocationOffset) = location;
                            if (++p.Count == capacity)
                            {
                                Spill(p);
                            }
                        }
                    }

                    if (since >= DatabaseEngine.RecoveryBatchSize && context.BatchPoint != null)
                    {
                        context.BatchPoint();
                        since = 0;
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }

            // A field that never spilled keeps its buffer as one in-memory run; the others spill their tail and give the buffer back.
            foreach (var p in plans)
            {
                if (p.Runs.Count == 0)
                {
                    if (!p.Rebuild.Sort(p.Base, p.Count, scratch.Pointer, counts))
                    {
                        stats.SortsSkipped++;
                    }

                    if (p.Count > 0)
                    {
                        p.Runs.Add(new RunDescriptor(p.Base, p.Count, nextRunId++));
                    }

                    stats.InMemoryTrees++;
                    continue;
                }

                if (p.Count > 0)
                {
                    Spill(p);
                }

                memory.Free(ref p.Buffer);
            }

            memory.Free(ref scratch);

            // ── Build: the kept runs stay, the pair buffers and the merge's buffers take what is left ───────────────────────────────────────────────
            var sections = new List<int>();
            var maxFileRuns = 0;
            foreach (var p in plans)
            {
                stats.Runs += p.Runs.Count;
                var fileRuns = 0;
                foreach (var r in p.Runs)
                {
                    fileRuns += r.InMemory ? 0 : 1;
                }

                maxFileRuns = Math.Max(maxFileRuns, fileRuns);
                if (p.Tree.AllowMultiple)
                {
                    p.Column = sections.Count;
                    sections.Add(p.Section);
                }
            }

            var pairBufferBytes = budget / 32;
            var pairKeepBytes = budget / 16;
            if (sections.Count > 0)
            {
                // Pair runs take ids from the top half, so no key run, merged or not, ever shares one with them.
                writeBack = new ElementIdWriteBack(sections.ToArray(), memory, pairBufferBytes, pairKeepBytes, OpenFile, Staging, IoBufferBytes,
                    int.MaxValue / 2);
            }

            var readBytes = 0L;
            if (maxFileRuns > 0)
            {
                writeBuffer = memory.Allocate(IoBufferBytes, "IndexRebuild.MergeOutput");
                var reserved = (staging == null ? IoBufferBytes : 0) + (writeBack == null ? 0 : 2 * pairBufferBytes + pairKeepBytes);
                readBytes = ReadBufferBytes(memory.Left - reserved, maxFileRuns, options.MergeReadBufferBytes);
                readBuffer = memory.Allocate(readBytes, "IndexRebuild.MergeRead");
            }

            foreach (var p in plans)
            {
                if (p.Column >= 0)
                {
                    writeBack.BeginColumn(p.Column);
                }

                var input = new TreeBuildInput
                {
                    File = file,
                    Runs = p.Runs,
                    ReadBuffer = readBuffer == null ? null : readBuffer.Pointer,
                    ReadBufferBytes = readBytes,
                    MaxRunBufferBytes = options.MergeReadBufferBytes,
                    WriteBuffer = writeBuffer == null ? null : writeBuffer.Pointer,
                    WriteBufferBytes = writeBuffer?.Length ?? 0,
                    Staging = staging == null ? null : staging.Pointer,
                    StagingBytes = staging?.Length ?? 0,
                    ChangeSet = context.ChangeSet,
                    BatchPoint = context.BatchPoint,
                    Sink = p.Column >= 0 ? writeBack : null,
                    MultiFieldIndex = p.Column,
                    NextRunId = nextRunId,
                };

                var result = p.Rebuild.Build(input);
                nextRunId = input.NextRunId;
                conflicts += result.UniqueConflicts;
                stats.Entries += result.Entries;
                stats.ExtraMergePasses += result.ExtraMergePasses;
                if (p.Column >= 0)
                {
                    writeBack.EndColumn(p.Column);
                }

                memory.Free(ref p.Buffer);
            }

            memory.Free(ref readBuffer);

            // ── Element ids back to the cluster tails, each field's pairs merged through a share of what is left ───────────────────────────────────
            if (writeBack != null)
            {
                writeBack.EndBuilds();
                stats.ElementIdRuns += writeBack.SpilledRuns;
                var applyRead = 0L;
                if (writeBack.SpilledRuns > 0)
                {
                    writeBuffer ??= memory.Allocate(IoBufferBytes, "IndexRebuild.MergeOutput");
                    var left = memory.Left - (staging == null ? IoBufferBytes : 0);
                    applyRead = Math.Max((long)sections.Count * 2 * RebuildTempFile.BlockSize, BlockFloor(left));
                    readBuffer = memory.Allocate(applyRead, "IndexRebuild.ElementIdRead");
                }

                writeBack.Apply(state.ClusterSegment, layout, clusters, context.ChangeSet, context.BatchPoint, readBuffer == null ? null : readBuffer.Pointer,
                    applyRead, options.MergeReadBufferBytes, writeBuffer == null ? null : writeBuffer.Pointer, writeBuffer?.Length ?? 0);
                stats.ElementIdTails += writeBack.TailsWritten;
            }

            stats.UniqueConflicts += conflicts;
            return conflicts;
        }
        finally
        {
            foreach (var p in plans)
            {
                memory.Free(ref p.Buffer);
            }

            memory.Free(ref scratch);
            memory.Free(ref readBuffer);
            memory.Free(ref writeBuffer);
            writeBack?.Dispose();
            memory.Free(ref staging);
            if (file != null)
            {
                stats.TempBytesWritten += file.BytesWritten;
                stats.TempBytesRead += file.BytesRead;
                file.Dispose();
            }
        }
    }

    /// <summary>
    /// The merge's read buffer out of <paramref name="left"/> bytes: no more than the fullest tree's runs can use (each up to
    /// <paramref name="maxRunBufferBytes"/>), unless they do not all get a block, when a merge-down needs it all. At least two blocks.
    /// </summary>
    private static long ReadBufferBytes(long left, int maxFileRuns, int maxRunBufferBytes)
    {
        if (MergeReadBytesForTest > 0)
        {
            return Math.Max(2L * RebuildTempFile.BlockSize, MergeReadBytesForTest);
        }

        var usable = BlockFloor(left);
        var needed = (long)maxFileRuns * maxRunBufferBytes;
        var bytes = maxFileRuns * (long)RebuildTempFile.BlockSize <= usable ? Math.Min(usable, needed) : usable;
        return Math.Max(2L * RebuildTempFile.BlockSize, Math.Min(bytes, int.MaxValue / RebuildTempFile.BlockSize * (long)RebuildTempFile.BlockSize));
    }

    private static long BlockFloor(long bytes) => Math.Max(0, bytes) / RebuildTempFile.BlockSize * RebuildTempFile.BlockSize;

    private static List<FieldPlan> BuildPlans(ArchetypeClusterState state, ArchetypeClusterInfo layout)
    {
        var plans = new List<FieldPlan>();
        foreach (var ixSlot in state.IndexSlots)
        {
            if (ixSlot.Fields == null)
            {
                continue;
            }

            var componentOffset = layout.ComponentOffset(ixSlot.Slot);
            var componentSize = layout.ComponentSize(ixSlot.Slot);
            foreach (var field in ixSlot.Fields)
            {
                if (field.Index == null)
                {
                    continue;
                }

                var rebuild = field.Index.CreateRebuild();
                if (!rebuild.Supported)
                {
                    ThrowHelper.ThrowInvalidOp($"Index rebuild: no exact sort order for the key of the {field.FieldSize}-byte field at {field.FieldOffset}.");
                }

                if (rebuild.KeySize != field.FieldSize)
                {
                    ThrowHelper.ThrowInvalidOp($"Index rebuild: a {field.FieldSize}-byte field is indexed by a tree of {rebuild.KeySize}-byte keys.");
                }

                if (field.Index.AllowMultiple && field.MultiFieldIndex < 0)
                {
                    ThrowHelper.ThrowInvalidOp("Index rebuild: a multi-value tree with no tail section.");
                }

                plans.Add(new FieldPlan
                {
                    Tree = field.Index,
                    Rebuild = rebuild,
                    KeyOffset = componentOffset + field.FieldOffset,
                    ComponentSize = componentSize,
                    KeySize = rebuild.KeySize,
                    Stride = rebuild.Stride,
                    LocationOffset = rebuild.LocationOffset,
                    Sparse = field.Index.Sparse,
                    Section = field.Index.AllowMultiple ? field.MultiFieldIndex : -1,
                });
            }
        }

        return plans;
    }
}
