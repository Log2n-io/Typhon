using System;
using System.Collections.Generic;
using System.Numerics;
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

    /// <summary>
    /// Optional post-recovery phase (#705 T3): mutate the RECOVERED engine and keep recording into the same shadow, so the run continues past the reopen
    /// instead of stopping at it. Run by <c>DifferentialRecoveryOracleTests.RecoverAndResume</c>, which then crashes a second time and re-asserts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is an interface change and not another test.</b> Nothing in the crash suite writes after recovery — <c>RecoveryOracle</c> has zero
    /// <c>Spawn&lt;</c> / <c>OpenMut</c> / <c>Destroy(</c> call sites, and every post-crash assertion in <c>TrueCrashE2ETests</c>,
    /// <c>DifferentialRecoveryOracleTests</c> and <c>WalCrashSweepTests</c> reopens, verifies and stops. A recovery that restores the DATA correctly while
    /// leaving an allocator, counter or watermark wrong is therefore invisible to all of them **by construction**, no matter how many workloads are added
    /// (#702 §3.1). #697 is that class; this method is what makes it reachable.
    /// </para>
    /// <para>
    /// Default-implemented as a no-op so the existing workloads compile unchanged. That is safe only because the harness REQUIRES the shadow to have grown
    /// across the call — a silently no-op <c>Resume</c> would otherwise be a test that reports post-recovery-write coverage it never performed, which is the
    /// trap #704 hit when a widened axis produced case names a fixture body never exercised.
    /// </para>
    /// </remarks>
    void Resume(UnitOfWork uow, RecoveryShadowModel shadow)
    {
    }
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
    private readonly DurabilityDiscipline _discipline;
    private readonly int _keyBase;

    // discipline TickFence (default): the SV spawn values are checkpoint-durable only (a hard crash before a checkpoint recovers them alive-but-default
    // — by design, #395 Face B's "non-guarantee"). discipline Commit: the spawn WAL-logs its SV values per-commit (#395 Face B fix / D5), so they
    // survive a hard crash with NO checkpoint — the per-commit-durable mode the differential oracle's "SurvivesCrash" assertion needs.
    // keyBase offsets K so two phases (before / after a checkpoint) don't collide on SvIndexed.K's UNIQUE index — same role as IndexedFlatWorkload's.
    public ClusterAllSvWorkload(int count = 10, DurabilityDiscipline discipline = DurabilityDiscipline.TickFence, int keyBase = 0)
    {
        _count = count;
        _discipline = discipline;
        _keyBase = keyBase;
    }

    public string Name => "ClusterAllSv";

    public void Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<SvIndexed>();
    }

    public void Execute(UnitOfWork uow, RecoveryShadowModel shadow)
    {
        using var tx = uow.CreateTransaction(_discipline);
        for (int i = 0; i < _count; i++)
        {
            var k = i + _keyBase;
            var s = new SvIndexed(k * 7, k);
            var id = tx.Spawn<SvIndexedArch>(SvIndexedArch.S.Set(in s));
            shadow.RecordSpawn(id);
        }

        tx.Commit();
    }
}

/// <summary>
/// The cluster counterpart of <see cref="MultiValueDupKeyWorkload"/> (#656): N entities of a cluster-backed archetype whose indexed field is
/// <c>AllowMultiple</c>, spread over <c>groups</c> duplicate keys. Commit discipline, so the SV spawn values are WAL-durable per commit and the crash needs no
/// checkpoint to be meaningful.
/// </summary>
internal sealed class ClusterMultiValueDupKeyWorkload : IRecoveryWorkload
{
    private readonly int _count;
    private readonly int _groups;

    public ClusterMultiValueDupKeyWorkload(int count, int groups)
    {
        _count = count;
        _groups = groups;
    }

    public string Name => "ClusterMultiValueDupKey";

    public void Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<SvMultiIndexed>();
    }

    public void Execute(UnitOfWork uow, RecoveryShadowModel shadow)
    {
        using var tx = uow.CreateTransaction(DurabilityDiscipline.Commit);
        for (var i = 0; i < _count; i++)
        {
            var s = new SvMultiIndexed(i % _groups, i);
            var id = tx.Spawn<SvMultiIndexedArch>(SvMultiIndexedArch.S.Set(in s));
            shadow.RecordSpawn(id);
        }

        tx.Commit();
    }
}

/// <summary>
/// The P2 <b>MixedDiscipline</b> workload (design 08 §5 / §T-6): a cluster-eligible all-SingleVersion archetype written under a MIX of durability
/// disciplines in interleaved transactions — a TickFence (default, ≤1-tick-loss) noise write followed by a <see cref="DurabilityDiscipline.Commit"/>
/// write that overwrites BOTH components with their final values. Because the differential oracle asserts every recorded entity's exact post-recovery
/// state, the workload makes the asserted state entirely Commit-durable: the Commit write is the last writer on every component, so each captured value
/// is the zero-loss Commit value (the spawn + TickFence values are deliberately transient and overwritten). This proves Commit-discipline WRITES
/// survive every crash boundary despite interleaved TickFence churn. (Commit-discipline SPAWNS — the value carried by the spawn itself rather than a
/// later write — are #395 Face B, covered by <see cref="ClusterAllSvWorkload"/> under <see cref="DurabilityDiscipline.Commit"/>; a plain TickFence
/// <see cref="ClusterAllSvWorkload"/> remains checkpoint-durable only, the documented non-guarantee.)
/// </summary>
internal sealed class MixedDisciplineWorkload : IRecoveryWorkload
{
    private readonly int _count;

    public MixedDisciplineWorkload(int count = 8) => _count = count;

    public string Name => "MixedDiscipline";

    public void Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<MixA>();
        dbe.RegisterComponentFromAccessor<MixB>();
    }

    public void Execute(UnitOfWork uow, RecoveryShadowModel shadow)
    {
        var ids = new List<EntityId>(_count);

        // Phase 1: spawn with placeholder values (default/TickFence discipline). The cluster SV spawn values are not WAL-durable on their own; the
        // Commit writes below carry the entity into durability.
        using (var tx = uow.CreateTransaction())
        {
            for (int i = 0; i < _count; i++)
            {
                var id = tx.Spawn<MixArch>(MixArch.A.Set(new MixA(-1)), MixArch.B.Set(new MixB(-1)));
                shadow.RecordSpawn(id);
                ids.Add(id);
            }

            tx.Commit();
        }

        // Phase 2: TickFence (default discipline) noise write on A — NOT WAL-durable; the Phase-3 Commit write is the last writer on A, so the
        // captured/recovered value is the Commit value, never this noise.
        using (var tx = uow.CreateTransaction())
        {
            foreach (var id in ids)
            {
                tx.OpenMut(id).Write(MixArch.A).X = unchecked((int)0xBADBAD);
            }

            tx.Commit();
        }

        // Phase 3: Commit discipline — overwrite BOTH components with their final, zero-loss values (last writer on every asserted field).
        using (var tx = uow.CreateTransaction(DurabilityDiscipline.Commit))
        {
            for (int i = 0; i < ids.Count; i++)
            {
                var e = tx.OpenMut(ids[i]);
                e.Write(MixArch.A).X = i + 1_000;
                e.Write(MixArch.B).Y = i + 2_000;
            }

            tx.Commit();
        }
    }
}

/// <summary>
/// Reads an entity's <c>ComponentCollection</c> ELEMENTS so the oracle can compare them (#705 T3 / #389).
/// </summary>
/// <remarks>
/// A projector rather than reflection inside <see cref="RecoveryShadowModel"/>: reading a collection needs the element type at compile time
/// (<c>Transaction.CreateComponentCollectionAccessor&lt;T&gt;</c>), and the workload is the one place that already knows it. The shadow enforces that a
/// collection-bearing archetype HAS one — see <c>RecoveryShadowModel.AssertCollectionsAreObservable</c> — so the type-safety is bought without leaving the
/// false-green reachable by omission.
/// </remarks>
internal interface ICollectionProjector
{
    /// <summary>The elements of every collection field the entity carries, in a stable field order.</summary>
    IReadOnlyList<int[]> Project(Transaction tx, EntityId id);
}

/// <summary>The storage shapes the #705 workloads can carry. One axis, three values — not three near-duplicate workload classes.</summary>
internal enum PostRecoveryShape
{
    /// <summary>Flat, Versioned, non-indexed (CompA) — #697's own minimal repro: a component with no collection data at all.</summary>
    Flat,

    /// <summary>Flat, Versioned, UNIQUE-indexed (CompD.B) — the sharp variant: a re-issued key violates the index and fails loudly, not silently.</summary>
    FlatIndexed,

    /// <summary>Cluster-backed all-SingleVersion under the Commit discipline (SvIndexed) — the other storage home, which has its own watermark path.</summary>
    ClusterSv,
}

/// <summary>
/// The per-shape register / spawn / update primitives, in ONE place (#705 T3).
/// </summary>
/// <remarks>
/// #704's fourth trap was a kit whose methods drifted apart as shapes were added: a composition one method handled and another did not fell through and wrote a
/// DIFFERENT archetype's component instead of refusing. Every switch here throws on an unhandled shape for that reason — adding a shape must break loudly at
/// compile-adjacent time, not silently produce a green test that exercised the wrong archetype.
/// </remarks>
internal static class ShapeOps
{
    public static void Register(DatabaseEngine dbe, PostRecoveryShape shape)
    {
        switch (shape)
        {
            case PostRecoveryShape.Flat:
                dbe.RegisterComponentFromAccessor<CompA>();
                break;
            case PostRecoveryShape.FlatIndexed:
                dbe.RegisterComponentFromAccessor<CompD>();
                break;
            case PostRecoveryShape.ClusterSv:
                dbe.RegisterComponentFromAccessor<SvIndexed>();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "unhandled shape");
        }
    }

    /// <summary>
    /// Begin a transaction appropriate to the shape. The cluster shape uses <see cref="DurabilityDiscipline.Commit"/>: a plain TickFence SingleVersion write is
    /// checkpoint-durable only (#395 Face B), so asserting it survives a hard crash would be asserting a documented NON-guarantee. Commit discipline is the
    /// mode that promises zero loss, which makes a dropped value unambiguously a defect rather than a contract the test misread — trap 1 from #704.
    /// </summary>
    public static Transaction Begin(UnitOfWork uow, PostRecoveryShape shape)
        => shape == PostRecoveryShape.ClusterSv ? uow.CreateTransaction(DurabilityDiscipline.Commit) : uow.CreateTransaction();

    public static EntityId Spawn(Transaction tx, PostRecoveryShape shape, int k) => shape switch
    {
        PostRecoveryShape.Flat => tx.Spawn<CompAArch>(CompAArch.A.Set(new CompA(k + 1, k, k))),
        PostRecoveryShape.FlatIndexed => tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(k * 1.5f, k * 100, k * 2.5))),
        PostRecoveryShape.ClusterSv => tx.Spawn<SvIndexedArch>(SvIndexedArch.S.Set(new SvIndexed(k * 7, k))),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unhandled shape"),
    };

    /// <summary>Overwrite an EXISTING entity's component with the value <paramref name="k"/> encodes. Indexed keys stay distinct across any k.</summary>
    public static void Update(Transaction tx, PostRecoveryShape shape, EntityId id, int k)
    {
        switch (shape)
        {
            case PostRecoveryShape.Flat:
                tx.OpenMut(id).Write(CompAArch.A) = new CompA(k + 1, k, k);
                break;
            case PostRecoveryShape.FlatIndexed:
                tx.OpenMut(id).Write(CompDArch.D) = new CompD(k * 1.5f, k * 100, k * 2.5);
                break;
            case PostRecoveryShape.ClusterSv:
                tx.OpenMut(id).Write(SvIndexedArch.S) = new SvIndexed(k * 7, k);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "unhandled shape");
        }
    }
}

/// <summary>
/// The cross-frontier update workload (#705 T3 / #569): it spawns NOTHING and updates entities a previous phase already committed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a constructor taking the alive-set is the whole point.</b> Every pre-existing workload builds its own entities in a <c>List&lt;EntityId&gt;</c> local
/// inside <c>Execute</c>, so the two windows of <c>RecoverWithMidCheckpoint</c> always touched DISJOINT entity sets — each phase spawned its own with a
/// disjoint <c>keyBase</c>. "An entity checkpointed in window 1 and UPDATED in window 2" was therefore not merely untested but inexpressible, and no number of
/// additional workloads through that interface could produce it (#702 §3.1).
/// </para>
/// <para>
/// <c>RecoveryDriver</c>'s aggregation loop drops the aggregated Slot payloads for any entity with no Spawn in the window — regardless of storage mode. #569 is
/// titled for SingleVersion, but the Versioned flat shapes below take the same branch, which is why all three run.
/// </para>
/// </remarks>
internal sealed class CrossFrontierUpdateWorkload : IRecoveryWorkload
{
    /// <summary>Offset applied to every updated payload, so the post-update value is distinguishable from the pre-update one on every field.</summary>
    public const int UpdateKeyOffset = 500;

    /// <summary>Offset for the FIRST pass when <c>passes == 2</c>. Far from <see cref="UpdateKeyOffset"/> so a stale value is unmistakable.</summary>
    public const int SupersededKeyOffset = 9_000;

    private readonly PostRecoveryShape _shape;
    private readonly EntityId[] _existing;
    private readonly int _passes;

    /// <param name="shape">Storage home to exercise.</param>
    /// <param name="existing">Entities a previous phase committed. Must be non-empty — see the constructor guard.</param>
    /// <param name="passes">
    /// 1 = a single update per entity. 2 = two updates in SEPARATE committed transactions, so the window holds two Slot records for the same (entity, slot)
    /// and the aggregation's latest-wins rule is what decides the recovered value — #569's CM-03 acceptance criterion. The first pass writes a deliberately
    /// distinct value, so applying the wrong record produces a diff rather than a coincidence.
    /// </param>
    public CrossFrontierUpdateWorkload(PostRecoveryShape shape, IReadOnlyCollection<EntityId> existing, int passes = 1)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (existing.Count == 0)
        {
            throw new ArgumentException(
                "the cross-frontier workload updates entities a PREVIOUS phase committed; an empty alive-set means it would assert nothing", nameof(existing));
        }

        if (passes is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(passes), passes, "1 (plain update) or 2 (last-writer-wins)");
        }

        _shape = shape;
        _existing = [.. existing];
        _passes = passes;
    }

    public string Name => $"CrossFrontierUpdate_{_shape}_x{_passes}";

    public void Register(DatabaseEngine dbe) => ShapeOps.Register(dbe, _shape);

    public void Execute(UnitOfWork uow, RecoveryShadowModel shadow)
    {
        // Pass 1 of 2 is a SEPARATE committed transaction, so its Slot records carry lower LSNs than pass 2's. One transaction writing twice would collapse
        // into a single record and test nothing about ordering.
        if (_passes == 2)
        {
            WritePass(uow, SupersededKeyOffset);
        }

        WritePass(uow, UpdateKeyOffset);

        // No RecordSpawn / RecordDestroy: the alive-set is unchanged. CaptureValues reads the FINAL values back before the crash, so the shadow expects the
        // last write — which is exactly the claim TickFence's ≤1-tick window makes and #569 says is not delivered.
    }

    private void WritePass(UnitOfWork uow, int keyOffset)
    {
        using var tx = ShapeOps.Begin(uow, _shape);
        for (var i = 0; i < _existing.Length; i++)
        {
            ShapeOps.Update(tx, _shape, _existing[i], i + keyOffset);
        }

        tx.Commit();
    }
}

/// <summary>
/// The <c>Resume</c> exemplar (#705 T3 / #697): spawn a generation, crash, recover, then <b>spawn a second generation on the recovered engine</b>.
/// </summary>
/// <remarks>
/// <para>
/// The assertion lives in <see cref="RecoveryShadowModel.RecordSpawn"/>, not here. If recovery restored the entity-key watermark too low, the second
/// generation's ids collide with live recovered entities, and the shadow rejects the duplicate naming #697. Putting the check in the shadow rather than the
/// workload means EVERY workload that ever gains a <c>Resume</c> inherits it, instead of each one having to remember.
/// </para>
/// <para>
/// The two generations use disjoint key bases so the UNIQUE index in <see cref="PostRecoveryShape.FlatIndexed"/> only ever rejects a genuine id collision,
/// never a payload one the workload created itself.
/// </para>
/// </remarks>
internal sealed class PostRecoveryWriteWorkload : IRecoveryWorkload
{
    private const int ResumeKeyBase = 10_000;

    private readonly PostRecoveryShape _shape;
    private readonly int _preCount;
    private readonly int _postCount;

    public PostRecoveryWriteWorkload(PostRecoveryShape shape, int preCount = 8, int postCount = 4)
    {
        _shape = shape;
        _preCount = preCount;
        _postCount = postCount;
    }

    public string Name => $"PostRecoveryWrite_{_shape}";

    public void Register(DatabaseEngine dbe) => ShapeOps.Register(dbe, _shape);

    public void Execute(UnitOfWork uow, RecoveryShadowModel shadow) => SpawnGeneration(uow, shadow, _preCount, keyBase: 0);

    public void Resume(UnitOfWork uow, RecoveryShadowModel shadow) => SpawnGeneration(uow, shadow, _postCount, ResumeKeyBase);

    private void SpawnGeneration(UnitOfWork uow, RecoveryShadowModel shadow, int count, int keyBase)
    {
        using var tx = ShapeOps.Begin(uow, _shape);
        for (var i = 0; i < count; i++)
        {
            shadow.RecordSpawn(ShapeOps.Spawn(tx, _shape, i + keyBase));
        }

        tx.Commit();
    }
}

// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// #705 T3 — the PAYLOAD axes: ComponentCollection, String64 and spatial (#389)
//
// `grep ComponentCollection RecoveryWorkloads.cs` returned 0 before this. Every workload carried plain blittable scalars, so the recovery oracle had never
// compared a variable-size buffer, a String64, or a spatially-indexed field — three payload kinds with their own persistence machinery.
// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A SingleVersion component carrying all three payload axes at once: a variable-size collection, a fixed 64-byte string, and a spatial box.
/// </summary>
/// <remarks>
/// One component rather than three, because the axes are cheap to carry together and the question here is whether recovery reproduces each payload KIND — not
/// how they interact. SingleVersion + no indexed field keeps the archetype cluster-eligible, which is where <c>ClusterState.CollectionSlots</c> tracks CC
/// buffers and therefore where the interesting ownership lives.
/// </remarks>
[Component("Typhon.Schema.UnitTest.PayloadBag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PayloadBag
{
    [Field]
    public ComponentCollection<int> Items;

    [Field]
    [SpatialIndex(0.0f)]
    public AABB3F Bounds;

    [Field]
    public String64 Name;

    public int Seq;
}

[Archetype]
internal class PayloadBagArch : Archetype<PayloadBagArch>
{
    public static readonly Comp<PayloadBag> P = Register<PayloadBag>();
}

/// <summary>
/// Spawns entities carrying a collection, a <c>String64</c> and a spatial box, so the recovery oracle finally compares payload kinds it never saw (#705 T3).
/// </summary>
/// <remarks>
/// <b>Element count varies per entity</b> (<c>(i % 4) + 1</c>), the same choice <c>ComponentCollectionMatrixTests</c> makes: a constant length would let a
/// defect that hands every entity the SAME buffer read as correct, whereas a varying length fails on the count — earlier, and localised.
/// </remarks>
internal sealed class PayloadPayloadWorkload : IRecoveryWorkload, ICollectionProjector
{
    private readonly int _count;

    public PayloadPayloadWorkload(int count = 8) => _count = count;

    public string Name => "PayloadBag";

    public static int ElementCountOf(int i) => (i % 4) + 1;

    public static int ElementValue(int i, int element) => (i * 100) + element + 1;

    public void Register(DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        dbe.RegisterComponentFromAccessor<PayloadBag>();

        // A cluster-eligible archetype with a [SpatialIndex] field requires the grid BEFORE InitializeArchetypes (#230 Phase 3 Option B), and Register is the
        // workload hook that runs there — on both the pre-crash and the post-crash open, which is exactly what a reopen needs.
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(-1000f, -1000f),
            worldMax: new Vector2(1000f, 1000f),
            cellSize: 100f));
    }

    public void Execute(UnitOfWork uow, RecoveryShadowModel shadow)
    {
        using var tx = uow.CreateTransaction(DurabilityDiscipline.Commit);
        for (var i = 0; i < _count; i++)
        {
            var bag = new PayloadBag
            {
                Bounds = new AABB3F { MinX = i, MinY = i, MinZ = i, MaxX = i + 1, MaxY = i + 1, MaxZ = i + 1 },
                Name = (String64)$"entity-{i}",
                Seq = i,
            };

            // Fill the buffer on the LOCAL struct before Spawn, as ClusterComponentCollectionTests does. Spawning first and then writing through
            // OpenMut(id).Write(...) in the same transaction NREs inside Transaction.BuildCommitBatch's Commit-staged path — see #713.
            using (var cca = tx.CreateComponentCollectionAccessor(ref bag.Items))
            {
                for (var el = 0; el < ElementCountOf(i); el++)
                {
                    cca.Add(ElementValue(i, el));
                }
            }

            var id = tx.Spawn<PayloadBagArch>(PayloadBagArch.P.Set(in bag));
            shadow.RecordSpawn(id);
        }

        tx.Commit();
    }

    /// <summary>The single collection field's elements — what makes the buffer's CONTENT observable to the oracle rather than only its descriptor.</summary>
    public IReadOnlyList<int[]> Project(Transaction tx, EntityId id)
    {
        ArgumentNullException.ThrowIfNull(tx);
        var bag = tx.Open(id).Read(PayloadBagArch.P);
        using var cca = tx.CreateComponentCollectionAccessor(ref bag.Items);
        var items = new int[cca.ElementCount];
        cca.GetAllElements(items);
        return [items];
    }
}

// ── MixedDiscipline workload archetype: all-SingleVersion, non-indexed ⇒ cluster-eligible (DatabaseEngine.InitializeArchetypes). Id 952 is unused. ──

[Component("Typhon.Schema.UnitTest.MixA", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct MixA
{
    public int X;

    public MixA(int x) => X = x;
}

[Component("Typhon.Schema.UnitTest.MixB", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct MixB
{
    public int Y;

    public MixB(int y) => Y = y;
}

[Archetype]
internal class MixArch : Archetype<MixArch>
{
    public static readonly Comp<MixA> A = Register<MixA>();
    public static readonly Comp<MixB> B = Register<MixB>();
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

[Archetype]
internal class SvIndexedArch : Archetype<SvIndexedArch>
{
    public static readonly Comp<SvIndexed> S = Register<SvIndexed>();
}

// ── The cluster twin of CompD's multi-value axis (#656). AllowMultiple keys store a VSBS buffer root in the leaf rather than the location itself, so a
//    rebuild that handles the unique case can still lose every entity but one per key — and only a duplicate-key workload can tell the difference. ──

[Component("Typhon.Schema.UnitTest.SvMultiIndexed", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SvMultiIndexed
{
    [Index(AllowMultiple = true)]
    public int G;
    public int V;

    public SvMultiIndexed(int g, int v)
    {
        G = g;
        V = v;
    }
}

[Archetype]
internal class SvMultiIndexedArch : Archetype<SvMultiIndexedArch>
{
    public static readonly Comp<SvMultiIndexed> S = Register<SvMultiIndexed>();
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

[Archetype]
internal class FlatSvArch : Archetype<FlatSvArch>
{
    public static readonly Comp<SvForFlat> S = Register<SvForFlat>();
    public static readonly Comp<TransientIndexed> T = Register<TransientIndexed>();
}
