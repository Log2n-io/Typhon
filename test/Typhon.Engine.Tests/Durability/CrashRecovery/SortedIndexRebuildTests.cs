using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Typhon.Profiler;
using Typhon.Profiler.Events;
using Typhon.Engine.Tests.Profiler;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.SortedRebuild.Row", 1, StorageMode = StorageMode.Versioned)]
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
struct SrRow
{
    [Index]
    public long Unique;

    [Index]
    public String64 Name;

    [Index(AllowMultiple = true)]
    public long Group;

    [Index(AllowMultiple = true, Sparse = true)]
    public int Tag;
}

[Archetype]
class SrArch : Archetype<SrArch>
{
    public static readonly Comp<SrRow> Row = Register<SrRow>();
}

[Component("Typhon.Test.SortedRebuild.Wide", 1, StorageMode = StorageMode.SingleVersion)]
struct SrThreeKeys
{
    [Index]
    public long A;

    [Index]
    public long B;

    [Index]
    public long C;
}

[Archetype]
class SrThreeKeysArch : Archetype<SrThreeKeysArch>
{
    public static readonly Comp<SrThreeKeys> Row = Register<SrThreeKeys>();
}

[Component("Typhon.Test.SortedRebuild.Halves", 1, StorageMode = StorageMode.SingleVersion)]
struct SrHalves
{
    [Index]
    public long Rising;

    [Index]
    public long Halves;
}

[Archetype]
class SrHalvesArch : Archetype<SrHalvesArch>
{
    public static readonly Comp<SrHalves> Row = Register<SrHalves>();
}

/// <summary>
/// The sorted index rebuild (#1588) fills every kind of tree (8-byte unique, String64 unique, 8-byte multi-value, sparse 4-byte multi-value) from the
/// clusters on a crash open, the same way whether it stays in memory, spills runs, merges them in several passes or writes the element ids back by
/// location bucket.
/// </summary>
/// <remarks>
/// What "the same" means is checked against the data, not against another build: every tree agrees with the clusters in both directions
/// (<see cref="IndexDataOracle"/>, plus a lookup of every String64 key, which the oracle skips), and every element id in a cluster tail is the one its
/// entry got, which destroying every entity then proves: each destroy removes through its tail's element id, and the trees end empty.
/// </remarks>
[TestFixture]
[NonParallelizable] // SortedIndexRebuild's test hooks are statics.
internal sealed unsafe class SortedIndexRebuildTests : DiskWalEngineFixture
{
    private const int Count = 6_000;
    private const int Groups = 37;

    public enum Mode
    {
        InMemory,
        Spilled,
        MultiPass,
        PairsSpilled,
        KeptNearTheBudget,
    }

    protected override void OnTearDown()
    {
        SortedIndexRebuild.StageEntriesForTest = 0;
        SortedIndexRebuild.MergeReadBytesForTest = 0;
        SortedIndexRebuild.MemoryBudgetForTest = 0;
        ElementIdWriteBack.PairBufferEntriesForTest = 0;
        SortedIndexRebuild.AfterSpillForTest = null;
        SortedIndexRebuild.ClustersPerPartitionForTest = 0;
        RebuildWorkers.ProgressCadenceForTest = TimeSpan.Zero;
        DatabaseEngine.RecoveryBatchSize = DatabaseEngine.DefaultRecoveryBatchSize;
        DatabaseEngine.RecoveryBatchPointProbe = null;
    }

    /// <summary>
    /// Design §12: an open that rebuilds logs one start line and one done line per archetype, and a progress line each time the coordinator's wait passes
    /// the cadence; the stats carry each phase's time and the data-file pages the passes read and wrote.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void AnOpenThatRebuilds_LogsItsStartProgressAndDone()
    {
        Seed();
        SortedIndexRebuild.StageEntriesForTest = 500;
        DatabaseEngine.RecoveryBatchSize = 64;
        RebuildWorkers.ProgressCadenceForTest = TimeSpan.FromMilliseconds(1);
        var log = new LogCapture();

        // The first build batch point waits for a progress line: the coordinator's join then outlasts the cadence however fast the phase runs (a Join of
        // 1 ms waits a timer tick, ~15 ms, and a small phase can finish inside it).
        var progressSeen = false;
        DatabaseEngine.RecoveryBatchPointProbe = (pass, _) =>
        {
            if (pass == DatabaseEngine.RecoveryPass.IndexBuild && !progressSeen)
            {
                progressSeen = log.WaitFor(LogLevel.Information, "Index rebuild of 'SrArch', ", TimeSpan.FromSeconds(10));
            }
        };
        using var scope = Provider(64UL << 20, logging: b => b.AddProvider(log).SetMinimumLevel(LogLevel.Information)).CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SrRow>();
        dbe.InitializeArchetypes();
        DatabaseEngine.RecoveryBatchPointProbe = null;

        var stats = dbe.LastOpenIndexRebuild;
        Assert.Multiple(() =>
        {
            Assert.That(progressSeen, Is.True, "a progress line while a build worker waited");
            Assert.That(log.Count(LogLevel.Information, "Index rebuild of 'SrArch': "), Is.EqualTo(1), "one start line");
            Assert.That(log.Count(LogLevel.Information, "4 trees (2 multi-value)"), Is.EqualTo(1), "the start line names the trees");
            Assert.That(log.Count(LogLevel.Information, "Index rebuild of 'SrArch' done in "), Is.EqualTo(1), "one done line");
            Assert.That(log.Count(LogLevel.Information, $"{stats.Entries} entries"), Is.EqualTo(1), "the done line counts the entries");
            Assert.That(log.Count(LogLevel.Information, "Index rebuild of 'SrArch', "), Is.GreaterThan(0), "progress lines, at the cadence");
            Assert.That(stats.StageMs, Is.GreaterThan(0), "the stage's time");
            Assert.That(stats.BuildMs, Is.GreaterThan(0), "the build's time");
            Assert.That(stats.WriteBackMs, Is.GreaterThan(0), "the write-back's time");
            Assert.That(stats.DataFilePagesWritten, Is.GreaterThan(0), "the pages the passes wrote");
        });
    }

    /// <summary>
    /// Design §12: the rebuild's spans reach a trace — one per archetype, per stage partition, per spilled run, per tree and for the write-back, each
    /// naming the archetype and carrying its entries.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void TheRebuild_ShowsInATrace()
    {
        Seed();
        SortedIndexRebuild.StageEntriesForTest = 500;
        SortedIndexRebuild.ClustersPerPartitionForTest = 8;
        var provider = Provider(64UL << 20, configure: o => o.IndexRebuild.Workers = 2);
        var profilerNode = provider.GetRequiredService<IResourceRegistry>().Profiler;
        using var observer = new TraceRingObserver(profilerNode, captureRawBytes: true);
        TyphonProfiler.AttachExporter(observer);
        TyphonProfiler.Start(profilerNode, new ProfilerSessionMetadata(systems: [], archetypes: [], componentTypes: [], workerCount: 0,
            baseTickRate: 60.0f, startTimestamp: System.Diagnostics.Stopwatch.GetTimestamp(), stopwatchFrequency: System.Diagnostics.Stopwatch.Frequency,
            startedUtc: DateTime.UtcNow));
        FreeDeadThreadsSlots();
        IndexRebuildStats stats;
        ushort archetypeId;

        // The profiler drains every slot, earlier tests' rebuilds of this archetype included: only spans that began after this point are this open's.
        var since = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            using var scope = provider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<SrRow>();
            dbe.InitializeArchetypes();
            stats = dbe.LastOpenIndexRebuild;
            archetypeId = (ushort)ArchetypeRegistry.GetMetadata<SrArch>().ArchetypeId;
        }
        finally
        {
            TyphonProfiler.Stop();
        }

        var spans = new List<DataIndexRebuildEventDto>();
        try
        {
            foreach (var (_, bytes) in observer.GetRecords())
            {
                // Decoded at one tick per microsecond, the timestamp reads in raw Stopwatch ticks.
                if (TraceEventDecoder.Decode(bytes, 0, 1) is DataIndexRebuildEventDto span && span.ArchetypeId == archetypeId && span.TimestampUs >= since)
                {
                    spans.Add(span);
                }
            }
        }
        finally
        {
            TyphonProfiler.ResetForTests();   // the profiler is process-wide: a decode that throws must not leave it to the next test
        }
        Assert.Multiple(() =>
        {
            Assert.That(spans.Count(s => s.Phase == (byte)IndexRebuildPhase.Archetype), Is.EqualTo(1), "one span for the archetype");
            Assert.That(spans.Single(s => s.Phase == (byte)IndexRebuildPhase.Archetype).Entries, Is.EqualTo(stats.Entries), "with its entries");
            Assert.That(spans.Where(s => s.Phase == (byte)IndexRebuildPhase.Stage).Select(s => s.Item), Is.EquivalentTo(new[] { 0, 1 }),
                "one per stage partition");
            Assert.That(spans.Count(s => s.Phase == (byte)IndexRebuildPhase.Spill), Is.EqualTo(stats.Runs), "one per spilled run");
            Assert.That(spans.Where(s => s.Phase == (byte)IndexRebuildPhase.Build).Select(s => s.Item), Is.EquivalentTo(new[] { 0, 1, 2, 3 }),
                "one per tree");
            Assert.That(spans.Count(s => s.Phase == (byte)IndexRebuildPhase.WriteBack), Is.EqualTo(1), "one for the write-back");
        });
    }

    // Spans claim a profiler thread slot even with no session running, and a dead thread's slot is freed only once its finalizer has run and a running
    // consumer has drained it. The worker threads of this fixture's earlier rebuilds can hold all 256, and this open's workers would then trace nothing.
    private static void FreeDeadThreadsSlots()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        for (var i = 0; i < ThreadSlotRegistry.HighWaterMark; i++)
        {
            while (ThreadSlotRegistry.GetSlotState(i) == (int)SlotState.Retiring)
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the profiler's consumer frees a retiring slot");
                Thread.Yield();
            }
        }
    }

    /// <summary>Keeps every message at Information and above, for tests that assert a line was logged.</summary>
    private sealed class LogCapture : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Text)> _lines = [];

        /// <summary>Waits until a line at <paramref name="level"/> containing <paramref name="fragment"/> arrives; false after <paramref name="timeout"/>.</summary>
        public bool WaitFor(LogLevel level, string fragment, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            lock (_lines)
            {
                while (Count(level, fragment) == 0)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !System.Threading.Monitor.Wait(_lines, left))
                    {
                        return Count(level, fragment) > 0;
                    }
                }

                return true;
            }
        }

        public int Count(LogLevel level, string fragment)
        {
            lock (_lines)
            {
                return _lines.Count(l => l.Level == level && l.Text.Contains(fragment, StringComparison.Ordinal));
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capturing(this);

        public void Dispose()
        {
        }

        private sealed class Capturing(LogCapture owner) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (owner._lines)
                {
                    owner._lines.Add((logLevel, formatter(state, exception)));
                    System.Threading.Monitor.PulseAll(owner._lines);
                }
            }
        }
    }

    private static bool IsIndexPass(DatabaseEngine.RecoveryPass pass)
        => pass is DatabaseEngine.RecoveryPass.IndexStage or DatabaseEngine.RecoveryPass.IndexBuild or DatabaseEngine.RecoveryPass.IndexWriteBack;

    /// <summary>
    /// Phase 5 (design §D8): the stage's partitions, one build worker per tree and the write-back worker give the same trees and the same element ids
    /// whatever the worker count, spilled runs and pair runs included; and every batch point the open counted in the index passes is one a worker made.
    /// </summary>
    [TestCase(1, false)]
    [TestCase(2, false)]
    [TestCase(4, false)]
    [TestCase(8, false)]
    [TestCase(4, true)]
    [CancelAfter(60_000)]
    [VerifiesRule("RB-09")]
    public void AnyWorkerCount_RebuildsTheSameTrees(int workers, bool multiPass)
    {
        var ids = Seed();
        SortedIndexRebuild.StageEntriesForTest = multiPass ? 200 : 500;
        SortedIndexRebuild.MergeReadBytesForTest = multiPass ? 4 * RebuildTempFile.BlockSize : 0;
        SortedIndexRebuild.ClustersPerPartitionForTest = 8;
        ElementIdWriteBack.PairBufferEntriesForTest = 300;
        DatabaseEngine.RecoveryBatchSize = 256;
        var indexBatchPoints = 0;
        DatabaseEngine.RecoveryBatchPointProbe = (pass, _) =>
        {
            if (IsIndexPass(pass))
            {
                indexBatchPoints++;
            }
        };

        using var scope = Provider(64UL << 20, configure: o => o.IndexRebuild.Workers = workers).CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SrRow>();
        dbe.InitializeArchetypes();
        DatabaseEngine.RecoveryBatchPointProbe = null;

        var stats = dbe.LastOpenIndexRebuild;
        Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: a crash open");
        Assert.Multiple(() =>
        {
            Assert.That(stats.Partitions, Is.EqualTo(workers), "premise: the stage ran on one partition per worker");
            Assert.That(stats.BuildWorkers, Is.EqualTo(Math.Min(workers, 4)), "premise: one build worker per tree, up to the workers");
            Assert.That(stats.Runs, Is.GreaterThan(4 * 3), "premise: the trees spilled runs");
            Assert.That(stats.ElementIdRuns, Is.GreaterThan(2), "premise: the element-id pairs spilled runs");
            Assert.That(stats.UniqueConflicts, Is.Zero);
            Assert.That(stats.FullLeafTrees, Is.EqualTo(2), "the leaf fill decided across partitions as in one");
            Assert.That(indexBatchPoints, Is.EqualTo(stats.WorkerBatchPoints).And.GreaterThan(3), "every index batch point is a worker's");
            Assert.That(stats.ExtraMergePasses, multiPass ? Is.GreaterThan(0) : Is.Zero, "premise: merged down, side by side, or not");
        });

        AssertTreesAgreeWithData(dbe, Count, $"after a crash open with {workers} workers");
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            foreach (var id in ids)
            {
                t.Destroy(id);
            }

            Assert.That(t.Commit(), Is.True);
        }

        AssertTreesAgreeWithData(dbe, 0, "after destroying every entity: each removed through its tail's element id");
    }

    /// <summary>
    /// D2 across partitions: a key that ascends within each stage partition but drops where the second begins is not location-ascending, and its tree keeps
    /// the leaf fill; the key that rises throughout is built full. Each partition checks only its own keys; the barrier checks each one's first key against
    /// the one before's last.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("IXB-01")]
    public void AKeyDroppingBetweenPartitions_IsNotAscending()
    {
        int n;
        using (var scope = Provider(64UL << 20, deleteFile: true).CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<SrHalves>();
            dbe.InitializeArchetypes();

            // Whole clusters, an even number of them: the two partitions split the sorted clusters in halves, and the spawns fill them in order.
            var clusterSize = dbe._archetypeStates[ArchetypeRegistry.GetMetadata<SrHalvesArch>().ArchetypeId].ClusterState.Layout.ClusterSize;
            n = clusterSize * 2 * (2_000 / (clusterSize * 2) + 1);
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < n; i++)
                {
                    t.Spawn<SrHalvesArch>(SrHalvesArch.Row.Set(new SrHalves { Rising = i, Halves = i < n / 2 ? n + i : i - n / 2 }));
                }

                Assert.That(t.Commit(), Is.True);
            }

            dbe.SimulateUncleanShutdownForTest = true;
            dbe.Dispose();
        }

        SortedIndexRebuild.ClustersPerPartitionForTest = 1;
        using (var scope = Provider(64UL << 20, configure: o => o.IndexRebuild.Workers = 2).CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<SrHalves>();
            dbe.InitializeArchetypes();
            var state = dbe._archetypeStates[ArchetypeRegistry.GetMetadata<SrHalvesArch>().ArchetypeId].ClusterState;
            var stats = dbe.LastOpenIndexRebuild;
            Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: a crash open");
            // Premise: in location order the key drops once, at the first slot of the cluster the second partition starts with — so each partition
            // ascends on its own and only the check across them sees the drop.
            var clusters = new int[state.ActiveClusterCount];
            Array.Copy(state.ActiveClusterIds, clusters, clusters.Length);
            Array.Sort(clusters);
            var layout = state.Layout;
            var halvesField = Array.Find(state.IndexSlots[0].Fields, field => field.FieldOffset == sizeof(long));
            var halvesOffset = layout.ComponentOffset(state.IndexSlots[0].Slot) + halvesField.FieldOffset;
            var componentSize = layout.ComponentSize(state.IndexSlots[0].Slot);
            var drops = new List<int>();
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                var scan = state.ClusterSegment.CreateChunkAccessor();
                try
                {
                    var previousKey = long.MinValue;
                    foreach (var chunkId in clusters)
                    {
                        var clusterBase = scan.GetChunkAddress(chunkId);
                        var occupancy = *(ulong*)clusterBase;
                        while (occupancy != 0)
                        {
                            var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                            occupancy &= occupancy - 1;
                            var key = *(long*)(clusterBase + halvesOffset + slot * componentSize);
                            if (key < previousKey)
                            {
                                drops.Add(chunkId * 64 + slot);
                            }

                            previousKey = key;
                        }
                    }
                }
                finally
                {
                    scan.Dispose();
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(state.ActiveClusterCount, Is.EqualTo(n / state.Layout.ClusterSize), "premise: whole clusters");
                Assert.That(stats.Partitions, Is.EqualTo(2), "premise: two partitions");
                Assert.That(drops, Is.EqualTo(new[] { clusters[clusters.Length / 2] * 64 }), "premise: one drop, where the second partition begins");
                Assert.That(stats.FullLeafTrees, Is.EqualTo(1), "only the key rising throughout fills its leaves");
            });

            IndexDataOracle.AssertIndexAgreesWithData(dbe, ArchetypeRegistry.GetMetadata<SrHalvesArch>(), "the trees two partitions staged");
        }
    }

    /// <summary>
    /// A throw on one build worker fails the open with that exception (design §D10, §9.9): the other workers stop at their next batch point, every thread
    /// exits, their epoch slots go back, and the next open rebuilds.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("RB-09")]
    public void AWorkerThrow_FailsTheOpenWithIt_EveryWorkerExits_AndTheNextOpenRebuilds()
    {
        Seed();
        SortedIndexRebuild.StageEntriesForTest = 500;
        DatabaseEngine.RecoveryBatchSize = 64;
        var injected = new InvalidOperationException("a build worker fails");
        var builds = 0;
        DatabaseEngine.RecoveryBatchPointProbe = (pass, _) =>
        {
            if (pass == DatabaseEngine.RecoveryPass.IndexBuild && ++builds == 3)
            {
                throw injected;
            }
        };

        EpochManager epochs;
        int baseline;
        using (var scope = Provider(64UL << 20, configure: o => o.IndexRebuild.Workers = 4).CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<SrRow>();
            epochs = dbe.EpochManager;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            baseline = epochs.ActiveSlotCount;

            var thrown = Assert.Catch(() => dbe.InitializeArchetypes(), "the open whose worker failed");
            var chain = new List<Exception>();
            for (var e = thrown; e != null; e = e.InnerException)
            {
                chain.Add(e);
            }

            Assert.That(chain, Does.Contain(injected), "the open failed with the worker's own exception");
            Assert.That(dbe.LastOpenIndexRebuild.BuildWorkers, Is.EqualTo(4), "premise: four builders, one per tree");

            // Each other builder stops at its next batch point: at most one each was already past the abort check. Run on, each would make a batch
            // point every 64 entries of its 6 000.
            Assert.That(builds, Is.LessThanOrEqualTo(3 + 3), "the other builders stopped at their next batch point");
            Assert.That(RebuildWorkers.LiveThreadsForTest, Is.Zero, "every worker thread exited");
        }

        // Once the engine is gone too, its own threads with it: the slots any thread of the failed open claimed all went back.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.That(epochs.ActiveSlotCount, Is.LessThanOrEqualTo(baseline), "the workers' epoch slots went back");

        DatabaseEngine.RecoveryBatchPointProbe = null;
        using var scope2 = Provider(64UL << 20).CreateScope();
        using var reopened = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        reopened.RegisterComponentFromAccessor<SrRow>();
        reopened.InitializeArchetypes();
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the failed open closed as a crash");
        AssertTreesAgreeWithData(reopened, Count, "after the open that rebuilt again");
    }

    [TestCase(Mode.InMemory)]
    [TestCase(Mode.Spilled)]
    [TestCase(Mode.MultiPass)]
    [TestCase(Mode.PairsSpilled)]
    [TestCase(Mode.KeptNearTheBudget)]
    [CancelAfter(60_000)]
    [VerifiesRule("RB-09")]
    public void ACrashOpen_RebuildsEveryTreeKind(Mode mode)
    {
        var ids = Seed();

        switch (mode)
        {
            case Mode.Spilled:
                SortedIndexRebuild.StageEntriesForTest = 500;
                break;
            case Mode.MultiPass:
                SortedIndexRebuild.StageEntriesForTest = 200;
                SortedIndexRebuild.MergeReadBytesForTest = 4 * RebuildTempFile.BlockSize;
                break;
            case Mode.PairsSpilled:
                SortedIndexRebuild.StageEntriesForTest = 700;
                ElementIdWriteBack.PairBufferEntriesForTest = 100;
                break;
            case Mode.KeptNearTheBudget:
                // The stage holds every key without spilling, in buffers that leave the build less than its least reserve.
                SortedIndexRebuild.MemoryBudgetForTest = 5L << 19;
                break;
        }

        var budget = mode == Mode.KeptNearTheBudget ? 5L << 19 : new IndexRebuildOptions().MemoryBudgetBytes;

        using var scope = Provider(64UL << 20).CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SrRow>();
        dbe.InitializeArchetypes();

        var stats = dbe.LastOpenIndexRebuild;
        Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: a crash open");
        Assert.That(stats, Is.Not.Null, "premise: the sorted rebuild ran");
        Assert.Multiple(() =>
        {
            Assert.That(dbe.LastOpenClusterIndexRebuildCount, Is.EqualTo(1));
            Assert.That(stats.Trees, Is.EqualTo(4));
            Assert.That(stats.UniqueConflicts, Is.Zero);
            Assert.That(stats.FullLeafTrees, Is.EqualTo(2), "the two multi-value trees fill their leaves; the two shuffled unique ones leave room (D2)");
            Assert.That(stats.PeakNativeBytes, Is.LessThanOrEqualTo(budget), "within the budget");
            switch (mode)
            {
                case Mode.InMemory:
                    Assert.That(stats.InMemoryTrees, Is.EqualTo(4), "premise: nothing spilled");
                    Assert.That(stats.TempBytesWritten, Is.Zero, "no temp file when nothing spills");
                    break;
                case Mode.Spilled:
                    Assert.That(stats.Runs, Is.GreaterThan(4 * 10), "premise: every tree spilled many runs");
                    Assert.That(stats.ExtraMergePasses, Is.Zero);
                    Assert.That(stats.TempBytesRead, Is.EqualTo(stats.TempBytesWritten), "every byte written is read back once");
                    break;
                case Mode.MultiPass:
                    Assert.That(stats.ExtraMergePasses, Is.GreaterThan(0), "premise: the merge needed extra passes");
                    break;
                case Mode.PairsSpilled:
                    Assert.That(stats.ElementIdRuns, Is.GreaterThan(4), "premise: the element-id pairs spilled runs");
                    break;
                case Mode.KeptNearTheBudget:
                    Assert.That(stats.Runs, Is.EqualTo(4), "premise: the stage spilled nothing on its own");
                    Assert.That(stats.InMemoryTrees, Is.LessThan(4), "kept buffers spilled at the barrier to leave the build its reserve");
                    break;
            }
        });

        AssertTreesAgreeWithData(dbe, Count, "after the crash open");

        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            foreach (var id in ids)
            {
                t.Destroy(id);
            }

            Assert.That(t.Commit(), Is.True);
        }

        AssertTreesAgreeWithData(dbe, 0, "after destroying every entity: each removed through its tail's element id");
    }

    [TestCase(49, false)]
    [TestCase(50, true)]
    [TestCase(100, true)]
    [TestCase(101, false)]
    public void LeafFillPercent_IsValidatedWithin50To100(int percent, bool valid)
    {
        var failures = new List<string>();
        new IndexRebuildOptions { LeafFillPercent = percent }.Validate(failures);
        Assert.That(failures, valid ? Is.Empty : Has.One.Contains("LeafFillPercent"));
    }

    [TestCase(-1, false)]
    [TestCase(0, true)]
    [TestCase(64, true)]
    [TestCase(65, false)]
    public void Workers_IsValidatedWithin0To64(int workers, bool valid)
    {
        var failures = new List<string>();
        new IndexRebuildOptions { Workers = workers }.Validate(failures);
        Assert.That(failures, valid ? Is.Empty : Has.One.Contains("Workers"));
    }

    /// <summary>Design §D8: the workers asked for, min(ProcessorCount, 8) when 0, and never more than one per 256 cache pages, one at least.</summary>
    [TestCase(4, 8192, 4)]
    [TestCase(64, 8192, 32)]
    [TestCase(8, 1024, 4)]
    [TestCase(8, 100, 1)]
    public void TheWorkers_AreCappedByTheCache(int asked, int cachePages, int expected)
        => Assert.That(DatabaseEngine.IndexRebuildWorkers(new IndexRebuildOptions { Workers = asked }, cachePages), Is.EqualTo(expected));

    [Test]
    public void ZeroWorkers_MeansTheProcessorsUpTo8()
        => Assert.That(DatabaseEngine.IndexRebuildWorkers(new IndexRebuildOptions(), 1 << 20), Is.EqualTo(Math.Min(System.Environment.ProcessorCount, 8)));

    /// <summary>
    /// D2: a unique tree whose keys ascend with location — the shape of a sequence or a timestamp — gets full leaves, since its inserts land at the right
    /// edge; one whose keys come in any other order gets <see cref="IndexRebuildOptions.LeafFillPercent"/>, room for inserts between them. The ascent is
    /// judged across every staged buffer, so a spilled build decides as an in-memory one does.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(60_000)]
    [VerifiesRule("IXB-01")]
    public void UniqueKeysAscendingWithLocation_GetFullLeaves(bool spilled)
    {
        const int n = 5_000;
        using (var scope = Provider(64UL << 20, deleteFile: true).CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<SrThreeKeys>();
            dbe.InitializeArchetypes();
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < n; i++)
                {
                    // A ascends with the spawn order, B descends, C is a sawtooth whose teeth are the spilled build's staged buffers (400 entries):
                    // each buffer ascends on its own, and only the check across buffers sees C drop.
                    t.Spawn<SrThreeKeysArch>(SrThreeKeysArch.Row.Set(new SrThreeKeys { A = i, B = -i, C = i % 400 * 100_000L + i / 400 }));
                }

                Assert.That(t.Commit(), Is.True);
            }

            dbe.SimulateUncleanShutdownForTest = true;
            dbe.Dispose();
        }

        if (spilled)
        {
            SortedIndexRebuild.StageEntriesForTest = 400;
        }

        using (var scope = Provider(64UL << 20).CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<SrThreeKeys>();
            dbe.InitializeArchetypes();
            var stats = dbe.LastOpenIndexRebuild;
            Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: a crash open");
            Assert.Multiple(() =>
            {
                Assert.That(stats.Trees, Is.EqualTo(3));
                Assert.That(stats.Runs, spilled ? Is.GreaterThan(3 * 5) : Is.EqualTo(3), "premise: spilled or not as the case says");
                Assert.That(stats.FullLeafTrees, Is.EqualTo(1), "only the ascending tree fills its leaves");
            });

            IndexDataOracle.AssertIndexAgreesWithData(dbe, ArchetypeRegistry.GetMetadata<SrThreeKeysArch>(), "the trees the crash open rebuilt");
        }
    }

    /// <summary>
    /// The point of #1588: a crash open whose trees are larger than its cache writes each index page about once, in a memory budget the data outgrows.
    /// The random-order inserts of the old rebuild evicted a dirty page on nearly every insert once the trees outgrew the cache, and wrote it again on the
    /// next one.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    [VerifiesRule("RB-09")]
    public void ACrashOpenBeyondTheCache_WritesEachIndexPageAboutOnce()
    {
        const int n = 160_000;
        const long budget = 3L << 20;
        const int batch = 10_000;
        using (var scope = Provider(256UL << 20, deleteFile: true).CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<SrThreeKeys>();
            dbe.InitializeArchetypes();
            for (var s = 0; s < n; s += batch)
            {
                using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
                for (var i = s; i < s + batch; i++)
                {
                    // Three unrelated permutations of the entity number: every tree's insert order is random.
                    t.Spawn<SrThreeKeysArch>(SrThreeKeysArch.Row.Set(new SrThreeKeys
                    {
                        A = i * 7919L % 1_000_003, B = i * 104_729L % 1_000_033, C = i * 15_485_863L % 1_000_037,
                    }));
                }

                Assert.That(t.Commit(), Is.True);
            }

            // Everything checkpointed first, so the crash open's replay window is empty and its cost is the rebuild's.
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(30)), Is.True, "premise: checkpointed");
            dbe.SimulateUncleanShutdownForTest = true;
            dbe.Dispose();
        }

        SortedIndexRebuild.MemoryBudgetForTest = budget;
        using (var scope = Provider(PagedMMF.MinimumCacheSize).CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            var written = new List<int>();
            dbe.MMF.DataFileWriteProbe = (page, count) =>
            {
                lock (written)
                {
                    for (var p = 0; p < count; p++)
                    {
                        written.Add(page + p);
                    }
                }
            };

            dbe.RegisterComponentFromAccessor<SrThreeKeys>();
            dbe.InitializeArchetypes();
            dbe.MMF.DataFileWriteProbe = null;

            var meta = ArchetypeRegistry.GetMetadata<SrThreeKeysArch>();
            var indexPages = new HashSet<int>(dbe._archetypeStates[meta.ArchetypeId].ClusterState.IndexSegment.Pages.ToArray());
            var indexWrites = 0;
            foreach (var page in written)
            {
                indexWrites += indexPages.Contains(page) ? 1 : 0;
            }

            var cachePages = (int)(PagedMMF.MinimumCacheSize / PagedMMF.PageSize);
            Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: a crash open");
            Assert.That(indexPages.Count, Is.GreaterThan(cachePages), "premise: the trees are larger than the cache");
            var stats = dbe.LastOpenIndexRebuild;
            TestContext.Out.WriteLine($"index-segment pages {indexPages.Count}, written during the open {indexWrites}, all data-file writes {written.Count}, "
                + $"runs {stats.Runs}, extra merge passes {stats.ExtraMergePasses}, peak native {stats.PeakNativeBytes}, nodes written {stats.NodesWritten}");
            Assert.Multiple(() =>
            {
                Assert.That(stats.Runs, Is.GreaterThan(3 * 2), "premise: the data outgrew the budget, so every tree spilled runs");
                Assert.That(stats.PeakNativeBytes, Is.LessThanOrEqualTo(budget), "the rebuild's native memory stayed within its budget");
                Assert.That(indexWrites, Is.LessThanOrEqualTo(2.5 * indexPages.Count),
                    $"index-segment page writes during the open: {indexWrites} for {indexPages.Count} pages "
                    + "(bound: the clear's ≤ I_old plus ≤ 1.5 × I_new)");
            });
        }

        SortedIndexRebuild.MemoryBudgetForTest = 0;

        // The oracle walks every cluster in one epoch scope, so it needs a cache that holds them: on the next open, which loads the trees.
        using (var scope = Provider(256UL << 20).CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<SrThreeKeys>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.LastOpenClusterIndexRebuildCount, Is.Zero, "premise: this open loads the rebuilt trees");
            IndexDataOracle.AssertIndexAgreesWithData(dbe, ArchetypeRegistry.GetMetadata<SrThreeKeysArch>(), "the trees the crash open rebuilt");
        }
    }

    /// <summary>
    /// A temp block that reads back other than written fails the open (RB-10): the tree is never built from it. The failed open closes as a crash, and the
    /// next open rebuilds from the clusters with a new temp file.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("RB-10")]
    public void ACorruptedRunBlock_FailsTheOpen_AndTheNextOpenRebuilds()
    {
        Seed();
        string bundle;
        SortedIndexRebuild.StageEntriesForTest = 500;
        var corrupted = 0;
        SortedIndexRebuild.AfterSpillForTest = (file, run) =>
        {
            if (Interlocked.Increment(ref corrupted) == 4)   // the stage partitions spill on their own threads
            {
                file.PatchForTest(run.Offset + RebuildTempFile.HeaderSize + 40, [0xA5, 0x5A]);
            }
        };

        var log = new LogCapture();
        using (var scope = Provider(64UL << 20, logging: b => b.AddProvider(log)).CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            bundle = dbe.MMF.BundleDirectory;
            dbe.RegisterComponentFromAccessor<SrRow>();
            var ex = Assert.Catch(() => dbe.InitializeArchetypes(), "the open fails");
            var cause = ex;
            while (cause != null && cause is not IndexRebuildTempCorruptException)
            {
                cause = cause.InnerException;
            }

            Assert.That(cause, Is.InstanceOf<IndexRebuildTempCorruptException>(), $"the temp block's check failed it, not something else: {ex}");
            Assert.That(((IndexRebuildTempCorruptException)cause).Check, Does.Contain("payload CRC"));
            Assert.That(log.Count(LogLevel.Error, "failed its check (payload CRC"), Is.EqualTo(1), "the corrupt block logged at Error, once");
            AssertNoTempFile(bundle, "after the failed open");
            dbe.Dispose();
        }

        SortedIndexRebuild.AfterSpillForTest = null;
        using (var scope = Provider(64UL << 20).CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<SrRow>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: the failed open closed as a crash");
            Assert.That(dbe.LastOpenIndexRebuild.Runs, Is.GreaterThan(4), "premise: the rebuild spilled again, to a new file");
            AssertTreesAgreeWithData(dbe, Count, "after the open that followed the failed one");
            AssertNoTempFile(bundle, "after the successful open");
        }
    }

    /// <summary>
    /// The sweep deletes the rebuild temp files a previous process left in the database's own temp directory, and nothing else: with a configured
    /// <see cref="IndexRebuildOptions.TempDirectory"/>, that directory is a subdirectory named after the database, so a file of another program's, or
    /// another database's, beside it is never touched (RB-10).
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("RB-10")]
    public void TheSweep_DeletesOnlyThisDatabasesLeftovers()
    {
        Seed();
        var shared = System.IO.Path.Combine(TestStorage.Root, nameof(TheSweep_DeletesOnlyThisDatabasesLeftovers) + "-shared");
        System.IO.Directory.CreateDirectory(shared);
        var foreign = System.IO.Path.Combine(shared, "ix-7.tmp");
        System.IO.File.WriteAllBytes(foreign, [1]);
        string ours;
        using (var scope = Provider(64UL << 20, configure: o => o.IndexRebuild.TempDirectory = shared).CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            ours = SortedIndexRebuild.TempDirectoryOf(new IndexRebuildOptions { TempDirectory = shared }, dbe.MMF.BundleDirectory);
            Assert.That(System.IO.Path.GetDirectoryName(ours), Is.EqualTo(shared), "premise: the database's own directory inside the shared one");
            System.IO.Directory.CreateDirectory(ours);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(ours, "ix-99.tmp"), [2]);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(ours, "notes.txt"), [3]);
            dbe.RegisterComponentFromAccessor<SrRow>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: the open rebuilt");
            dbe.Dispose();
        }

        Assert.Multiple(() =>
        {
            Assert.That(System.IO.File.Exists(System.IO.Path.Combine(ours, "ix-99.tmp")), Is.False, "this database's leftover is swept");
            Assert.That(System.IO.File.Exists(System.IO.Path.Combine(ours, "notes.txt")), Is.True, "a file that is not a rebuild temp file stays");
            Assert.That(System.IO.File.Exists(foreign), Is.True, "a file beside the database's directory is never touched");
        });
    }

    private static void AssertNoTempFile(string bundle, string when)
    {
        var directory = System.IO.Path.Combine(bundle, SortedIndexRebuild.TempDirectoryName);
        Assert.That(System.IO.Directory.Exists(directory) ? System.IO.Directory.GetFiles(directory) : [], Is.Empty, $"no temp file {when}");
    }

    private List<EntityId> Seed()
    {
        var ids = new List<EntityId>(Count);
        using var scope = Provider(64UL << 20, deleteFile: true).CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SrRow>();
        dbe.InitializeArchetypes();
        for (var s = 0; s < Count; s += 1_000)
        {
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = s; i < s + 1_000; i++)
            {
                ids.Add(t.Spawn<SrArch>(SrArch.Row.Set(RowOf(i))));
            }

            Assert.That(t.Commit(), Is.True);
        }

        dbe.SimulateUncleanShutdownForTest = true;
        dbe.Dispose();
        return ids;
    }

    /// <summary>Distinct unique keys in an order unrelated to the spawn order, negative ones included; groups and tags repeat; one tag in five is 0.</summary>
    private static SrRow RowOf(int i) => new()
    {
        Unique = (long)(i * 7919L % 1_000_003) - 500_000,
        Name = (String64)$"name-{(i * 4099) % 1_000_003:D7}",
        Group = i % Groups - Groups / 2,
        Tag = i % 5,
    };

    private static void AssertTreesAgreeWithData(DatabaseEngine dbe, int live, string when)
    {
        var meta = ArchetypeRegistry.GetMetadata<SrArch>();
        IndexDataOracle.AssertIndexAgreesWithData(dbe, meta, when);

        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var fields = clusterState.IndexSlots[0].Fields;
        Assert.That(fields, Has.Length.EqualTo(4), "premise");
        var name = fields[1].Index;
        Assert.That(name.EntryCount, Is.EqualTo(live), $"String64 tree entries {when}");

        // The String64 keys the oracle skips: each occupied slot's name resolves to that slot, and the entry count says there is nothing else.
        var layout = clusterState.Layout;
        var nameOffset = layout.ComponentOffset(clusterState.IndexSlots[0].Slot) + fields[1].FieldOffset;
        var componentSize = layout.ComponentSize(clusterState.IndexSlots[0].Slot);
        var accessor = name.Segment.CreateChunkAccessor();
        var clusters = clusterState.ClusterSegment.CreateChunkAccessor();
        var checkedNames = 0;
        try
        {
            for (var c = 0; c < clusterState.ActiveClusterCount; c++)
            {
                var chunkId = clusterState.ActiveClusterIds[c];
                var clusterBase = clusters.GetChunkAddress(chunkId);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    var key = *(String64*)(clusterBase + nameOffset + slot * componentSize);
                    var found = name.TryGet(&key, ref accessor);
                    Assert.That(found.IsSuccess, Is.True, $"String64 key of the slot {chunkId}:{slot} {when}");
                    Assert.That(found.Value, Is.EqualTo(chunkId * 64 + slot), $"String64 key resolves to its own slot {when}");
                    checkedNames++;
                }
            }
        }
        finally
        {
            clusters.Dispose();
            accessor.Dispose();
        }

        Assert.That(checkedNames, Is.EqualTo(live), $"premise: every live entity's name checked {when}");

        Assert.That(MultiValues(fields[2].Index), Is.EqualTo(live), $"group entries {when}");
        Assert.That(MultiValues(fields[3].Index), Is.EqualTo(live - live / 5), $"sparse tag entries, zeros skipped, {when}");
    }

    private static int MultiValues(BTreeBase<PersistentStore> index)
    {
        var count = 0;
        if (index is BTree<int, PersistentStore> ints)
        {
            using var e = ints.EnumerateRangeMultiple(int.MinValue, int.MaxValue);
            while (e.MoveNextKey())
            {
                do
                {
                    count += e.CurrentValues.Length;
                }
                while (e.NextChunk());
            }

            return count;
        }

        using var le = ((BTree<long, PersistentStore>)index).EnumerateRangeMultiple(long.MinValue, long.MaxValue);
        while (le.MoveNextKey())
        {
            do
            {
                count += le.CurrentValues.Length;
            }
            while (le.NextChunk());
        }

        return count;
    }
}
