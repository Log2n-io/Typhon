using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Data;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Services;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Data;

/// <summary>
/// Pure unit tests for <see cref="AggregationService.Compute"/>. No HTTP layer — exercises all
/// 13 Tier 1 operators, multi-query ordering, range clamping, and validation errors.
/// </summary>
[TestFixture]
public sealed class AggregationServiceTests
{
    private static TickSummaryDto Tick(uint n, float dur, ushort waitUs = 0, byte intentClass = 0)
        => new(n, 0.0, dur, 0, 0f, "0", 0, 0, waitUs, intentClass, 0, 0);

    // Five ticks (1–5), DurationUs = 10, 20, 30, 40, 50 µs.
    private static readonly TickSummaryDto[] Ticks5 =
    [
        Tick(1, 10f), Tick(2, 20f), Tick(3, 30f), Tick(4, 40f), Tick(5, 50f),
    ];

    private static AggregationQueryDto Q(string op, string trackId = "tick/summary", string field = "durationUs",
        uint t0 = 1, uint t1 = 5)
        => new(trackId, field, op, [t0, t1]);

    private static double? Run(string op) =>
        AggregationService.Compute(Ticks5, [Q(op)])[0].Value;

    // ── All 13 Tier-1 operators on a 5-element full-range dataset ──

    [Test] public void Mean_FullRange() => Assert.That(Run("mean"), Is.EqualTo(30.0).Within(1e-9));
    [Test] public void Sum_FullRange() => Assert.That(Run("sum"), Is.EqualTo(150.0).Within(1e-9));
    [Test] public void Count_FullRange() => Assert.That(Run("count"), Is.EqualTo(5.0).Within(1e-9));
    [Test] public void Min_FullRange() => Assert.That(Run("min"), Is.EqualTo(10.0).Within(1e-9));
    [Test] public void Max_FullRange() => Assert.That(Run("max"), Is.EqualTo(50.0).Within(1e-9));

    [Test]
    public void Variance_FullRange()
    {
        // Population variance: mean=30, deviations²=400+100+0+100+400 → m2=1000, var=1000/5=200
        Assert.That(Run("variance"), Is.EqualTo(200.0).Within(1e-9));
    }

    [Test]
    public void Stddev_FullRange()
    {
        Assert.That(Run("stddev"), Is.EqualTo(Math.Sqrt(200.0)).Within(1e-9));
    }

    [Test] public void P50_FullRange() => Assert.That(Run("p50"), Is.EqualTo(30.0).Within(1e-9)); // floor(0.5*4)=2
    [Test] public void Median_EqualsP50() => Assert.That(Run("median"), Is.EqualTo(Run("p50")).Within(1e-9));
    [Test] public void P75_FullRange() => Assert.That(Run("p75"), Is.EqualTo(40.0).Within(1e-9)); // floor(0.75*4)=3
    [Test] public void P90_FullRange() => Assert.That(Run("p90"), Is.EqualTo(40.0).Within(1e-9)); // floor(0.9*4)=3
    [Test] public void P95_FullRange() => Assert.That(Run("p95"), Is.EqualTo(40.0).Within(1e-9)); // floor(0.95*4)=3
    [Test] public void P99_FullRange() => Assert.That(Run("p99"), Is.EqualTo(40.0).Within(1e-9)); // floor(0.99*4)=3

    // ── Edge cases ──

    [Test]
    public void EmptyRange_ReturnsNull()
    {
        var result = AggregationService.Compute(Ticks5, [Q("mean", t0: 100, t1: 200)]);
        Assert.That(result[0].Value, Is.Null, "range beyond max tick should return null, not throw");
    }

    [Test]
    public void RangeBelowMin_ReturnsNull()
    {
        var result = AggregationService.Compute(Ticks5, [Q("sum", t0: 0, t1: 0)]);
        Assert.That(result[0].Value, Is.Null, "range [0,0] has no matching ticks (first is tick 1)");
    }

    [Test]
    public void SingleElement_Count_IsOne()
    {
        var result = AggregationService.Compute(Ticks5, [Q("count", t0: 3, t1: 3)]);
        Assert.That(result[0].Value, Is.EqualTo(1.0).Within(1e-9));
    }

    [Test]
    public void SingleElement_Mean_EqualsSingleValue()
    {
        var result = AggregationService.Compute(Ticks5, [Q("mean", t0: 3, t1: 3)]);
        Assert.That(result[0].Value, Is.EqualTo(30.0).Within(1e-9));
    }

    [Test]
    public void SingleElement_Stddev_IsZero()
    {
        // Welford: n=1 → m2=0 → variance=0/1=0 → stddev=0
        var result = AggregationService.Compute(Ticks5, [Q("stddev", t0: 3, t1: 3)]);
        Assert.That(result[0].Value, Is.EqualTo(0.0).Within(1e-9));
    }

    [Test]
    public void SubRange_Correctly_Sliced()
    {
        // Ticks 2–4 → DurationUs 20,30,40 → sum=90, mean=30, min=20, max=40
        var queries = new[]
        {
            Q("sum",  t0: 2, t1: 4),
            Q("mean", t0: 2, t1: 4),
            Q("min",  t0: 2, t1: 4),
            Q("max",  t0: 2, t1: 4),
        };
        var results = AggregationService.Compute(Ticks5, queries);
        Assert.That(results[0].Value, Is.EqualTo(90.0).Within(1e-9));
        Assert.That(results[1].Value, Is.EqualTo(30.0).Within(1e-9));
        Assert.That(results[2].Value, Is.EqualTo(20.0).Within(1e-9));
        Assert.That(results[3].Value, Is.EqualTo(40.0).Within(1e-9));
    }

    [Test]
    public void MultipleQueries_ReturnedInOrder()
    {
        var queries = new[]
        {
            Q("min"),
            Q("max"),
            Q("count"),
        };
        var results = AggregationService.Compute(Ticks5, queries);
        Assert.That(results[0].Value, Is.EqualTo(10.0).Within(1e-9));
        Assert.That(results[1].Value, Is.EqualTo(50.0).Within(1e-9));
        Assert.That(results[2].Value, Is.EqualTo(5.0).Within(1e-9));
    }

    // ── metronome/wait track ──

    [Test]
    public void MetronomeWait_WaitUs_Mean()
    {
        var ticks = new[]
        {
            Tick(1, 0f, waitUs: 100),
            Tick(2, 0f, waitUs: 200),
            Tick(3, 0f, waitUs: 300),
        };
        var result = AggregationService.Compute(ticks, [Q("mean", trackId: "metronome/wait", field: "waitUs", t0: 1, t1: 3)]);
        Assert.That(result[0].Value, Is.EqualTo(200.0).Within(1e-9));
    }

    [Test]
    public void MetronomeWait_IntentClass_Max()
    {
        var ticks = new[]
        {
            Tick(1, 0f, intentClass: 0),
            Tick(2, 0f, intentClass: 2),
            Tick(3, 0f, intentClass: 1),
        };
        var result = AggregationService.Compute(ticks,
            [Q("max", trackId: "metronome/wait", field: "intentClass", t0: 1, t1: 3)]);
        Assert.That(result[0].Value, Is.EqualTo(2.0).Within(1e-9));
    }

    // ── Validation errors ──

    [Test]
    public void UnknownTrackId_ThrowsWorkbenchException()
    {
        var q = Q("mean", trackId: "bad/track");
        Assert.Throws<WorkbenchException>(() => AggregationService.Compute(Ticks5, [q]));
    }

    [Test]
    public void UnknownField_ThrowsWorkbenchException()
    {
        var q = Q("mean", field: "notAField");
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(Ticks5, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("unknown-field"));
    }

    [Test]
    public void UnknownOp_ThrowsWorkbenchException_EvenOnEmptyRange()
    {
        // Op validation must fire before range evaluation so an invalid op is never silently null.
        var q = new AggregationQueryDto("tick/summary", "durationUs", "garbage", [100, 200]);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(Ticks5, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("unknown-op"));
    }

    [Test]
    public void BadRange_t0GreaterThan_t1_ThrowsWorkbenchException()
    {
        var q = new AggregationQueryDto("tick/summary", "durationUs", "mean", [5, 1]);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(Ticks5, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("bad-range"));
    }

    [Test]
    public void NullRange_ThrowsWorkbenchException()
    {
        var q = new AggregationQueryDto("tick/summary", "durationUs", "mean", null);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(Ticks5, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("bad-range"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Tier 2 (#312) — histogram / topk / cdf
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void Histogram_FullRange_5BucketsOver_10_50()
    {
        // Values 10,20,30,40,50; range [10,50], 5 equal-width buckets of width 8.
        // Boundaries: 10–18, 18–26, 26–34, 34–42, 42–50. Last bucket inclusive of 50.
        // 10→[0]; 20→[1]; 30→[2]; 40→[3]; 50→[4].
        var q = new AggregationQueryDto("tick/summary", "durationUs", "histogram", [1, 5], Buckets: 5);
        var result = AggregationService.Compute(Ticks5, [q])[0];
        Assert.That(result.Histogram, Is.Not.Null);
        Assert.That(result.Histogram, Has.Length.EqualTo(5));
        Assert.That(result.Histogram[0].Count, Is.EqualTo(1));
        Assert.That(result.Histogram[1].Count, Is.EqualTo(1));
        Assert.That(result.Histogram[2].Count, Is.EqualTo(1));
        Assert.That(result.Histogram[3].Count, Is.EqualTo(1));
        Assert.That(result.Histogram[4].Count, Is.EqualTo(1), "last bucket must include max value");
        // Total preserved.
        var total = 0;
        for (var i = 0; i < result.Histogram.Length; i++) total += result.Histogram[i].Count;
        Assert.That(total, Is.EqualTo(5));
    }

    [Test]
    public void Histogram_AllSameValue_DegenerateRange()
    {
        var ticks = new[] { Tick(1, 100f), Tick(2, 100f), Tick(3, 100f) };
        var q = new AggregationQueryDto("tick/summary", "durationUs", "histogram", [1, 3], Buckets: 4);
        var result = AggregationService.Compute(ticks, [q])[0];
        Assert.That(result.Histogram, Has.Length.EqualTo(4));
        Assert.That(result.Histogram[0].Count, Is.EqualTo(3));
        Assert.That(result.Histogram[1].Count, Is.EqualTo(0));
        Assert.That(result.Histogram[2].Count, Is.EqualTo(0));
        Assert.That(result.Histogram[3].Count, Is.EqualTo(0));
    }

    [Test]
    public void Histogram_MissingBuckets_ThrowsMissingParam()
    {
        var q = new AggregationQueryDto("tick/summary", "durationUs", "histogram", [1, 5]);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(Ticks5, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("missing-param"));
    }

    [Test]
    public void TopK_ReturnsTopByValueDescending_WithTickNumbers()
    {
        var q = new AggregationQueryDto("tick/summary", "durationUs", "topk", [1, 5], N: 3);
        var result = AggregationService.Compute(Ticks5, [q])[0];
        Assert.That(result.TopK, Is.Not.Null);
        Assert.That(result.TopK, Has.Length.EqualTo(3));
        Assert.That(result.TopK[0].Value, Is.EqualTo(50.0).Within(1e-9));
        Assert.That(result.TopK[0].TickNumber, Is.EqualTo(5u));
        Assert.That(result.TopK[1].Value, Is.EqualTo(40.0).Within(1e-9));
        Assert.That(result.TopK[1].TickNumber, Is.EqualTo(4u));
        Assert.That(result.TopK[2].Value, Is.EqualTo(30.0).Within(1e-9));
        Assert.That(result.TopK[2].TickNumber, Is.EqualTo(3u));
    }

    [Test]
    public void TopK_NLargerThanCount_ReturnsAll()
    {
        var q = new AggregationQueryDto("tick/summary", "durationUs", "topk", [1, 5], N: 100);
        var result = AggregationService.Compute(Ticks5, [q])[0];
        Assert.That(result.TopK, Has.Length.EqualTo(5));
    }

    [Test]
    public void TopK_EmptyRange_ReturnsEmptyArray()
    {
        var q = new AggregationQueryDto("tick/summary", "durationUs", "topk", [100, 200], N: 5);
        var result = AggregationService.Compute(Ticks5, [q])[0];
        Assert.That(result.TopK, Is.Not.Null);
        Assert.That(result.TopK, Is.Empty);
    }

    [Test]
    public void TopK_MissingN_ThrowsMissingParam()
    {
        var q = new AggregationQueryDto("tick/summary", "durationUs", "topk", [1, 5]);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(Ticks5, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("missing-param"));
    }

    [Test]
    public void Cdf_5Samples_QuantilesMonotonic()
    {
        var q = new AggregationQueryDto("tick/summary", "durationUs", "cdf", [1, 5], Samples: 5);
        var result = AggregationService.Compute(Ticks5, [q])[0];
        Assert.That(result.Cdf, Is.Not.Null);
        Assert.That(result.Cdf, Has.Length.EqualTo(5));
        // Quantiles: 0, 0.25, 0.5, 0.75, 1.0 over [10,20,30,40,50] indices 0..4
        // round(q*4) → indices 0,1,2,3,4 → values 10,20,30,40,50.
        Assert.That(result.Cdf[0].Quantile, Is.EqualTo(0.0).Within(1e-9));
        Assert.That(result.Cdf[0].Value, Is.EqualTo(10.0).Within(1e-9));
        Assert.That(result.Cdf[2].Quantile, Is.EqualTo(0.5).Within(1e-9));
        Assert.That(result.Cdf[2].Value, Is.EqualTo(30.0).Within(1e-9));
        Assert.That(result.Cdf[4].Quantile, Is.EqualTo(1.0).Within(1e-9));
        Assert.That(result.Cdf[4].Value, Is.EqualTo(50.0).Within(1e-9));
        // Values are non-decreasing (sorted set).
        for (var i = 1; i < result.Cdf.Length; i++)
        {
            Assert.That(result.Cdf[i].Value, Is.GreaterThanOrEqualTo(result.Cdf[i - 1].Value));
        }
    }

    [Test]
    public void Cdf_MissingSamples_ThrowsMissingParam()
    {
        var q = new AggregationQueryDto("tick/summary", "durationUs", "cdf", [1, 5]);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(Ticks5, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("missing-param"));
    }

    [Test]
    public void Cdf_SamplesLessThan2_ThrowsMissingParam()
    {
        var q = new AggregationQueryDto("tick/summary", "durationUs", "cdf", [1, 5], Samples: 1);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(Ticks5, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("missing-param"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // v2 track families — system/<name>, queue/<name>, posttick/<phase>
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void SystemTrack_DurationUs_Mean()
    {
        var meta = TestMetadata.WithSystemRows("Physics", sysIdx: 7,
            (tick: 1u, duration: 100f),
            (tick: 2u, duration: 200f),
            (tick: 3u, duration: 300f));
        var q = new AggregationQueryDto("system/Physics", "durationUs", "mean", [1, 3]);
        var result = AggregationService.Compute(meta, [q])[0];
        Assert.That(result.Value, Is.EqualTo(200.0).Within(1e-9));
    }

    [Test]
    public void SystemTrack_FiltersBySystemIndex_IgnoresOtherSystems()
    {
        // Two systems share the metadata; query for "Physics" must skip "AI" rows.
        var systems = new[]
        {
            MakeSystem("Physics", 7),
            MakeSystem("AI", 8),
        };
        var rows = new[]
        {
            MakeSystemRow(tick: 1, sysIdx: 7, duration: 100f),
            MakeSystemRow(tick: 1, sysIdx: 8, duration: 9999f), // AI tick 1 — must NOT be aggregated.
            MakeSystemRow(tick: 2, sysIdx: 7, duration: 200f),
            MakeSystemRow(tick: 2, sysIdx: 8, duration: 9999f),
        };
        var meta = TestMetadata.Build(systems: systems, systemRows: rows);

        var q = new AggregationQueryDto("system/Physics", "durationUs", "sum", [1, 2]);
        var result = AggregationService.Compute(meta, [q])[0];
        Assert.That(result.Value, Is.EqualTo(300.0).Within(1e-9), "sum must include only Physics rows");
    }

    [Test]
    public void SystemTrack_UnknownSystem_ThrowsUnknownSystem()
    {
        var meta = TestMetadata.WithSystemRows("Physics", 0);
        var q = new AggregationQueryDto("system/Bogus", "durationUs", "mean", [1, 5]);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(meta, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("unknown-system"));
    }

    [Test]
    public void SystemTrack_UnknownField_ThrowsUnknownField()
    {
        var meta = TestMetadata.WithSystemRows("Physics", 0);
        var q = new AggregationQueryDto("system/Physics", "garbage", "mean", [1, 5]);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(meta, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("unknown-field"));
    }

    [Test]
    public void QueueTrack_PeakDepth_Max()
    {
        var meta = TestMetadata.WithQueueRows("Damage", qid: 3,
            (tick: 1u, peak: 10u),
            (tick: 2u, peak: 50u),
            (tick: 3u, peak: 30u));
        var q = new AggregationQueryDto("queue/Damage", "peakDepth", "max", [1, 3]);
        var result = AggregationService.Compute(meta, [q])[0];
        Assert.That(result.Value, Is.EqualTo(50.0).Within(1e-9));
    }

    [Test]
    public void QueueTrack_UnknownQueue_ThrowsUnknownQueue()
    {
        var meta = TestMetadata.WithQueueRows("Damage", 0);
        var q = new AggregationQueryDto("queue/Bogus", "peakDepth", "mean", [1, 5]);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(meta, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("unknown-queue"));
    }

    [Test]
    public void PostTickTrack_WalFlush_DurationUs_Mean()
    {
        var meta = TestMetadata.WithPostTickRows(
            (tick: 1u, walFlushUs: 10f),
            (tick: 2u, walFlushUs: 20f),
            (tick: 3u, walFlushUs: 30f));
        var q = new AggregationQueryDto("posttick/walFlush", "durationUs", "mean", [1, 3]);
        var result = AggregationService.Compute(meta, [q])[0];
        Assert.That(result.Value, Is.EqualTo(20.0).Within(1e-9));
    }

    [Test]
    public void PostTickTrack_UnknownPhase_ThrowsUnknownPhase()
    {
        var meta = TestMetadata.WithPostTickRows();
        var q = new AggregationQueryDto("posttick/garbage", "durationUs", "mean", [1, 5]);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(meta, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("unknown-posttick-phase"));
    }

    [Test]
    public void PostTickTrack_AllPhases_Resolve()
    {
        var meta = TestMetadata.Build(postRows: new[]
        {
            new PostTickSummary
            {
                TickNumber = 1, _reserved = 0,
                WriteTickFenceUs = 1f, WalFlushUs = 2f, SubscriptionOutputUs = 3f,
                TierIndexRebuildUs = 4f, DormancySweepUs = 5f, TierBudgetUs = 6f,
            },
        });
        var queries = new[]
        {
            new AggregationQueryDto("posttick/writeTickFence",     "durationUs", "sum", [1, 1]),
            new AggregationQueryDto("posttick/walFlush",           "durationUs", "sum", [1, 1]),
            new AggregationQueryDto("posttick/subscriptionOutput", "durationUs", "sum", [1, 1]),
            new AggregationQueryDto("posttick/tierIndexRebuild",   "durationUs", "sum", [1, 1]),
            new AggregationQueryDto("posttick/dormancySweep",      "durationUs", "sum", [1, 1]),
            new AggregationQueryDto("posttick/tierBudget",         "durationUs", "sum", [1, 1]),
        };
        var results = AggregationService.Compute(meta, queries);
        Assert.That(results[0].Value, Is.EqualTo(1.0).Within(1e-9));
        Assert.That(results[1].Value, Is.EqualTo(2.0).Within(1e-9));
        Assert.That(results[2].Value, Is.EqualTo(3.0).Within(1e-9));
        Assert.That(results[3].Value, Is.EqualTo(4.0).Within(1e-9));
        Assert.That(results[4].Value, Is.EqualTo(5.0).Within(1e-9));
        Assert.That(results[5].Value, Is.EqualTo(6.0).Within(1e-9));
    }

    // ────────────────────────────────────────────────────────────────────────
    // v3 track families (#327) — Workbench Data Flow module
    // ────────────────────────────────────────────────────────────────────────

    private static ProfilerMetadataDto MakeDataFlowFixture()
    {
        // Two systems, two archetypes, two ticks. Physics touches Ant (entities 100/200), AI touches Ant + Food per tick.
        return TestMetadata.WithSystemArchetypeRows(
            systems: [("Physics", 1), ("AI", 2)],
            archetypes:
            [
                ("Ant",  100, [..new[] { "Game.Position", "Game.Velocity" }]),  // → Spatial
                ("Food", 101, [..new[] { "Game.Health" }]),                     // → Combat
            ],
            // tick 10
            (10u, 1, 100, 100u, 4u),    // Physics × Ant
            (10u, 2, 100, 80u, 3u),     // AI × Ant
            (10u, 2, 101, 50u, 2u),     // AI × Food
            // tick 11
            (11u, 1, 100, 200u, 6u),    // Physics × Ant
            (11u, 2, 100, 90u, 3u),     // AI × Ant
            (11u, 2, 101, 60u, 2u));    // AI × Food
    }

    [Test]
    public void SystemArchetypeTrack_Sum_DirectRowSumming()
    {
        var meta = MakeDataFlowFixture();
        var q = new AggregationQueryDto("system-archetype/Physics/Ant", "entitiesProcessed", "sum", [10, 11]);
        var result = AggregationService.Compute(meta, [q])[0];
        // 100 + 200 = 300
        Assert.That(result.Value, Is.EqualTo(300.0).Within(1e-9));
    }

    [Test]
    public void ArchetypeTrack_RolledUp_AcrossAllSystems()
    {
        var meta = MakeDataFlowFixture();
        // Ant is touched by Physics + AI on each tick. Tick 10: 100+80=180; Tick 11: 200+90=290.
        var sumQ = new AggregationQueryDto("archetype/Ant", "entitiesProcessed", "sum", [10, 11]);
        var meanQ = new AggregationQueryDto("archetype/Ant", "entitiesProcessed", "mean", [10, 11]);
        var results = AggregationService.Compute(meta, [sumQ, meanQ]);
        Assert.That(results[0].Value, Is.EqualTo(470.0).Within(1e-9));   // 180 + 290
        Assert.That(results[1].Value, Is.EqualTo(235.0).Within(1e-9));   // 470 / 2 ticks
    }

    [Test]
    public void ComponentFamilyTrack_RollsUp_FamilyMemberArchetypes()
    {
        var meta = MakeDataFlowFixture();
        // Spatial family contains Ant (Position, Velocity → Spatial). Food has Health → Combat.
        // Spatial sum = Ant rollup = 470 (180 + 290).
        var spatial = new AggregationQueryDto("component-family/Spatial", "entitiesProcessed", "sum", [10, 11]);
        // Combat sum = Food rollup. AI × Food: tick 10 = 50, tick 11 = 60. Sum = 110.
        var combat = new AggregationQueryDto("component-family/Combat", "entitiesProcessed", "sum", [10, 11]);
        var results = AggregationService.Compute(meta, [spatial, combat]);
        Assert.That(results[0].Value, Is.EqualTo(470.0).Within(1e-9));
        Assert.That(results[1].Value, Is.EqualTo(110.0).Within(1e-9));
    }

    [Test]
    public void ArchetypeTrack_ChunkCountField()
    {
        var meta = MakeDataFlowFixture();
        // Ant chunks: tick 10: 4+3=7; tick 11: 6+3=9. Max = 9.
        var q = new AggregationQueryDto("archetype/Ant", "chunkCount", "max", [10, 11]);
        Assert.That(AggregationService.Compute(meta, [q])[0].Value, Is.EqualTo(9.0).Within(1e-9));
    }

    [Test]
    public void SystemArchetypeTrack_UnknownSystem_ThrowsValidation()
    {
        var meta = MakeDataFlowFixture();
        var q = new AggregationQueryDto("system-archetype/Unknown/Ant", "entitiesProcessed", "sum", [10, 11]);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(meta, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("unknown-system"));
    }

    [Test]
    public void SystemArchetypeTrack_UnknownArchetype_ThrowsValidation()
    {
        var meta = MakeDataFlowFixture();
        var q = new AggregationQueryDto("system-archetype/Physics/Bogus", "entitiesProcessed", "sum", [10, 11]);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(meta, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("unknown-archetype"));
    }

    [Test]
    public void SystemArchetypeTrack_BadTrackId_MissingSlash_ThrowsValidation()
    {
        var meta = MakeDataFlowFixture();
        var q = new AggregationQueryDto("system-archetype/Physics", "entitiesProcessed", "sum", [10, 11]);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(meta, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("bad-trackid"));
    }

    [Test]
    public void ArchetypeTrack_UnknownField_ThrowsValidation()
    {
        var meta = MakeDataFlowFixture();
        var q = new AggregationQueryDto("archetype/Ant", "durationUs", "mean", [10, 11]);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(meta, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("unknown-field"));
    }

    [Test]
    public void V2Track_OnLegacyOverload_ThrowsUnknownTrack()
    {
        // Legacy Compute(TickSummaryDto[], …) has no metadata to resolve v2 tracks.
        var q = new AggregationQueryDto("system/Physics", "durationUs", "mean", [1, 5]);
        var ex = Assert.Throws<WorkbenchException>(() => AggregationService.Compute(Ticks5, [q]));
        Assert.That(ex.ErrorCode, Is.EqualTo("unknown-track"));
    }

    [Test]
    public void V2Track_TopK_ReturnsTickNumbersFromV2Rows()
    {
        // Cross-feature: topk over a per-system track must report the correct system-row tick numbers.
        var meta = TestMetadata.WithSystemRows("Physics", sysIdx: 7,
            (tick: 10u, duration: 100f),
            (tick: 11u, duration: 50f),
            (tick: 12u, duration: 200f),
            (tick: 13u, duration: 75f));
        var q = new AggregationQueryDto("system/Physics", "durationUs", "topk", [10, 13], N: 2);
        var result = AggregationService.Compute(meta, [q])[0];
        Assert.That(result.TopK, Has.Length.EqualTo(2));
        Assert.That(result.TopK[0].TickNumber, Is.EqualTo(12u));
        Assert.That(result.TopK[0].Value, Is.EqualTo(200.0).Within(1e-9));
        Assert.That(result.TopK[1].TickNumber, Is.EqualTo(10u));
        Assert.That(result.TopK[1].Value, Is.EqualTo(100.0).Within(1e-9));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Test fixtures — construct minimal ProfilerMetadataDto instances.
    // ────────────────────────────────────────────────────────────────────────

    private static SystemDefinitionDto MakeSystem(string name, ushort idx) => new(
        idx, name, 0, 0, false, 0,
        System.Array.Empty<ushort>(), System.Array.Empty<ushort>(),
        "", false,
        System.Array.Empty<string>(), System.Array.Empty<string>(),
        System.Array.Empty<string>(), System.Array.Empty<string>(),
        System.Array.Empty<string>(), System.Array.Empty<string>(),
        System.Array.Empty<string>(), System.Array.Empty<string>(),
        System.Array.Empty<string>(), System.Array.Empty<string>(),
        System.Array.Empty<string>(), System.Array.Empty<string>());

    private static SystemTickSummary MakeSystemRow(uint tick, ushort sysIdx, float duration) => new()
    {
        TickNumber = tick, SystemIndex = sysIdx, SkipReasonCode = 0, Flags = 0,
        StartUs = 0, EndUs = 0, ReadyUs = 0, DurationUs = duration,
        EntitiesProcessed = 0, WorkersTouched = 0, ChunksProcessed = 0, _reserved = 0,
    };

    private static class TestMetadata
    {
        public static ProfilerMetadataDto WithSystemRows(string name, ushort sysIdx, params (uint tick, float duration)[] rows)
        {
            var sysRows = new SystemTickSummary[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                sysRows[i] = MakeSystemRow(rows[i].tick, sysIdx, rows[i].duration);
            }
            return Build(systems: [MakeSystem(name, sysIdx)], systemRows: sysRows);
        }

        public static ProfilerMetadataDto WithQueueRows(string name, ushort qid, params (uint tick, uint peak)[] rows)
        {
            var qRows = new QueueTickSummary[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                qRows[i] = new QueueTickSummary
                {
                    TickNumber = rows[i].tick, QueueId = qid, _reserved = 0,
                    PeakDepth = rows[i].peak, EndOfTickDepth = 0, OverflowCount = 0,
                    Produced = 0, Consumed = 0,
                };
            }
            return Build(queueRows: qRows, queueIdToName: new Dictionary<ushort, string> { [qid] = name });
        }

        // Workbench Data Flow module (#327) helpers.
        public static ProfilerMetadataDto WithSystemArchetypeRows(
            (string sysName, ushort sysIdx)[] systems,
            (string label, ushort archId, string[] componentNames)[] archetypes,
            params (uint tick, ushort sysIdx, ushort archId, uint entities, uint chunks)[] rows)
        {
            var sysDefs = new SystemDefinitionDto[systems.Length];
            for (var i = 0; i < systems.Length; i++)
            {
                sysDefs[i] = MakeSystem(systems[i].sysName, systems[i].sysIdx);
            }

            var archDefs = new ArchetypeDto[archetypes.Length];
            for (var i = 0; i < archetypes.Length; i++)
            {
                var (label, archId, comps) = archetypes[i];
                archDefs[i] = new ArchetypeDto(archId, label, label, 1, comps);
            }

            var satRows = new SystemArchetypeTouchSummary[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                satRows[i] = new SystemArchetypeTouchSummary
                {
                    TickNumber = rows[i].tick,
                    SystemIndex = rows[i].sysIdx,
                    ArchetypeId = rows[i].archId,
                    EntityCount = rows[i].entities,
                    ChunkCount = rows[i].chunks,
                };
            }

            return Build(systems: sysDefs, archetypes: archDefs, archetypeTouches: satRows);
        }

        public static ProfilerMetadataDto WithPostTickRows(params (uint tick, float walFlushUs)[] rows)
        {
            var pRows = new PostTickSummary[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                pRows[i] = new PostTickSummary
                {
                    TickNumber = rows[i].tick, _reserved = 0,
                    WriteTickFenceUs = 0, WalFlushUs = rows[i].walFlushUs, SubscriptionOutputUs = 0,
                    TierIndexRebuildUs = 0, DormancySweepUs = 0, TierBudgetUs = 0,
                };
            }
            return Build(postRows: pRows);
        }

        public static ProfilerMetadataDto Build(
            SystemDefinitionDto[] systems = null,
            SystemTickSummary[] systemRows = null,
            QueueTickSummary[] queueRows = null,
            PostTickSummary[] postRows = null,
            Dictionary<ushort, string> queueIdToName = null,
            SystemArchetypeTouchSummary[] archetypeTouches = null,
            ArchetypeDto[] archetypes = null)
        {
            return new ProfilerMetadataDto(
                Fingerprint: "TEST",
                Header: new ProfilerHeaderDto(0, 1, 0f, 0, 0, 0, 0, 0, 0),
                Systems: systems ?? System.Array.Empty<SystemDefinitionDto>(),
                Archetypes: archetypes ?? System.Array.Empty<ArchetypeDto>(),
                ComponentTypes: System.Array.Empty<ComponentTypeDto>(),
                SpanNames: new Dictionary<int, string>(),
                GlobalMetrics: new GlobalMetricsDto(0, 0, 0, 0, 0, 0, 0, System.Array.Empty<SystemAggregateDto>()),
                TickSummaries: System.Array.Empty<TickSummaryDto>(),
                ChunkManifest: System.Array.Empty<ChunkManifestEntryDto>(),
                GcSuspensions: System.Array.Empty<GcSuspensionDto>(),
                Phases: System.Array.Empty<string>(),
                SystemTickSummaries: systemRows ?? System.Array.Empty<SystemTickSummary>(),
                QueueTickSummaries: queueRows ?? System.Array.Empty<QueueTickSummary>(),
                PostTickSummaries: postRows ?? System.Array.Empty<PostTickSummary>(),
                QueueIdToName: queueIdToName ?? new Dictionary<ushort, string>(),
                SystemArchetypeTouches: archetypeTouches ?? System.Array.Empty<SystemArchetypeTouchSummary>());
        }
    }
}
