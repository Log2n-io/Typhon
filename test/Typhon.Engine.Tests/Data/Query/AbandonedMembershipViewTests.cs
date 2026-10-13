using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.CompilerServices;
using Typhon.Engine.internals;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Runtime;

namespace Typhon.Engine.Tests;

/// <summary>
/// #865 — an unfiltered view abandoned without <c>Dispose</c> stays subscribed to its archetype's membership channel for the life of the engine.
/// </summary>
/// <remarks>
/// <para>
/// <c>ArchetypeMembershipRegistry</c> holds each subscriber through <c>ViewRegistration.View</c>, a strong reference, and the only path that removes it
/// is <c>ViewBase.Dispose</c> → <c>EcsView.DeregisterFromRegistries</c>. So the registry ROOTS the view: an abandoned view is never garbage, and every
/// spawn/destroy commit on its archetype keeps appending to its ring buffer.
/// </para>
/// <para>
/// Both cases measure by COUNTING, not timing: the subscriber count, the views the GC could not collect, and the appends one commit performs.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class AbandonedMembershipViewTests : TestBase<AbandonedMembershipViewTests>
{
    private const int AbandonedViews = 50;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] AbandonFieldViews(DatabaseEngine dbe, int count)
    {
        var weak = new WeakReference[count];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            weak[i] = new WeakReference(tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView());
        }
        return weak;
    }

    private static ArchetypeMembershipRegistry RegistryOf(DatabaseEngine dbe)
        => dbe._archetypeStates[ArchetypeRegistry.GetMetadata<TouchArch>().ArchetypeId].MembershipViews;

    /// <summary>Builds <paramref name="count"/> membership views and drops every strong reference to them.</summary>
    /// <remarks>Its own non-inlined frame, so no local of the caller can keep a view reachable — in Debug the JIT extends locals to method end.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] AbandonViews(DatabaseEngine dbe, int count)
    {
        var weak = new WeakReference[count];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            weak[i] = new WeakReference(tx.Query<TouchArch>().ToView());
        }
        return weak;
    }

    private static void FullCollect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Test]
    public void AnAbandonedMembershipView_IsNotKeptAliveByItsArchetypeRegistry()
    {
        using var dbe = SetupEngine();
        var registry = RegistryOf(dbe);
        var baseline = registry.ViewCount;

        var retiredBefore = dbe.ViewBufferReclaimer.RetiredTotal;
        var weak = AbandonViews(dbe, AbandonedViews);
        Assert.That(registry.ViewCount, Is.EqualTo(baseline + AbandonedViews), "PREMISE: every unfiltered ToView() subscribes to the membership channel");

        FullCollect();
        // The finalizer only flags; the registry sweeps on its next registration.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Query<TouchArch>().ToView().Dispose();
        }

        var alive = 0;
        foreach (var w in weak)
        {
            if (w.IsAlive)
            {
                alive++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(alive, Is.Zero,
                "an abandoned view must be collectable — measured: the registry's strong ViewRegistration.View reference keeps every one alive");
            Assert.That(registry.ViewCount, Is.EqualTo(baseline),
                "an abandoned view must not stay subscribed — measured: the subscriber count never comes back down, for the life of the engine");
            Assert.That(dbe.ViewBufferReclaimer.RetiredTotal - retiredBefore, Is.EqualTo(AbandonedViews + 1),
                "every abandoned view's pinned buffer is retired by the sweep, plus the one disposed view's");
        });
    }

    /// <summary>The field channel (<c>ViewRegistry</c>, pre-#790) has the identical strong reference and the identical Dispose-only exit.</summary>
    /// <remarks>#790 did not create the leak; it moved it onto the default view shape. A fix to the membership registry alone leaves this one.</remarks>
    [Test]
    public void AnAbandonedFieldView_IsNotKeptAliveByItsViewRegistry()
    {
        using var dbe = SetupEngine();
        var registry = dbe.GetComponentTable<CompD>().ViewRegistry;
        var baseline = registry.ViewCount;

        var weak = AbandonFieldViews(dbe, AbandonedViews);
        Assert.That(registry.ViewCount, Is.EqualTo(baseline + AbandonedViews), "PREMISE: every WhereField ToView() subscribes to the field channel");

        FullCollect();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView().Dispose();
        }

        var alive = 0;
        foreach (var w in weak)
        {
            if (w.IsAlive)
            {
                alive++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(alive, Is.Zero, "an abandoned WhereField view must be collectable");
            Assert.That(registry.ViewCount, Is.EqualTo(baseline), "an abandoned WhereField view must not stay subscribed");
        });
    }

    [Test]
    public void ACommit_DoesNotAppendToViewsNobodyHolds()
    {
        using var dbe = SetupEngine();
        AbandonViews(dbe, AbandonedViews);
        FullCollect();

        // One live, disposed-on-exit view: the cost a commit SHOULD pay is one append per structurally-changed entity, for this view only.
        using var txLive = dbe.CreateQuickTransaction();
        using var live = txLive.Query<TouchArch>().ToView();

        var appends = 0;
        QueryPathProbe.PrePublishAppendHook = () => appends++;
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            var v = new TouchPos { X = 1 };
            tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            tx.Commit();
        }
        finally
        {
            QueryPathProbe.PrePublishAppendHook = null;
        }

        Assert.That(appends, Is.EqualTo(1),
            $"one spawn with one live view must cost one append — measured {appends}: commit cost scales with views ever created, not views alive");
    }
}
