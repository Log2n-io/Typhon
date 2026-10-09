using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// A disposed transaction leaves no page slot-referenced. A slot reference keeps a page in the cache whatever its epoch, so one that outlives its
/// accessor pins the page for the life of the process: found by <c>MarketHardeningTests</c>, whose snapshot of a database six times its cache left 94 % of
/// the cache slot-referenced after its read-only transactions had all been disposed, and whose storm then timed out on back-pressure.
/// </summary>
/// <remarks>
/// Three leaks, one per test below: every component read through a copy of the component accessor (one reference per read); the component accessors
/// of a read-only transaction, never disposed (its pool reset recycles them as they are); and the cluster accessors of a transaction a full pool drops,
/// which nothing resets.
/// </remarks>
[TestFixture]
class ReadOnlyTransactionSlotPinTests : TestBase<ReadOnlyTransactionSlotPinTests>
{
    private const int Entities = 2_000;

    private DatabaseEngine SetupEngine(out EntityId[] ids, int entities = Entities)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        ids = new EntityId[entities];
        for (var s = 0; s < entities; s += Entities)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = s; i < s + Entities && i < entities; i++)
            {
                var a = new CompA(i, i, i);
                ids[i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in a));
            }

            tx.Commit();
        }

        Assert.That(dbe.MMF.CountUnevictablePages().SlotRef, Is.Zero, "precondition: the spawn left nothing referenced");
        return dbe;
    }

    /// <summary>The verifier: no page is slot-referenced, and if one is, which segments hold them.</summary>
    private static void AssertNoPageSlotReferenced(DatabaseEngine dbe, string what)
        => Assert.That(dbe.MMF.CountUnevictablePages().SlotRef, Is.Zero,
            () => $"{what} left pages slot-referenced, which can never be evicted: {Attribution(dbe)}");

    /// <summary>The slot-referenced pages, counted per segment: what holds them.</summary>
    private static string Attribution(DatabaseEngine dbe)
    {
        var pages = dbe.MMF.SlotReferencedFilePages();
        var parts = new List<string>();
        foreach (var seg in dbe.EnumerateStorageSegments())
        {
            var owned = new HashSet<int>(seg.Pages.ToArray());
            var c = 0;
            foreach (var p in pages)
            {
                if (owned.Contains(p))
                {
                    c++;
                }
            }

            if (c > 0)
            {
                parts.Add($"{seg.Kind}@{seg.RootPageIndex}: {c}");
            }
        }

        return $"{pages.Count} slot-referenced ({string.Join(", ", parts)})";
    }

    [Test]
    [VerifiesRule("PS-19")]
    public void ReadOnlyTransactions_LeaveNoPageSlotReferenced_OnceDisposed()
    {
        using var dbe = SetupEngine(out var ids);

        // Each transaction reads across the whole population, so its accessors rotate slots.
        for (var round = 0; round < 40; round++)
        {
            using var tx = dbe.CreateReadOnlyTransaction();
            for (var i = round % 7; i < Entities; i += 7)
            {
                Assert.That(tx.Open(ids[i]).Read(CompAArch.A).A, Is.EqualTo(i));
            }
        }

        AssertNoPageSlotReferenced(dbe, "a disposed read-only transaction");
    }

    [Test]
    [VerifiesRule("PS-19")]
    public void WritingTransactions_LeaveNoPageSlotReferenced_OnceDisposed()
    {
        using var dbe = SetupEngine(out var ids);

        for (var round = 0; round < 40; round++)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = round % 7; i < Entities; i += 7)
            {
                Assert.That(tx.Open(ids[i]).Read(CompAArch.A).A, Is.EqualTo(i));
            }

            tx.Commit();
        }

        AssertNoPageSlotReferenced(dbe, "a disposed transaction");
    }

    /// <summary>
    /// More transactions alive at once than the transaction pool holds: the ones disposed into a full pool are dropped, never reset, so whatever their
    /// dispose does not release stays referenced.
    /// </summary>
    [Test]
    [VerifiesRule("PS-19")]
    public void TransactionsDroppedByAFullPool_LeaveNoPageSlotReferenced()
    {
        using var dbe = SetupEngine(out var ids);
        var live = new List<Transaction>();
        for (var t = 0; t < 24; t++)
        {
#pragma warning disable TYPHON004 // kept open on purpose: disposed below, in reverse order, once more are alive than the pool holds
            var tx = dbe.CreateReadOnlyTransaction();
#pragma warning restore TYPHON004
            for (var i = t; i < Entities; i += 24)
            {
                Assert.That(tx.Open(ids[i]).Read(CompAArch.A).A, Is.EqualTo(i));
            }

            live.Add(tx);
        }

        for (var t = live.Count - 1; t >= 0; t--)
        {
            live[t].Dispose();
        }

        AssertNoPageSlotReferenced(dbe, "transactions dropped by a full pool");
    }

    /// <summary>The same with writing transactions, whose dispose takes another path: their entity-map lookups leave the map accessor holding pages.</summary>
    [Test]
    [VerifiesRule("PS-19")]
    public void WritingTransactionsDroppedByAFullPool_LeaveNoPageSlotReferenced()
    {
        using var dbe = SetupEngine(out var ids);
        var live = new List<Transaction>();
        for (var t = 0; t < 24; t++)
        {
#pragma warning disable TYPHON004 // kept open on purpose: disposed below, in reverse order, once more are alive than the pool holds
            var tx = dbe.CreateQuickTransaction();
#pragma warning restore TYPHON004
            for (var i = t; i < Entities; i += 24)
            {
                Assert.That(tx.Open(ids[i]).Read(CompAArch.A).A, Is.EqualTo(i));
            }

            live.Add(tx);
        }

        for (var t = live.Count - 1; t >= 0; t--)
        {
            live[t].Dispose();
        }

        AssertNoPageSlotReferenced(dbe, "writing transactions dropped by a full pool");
    }

    /// <summary>The market snapshot's shape: short read-only transactions over batches, on several threads.</summary>
    [Test]
    [VerifiesRule("PS-19")]
    public void ParallelReadOnlyBatches_LeaveNoPageSlotReferenced()
    {
        const int entities = 8_000;
        const int batch = 256;
        using var dbe = SetupEngine(out var ids, entities);

        Parallel.For(0, entities / batch, new ParallelOptions { MaxDegreeOfParallelism = 8 }, b =>
        {
            using var tx = dbe.CreateReadOnlyTransaction();
            for (var i = b * batch; i < (b + 1) * batch; i++)
            {
                if (tx.Open(ids[i]).Read(CompAArch.A).A != i)
                {
                    Assert.Fail($"entity {i} read another entity's value");
                }
            }
        });

        AssertNoPageSlotReferenced(dbe, "parallel read-only transactions");
    }

    /// <summary>
    /// The verifier can fail: an accessor copied, used and dropped — what <c>ResolveSpawnAwarePayload</c> did with every component read — leaves its
    /// page slot-referenced, and the check above reports it.
    /// </summary>
    [Test]
    [RuleMutant("PS-19")]
    public unsafe void Mutant_ACopiedAccessorLeaksItsPage_IsReported()
    {
        using var dbe = SetupEngine(out _);
        var segment = dbe.GetComponentTable<CompA>().ComponentSegment;
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var copy = accessor;
            _ = copy.GetChunkAddress(1);   // loaded into the copy's slot, which takes the reference; the copy is then dropped
            accessor.Dispose();
        }
        finally
        {
            dbe.EpochManager.ExitScope(depth);
        }

        RuleMutants.AssertDetects("PS-19", "can never be evicted", () => AssertNoPageSlotReferenced(dbe, "a copied accessor"));
    }
}
