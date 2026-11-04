using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
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
    }

    protected override void OnTearDown()
    {
        SortedIndexRebuild.StageEntriesForTest = 0;
        SortedIndexRebuild.MergeReadBytesForTest = 0;
        SortedIndexRebuild.MemoryBudgetForTest = 0;
        ElementIdWriteBack.PairBufferEntriesForTest = 0;
        SortedIndexRebuild.AfterSpillForTest = null;
    }

    [TestCase(Mode.InMemory)]
    [TestCase(Mode.Spilled)]
    [TestCase(Mode.MultiPass)]
    [TestCase(Mode.PairsSpilled)]
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
        }

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
            Assert.That(stats.PeakNativeBytes, Is.LessThanOrEqualTo(new IndexRebuildOptions().MemoryBudgetBytes), "within the budget");
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
                + $"runs {stats.Runs}, extra merge passes {stats.ExtraMergePasses}, peak native {stats.PeakNativeBytes}");
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
            if (corrupted++ == 3)
            {
                file.PatchForTest(run.Offset + RebuildTempFile.HeaderSize + 40, [0xA5, 0x5A]);
            }
        };

        using (var scope = Provider(64UL << 20).CreateScope())
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
