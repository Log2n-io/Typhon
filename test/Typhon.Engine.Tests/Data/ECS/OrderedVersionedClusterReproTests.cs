using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Repro for the "ordered query returns 0 after updating a Versioned indexed field" bug.
//
// Mirrors the SWG-Light "Agent" shape: a CLUSTER-ELIGIBLE archetype (it has a SingleVersion slot) that carries a
// Versioned component with an AllowMultiple-indexed field (like Wallet.Gold). Because the Versioned slot is non-Transient
// and indexed, the archetype has a per-archetype CLUSTER B+Tree index, and OrderBy routes through ExecuteOrderedClustered
// — a different path than a pure-Versioned (non-cluster) archetype.
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.RR.Pose", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct RRPose
{
    [Field] public int X;
    [Field] public int Y;

    public RRPose(int x, int y)
    {
        X = x;
        Y = y;
    }
}

[Component("Typhon.Test.RR.Wallet", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct RRWallet
{
    [Index(AllowMultiple = true)] public long Gold;

    public RRWallet(long gold)
    {
        Gold = gold;
    }
}

[Archetype]
partial class RRAgent : Archetype<RRAgent>
{
    public static readonly Comp<RRPose> Pose = Register<RRPose>();
    public static readonly Comp<RRWallet> Wallet = Register<RRWallet>();
}

[TestFixture]
class OrderedVersionedClusterReproTests : TestBase<OrderedVersionedClusterReproTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RRPose>();
        dbe.RegisterComponentFromAccessor<RRWallet>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void OrderByVersionedIndexedField_AfterUpdate_ReturnsAllEntities()
    {
        using var dbe = SetupEngine();

        var meta = Archetype<RRAgent>.Metadata;
        Assert.That(meta.IsClusterEligible, Is.True, "Agent-shaped archetype (SV + Versioned) must be cluster-eligible");
        Assert.That(meta.HasClusterIndexes, Is.True, "Versioned indexed Gold should be a per-archetype cluster index");

        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                ids.Add(tx.Spawn<RRAgent>(RRAgent.Pose.Set(new RRPose(i, i)), RRAgent.Wallet.Set(new RRWallet(100 + i))));
            }
            tx.Commit();
        }

        // Baseline: ordered scan on the Versioned indexed field works right after create.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var ordered = tx.Query<RRAgent>().WhereField<RRWallet>(w => w.Gold >= 0).OrderByFieldDescending<RRWallet, long>(w => w.Gold).ExecuteOrdered();
            Assert.That(ordered, Has.Count.EqualTo(5), "baseline ordered scan on Versioned indexed Gold after create");
        }

        // Update every entity's Versioned indexed field (each entity leaves its old key for a new one).
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var id in ids)
            {
                ref var w = ref tx.OpenMut(id).Write(RRAgent.Wallet);
                w.Gold += 1000;
            }
            tx.Commit();
        }

        // BUG (fixed): after updating the AllowMultiple Versioned cluster index, the ordered scan must still find all 5
        // entities, and in the correct (new-key) descending order — 1104, 1103, 1102, 1101, 1100.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var ordered = tx.Query<RRAgent>().WhereField<RRWallet>(w => w.Gold >= 0).OrderByFieldDescending<RRWallet, long>(w => w.Gold).ExecuteOrdered();
            Assert.That(ordered, Has.Count.EqualTo(5), "ordered scan on Versioned indexed Gold after update");

            var gold = new List<long>();
            foreach (var id in ordered)
            {
                gold.Add(tx.Open(id).Read(RRAgent.Wallet).Gold);
            }
            Assert.That(gold, Is.EqualTo(new[] { 1104L, 1103L, 1102L, 1101L, 1100L }), "results must be ordered by the updated key");
        }

        // Destroy two entities AFTER the update. Destroy reads the per-entity index element id from the cluster tail slot,
        // which the update's MoveValue must have written back — so this also guards the element-id round-trip through Move.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[0]); // Gold was 100 → 1100
            tx.Destroy(ids[4]); // Gold was 104 → 1104
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var ordered = tx.Query<RRAgent>().WhereField<RRWallet>(w => w.Gold >= 0).OrderByFieldDescending<RRWallet, long>(w => w.Gold).ExecuteOrdered();
            var gold = new List<long>();
            foreach (var id in ordered)
            {
                gold.Add(tx.Open(id).Read(RRAgent.Wallet).Gold);
            }
            Assert.That(gold, Is.EqualTo(new[] { 1103L, 1102L, 1101L }), "ordered scan after destroy-following-update must drop exactly the destroyed entities");
        }
    }
}
