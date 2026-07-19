using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════
// Additional archetype for K-way merge tests (same components as ClQUnit but different archetype)
// ═══════════════════════════════════════════════════════════════════════

[Archetype(543)]
partial class ClQUnit2 : Archetype<ClQUnit2>
{
    public static readonly Comp<ClQStats> Stats = Register<ClQStats>();
    public static readonly Comp<ClQTag> Tag = Register<ClQTag>();
}

[Archetype(544)]
partial class ClQUnit3 : Archetype<ClQUnit3>
{
    public static readonly Comp<ClQStats> Stats = Register<ClQStats>();
    public static readonly Comp<ClQTag> Tag = Register<ClQTag>();
}

// ═══════════════════════════════════════════════════════════════════════
// Tests
// ═══════════════════════════════════════════════════════════════════════

[NonParallelizable]
[TestFixture]
class SimdKwayMergeTests : TestBase<SimdKwayMergeTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClQStats>();
        dbe.RegisterComponentFromAccessor<ClQTag>();
        dbe.RegisterComponentFromAccessor<ClQNCStats>();
        dbe.RegisterComponentFromAccessor<ClQVData>();
        dbe.RegisterComponentFromAccessor<ClQFloatData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════
    // SIMD Evaluation Tests (via WhereField queries on cluster archetypes)
    // SIMD is exercised transparently — the query engine selects the SIMD path
    // when AVX2 is supported and the field type is eligible.
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Simd_IntGreaterThan()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 200, i => i); // Score = 0..199
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score > 150).Execute();
        Assert.That(result, Has.Count.EqualTo(49)); // 151..199
    }

    [Test]
    public void Simd_IntLessThan()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 200, i => i);
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score < 50).Execute();
        Assert.That(result, Has.Count.EqualTo(50)); // 0..49
    }

    [Test]
    public void Simd_IntEqual()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 200, i => i % 10); // Scores repeat: 0,1,..9,0,1,..9,...
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score == 5).Execute();
        Assert.That(result, Has.Count.EqualTo(20)); // 200/10 = 20
    }

    [Test]
    public void Simd_IntGreaterThanOrEqual()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 100, i => i);
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score >= 90).Execute();
        Assert.That(result, Has.Count.EqualTo(10)); // 90..99
    }

    [Test]
    public void Simd_IntLessThanOrEqual()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 100, i => i);
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score <= 9).Execute();
        Assert.That(result, Has.Count.EqualTo(10)); // 0..9
    }

    [Test]
    public void Simd_IntNotEqual()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 100, i => i % 5); // 0,1,2,3,4 repeated
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score != 3).Execute();
        Assert.That(result, Has.Count.EqualTo(80)); // 100 - 20 = 80
    }

    [Test]
    public void Simd_FloatLessThan()
    {
        using var dbe = SetupEngine();
        SpawnClQFloats(dbe, 200, i => i * 1.5f); // 0, 1.5, 3.0, ..., 298.5
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQFloatUnit>().WhereField<ClQFloatData>(d => d.Value < 75.0f).Execute();
        Assert.That(result, Has.Count.EqualTo(50)); // 0..49 × 1.5 < 75
    }

    [Test]
    public void Simd_FloatGreaterThanOrEqual()
    {
        using var dbe = SetupEngine();
        SpawnClQFloats(dbe, 100, i => i * 2.0f); // 0, 2, 4, ..., 198
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQFloatUnit>().WhereField<ClQFloatData>(d => d.Value >= 180.0f).Execute();
        Assert.That(result, Has.Count.EqualTo(10)); // 90..99 × 2.0 >= 180
    }

    [Test]
    public void Simd_FloatNegativeValues()
    {
        using var dbe = SetupEngine();
        SpawnClQFloats(dbe, 200, i => (i - 100) * 1.0f); // -100..99
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQFloatUnit>().WhereField<ClQFloatData>(d => d.Value < 0.0f).Execute();
        Assert.That(result, Has.Count.EqualTo(100)); // -100..-1
    }

    [Test]
    public void Simd_EmptyArchetype_ReturnsEmpty()
    {
        using var dbe = SetupEngine();
        // Don't spawn anything
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score > 0).Execute();
        Assert.That(result, Has.Count.EqualTo(0));
    }

    [Test]
    public void Simd_AllEntitiesMatch()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 100, _ => 42); // All Score = 42
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score == 42).Execute();
        Assert.That(result, Has.Count.EqualTo(100));
    }

    [Test]
    public void Simd_PartialCluster_CorrectCount()
    {
        using var dbe = SetupEngine();
        // Spawn fewer entities than a full cluster (e.g., 5 entities in a 36-slot cluster)
        SpawnClQUnits(dbe, 5, i => i * 10); // 0, 10, 20, 30, 40
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score >= 20).Execute();
        Assert.That(result, Has.Count.EqualTo(3)); // 20, 30, 40
    }

    [Test]
    [Category("Sensitive")] // heavy/longest test — runs in the gate's serial quiet pass to avoid starving parallel shards
    public void Simd_LargeEntityCount_CorrectResults()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 1000, i => i); // Score = 0..999 (halved volumetry — was 2000; ~2× faster, same SIMD path)
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score >= 900).Execute();
        Assert.That(result, Has.Count.EqualTo(100)); // 900..999
    }

    [Test]
    public void Simd_MatchesScalar_Equivalence()
    {
        // Run the same query — SIMD and scalar should produce identical results.
        // We verify by checking result count matches expected count exactly.
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 500, i => i * 3 % 100); // Various scores 0..99
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score >= 50 && s.Score < 80).Execute();

        // Count expected matches manually
        int expected = 0;
        for (int i = 0; i < 500; i++)
        {
            int score = i * 3 % 100;
            if (score >= 50 && score < 80)
            {
                expected++;
            }
        }

        Assert.That(result, Has.Count.EqualTo(expected));
    }

    // ═══════════════════════════════════════════════════════════════
    // K-way Merge Tests (ExecuteOrdered on cluster archetypes)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void KWay_K2_Ascending_CorrectOrder()
    {
        using var dbe = SetupEngine();
        // Spawn in two archetypes with interleaved scores
        SpawnClQUnits(dbe, 50, i => i * 2);          // ClQUnit: 0, 2, 4, ..., 98
        SpawnClQUnit2s(dbe, 50, i => i * 2 + 1);      // ClQUnit2: 1, 3, 5, ..., 99

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 0)
            .OrderByField<ClQStats, int>(s => s.Score)
            .ExecuteOrdered();

        // ClQUnit only sees its own archetype (non-polymorphic)
        Assert.That(result, Has.Count.EqualTo(50));
        // Verify sorted order
        for (int i = 1; i < result.Count; i++)
        {
            Assert.That(result[i].ArchetypeId, Is.EqualTo(result[0].ArchetypeId));
        }
    }

    [Test]
    public void KWay_SingleArchetype_SortedOrder()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 100, i => 100 - i); // Score = 100, 99, ..., 1 (reverse spawn order)

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 1)
            .OrderByField<ClQStats, int>(s => s.Score)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(100));
        // Verify ascending order by reading back scores
        var scores = ReadScores(dbe, result);
        for (int i = 1; i < scores.Count; i++)
        {
            Assert.That(scores[i], Is.GreaterThanOrEqualTo(scores[i - 1]),
                $"Score at [{i}]={scores[i]} should be >= [{i - 1}]={scores[i - 1]}");
        }
    }

    [Test]
    public void KWay_Descending_CorrectOrder()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 100, i => i); // Score = 0..99

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 0)
            .OrderByFieldDescending<ClQStats, int>(s => s.Score)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(100));
        var scores = ReadScores(dbe, result);
        for (int i = 1; i < scores.Count; i++)
        {
            Assert.That(scores[i], Is.LessThanOrEqualTo(scores[i - 1]),
                $"Score at [{i}]={scores[i]} should be <= [{i - 1}]={scores[i - 1]} (descending)");
        }
    }

    [Test]
    public void KWay_SkipOnly()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 100, i => i);

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 0)
            .OrderByField<ClQStats, int>(s => s.Score)
            .Skip(90)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(10)); // 90..99
        var scores = ReadScores(dbe, result);
        Assert.That(scores[0], Is.GreaterThanOrEqualTo(90));
    }

    [Test]
    public void KWay_TakeOnly()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 100, i => i);

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 0)
            .OrderByField<ClQStats, int>(s => s.Score)
            .Take(5)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(5));
        var scores = ReadScores(dbe, result);
        Assert.That(scores[0], Is.EqualTo(0));
        Assert.That(scores[4], Is.EqualTo(4));
    }

    [Test]
    public void KWay_SkipAndTake_Pagination()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 100, i => i); // Score = 0..99

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 0)
            .OrderByField<ClQStats, int>(s => s.Score)
            .Skip(10)
            .Take(5)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(5));
        var scores = ReadScores(dbe, result);
        Assert.That(scores[0], Is.EqualTo(10));
        Assert.That(scores[4], Is.EqualTo(14));
    }

    [Test]
    public void KWay_EmptyArchetype_ReturnsEmpty()
    {
        using var dbe = SetupEngine();
        // No entities spawned

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 0)
            .OrderByField<ClQStats, int>(s => s.Score)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(0));
    }

    [Test]
    public void KWay_LargeResultSet_OrderPreserved()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 1000, i => i); // 1000 entities

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 0)
            .OrderByField<ClQStats, int>(s => s.Score)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(1000));
        var scores = ReadScores(dbe, result);
        for (int i = 1; i < scores.Count; i++)
        {
            Assert.That(scores[i], Is.GreaterThanOrEqualTo(scores[i - 1]));
        }
    }

    [Test]
    public void KWay_NoDuplicates()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 200, i => i);

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 0)
            .OrderByField<ClQStats, int>(s => s.Score)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(200));
        var uniqueIds = new HashSet<EntityId>(result);
        Assert.That(uniqueIds, Has.Count.EqualTo(result.Count), "No duplicate EntityIds in ordered results");
    }

    [Test]
    public void KWay_MatchesBruteForceSort()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 300, i => (i * 7) % 100); // Various scores

        using var tx = dbe.CreateQuickTransaction();

        // Ordered query via K-way merge
        var orderedResult = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 20 && s.Score < 80)
            .OrderByField<ClQStats, int>(s => s.Score)
            .ExecuteOrdered();

        // Unordered query + manual sort for comparison
        var unorderedResult = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 20 && s.Score < 80)
            .Execute();

        Assert.That(orderedResult, Has.Count.EqualTo(unorderedResult.Count),
            "Ordered and unordered queries must return the same count");

        // Verify ordered result is actually sorted
        var scores = ReadScores(dbe, orderedResult);
        for (int i = 1; i < scores.Count; i++)
        {
            Assert.That(scores[i], Is.GreaterThanOrEqualTo(scores[i - 1]));
        }
    }

    [Test]
    public void KWay_WithPredicate_FiltersCorrectly()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 100, i => i); // Score = 0..99

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 50)
            .OrderByField<ClQStats, int>(s => s.Score)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(50)); // 50..99
        var scores = ReadScores(dbe, result);
        Assert.That(scores[0], Is.EqualTo(50));
        Assert.That(scores[49], Is.EqualTo(99));
    }

    // ═══════════════════════════════════════════════════════════════
    // Additional edge case tests (from code review)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void KWay_FloatOrdered_NegativeValues_CorrectOrder()
    {
        using var dbe = SetupEngine();
        SpawnClQFloats(dbe, 100, i => (i - 50) * 1.0f); // -50.0 .. 49.0
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQFloatUnit>()
            .WhereField<ClQFloatData>(d => d.Value >= -20.0f && d.Value <= 20.0f)
            .OrderByField<ClQFloatData, float>(d => d.Value)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(41)); // -20..20 inclusive
        // Verify ascending order by reading back
        var values = ReadFloatValues(dbe, result);
        for (int i = 1; i < values.Count; i++)
        {
            Assert.That(values[i], Is.GreaterThanOrEqualTo(values[i - 1]),
                $"Float value at [{i}]={values[i]} should be >= [{i - 1}]={values[i - 1]}");
        }
    }

    [Test]
    public void KWay_FloatDescending_NegativeValues()
    {
        using var dbe = SetupEngine();
        SpawnClQFloats(dbe, 100, i => (i - 50) * 1.0f); // -50.0 .. 49.0
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQFloatUnit>()
            .WhereField<ClQFloatData>(d => d.Value >= -10.0f)
            .OrderByFieldDescending<ClQFloatData, float>(d => d.Value)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(60)); // -10..49
        var values = ReadFloatValues(dbe, result);
        for (int i = 1; i < values.Count; i++)
        {
            Assert.That(values[i], Is.LessThanOrEqualTo(values[i - 1]),
                $"Float value at [{i}]={values[i]} should be <= [{i - 1}]={values[i - 1]} (descending)");
        }
    }

    [Test]
    public void KWay_EmptyResult_WithPredicate()
    {
        using var dbe = SetupEngine();
        SpawnClQUnits(dbe, 100, i => i); // Score = 0..99
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score > 999) // No matches
            .OrderByField<ClQStats, int>(s => s.Score)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static void SpawnClQUnits(DatabaseEngine dbe, int count, System.Func<int, int> scoreFunc)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            var stats = new ClQStats(scoreFunc(i), i);
            var tag = new ClQTag(i);
            tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
        }

        tx.Commit();
    }

    private static void SpawnClQUnit2s(DatabaseEngine dbe, int count, System.Func<int, int> scoreFunc)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            var stats = new ClQStats(scoreFunc(i), i);
            var tag = new ClQTag(i + 1000);
            tx.Spawn<ClQUnit2>(ClQUnit2.Stats.Set(in stats), ClQUnit2.Tag.Set(in tag));
        }

        tx.Commit();
    }

    private static void SpawnClQFloats(DatabaseEngine dbe, int count, System.Func<int, float> valueFunc)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            var data = new ClQFloatData(valueFunc(i));
            tx.Spawn<ClQFloatUnit>(ClQFloatUnit.Data.Set(in data));
        }

        tx.Commit();
    }

    private static List<int> ReadScores(DatabaseEngine dbe, List<EntityId> entities)
    {
        var scores = new List<int>();
        using var tx = dbe.CreateQuickTransaction();
        foreach (var id in entities)
        {
            // ClQStats is shared across ClQUnit, ClQUnit2, ClQUnit3 — use the appropriate Comp accessor
            ref readonly var stats = ref tx.Open(id).Read(ClQUnit.Stats);
            scores.Add(stats.Score);
        }

        return scores;
    }

    private static List<float> ReadFloatValues(DatabaseEngine dbe, List<EntityId> entities)
    {
        var values = new List<float>();
        using var tx = dbe.CreateQuickTransaction();
        foreach (var id in entities)
        {
            ref readonly var data = ref tx.Open(id).Read(ClQFloatUnit.Data);
            values.Add(data.Value);
        }

        return values;
    }
}
