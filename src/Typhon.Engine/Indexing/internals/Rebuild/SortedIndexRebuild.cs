using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Logging;

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
    public long NodesWritten;
    public int FullLeafTrees;
    public int WorkerBatchPoints;
    public int Partitions;      // the most stage partitions an archetype used
    public int BuildWorkers;    // the most build workers an archetype used
    public double StageMs;
    public double BuildMs;
    public double WriteBackMs;
    public long DataFilePagesRead;      // during the passes, the whole engine's: the rebuild is the only work of the open while it runs
    public long DataFilePagesWritten;
}

/// <summary>A rebuild phase's progress, which its workers count and the coordinator reports every few seconds (design §12).</summary>
internal sealed class RebuildProgress
{
    public long Done;
    public long Total;
    public IndexRebuildPhase Phase;
    public long StartTimestamp;
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
/// buffer. Nothing is proportional to the data: more data is more runs.
/// </para>
/// <para>
/// <b>Workers (§D8, §9).</b> Each phase runs on <see cref="RebuildWorkers"/>: the stage on P partitions of the sorted cluster list, each with its share of
/// the budget; the build on one worker per tree, the largest first; the write-back on one. Every worker has its own epoch scope, ChangeSet and batch points
/// (CK-08); the calling thread holds no scope while they run (RB-09).
/// </para>
/// </remarks>
internal static unsafe class SortedIndexRebuild
{
    /// <summary>The temp file's write staging and the multi-pass merge's output buffer: 16 blocks each.</summary>
    private const int IoBufferBytes = 16 * RebuildTempFile.BlockSize;

    /// <summary>The fewest entries a stage buffer holds, whatever the budget.</summary>
    private const int MinimumStageEntries = 4096;

    /// <summary>The fewest clusters a stage partition walks: below it a partition's thread costs more than its share of the walk.</summary>
    private const int ClustersPerPartition = 1024;

    /// <summary>The run ids each tree's merge-down may number its runs with, a range of its own so builds running side by side never share one.</summary>
    private const int MergeRunIdsPerTree = 1 << 22;

    /// <summary>Test hook: when positive, the fewest clusters a stage partition walks, so a small archetype is staged by several partitions.</summary>
    internal static int ClustersPerPartitionForTest { get; set; }

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
        public RebuildEnvironment Environment;
        public IndexRebuildStats Stats;
        public ILogger Logger;
        public string ArchetypeName;
        public ushort ArchetypeId;
        public Func<long> DataFilePagesRead;
        public Func<long> DataFilePagesWritten;
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
        public List<RunDescriptor> Runs = [];
        public long Entries;        // staged, every partition
        public bool Ascending;      // the keys ascended with location across the whole archetype (§D2)
        public bool Kept;           // built from the partitions' buffers, nothing spilled
    }

    /// <summary>One stage partition's state for one field: its buffer, its runs, and whether its keys ascend with location (§D2).</summary>
    private sealed class FieldStage
    {
        public NativeBuffer Buffer;
        public byte* Base;
        public int Count;
        public List<RunDescriptor> Runs = [];
        public long Entries;
        public bool Ascending = true;
        public NativeBuffer First;   // the partition's first staged entry, for the check across partitions
        public NativeBuffer Last;    // its last staged entry so far
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

    /// <summary>
    /// Rebuilds every persistent tree of <see cref="Context.State"/>, all of them empty, on the environment's workers; the calling thread holds no epoch
    /// scope (RB-09). Returns the unique-key conflicts dropped (RB-07).
    /// </summary>
    public static int Run(Context context)
    {
        var state = context.State;
        var options = context.Options;
        var stats = context.Stats;
        var environment = context.Environment;
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
        var workers = Math.Max(1, environment.Workers);
        RebuildTempFile file = null;
        var fileLock = new Lock();
        var stages = new FieldStage[0][];
        var pool = new List<NativeBuffer>();   // every buffer a worker allocated, freed in the end whatever happened
        ElementIdWriteBack writeBack = null;
        var conflicts = 0;
        var nextRunId = 0;

        RebuildTempFile OpenFile()
        {
            lock (fileLock)
            {
                return file ??= RebuildTempFile.Create(context.TempDirectory, context.TempFileName);
            }
        }

        NativeBuffer Allocate(long bytes, string id)
        {
            var buffer = memory.Allocate(bytes, id);
            lock (pool)
            {
                pool.Add(buffer);
            }

            return buffer;
        }

        void Free(ref NativeBuffer buffer)
        {
            if (buffer == null)
            {
                return;
            }

            lock (pool)
            {
                pool.Remove(buffer);
            }

            memory.Free(ref buffer);
        }

        var archetypeSpan = TyphonEvent.BeginDataIndexRebuild(context.ArchetypeId, (byte)IndexRebuildPhase.Archetype, plans.Count);
        var startedAt = Stopwatch.GetTimestamp();
        var readAtStart = context.DataFilePagesRead?.Invoke() ?? 0;
        var writtenAtStart = context.DataFilePagesWritten?.Invoke() ?? 0;
        var logger = context.Logger;
        var name = context.ArchetypeName ?? "?";
        var archetypeId = context.ArchetypeId;
        var progress = new RebuildProgress();

        // Every few seconds from the coordinator's wait: done, rate since the phase began, and what is left at that rate.
        void Report()
        {
            if (logger == null || !logger.IsEnabled(LogLevel.Information))
            {
                return;
            }

            var done = Interlocked.Read(ref progress.Done);
            var seconds = Stopwatch.GetElapsedTime(progress.StartTimestamp).TotalSeconds;
            var rate = seconds > 0 ? done / seconds : 0;
            var total = Math.Max(progress.Total, done);
            var percent = total == 0 ? 100 : 100.0 * done / total;
            if (rate > 0)
            {
                IndexRebuildLog.Progress(logger, name, progress.Phase, done, total, percent, rate, (total - done) / rate);
            }
            else
            {
                // Nothing counted yet (before the first batch point, or a merge-down, which counts nothing): no rate, so no ETA.
                IndexRebuildLog.ProgressNoRate(logger, name, progress.Phase, done, total, percent);
            }
        }

        void BeginPhase(IndexRebuildPhase phase, long total)
        {
            progress.Phase = phase;
            progress.Total = total;
            Interlocked.Exchange(ref progress.Done, 0);
            progress.StartTimestamp = Stopwatch.GetTimestamp();
        }

        try
        {
            stats.Archetypes++;
            stats.Trees += plans.Count;

            // ── Stage: P partitions of the sorted cluster list, each with its share of the budget ─────────────────────────────────────────────────────
            var strideSum = 0;
            var maxStride = 0;
            foreach (var p in plans)
            {
                strideSum += p.Stride;
                maxStride = Math.Max(maxStride, p.Stride);
            }

            var perPartition = Math.Max(1, ClustersPerPartitionForTest > 0 ? ClustersPerPartitionForTest : ClustersPerPartition);
            var partitions = (int)Math.Max(1, Math.Min(Math.Min(workers, (clusterCount + perPartition - 1) / perPartition),
                budget / ((plans.Count + 1L) << 20)));
            var partitionBudget = budget / partitions;
            stats.Partitions = Math.Max(stats.Partitions, partitions);
            if (logger != null)
            {
                var multiTrees = 0;
                foreach (var p in plans)
                {
                    multiTrees += p.Tree.AllowMultiple ? 1 : 0;
                }

                IndexRebuildLog.Start(logger, name, clusterCount, plans.Count, multiTrees, budget >> 20, workers, partitions);
            }


            // Σstride twice more for each field's first and last staged keys (the ascent check, D2), so they stay inside the budget too.
            var capacity = Math.Max(MinimumStageEntries, (partitionBudget - IoBufferBytes - 2L * strideSum) / (strideSum + maxStride));
            capacity = Math.Min(capacity, ((long)clusterCount + partitions - 1) / partitions * layout.ClusterSize);
            capacity = Math.Min(capacity, int.MaxValue / maxStride);
            if (StageEntriesForTest > 0)
            {
                capacity = StageEntriesForTest;
            }

            var planArray = plans.ToArray();
            stages = new FieldStage[partitions][];
            BeginPhase(IndexRebuildPhase.Stage, clusterCount);
            var stageStarted = Stopwatch.GetTimestamp();
            var batchPoints = RebuildWorkers.Run(environment, DatabaseEngine.RecoveryPass.IndexStage, partitions, worker =>
            {
                var part = worker.Index;
                var partitionSpan = TyphonEvent.BeginDataIndexRebuild(archetypeId, (byte)IndexRebuildPhase.Stage, part);
                try
                {
                    StagePartition(worker, part);
                }
                finally
                {
                    var staged = 0L;
                    foreach (var s in stages[part] ?? [])
                    {
                        staged += s?.Entries ?? 0;
                    }

                    partitionSpan.Entries = staged;
                    partitionSpan.Dispose();
                }
            }, Report);
            stats.WorkerBatchPoints += batchPoints;

            void StagePartition(RebuildWorker worker, int part)
            {
                var first = (int)((long)clusterCount * part / partitions);
                var last = (int)((long)clusterCount * (part + 1) / partitions);
                var mine = stages[part] = new FieldStage[planArray.Length];
                for (var f = 0; f < planArray.Length; f++)
                {
                    var buffer = Allocate(capacity * planArray[f].Stride, "IndexRebuild.Stage");
                    mine[f] = new FieldStage { Buffer = buffer, Base = buffer.Pointer };
                }

                var scratch = Allocate(capacity * maxStride, "IndexRebuild.SortScratch");
                NativeBuffer staging = null;
                var counts = new int[RadixSort.Buckets];
                try
                {
                    // Sorts a field's buffer, tracking whether its keys still ascend with location.
                    void SortBuffer(FieldPlan p, FieldStage s)
                    {
                        if (s.Count == 0)
                        {
                            return;
                        }

                        if (s.Ascending && s.Last != null && p.Rebuild.CompareKeys(s.Last.Pointer, s.Base) > 0)
                        {
                            s.Ascending = false;
                        }

                        if (p.Rebuild.Sort(s.Base, s.Count, scratch.Pointer, counts))
                        {
                            s.Ascending = false;
                        }
                        else
                        {
                            Interlocked.Increment(ref stats.SortsSkipped);
                        }

                        if (s.First == null)
                        {
                            s.First = Allocate(p.Stride, "IndexRebuild.FirstKey");
                            new ReadOnlySpan<byte>(s.Base, p.Stride).CopyTo(s.First.Span);
                        }

                        if (s.Ascending)
                        {
                            s.Last ??= Allocate(p.Stride, "IndexRebuild.LastKey");
                            new ReadOnlySpan<byte>(s.Base + (long)(s.Count - 1) * p.Stride, p.Stride).CopyTo(s.Last.Span);
                        }
                    }

                    void Spill(FieldPlan p, FieldStage s)
                    {
                        SortBuffer(p, s);
                        staging ??= Allocate(IoBufferBytes, "IndexRebuild.Staging");
                        s.Runs.Add(WriteRun(OpenFile(), archetypeId, s.Base, s.Count, p.Stride, Interlocked.Increment(ref nextRunId) - 1, staging.Span));
                        s.Count = 0;
                        AfterSpillForTest?.Invoke(file, s.Runs[^1]);
                    }

                    var accessor = state.ClusterSegment.CreateScanAccessor();
                    try
                    {
                        var since = 0;
                        var unreported = 0;   // clusters walked since the last report: one locked add per batch point, not per cluster
                        for (var c = first; c < last; c++)
                        {
                            var chunkId = clusters[c];
                            var clusterBase = accessor.GetChunkAddress(chunkId);
                            var occupancy = *(ulong*)clusterBase;
                            since += BitOperations.PopCount(occupancy);
                            unreported++;
                            while (occupancy != 0)
                            {
                                var slot = BitOperations.TrailingZeroCount(occupancy);
                                occupancy &= occupancy - 1;
                                var location = chunkId * 64 + slot;
                                for (var f = 0; f < planArray.Length; f++)
                                {
                                    var p = planArray[f];
                                    var key = clusterBase + p.KeyOffset + slot * p.ComponentSize;
                                    if (p.Sparse && p.Tree.SkipsKey(key))
                                    {
                                        continue;
                                    }

                                    var s = mine[f];
                                    var entry = s.Base + (long)s.Count * p.Stride;
                                    if (p.KeySize < sizeof(int))
                                    {
                                        *(int*)entry = 0;   // the padding a narrow key leaves, so the run's bytes are deterministic
                                    }

                                    Unsafe.CopyBlockUnaligned(entry, key, (uint)p.KeySize);
                                    *(int*)(entry + p.LocationOffset) = location;
                                    s.Entries++;
                                    if (++s.Count == capacity)
                                    {
                                        Spill(p, s);
                                    }
                                }
                            }

                            if (since >= DatabaseEngine.RecoveryBatchSize)
                            {
                                worker.BatchPoint();   // the scan accessor holds no dirty slot; its pins are released by the refresh's rules (EP-02)
                                Interlocked.Add(ref progress.Done, unreported);
                                since = 0;
                                unreported = 0;
                            }
                        }

                        Interlocked.Add(ref progress.Done, unreported);
                    }
                    finally
                    {
                        accessor.Dispose();
                    }

                    // The partition's tail, sorted: kept or spilled once every partition is done.
                    for (var f = 0; f < planArray.Length; f++)
                    {
                        SortBuffer(planArray[f], mine[f]);
                    }
                }
                finally
                {
                    Free(ref scratch);
                    Free(ref staging);
                }
            }

            // Barrier. Per field: no partition spilled → its partitions' tails are its runs, in memory; otherwise they spill too and their buffers go back.
            // The keys ascended with location across the archetype when every partition's did and each one's first key is not below the one before's last.
            // Kept buffers are spilled after all, the largest first, while the build's least reserve does not fit beside them (RB-09): two I/O buffers and
            // a two-block read buffer for one builder, and the element-id pairs' collector and keep shares.
            NativeBuffer coordinatorStaging = null;
            try
            {
                void SpillField(int f)
                {
                    var p = planArray[f];
                    for (var part = 0; part < partitions; part++)
                    {
                        var s = stages[part][f];
                        p.Runs.AddRange(s.Runs);
                        if (s.Count > 0)
                        {
                            coordinatorStaging ??= Allocate(IoBufferBytes, "IndexRebuild.Staging");
                            p.Runs.Add(WriteRun(OpenFile(), archetypeId, s.Base, s.Count, p.Stride, Interlocked.Increment(ref nextRunId) - 1,
                                coordinatorStaging.Span));
                            s.Count = 0;
                            AfterSpillForTest?.Invoke(file, p.Runs[^1]);
                        }

                        Free(ref s.Buffer);
                    }
                }

                var keep = new bool[planArray.Length];
                var hasMulti = false;
                for (var f = 0; f < planArray.Length; f++)
                {
                    var p = planArray[f];
                    hasMulti |= p.Tree.AllowMultiple;
                    var spilled = false;
                    p.Ascending = true;
                    FieldStage previous = null;
                    for (var part = 0; part < partitions; part++)
                    {
                        var s = stages[part][f];
                        spilled |= s.Runs.Count > 0;
                        p.Entries += s.Entries;
                        if (s.Entries > 0)
                        {
                            // Short-circuits on a partition that did not ascend, which keeps no last key.
                            p.Ascending = p.Ascending && s.Ascending
                                && (previous == null || p.Rebuild.CompareKeys(previous.Last.Pointer, s.First.Pointer) <= 0);
                            previous = s;
                        }
                    }

                    for (var part = 0; part < partitions; part++)
                    {
                        Free(ref stages[part][f].First);
                        Free(ref stages[part][f].Last);
                    }

                    keep[f] = !spilled;
                    if (spilled)
                    {
                        SpillField(f);
                    }
                }

                var reserve = 2L * IoBufferBytes + 2L * RebuildTempFile.BlockSize + (hasMulti ? budget / 8 : 0);
                while (memory.Left < reserve)
                {
                    var largest = -1;
                    for (var f = 0; f < planArray.Length; f++)
                    {
                        if (keep[f] && (largest < 0 || planArray[f].Stride > planArray[largest].Stride))
                        {
                            largest = f;   // every kept field holds P buffers of the same capacity: the widest stride holds the most
                        }
                    }

                    if (largest < 0)
                    {
                        break;
                    }

                    keep[largest] = false;
                    SpillField(largest);
                }

                for (var f = 0; f < planArray.Length; f++)
                {
                    var p = planArray[f];
                    p.Kept = keep[f];
                    if (!p.Kept)
                    {
                        continue;
                    }

                    stats.InMemoryTrees++;
                    for (var part = 0; part < partitions; part++)
                    {
                        var s = stages[part][f];
                        if (s.Count > 0)
                        {
                            p.Runs.Add(new RunDescriptor(s.Base, s.Count, Interlocked.Increment(ref nextRunId) - 1));
                        }
                    }
                }
            }
            finally
            {
                Free(ref coordinatorStaging);
            }

            // ── Build: one worker per tree, the largest first; each with its own merge buffers and pair collector ────────────────────────────────────
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

            // As many builders as the budget holds, each with its merge output and staging buffers and the least read buffer a merge needs; the pair
            // collectors share a sixteenth of the budget, and the fields' unspilled pairs keep another (RB-09).
            var pairKeepBytes = budget / 16;
            var pairBytes = sections.Count > 0 ? budget / 16 : 0;
            var perBuilder = 2L * IoBufferBytes + (maxFileRuns > 0 ? 2L * RebuildTempFile.BlockSize : 0);
            var available = memory.Left - (sections.Count > 0 ? pairKeepBytes + pairBytes : 0);
            var builders = (int)Math.Clamp(available / perBuilder, 1, Math.Min(workers, plans.Count));
            stats.BuildWorkers = Math.Max(stats.BuildWorkers, builders);
            if (sections.Count > 0)
            {
                // Pair runs take ids from the top half, so no key run, merged or not, ever shares one with them.
                writeBack = new ElementIdWriteBack(sections.ToArray(), memory, pairBytes / 2 / builders, pairKeepBytes, OpenFile, int.MaxValue / 2);
            }

            // A merge-down numbers its runs from its own range, so builds running side by side never share an id (RB-10 checks a block's owner).
            if ((long)nextRunId + (plans.Count + 1L) * MergeRunIdsPerTree >= int.MaxValue / 2)
            {
                ThrowHelper.ThrowInvalidOp($"Index rebuild: {plans.Count} trees and {nextRunId} runs exhaust the run ids.");
            }

            var readBytes = 0L;
            if (maxFileRuns > 0)
            {
                var left = memory.Left - builders * (2L * IoBufferBytes + (writeBack?.CollectorBytes ?? 0)) - (writeBack == null ? 0 : pairKeepBytes);
                readBytes = ReadBufferBytes(left / builders, maxFileRuns, options.MergeReadBufferBytes);
            }

            var order = new int[planArray.Length];
            for (var i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, (a, b) => planArray[b].Entries.CompareTo(planArray[a].Entries));
            var results = new TreeBuildResult[planArray.Length];
            var nextPlan = -1;
            var firstMergeRunId = nextRunId;
            stats.StageMs += Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
            var totalEntries = 0L;
            foreach (var p in plans)
            {
                totalEntries += p.Entries;
            }

            BeginPhase(IndexRebuildPhase.Build, totalEntries);
            var buildStarted = Stopwatch.GetTimestamp();
            batchPoints = RebuildWorkers.Run(environment, DatabaseEngine.RecoveryPass.IndexBuild, builders, worker =>
            {
                NativeBuffer readBuffer = null;
                NativeBuffer writeBuffer = null;
                NativeBuffer staging = null;
                PairCollector collector = null;
                try
                {
                    int next;
                    while ((next = Interlocked.Increment(ref nextPlan)) < order.Length)
                    {
                        worker.ThrowIfAborted();
                        var index = order[next];
                        var p = planArray[index];
                        if (!p.Kept)
                        {
                            readBuffer ??= Allocate(readBytes, "IndexRebuild.MergeRead");
                        }

                        writeBuffer ??= Allocate(IoBufferBytes, "IndexRebuild.MergeOutput");
                        staging ??= Allocate(IoBufferBytes, "IndexRebuild.Staging");
                        if (p.Column >= 0)
                        {
                            collector ??= writeBack.CreateCollector(staging.Pointer, staging.Length);
                            collector.Begin(p.Column);
                        }

                        var input = new TreeBuildInput
                        {
                            File = file,
                            Runs = p.Runs,
                            ReadBuffer = readBuffer == null ? null : readBuffer.Pointer,
                            ReadBufferBytes = readBuffer == null ? 0 : readBytes,
                            MaxRunBufferBytes = options.MergeReadBufferBytes,
                            WriteBuffer = writeBuffer.Pointer,
                            WriteBufferBytes = writeBuffer.Length,
                            Staging = staging.Pointer,
                            StagingBytes = staging.Length,
                            ChangeSet = worker.ChangeSet,
                            BatchPoint = worker.BatchPoint,
                            Sink = p.Column >= 0 ? collector : null,
                            MultiFieldIndex = p.Column,
                            NextRunId = firstMergeRunId + (index + 1) * MergeRunIdsPerTree,
                            LeafFillPercent = p.Tree.AllowMultiple || p.Ascending ? 100 : options.LeafFillPercent,

                            // Room in the internal nodes too, but for a unique tree whose inserts land at its right edge, which splits only there.
                            InternalFillPercent = !p.Tree.AllowMultiple && p.Ascending ? 100 : options.LeafFillPercent,
                            Progress = progress,
                        };
                        var treeSpan = TyphonEvent.BeginDataIndexRebuild(archetypeId, (byte)IndexRebuildPhase.Build, index);
                        try
                        {
                            results[index] = p.Rebuild.Build(input);
                            treeSpan.Entries = results[index].Entries;
                        }
                        finally
                        {
                            treeSpan.Dispose();
                        }

                        if (p.Column >= 0)
                        {
                            collector.End();
                        }

                        if (p.Kept)
                        {
                            for (var part = 0; part < partitions; part++)
                            {
                                Free(ref stages[part][index].Buffer);
                            }
                        }
                    }
                }
                finally
                {
                    collector?.Dispose();
                    Free(ref readBuffer);
                    Free(ref writeBuffer);
                    Free(ref staging);
                }
            }, Report);
            stats.WorkerBatchPoints += batchPoints;
            stats.BuildMs += Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds;

            long entries = 0, nodes = 0;
            int runs = 0, extraPasses = 0;
            for (var i = 0; i < planArray.Length; i++)
            {
                var p = planArray[i];
                var result = results[i];
                entries += result.Entries;
                nodes += result.NodesWritten;
                runs += p.Runs.Count;
                extraPasses += result.ExtraMergePasses;
                conflicts += result.UniqueConflicts;
                stats.Entries += result.Entries;
                stats.ExtraMergePasses += result.ExtraMergePasses;
                stats.NodesWritten += result.NodesWritten;
                if (p.Tree.AllowMultiple || p.Ascending)
                {
                    stats.FullLeafTrees++;   // full by the D2 policy, not because the option says 100
                }
            }

            // ── Element ids back to the cluster tails: one worker, one ascending walk, each field's pairs merged through a share of what is left ─────
            var writeBackStarted = Stopwatch.GetTimestamp();
            if (writeBack != null)
            {
                stats.ElementIdRuns += writeBack.SpilledRuns;
                BeginPhase(IndexRebuildPhase.WriteBack, clusterCount);
                batchPoints = RebuildWorkers.Run(environment, DatabaseEngine.RecoveryPass.IndexWriteBack, 1, worker =>
                {
                    var writeBackSpan = TyphonEvent.BeginDataIndexRebuild(archetypeId, (byte)IndexRebuildPhase.WriteBack, 0);
                    NativeBuffer readBuffer = null;
                    NativeBuffer writeBuffer = null;
                    NativeBuffer staging = null;
                    try
                    {
                        var applyRead = 0L;
                        if (writeBack.SpilledRuns > 0)
                        {
                            writeBuffer = Allocate(IoBufferBytes, "IndexRebuild.MergeOutput");
                            staging = Allocate(IoBufferBytes, "IndexRebuild.Staging");
                            applyRead = Math.Max((long)sections.Count * 2 * RebuildTempFile.BlockSize, BlockFloor(memory.Left));
                            readBuffer = Allocate(applyRead, "IndexRebuild.ElementIdRead");
                        }

                        writeBack.Apply(state.ClusterSegment, layout, clusters, worker.ChangeSet, worker.BatchPoint,
                            readBuffer == null ? null : readBuffer.Pointer, applyRead, options.MergeReadBufferBytes,
                            writeBuffer == null ? null : writeBuffer.Pointer, writeBuffer?.Length ?? 0, staging == null ? null : staging.Pointer,
                            staging?.Length ?? 0, progress);
                        writeBackSpan.Entries = writeBack.TailsWritten;
                    }
                    finally
                    {
                        writeBackSpan.Dispose();
                        Free(ref readBuffer);
                        Free(ref writeBuffer);
                        Free(ref staging);
                    }
                }, Report);
                stats.WorkerBatchPoints += batchPoints;
                stats.ElementIdTails += writeBack.TailsWritten;
            }

            stats.WriteBackMs += Stopwatch.GetElapsedTime(writeBackStarted).TotalMilliseconds;
            stats.UniqueConflicts += conflicts;
            var pagesRead = (context.DataFilePagesRead?.Invoke() ?? 0) - readAtStart;
            var pagesWritten = (context.DataFilePagesWritten?.Invoke() ?? 0) - writtenAtStart;
            stats.DataFilePagesRead += pagesRead;
            stats.DataFilePagesWritten += pagesWritten;
            archetypeSpan.Entries = entries;
            if (logger != null)
            {
                IndexRebuildLog.Done(logger, name, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(stageStarted, buildStarted).TotalMilliseconds,   // as stats.StageMs; the total adds the planning
                    Stopwatch.GetElapsedTime(buildStarted, writeBackStarted).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(writeBackStarted).TotalMilliseconds, entries, runs, extraPasses,
                    (file?.BytesWritten ?? 0) / (double)(1 << 20), nodes, pagesRead, pagesWritten);
            }

            return conflicts;
        }
        catch (IndexRebuildTempCorruptException ex)
        {
            if (logger != null)
            {
                IndexRebuildLog.TempCorrupt(logger, name, ex.Offset, ex.Path, ex.Check);
            }

            throw;
        }
        finally
        {
            archetypeSpan.Dispose();
            writeBack?.Dispose();
            lock (pool)
            {
                for (var i = pool.Count - 1; i >= 0; i--)
                {
                    var buffer = pool[i];
                    memory.Free(ref buffer);
                }

                pool.Clear();
            }

            if (file != null)
            {
                stats.TempBytesWritten += file.BytesWritten;
                stats.TempBytesRead += file.BytesRead;
                file.Dispose();
            }
        }
    }

    /// <summary>Writes one sorted run to the temp file, inside its trace span.</summary>
    private static RunDescriptor WriteRun(RebuildTempFile file, ushort archetypeId, byte* records, int count, int stride, int runId, Span<byte> staging)
    {
        var span = TyphonEvent.BeginDataIndexRebuild(archetypeId, (byte)IndexRebuildPhase.Spill, runId);
        try
        {
            span.Entries = count;
            return file.Write(new ReadOnlySpan<byte>(records, count * stride), stride, count, RebuildBlockKind.Run, runId, staging);
        }
        finally
        {
            span.Dispose();
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
