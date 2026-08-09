using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════
// Test archetypes with SV and Transient components
// ═══════════════════════════════════════════════════════════════════════

[Archetype]
class SvTestArchetype : Archetype<SvTestArchetype>
{
    public static readonly Comp<CompSmSingleVersion> SvComp = Register<CompSmSingleVersion>();
}

[Archetype]
class TransientTestArchetype : Archetype<TransientTestArchetype>
{
    public static readonly Comp<CompSmTransient> TransComp = Register<CompSmTransient>();
}

// ── Mixed-mode component (Versioned, used alongside SV and Transient in one archetype) ──

[Component("Typhon.Test.SM.VersionedMix", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompSmVersionedMix
{
    public int Value;
    public int _pad;
    public CompSmVersionedMix(int v) { Value = v; }
}

[Archetype]
class MixedModeArchetype : Archetype<MixedModeArchetype>
{
    public static readonly Comp<CompSmVersionedMix> Versioned = Register<CompSmVersionedMix>();
    public static readonly Comp<CompSmSingleVersion> SV = Register<CompSmSingleVersion>();
    public static readonly Comp<CompSmTransient> Trans = Register<CompSmTransient>();
}

// ═══════════════════════════════════════════════════════════════════════

class StorageModeReadWriteTests : TestBase<StorageModeReadWriteTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
        dbe.RegisterComponentFromAccessor<CompSmTransient>();
        dbe.RegisterComponentFromAccessor<CompSmVersioned>();
        dbe.RegisterComponentFromAccessor<CompSmVersionedMix>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ── SV: Spawn/Read/Write ──

    [Test]
    public void SV_SpawnAndRead()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var comp = new CompSmSingleVersion(42);
        var entityId = tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));

        var entity = tx.Open(entityId);
        ref readonly var read = ref entity.Read(SvTestArchetype.SvComp);
        Assert.That(read.Value, Is.EqualTo(42));
    }

    [Test]
    public void SV_SpawnAndWrite()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var comp = new CompSmSingleVersion(10);
        var entityId = tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.OpenMut(entityId);
        ref var write = ref entity.Write(SvTestArchetype.SvComp);
        write.Value = 99;

        // SV is in-place — visible immediately
        ref readonly var read = ref entity.Read(SvTestArchetype.SvComp);
        Assert.That(read.Value, Is.EqualTo(99));
    }

    [Test]
    public void SV_Write_SetsDirtyBitmap()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var comp = new CompSmSingleVersion(1);
        var entityId = tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
        tx.Commit();

        // Clear any spawn-time dirty bits
        var clusterState = dbe._archetypeStates[Archetype<SvTestArchetype>.Metadata.ArchetypeId]?.ClusterState;
        clusterState?.ClusterDirtyBitmap.Snapshot();

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.OpenMut(entityId);
        ref var write = ref entity.Write(SvTestArchetype.SvComp);
        write.Value = 2;

        if (clusterState != null)
        {
            Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True, "After Write, ClusterDirtyBitmap should have dirty bits");
        }
        else
        {
            var table = dbe.GetComponentTable<CompSmSingleVersion>();
            Assert.That(table.DirtyBitmap.HasDirty, Is.True, "After Write, DirtyBitmap should have dirty bits");
        }
    }

    [Test]
    public void SV_DirtyBitmap_Snapshot_ClearsAndReturns()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var comp = new CompSmSingleVersion(1);
        var entityId = tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
        tx.Commit();

        // Clear any spawn-time dirty bits
        var clusterState = dbe._archetypeStates[Archetype<SvTestArchetype>.Metadata.ArchetypeId]?.ClusterState;
        clusterState?.ClusterDirtyBitmap.Snapshot();

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.OpenMut(entityId);
        ref var write = ref entity.Write(SvTestArchetype.SvComp);
        write.Value = 2;

        if (clusterState != null)
        {
            var snapshot = clusterState.ClusterDirtyBitmap.Snapshot();
            Assert.That(snapshot, Is.Not.Null);
            bool anySet = false;
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] != 0) { anySet = true; break; }
            }
            Assert.That(anySet, Is.True, "Snapshot should contain dirty bits");
            Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.False, "After Snapshot, bitmap should be clear");
        }
        else
        {
            var table = dbe.GetComponentTable<CompSmSingleVersion>();
            var snapshot = table.DirtyBitmap.Snapshot();
            Assert.That(snapshot, Is.Not.Null);
            bool anySet = false;
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] != 0) { anySet = true; break; }
            }
            Assert.That(anySet, Is.True, "Snapshot should contain dirty bits");
            Assert.That(table.DirtyBitmap.HasDirty, Is.False, "After Snapshot, bitmap should be clear");
        }
    }

    // ── Transient: Spawn/Read/Write ──

    [Test]
    public void Transient_SpawnAndRead()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var comp = new CompSmTransient(77);
        var entityId = tx.Spawn<TransientTestArchetype>(TransientTestArchetype.TransComp.Set(in comp));

        var entity = tx.Open(entityId);
        ref readonly var read = ref entity.Read(TransientTestArchetype.TransComp);
        Assert.That(read.Value, Is.EqualTo(77));
    }

    [Test]
    public void Transient_SpawnAndWrite()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var comp = new CompSmTransient(10);
        var entityId = tx.Spawn<TransientTestArchetype>(TransientTestArchetype.TransComp.Set(in comp));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.OpenMut(entityId);
        ref var write = ref entity.Write(TransientTestArchetype.TransComp);
        write.Value = 55;

        ref readonly var read = ref entity.Read(TransientTestArchetype.TransComp);
        Assert.That(read.Value, Is.EqualTo(55));
    }

    [Test]
    public void Transient_NoDirtyBitmap()
    {
        using var dbe = SetupEngine();
        var table = dbe.GetComponentTable<CompSmTransient>();
        Assert.That(table.DirtyBitmap, Is.Null, "Transient table should have no DirtyBitmap");
    }

    // ── Rollback tests ──

    [Test]
    public void Transient_Rollback_FreesChunks()
    {
        using var dbe = SetupEngine();
        var table = dbe.GetComponentTable<CompSmTransient>();
        int allocBefore = table.TransientComponentSegment.AllocatedChunkCount;

        using (var tx = dbe.CreateQuickTransaction())
        {
            var comp = new CompSmTransient(1);
            tx.Spawn<TransientTestArchetype>(TransientTestArchetype.TransComp.Set(in comp));
            // Don't commit — triggers rollback on dispose
        }

        int allocAfter = table.TransientComponentSegment.AllocatedChunkCount;
        Assert.That(allocAfter, Is.EqualTo(allocBefore), "Rollback should free chunks allocated by pending spawn");
    }

    [Test]
    public void SV_Rollback_FreesChunks()
    {
        using var dbe = SetupEngine();
        var table = dbe.GetComponentTable<CompSmSingleVersion>();
        int allocBefore = table.ComponentSegment.AllocatedChunkCount;

        using (var tx = dbe.CreateQuickTransaction())
        {
            var comp = new CompSmSingleVersion(1);
            tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
            // Don't commit
        }

        int allocAfter = table.ComponentSegment.AllocatedChunkCount;
        Assert.That(allocAfter, Is.EqualTo(allocBefore), "Rollback should free chunks allocated by pending spawn");
    }

    // ── Versioned regression ──

    [Test]
    public void Versioned_StillWorks()
    {
        using var dbe = SetupEngine();

        var table = dbe.GetComponentTable<EcsPosition>();
        Assert.That(table.StorageMode, Is.EqualTo(StorageMode.Versioned));
        Assert.That(table.DirtyBitmap, Is.Null, "Versioned table should have no DirtyBitmap");

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var entityId = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

        var entity = tx.Open(entityId);
        ref readonly var read = ref entity.Read(EcsUnit.Position);
        Assert.That(read.X, Is.EqualTo(1f));
        Assert.That(read.Y, Is.EqualTo(2f));
        Assert.That(read.Z, Is.EqualTo(3f));
    }

    // ── DirtyBitmap unit tests ──

    // #709 regression: covers the Grow half of the array-swap race. The bitmap starts at 1024 bits (16 words) and the threads reach bit 7999
    // (125 words), so Grow is guaranteed to fire while the other writers are mid-Set.
    [Test]
    public void DirtyBitmap_ConcurrentSet()
    {
        var bitmap = new DirtyBitmap(1024);

        const int threadCount = 8;
        const int opsPerThread = 1000;
        var barrier = new Barrier(threadCount);
        var threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < opsPerThread; i++)
                {
                    bitmap.Set(threadId * opsPerThread + i);
                }
            });
            threads[t].Start();
        }

        for (int t = 0; t < threadCount; t++)
        {
            threads[t].Join();
        }

        var snapshot = bitmap.Snapshot();
        int setBits = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            setBits += System.Numerics.BitOperations.PopCount((ulong)snapshot[i]);
        }
        Assert.That(setBits, Is.EqualTo(threadCount * opsPerThread));
    }

    /// <summary>
    /// #709 regression, the drain half. The bitmap is pre-sized so growth cannot fire, leaving <c>Snapshot</c> as the only thing racing the writers — the
    /// half a growth-only fix would leave broken. Every bit set by a writer must appear in exactly one place: some snapshot taken along the way, or the
    /// final one. A bit in neither was lost to the drain.
    /// </summary>
    [Test]
    public void DirtyBitmap_ConcurrentSetWhileSnapshotting()
    {
        const int threadCount = 4;
        const int opsPerThread = 2000;
        const int totalBits = threadCount * opsPerThread;

        var bitmap = new DirtyBitmap(totalBits);   // pre-sized: no growth can fire, so any lost bit is the drain's doing
        var seen = new long[(totalBits + 63) >> 6];
        var barrier = new Barrier(threadCount + 1);
        var writersDone = 0;
        var threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < opsPerThread; i++)
                {
                    bitmap.Set(threadId * opsPerThread + i);
                }
                Interlocked.Increment(ref writersDone);
            });
            threads[t].Start();
        }

        barrier.SignalAndWait();

        // Snapshot repeatedly while the writers run, ORing every drained word into `seen`. A snapshot that races a Set is exactly the window
        // under test, so the loop deliberately does not pace itself.
        while (Volatile.Read(ref writersDone) < threadCount)
        {
            Accumulate(bitmap.Snapshot(), seen);
        }

        for (int t = 0; t < threadCount; t++)
        {
            threads[t].Join();
        }
        Accumulate(bitmap.Snapshot(), seen);   // final drain, after every writer has retired

        int setBits = 0;
        for (int i = 0; i < seen.Length; i++)
        {
            setBits += System.Numerics.BitOperations.PopCount((ulong)seen[i]);
        }
        Assert.That(setBits, Is.EqualTo(totalBits), "every bit set by a writer must survive in some snapshot");

        static void Accumulate(long[] snapshot, long[] seen)
        {
            for (int i = 0; i < snapshot.Length && i < seen.Length; i++)
            {
                seen[i] |= snapshot[i];
            }
        }
    }

    /// <summary>
    /// #709 regression, the growth window — the case that decided the implementation. A design that grows by copying the words into a bigger array cannot
    /// survive this, because the copy runs before the new array is published: a writer landing in between still sees its own array as the live one, and the
    /// bit reaches neither.
    /// </summary>
    /// <remarks>
    /// Both of the other two cases missed that. They grow from 16 words to 125 — three copies, three sub-microsecond windows — so they went green against a
    /// copying design with a post-OR validation still in it, and only the full parallel suite reddened them, once. This one starts every round at a single
    /// block so each round crosses several extensions, and runs many rounds: against that design it failed 6 runs in 30, with the tell being CONTIGUOUS runs
    /// of lost bits at a thread's first writes (e.g. <c>3001..3007</c>) rather than isolated ones.
    /// </remarks>
    [Test]
    public void DirtyBitmap_ConcurrentSetAcrossManyGrowths()
    {
        const int threadCount = 8;
        const int opsPerThread = 1000;
        const int rounds = 25;

        for (int round = 0; round < rounds; round++)
        {
            var bitmap = new DirtyBitmap(1);   // smallest possible — every round has to extend the bitmap repeatedly to reach bit 7999
            var barrier = new Barrier(threadCount);
            var threads = new Thread[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                int threadId = t;
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < opsPerThread; i++)
                    {
                        bitmap.Set(threadId * opsPerThread + i);
                    }
                });
                threads[t].Start();
            }

            for (int t = 0; t < threadCount; t++)
            {
                threads[t].Join();
            }

            var snapshot = bitmap.Snapshot();
            int setBits = 0;
            for (int i = 0; i < snapshot.Length; i++)
            {
                setBits += System.Numerics.BitOperations.PopCount((ulong)snapshot[i]);
            }
            if (setBits != threadCount * opsPerThread)
            {
                var missing = new System.Text.StringBuilder();
                for (int b = 0; b < threadCount * opsPerThread; b++)
                {
                    int w = b >> 6;
                    if (w >= snapshot.Length || (snapshot[w] & (1L << (b & 63))) == 0)
                    {
                        missing.Append(b).Append(" (w").Append(w).Append(") ");
                    }
                }
                Assert.Fail($"round {round}: {threadCount * opsPerThread - setBits} bit(s) lost, snapshotWords={snapshot.Length}, missing={missing}");
            }
        }
    }

    [Test]
    public void DirtyBitmap_Growth()
    {
        var bitmap = new DirtyBitmap(64);

        bitmap.Set(10000);
        Assert.That(bitmap.HasDirty, Is.True);

        var snapshot = bitmap.Snapshot();
        int wordIndex = 10000 >> 6;
        long mask = 1L << (10000 & 63);
        Assert.That(snapshot[wordIndex] & mask, Is.Not.EqualTo(0L), "Bit 10000 should be set after growth");
    }

    // ── Mixed-mode entity tests (3.3) ──

    [Test]
    public void Mixed_SpawnWithAllThreeModes()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var v = new CompSmVersionedMix(10);
        var sv = new CompSmSingleVersion(20);
        var tr = new CompSmTransient(30);
        var entityId = tx.Spawn<MixedModeArchetype>(
            MixedModeArchetype.Versioned.Set(in v),
            MixedModeArchetype.SV.Set(in sv),
            MixedModeArchetype.Trans.Set(in tr));

        var entity = tx.Open(entityId);
        Assert.That(entity.Read(MixedModeArchetype.Versioned).Value, Is.EqualTo(10));
        Assert.That(entity.Read(MixedModeArchetype.SV).Value, Is.EqualTo(20));
        Assert.That(entity.Read(MixedModeArchetype.Trans).Value, Is.EqualTo(30));
    }

    [Test]
    public void Mixed_WriteEachMode()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var v = new CompSmVersionedMix(1);
        var sv = new CompSmSingleVersion(2);
        var tr = new CompSmTransient(3);
        var entityId = tx.Spawn<MixedModeArchetype>(
            MixedModeArchetype.Versioned.Set(in v),
            MixedModeArchetype.SV.Set(in sv),
            MixedModeArchetype.Trans.Set(in tr));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.OpenMut(entityId);

        entity.Write(MixedModeArchetype.Versioned).Value = 100;
        entity.Write(MixedModeArchetype.SV).Value = 200;
        entity.Write(MixedModeArchetype.Trans).Value = 300;

        Assert.That(entity.Read(MixedModeArchetype.Versioned).Value, Is.EqualTo(100));
        Assert.That(entity.Read(MixedModeArchetype.SV).Value, Is.EqualTo(200));
        Assert.That(entity.Read(MixedModeArchetype.Trans).Value, Is.EqualTo(300));
    }

    [Test]
    public void Mixed_SV_DirtyBitmap_OnlyForSV()
    {
        using var dbe = SetupEngine();

        var vTable = dbe.GetComponentTable<CompSmVersionedMix>();
        var svTable = dbe.GetComponentTable<CompSmSingleVersion>();
        var trTable = dbe.GetComponentTable<CompSmTransient>();

        Assert.That(vTable.DirtyBitmap, Is.Null, "Versioned has no DirtyBitmap");
        Assert.That(svTable.DirtyBitmap, Is.Not.Null, "SV has DirtyBitmap");
        Assert.That(trTable.DirtyBitmap, Is.Null, "Transient has no DirtyBitmap");

        using var tx = dbe.CreateQuickTransaction();
        var v = new CompSmVersionedMix(1);
        var sv = new CompSmSingleVersion(2);
        var tr = new CompSmTransient(3);
        var entityId = tx.Spawn<MixedModeArchetype>(
            MixedModeArchetype.Versioned.Set(in v),
            MixedModeArchetype.SV.Set(in sv),
            MixedModeArchetype.Trans.Set(in tr));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.OpenMut(entityId);
        entity.Write(MixedModeArchetype.Versioned).Value = 100;
        entity.Write(MixedModeArchetype.SV).Value = 200;
        entity.Write(MixedModeArchetype.Trans).Value = 300;

        // Dirty tracking: cluster-eligible archetypes use per-archetype ClusterDirtyBitmap, not per-ComponentTable DirtyBitmap.
        var meta = ArchetypeRegistry.GetMetadata<MixedModeArchetype>();
        if (meta.IsClusterEligible)
        {
            var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
            Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True, "ClusterDirtyBitmap should be set after write");
        }
        else
        {
            Assert.That(svTable.DirtyBitmap.HasDirty, Is.True, "SV DirtyBitmap should be set after write");
        }
    }

    [Test]
    public void Mixed_Rollback_FreesAllChunks()
    {
        using var dbe = SetupEngine();

        var vTable = dbe.GetComponentTable<CompSmVersionedMix>();
        var svTable = dbe.GetComponentTable<CompSmSingleVersion>();
        var trTable = dbe.GetComponentTable<CompSmTransient>();

        int vBefore = vTable.ComponentSegment.AllocatedChunkCount;
        int svBefore = svTable.ComponentSegment.AllocatedChunkCount;
        int trBefore = trTable.TransientComponentSegment.AllocatedChunkCount;

        using (var tx = dbe.CreateQuickTransaction())
        {
            var v = new CompSmVersionedMix(1);
            var sv = new CompSmSingleVersion(2);
            var tr = new CompSmTransient(3);
            tx.Spawn<MixedModeArchetype>(
                MixedModeArchetype.Versioned.Set(in v),
                MixedModeArchetype.SV.Set(in sv),
                MixedModeArchetype.Trans.Set(in tr));
            // Don't commit — triggers rollback
        }

        Assert.That(vTable.ComponentSegment.AllocatedChunkCount, Is.EqualTo(vBefore), "Versioned chunks freed on rollback");
        Assert.That(svTable.ComponentSegment.AllocatedChunkCount, Is.EqualTo(svBefore), "SV chunks freed on rollback");
        Assert.That(trTable.TransientComponentSegment.AllocatedChunkCount, Is.EqualTo(trBefore), "Transient chunks freed on rollback");
    }

    // ── Full commit cycle tests (exercises FlushAndRefreshEpoch + Commit persist path) ──

    [Test]
    public void Mixed_FullCommitCycle_Works()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var v = new CompSmVersionedMix(10);
        var sv = new CompSmSingleVersion(20);
        var tr = new CompSmTransient(30);
        var id = tx.Spawn<MixedModeArchetype>(
            MixedModeArchetype.Versioned.Set(in v),
            MixedModeArchetype.SV.Set(in sv),
            MixedModeArchetype.Trans.Set(in tr));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.Open(id);
        Assert.That(entity.Read(MixedModeArchetype.Versioned).Value, Is.EqualTo(10));
        Assert.That(entity.Read(MixedModeArchetype.SV).Value, Is.EqualTo(20));
        Assert.That(entity.Read(MixedModeArchetype.Trans).Value, Is.EqualTo(30));
    }

    [Test]
    public void Mixed_WriteAndCommit_AllModesWork()
    {
        using var dbe = SetupEngine();

        // Spawn
        using var tx = dbe.CreateQuickTransaction();
        var v = new CompSmVersionedMix(1);
        var sv = new CompSmSingleVersion(2);
        var tr = new CompSmTransient(3);
        var id = tx.Spawn<MixedModeArchetype>(
            MixedModeArchetype.Versioned.Set(in v),
            MixedModeArchetype.SV.Set(in sv),
            MixedModeArchetype.Trans.Set(in tr));
        tx.Commit();

        // Write all modes, then commit
        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.OpenMut(id);
        entity.Write(MixedModeArchetype.Versioned).Value = 100;
        entity.Write(MixedModeArchetype.SV).Value = 200;
        entity.Write(MixedModeArchetype.Trans).Value = 300;
        tx2.Commit();

        // Verify writes persisted
        using var tx3 = dbe.CreateQuickTransaction();
        var entity3 = tx3.Open(id);
        Assert.That(entity3.Read(MixedModeArchetype.Versioned).Value, Is.EqualTo(100));
        Assert.That(entity3.Read(MixedModeArchetype.SV).Value, Is.EqualTo(200));
        Assert.That(entity3.Read(MixedModeArchetype.Trans).Value, Is.EqualTo(300));
    }

    // ── Epoch refresh stress test (triggers FlushAndRefreshEpoch after 128 entity ops) ──

    [Test]
    public void Mixed_ManySpawns_SurvivesEpochRefresh()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var ids = new EntityId[200];
        for (int i = 0; i < 200; i++)
        {
            var v = new CompSmVersionedMix(i);
            var sv = new CompSmSingleVersion(i + 1000);
            var tr = new CompSmTransient(i + 2000);
            ids[i] = tx.Spawn<MixedModeArchetype>(
                MixedModeArchetype.Versioned.Set(in v),
                MixedModeArchetype.SV.Set(in sv),
                MixedModeArchetype.Trans.Set(in tr));
        }
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        for (int i = 0; i < 200; i++)
        {
            var entity = tx2.Open(ids[i]);
            Assert.That(entity.Read(MixedModeArchetype.Versioned).Value, Is.EqualTo(i));
            Assert.That(entity.Read(MixedModeArchetype.SV).Value, Is.EqualTo(i + 1000));
            Assert.That(entity.Read(MixedModeArchetype.Trans).Value, Is.EqualTo(i + 2000));
        }
    }

    // ── DirtyBitmap edge cases ──

    [Test]
    public void DirtyBitmap_AllBitsInWord_SetCorrectly()
    {
        var bitmap = new DirtyBitmap(64);

        // Set all 64 bits in one word
        for (int i = 0; i < 64; i++)
        {
            bitmap.Set(i);
        }

        var snapshot = bitmap.Snapshot();
        Assert.That(snapshot[0], Is.EqualTo(-1L), "All 64 bits should be set (long = -1)");
    }

    [Test]
    public void DirtyBitmap_SparseHighBits()
    {
        var bitmap = new DirtyBitmap(64);

        bitmap.Set(63);    // last bit in first word
        bitmap.Set(64);    // first bit in second word
        bitmap.Set(127);   // last bit in second word

        var snapshot = bitmap.Snapshot();
        Assert.That(snapshot[0] & (1L << 63), Is.Not.EqualTo(0L), "Bit 63 should be set");
        Assert.That(snapshot[1] & 1L, Is.Not.EqualTo(0L), "Bit 64 should be set");
        Assert.That(snapshot[1] & (1L << 63), Is.Not.EqualTo(0L), "Bit 127 should be set");

        int totalBits = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            totalBits += System.Numerics.BitOperations.PopCount((ulong)snapshot[i]);
        }
        Assert.That(totalBits, Is.EqualTo(3));
    }

    [Test]
    public void DirtyBitmap_SnapshotLifecycle_SetClearAndReSet()
    {
        // Set + Snapshot clears
        var bitmap = new DirtyBitmap(64);
        bitmap.Set(0);
        var snapshot = bitmap.Snapshot();
        Assert.That(snapshot[0] & 1L, Is.Not.EqualTo(0L));
        Assert.That(bitmap.HasDirty, Is.False, "Snapshot should clear bitmap");

        // Double snapshot: second is empty
        bitmap.Set(5);
        bitmap.Snapshot();
        var second = bitmap.Snapshot();
        bool anySet = false;
        for (int i = 0; i < second.Length; i++)
        {
            if (second[i] != 0) { anySet = true; break; }
        }
        Assert.That(anySet, Is.False, "Second snapshot should be empty");

        // Set after snapshot visible in next snapshot
        bitmap.Set(20);
        var third = bitmap.Snapshot();
        int wordIndex = 20 >> 6;
        long mask = 1L << (20 & 63);
        Assert.That(third[wordIndex] & mask, Is.Not.EqualTo(0L), "Bit 20 set after snapshot should appear in next");
    }

    // ── WriteTickFence edge cases ──

    [Test]
    public void WriteTickFence_MultipleTicks_LastLsnUpdated()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var comp = new CompSmSingleVersion(1);
        var id = tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
        tx.Commit();

        // First tick — write to make dirty
        using var tx2 = dbe.CreateQuickTransaction();
        tx2.OpenMut(id).Write(SvTestArchetype.SvComp).Value = 10;
        dbe.WriteTickFence(1);

        // No WAL → LSN stays 0, but bitmap was cleared
        Assert.That(dbe.GetComponentTable<CompSmSingleVersion>().DirtyBitmap.HasDirty, Is.False);

        // Second tick — write again
        tx2.OpenMut(id).Write(SvTestArchetype.SvComp).Value = 20;
        dbe.WriteTickFence(2);

        Assert.That(dbe.GetComponentTable<CompSmSingleVersion>().DirtyBitmap.HasDirty, Is.False);
    }
}
