using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1208: a TSN is stored in 48 bits. The transaction chain warns once its counter passes 2^46, and refuses every draw — a transaction's or a
/// snapshot's — from 2^47 − 2^24 on, so nothing wraps silently into "born at genesis, alive". A database reopened past that is refused at open.
/// </summary>
[TestFixture]
class TsnHeadroomTests : TestBase<TsnHeadroomTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    [VerifiesRule("TXC-03")]
    [CancelAfter(10_000)]
    public void BelowTheWarningThreshold_NothingIsLoggedOrRefused()
    {
        using var dbe = SetupEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 1, 1.0)));
            Assert.That(tx.Commit(), Is.True);
        }

        Assert.That(dbe.TsnHeadroomWarnings, Is.Zero);
    }

    /// <summary>Past 2^46 the engine logs one warning, whichever draw crosses first — a transaction's or a point-in-time snapshot's.</summary>
    [Test]
    [VerifiesRule("TXC-03")]
    [CancelAfter(10_000)]
    public void PastTheWarningThreshold_ItWarnsOnce([Values] bool snapshotDrawsFirst)
    {
        using var dbe = SetupEngine();
        var chain = dbe.TransactionChain;
        chain.SetNextFreeIdForTest(TransactionChain.TsnWarningThreshold);
        if (snapshotDrawsFirst)
        {
            chain.AllocateTSN();
        }

        for (var i = 0; i < 3; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Commit();
        }

        Assert.That(dbe.TsnHeadroomWarnings, Is.EqualTo(1), "one warning past 2^46, whichever draw crossed");
    }

    /// <summary>
    /// The last TSNs before the refusal store whole: an entity spawned there is invisible to a snapshot taken before it, and visible after. A BornTSN
    /// that had wrapped would read as 0 — "born at genesis" — and the earlier snapshot would see it.
    /// </summary>
    [Test]
    [VerifiesRule("TXC-03")]
    [CancelAfter(10_000)]
    public void NearTheLimit_ATsnStoresWhole()
    {
        using var dbe = SetupEngine();
        dbe.TransactionChain.SetNextFreeIdForTest(TransactionChain.TsnRefusalThreshold - 16);
        using var before = dbe.CreateReadOnlyTransaction();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 42, 1.0)));
            Assert.That(tx.Commit(), Is.True);
        }

        Assert.That(before.TryOpen(id, out _), Is.False, "a snapshot taken before the spawn does not see it: its BornTSN did not wrap to genesis");
        using var after = dbe.CreateReadOnlyTransaction();
        Assert.That(after.Open(id).Read(CompDArch.D).B, Is.EqualTo(42));
    }

    /// <summary>At the limit no draw happens: a transaction and a snapshot are refused with the non-transient exception, and the counter stays put.</summary>
    [Test]
    [VerifiesRule("TXC-03")]
    [CancelAfter(10_000)]
    public void AtTheRefusalThreshold_NoTsnIsDrawn()
    {
        using var dbe = SetupEngine();
        var chain = dbe.TransactionChain;
        const long limit = TransactionChain.TsnRefusalThreshold;
        Assert.That(limit, Is.LessThan(1L << 47), "bit 47 stays free");

        chain.SetNextFreeIdForTest(limit);
        var refused = Assert.Throws<TransactionSequenceExhaustedException>(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
        }, "a transaction started past the TSN limit");
        Assert.That(refused!.IsTransient, Is.False, "TSN exhaustion does not heal");
        Assert.That(refused.ResourcePath, Is.EqualTo("Data/TransactionChain/Tsn"));
        Assert.Throws<TransactionSequenceExhaustedException>(() => chain.AllocateTSN(), "a snapshot TSN was drawn past the limit");
        Assert.That(chain.NextFreeId, Is.EqualTo(limit), "a refused draw drew a TSN");
    }

    /// <summary>A database reopened with its counter past the limit is refused at open (the counter is restored through SetNextFreeId), not later.</summary>
    [Test]
    [VerifiesRule("TXC-03")]
    [CancelAfter(10_000)]
    public void ACounterRestoredPastTheLimit_IsRefusedAtOpen()
    {
        using var dbe = SetupEngine();
        var chain = dbe.TransactionChain;
        Assert.Throws<TransactionSequenceExhaustedException>(() => chain.SetNextFreeId(TransactionChain.TsnRefusalThreshold));
        chain.SetNextFreeIdForTest(1_000);
        chain.SetNextFreeId(TransactionChain.TsnWarningThreshold);
        Assert.That(dbe.TsnHeadroomWarnings, Is.EqualTo(1), "a counter restored past 2^46 warns at open");
    }
}
