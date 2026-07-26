using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Deferred (lazy) revision-chain resolution — mixed SV + Versioned cluster archetype.
//
// EntityAccessor.ResolveEntity used to walk EVERY Versioned slot's revision chain on every open, whether or not the caller ever touched that component.
// It now stashes only the chain ROOT and defers the walk to the first read of that slot. These tests pin the contract that makes the deferral safe:
// walking later must resolve to the SAME revision, because visibility is a function of the accessor's TSN and not of when the walk happens.
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.LazyV.SvPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct LazyVSvPos
{
    public float X, Y;
    public LazyVSvPos(float x, float y) { X = x; Y = y; }
}

[Component("Typhon.Test.LazyV.VGold", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct LazyVGold
{
    public long Amount;
    public LazyVGold(long amount) { Amount = amount; }
}

/// <summary>Mixed archetype: one SingleVersion slot (cluster-resident) + one Versioned slot (revision-chain resident).</summary>
[Archetype]
partial class LazyVMixed : Archetype<LazyVMixed>
{
    public static readonly Comp<LazyVSvPos> Pos = Register<LazyVSvPos>();
    public static readonly Comp<LazyVGold> Gold = Register<LazyVGold>();
}

[TestFixture]
class LazyVersionedResolveTests : TestBase<LazyVersionedResolveTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<LazyVSvPos>();
        dbe.RegisterComponentFromAccessor<LazyVGold>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId Spawn(DatabaseEngine dbe, long gold)
    {
        using var tx = dbe.CreateQuickTransaction();
        var pos = new LazyVSvPos(1, 2);
        var g = new LazyVGold(gold);
        var id = tx.Spawn<LazyVMixed>(LazyVMixed.Pos.Set(in pos), LazyVMixed.Gold.Set(in g));
        tx.Commit();
        return id;
    }

    private static void SetGold(DatabaseEngine dbe, EntityId id, long gold)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.OpenMut(id).Write(LazyVMixed.Gold).Amount = gold;
        tx.Commit();
    }

    [Test]
    public void DeferredWalk_ResolvesLatestVisibleRevision()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe, 100);
        SetGold(dbe, id, 250);
        SetGold(dbe, id, 375);   // three revisions in the chain

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var e = wa.Open(id);

        Assert.That(e.Read(LazyVMixed.Gold).Amount, Is.EqualTo(375), "deferred walk must land on the newest revision visible at this TSN");
    }

    [Test]
    public void SnapshotHeld_WhenRefIsOpenedBeforeAConcurrentCommit()
    {
        // THE test that separates lazy from eager. Eagerly, the content chunk was resolved at Open. Lazily, it is resolved at Read — which here happens
        // AFTER another transaction has committed a newer revision. The accessor's TSN is fixed, so the answer must be identical either way.
        using var dbe = SetupEngine();
        var id = Spawn(dbe, 100);

        // A PointInTimeAccessor is a FROZEN TSN, not a registered reader — Create() only calls AllocateTSN(), so it does not hold revisions back from
        // cleanup. Pin the chain with a real read-only transaction so this test measures snapshot fidelity and not reclamation timing.
        using var pin = dbe.CreateReadOnlyTransaction();

        using var accessor = PointInTimeAccessor.Create(dbe);   // TSN anchored here
        var wa = accessor.GetWorkerAccessor(0);
        var e = wa.Open(id);                                    // chain root stashed, NOT walked

        SetGold(dbe, id, 999);                                  // newer revision commits while the ref is held

        Assert.That(e.Read(LazyVMixed.Gold).Amount, Is.EqualTo(100), "the deferred walk must honour the accessor's snapshot, not the newest revision");
    }

    [Test]
    public void RepeatedReads_AreStableAndMemoized()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe, 100);
        SetGold(dbe, id, 200);

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var e = wa.Open(id);

        var first = e.Read(LazyVMixed.Gold).Amount;   // resolves + memoizes
        var second = e.Read(LazyVMixed.Gold).Amount;  // must take the memoized path
        var third = e.Read(LazyVMixed.Gold).Amount;

        Assert.That(first, Is.EqualTo(200));
        Assert.That(second, Is.EqualTo(first));
        Assert.That(third, Is.EqualTo(first));
    }

    [Test]
    public void TryRead_OnVersionedSlot_ResolvesLazily()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe, 100);
        SetGold(dbe, id, 512);

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var e = wa.Open(id);

        Assert.That(e.TryRead<LazyVGold>(out var gold), Is.True);
        Assert.That(gold.Amount, Is.EqualTo(512), "TryRead must trigger the deferred walk, not read an unresolved sentinel");
    }

    [Test]
    public void ReadRaw_OnVersionedSlot_ResolvesLazily()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe, 100);
        SetGold(dbe, id, 4242);

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var e = wa.Open(id);

        byte goldSlot = Archetype<LazyVMixed>.Metadata.GetSlot(ArchetypeRegistry.GetComponentTypeId<LazyVGold>());
        var raw = e.ReadRaw(goldSlot);

        Assert.That(raw.Length, Is.EqualTo(sizeof(long)), "ReadRaw must return the component payload, not the empty span of an unresolved slot");
        Assert.That(MemoryMarshal.Read<long>(raw), Is.EqualTo(4242));
    }

    [Test]
    public void ReadByType_OnVersionedSlot_ResolvesLazily()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe, 100);
        SetGold(dbe, id, 77);

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var e = wa.Open(id);

        Assert.That(e.Read<LazyVGold>().Amount, Is.EqualTo(77), "the by-type Read overload must also trigger the deferred walk");
    }

    [Test]
    public void SvOnlyAccess_NeverTouchesTheVersionedSlot()
    {
        // The case the deferral exists for: a caller that reads only the SingleVersion component. Correctness is all we can assert here — the absence of
        // the chain walk is a performance property, measured in the profiler, not observable through the API.
        using var dbe = SetupEngine();
        var id = Spawn(dbe, 100);
        SetGold(dbe, id, 300);

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);
        var e = wa.Open(id);

        var pos = e.Read(LazyVMixed.Pos);
        Assert.That(pos.X, Is.EqualTo(1f));
        Assert.That(pos.Y, Is.EqualTo(2f));

        // ...and the Versioned slot is still correct if it IS read afterwards.
        Assert.That(e.Read(LazyVMixed.Gold).Amount, Is.EqualTo(300));
    }

    [Test]
    public void MultipleEntities_EachResolveIndependently()
    {
        using var dbe = SetupEngine();
        var a = Spawn(dbe, 10);
        var b = Spawn(dbe, 20);
        var c = Spawn(dbe, 30);
        SetGold(dbe, b, 999);

        using var accessor = PointInTimeAccessor.Create(dbe);
        var wa = accessor.GetWorkerAccessor(0);

        // Interleaved opens through one accessor — a stale pending mask or a shared chain root would cross-contaminate here.
        Assert.That(wa.Open(a).Read(LazyVMixed.Gold).Amount, Is.EqualTo(10));
        Assert.That(wa.Open(b).Read(LazyVMixed.Gold).Amount, Is.EqualTo(999));
        Assert.That(wa.Open(c).Read(LazyVMixed.Gold).Amount, Is.EqualTo(30));
        Assert.That(wa.Open(a).Read(LazyVMixed.Gold).Amount, Is.EqualTo(10), "re-opening must resolve afresh, not reuse another entity's memoized location");
    }
}
