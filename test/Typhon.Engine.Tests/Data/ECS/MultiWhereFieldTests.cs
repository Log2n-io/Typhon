using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Two components on ONE archetype, each carrying an indexed field that shares a name with the other's (Code) and one that does not (AlphaOnly / BetaOnly).
// The shared name is the point: EcsQuery.WhereField merges every call's predicate branches into one flat FieldPredicate[][], and FieldPredicate stores only
// a field NAME - no component identity - while _whereComponentTable is overwritten by each call. So a second WhereField on a different component leaves the
// FIRST call's predicates to be resolved against the SECOND call's component. A name that exists on both resolves silently against the wrong one.
[Component("Typhon.Test.ECS.MultiWhere.Alpha", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MwfAlpha
{
    [Index(AllowMultiple = true)] public int Code;
    [Index(AllowMultiple = true)] public int AlphaOnly;

    public MwfAlpha(int code, int alphaOnly)
    {
        Code = code;
        AlphaOnly = alphaOnly;
    }
}

[Component("Typhon.Test.ECS.MultiWhere.Beta", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MwfBeta
{
    [Index(AllowMultiple = true)] public int Code;
    [Index(AllowMultiple = true)] public int BetaOnly;

    public MwfBeta(int code, int betaOnly)
    {
        Code = code;
        BetaOnly = betaOnly;
    }
}

[Archetype]
class MwfArch : Archetype<MwfArch>
{
    public static readonly Comp<MwfAlpha> Alpha = Register<MwfAlpha>();
    public static readonly Comp<MwfBeta> Beta = Register<MwfBeta>();
}

/// <summary>
/// Characterises what two <c>WhereField</c> calls on DIFFERENT components actually do - review item 29, design §9.6.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in the suite chained two <c>WhereField</c> calls before this fixture: 293 call sites across the test project, none of them a second call on the
/// same query. That is why the behaviour below was never observed.
/// </para>
/// <para>
/// <c>Where</c> (the opaque, per-entity form) composes correctly across components and is used here as the oracle - it opens the entity and reads each
/// component by its own type, so it cannot confuse two of them.
/// </para>
/// </remarks>
[TestFixture]
class MultiWhereFieldTests : TestBase<MultiWhereFieldTests>
{
    private const int Count = 40;

    /// <summary>Alpha.Code = i, Beta.Code = 1000 + i, so a predicate meant for one is trivially distinguishable from the same predicate against the other.</summary>
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<MwfAlpha>();
        dbe.RegisterComponentFromAccessor<MwfBeta>();
        dbe.InitializeArchetypes();

        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < Count; i++)
        {
            tx.Spawn<MwfArch>(
                MwfArch.Alpha.Set(new MwfAlpha(i, i)),
                MwfArch.Beta.Set(new MwfBeta(1000 + i, 1000 + i)));
        }
        tx.Commit();

        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // What ships today: the guard. A cross-component chain raises instead of answering.
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MultiWhereField_DifferentComponent_Throws()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            tx.Query<MwfArch>()
                .WhereField<MwfAlpha>(a => a.Code >= 30)
                .WhereField<MwfBeta>(b => b.Code >= 1000));

        // The message must name BOTH components: the whole failure mode is that the second call silently captured the first's predicates, so a message
        // naming only one of them would leave the reader guessing which call was the problem.
        Assert.That(ex.Message, Does.Contain("Typhon.Test.ECS.MultiWhere.Alpha"));
        Assert.That(ex.Message, Does.Contain("Typhon.Test.ECS.MultiWhere.Beta"));
        Assert.That(ex.Message, Does.Contain("Where<T>"), "the message must point at the API that does compose across components");
    }

    [Test]
    public void MultiWhereField_SameComponent_StillChains()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        // Two calls on ONE component are the case the cross-product was built for, and the guard must not touch it: last-wins on _whereComponentTable is
        // harmless when both calls resolve to the same table.
        var chained = tx.Query<MwfArch>()
            .WhereField<MwfAlpha>(a => a.Code >= 10)
            .WhereField<MwfAlpha>(a => a.AlphaOnly < 20)
            .Execute();

        var oracle = tx.Query<MwfArch>()
            .Where<MwfAlpha>(a => a.Code >= 10 && a.AlphaOnly < 20)
            .Execute();

        Assert.That(oracle.Count, Is.EqualTo(10), "oracle sanity: i in 10..19");
        Assert.That(chained.SetEquals(oracle), Is.True, "chained WhereField on the SAME component must keep ANDing correctly");
    }

    [Test]
    public void MultiWhereField_MixedWithWhere_ComposesAcrossComponents()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        // The documented way to filter a second component today — and the one the guard's message recommends. It must actually work.
        var mixed = tx.Query<MwfArch>()
            .WhereField<MwfAlpha>(a => a.Code >= 30)
            .Where<MwfBeta>(b => b.Code >= 1000)
            .Execute();

        var oracle = tx.Query<MwfArch>()
            .Where<MwfAlpha>(a => a.Code >= 30)
            .Where<MwfBeta>(b => b.Code >= 1000)
            .Execute();

        Assert.That(oracle.Count, Is.EqualTo(10), "oracle sanity");
        Assert.That(mixed.SetEquals(oracle), Is.True, "WhereField on one component + Where on another must compose");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Acceptance tests for #693 — what correct cross-component WhereField looks like.
    // Ignored, not deleted: they are the specification the fix has to satisfy.
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [Ignore("#693 — cross-component WhereField is guarded, not implemented. Un-Ignore with the fix.")]
    public void MultiWhereField_SharedFieldName_MatchesBroadScan()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        // Alpha.Code >= 30 selects i in 30..39 (10 entities). Beta.Code >= 1000 selects all 40. The AND is 10.
        var actual = tx.Query<MwfArch>()
            .WhereField<MwfAlpha>(a => a.Code >= 30)
            .WhereField<MwfBeta>(b => b.Code >= 1000)
            .Execute();

        var oracle = tx.Query<MwfArch>()
            .Where<MwfAlpha>(a => a.Code >= 30)
            .Where<MwfBeta>(b => b.Code >= 1000)
            .Execute();

        Assert.That(oracle.Count, Is.EqualTo(10), "oracle sanity: the broad scan must see the two components separately");
        Assert.That(actual.Count, Is.EqualTo(oracle.Count),
            "two WhereField calls on different components must AND their predicates against their OWN components");
        Assert.That(actual.SetEquals(oracle), Is.True, "and must select the same entities as the broad scan");
    }

    [Test]
    [Ignore("#693 — cross-component WhereField is guarded, not implemented. Un-Ignore with the fix.")]
    public void MultiWhereField_SharedFieldName_OrderIndependent()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        // AND is commutative, so swapping the two calls must not change the answer. It does if the last call decides whose fields both predicates read.
        var alphaFirst = tx.Query<MwfArch>()
            .WhereField<MwfAlpha>(a => a.Code >= 30)
            .WhereField<MwfBeta>(b => b.Code >= 1000)
            .Execute();

        var betaFirst = tx.Query<MwfArch>()
            .WhereField<MwfBeta>(b => b.Code >= 1000)
            .WhereField<MwfAlpha>(a => a.Code >= 30)
            .Execute();

        Assert.That(alphaFirst.SetEquals(betaFirst), Is.True, "AND is commutative — call order must not change the result set");
    }

    [Test]
    [Ignore("#693 — cross-component WhereField is guarded, not implemented. Un-Ignore with the fix.")]
    public void MultiWhereField_DistinctFieldNames_MatchesBroadScan()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var oracle = tx.Query<MwfArch>()
            .Where<MwfAlpha>(a => a.AlphaOnly >= 30)
            .Where<MwfBeta>(b => b.BetaOnly >= 1000)
            .Execute();

        Assert.That(oracle.Count, Is.EqualTo(10), "oracle sanity");

        var actual = tx.Query<MwfArch>()
            .WhereField<MwfAlpha>(a => a.AlphaOnly >= 30)
            .WhereField<MwfBeta>(b => b.BetaOnly >= 1000)
            .Execute();

        Assert.That(actual.SetEquals(oracle), Is.True,
            "AlphaOnly exists on neither the other component nor its field table — this must not silently drop or mis-resolve either predicate");
    }
}
