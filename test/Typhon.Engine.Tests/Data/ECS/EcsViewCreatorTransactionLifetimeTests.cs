using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Regression coverage for #862: an EcsView outlives the Transaction that constructed its EcsQuery.
/// The creator is disposed only after the mutation and refresh transactions have been leased, so the
/// pooled creator object cannot be accidentally reused and hide a stale-transaction dereference.
/// </summary>
class EcsViewCreatorTransactionLifetimeTests : TestBase<EcsViewCreatorTransactionLifetimeTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.RegisterComponentFromAccessor<CompF>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void IncrementalView_CreatorDisposedBeforeRefresh_UsesRefreshContext()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var seed = dbe.CreateQuickTransaction())
        {
            var d = new CompD(1.0f, 30, 2.0);
            id = seed.Spawn<CompDArch>(CompDArch.D.Set(in d));
            seed.Commit();
        }

        Transaction creator = dbe.CreateQuickTransaction();
        EcsView<CompDArch> view = null;
        try
        {
            view = creator.Query<CompDArch>()
                .WhereField<CompD>(d => d.B >= 50)
                .ToView();
            Assert.That(view.Count, Is.Zero);

            // Lease both transactions while creator is still live. After creator.Dispose(), its pooled
            // object therefore stays reset instead of being reused as either transaction and masking #862.
            using var mutation = dbe.CreateQuickTransaction();
            mutation.OpenMut(id).Write(CompDArch.D).B = 60;
            mutation.Commit();

            using var refresh = dbe.CreateQuickTransaction();
            creator.Dispose();
            creator = null;

            view.Refresh(refresh);

            Assert.Multiple(() =>
            {
                Assert.That(view.Count, Is.EqualTo(1));
                Assert.That(view.Contains(id), Is.True);
                Assert.That(view.Added, Has.Count.EqualTo(1));
                Assert.That(view.Added[0], Is.EqualTo(id));
            });
        }
        finally
        {
            view?.Dispose();
            creator?.Dispose();
        }
    }

    [Test]
    public void OrView_CreatorDisposedBeforeRefresh_UsesRefreshContext()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var seed = dbe.CreateQuickTransaction())
        {
            id = seed.Spawn<CompFArch>(CompFArch.F.Set(new CompF(10, 1)));
            seed.Commit();
        }

        Transaction creator = dbe.CreateQuickTransaction();
        EcsView<CompFArch> view = null;
        try
        {
            view = creator.Query<CompFArch>()
                .WhereField<CompF>(f => f.Gold >= 90 || f.Rank >= 5)
                .ToView();
            Assert.That(view.Count, Is.Zero);

            using var mutation = dbe.CreateQuickTransaction();
            mutation.OpenMut(id).Write(CompFArch.F).Rank = 7;
            mutation.Commit();

            using var refresh = dbe.CreateQuickTransaction();
            creator.Dispose();
            creator = null;

            view.Refresh(refresh);

            Assert.Multiple(() =>
            {
                Assert.That(view.Count, Is.EqualTo(1));
                Assert.That(view.Contains(id), Is.True);
                Assert.That(view.Added, Has.Count.EqualTo(1));
                Assert.That(view.Added[0], Is.EqualTo(id));
            });
        }
        finally
        {
            view?.Dispose();
            creator?.Dispose();
        }
    }

    [Test]
    public void PullView_CreatorDisposedBeforeRefresh_RequeriesAtRefreshSnapshot()
    {
        using var dbe = SetupEngine();

        Transaction creator = dbe.CreateQuickTransaction();
        EcsView<CompDArch> view = null;
        try
        {
            view = creator.Query<CompDArch>().ToView();
            Assert.That(view.Count, Is.Zero);

            using var mutation = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 60, 2.0);
            var id = mutation.Spawn<CompDArch>(CompDArch.D.Set(in d));
            mutation.Commit();

            using var refresh = dbe.CreateQuickTransaction();
            creator.Dispose();
            creator = null;

            view.Refresh(refresh);

            Assert.Multiple(() =>
            {
                Assert.That(view.Count, Is.EqualTo(1));
                Assert.That(view.Contains(id), Is.True);
            });
        }
        finally
        {
            view?.Dispose();
            creator?.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // VIEW-01 — the invariant itself, not its consequence
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>The marker VIEW-01's assertion emits when it rejects. The mutant below keys on it, so it must come from here and nowhere else.</summary>
    private const string RetainedTransactionMarker = "VIEW-01: the retained query still holds a Transaction";

    /// <summary>
    /// The single assertion path for VIEW-01, shared by the verifier and its mutant so the mutant proves THIS assertion can fail rather than
    /// some parallel restatement of it.
    /// </summary>
    private static void AssertNoRetainedTransaction(bool holdsTransaction, string when) =>
        Assert.That(holdsTransaction, Is.False, $"{RetainedTransactionMarker} ({when})");

    /// <summary>
    /// The three tests above assert that a view still works once its creator is gone. That is the consequence, and it would also hold for a fix
    /// that simply rebound the query at the top of <c>Refresh</c> — which leaves the view retaining the most recent refresher instead. This
    /// asserts the rule: between operations the retained query holds nothing at all.
    /// </summary>
    [Test]
    [VerifiesRule("VIEW-01")]
    public void RetainedQuery_HoldsNoTransaction_AcrossEveryViewShape()
    {
        using var dbe = SetupEngine();

        using (var seed = dbe.CreateQuickTransaction())
        {
            seed.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1.0f, 60, 2.0)));
            seed.Spawn<CompFArch>(CompFArch.F.Set(new CompF(10, 7)));
            seed.Commit();
        }

        EcsView<CompDArch> incremental;
        EcsView<CompFArch> or;
        EcsView<CompDArch> pull;

        using (var creator = dbe.CreateQuickTransaction())
        {
            incremental = creator.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();
            or = creator.Query<CompFArch>().WhereField<CompF>(f => f.Gold >= 90 || f.Rank >= 5).ToView();
            pull = creator.Query<CompDArch>().ToView();

            // Still inside the creator's lease: the detach happens at construction, not at first refresh, so the creator being alive here is
            // exactly the case a constructor-only reading of the rule would get wrong.
            AssertNoRetainedTransaction(incremental.RetainedQueryHoldsATransaction, "incremental, after construction");
            AssertNoRetainedTransaction(or.RetainedQueryHoldsATransaction, "OR, after construction");
            AssertNoRetainedTransaction(pull.RetainedQueryHoldsATransaction, "pull, after construction");
        }

        try
        {
            using var refresh = dbe.CreateQuickTransaction();
            incremental.Refresh(refresh);
            or.Refresh(refresh);
            pull.Refresh(refresh);

            // RefreshPull binds a transaction for the duration of the re-query; the scoped borrow is what puts it back.
            AssertNoRetainedTransaction(incremental.RetainedQueryHoldsATransaction, "incremental, after Refresh");
            AssertNoRetainedTransaction(or.RetainedQueryHoldsATransaction, "OR, after Refresh");
            AssertNoRetainedTransaction(pull.RetainedQueryHoldsATransaction, "pull, after Refresh");
        }
        finally
        {
            incremental.Dispose();
            or.Dispose();
            pull.Dispose();
        }
    }

    /// <summary>
    /// A query taken straight from a transaction legitimately holds it — that is what makes it a usable query. Feeding VIEW-01's assertion that
    /// state must turn it red, or the assertion is vacuous and the rule reports confidence it has not earned.
    /// </summary>
    [Test]
    [RuleMutant("VIEW-01")]
    public void Mutant_AQueryStillBoundToItsTransaction_IsReported()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var boundToItsTransaction = tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50);

        RuleMutants.AssertDetects("VIEW-01", RetainedTransactionMarker,
            () => AssertNoRetainedTransaction(boundToItsTransaction.HoldsTransaction, "a query that never became a view"));
    }
}
