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

    private static HashMap<long> ExecutePlan(DatabaseEngine dbe, ExecutionPlan plan)
    {
        var ct = dbe.GetComponentTable<CompD>();
        using var tx = dbe.CreateQuickTransaction();
        var result = new HashMap<long>();
        PipelineExecutor.Instance.Execute(plan, plan.OrderedEvaluators, ct, tx, result);
        return result;
    }

    private static List<long> ExecutePlanOrdered(DatabaseEngine dbe, ExecutionPlan plan,
        int skip = 0, int take = int.MaxValue)
    {
        var ct = dbe.GetComponentTable<CompD>();
        using var tx = dbe.CreateQuickTransaction();
        var result = new List<long>();
        PipelineExecutor.Instance.ExecuteOrdered(plan, plan.OrderedEvaluators, ct, tx, result, skip, take);
        return result;
    }

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

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 40);
        var result = ExecutePlan(dbe, plan);

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
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 50 && p.A > 3.0f);
        var result = ExecutePlan(dbe, plan);

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

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 100);
        var result = ExecutePlan(dbe, plan);

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

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 0);
        var result = ExecutePlan(dbe, plan);

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

        var ct = dbe.GetComponentTable<CompD>();
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        var orderBy = new OrderByField(bFieldIndex, descending: true);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 0, orderBy);
        var result = ExecutePlanOrdered(dbe, plan);

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

        var ct = dbe.GetComponentTable<CompD>();
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        var orderBy = new OrderByField(bFieldIndex);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 0, orderBy);
        var result = ExecutePlanOrdered(dbe, plan);

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

        var ct = dbe.GetComponentTable<CompD>();
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        var orderBy = new OrderByField(bFieldIndex);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 0, orderBy);

        // Skip 3, Take 4
        var result = ExecutePlanOrdered(dbe, plan, skip: 3, take: 4);

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

        var ct = dbe.GetComponentTable<CompD>();
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        var orderBy = new OrderByField(bFieldIndex);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 0, orderBy);
        var result = ExecutePlanOrdered(dbe, plan, skip: 100);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ExecuteOrdered_TakeZero_ReturnsEmpty()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateEntity(dbe, 1.0f, 50, 2.0);

        var ct = dbe.GetComponentTable<CompD>();
        var bFieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition,
            ct.Definition.FieldsByName["B"]);

        var orderBy = new OrderByField(bFieldIndex);
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 0, orderBy);
        var result = ExecutePlanOrdered(dbe, plan, take: 0);

        Assert.That(result, Is.Empty);
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

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B == 42);

        var result = ExecutePlan(dbe, plan);
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

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 50);

        Assert.That(plan.UsesSecondaryIndex, Is.True, "Plan should use secondary index for unique field B");
        Assert.That(plan.PrimaryFieldIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(plan.ToString(), Does.Contain("Index scan"));
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

        // Only predicate is on A (AllowMultiple) — should use index scan with VSBS expansion
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.A > 5.0f);

        Assert.That(plan.UsesSecondaryIndex, Is.True, "AllowMultiple index should be used as primary stream");
        Assert.That(plan.PrimaryFieldIndex, Is.GreaterThanOrEqualTo(0));
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

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B == 42);

        Assert.That(plan.UsesSecondaryIndex, Is.True, "EQ on unique index should use index scan");

        var result = ExecutePlan(dbe, plan);
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

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B == 999);

        Assert.That(plan.UsesSecondaryIndex, Is.True);

        var result = ExecutePlan(dbe, plan);
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

        var (plan, _) = BuildPlanFromExpression(dbe, lambda);

        Assert.That(plan.UsesSecondaryIndex, Is.True);

        var result = ExecutePlan(dbe, plan);
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
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B > 50 && p.A > 3.0f);

        Assert.That(plan.UsesSecondaryIndex, Is.True, "B (unique) should be primary stream");

        var result = ExecutePlan(dbe, plan);

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
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.B >= 150);

        Assert.That(plan.UsesSecondaryIndex, Is.True);

        var result = ExecutePlan(dbe, plan);
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

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.A == 3.0f);
        var results = ExecutePlan(dbe, plan);

        Assert.That(plan.UsesSecondaryIndex, Is.True, "AllowMultiple equality should use index scan");
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

        var (plan, _) = BuildPlanFromExpression(dbe, p => p.A >= 3.0f);
        var results = ExecutePlan(dbe, plan);

        Assert.That(plan.UsesSecondaryIndex, Is.True, "AllowMultiple range should use index scan");
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
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.A == 3.0f && p.B == 100);
        var results = ExecutePlan(dbe, plan);

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
        var (plan, _) = BuildPlanFromExpression(dbe, p => p.A == 3.0f && p.B > 15);
        var results = ExecutePlan(dbe, plan);

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
        var (indexPlan, _) = BuildPlanFromExpression(dbe, p => p.A >= 3.0f);
        Assert.That(indexPlan.UsesSecondaryIndex, Is.True);
        var indexResults = ExecutePlan(dbe, indexPlan);

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
