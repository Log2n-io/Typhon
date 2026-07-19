using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// SV component with two indexed fields for non-primary evaluator testing
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.Pipeline.SvMultiIdx", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct PipeSvData
{
    [Index(AllowMultiple = true)]
    public int Category;
    [Index(AllowMultiple = true)]
    public int Score;
    public PipeSvData(int cat, int score) { Category = cat; Score = score; }
}

[Archetype(330)]
class PipeSvArch : Archetype<PipeSvArch>
{
    public static readonly Comp<PipeSvData> Data = Register<PipeSvData>();
}

// ═══════════════════════════════════════════════════════════════════════════════
// Tests: Combined PipelineExecutor paths for Versioned, SV, and Two-Component
// ═══════════════════════════════════════════════════════════════════════════════

[TestFixture]
[NonParallelizable]
class PipelineExecutorCombinedPathTests : TestBase<PipelineExecutorCombinedPathTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.RegisterComponentFromAccessor<PipeSvData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Spawn N CompD entities with predictable values: A=i*1.5, B=i, C=i*2.5</summary>
    private static EntityId[] SpawnCompDEntities(DatabaseEngine dbe, int count)
    {
        var ids = new EntityId[count];
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            var d = new CompD(i * 1.5f, i, i * 2.5);
            ids[i] = tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
        }
        tx.Commit();
        return ids;
    }

    /// <summary>Spawn N PipeSvData entities with predictable values.</summary>
    private static EntityId[] SpawnSvEntities(DatabaseEngine dbe, int count)
    {
        var ids = new EntityId[count];
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            var d = new PipeSvData(i % 5, i * 10);  // Category cycles 0-4, Score = i*10
            ids[i] = tx.Spawn<PipeSvArch>(PipeSvArch.Data.Set(in d));
        }
        tx.Commit();
        return ids;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A. Versioned single-component — Count (CountOneVersioned path)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Versioned_Count_PrimaryOnly_MatchesExpected()
    {
        using var dbe = SetupEngine();
        SpawnCompDEntities(dbe, 20);

        // B == 10 → exactly 1 match (B is unique index)
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B == 10).Count(), Is.EqualTo(1));
    }

    [Test]
    public void Versioned_Count_PrimaryOnly_NoMatch()
    {
        using var dbe = SetupEngine();
        SpawnCompDEntities(dbe, 10);

        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B == 999).Count(), Is.EqualTo(0));
    }

    [Test]
    public void Versioned_Count_PrimaryOnly_RangeQuery()
    {
        using var dbe = SetupEngine();
        SpawnCompDEntities(dbe, 20);

        // B >= 10 → entities with B=10..19 → 10 entities
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 10).Count(), Is.EqualTo(10));
    }

    [Test]
    public void Versioned_Count_NonPrimaryEvaluator_MultiFieldPredicate()
    {
        using var dbe = SetupEngine();
        SpawnCompDEntities(dbe, 20);

        // B >= 0 (all match primary) AND A > 10.0f (non-primary filter on AllowMultiple field)
        // A = i * 1.5 → A > 10 when i > 6.67 → i=7..19 → 13 entities
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 0 && d.A > 10.0f).Count(), Is.EqualTo(13));
    }

    [Test]
    public void Versioned_Count_AllMatch()
    {
        using var dbe = SetupEngine();
        SpawnCompDEntities(dbe, 15);

        // B >= 0 → all 15 entities match
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 0).Count(), Is.EqualTo(15));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // B. Versioned single-component — Execute (ExecuteOneVersioned path)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Versioned_Execute_ReturnsCorrectEntityIds()
    {
        using var dbe = SetupEngine();
        var ids = SpawnCompDEntities(dbe, 10);

        // B == 5 → should return ids[5]
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<CompDArch>().WhereField<CompD>(d => d.B == 5).Execute();
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(ids[5]));
    }

    [Test]
    public void Versioned_Execute_RangeQuery_MultipleResults()
    {
        using var dbe = SetupEngine();
        var ids = SpawnCompDEntities(dbe, 20);

        // B >= 15 → entities with B=15,16,17,18,19 → 5 entities
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 15).Execute();
        Assert.That(result, Has.Count.EqualTo(5));
        for (int i = 15; i < 20; i++)
        {
            Assert.That(result, Does.Contain(ids[i]));
        }
    }

    [Test]
    public void Versioned_Execute_WithNonPrimaryFilter()
    {
        using var dbe = SetupEngine();
        var ids = SpawnCompDEntities(dbe, 20);

        // B >= 10 (primary) AND A <= 20.0f (non-primary)
        // B >= 10 → i=10..19
        // A = i*1.5 → A <= 20 when i <= 13
        // Intersection: i=10,11,12,13 → 4 entities
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 10 && d.A <= 20.0f).Execute();
        Assert.That(result, Has.Count.EqualTo(4));
        for (int i = 10; i <= 13; i++)
        {
            Assert.That(result, Does.Contain(ids[i]));
        }
    }

    [Test]
    public void Versioned_Execute_EmptyResult()
    {
        using var dbe = SetupEngine();
        SpawnCompDEntities(dbe, 10);

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<CompDArch>().WhereField<CompD>(d => d.B == 999).Execute();
        Assert.That(result, Is.Empty);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // C. Versioned — OrderBy, Skip/Take, Descending
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Versioned_AllowMultiple_PrimaryStream()
    {
        using var dbe = SetupEngine();
        SpawnCompDEntities(dbe, 20);

        // A is AllowMultiple index. A >= 15.0f → i*1.5 >= 15 → i >= 10 → 10 entities
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<CompDArch>().WhereField<CompD>(d => d.A >= 15.0f).Execute();
        Assert.That(result, Has.Count.EqualTo(10));
    }

    [Test]
    public void Versioned_DeletedEntity_NotVisibleInQuery()
    {
        using var dbe = SetupEngine();
        var ids = SpawnCompDEntities(dbe, 10);

        // Destroy entity with B=5
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[5]);
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B == 5).Count(), Is.EqualTo(0), "Deleted entity invisible");
            Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 0).Count(), Is.EqualTo(9), "Total minus deleted");
        }
    }

    [Test]
    public void Versioned_ConcurrentSnapshot_IsolatedView()
    {
        using var dbe = SetupEngine();
        SpawnCompDEntities(dbe, 10);

        // Start read transaction (snapshot)
        using var txRead = dbe.CreateQuickTransaction();
        int countBefore = txRead.Query<CompDArch>().WhereField<CompD>(d => d.B >= 0).Count();

        // Add 5 more entities in a separate transaction
        using (var txWrite = dbe.CreateQuickTransaction())
        {
            for (int i = 10; i < 15; i++)
            {
                var d = new CompD(i * 1.5f, i, i * 2.5);
                txWrite.Spawn<CompDArch>(CompDArch.D.Set(in d));
            }
            txWrite.Commit();
        }

        // Read transaction should still see 10 (MVCC snapshot)
        int countAfter = txRead.Query<CompDArch>().WhereField<CompD>(d => d.B >= 0).Count();
        Assert.That(countAfter, Is.EqualTo(countBefore), "Snapshot isolation: count unchanged");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // D. SV single-component — Count (CountPKsTypedSV path)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SV_Count_PrimaryOnly()
    {
        using var dbe = SetupEngine();
        SpawnSvEntities(dbe, 25);
        dbe.WriteTickFence(1);

        // Category == 2 → entities at index 2,7,12,17,22 → 5 entities
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<PipeSvArch>().WhereField<PipeSvData>(d => d.Category == 2).Count(), Is.EqualTo(5));
    }

    [Test]
    public void SV_Count_NoMatch()
    {
        using var dbe = SetupEngine();
        SpawnSvEntities(dbe, 10);
        dbe.WriteTickFence(1);

        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<PipeSvArch>().WhereField<PipeSvData>(d => d.Category == 99).Count(), Is.EqualTo(0));
    }

    [Test]
    public void SV_Count_NonPrimaryEvaluator()
    {
        using var dbe = SetupEngine();
        SpawnSvEntities(dbe, 25);
        dbe.WriteTickFence(1);

        // Category == 1 (primary) AND Score >= 50 (non-primary)
        // Category==1 at indices 1,6,11,16,21 → Scores: 10,60,110,160,210
        // Score >= 50: indices 6,11,16,21 → 4 entities
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<PipeSvArch>().WhereField<PipeSvData>(d => d.Category == 1 && d.Score >= 50).Count(), Is.EqualTo(4));
    }

    [Test]
    public void SV_Count_AllMatch()
    {
        using var dbe = SetupEngine();
        SpawnSvEntities(dbe, 15);
        dbe.WriteTickFence(1);

        // Category >= 0 → all entities
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<PipeSvArch>().WhereField<PipeSvData>(d => d.Category >= 0).Count(), Is.EqualTo(15));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // E. SV single-component — Execute (ExecutePKsTypedSV path)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SV_Execute_ReturnsCorrectEntityIds()
    {
        using var dbe = SetupEngine();
        var ids = SpawnSvEntities(dbe, 10);
        dbe.WriteTickFence(1);

        // Category == 3 → entities at indices 3,8 → 2 entities
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<PipeSvArch>().WhereField<PipeSvData>(d => d.Category == 3).Execute();
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain(ids[3]));
        Assert.That(result, Does.Contain(ids[8]));
    }

    [Test]
    public void SV_Execute_WithNonPrimaryFilter()
    {
        using var dbe = SetupEngine();
        var ids = SpawnSvEntities(dbe, 20);
        dbe.WriteTickFence(1);

        // Category == 0 (primary) AND Score >= 50 (non-primary)
        // Category==0 at indices 0,5,10,15 → Scores: 0,50,100,150
        // Score >= 50: indices 5,10,15 → 3 entities
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<PipeSvArch>().WhereField<PipeSvData>(d => d.Category == 0 && d.Score >= 50).Execute();
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result, Does.Contain(ids[5]));
        Assert.That(result, Does.Contain(ids[10]));
        Assert.That(result, Does.Contain(ids[15]));
    }

    [Test]
    public void SV_Execute_EmptyResult()
    {
        using var dbe = SetupEngine();
        SpawnSvEntities(dbe, 10);
        dbe.WriteTickFence(1);

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<PipeSvArch>().WhereField<PipeSvData>(d => d.Category == 99).Execute();
        Assert.That(result, Is.Empty);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // F. SV mutation + query interaction
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SV_MutateAndQuery_IndexReflectsChangesAfterTickFence()
    {
        using var dbe = SetupEngine();
        var ids = SpawnSvEntities(dbe, 10);
        dbe.WriteTickFence(1);

        // Mutate entity 0: Category 0 → 4
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(ids[0]).Write(PipeSvArch.Data) = new PipeSvData(4, 999);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using (var tx = dbe.CreateQuickTransaction())
        {
            // Category==0 now has only entity at index 5 (was 0 and 5, but 0 moved to 4)
            Assert.That(tx.Query<PipeSvArch>().WhereField<PipeSvData>(d => d.Category == 0).Count(), Is.EqualTo(1));
            // Category==4 now has entities at indices 4, 9, AND 0 (mutated) → 3
            Assert.That(tx.Query<PipeSvArch>().WhereField<PipeSvData>(d => d.Category == 4).Count(), Is.EqualTo(3));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // G. Versioned — Large dataset
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Versioned_LargeDataset_CountMatchesBruteForce()
    {
        using var dbe = SetupEngine();
        const int n = 200;
        SpawnCompDEntities(dbe, n);

        // B >= 150 → entities 150..199 → 50 entities
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 150).Count(), Is.EqualTo(50));
    }

    [Test]
    public void Versioned_LargeDataset_ExecuteMatchesBruteForce()
    {
        using var dbe = SetupEngine();
        const int n = 200;
        var ids = SpawnCompDEntities(dbe, n);

        // B >= 180 → entities 180..199 → 20 entities
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 180).Execute();
        Assert.That(result, Has.Count.EqualTo(20));
        for (int i = 180; i < 200; i++)
        {
            Assert.That(result, Does.Contain(ids[i]));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // H. SV — Large dataset
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SV_LargeDataset_CountCorrect()
    {
        using var dbe = SetupEngine();
        SpawnSvEntities(dbe, 100);
        dbe.WriteTickFence(1);

        // Category == 3 → indices 3,8,13,...,98 → 20 entities
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<PipeSvArch>().WhereField<PipeSvData>(d => d.Category == 3).Count(), Is.EqualTo(20));
    }

    [Test]
    public void SV_LargeDataset_ExecuteCorrect()
    {
        using var dbe = SetupEngine();
        SpawnSvEntities(dbe, 100);
        dbe.WriteTickFence(1);

        // Category == 1 AND Score >= 500 → Category==1 at 1,6,11,...,96 → Scores: 10,60,...,960
        // Score >= 500: 510(51),560(56),...,960(96) → indices 51,56,61,66,71,76,81,86,91,96 → 10 entities
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<PipeSvArch>().WhereField<PipeSvData>(d => d.Category == 1 && d.Score >= 500).Execute();
        Assert.That(result, Has.Count.EqualTo(10));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // I. MVCC isolation — Versioned query sees correct snapshot
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Versioned_MvccIsolation_QuerySeesCommittedSnapshot()
    {
        using var dbe = SetupEngine();
        var ids = SpawnCompDEntities(dbe, 10);

        // Start a read transaction
        using var txRead = dbe.CreateQuickTransaction();

        // Mutate entity 5 in a separate transaction: B stays 5, A changes from 7.5 to 100
        using (var txWrite = dbe.CreateQuickTransaction())
        {
            txWrite.OpenMut(ids[5]).Write(CompDArch.D) = new CompD(100.0f, 5, 12.5);
            txWrite.Commit();
        }

        // Read transaction should still see the original A value (MVCC snapshot isolation)
        var comp = txRead.Open(ids[5]).Read(CompDArch.D);
        Assert.That(comp.A, Is.EqualTo(7.5f).Within(0.01f), "MVCC: read tx should see pre-mutation value");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // J. Single entity edge case
    // ═══════════════════════════════════════════════════════════════════════
    // K. Dual-bound range queries (PlanBuilder bounds merging)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Versioned_DualBound_RangeQuery_Count()
    {
        using var dbe = SetupEngine();
        SpawnCompDEntities(dbe, 20);

        // B >= 5 AND B < 15 → entities with B=5,6,...,14 → 10 entities
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 5 && d.B < 15).Count(), Is.EqualTo(10));
    }

    [Test]
    public void Versioned_DualBound_RangeQuery_Execute()
    {
        using var dbe = SetupEngine();
        var ids = SpawnCompDEntities(dbe, 20);

        // B > 3 AND B <= 7 → entities with B=4,5,6,7 → 4 entities
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<CompDArch>().WhereField<CompD>(d => d.B > 3 && d.B <= 7).Execute();
        Assert.That(result, Has.Count.EqualTo(4));
        for (int i = 4; i <= 7; i++)
        {
            Assert.That(result, Does.Contain(ids[i]));
        }
    }

    [Test]
    public void Versioned_DualBound_EmptyRange()
    {
        using var dbe = SetupEngine();
        SpawnCompDEntities(dbe, 20);

        // B >= 15 AND B < 10 → empty range (min > max)
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 15 && d.B < 10).Count(), Is.EqualTo(0));
    }

    [Test]
    public void Versioned_DualBound_EqualityCollapse()
    {
        using var dbe = SetupEngine();
        SpawnCompDEntities(dbe, 20);

        // B >= 10 AND B <= 10 → exactly 1 entity (B=10)
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 10 && d.B <= 10).Count(), Is.EqualTo(1));
    }

    [Test]
    public void SV_DualBound_RangeQuery_Count()
    {
        using var dbe = SetupEngine();
        SpawnSvEntities(dbe, 25);
        dbe.WriteTickFence(1);

        // Category >= 1 AND Category <= 3 → entities with Category=1,2,3
        // Out of 25 entities (0-4 cycling): 5 each for 1,2,3 → 15 entities
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<PipeSvArch>().WhereField<PipeSvData>(d => d.Category >= 1 && d.Category <= 3).Count(), Is.EqualTo(15));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // L. Edge cases
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SingleEntity_Count_ReturnsOne()
    {
        using var dbe = SetupEngine();
        SpawnCompDEntities(dbe, 1);

        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B == 0).Count(), Is.EqualTo(1));
    }

    [Test]
    public void SingleEntity_Execute_ReturnsCorrectId()
    {
        using var dbe = SetupEngine();
        var ids = SpawnCompDEntities(dbe, 1);

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<CompDArch>().WhereField<CompD>(d => d.B == 0).Execute();
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(ids[0]));
    }
}
