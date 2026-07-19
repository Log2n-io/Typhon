using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── Test components for the Committed durability discipline (issue #392) ──────────────────
[Component("Typhon.Test.Committed.CmPosition", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CmPosition
{
    public float X, Y;
    public CmPosition(float x, float y) { X = x; Y = y; }
}

// DefaultDiscipline=Commit — any tx that writes this component is escalated to Commit (CM-02).
[Component("Typhon.Test.Committed.CmWallet", 1, StorageMode = StorageMode.SingleVersion, DefaultDiscipline = DurabilityDiscipline.Commit)]
[StructLayout(LayoutKind.Sequential)]
struct CmWallet
{
    public long Gold;
    public CmWallet(long gold) { Gold = gold; }
}

[Archetype]
partial class CmEntity : Archetype<CmEntity>
{
    public static readonly Comp<CmPosition> Position = Register<CmPosition>();
    public static readonly Comp<CmWallet> Wallet = Register<CmWallet>();
}

// Indexed SV component — for the exact-index-at-commit test (AC-11 / CM-05).
[Component("Typhon.Test.Committed.CmTeam", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CmTeam
{
    [Index]
    public int TeamId;
    public int Rank;
}

[Archetype]
partial class CmIdxEntity : Archetype<CmIdxEntity>
{
    public static readonly Comp<CmPosition> Position = Register<CmPosition>();
    public static readonly Comp<CmTeam> Team = Register<CmTeam>();
}

// Indexed SV component written under Commit on the FLAT (non-cluster) path. The archetype is forced non-cluster-eligible by pairing it with a
// Transient+indexed component (engine rule: a Transient indexed field keeps the whole archetype on the legacy per-entity path).
[Component("Typhon.Test.Committed.CmFlatVal", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CmFlatVal
{
    [Index]
    public int Tag;
    public int Other;
}

[Component("Typhon.Test.Committed.CmTransIdx", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct CmTransIdx
{
    [Index]
    public int Key;
}

[Archetype]
partial class CmFlatEntity : Archetype<CmFlatEntity>
{
    public static readonly Comp<CmFlatVal> Val = Register<CmFlatVal>();
    public static readonly Comp<CmTransIdx> Idx = Register<CmTransIdx>();
}

[TestFixture]
[NonParallelizable]
class CommittedDisciplineTests : TestBase<CommittedDisciplineTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CmPosition>();
        dbe.RegisterComponentFromAccessor<CmWallet>();
        dbe.RegisterComponentFromAccessor<CmTeam>();
        dbe.RegisterComponentFromAccessor<CmFlatVal>();
        dbe.RegisterComponentFromAccessor<CmTransIdx>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId SpawnAnt(DatabaseEngine dbe, float x, float y, long gold)
    {
        using var tx = dbe.CreateQuickTransaction();
        var pos = new CmPosition(x, y);
        var wallet = new CmWallet(gold);
        var id = tx.Spawn<CmEntity>(CmEntity.Position.Set(in pos), CmEntity.Wallet.Set(in wallet));
        tx.Commit();
        return id;
    }

    // ── AC-1 / AC-2 (path): a Commit-discipline write publishes to HEAD at commit ──────────
    [Test]
    public void CommitDiscipline_Write_PublishesAtCommit()
    {
        using var dbe = SetupEngine();
        var id = SpawnAnt(dbe, 10, 20, 0);

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit))
        {
            var e = tx.OpenMut(id);
            ref var p = ref e.Write(CmEntity.Position);
            p.X = 99;
            p.Y = 88;
            tx.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        ref readonly var rp = ref read.Open(id).Read(CmEntity.Position);
        Assert.That(rp.X, Is.EqualTo(99f));
        Assert.That(rp.Y, Is.EqualTo(88f));
    }

    // ── CM-01: staged writes never touch HEAD before commit (a concurrent reader sees the old value) ──
    [Test]
    public void CommitDiscipline_StagedWrite_NotVisibleBeforeCommit()
    {
        using var dbe = SetupEngine();
        var id = SpawnAnt(dbe, 1, 2, 0);

        using var writeTx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, DurabilityDiscipline.Commit);
        var e = writeTx.OpenMut(id);
        e.Write(CmEntity.Position).X = 777;   // staged — HEAD must remain (1,2)

        // A separate transaction reads HEAD: read-committed ⇒ still sees the pre-write value.
        using (var peek = dbe.CreateQuickTransaction())
        {
            ref readonly var pk = ref peek.Open(id).Read(CmEntity.Position);
            Assert.That(pk.X, Is.EqualTo(1f), "staged value leaked to HEAD before commit (CM-01 violation)");
        }

        writeTx.Commit();

        using var after = dbe.CreateQuickTransaction();
        Assert.That(after.Open(id).Read(CmEntity.Position).X, Is.EqualTo(777f), "value not published at commit");
    }

    // ── AC-4: read-your-own-writes within the writing Commit-discipline tx ─────────────────
    [Test]
    public void CommitDiscipline_ReadYourOwnWrites()
    {
        using var dbe = SetupEngine();
        var id = SpawnAnt(dbe, 5, 6, 0);

        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, DurabilityDiscipline.Commit);
        var e = tx.OpenMut(id);
        e.Write(CmEntity.Position).X = 42;
        ref readonly var rp = ref e.Read(CmEntity.Position);
        Assert.That(rp.X, Is.EqualTo(42f), "writer did not see its own staged value (RYOW)");
        Assert.That(rp.Y, Is.EqualTo(6f), "partial write lost the unwritten field (seed missing)");
    }

    // ── AC-3: rollback discards staged values; HEAD never changed ──────────────────────────
    [Test]
    public void CommitDiscipline_Rollback_DiscardsStaged()
    {
        using var dbe = SetupEngine();
        var id = SpawnAnt(dbe, 3, 4, 0);

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, DurabilityDiscipline.Commit))
        {
            tx.OpenMut(id).Write(CmEntity.Position).X = 1234;
            tx.Rollback();
        }

        using var read = dbe.CreateQuickTransaction();
        Assert.That(read.Open(id).Read(CmEntity.Position).X, Is.EqualTo(3f), "rollback did not discard the staged write");
    }

    // ── AC-3: all writes of a Commit-discipline tx become visible together ─────────────────
    [Test]
    public void CommitDiscipline_MultiWrite_Atomic()
    {
        using var dbe = SetupEngine();
        var id = SpawnAnt(dbe, 0, 0, 100);

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit))
        {
            var e = tx.OpenMut(id);
            e.Write(CmEntity.Position) = new CmPosition(7, 8);
            e.Write(CmEntity.Wallet) = new CmWallet(500);
            tx.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        var e2 = read.Open(id);
        Assert.That(e2.Read(CmEntity.Position).X, Is.EqualTo(7f));
        Assert.That(e2.Read(CmEntity.Wallet).Gold, Is.EqualTo(500L));
    }

    // ── AC-1: DefaultDiscipline=Commit escalates a default-discipline tx (CM-02) ────────────
    [Test]
    public void DefaultDiscipline_Commit_EscalatesTransaction()
    {
        using var dbe = SetupEngine();
        var id = SpawnAnt(dbe, 0, 0, 10);

        // No explicit discipline → escalates to Commit on first touch of CmWallet (DefaultDiscipline=Commit).
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            tx.OpenMut(id).Write(CmEntity.Wallet).Gold = 9999;
            Assert.That(tx.Discipline, Is.EqualTo(DurabilityDiscipline.Commit), "tx was not escalated by DefaultDiscipline=Commit");
            tx.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        Assert.That(read.Open(id).Read(CmEntity.Wallet).Gold, Is.EqualTo(9999L));
    }

    // ── AC-11 / CM-05: the exact B+Tree index reflects a Commit-discipline write AT COMMIT (no tick fence) ──
    [Test]
    public void CommitDiscipline_IndexedWrite_FreshAtCommit_NoFence()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<CmIdxEntity>(CmIdxEntity.Position.Set(new CmPosition(0, 0)), CmIdxEntity.Team.Set(new CmTeam { TeamId = 1, Rank = 5 }));
            tx.Spawn<CmIdxEntity>(CmIdxEntity.Position.Set(new CmPosition(1, 1)), CmIdxEntity.Team.Set(new CmTeam { TeamId = 2, Rank = 5 }));
            tx.Commit();
        }
        dbe.WriteTickFence(1); // index the spawned values

        // Move entity from TeamId 1 → 7 under Commit discipline, then commit. Deliberately NO WriteTickFence afterward:
        // the exact index must already reflect TeamId=7, the same as Versioned (CM-05/AC-11 — Move done at commit).
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit))
        {
            tx.OpenMut(id).Write(CmIdxEntity.Team).TeamId = 7;
            tx.Commit();
        }

        using var q = dbe.CreateQuickTransaction();
        Assert.That(q.Query<CmIdxEntity>().WhereField<CmTeam>(t => t.TeamId == 1).Count(), Is.EqualTo(0),
            "old key still present in the exact index after a committed write (AC-11 false-negative)");
        Assert.That(q.Query<CmIdxEntity>().WhereField<CmTeam>(t => t.TeamId == 7).Count(), Is.EqualTo(1),
            "new key not visible in the exact index at commit — index lagged to the fence (AC-11/CM-05)");
        Assert.That(q.Query<CmIdxEntity>().WhereField<CmTeam>(t => t.TeamId == 2).Count(), Is.EqualTo(1),
            "untouched entity disappeared from the index");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Non-cluster (flat) Commit path — large component ⇒ not cluster-eligible
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void NonCluster_ArchetypeIsFlat()
    {
        using var dbe = SetupEngine();
        Assert.That(Archetype<CmFlatEntity>.Metadata.IsClusterEligible, Is.False,
            "CmFlatEntity must NOT be cluster-eligible for these tests to exercise the flat Commit path");
    }

    private static EntityId SpawnFlat(DatabaseEngine dbe, int tag)
    {
        using var tx = dbe.CreateQuickTransaction();
        // CmTransIdx is a unique Transient index — its Key must be distinct per entity. Derive it from tag.
        var id = tx.Spawn<CmFlatEntity>(CmFlatEntity.Val.Set(new CmFlatVal { Tag = tag }), CmFlatEntity.Idx.Set(new CmTransIdx { Key = tag + 1000 }));
        tx.Commit();
        return id;
    }

    [Test]
    public void NonCluster_CommitDiscipline_Write_PublishesAtCommit()
    {
        using var dbe = SetupEngine();
        var id = SpawnFlat(dbe, 10);

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit))
        {
            var e = tx.OpenMut(id);
            // Staged write — HEAD untouched until commit; a peek tx sees the old value.
            e.Write(CmFlatEntity.Val).Tag = 55;
            Assert.That(e.Read(CmFlatEntity.Val).Tag, Is.EqualTo(55), "flat read-your-own-writes failed");
            using (var peek = dbe.CreateQuickTransaction())
            {
                Assert.That(peek.Open(id).Read(CmFlatEntity.Val).Tag, Is.EqualTo(10), "staged flat write leaked to HEAD pre-commit (CM-01)");
            }
            tx.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        Assert.That(read.Open(id).Read(CmFlatEntity.Val).Tag, Is.EqualTo(55), "flat Commit write not published at commit");
    }

    [Test]
    public void NonCluster_CommitDiscipline_Rollback_DiscardsStaged()
    {
        using var dbe = SetupEngine();
        var id = SpawnFlat(dbe, 7);

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, DurabilityDiscipline.Commit))
        {
            tx.OpenMut(id).Write(CmFlatEntity.Val).Tag = 999;
            tx.Rollback();
        }

        using var read = dbe.CreateQuickTransaction();
        Assert.That(read.Open(id).Read(CmFlatEntity.Val).Tag, Is.EqualTo(7), "flat rollback did not discard the staged write");
    }

    // ── AC-11 on the flat path: the table B+Tree reflects a Commit write at commit (no tick fence). Fixed by rebasing the
    // ReconcileFlatIndexAndViews field offset (OffsetToField is chunk-base-relative; the data pointers are data-relative). ──
    [Test]
    public void NonCluster_CommitDiscipline_IndexedWrite_FreshAtCommit_NoFence()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<CmFlatEntity>(CmFlatEntity.Val.Set(new CmFlatVal { Tag = 1 }), CmFlatEntity.Idx.Set(new CmTransIdx { Key = 1001 }));
            tx.Spawn<CmFlatEntity>(CmFlatEntity.Val.Set(new CmFlatVal { Tag = 2 }), CmFlatEntity.Idx.Set(new CmTransIdx { Key = 1002 }));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit))
        {
            tx.OpenMut(id).Write(CmFlatEntity.Val).Tag = 7;
            tx.Commit();
        }

        using var q = dbe.CreateQuickTransaction();
        Assert.That(q.Query<CmFlatEntity>().WhereField<CmFlatVal>(b => b.Tag == 1).Count(), Is.EqualTo(0),
            "old key still in the flat index after a committed write (AC-11)");
        Assert.That(q.Query<CmFlatEntity>().WhereField<CmFlatVal>(b => b.Tag == 7).Count(), Is.EqualTo(1),
            "new key not in the flat index at commit (AC-11 / CM-05)");
        Assert.That(q.Query<CmFlatEntity>().WhereField<CmFlatVal>(b => b.Tag == 2).Count(), Is.EqualTo(1),
            "untouched flat entity disappeared from the index");
    }
}
