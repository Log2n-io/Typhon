using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Covers the plans that no index range can narrow (#591), plus the NotEqual-on-the-scanned-field cull those plans exposed.
/// </summary>
/// <remarks>
/// <para>
/// <b>#591.</b> <c>PlanBuilder</c> emitted <c>PrimaryFieldIndex = -1</c> — "scan the PK index" — whenever nothing could narrow a range, but the PK B+Tree was
/// removed, so <c>PipelineExecutor</c>'s non-secondary-index paths no-op. The query then returned an empty set with no exception, which is indistinguishable
/// from "nothing matched". Two shapes reach it: an <c>!=</c> predicate (NotEqual is never selected as a primary stream because it cannot narrow a range), and
/// an OrderBy on a field the WHERE clause never mentions. Cluster archetypes compensated internally, so exposure depended on schema shape — which is what made
/// it intermittent and easy to miss.
/// </para>
/// <para>
/// <b>The cull.</b> Once those plans got a real stream to scan, <c>ComputeNonPrimaryEvaluators</c> dropped every evaluator on the scanned field as "already
/// guaranteed by the range". That holds only for evaluators that shaped the range, and NotEqual never does. It was already wrong before #591 for a compound
/// predicate like <c>B &gt;= 5 &amp;&amp; B != 7</c>, which returned the <c>B == 7</c> rows.
/// </para>
/// <para>Uses <c>CompD</c> (indexed <c>float A</c>, <c>int B</c>, <c>double C</c>) on a non-cluster archetype — the configuration that has no compensation.</para>
/// </remarks>
class EcsQueryFullScanFallbackTests : TestBase<EcsQueryFullScanFallbackTests>
{
    private const int Count = 10;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Spawns entities 0..9 with A = B = C = i, so every predicate below has an obvious expected answer.</summary>
    private static void SpawnRange(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < Count; i++)
        {
            tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD { A = i, B = i, C = i }));
        }
        tx.Commit();
    }

    private static List<int> ReadBValues(Transaction tx, IEnumerable<EntityId> ids)
        => ids.Select(id => tx.Open(id).Read(CompDArch.D).B).ToList();

    // ── Shape 1: NotEqual, which no range can express ──────────────────────────────────────────────────────────────

    [Test]
    public void NotEqualPredicate_Execute_ReturnsEveryNonMatchingEntity()
    {
        using var dbe = SetupEngine();
        SpawnRange(dbe);

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<CompDArch>().WhereField<CompD>(d => d.B != 5).Execute();

        Assert.That(result, Has.Count.EqualTo(Count - 1), "an empty set here is the #591 silent wrong result");
        Assert.That(ReadBValues(tx, result), Does.Not.Contain(5).And.Contains(0).And.Contains(9));
    }

    [Test]
    public void NotEqualPredicate_Count_MatchesExecute()
    {
        using var dbe = SetupEngine();
        SpawnRange(dbe);

        using var tx = dbe.CreateQuickTransaction();

        Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B != 5).Count(), Is.EqualTo(Count - 1));
    }

    /// <summary>The worst face of #591: a boolean "does anything match" answering false while nine of ten entities match.</summary>
    [Test]
    public void NotEqualPredicate_Any_IsTrueWhenMatchesExist()
    {
        using var dbe = SetupEngine();
        SpawnRange(dbe);

        using var tx = dbe.CreateQuickTransaction();

        Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B != 5).Any(), Is.True);
    }

    [Test]
    public void NotEqualPredicate_MatchingNothing_StillReturnsEmpty()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD { A = 1, B = 1, C = 1 }));
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();

        // Guards the opposite failure: the fallback must not turn a full-range scan into "match everything".
        Assert.That(tx2.Query<CompDArch>().WhereField<CompD>(d => d.B != 1).Execute(), Is.Empty);
    }

    // ── The cull: NotEqual on the very field being scanned ─────────────────────────────────────────────────────────

    /// <summary>
    /// Pre-existing wrong-rows bug, independent of #591: the range comes from <c>B &gt;= 5</c>, and <c>B != 7</c> narrows nothing, so culling it as
    /// "covered by the index scan" silently readmits <c>B == 7</c>.
    /// </summary>
    [Test]
    public void NotEqualOnScannedField_IsStillEvaluated()
    {
        using var dbe = SetupEngine();
        SpawnRange(dbe);

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 5 && d.B != 7).Execute();

        Assert.That(ReadBValues(tx, result), Is.EquivalentTo(new[] { 5, 6, 8, 9 }));
    }

    // ── Shape 2: OrderBy on a field the predicate never mentions — STILL OPEN ──────────────────────────────────────
    //
    // Left unfixed deliberately. Serving this needs a full-range scan bound on the OrderBy field, and for Float/Double the raw IEEE-bit endpoints that both
    // PlanBuilder.TypeMinAsLong/TypeMaxAsLong and EcsQuery.GetTypeMinAsLong/GetTypeMaxAsLong return do not bracket negative values: -1.0f encodes to
    // -1082130432, which is outside [float.MinValue bits, float.MaxValue bits] = [-8388609, 2139095039]. IntersectEvaluatorBounds already documents the same
    // hazard ("IEEE 754 bit patterns don't sort as signed longs for negatives"). Substituting a stream here therefore drops negative keys — an attempt at it
    // turned KWayMergeTests' negative-float cases red. The cluster path dodges this only because PrimaryFieldIndex stays -1 and it defaults to
    // keyType=Int over [long.Min, long.Max]. So this half is blocked on the float key-range question, not on plumbing.
    //
    // These two stay as live reproducers.

    [Test]
    [Ignore("#591 second shape — blocked on float/double full-range key bounds; see the comment above this fixture region.")]
    public void OrderByFieldAbsentFromPredicate_ReturnsOrderedResults()
    {
        using var dbe = SetupEngine();
        SpawnRange(dbe);

        using var tx = dbe.CreateQuickTransaction();
        var ordered = tx.Query<CompDArch>()
            .WhereField<CompD>(d => d.B >= 0)
            .OrderByField<CompD, float>(d => d.A)
            .ExecuteOrdered();

        Assert.That(ordered, Has.Count.EqualTo(Count), "an empty list here is the #591 OrderBy shape");

        var aValues = ordered.Select(id => tx.Open(id).Read(CompDArch.D).A).ToList();
        Assert.That(aValues, Is.Ordered, "the OrderBy field's index is the scan stream, so results must come out sorted");
    }

    [Test]
    [Ignore("#591 second shape — blocked on float/double full-range key bounds; see the comment above this fixture region.")]
    public void OrderByFieldAbsentFromPredicate_AppliesThePredicate()
    {
        using var dbe = SetupEngine();
        SpawnRange(dbe);

        using var tx = dbe.CreateQuickTransaction();
        var ordered = tx.Query<CompDArch>()
            .WhereField<CompD>(d => d.B >= 6)
            .OrderByField<CompD, float>(d => d.A)
            .ExecuteOrdered();

        // Scanning A's index full-range must not lose B's filter — every evaluator is non-primary here and has to be applied per entity.
        Assert.That(ReadBValues(tx, ordered), Is.EqualTo(new[] { 6, 7, 8, 9 }));
    }

    // ── Control: plans that could always narrow must be untouched ──────────────────────────────────────────────────

    [Test]
    public void NarrowablePredicate_IsUnaffected()
    {
        using var dbe = SetupEngine();
        SpawnRange(dbe);

        using var tx = dbe.CreateQuickTransaction();

        Assert.Multiple(() =>
        {
            Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 5).Execute(), Has.Count.EqualTo(5));
            Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 5).Count(), Is.EqualTo(5));
            Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B == 3).Execute(), Has.Count.EqualTo(1));
        });
    }
}
