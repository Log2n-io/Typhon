using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// A crash-recovery workload (the T-6 library, design 08 §1). <see cref="Register"/> declares its components (called before <c>InitializeArchetypes</c> on both the
/// pre-crash and the post-crash open); <see cref="Execute"/> runs a committed op sequence on the given <see cref="UnitOfWork"/> and records the resulting alive-set into
/// the <see cref="RecoveryShadowModel"/> as it goes. The shadow's component values are captured separately by read-back (see <see cref="RecoveryShadowModel.CaptureValues"/>).
/// </summary>
internal interface IRecoveryWorkload
{
    string Name { get; }

    void Register(DatabaseEngine dbe);

    void Execute(UnitOfWork uow, RecoveryShadowModel shadow);
}

/// <summary>The simplest case: N CompA entities spawned in one committed transaction (flat, non-indexed, Versioned). Exercises increments 1–2 as a differential property.</summary>
internal sealed class SingleTxSpawnWorkload : IRecoveryWorkload
{
    private readonly int _count;

    public SingleTxSpawnWorkload(int count = 10) => _count = count;

    public string Name => "SingleTxSpawn";

    public void Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<CompA>();
        Archetype<CompAArch>.Touch();
    }

    public void Execute(UnitOfWork uow, RecoveryShadowModel shadow)
    {
        using var tx = uow.CreateTransaction();
        for (int i = 0; i < _count; i++)
        {
            var a = new CompA(i + 1, i, i);
            var id = tx.Spawn<CompAArch>(CompAArch.A.Set(in a));
            shadow.RecordSpawn(id);
        }

        tx.Commit();
    }
}

/// <summary>
/// A seeded-random churn over the flat two-component archetype CompAB: spawn all, update CompA on ~half, disable CompB on ~third, destroy ~quarter — each phase its own
/// committed transaction. Exercises the full flat lifecycle (spawn, post-spawn value update, enabled-bits change, destroy → net-dead-skip) as one differential property,
/// generalizing increments 1–4 + the enabled-bits path beyond their hand-picked asserts.
/// </summary>
internal sealed class LifecycleChurnWorkload : IRecoveryWorkload
{
    private readonly int _seed;
    private readonly int _count;

    public LifecycleChurnWorkload(int seed = 1234, int count = 24)
    {
        _seed = seed;
        _count = count;
    }

    public string Name => "LifecycleChurn";

    public void Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.RegisterComponentFromAccessor<CompB>();
        Archetype<CompABArch>.Touch();
    }

    public void Execute(UnitOfWork uow, RecoveryShadowModel shadow)
    {
        var rand = new Random(_seed);
        var live = new List<EntityId>(_count);

        // Phase 1: spawn all (both components enabled).
        using (var tx = uow.CreateTransaction())
        {
            for (int i = 0; i < _count; i++)
            {
                var a = new CompA(i + 1, i, i);
                var b = new CompB(i * 10, i);
                var id = tx.Spawn<CompABArch>(CompABArch.A.Set(in a), CompABArch.B.Set(in b));
                shadow.RecordSpawn(id);
                live.Add(id);
            }

            tx.Commit();
        }

        // Phase 2: update CompA on ~half (post-spawn value change → Slot Upsert).
        using (var tx = uow.CreateTransaction())
        {
            foreach (var id in live)
            {
                if (rand.Next(2) == 0)
                {
                    ref var w = ref tx.OpenMut(id).Write(CompABArch.A);
                    w = new CompA(rand.Next(), (float)rand.NextDouble(), rand.NextDouble());
                }
            }

            tx.Commit();
        }

        // Phase 3: disable CompB on ~third (SetEnabledBits; CompA stays enabled so the entity always has ≥1 enabled component).
        using (var tx = uow.CreateTransaction())
        {
            foreach (var id in live)
            {
                if (rand.Next(3) == 0)
                {
                    tx.OpenMut(id).Disable(CompABArch.B);
                }
            }

            tx.Commit();
        }

        // Phase 4: destroy ~quarter (spawn+…+destroy all in-window → recovery must leave them dead).
        using (var tx = uow.CreateTransaction())
        {
            foreach (var id in live)
            {
                if (rand.Next(4) == 0)
                {
                    tx.Destroy(id);
                    shadow.RecordDestroy(id);
                }
            }

            tx.Commit();
        }
    }
}

/// <summary>
/// N CompD entities (flat, Versioned, three indexed fields — A float / B int unique / C double) spawned in one committed transaction. CompD is pure-Versioned so it stays
/// on the legacy (flat) path; its entities recover via the existing applier, but its secondary B+Trees are not rebuilt — the substrate for the index-axis measurement.
/// </summary>
internal sealed class IndexedFlatWorkload : IRecoveryWorkload
{
    private readonly int _count;
    private readonly int _keyBase;

    // keyBase offsets the indexed values so two instances (e.g. a before-checkpoint and an after-checkpoint phase) don't collide on CompD.B's UNIQUE index.
    public IndexedFlatWorkload(int count = 10, int keyBase = 0)
    {
        _count = count;
        _keyBase = keyBase;
    }

    public string Name => "IndexedFlat";

    public void Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<CompD>();
        Archetype<CompDArch>.Touch();
    }

    public void Execute(UnitOfWork uow, RecoveryShadowModel shadow)
    {
        using var tx = uow.CreateTransaction();
        for (int i = 0; i < _count; i++)
        {
            var k = i + _keyBase;
            var d = new CompD(k * 1.5f, k * 100, k * 2.5);   // B = k*100 → distinct keys for the unique index
            var id = tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            shadow.RecordSpawn(id);
        }

        tx.Commit();
    }
}

/// <summary>
/// N CompD entities whose multi-value index A (<c>[Index(AllowMultiple)]</c> float) and C (double) deliberately REPEAT — the <c>count</c> entities spread over
/// <c>groups</c> distinct A/C values, so each multi-value key holds several entities. B stays unique (<c>= i</c>, the unique index forbids duplicates). Substrate for
/// the multi-value index-rebuild test: post-crash the rebuilt A index must return EVERY entity (all duplicate-key buffer members), not just one per key (RB-01).
/// </summary>
internal sealed class MultiValueDupKeyWorkload : IRecoveryWorkload
{
    private readonly int _count;
    private readonly int _groups;

    public MultiValueDupKeyWorkload(int count, int groups)
    {
        _count = count;
        _groups = groups;
    }

    public string Name => "MultiValueDupKey";

    public void Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<CompD>();
        Archetype<CompDArch>.Touch();
    }

    public void Execute(UnitOfWork uow, RecoveryShadowModel shadow)
    {
        using var tx = uow.CreateTransaction();
        for (int i = 0; i < _count; i++)
        {
            var g = i % _groups;
            var d = new CompD(g * 1.5f, i, g * 2.5);   // A & C repeat across groups (multi-value duplicate keys); B = i is unique
            var id = tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            shadow.RecordSpawn(id);
        }

        tx.Commit();
    }
}

/// <summary>
/// N SvIndexed entities (all-SingleVersion + indexed ⇒ cluster-eligible storage) spawned in one committed transaction. The flat-only applier does not restore cluster
/// storage, so this is the substrate for the cluster-axis measurement.
/// </summary>
internal sealed class ClusterAllSvWorkload : IRecoveryWorkload
{
    private readonly int _count;

    public ClusterAllSvWorkload(int count = 10) => _count = count;

    public string Name => "ClusterAllSv";

    public void Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<SvIndexed>();
        Archetype<SvIndexedArch>.Touch();
    }

    public void Execute(UnitOfWork uow, RecoveryShadowModel shadow)
    {
        using var tx = uow.CreateTransaction();
        for (int i = 0; i < _count; i++)
        {
            var s = new SvIndexed(i * 7, i);
            var id = tx.Spawn<SvIndexedArch>(SvIndexedArch.S.Set(in s));
            shadow.RecordSpawn(id);
        }

        tx.Commit();
    }
}

// ── An all-SingleVersion, indexed component + archetype: all-SV + a non-Transient indexed field ⇒ cluster-eligible (DatabaseEngine.InitializeArchetypes), the cluster
//    storage path the flat-only RecoveryApplier does not yet restore. Id 950 is unused by the existing test archetypes. ──

[Component("Typhon.Schema.UnitTest.SvIndexed", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SvIndexed
{
    [Index]
    public int K;
    public int V;

    public SvIndexed(int k, int v)
    {
        K = k;
        V = v;
    }
}

[Archetype(950)]
internal class SvIndexedArch : Archetype<SvIndexedArch>
{
    public static readonly Comp<SvIndexed> S = Register<SvIndexed>();
}

// ── The rare NON-rebuildable EntityMap residual: a non-cluster archetype that still owns a SingleVersion slot. An archetype is forced off the cluster path when it has a
//    Transient component with an indexed field (DatabaseEngine.InitializeArchetypes), so {SV slot + Transient-indexed slot} lands on the legacy flat path. Its SV slot
//    location has no persisted source (no cluster EntityKeys[N], no revision chain), so a torn EntityMap page there must loud-fail (RB-04), not silent-heal. Used by the
//    IsEntityMapRebuildable classifier test. Id 951 is unused. ──

[Component("Typhon.Schema.UnitTest.SvForFlat", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SvForFlat
{
    public int V;

    public SvForFlat(int v) => V = v;
}

[Component("Typhon.Schema.UnitTest.TransientIndexed", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
public struct TransientIndexed
{
    [Index]
    public int T;

    public TransientIndexed(int t) => T = t;
}

[Archetype(951)]
internal class FlatSvArch : Archetype<FlatSvArch>
{
    public static readonly Comp<SvForFlat> S = Register<SvForFlat>();
    public static readonly Comp<TransientIndexed> T = Register<TransientIndexed>();
}
