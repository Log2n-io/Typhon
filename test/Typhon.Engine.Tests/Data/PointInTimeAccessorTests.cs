using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════
// Test components — one per storage mode
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.PTA.Versioned", 1)]
[StructLayout(LayoutKind.Sequential)]
struct PtaVersioned
{
    public int Value;
    public int _pad;
    public PtaVersioned(int v) { Value = v; }
}

[Component("Typhon.Test.PTA.SingleVersion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct PtaSingleVersion
{
    public int Value;
    public int _pad;
    public PtaSingleVersion(int v) { Value = v; }
}

[Component("Typhon.Test.PTA.Transient", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct PtaTransient
{
    public int Value;
    public int _pad;
    public PtaTransient(int v) { Value = v; }
}

// ═══════════════════════════════════════════════════════════════════════
// Test archetypes
// ═══════════════════════════════════════════════════════════════════════

[Archetype]
partial class PtaArchVersioned : Archetype<PtaArchVersioned>
{
    public static readonly Comp<PtaVersioned> Data = Register<PtaVersioned>();
}

[Archetype]
partial class PtaArchSingleVersion : Archetype<PtaArchSingleVersion>
{
    public static readonly Comp<PtaSingleVersion> Data = Register<PtaSingleVersion>();
}

[Archetype]
partial class PtaArchTransient : Archetype<PtaArchTransient>
{
    public static readonly Comp<PtaTransient> Data = Register<PtaTransient>();
}

[Archetype]
partial class PtaArchMixed : Archetype<PtaArchMixed>
{
    public static readonly Comp<PtaVersioned> V = Register<PtaVersioned>();
    public static readonly Comp<PtaSingleVersion> SV = Register<PtaSingleVersion>();
    public static readonly Comp<PtaTransient> T = Register<PtaTransient>();
}

// ═══════════════════════════════════════════════════════════════════════
// Tests
// ═══════════════════════════════════════════════════════════════════════

class PointInTimeAccessorTests : TestBase<PointInTimeAccessorTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<PtaVersioned>();
        dbe.RegisterComponentFromAccessor<PtaSingleVersion>();
        dbe.RegisterComponentFromAccessor<PtaTransient>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1. Basic Read Operations
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ReadVersionedComponent_ReturnsCorrectValue()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new PtaVersioned(42);
            id = t.Spawn<PtaArchVersioned>(PtaArchVersioned.Data.Set(in v));
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var entity = wa.Open(id);
        ref readonly var data = ref entity.Read(PtaArchVersioned.Data);
        Assert.That(data.Value, Is.EqualTo(42));
    }

    [Test]
    public void ReadSingleVersionComponent_ReturnsCorrectValue()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var sv = new PtaSingleVersion(99);
            id = t.Spawn<PtaArchSingleVersion>(PtaArchSingleVersion.Data.Set(in sv));
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var entity = wa.Open(id);
        ref readonly var data = ref entity.Read(PtaArchSingleVersion.Data);
        Assert.That(data.Value, Is.EqualTo(99));
    }

    [Test]
    public void ReadTransientComponent_ReturnsCorrectValue()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var tr = new PtaTransient(77);
            id = t.Spawn<PtaArchTransient>(PtaArchTransient.Data.Set(in tr));
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var entity = wa.Open(id);
        ref readonly var data = ref entity.Read(PtaArchTransient.Data);
        Assert.That(data.Value, Is.EqualTo(77));
    }

    [Test]
    public void OpenNonExistentEntity_TryOpenReturnsFalse()
    {
        using var dbe = SetupEngine();

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var fakeId = EntityId.FromRaw(900L << 48 | 999999L);
        var found = wa.TryOpen(fakeId, out var entity);
        Assert.That(found, Is.False);
        Assert.That(entity.IsValid, Is.False);
    }

    [Test]
    public void OpenDestroyedEntity_TryOpenReturnsFalse()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new PtaVersioned(1);
            id = t.Spawn<PtaArchVersioned>(PtaArchVersioned.Data.Set(in v));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id);
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var found = wa.TryOpen(id, out _);
        Assert.That(found, Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. OpenMut Operations
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void OpenMutSingleVersion_WriteSucceeds()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var sv = new PtaSingleVersion(10);
            id = t.Spawn<PtaArchSingleVersion>(PtaArchSingleVersion.Data.Set(in sv));
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var entity = wa.OpenMut(id);
        ref var data = ref entity.Write(PtaArchSingleVersion.Data);
        data.Value = 200;

        // Read back through same accessor
        var entity2 = wa.Open(id);
        Assert.That(entity2.Read(PtaArchSingleVersion.Data).Value, Is.EqualTo(200));
    }

    [Test]
    public void OpenMutTransient_WriteSucceeds()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var tr = new PtaTransient(10);
            id = t.Spawn<PtaArchTransient>(PtaArchTransient.Data.Set(in tr));
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var entity = wa.OpenMut(id);
        ref var data = ref entity.Write(PtaArchTransient.Data);
        data.Value = 300;

        var entity2 = wa.Open(id);
        Assert.That(entity2.Read(PtaArchTransient.Data).Value, Is.EqualTo(300));
    }

    [Test]
    public void OpenMutVersionedComponent_ThrowsOnWrite()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new PtaVersioned(1);
            id = t.Spawn<PtaArchVersioned>(PtaArchVersioned.Data.Set(in v));
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);

        // EntityRef is a ref struct — can't capture in lambda. Call Write directly and catch.
        var entity = wa.OpenMut(id);
        try
        {
            ref var _ = ref entity.Write(PtaArchVersioned.Data);
            Assert.Fail("Expected InvalidOperationException for Versioned write");
        }
        catch (InvalidOperationException)
        {
            // Expected
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. MVCC Visibility
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void AccessorSeesOnlyCommittedData()
    {
        using var dbe = SetupEngine();

        EntityId id1;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new PtaVersioned(1);
            id1 = t.Spawn<PtaArchVersioned>(PtaArchVersioned.Data.Set(in v));
            t.Commit();
        }

        // Create accessor BEFORE spawning the second entity
        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);

        EntityId id2;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new PtaVersioned(2);
            id2 = t.Spawn<PtaArchVersioned>(PtaArchVersioned.Data.Set(in v));
            t.Commit();
        }

        // Accessor should see id1 (committed before accessor creation)
        Assert.That(wa.TryOpen(id1, out _), Is.True);

        // Accessor should NOT see id2 (committed after accessor TSN)
        Assert.That(wa.TryOpen(id2, out _), Is.False);
    }

    /// <summary>
    /// A PointInTimeAccessor holds no retention, so a committing writer is free to trim the revision its snapshot needs. The read must SAY so (#672).
    /// </summary>
    /// <remarks>
    /// This asserted <c>Value == 100</c> before #672 was fixed, which is what the type's name promises and what the engine does not deliver: the accessor
    /// registers nothing in the transaction chain, so <c>ComputeNextMinTSN</c> cannot see it. The read then returned a ZEROED component — wrong data, and at
    /// the call site indistinguishable from legitimately-zero data. Retention was rejected as the fix because it would let any caller degrade the engine-wide
    /// lock-free Versioned read path by holding an accessor too long; failing fast is the cheaper contract, so the assertion is now the throw.
    /// </remarks>
    [VerifiesRule("SNAP-01")]
    [Test]
    public void AccessorReadingATrimmedVersionedRevision_ThrowsSnapshotExpired()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new PtaVersioned(100);
            id = t.Spawn<PtaArchVersioned>(PtaArchVersioned.Data.Set(in v));
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe);

        // Trims the revision the accessor's snapshot needs — the accessor is invisible to the cleanup that decides how far back to keep.
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            ref var data = ref entity.Write(PtaArchVersioned.Data);
            data.Value = 200;
            t.Commit();
        }

        var ex = Assert.Throws<SnapshotExpiredException>(
            () => _ = accessor.GetWorkerAccessor(0).Open(id).Read(PtaArchVersioned.Data).Value,
            "reading an expired snapshot must fail, not return a zeroed component");

        Assert.That(ex.SnapshotTsn, Is.LessThan(ex.RetainedMinTsn), "the exception must name a snapshot genuinely below the retention floor");
    }

    /// <summary>A read-only Transaction registers in the chain, so cleanup can see it and its snapshot survives the same write.</summary>
    /// <remarks>
    /// The control for the case above, and the reason the fix is fail-fast rather than retention: the guarantee the accessor cannot make is one the
    /// transaction already makes, through the mechanism the accessor skips. Without this, "PointInTimeAccessor throws" would look like a limitation of MVCC
    /// rather than of the accessor.
    /// </remarks>
    [VerifiesRule("SNAP-02")]
    [Test]
    public void ReadOnlyTransaction_KeepsItsSnapshotAcrossTheSameWrite()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new PtaVersioned(100);
            id = t.Spawn<PtaArchVersioned>(PtaArchVersioned.Data.Set(in v));
            t.Commit();
        }

        using var snapshot = dbe.TransactionChain.CreateTransaction(dbe, readOnly: true);
        Assert.That(snapshot.Open(id).Read(PtaArchVersioned.Data).Value, Is.EqualTo(100), "PREMISE: the transaction sees the pre-update value");

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            ref var data = ref entity.Write(PtaArchVersioned.Data);
            data.Value = 200;
            t.Commit();
        }

        Assert.That(snapshot.Open(id).Read(PtaArchVersioned.Data).Value, Is.EqualTo(100),
            "a registered snapshot must still see its own revision after a concurrent commit — this is what the accessor cannot do");
    }

    /// <remarks>
    /// The write that separates A from B is the same write that trims A's snapshot (#672), so "different snapshots" resolves to "B reads, A raises". Kept
    /// as one test rather than split: the pair is the point — B proves the newer snapshot is intact, which is what stops A's throw reading as a general
    /// breakage of the accessor.
    /// </remarks>
    [VerifiesRule("SNAP-01")]
    [Test]
    public void TwoAccessorsAtDifferentTSNs_SeeDifferentSnapshots()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new PtaVersioned(10);
            id = t.Spawn<PtaArchVersioned>(PtaArchVersioned.Data.Set(in v));
            t.Commit();
        }

        using var accessorA = PointInTimeAccessor.Create(dbe);

        // Update to 20 between the two accessors
        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(id);
            ref var d = ref e.Write(PtaArchVersioned.Data);
            d.Value = 20;
            t.Commit();
        }

        using var accessorB = PointInTimeAccessor.Create(dbe);

        Assert.Throws<SnapshotExpiredException>(
            () => _ = accessorA.GetWorkerAccessor(0).Open(id).Read(PtaArchVersioned.Data).Value,
            "A's snapshot was trimmed by the update that separates it from B");
        Assert.That(accessorB.GetWorkerAccessor(0).Open(id).Read(PtaArchVersioned.Data).Value, Is.EqualTo(20),
            "B's snapshot is above the retention floor and must read normally");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. Thread Safety
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void ConcurrentReads_DifferentEntities_AllSucceed()
    {
        using var dbe = SetupEngine();
        const int entityCount = 100;
        const int threadCount = 8;

        var ids = new EntityId[entityCount];
        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                var sv = new PtaSingleVersion(i);
                ids[i] = t.Spawn<PtaArchSingleVersion>(PtaArchSingleVersion.Data.Set(in sv));
            }
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe, threadCount);
        var barrier = new Barrier(threadCount);
        var errors = new ConcurrentBag<Exception>();

        Parallel.For(0, threadCount, i =>
        {
            barrier.SignalAndWait();
            try
            {
                var wa = accessor.GetWorkerAccessor(i);
                int start = i * (entityCount / threadCount);
                int end = (i + 1) * (entityCount / threadCount);
                for (int j = start; j < end; j++)
                {
                    var entity = wa.Open(ids[j]);
                    var val = entity.Read(PtaArchSingleVersion.Data).Value;
                    if (val != j)
                    {
                        errors.Add(new Exception($"Thread {i}: Expected {j} but got {val}"));
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        Assert.That(errors, Is.Empty, () => string.Join("\n", errors));
    }

    [Test]
    [CancelAfter(15000)]
    public void ConcurrentReads_SameEntity_AllReturnCorrectData()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var sv = new PtaSingleVersion(42);
            id = t.Spawn<PtaArchSingleVersion>(PtaArchSingleVersion.Data.Set(in sv));
            t.Commit();
        }

        const int threadCount = 8;
        using var accessor = PointInTimeAccessor.Create(dbe, threadCount);
        var barrier = new Barrier(threadCount);
        var errors = new ConcurrentBag<Exception>();

        Parallel.For(0, threadCount, i =>
        {
            barrier.SignalAndWait();
            try
            {
                var wa = accessor.GetWorkerAccessor(i);
                for (int j = 0; j < 100; j++)
                {
                    var entity = wa.Open(id);
                    var val = entity.Read(PtaArchSingleVersion.Data).Value;
                    if (val != 42)
                    {
                        errors.Add(new Exception($"Thread {i}, iter {j}: Expected 42 but got {val}"));
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        Assert.That(errors, Is.Empty, () => string.Join("\n", errors));
    }

    [Test]
    [CancelAfter(15000)]
    public void ConcurrentOpenMut_DifferentSVEntities_NoCorruption()
    {
        using var dbe = SetupEngine();
        const int threadCount = 8;
        const int entitiesPerThread = 50;
        int totalEntities = threadCount * entitiesPerThread;

        var ids = new EntityId[totalEntities];
        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < totalEntities; i++)
            {
                var sv = new PtaSingleVersion(i);
                ids[i] = t.Spawn<PtaArchSingleVersion>(PtaArchSingleVersion.Data.Set(in sv));
            }
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe, threadCount);
        var barrier = new Barrier(threadCount);
        var errors = new ConcurrentBag<Exception>();

        Parallel.For(0, threadCount, i =>
        {
            barrier.SignalAndWait();
            try
            {
                var wa = accessor.GetWorkerAccessor(i);
                int start = i * entitiesPerThread;
                for (int j = 0; j < entitiesPerThread; j++)
                {
                    var entity = wa.OpenMut(ids[start + j]);
                    ref var data = ref entity.Write(PtaArchSingleVersion.Data);
                    data.Value = (i + 1) * 1000 + j;
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        Assert.That(errors, Is.Empty, () => string.Join("\n", errors));

        // Verify writes
        using var verifier = PointInTimeAccessor.Create(dbe);
        var vwa = verifier.GetWorkerAccessor(0);
        for (int i = 0; i < threadCount; i++)
        {
            int start = i * entitiesPerThread;
            for (int j = 0; j < entitiesPerThread; j++)
            {
                var entity = vwa.Open(ids[start + j]);
                Assert.That(entity.Read(PtaArchSingleVersion.Data).Value, Is.EqualTo((i + 1) * 1000 + j));
            }
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void ConcurrentMixedReadWrite_NoRaceConditions()
    {
        using var dbe = SetupEngine();
        const int entityCount = 100;
        const int threadCount = 8;

        // Create mixed entities: half SV, half Versioned (read-only)
        var svIds = new EntityId[entityCount / 2];
        var vIds = new EntityId[entityCount / 2];

        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount / 2; i++)
            {
                var sv = new PtaSingleVersion(i);
                svIds[i] = t.Spawn<PtaArchSingleVersion>(PtaArchSingleVersion.Data.Set(in sv));

                var v = new PtaVersioned(i + 1000);
                vIds[i] = t.Spawn<PtaArchVersioned>(PtaArchVersioned.Data.Set(in v));
            }
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe, threadCount);
        var barrier = new Barrier(threadCount);
        var errors = new ConcurrentBag<Exception>();

        Parallel.For(0, threadCount, threadIdx =>
        {
            barrier.SignalAndWait();
            try
            {
                var wa = accessor.GetWorkerAccessor(threadIdx);
                // Even threads write SV, odd threads read Versioned
                if (threadIdx % 2 == 0)
                {
                    int start = (threadIdx / 2) * (entityCount / 2 / (threadCount / 2));
                    int count = entityCount / 2 / (threadCount / 2);
                    for (int j = start; j < start + count && j < svIds.Length; j++)
                    {
                        var entity = wa.OpenMut(svIds[j]);
                        ref var data = ref entity.Write(PtaArchSingleVersion.Data);
                        data.Value = threadIdx * 10000 + j;
                    }
                }
                else
                {
                    for (int j = 0; j < vIds.Length; j++)
                    {
                        var entity = wa.Open(vIds[j]);
                        var val = entity.Read(PtaArchVersioned.Data).Value;
                        if (val != j + 1000)
                        {
                            errors.Add(new Exception($"Thread {threadIdx}: Expected {j + 1000} but got {val}"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        Assert.That(errors, Is.Empty, () => string.Join("\n", errors));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5. Lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void CreateAndDisposeImmediately_NoCrash()
    {
        using var dbe = SetupEngine();
        using var accessor = PointInTimeAccessor.Create(dbe);
        // No operations — just dispose
    }

    [Test]
    public void DoubleDispose_NoCrash()
    {
        using var dbe = SetupEngine();
        var accessor = PointInTimeAccessor.Create(dbe);
        accessor.Dispose();
        accessor.Dispose(); // Should not throw
    }

    /// <remarks>
    /// "Independent" survives #672; "both readable" does not. The older accessor's revision is trimmed by the write between them, so what independence now
    /// means is that the two snapshots resolve differently — one raises, one reads — rather than both silently resolving to the same value.
    /// </remarks>
    [VerifiesRule("SNAP-01")]
    [Test]
    public void MultipleAccessorsConcurrently_IndependentSnapshots()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new PtaVersioned(1);
            id = t.Spawn<PtaArchVersioned>(PtaArchVersioned.Data.Set(in v));
            t.Commit();
        }

        using var acc1 = PointInTimeAccessor.Create(dbe);

        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(id);
            ref var d = ref e.Write(PtaArchVersioned.Data);
            d.Value = 2;
            t.Commit();
        }

        using var acc2 = PointInTimeAccessor.Create(dbe);

        // Both are alive and independently resolved; only the older one's revision is gone.
        Assert.Throws<SnapshotExpiredException>(() => _ = acc1.GetWorkerAccessor(0).Open(id).Read(PtaArchVersioned.Data).Value);
        Assert.That(acc2.GetWorkerAccessor(0).Open(id).Read(PtaArchVersioned.Data).Value, Is.EqualTo(2));
        Assert.That(acc1.TSN, Is.LessThan(acc2.TSN));
    }

    [Test]
    public void TSN_ReflectsCreationOrder()
    {
        using var dbe = SetupEngine();
        using var acc1 = PointInTimeAccessor.Create(dbe);
        using var acc2 = PointInTimeAccessor.Create(dbe);
        Assert.That(acc1.TSN, Is.LessThan(acc2.TSN));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6. Stress Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void StressTest_ManyThreadsManyEntities()
    {
        using var dbe = SetupEngine();
        const int entityCount = 500;
        const int threadCount = 8;

        var ids = new EntityId[entityCount];
        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                var sv = new PtaSingleVersion(i);
                ids[i] = t.Spawn<PtaArchSingleVersion>(PtaArchSingleVersion.Data.Set(in sv));
            }
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe, threadCount);
        var barrier = new Barrier(threadCount);
        var totalReads = 0;
        var errors = new ConcurrentBag<Exception>();

        Parallel.For(0, threadCount, threadIdx =>
        {
            barrier.SignalAndWait();
            try
            {
                var wa = accessor.GetWorkerAccessor(threadIdx);
                int reads = 0;
                for (int pass = 0; pass < 3; pass++)
                {
                    for (int i = 0; i < entityCount; i++)
                    {
                        var entity = wa.Open(ids[i]);
                        var val = entity.Read(PtaArchSingleVersion.Data).Value;
                        if (val != i)
                        {
                            errors.Add(new Exception($"Thread {threadIdx}, pass {pass}, entity {i}: Expected {i} but got {val}"));
                        }
                        reads++;
                    }
                }
                Interlocked.Add(ref totalReads, reads);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        Assert.That(errors, Is.Empty, () => string.Join("\n", errors));
        Assert.That(totalReads, Is.EqualTo(threadCount * 3 * entityCount));
    }

    [Test]
    [CancelAfter(15000)]
    public void StressTest_RapidCreateDispose()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var sv = new PtaSingleVersion(42);
            id = t.Spawn<PtaArchSingleVersion>(PtaArchSingleVersion.Data.Set(in sv));
            t.Commit();
        }

        // Rapidly create and dispose accessors — verify no resource leaks
        for (int i = 0; i < 100; i++)
        {
            using var accessor = PointInTimeAccessor.Create(dbe);
            var entity = accessor.GetWorkerAccessor(0).Open(id);
            Assert.That(entity.Read(PtaArchSingleVersion.Data).Value, Is.EqualTo(42));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7. Mixed Archetype (V + SV + Transient on same entity)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MixedArchetype_ReadAllStorageModes()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new PtaVersioned(1);
            var sv = new PtaSingleVersion(2);
            var tr = new PtaTransient(3);
            id = t.Spawn<PtaArchMixed>(
                PtaArchMixed.V.Set(in v),
                PtaArchMixed.SV.Set(in sv),
                PtaArchMixed.T.Set(in tr)
            );
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var entity = wa.Open(id);

        Assert.That(entity.Read(PtaArchMixed.V).Value, Is.EqualTo(1));
        Assert.That(entity.Read(PtaArchMixed.SV).Value, Is.EqualTo(2));
        Assert.That(entity.Read(PtaArchMixed.T).Value, Is.EqualTo(3));
    }

    [Test]
    public void MixedArchetype_WriteSVAndTransient_ReadVersioned()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new PtaVersioned(10);
            var sv = new PtaSingleVersion(20);
            var tr = new PtaTransient(30);
            id = t.Spawn<PtaArchMixed>(
                PtaArchMixed.V.Set(in v),
                PtaArchMixed.SV.Set(in sv),
                PtaArchMixed.T.Set(in tr)
            );
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var entity = wa.OpenMut(id);

        // Write SV and Transient
        ref var svData = ref entity.Write(PtaArchMixed.SV);
        svData.Value = 200;
        ref var trData = ref entity.Write(PtaArchMixed.T);
        trData.Value = 300;

        // Read Versioned (should still work)
        Assert.That(entity.Read(PtaArchMixed.V).Value, Is.EqualTo(10));

        // Verify writes
        var entity2 = wa.Open(id);
        Assert.That(entity2.Read(PtaArchMixed.SV).Value, Is.EqualTo(200));
        Assert.That(entity2.Read(PtaArchMixed.T).Value, Is.EqualTo(300));

        // Writing Versioned should throw
        try
        {
            var e = wa.OpenMut(id);
            ref var _ = ref e.Write(PtaArchMixed.V);
            Assert.Fail("Expected InvalidOperationException for Versioned write");
        }
        catch (InvalidOperationException)
        {
            // Expected
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 8. Torture Test — all storage modes, high thread count, multi-pass
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void Torture_AllStorageModes_HighConcurrency_DataIntegrity()
    {
        using var dbe = SetupEngine();
        const int threadCount = 16;
        const int entitiesPerType = 200;
        const int passCount = 5;

        // Spawn entities across all storage modes
        var mixedIds = new EntityId[entitiesPerType];
        var svIds = new EntityId[entitiesPerType];
        var transientIds = new EntityId[entitiesPerType];
        var versionedIds = new EntityId[entitiesPerType];

        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entitiesPerType; i++)
            {
                var v = new PtaVersioned(i * 10);
                var sv = new PtaSingleVersion(i * 100);
                var tr = new PtaTransient(i * 1000);
                mixedIds[i] = t.Spawn<PtaArchMixed>(
                    PtaArchMixed.V.Set(in v),
                    PtaArchMixed.SV.Set(in sv),
                    PtaArchMixed.T.Set(in tr)
                );

                var sv2 = new PtaSingleVersion(i);
                svIds[i] = t.Spawn<PtaArchSingleVersion>(PtaArchSingleVersion.Data.Set(in sv2));

                var tr2 = new PtaTransient(i + 5000);
                transientIds[i] = t.Spawn<PtaArchTransient>(PtaArchTransient.Data.Set(in tr2));

                var v2 = new PtaVersioned(i + 9000);
                versionedIds[i] = t.Spawn<PtaArchVersioned>(PtaArchVersioned.Data.Set(in v2));
            }
            t.Commit();
        }

        using var accessor = PointInTimeAccessor.Create(dbe, threadCount);
        var barrier = new Barrier(threadCount);
        var errors = new ConcurrentBag<Exception>();
        var totalOps = 0;

        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, threadIdx =>
        {
            barrier.SignalAndWait();
            int ops = 0;
            var wa = accessor.GetWorkerAccessor(threadIdx);

            try
            {
                // Each thread partitions the entity space
                int chunkSize = entitiesPerType / threadCount;
                int start = threadIdx * chunkSize;
                int end = Math.Min(start + chunkSize, entitiesPerType);

                for (int pass = 0; pass < passCount; pass++)
                {
                    for (int i = start; i < end; i++)
                    {
                        // 1. Read Versioned on mixed entity (MVCC chain walk)
                        {
                            var e = wa.Open(mixedIds[i]);
                            var val = e.Read(PtaArchMixed.V).Value;
                            if (val != i * 10)
                            {
                                errors.Add(new Exception($"T{threadIdx} P{pass}: Mixed.V[{i}] expected {i * 10}, got {val}"));
                            }
                            ops++;
                        }

                        // 2. Write SV on mixed entity (non-overlapping: each thread owns its chunk)
                        {
                            var e = wa.OpenMut(mixedIds[i]);
                            ref var sv = ref e.Write(PtaArchMixed.SV);
                            int expected = pass == 0 ? i * 100 : threadIdx * 100000 + i * 100 + (pass - 1);
                            if (sv.Value != expected)
                            {
                                errors.Add(new Exception($"T{threadIdx} P{pass}: Mixed.SV[{i}] expected {expected}, got {sv.Value}"));
                            }
                            sv.Value = threadIdx * 100000 + i * 100 + pass;
                            ops++;
                        }

                        // 3. Write Transient on mixed entity
                        {
                            var e = wa.OpenMut(mixedIds[i]);
                            ref var tr = ref e.Write(PtaArchMixed.T);
                            tr.Value = threadIdx * 200000 + i;
                            ops++;
                        }

                        // 4. Read standalone SV entity
                        {
                            var e = wa.Open(svIds[i]);
                            var val = e.Read(PtaArchSingleVersion.Data).Value;
                            if (pass == 0 && val != i)
                            {
                                errors.Add(new Exception($"T{threadIdx} P{pass}: SV[{i}] expected {i}, got {val}"));
                            }
                            ops++;
                        }

                        // 5. Write standalone SV entity
                        {
                            var e = wa.OpenMut(svIds[i]);
                            ref var d = ref e.Write(PtaArchSingleVersion.Data);
                            d.Value = threadIdx * 300000 + i + pass;
                            ops++;
                        }

                        // 6. Read standalone Transient entity
                        {
                            var e = wa.Open(transientIds[i]);
                            e.Read(PtaArchTransient.Data); // just exercise the read path
                            ops++;
                        }

                        // 7. Read standalone Versioned entity (MVCC)
                        {
                            var e = wa.Open(versionedIds[i]);
                            var val = e.Read(PtaArchVersioned.Data).Value;
                            if (val != i + 9000)
                            {
                                errors.Add(new Exception($"T{threadIdx} P{pass}: V[{i}] expected {i + 9000}, got {val}"));
                            }
                            ops++;
                        }
                    }
                }

                Interlocked.Add(ref totalOps, ops);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        Assert.That(errors, Is.Empty, () => $"Errors ({errors.Count}):\n" + string.Join("\n", errors));

        // Verify expected operation count
        int expectedChunkSize = entitiesPerType / threadCount;
        int expectedOps = threadCount * passCount * expectedChunkSize * 7;
        Assert.That(totalOps, Is.EqualTo(expectedOps), "Total operations mismatch");

        // Verify SV writes persisted correctly — each thread wrote to its own chunk
        using var verifier = PointInTimeAccessor.Create(dbe);
        var vwa = verifier.GetWorkerAccessor(0);
        for (int threadIdx = 0; threadIdx < threadCount; threadIdx++)
        {
            int start = threadIdx * expectedChunkSize;
            int end = Math.Min(start + expectedChunkSize, entitiesPerType);
            for (int i = start; i < end; i++)
            {
                // Check standalone SV final value
                var e = vwa.Open(svIds[i]);
                int expectedVal = threadIdx * 300000 + i + (passCount - 1);
                Assert.That(e.Read(PtaArchSingleVersion.Data).Value, Is.EqualTo(expectedVal),
                    $"SV verify failed: thread {threadIdx}, entity {i}");

                // Check mixed SV final value
                var em = vwa.Open(mixedIds[i]);
                int expectedMixedSV = threadIdx * 100000 + i * 100 + (passCount - 1);
                Assert.That(em.Read(PtaArchMixed.SV).Value, Is.EqualTo(expectedMixedSV),
                    $"Mixed.SV verify failed: thread {threadIdx}, entity {i}");

                // Versioned value unchanged (accessor doesn't write Versioned)
                Assert.That(em.Read(PtaArchMixed.V).Value, Is.EqualTo(i * 10),
                    $"Mixed.V verify failed: thread {threadIdx}, entity {i}");
            }
        }
    }

    [VerifiesRule("SNAP-01")]
    [Test]
    public void MultipleSnapshotsSequential_MVCCVisibility()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            // NOT zero. This test used to spawn 0 and then assert accA still read 0 after the mutation — which is exactly what a DESTROYED snapshot
            // returns, so the assertion held whether MVCC isolation worked or not. It was passing for the wrong reason, and #672's probe found it: the
            // whole 5044-test suite produced two revision-chain walk failures and this test owned one of them.
            var v = new PtaVersioned(7);
            id = t.Spawn<PtaArchVersioned>(PtaArchVersioned.Data.Set(in v));
            t.Commit();
        }

        // Snapshot A: sees the initial value.
        using var accA = PointInTimeAccessor.Create(dbe);
        Assert.That(accA.GetWorkerAccessor(0).Open(id).Read(PtaArchVersioned.Data).Value, Is.EqualTo(7), "accA initial");

        // Mutate to 100
        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(id);
            ref var d = ref e.Write(PtaArchVersioned.Data);
            d.Value = 100;
            t.Commit();
        }

        // Snapshot B: should see 100
        using var accB = PointInTimeAccessor.Create(dbe);

        // A's revision was trimmed by the mutation above — the accessor holds no retention, so this is now a loud failure rather than a zeroed read.
        Assert.Throws<SnapshotExpiredException>(
            () => _ = accA.GetWorkerAccessor(0).Open(id).Read(PtaArchVersioned.Data).Value, "accA after mutation");
        // B should see 100
        Assert.That(accB.GetWorkerAccessor(0).Open(id).Read(PtaArchVersioned.Data).Value, Is.EqualTo(100), "accB after mutation");

        // Mutate to 200
        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(id);
            ref var d = ref e.Write(PtaArchVersioned.Data);
            d.Value = 200;
            t.Commit();
        }

        // Snapshot C
        using var accC = PointInTimeAccessor.Create(dbe);

        // Verify same behavior with a regular read-only Transaction (control group)
        using var txB = dbe.TransactionChain.CreateTransaction(dbe, readOnly: true);
        // txB.TSN was allocated after mutation 2, so it sees value 200
        Assert.That(txB.Open(id).Read(PtaArchVersioned.Data).Value, Is.EqualTo(200), "txB control");

        // A is two mutations behind the retention floor by now; C is current. The note that used to sit here — "accB's second read may see stale data ...
        // a known behavioral difference" — was a description of this same defect, not of a difference worth keeping.
        Assert.Throws<SnapshotExpiredException>(
            () => _ = accA.GetWorkerAccessor(0).Open(id).Read(PtaArchVersioned.Data).Value, "accA final");
        Assert.That(accC.GetWorkerAccessor(0).Open(id).Read(PtaArchVersioned.Data).Value, Is.EqualTo(200), "accC final");
    }

    [Test]
    [CancelAfter(15000)]
    public void Torture_ConcurrentAccessors_DifferentSnapshots()
    {
        using var dbe = SetupEngine();
        const int entityCount = 200;
        const int threadCount = 12;

        // Use SV entities: each accessor sees the SV value at its snapshot point.
        // SV is last-writer-wins — concurrent readers on different PTA snapshots
        // all see the same value (latest committed write).
        // This test verifies concurrent PTA usage with high thread count, not MVCC versioning.
        var ids = new EntityId[entityCount];
        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                var sv = new PtaSingleVersion(i);
                ids[i] = t.Spawn<PtaArchSingleVersion>(PtaArchSingleVersion.Data.Set(in sv));
            }
            t.Commit();
        }

        // Create multiple accessors — all at different TSNs but SV values are the same
        // Each accessor gets enough workers for the threads that will use it (threadCount/3 threads per accessor, rounded up)
        int workersPerAccessor = (threadCount + 2) / 3;
        var accessors = new PointInTimeAccessor[3];
        for (int s = 0; s < 3; s++)
        {
            accessors[s] = PointInTimeAccessor.Create(dbe, workersPerAccessor);
        }

        // All threads read through different accessors concurrently
        var barrier = new Barrier(threadCount);
        var errors = new ConcurrentBag<Exception>();

        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, threadIdx =>
        {
            barrier.SignalAndWait();
            int accIdx = threadIdx % 3;
            int workerIdx = threadIdx / 3;
            var acc = accessors[accIdx];
            var wa = acc.GetWorkerAccessor(workerIdx);

            try
            {
                // Each thread reads all entities, verifying correct values
                for (int i = 0; i < entityCount; i++)
                {
                    var e = wa.Open(ids[i]);
                    var val = e.Read(PtaArchSingleVersion.Data).Value;
                    if (val != i)
                    {
                        errors.Add(new Exception(
                            $"T{threadIdx} A{accIdx}: entity[{i}] expected {i}, got {val}"));
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        Assert.That(errors, Is.Empty, () => $"Errors ({errors.Count}):\n" + string.Join("\n", errors));

        foreach (var acc in accessors)
        {
            acc.Dispose();
        }
    }
}
