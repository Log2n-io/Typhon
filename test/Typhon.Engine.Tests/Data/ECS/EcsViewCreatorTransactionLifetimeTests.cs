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
}
