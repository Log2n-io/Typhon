using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── Test components for #837 ───────────────────────────────────────────────────
// Plain SingleVersion, no index, no DefaultDiscipline: a transaction touching only these stays on the DEFAULT TickFence
// discipline, which is the regime the defect was reported in.
[Component("Typhon.Test.SpawnFence.SfPosition", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SfPosition
{
    public float X, Y;
    public SfPosition(float x, float y) { X = x; Y = y; }
}

// DefaultDiscipline=Commit — writing this escalates the whole transaction (CM-02), giving the OTHER durability regime.
[Component("Typhon.Test.SpawnFence.SfPurse", 1, StorageMode = StorageMode.SingleVersion, DefaultDiscipline = CommitDiscipline.Commit)]
[StructLayout(LayoutKind.Sequential)]
struct SfPurse
{
    public long Gold;
    public SfPurse(long gold) { Gold = gold; }
}

[Archetype]
partial class SfEntity : Archetype<SfEntity>
{
    public static readonly Comp<SfPosition> Position = Register<SfPosition>();
}

[Archetype]
partial class SfCommitEntity : Archetype<SfCommitEntity>
{
    public static readonly Comp<SfPosition> Position = Register<SfPosition>();
    public static readonly Comp<SfPurse> Purse = Register<SfPurse>();
}

/// <summary>
/// Rule <b>DIRTY-01</b> (<c>rules/ecs.md</c>): a spawn does not set a dirty bit — the dirty bitmaps track write mutations
/// to entities that are already PUBLISHED.
/// <para>
/// The defect (#837): spawning an entity and writing one of its SingleVersion components in the SAME transaction — "build
/// the object completely, then commit it", the common shape — poisoned every tick the system ran in. The write reaches
/// the pre-publish branch of <c>EntityRef.Write</c>, because a spawn has no cluster slot until <c>FinalizeSpawns</c>
/// claims one at commit, and that branch marked the SPAWN-STAGING chunk in <c>ComponentTable.DirtyBitmap</c>. At the
/// fence, <c>ProcessTableFence</c> read the entity PK from chunk offset 0 and got zero — the PK is stamped into the
/// staging chunk only for Transient slots — so <c>GetMetaByRouting(0)</c> returned null and the fence threw.
/// </para>
/// <para>
/// In Release that throw is <b>silent</b>: the scheduler logs it, calls a callback that is null unless the host
/// subscribed, drops the tick and keeps running. Measured over 55 s: 6,616 fence exceptions, 6,616 leaked
/// <c>UnitOfWork</c> objects (exactly 1:1 with poisoned ticks), 321 MB of WAL, and <c>CurrentTickNumber</c> frozen at 0
/// because the throw escapes before the counter's only mutation. The engine does not crash and does not block — its
/// clock simply stops while it keeps burning CPU and disk.
/// </para>
/// <para>
/// Nothing was lost by removing the bit. Under TickFence discipline a spawn's SingleVersion values were never WAL-logged
/// in the first place (they are checkpoint-durable by design); under Commit discipline the spawn's own CM-06 Slot record
/// carries them, and it is built AFTER the in-place write because own-spawns deliberately skip write staging (#713).
/// </para>
/// </summary>
[TestFixture]
// The clock test starts a two-worker runtime and asserts on tick cadence, which is why the other runtime fixtures that do
// the same (CheckerboardTests, MultiArchetypeClusterDispatchTests) carry this too.
[NonParallelizable]
class SpawnThenWriteFenceTests : TestBase<SpawnThenWriteFenceTests>
{
    /// <summary>
    /// Distinctive substring of the verifier's own rejection message — <see cref="RuleMutants.AssertDetects"/> requires
    /// the mutant to fail on THIS assertion rather than on its scaffolding.
    /// </summary>
    private const string Dirty01Marker = "DIRTY-01 violated: a spawn left a dirty bit";

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SfPosition>();
        dbe.RegisterComponentFromAccessor<SfPurse>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Spawn one entity and write one of its SingleVersion components in the same transaction.</summary>
    private static EntityId SpawnThenWrite(DatabaseEngine dbe, float newX)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<SfEntity>(SfEntity.Position.Set(new SfPosition(1, 2)));
        tx.OpenMut(id).Write(SfEntity.Position).X = newX;
        tx.Commit();
        return id;
    }

    /// <summary>
    /// The shared assertion, used by both the verifier and its mutant so the mutant is guaranteed to fail on the
    /// verifier's own message.
    /// </summary>
    private static void AssertNoDirtyBit(DatabaseEngine dbe)
    {
        var table = dbe.GetComponentTable<SfPosition>();
        Assert.That(table.DirtyBitmap, Is.Not.Null, "the SingleVersion table must have a dirty bitmap at all");
        Assert.That(table.DirtyBitmap.HasDirty, Is.False,
            $"{Dirty01Marker}: the per-ComponentTable bitmap tracks write mutations to PUBLISHED entities, and "
            + "FinalizeSpawns deliberately sets neither it nor ClusterDirtyBitmap for a spawn. A bit set here names a "
            + "spawn-staging chunk, whose overhead carries no entity PK for a non-Transient slot — so the fence reads "
            + "PK 0, GetMetaByRouting(0) returns null, and the tick dies (#837).");

        // DIRTY-01 names BOTH bitmaps, so assert both — a verifier that checks half an invariant reports more confidence
        // than it earned. FinalizeSpawns is what must leave this one alone; the gate under test is the other half.
        var clusterState = dbe._archetypeStates[Archetype<SfEntity>.Metadata.ArchetypeId]?.ClusterState;
        Assert.That(clusterState, Is.Not.Null, "the archetype must be cluster-backed, or this fixture is testing a dead path");
        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.False,
            $"{Dirty01Marker}: FinalizeSpawns must not mark a spawn in ClusterDirtyBitmap either — that bitmap drives "
            + "change-filtered dispatch, and a spawn is not a mutation of a published entity.");
    }

    [Test]
    [VerifiesRule("DIRTY-01")]
    public void OwnSpawnWrite_LeavesTheTableDirtyBitmapClean()
    {
        using var dbe = SetupEngine();

        SpawnThenWrite(dbe, 42f);

        AssertNoDirtyBit(dbe);
    }

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion: reproduces the pre-fix state by marking the staging chunk itself,
    /// and requires the verifier's assertion to reject it. Without this the verifier could be green because the bitmap is
    /// never populated by anything, rather than because the spawn path stopped populating it.
    /// </summary>
    [Test]
    [RuleMutant("DIRTY-01")]
    public void OwnSpawnWrite_ThatMarksTheStagingChunk_IsRejected()
    {
        RuleMutants.AssertDetects("DIRTY-01", Dirty01Marker, () =>
        {
            using var dbe = SetupEngine();

            SpawnThenWrite(dbe, 42f);

            // Exactly what WriteEcsComponentData used to do on the own-spawn path: mark the spawn-staging content chunk.
            dbe.GetComponentTable<SfPosition>().DirtyBitmap.Set(1);

            AssertNoDirtyBit(dbe);
        });
    }

    /// <summary>
    /// End to end under the DEFAULT TickFence discipline: the fence must survive the sequence and the write must stick.
    /// Pre-fix this threw a <see cref="NullReferenceException"/> out of the fence in Release, and died on the epoch-scope
    /// guard in Debug.
    /// </summary>
    [Test]
    public void SpawnThenWrite_ThenTickFence_TickFenceDiscipline()
    {
        using var dbe = SetupEngine();

        var id = SpawnThenWrite(dbe, 42f);

        Assert.DoesNotThrow(() => dbe.WriteTickFence(1),
            "a tick fence following spawn-then-write in one transaction must complete — the staging chunk must never "
            + "reach the fence's dirty set (#837)");

        using var read = dbe.CreateQuickTransaction();
        ref readonly var pos = ref read.Open(id).Read(SfEntity.Position);
        Assert.That(pos.X, Is.EqualTo(42f), "the same-transaction write must survive");
        Assert.That(pos.Y, Is.EqualTo(2f), "and must not have clobbered the untouched field");
    }

    /// <summary>
    /// The same sequence under Commit discipline, where the durability path is different: the spawn's own CM-06 Slot
    /// record carries the values, built from the staging chunk after the in-place write.
    /// </summary>
    [Test]
    public void SpawnThenWrite_ThenTickFence_CommitDiscipline()
    {
        using var dbe = SetupEngine();

        EntityId id;

        // Ask for Commit discipline EXPLICITLY. Relying on SfPurse's DefaultDiscipline to escalate does not work here:
        // CM-02 escalation runs from the EntityRef write paths, and `Spawn(...Purse.Set(...))` writes no EntityRef — so a
        // transaction that only ever Writes SfPosition stays on TickFence and this test silently duplicates the one above.
        // Nor can the escalation be forced by writing Purse afterwards: _didInPlaceSvWrite makes ResolveCommitDiscipline throw.
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit))
        {
            id = tx.Spawn<SfCommitEntity>(
                SfCommitEntity.Position.Set(new SfPosition(1, 2)),
                SfCommitEntity.Purse.Set(new SfPurse(7)));
            tx.OpenMut(id).Write(SfCommitEntity.Position).X = 42;
            tx.Commit();
        }

        Assert.DoesNotThrow(() => dbe.WriteTickFence(1), "the fence must complete under Commit discipline too");

        using var read = dbe.CreateQuickTransaction();
        var entity = read.Open(id);
        Assert.That(entity.Read(SfCommitEntity.Position).X, Is.EqualTo(42f));
        Assert.That(entity.Read(SfCommitEntity.Purse).Gold, Is.EqualTo(7L));
    }

    /// <summary>
    /// The field symptom, and the one no existing test would have caught: a system doing the sequence every tick must not
    /// stop the runtime's clock. Pre-fix <c>CurrentTickNumber</c> stayed at 0 forever while systems kept running, because
    /// the fence throw escapes <c>RunParallelFence</c> before the counter's only increment.
    /// </summary>
    [Test]
    public void SpawnThenWrite_EveryTick_LeavesTheRuntimeClockAdvancing()
    {
        using var dbe = SetupEngine();

        var systemRuns = 0;
        Exception unhandled = null;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("SpawnAndWrite", ctx =>
            {
                Interlocked.Increment(ref systemRuns);
                var id = ctx.Transaction.Spawn<SfEntity>(SfEntity.Position.Set(new SfPosition(1, 2)));
                ctx.Transaction.OpenMut(id).Write(SfEntity.Position).X = 42;
            });
        }, new RuntimeOptions
        {
            WorkerCount = 2,
            BaseTickRate = 1000,
            EnableParallelFence = true,
        });

        runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);

        runtime.Start();
        // 5 ticks at 1000 Hz is ~5 ms; a second is generous headroom on a loaded box without spinning hot for five.
        SpinWait.SpinUntil(() => Volatile.Read(ref systemRuns) >= 5, TimeSpan.FromSeconds(1));
        var observedTick = runtime.CurrentTickNumber;
        runtime.Shutdown();

        Assert.That(unhandled, Is.Null,
            $"the fence must not throw on a spawn-then-write tick — in Release this is swallowed, which is why it went "
            + $"unnoticed (#837). Got: {unhandled}");
        Assert.That(systemRuns, Is.GreaterThanOrEqualTo(5), "the system itself must have run");
        Assert.That(observedTick, Is.GreaterThan(0),
            "CurrentTickNumber must advance — a poisoned fence escapes before the counter's only mutation, so the "
            + "runtime reports tick 0 forever while continuing to execute systems and burn WAL");
    }
}
