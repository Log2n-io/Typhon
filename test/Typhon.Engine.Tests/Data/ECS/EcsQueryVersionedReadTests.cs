using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Pure-Versioned component (2 public ints -> 8-byte stride). A pure-Versioned archetype is NOT cluster-eligible, so its
// query iteration takes the non-cluster EcsQueryEnumerator branch — the path that #504 left MVCC-unresolved.
[Component("Typhon.Test.Q504.Data", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct Q504Data
{
    public int Value;
    public int Pad;
    public Q504Data(int value) { Value = value; Pad = 0; }
}

[Archetype]
partial class Q504Arch : Archetype<Q504Arch>
{
    public static readonly Comp<Q504Data> Data = Register<Q504Data>();
}

/// <summary>
/// Regression for #504: iterating a query over a pure-Versioned (non-cluster) archetype yielded EntityRefs whose
/// <c>.Read</c> returned the revision-chain HEAD (pre-mutation value) instead of the snapshot-visible revision, while
/// <c>tx.Open(id).Read</c> in the same transaction returned the correct value — two read paths, one snapshot, disagreeing.
/// </summary>
[NonParallelizable]
class EcsQueryVersionedReadTests : TestBase<EcsQueryVersionedReadTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<Q504Data>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void ForeachQuery_VersionedComponent_ReadsSnapshotVisibleRevision()
    {
        using var dbe = SetupEngine();

        // Entity A: spawned at 100, later mutated to 42 (COW -> 2nd revision). Entity B: spawned at 200, never mutated.
        EntityId a, b;
        using (var tx = dbe.CreateQuickTransaction())
        {
            a = tx.Spawn<Q504Arch>(Q504Arch.Data.Set(new Q504Data(100)));
            b = tx.Spawn<Q504Arch>(Q504Arch.Data.Set(new Q504Data(200)));
            tx.Commit();
        }

        // Mutate A: 100 -> 42 in its own committed transaction.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(a).Write(Q504Arch.Data).Value = 42;
            tx.Commit();
        }

        // NEW transaction created strictly AFTER the mutation committed. The query-yielded EntityRef.Read and the
        // open-by-id EntityRef.Read must agree on the snapshot-visible value for BOTH entities.
        using (var tx = dbe.CreateQuickTransaction())
        {
            int seen = 0;
            foreach (var qe in tx.Query<Q504Arch>())
            {
                int expected = qe.Id == a ? 42 : 200;
                int viaOpen = tx.Open(qe.Id).Read(Q504Arch.Data).Value;
                int viaQuery = qe.Read(Q504Arch.Data).Value;

                Assert.That(viaOpen, Is.EqualTo(expected), "open-by-id must see the latest committed revision");
                Assert.That(viaQuery, Is.EqualTo(expected),
                    "#504: query-yielded EntityRef.Read must resolve the same snapshot-visible revision as tx.Open");
                seen++;
            }

            Assert.That(seen, Is.EqualTo(2), "both pure-Versioned entities must be yielded by the query");
        }
    }
}
