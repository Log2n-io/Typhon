using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

class PlanBuilderAndExecutorTests : TestBase<PlanBuilderAndExecutorTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    #region Helpers

    private static EntityId CreateEntity(DatabaseEngine dbe, float a, int b, double c)
    {
        using var t = dbe.CreateQuickTransaction();
        var d = new CompD(a, b, c);
        var id = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
        t.Commit();
        return id;
    }

    /// <summary>
    /// Resolves evaluators and builds an execution plan using the basic selectivity estimator.
    /// Returns the plan and the ordered evaluators (from the plan).
    /// </summary>
    private static (ExecutionPlan Plan, FieldEvaluator[] Evaluators) BuildPlanFromExpression(DatabaseEngine dbe,
        System.Linq.Expressions.Expression<System.Func<CompD, bool>> predicate, OrderByField? orderBy = null)
    {
        var fieldPredicates = ExpressionParser.Parse<CompD>(predicate);
        var ct = dbe.GetComponentTable<CompD>();
        var allEvaluators = QueryResolverHelper.ResolveEvaluators(fieldPredicates, ct, 0);
        var estimator = BasicSelectivityEstimator.Instance;

        var plan = orderBy.HasValue ? PlanBuilder.Instance.BuildPlan(allEvaluators, ct, estimator, orderBy.Value) : PlanBuilder.Instance.BuildPlan(allEvaluators, ct, estimator);

        return (plan, plan.OrderedEvaluators);
    }

    /// <summary>Runs a predicate the way production runs it — through the public query API.</summary>
    /// <remarks>
    /// <para>
    /// This fixture used to drive <c>PipelineExecutor</c> over a <see cref="ComponentTable"/> directly. That executor only ever reads the flat per-table home,
    /// which no archetype populates now that all of them are cluster-backed (#629), so every test built on it returned an empty set — and the ones whose
    /// expectation happened to BE empty went on passing while asserting nothing.
    /// </para>
    /// <para>
    /// It is not a shortcut into production either: <c>EcsQuery.ScanAllArchetypes</c> routes to that executor only for an archetype with no cluster state at
    /// all, so going through <see cref="EcsQuery{TArchetype}"/> is what actually exercises the shipped path.
    /// </para>
    /// </remarks>
    private static List<long> ExecuteViaQuery(DatabaseEngine dbe, System.Linq.Expressions.Expression<System.Func<CompD, bool>> predicate)
    {
        using var tx = dbe.CreateQuickTransaction();
        var result = new List<long>();
        foreach (var id in tx.Query<CompDArch>().WhereField<CompD>(predicate).Execute())
        {
            result.Add((long)id.RawValue);
        }

        return result;
    }

    /// <summary>Ordered counterpart of <see cref="ExecuteViaQuery"/>. <paramref name="take"/> below zero means "do not call Take at all".</summary>
    private static List<long> ExecuteOrderedViaQuery(DatabaseEngine dbe, System.Linq.Expressions.Expression<System.Func<CompD, bool>> predicate,
        System.Linq.Expressions.Expression<System.Func<CompD, int>> orderKey, bool descending = false, int skip = 0, int take = -1)
    {
        using var tx = dbe.CreateQuickTransaction();
        var q = tx.Query<CompDArch>().WhereField<CompD>(predicate);
        q = descending ? q.OrderByFieldDescending<CompD, int>(orderKey) : q.OrderByField<CompD, int>(orderKey);

        if (skip > 0)
        {
            q = q.Skip(skip);
        }

        if (take >= 0)
        {
            q = q.Take(take);
        }

        var result = new List<long>();
        foreach (var id in q.ExecuteOrdered())
        {
            result.Add((long)id.RawValue);
        }

        return result;
    }

    /// <summary>
    /// Asserts the per-archetype B+Tree backing <paramref name="indexedFieldIndex"/> holds entries — the post-#629 stand-in for <c>plan.UsesSecondaryIndex</c>.
    /// </summary>
    /// <remarks>
    /// <c>UsesSecondaryIndex</c> is <c>PrimaryFieldIndex &gt;= 0</c>, and <c>PlanBuilder</c> only sets that when the SHARED per-ComponentTable tree has
    /// entries. That tree is permanently empty now, so the flag is permanently false and can no longer express "this query has an index available" — the
    /// sentinel is overloaded, and untangling it is tracked separately. What remains assertable, and is what these tests were really about, is that the index
    /// exists, lives on the archetype, and holds every distinct key.
    /// </remarks>
    /// <param name="expectedKeys">
    /// DISTINCT key count, not entity count. A unique index makes the two identical, but an <c>AllowMultiple</c> index stores one entry per key with the
    /// matching entities hanging off it, so four entities with A = 3, 3, 5, 3 are two entries. Passing an entity count here is the easy mistake.
    /// </param>
    private static void AssertArchetypeIndexPopulated(DatabaseEngine dbe, int indexedFieldIndex, int expectedKeys)
    {
        var ct = dbe.GetComponentTable<CompD>();
        var index = IndexTestHelpers.ArchetypeIndex<CompDArch>(dbe, ct, indexedFieldIndex);
        Assert.That(index, Is.Not.Null, "the archetype must own a B+Tree for this field");
        Assert.That(index.EntryCount, Is.EqualTo(expectedKeys), "the per-archetype index must hold every distinct key of the archetype");
    }

    /// <summary>Indices into <c>ComponentTable.IndexedFieldInfos</c> for CompD's three indexed fields.</summary>
    private const int FieldA = 0;
    private const int FieldB = 1;

    #endregion

    #region PlanBuilder Tests

    [Test]
    public void PlanBuilder_SinglePredicate_SingleEvaluator()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateEntity(dbe, 1.0f, 50, 2.0);
        var (plan, evaluators) = BuildPlanFromExpression(dbe, p => p.B > 40);

        Assert.That(plan.OrderedEvaluators, Has.Length.EqualTo(1));
        Assert.That(evaluators, Has.Length.EqualTo(1));
    }

    [Test]
    public void PlanBuilder_MultiPredicate_SortedByAscendingEstimate()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Create 20 entities with varying field values
        for (var i = 0; i < 20; i++)
        {
            CreateEntity(dbe, i / 10.0f, i, i * 1.0);
        }

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 15 && p.A > 1.0f);

        // Verify evaluators are ordered by ascending estimated cardinality
        Assert.That(plan.OrderedEvaluators, Has.Length.EqualTo(2));
        Assert.That(plan.EstimatedCounts, Has.Length.EqualTo(2));
        Assert.That(plan.EstimatedCounts[0], Is.LessThanOrEqualTo(plan.EstimatedCounts[1]));
    }

    [Test]
    public void PlanBuilder_OrderBy_SetsDescendingFlag()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        for (var i = 0; i < 50; i++)
        {
            CreateEntity(dbe, i * 1.0f, i, i * 1.0);
        }

        var ct = dbe.GetComponentTable<CompD>();
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        // OrderBy sets the descending flag; evaluators still ordered by selectivity
        var orderBy = new OrderByField(bFieldIndex);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 10 && p.A > 5.0f, orderBy);

        Assert.That(plan.Descending, Is.False);
        Assert.That(plan.OrderedEvaluators, Has.Length.EqualTo(2));
    }

    [Test]
    public void PlanBuilder_OrderByPK_AllEvaluatorsAreFilters()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateEntity(dbe, 1.0f, 50, 2.0);

        var orderBy = new OrderByField(-1);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 40, orderBy);

        // PK is always the primary scan; all predicates become filter evaluators
        Assert.That(plan.OrderedEvaluators, Has.Length.EqualTo(1));
        Assert.That(plan.Descending, Is.False);
    }

    [Test]
    public void PlanBuilder_OrderByDescending_SetsFlag()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateEntity(dbe, 1.0f, 50, 2.0);

        var ct = dbe.GetComponentTable<CompD>();
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        var orderBy = new OrderByField(bFieldIndex, descending: true);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 10, orderBy);

        Assert.That(plan.Descending, Is.True);
    }

    [Test]
    public void PlanBuilder_TieBreaking_LowerFieldIndexFirst()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Single entity: both fields have the same estimated cardinality
        CreateEntity(dbe, 1.0f, 1, 1.0);

        var ct = dbe.GetComponentTable<CompD>();
        var aFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["A"]);
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.A == 1.0f && p.B == 1);

        // With equal selectivity, the lower FieldIndex should come first
        var expectedFirst = System.Math.Min(aFieldIndex, bFieldIndex);
        Assert.That(plan.OrderedEvaluators[0].FieldIndex, Is.EqualTo(expectedFirst));
    }

    #endregion

    #region PipelineExecutor Tests

    [Test]
    public void Execute_SinglePredicate_FiltersCorrectly()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var id1 = CreateEntity(dbe, 1.0f, 50, 2.0);
        var id2 = CreateEntity(dbe, 1.0f, 30, 2.0);
        var id3 = CreateEntity(dbe, 1.0f, 60, 2.0);
        var pk1 = (long)id1.RawValue;
        var pk2 = (long)id2.RawValue;
        var pk3 = (long)id3.RawValue;

        var result = ExecuteViaQuery(dbe, p => p.B > 40);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain(pk1));
        Assert.That(result, Does.Not.Contain(pk2));
        Assert.That(result, Does.Contain(pk3));
    }

    [Test]
    public void Execute_MultiPredicate_MatchesBruteForce()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Create 20 entities with varying B and A values
        var ids = new EntityId[20];
        for (var i = 0; i < 20; i++)
        {
            ids[i] = CreateEntity(dbe, i * 0.5f, i * 5, i * 1.0);
        }

        // B > 50 && A > 3.0f => Intersection: i=11..19 => 9 entities
        var result = ExecuteViaQuery(dbe, p => p.B > 50 && p.A > 3.0f);

        // Brute-force verification
        var expected = new HashSet<long>();
        for (var i = 0; i < 20; i++)
        {
            if (i * 5 > 50 && i * 0.5f > 3.0f)
            {
                expected.Add((long)ids[i].RawValue);
            }
        }

        Assert.That(result, Is.EquivalentTo(expected));
    }

    [Test]
    public void Execute_EmptyResult_NoMatches()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateEntity(dbe, 1.0f, 10, 2.0);
        CreateEntity(dbe, 2.0f, 20, 3.0);

        var result = ExecuteViaQuery(dbe, p => p.B > 100);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Execute_AllMatch_EveryEntityPasses()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = (long)CreateEntity(dbe, 1.0f, 50, 2.0).RawValue;
        var pk2 = (long)CreateEntity(dbe, 2.0f, 60, 3.0).RawValue;
        var pk3 = (long)CreateEntity(dbe, 3.0f, 70, 4.0).RawValue;

        var result = ExecuteViaQuery(dbe, p => p.B > 0);

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result, Does.Contain(pk1));
        Assert.That(result, Does.Contain(pk2));
        Assert.That(result, Does.Contain(pk3));
    }

    [Test]
    public void ExecuteOrdered_Descending_ReverseOrder()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = (long)CreateEntity(dbe, 1.0f, 10, 2.0).RawValue;
        var pk2 = (long)CreateEntity(dbe, 2.0f, 20, 3.0).RawValue;
        var pk3 = (long)CreateEntity(dbe, 3.0f, 30, 4.0).RawValue;

        var result = ExecuteOrderedViaQuery(dbe, p => p.B > 0, x => x.B, descending: true);

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0], Is.EqualTo(pk3)); // PK order descending
        Assert.That(result[1], Is.EqualTo(pk2));
        Assert.That(result[2], Is.EqualTo(pk1));
    }

    [Test]
    public void ExecuteOrdered_Ascending_NaturalOrder()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = (long)CreateEntity(dbe, 1.0f, 10, 2.0).RawValue;
        var pk2 = (long)CreateEntity(dbe, 2.0f, 20, 3.0).RawValue;
        var pk3 = (long)CreateEntity(dbe, 3.0f, 30, 4.0).RawValue;

        var result = ExecuteOrderedViaQuery(dbe, p => p.B > 0, x => x.B);

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0], Is.EqualTo(pk1)); // PK order ascending
        Assert.That(result[1], Is.EqualTo(pk2));
        Assert.That(result[2], Is.EqualTo(pk3));
    }

    [Test]
    public void ExecuteOrdered_SkipTake_CorrectWindow()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pks = new long[10];
        for (var i = 0; i < 10; i++)
        {
            pks[i] = (long)CreateEntity(dbe, 1.0f, (i + 1) * 10, 2.0).RawValue;
        }

        // Skip 3, Take 4
        var result = ExecuteOrderedViaQuery(dbe, p => p.B > 0, x => x.B, skip: 3, take: 4);

        Assert.That(result, Has.Count.EqualTo(4));
        Assert.That(result[0], Is.EqualTo(pks[3]));
        Assert.That(result[1], Is.EqualTo(pks[4]));
        Assert.That(result[2], Is.EqualTo(pks[5]));
        Assert.That(result[3], Is.EqualTo(pks[6]));
    }

    [Test]
    public void ExecuteOrdered_SkipExceedsCount_ReturnsEmpty()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateEntity(dbe, 1.0f, 50, 2.0);
        CreateEntity(dbe, 2.0f, 60, 3.0);

        var result = ExecuteOrderedViaQuery(dbe, p => p.B > 0, x => x.B, skip: 100);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ExecuteOrdered_TakeZero_ReturnsEmpty()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateEntity(dbe, 1.0f, 50, 2.0);
        CreateEntity(dbe, 2.0f, 60, 3.0);

        // Not a typo, and not the behaviour you would design: EcsQuery stores the limit in _take, whose UNSET value is also 0, and every execution path reads
        // it as `_take > 0 ? _take : int.MaxValue`. Take(0) is therefore indistinguishable from never calling Take, and means unlimited rather than nothing.
        // The old assertion here said "empty", which was true of PipelineExecutor.ExecuteOrdered's own take parameter — a different knob with the opposite
        // convention — and went on passing against the query API only because the flat scan it ran returned nothing at all.
        var result = ExecuteOrderedViaQuery(dbe, p => p.B > 0, x => x.B, take: 0);

        Assert.That(result, Has.Count.EqualTo(2), "Take(0) currently means 'no limit', because 0 is also the unset sentinel for _take");
    }

    // Execute_OrderByPK_FiltersCorrectly — removed (PK B+Tree eliminated, PK ordering no longer exists)

    [Test]
    public void Execute_EqualityPredicate_MatchesExactValue()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = (long)CreateEntity(dbe, 1.0f, 42, 2.0).RawValue;
        CreateEntity(dbe, 2.0f, 99, 3.0);
        CreateEntity(dbe, 3.0f, 77, 4.0);

        var result = ExecuteViaQuery(dbe, p => p.B == 42);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(pk1));
    }

    [Test]
    public void ExecutionPlan_ToString_DiagnosticOutput()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        CreateEntity(dbe, 1.0f, 50, 2.0);

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 40);
        var str = plan.ToString();

        // Plan may use PK scan or secondary Index scan depending on available indexes
        Assert.That(str, Does.Contain("scan"));
        Assert.That(str, Does.Contain("Field["));
    }

    #endregion

    #region Secondary Index Scan Tests

    [Test]
    public void PlanBuilder_UniqueIndex_SelectsSecondaryStream()
    {
        // B has [Index] (unique) — should be selected as primary stream
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        for (var i = 0; i < 10; i++)
        {
            CreateEntity(dbe, i * 1.0f, i * 10, i * 2.0);
        }

        AssertArchetypeIndexPopulated(dbe, FieldB, 10);
        Assert.That(ExecuteViaQuery(dbe, p => p.B > 50), Has.Count.EqualTo(4), "B = 60,70,80,90");
    }

    [Test]
    public void PlanBuilder_AllowMultipleIndex_UsedAsPrimaryStream()
    {
        // A has [Index(AllowMultiple = true)] — should be selected as primary stream (VSBS expansion supported)
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        for (var i = 0; i < 10; i++)
        {
            CreateEntity(dbe, i * 1.0f, i * 10, i * 2.0);
        }

        // Only predicate is on A (AllowMultiple) — the archetype's tree must carry every entity, duplicates included
        AssertArchetypeIndexPopulated(dbe, FieldA, 10);
        Assert.That(ExecuteViaQuery(dbe, p => p.A > 5.0f), Has.Count.EqualTo(4), "A = 6,7,8,9");
    }

    [Test]
    public void IndexScan_EqualityPointQuery_SingleMatch()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = (long)CreateEntity(dbe, 1.0f, 42, 1.0).RawValue;
        CreateEntity(dbe, 2.0f, 99, 2.0);
        CreateEntity(dbe, 3.0f, 77, 3.0);

        AssertArchetypeIndexPopulated(dbe, FieldB, 3);

        var result = ExecuteViaQuery(dbe, p => p.B == 42);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(pk1));
    }

    [Test]
    public void IndexScan_EqualityPointQuery_NoMatch()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateEntity(dbe, 1.0f, 10, 1.0);
        CreateEntity(dbe, 2.0f, 20, 2.0);

        AssertArchetypeIndexPopulated(dbe, FieldB, 2);

        var result = ExecuteViaQuery(dbe, p => p.B == 999);
        Assert.That(result, Is.Empty);
    }

    [TestCase(CompareOp.GreaterThan, 2, false)]       // B > 30: match B=40,50; exclude B=30
    [TestCase(CompareOp.GreaterThanOrEqual, 3, true)] // B >= 30: match B=30,40,50
    [TestCase(CompareOp.LessThan, 2, false)]          // B < 30: match B=10,20; exclude B=30
    [TestCase(CompareOp.LessThanOrEqual, 3, true)]    // B <= 30: match B=10,20,30
    [Test]
    public void IndexScan_BoundaryBehavior(CompareOp op, int expectedCount, bool boundaryIncluded)
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Create entities with B = 10, 20, 30, 40, 50
        var pks = new long[5];
        for (var i = 0; i < 5; i++)
        {
            pks[i] = (long)CreateEntity(dbe, 1.0f, (i + 1) * 10, 1.0).RawValue;
        }

        // Build expression: p.B {op} 30
        var param = System.Linq.Expressions.Expression.Parameter(typeof(CompD), "p");
        var field = System.Linq.Expressions.Expression.Field(param, nameof(CompD.B));
        var constant = System.Linq.Expressions.Expression.Constant(30);
        var exprType = op switch
        {
            CompareOp.GreaterThan => System.Linq.Expressions.ExpressionType.GreaterThan,
            CompareOp.GreaterThanOrEqual => System.Linq.Expressions.ExpressionType.GreaterThanOrEqual,
            CompareOp.LessThan => System.Linq.Expressions.ExpressionType.LessThan,
            CompareOp.LessThanOrEqual => System.Linq.Expressions.ExpressionType.LessThanOrEqual,
            _ => throw new System.ArgumentOutOfRangeException()
        };
        var binary = System.Linq.Expressions.Expression.MakeBinary(exprType, field, constant);
        var lambda = System.Linq.Expressions.Expression.Lambda<System.Func<CompD, bool>>(binary, param);

        AssertArchetypeIndexPopulated(dbe, FieldB, 5);

        var result = ExecuteViaQuery(dbe, lambda);
        Assert.That(result, Has.Count.EqualTo(expectedCount));

        // Verify boundary inclusion/exclusion for B=30 (pks[2])
        if (boundaryIncluded)
        {
            Assert.That(result, Does.Contain(pks[2]), "B=30 should be included");
        }
        else
        {
            Assert.That(result, Does.Not.Contain(pks[2]), "B=30 should be excluded");
        }
    }

    [Test]
    public void IndexScan_MultiPredicate_IndexOnPrimaryAndFilterOnSecondary()
    {
        // B (unique index) used as primary stream, A (AllowMultiple) evaluated as filter
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var ids = new EntityId[20];
        for (var i = 0; i < 20; i++)
        {
            ids[i] = CreateEntity(dbe, i * 0.5f, i * 5, i * 1.0);
        }

        // B > 50 && A > 3.0f → B narrows to i=11..19 (B=55..95), A > 3.0 further filters to i >= 7
        // Intersection: i=11..19 → 9 entities
        AssertArchetypeIndexPopulated(dbe, FieldB, 20);
        var result = ExecuteViaQuery(dbe, p => p.B > 50 && p.A > 3.0f);

        // Brute-force verification
        var expected = new HashSet<long>();
        for (var i = 0; i < 20; i++)
        {
            if (i * 5 > 50 && i * 0.5f > 3.0f)
            {
                expected.Add((long)ids[i].RawValue);
            }
        }

        Assert.That(result, Is.EquivalentTo(expected));
    }

    [Test]
    public void IndexScan_LargeDataset_CorrectResults()
    {
        // Verify correctness at scale — wrong scan range would produce wrong results
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pks = new long[200];
        for (var i = 0; i < 200; i++)
        {
            pks[i] = (long)CreateEntity(dbe, i * 0.1f, i, i * 0.5).RawValue;
        }

        // B >= 150: should match exactly 50 entities (i=150..199)
        AssertArchetypeIndexPopulated(dbe, FieldB, 200);

        var result = ExecuteViaQuery(dbe, p => p.B >= 150);
        Assert.That(result, Has.Count.EqualTo(50));

        for (var i = 150; i < 200; i++)
        {
            Assert.That(result, Does.Contain(pks[i]));
        }

        for (var i = 0; i < 150; i++)
        {
            Assert.That(result, Does.Not.Contain(pks[i]));
        }
    }

    [Test]
    public void IndexScan_OrderByPK_FallsBackToPKScan()
    {
        // When OrderBy is by PK, secondary index should NOT be used
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        for (var i = 0; i < 10; i++)
        {
            CreateEntity(dbe, 1.0f, (i + 1) * 10, 1.0);
        }

        var orderBy = new OrderByField(-1); // PK ordering
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 50, orderBy);

        // Still the right expectation, but no longer evidence of anything: UsesSecondaryIndex is false for EVERY plan now, because PlanBuilder only sets
        // PrimaryFieldIndex from the shared per-ComponentTable tree and that tree is permanently empty. This assertion cannot distinguish "OrderBy PK forced a
        // PK scan" from "the flag is stuck". It regains its meaning when the overloaded sentinel is untangled; kept until then so the intent is not lost.
        Assert.That(plan.UsesSecondaryIndex, Is.False, "OrderBy PK should force PK scan");
    }

    #endregion

    #region AllowMultiple Primary Stream Tests

    [Test]
    public void AllowMultiple_PrimaryStream_EqualityPredicate()
    {
        // AllowMultiple index on A — equality predicate should use index scan and return correct results
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Create entities with duplicate A values (AllowMultiple)
        var pk1 = (long)CreateEntity(dbe, 3.0f, 10, 1.0).RawValue;
        var pk2 = (long)CreateEntity(dbe, 3.0f, 20, 2.0).RawValue;
        var pk3 = (long)CreateEntity(dbe, 5.0f, 30, 3.0).RawValue;
        var pk4 = (long)CreateEntity(dbe, 3.0f, 40, 4.0).RawValue;

        AssertArchetypeIndexPopulated(dbe, FieldA, 2); // 4 entities, keys {3.0, 5.0}
        var results = ExecuteViaQuery(dbe, p => p.A == 3.0f);

        Assert.That(results, Has.Count.EqualTo(3));
        Assert.That(results, Does.Contain(pk1));
        Assert.That(results, Does.Contain(pk2));
        Assert.That(results, Does.Contain(pk4));
        Assert.That(results, Does.Not.Contain(pk3));
    }

    [Test]
    public void AllowMultiple_PrimaryStream_RangePredicate()
    {
        // AllowMultiple index on A — range predicate should use index scan and return correct results
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = (long)CreateEntity(dbe, 1.0f, 10, 1.0).RawValue;
        var pk2 = (long)CreateEntity(dbe, 3.0f, 20, 2.0).RawValue;
        var pk3 = (long)CreateEntity(dbe, 3.0f, 30, 3.0).RawValue;
        var pk4 = (long)CreateEntity(dbe, 5.0f, 40, 4.0).RawValue;
        var pk5 = (long)CreateEntity(dbe, 7.0f, 50, 5.0).RawValue;

        AssertArchetypeIndexPopulated(dbe, FieldA, 4); // 5 entities, keys {1.0, 3.0, 5.0, 7.0}
        var results = ExecuteViaQuery(dbe, p => p.A >= 3.0f);

        Assert.That(results, Has.Count.EqualTo(4));
        Assert.That(results, Does.Contain(pk2));
        Assert.That(results, Does.Contain(pk3));
        Assert.That(results, Does.Contain(pk4));
        Assert.That(results, Does.Contain(pk5));
        Assert.That(results, Does.Not.Contain(pk1));
    }

    [Test]
    public void AllowMultiple_PrimaryStream_SelectivityOrdering()
    {
        // When both AllowMultiple A and unique B have predicates, PlanBuilder should pick the most selective one
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        for (var i = 0; i < 20; i++)
        {
            CreateEntity(dbe, 3.0f, i, i * 1.0); // 20 entities with A=3.0, B=0..19
        }
        CreateEntity(dbe, 5.0f, 100, 50.0);

        // A == 3.0 matches 20 entities, B == 100 matches 1 entity — B should be picked as primary
        var results = ExecuteViaQuery(dbe, p => p.A == 3.0f && p.B == 100);

        Assert.That(results, Has.Count.EqualTo(0), "No entity has both A==3.0 and B==100");
    }

    [Test]
    public void AllowMultiple_PrimaryStream_ChainedPredicates_CorrectResults()
    {
        // AllowMultiple A as primary + filter on B — verifies VSBS expansion + filter evaluation
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = (long)CreateEntity(dbe, 3.0f, 10, 1.0).RawValue;
        var pk2 = (long)CreateEntity(dbe, 3.0f, 20, 2.0).RawValue;
        var pk3 = (long)CreateEntity(dbe, 3.0f, 30, 3.0).RawValue;
        CreateEntity(dbe, 5.0f, 40, 4.0);

        // A == 3.0 (AllowMultiple primary) + B > 15 (filter)
        var results = ExecuteViaQuery(dbe, p => p.A == 3.0f && p.B > 15);

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results, Does.Contain(pk2));
        Assert.That(results, Does.Contain(pk3));
    }

    [Test]
    public void AllowMultiple_PrimaryStream_MatchesBruteForce()
    {
        // Verify AllowMultiple index scan matches brute-force PK scan for a variety of data
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Insert 50 entities with various A values (duplicates expected)
        var allIds = new List<EntityId>();
        for (var i = 0; i < 50; i++)
        {
            allIds.Add(CreateEntity(dbe, (i % 5) * 1.0f, i, i * 0.5));
        }

        // Query via AllowMultiple index
        AssertArchetypeIndexPopulated(dbe, FieldA, 5); // 50 entities, keys {0.0 … 4.0} from (i % 5)
        var indexResults = ExecuteViaQuery(dbe, p => p.A >= 3.0f);

        // Brute-force: read all entities and filter manually
        using var tx = dbe.CreateQuickTransaction();
        var expected = new HashSet<long>();
        foreach (var id in allIds)
        {
            var comp = tx.Open(id).Read(CompDArch.D);
            if (comp.A >= 3.0f)
            {
                expected.Add((long)id.RawValue);
            }
        }

        Assert.That(indexResults, Is.EquivalentTo(expected), "AllowMultiple index scan must match brute-force results");
    }

    #endregion
}
