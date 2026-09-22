using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Schema.UnitTest.FesUnit", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct FesUnit
{
    public long Stamp;
    public int Seq;
    public int Pad;
}

[Archetype]
internal class FesUnitArch : Archetype<FesUnitArch>
{
    public static readonly Comp<FesUnit> C = Register<FesUnit>();
}

/// <summary>The same shape under Checkpoint durability: the fence emits nothing for it, so at W ≥ 2 its Finalize head runs and no item follows.</summary>
[Archetype(ClusterDurability = ClusterDurability.Checkpoint)]
internal class FesCkptArch : Archetype<FesCkptArch>
{
    public static readonly Comp<FesUnit> C = Register<FesUnit>();
}

/// <summary>
/// #889 lead G. Finalize's WAL emit runs as <see cref="FenceWorkKind.FinalizeEmitSlice"/> items over dirty-word ranges when the archetype is large and
/// the runtime has two or more workers; the head runs once on the driver. What the log must contain is not allowed to depend on that: the same world, the
/// same writes and the same ticks at W = 1 (one atomic Finalize item) and at W = 8 (head + slices) must put the same fence records on disk — the same
/// entities, the same slots, the same bytes — with only their LSN order free to differ. Recovery applies fence records by LSN without a commit marker, so
/// equal multisets recover to equal states.
/// </summary>
/// <remarks>
/// No spatial index and no migrations on purpose: this fixture asserts on the emit alone, and the Migrate / index / AABB phases are empty, so it stays clear
/// of #887, which makes the parallel fence at W = 8 disagree with the serial one on a MOVING world. The dirty set here is decided by the writes, not by
/// crossings.
/// </remarks>
[TestFixture]
[NonParallelizable]
internal sealed class FinalizeEmitSliceEquivalenceTests
{
    private const int Clusters = 320;                 // dirty words in three 128-word ranges ≥ FinalizeSliceMinRanges, so the W = 8 arm slices
    private const int SlotsPerCluster = 64;
    private const int EntityCount = Clusters * SlotsPerCluster;
    private const long WriteTick = 1;

    /// <summary>The scheduler numbers a tick by the ticks completed before it, so the write tick's fence stamps its records with this TSN.</summary>
    private const long WriteFenceTsn = WriteTick - 1;

    private readonly List<ServiceProvider> _providers = [];
    private string _root;

    private static ushort ArchetypeId => Archetype<FesUnitArch>.Metadata.ArchetypeId;

    private static ushort CkptArchetypeId => Archetype<FesCkptArch>.Metadata.ArchetypeId;

    [SetUp]
    public void Setup()
    {
        var name = TestContext.CurrentContext.Test.Name;
        foreach (var c in new[] { '(', ')', ',', ' ', '"' })
        {
            name = name.Replace(c, '_');
        }

        _root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(FinalizeEmitSliceEquivalenceTests), name);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var p in _providers)
        {
            p.Dispose();
        }

        _providers.Clear();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
            // A handle the OS has not released yet; the next run's Setup recreates the directory.
        }
    }

    /// <summary>One file-backed engine per arm, each in its own directory, so the two logs can be scanned side by side.</summary>
    private (ServiceProvider Provider, string WalDir) CreateArm(string arm)
    {
        var dbDir = Path.Combine(_root, arm, "db");
        var walDir = Path.Combine(_root, arm, "wal");
        Directory.CreateDirectory(dbDir);
        Directory.CreateDirectory(walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b =>
            {
                b.AddSimpleConsole();
                b.SetMinimumLevel(LogLevel.Warning);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = $"Fes_{arm}";
                opts.DatabaseDirectory = dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 8;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 8 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
            });

        var provider = services.BuildServiceProvider();
        provider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _providers.Add(provider);
        return (provider, walDir);
    }

    private sealed class ArmOutcome
    {
        public readonly List<(int Start, int Count)> Slices = [];
        public int AtomicItems;
        public long[] DirtyWordsAfterWriteTick;
        public long LastTickFenceLsn;

        /// <summary>Every fence phase's skip reason at the end of the write tick's fence, by system name.</summary>
        public readonly Dictionary<string, SkipReason> FencePhaseOutcome = [];

        /// <summary>The write tick's fence from Prep's Prepare to the last chunk end, and Finalize's own span, in Stopwatch ticks.</summary>
        public long FenceWallTicks;
        public long FinalizeSpanTicks;

        /// <summary>What the head leaves behind, for the arms to compare: the dormancy count and the dirty snapshot's populated words.</summary>
        public int SleepingClusters;
        public int SnapshotDirtyWords;
    }

    /// <summary>
    /// Spawns the world, then runs a runtime at <paramref name="workerCount"/> for three ticks: tick 1 rewrites every third entity, and tick 2's user DAG
    /// reads back the Finalize plan tick 1's fence left on the exec system together with the dirty snapshot it worked from.
    /// </summary>
    private ArmOutcome RunArm(string arm, int workerCount, out string walDir, bool checkpoint = false)
    {
        var (provider, dir) = CreateArm(arm);
        walDir = dir;
        var outcome = new ArmOutcome();
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<FesUnit>();
        dbe.InitializeArchetypes();
        var archetypeId = checkpoint ? CkptArchetypeId : ArchetypeId;

        var ids = new EntityId[EntityCount];
        const int batch = 4096;
        for (var start = 0; start < EntityCount; start += batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = start; i < Math.Min(EntityCount, start + batch); i++)
            {
                var v = new FesUnit { Seq = i, Stamp = 1000L + i };
                ids[i] = checkpoint ? tx.Spawn<FesCkptArch>(FesCkptArch.C.Set(v)) : tx.Spawn<FesUnitArch>(FesUnitArch.C.Set(v));
            }

            tx.Commit();
        }

        var ticks = 0;
        TyphonRuntime runtime = null;
        runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Sample", _ =>
            {
                var n = Interlocked.Increment(ref ticks);
                if (n == WriteTick + 1)
                {
                    // Tick 1's fence just ran: its Finalize plan is still the one on the exec system, and its dirty snapshot is still on the state —
                    // tick 2's Prep, which replaces both, runs after this DAG.
                    var plan = runtime.FenceFinalizeExec.PlanForTest;
                    for (var i = 0; i < plan.ItemCount; i++)
                    {
                        ref var item = ref plan.Items[i];
                        if (item.TargetId != archetypeId)
                        {
                            continue;
                        }

                        if (item.Kind == FenceWorkKind.FinalizeEmitSlice)
                        {
                            outcome.Slices.Add((item.SliceStart, item.SliceCount));
                        }
                        else if (item.Kind == FenceWorkKind.ArchetypeFinalize)
                        {
                            outcome.AtomicItems++;
                        }
                    }

                    var cs = dbe._archetypeStates[archetypeId].ClusterState;
                    var bits = cs.FenceDirtyBits;
                    outcome.DirtyWordsAfterWriteTick = bits == null ? [] : (long[])bits.Clone();
                    outcome.FenceWallTicks = runtime.LastFenceWallTicks;
                    outcome.FinalizeSpanTicks = runtime.FenceFinalizeExec.PhaseSpanTicks;
                    outcome.SleepingClusters = cs.SleepingClusterCount;
                    outcome.SnapshotDirtyWords = cs.PreviousTickDirtySnapshot == null ? -1 : cs.PreviousTickDirtySnapshot.Count(w => w != 0);
                }
            });
            dag.CallbackSystem("Write", ctx =>
            {
                if (Volatile.Read(ref ticks) == WriteTick)
                {
                    // Every third entity, so most clusters hold a mix of dirty and clean slots and the slot RANGE the block describes is exercised.
                    for (var i = 0; i < EntityCount; i += 3)
                    {
                        var value = new FesUnit { Seq = -i, Stamp = 7_000_000L + i };
                        if (checkpoint)
                        {
                            ctx.Transaction.OpenMut(ids[i]).Write(FesCkptArch.C) = value;
                        }
                        else
                        {
                            ctx.Transaction.OpenMut(ids[i]).Write(FesUnitArch.C) = value;
                        }
                    }
                }
            }, after: "Sample");
        }, new RuntimeOptions { WorkerCount = workerCount, BaseTickRate = 100, EnableParallelFence = true });

        using (runtime)
        {
            Exception unhandled = null;
            runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);
            // A fence phase whose Prepare throws is recorded in the scheduler's per-system telemetry and every later phase is skipped as DependencyFailed.
            // Since #890 it also reaches UnhandledExceptionCallback and shows up as TickOutcomeReason.FenceFailure, but the telemetry read below is what
            // NAMES the phase, which is what this fixture asserts on. The write tick's outcome is read here at the end of the tick the scheduler numbers
            // WriteTick - 1 (CurrentTickNumber counts completed ticks).
            var inner = runtime.Scheduler.TickEndCallback;
            runtime.Scheduler.TickEndCallback = s =>
            {
                inner(s);
                if (s.CurrentTickNumber == WriteTick - 1)
                {
                    for (var i = 0; i < s.Systems.Length; i++)
                    {
                        if (s.Systems[i].Name.StartsWith("Fence", StringComparison.Ordinal))
                        {
                            outcome.FencePhaseOutcome[s.Systems[i].Name] = s.GetCurrentSystemMetrics(i).SkipReason;
                        }
                    }
                }
            };
            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 3, TimeSpan.FromSeconds(30));
            runtime.Shutdown();
            Assert.That(unhandled, Is.Null, $"the parallel fence must not throw at W={workerCount}. Got: {unhandled}");
            Assert.That(ticks, Is.GreaterThanOrEqualTo(3), "the runtime must have ticked past the write tick and its fence");
            outcome.LastTickFenceLsn = dbe.LastTickFenceLSN;
        }

        // Before #889 an index-less archetype like this one threw inside Migrate's Prepare on every sliced-Prep tick (a null shadow bitmap), and the
        // fence silently ran Prep alone: no Finalize, no WAL emit. That failure never reached the host, so it is asserted from the telemetry.
        Assert.That(outcome.FencePhaseOutcome, Is.Not.Empty, "the write tick's fence outcome must have been captured");
        foreach (var (name, reason) in outcome.FencePhaseOutcome)
        {
            Assert.That(reason, Is.EqualTo(SkipReason.NotSkipped).Or.EqualTo(SkipReason.EmptyInput),
                $"W={workerCount}: fence phase {name} was skipped as {reason} — a phase that failed took every later phase down with it");
        }

        return outcome;
    }

    /// <summary>What recovery would see: every fence-block record of the ticks both arms certainly completed, as (tick, entity, slot, bytes).</summary>
    private static List<string> FenceRecordsOf(string walDir)
        => WalScanner.ScanAll(walDir)
            .Where(r => r.FromFenceBlock && r.Tsn <= WriteFenceTsn + 1)
            .Select(r => $"{r.Tsn}|{r.EntityId:X}|{r.SlotIndex}|{Convert.ToHexString(r.Payload)}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    [Test]
    [CancelAfter(60_000)]
    public void SlicedEmit_PutsTheSameFenceRecordsOnDisk_AsTheAtomicOne()
    {
        var serial = RunArm("serial", workerCount: 1, out var serialWal);
        var sliced = RunArm("sliced", workerCount: 8, out var slicedWal);

        // The arms took the paths this fixture is about.
        Assert.That(serial.AtomicItems, Is.EqualTo(1), "W=1 runs one atomic Finalize item for the archetype");
        Assert.That(serial.Slices, Is.Empty, "and no slices");
        Assert.That(sliced.AtomicItems, Is.Zero, "W=8 must not also run the atomic item — it would sweep dormancy a second time");
        Assert.That(sliced.Slices.Count, Is.GreaterThanOrEqualTo(2), $"a {Clusters}-cluster archetype at W=8 slices its emit");

        // The slices are disjoint, ordered, and between them cover every dirty word of the snapshot the emit worked from.
        var ordered = sliced.Slices.OrderBy(s => s.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            Assert.That(ordered[i].Start, Is.GreaterThanOrEqualTo(ordered[i - 1].Start + ordered[i - 1].Count), $"slices {i - 1} and {i} overlap");
        }

        var dirty = sliced.DirtyWordsAfterWriteTick;
        Assert.That(dirty, Is.Not.Null.And.Not.Empty, "the write tick's dirty snapshot must still be on the state when tick 2's DAG runs");
        Assert.That(dirty.Any(w => w != 0), Is.True, "and it must hold dirty words, or the coverage check below is vacuous");
        for (var w = 0; w < dirty.Length; w++)
        {
            if (dirty[w] != 0)
            {
                Assert.That(ordered.Any(s => w >= s.Start && w < s.Start + s.Count), Is.True, $"dirty word {w} is in no slice");
            }
        }

        // The log: the same records, whatever the width. The spawn is logged by its own commit, so the write tick's fence carries exactly the rewritten
        // entities — which is what makes this a comparison of two logs with something in them. Comparing entity ids across two engines assumes both
        // allocate the same ids for the same spawn order, which two fresh providers with no migrations do; a divergence would fail this loudly, not pass it.
        // (Two scoped engines sharing one provider do NOT — see PrepSliceEquivalenceTests, which compares by domain key for that reason.)
        var serialRecords = FenceRecordsOf(serialWal);
        var slicedRecords = FenceRecordsOf(slicedWal);
        Assert.That(serialRecords.Count, Is.EqualTo((EntityCount + 2) / 3), "sanity: every rewritten entity reached the log as a fence-block record");
        Assert.That(slicedRecords, Is.EqualTo(serialRecords), "the sliced emit must put exactly the atomic emit's records on disk");

        // The wall span covers Finalize in both arms, and Finalize's own span is not zero: the head's serial work is fence time whether or not
        // a chunk follows it.
        Assert.That(serial.FinalizeSpanTicks, Is.GreaterThan(0), "W=1: Finalize ran an item, its span is not zero");
        Assert.That(sliced.FinalizeSpanTicks, Is.GreaterThan(0), "W=8: Finalize ran slices, its span is not zero");
        Assert.That(sliced.FenceWallTicks, Is.GreaterThanOrEqualTo(sliced.FinalizeSpanTicks), "the fence wall ends no earlier than Finalize does");

        // The LSN fold: the runtime's fence watermark is at least the highest LSN any slice published for a tick that certainly completed.
        var slicedMaxLsn = WalScanner.ScanAll(slicedWal).Where(r => r.FromFenceBlock && r.Tsn == WriteFenceTsn).Select(r => r.Lsn).DefaultIfEmpty(0).Max();
        Assert.That(slicedMaxLsn, Is.GreaterThan(0), "sanity: the write tick's fence published records in the sliced arm");
        Assert.That(sliced.LastTickFenceLsn, Is.GreaterThanOrEqualTo(slicedMaxLsn),
            "a slice's highest LSN was lost in the per-chunk fold — the checkpoint would truncate the log below records it needs");
    }

    /// <summary>
    /// The third state: the head ran and found nothing to emit. A Checkpoint-durability archetype emits no fence records, so at W = 8 its head runs on the
    /// driver and the planner must emit NEITHER a slice NOR the atomic item — the atomic item would sweep dormancy a second time (DM-02). "Emit nothing" is
    /// only right if the head alone did everything the atomic item does, so what the head leaves behind is compared with the W = 1 arm's: the dormancy
    /// count and the dirty snapshot. And the phase's span must still be counted, or the fence wall omits the head.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void CheckpointArchetype_HeadRunsAndNothingIsEmitted_AtW8()
    {
        var serial = RunArm("ckpt-serial", workerCount: 1, out var serialWal, checkpoint: true);
        var sliced = RunArm("ckpt-sliced", workerCount: 8, out var slicedWal, checkpoint: true);

        Assert.That(serial.AtomicItems, Is.EqualTo(1), "W=1 runs the atomic item, which returns 0 after its head");
        Assert.That(sliced.AtomicItems, Is.Zero, "W=8: the head ran on the driver; the atomic item would sweep dormancy twice");
        Assert.That(sliced.Slices, Is.Empty, "W=8: nothing to emit, so no slice");

        Assert.That(FenceRecordsOf(serialWal), Is.Empty, "a Checkpoint archetype puts no fence records on disk at W=1");
        Assert.That(FenceRecordsOf(slicedWal), Is.Empty, "nor at W=8");

        Assert.That(sliced.SnapshotDirtyWords, Is.EqualTo(serial.SnapshotDirtyWords).And.GreaterThan(0),
            "the head published the same dirty snapshot the atomic item does — change-filtered dispatch reads it");
        Assert.That(sliced.SleepingClusters, Is.EqualTo(serial.SleepingClusters), "the head swept dormancy exactly once, as the atomic item does (DM-02)");

        Assert.That(sliced.FinalizeSpanTicks, Is.GreaterThan(0), "a phase that dispatched no chunk still spent its Prepare, and the span must say so");
        Assert.That(sliced.FenceWallTicks, Is.GreaterThanOrEqualTo(sliced.FinalizeSpanTicks), "the fence wall includes the head");
    }

    /// <summary>Below two slices' worth of words the atomic item stays, at any width — the same rule Prep's slicing follows.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void SmallWorld_IsNotSliced()
    {
        var was = DatabaseEngine.FinalizeSliceMinRanges;
        try
        {
            // The world is fixed at 320 clusters in three populated ranges; raise the bar past it instead of shrinking the world, which is the switch the
            // harness uses too.
            DatabaseEngine.FinalizeSliceMinRanges = 8;
            var outcome = RunArm("small", workerCount: 8, out _);
            Assert.That(outcome.Slices, Is.Empty, "below the threshold there are no slices");
            Assert.That(outcome.AtomicItems, Is.EqualTo(1), "and the atomic item runs the whole Finalize, head included");
        }
        finally
        {
            DatabaseEngine.FinalizeSliceMinRanges = was;
        }
    }
}
