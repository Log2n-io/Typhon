using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Profiler.Events;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Services;

namespace Typhon.Workbench.Tests.Services;

/// <summary>
/// Unit tests for <see cref="QueryCatalogBuilder"/>. Exercises the per-event business logic via the
/// <c>BuildFromEventsForTest</c> entry point with synthetic event sequences — no chunk I/O,
/// no LZ4 compression, no on-disk traces.
/// </summary>
[TestFixture]
public sealed class QueryCatalogBuilderTests
{
    private const byte KindView = 0;
    private const byte KindEcs = 1;

    // ── Helpers ──

    private static QueryDefinitionDescribeEventDto Describe(
        byte kind,
        uint localId,
        ushort fileId = 0,
        int line = 0,
        ushort methodId = 0,
        params (ushort fieldIdx, byte op)[] evaluators)
    {
        var evs = new QueryFieldEvaluatorShapeDto[evaluators.Length];
        for (var i = 0; i < evaluators.Length; i++)
        {
            evs[i] = new QueryFieldEvaluatorShapeDto(evaluators[i].fieldIdx, evaluators[i].op);
        }
        return new QueryDefinitionDescribeEventDto
        {
            ThreadSlot = 0,
            TickNumber = 1,
            TimestampUs = 1.0,
            Kind = kind,
            LocalId = localId,
            TargetComponentType = 100,
            PrimaryIndexFieldIdx = -1,
            SortFieldIdx = -1,
            SortDescending = false,
            DefinitionSourceFileId = fileId,
            DefinitionSourceLine = line,
            DefinitionSourceMethodId = methodId,
            Evaluators = evs,
            FieldDependencies = [],
        };
    }

    private static QueryPlanEventDto QueryPlanSpan(
        byte kind,
        uint localId,
        long spanId,
        long parentSpanId = 0,
        double durationUs = 10.0,
        int tickNumber = 1)
    {
        return new QueryPlanEventDto
        {
            ThreadSlot = 0,
            TickNumber = tickNumber,
            TimestampUs = 1.0,
            DurationUs = durationUs,
            SpanId = spanId.ToString(),
            ParentSpanId = parentSpanId.ToString(),
            TraceIdHi = "0",
            TraceIdLo = "0",
            EvaluatorCount = 0,
            IndexFieldIdx = 0,
            RangeMin = 0,
            RangeMax = 0,
            QueryInstanceKind = kind,
            QueryInstanceLocalId = localId,
            ExecutionSourceFileId = 0,
            ExecutionSourceLine = 0,
            ExecutionSourceMethodId = 0,
        };
    }

    private static QueryExecuteFilterEventDto FilterSpan(long spanId, long parentSpanId, int rejected = 5)
    {
        return new QueryExecuteFilterEventDto
        {
            ThreadSlot = 0,
            TickNumber = 1,
            TimestampUs = 2.0,
            DurationUs = 1.0,
            SpanId = spanId.ToString(),
            ParentSpanId = parentSpanId.ToString(),
            TraceIdHi = "0",
            TraceIdLo = "0",
            FilterCount = 1,
            RejectedCount = rejected,
        };
    }

    private static QueryExecuteIterateEventDto IterateSpan(long spanId, long parentSpanId, int entryCount = 100)
    {
        return new QueryExecuteIterateEventDto
        {
            ThreadSlot = 0,
            TickNumber = 1,
            TimestampUs = 2.5,
            DurationUs = 0.5,
            SpanId = spanId.ToString(),
            ParentSpanId = parentSpanId.ToString(),
            TraceIdHi = "0",
            TraceIdLo = "0",
            ChunkCount = 1,
            EntryCount = entryCount,
        };
    }

    private static QueryArgsEventDto Args(params long[] thresholds)
    {
        return new QueryArgsEventDto
        {
            ThreadSlot = 0,
            TickNumber = 1,
            TimestampUs = 1.5,
            Thresholds = thresholds,
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // Tests
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void EmptyEventStream_ProducesEmptyCatalog()
    {
        var data = QueryCatalogBuilder.BuildFromEventsForTest([]);
        Assert.That(data.Definitions, Is.Empty);
        Assert.That(data.Executions, Is.Empty);
    }

    [Test]
    public void SingleDefinition_SingleExecution_BuildsCorrectly()
    {
        var events = new List<TraceEventDto>
        {
            Describe(KindView, 42, evaluators: (5, 3)),
            QueryPlanSpan(KindView, 42, spanId: 1000, durationUs: 10.0),
            IterateSpan(spanId: 1001, parentSpanId: 1000, entryCount: 50),
            FilterSpan(spanId: 1002, parentSpanId: 1000, rejected: 3),
        };

        var data = QueryCatalogBuilder.BuildFromEventsForTest(events);

        Assert.That(data.Definitions, Has.Length.EqualTo(1));
        Assert.That(data.Definitions[0].InstanceId.Kind, Is.EqualTo(KindView));
        Assert.That(data.Definitions[0].InstanceId.LocalId, Is.EqualTo((uint)42));
        Assert.That(data.Definitions[0].Evaluators, Has.Length.EqualTo(1));
        Assert.That(data.Definitions[0].Aggregate.ExecutionCount, Is.EqualTo(1));

        Assert.That(data.Executions, Has.Length.EqualTo(1));
        Assert.That(data.Executions[0].DefinitionId.Kind, Is.EqualTo(KindView));
        Assert.That(data.Executions[0].DefinitionId.LocalId, Is.EqualTo((uint)42));
        Assert.That(data.Executions[0].Phases, Has.Length.EqualTo(2), "Iterate + Filter should both be captured as phases");
    }

    [Test]
    public void DuplicateDescribeForSameInstance_ProducesOneDefinitionRow()
    {
        // P2's producer-side dedup tracker SHOULD prevent duplicates, but if a duplicate sneaks through
        // (e.g., across a TyphonProfiler.Stop/Start boundary where the tracker is reset), the builder
        // must still produce ONE definition row, not two. Regression case.
        var events = new List<TraceEventDto>
        {
            Describe(KindView, 7, evaluators: (1, 0)),
            Describe(KindView, 7, evaluators: (1, 0)),
            Describe(KindView, 7, evaluators: (1, 0)),
        };

        var data = QueryCatalogBuilder.BuildFromEventsForTest(events);
        Assert.That(data.Definitions, Has.Length.EqualTo(1));
    }

    [Test]
    public void MultipleDefinitions_MultipleExecutionsPerDefinition_AccumulatesCounts()
    {
        var events = new List<TraceEventDto>
        {
            Describe(KindView, 1, evaluators: (1, 0)),
            Describe(KindEcs, 2, evaluators: (2, 1)),
            // 3 executions of def 1
            QueryPlanSpan(KindView, 1, spanId: 100, durationUs: 5),
            QueryPlanSpan(KindView, 1, spanId: 101, durationUs: 10),
            QueryPlanSpan(KindView, 1, spanId: 102, durationUs: 15),
            // 2 executions of def 2
            QueryPlanSpan(KindEcs, 2, spanId: 200, durationUs: 20),
            QueryPlanSpan(KindEcs, 2, spanId: 201, durationUs: 30),
        };

        var data = QueryCatalogBuilder.BuildFromEventsForTest(events);

        Assert.That(data.Definitions, Has.Length.EqualTo(2));
        Assert.That(data.Executions, Has.Length.EqualTo(5));

        var def1 = FindDefinition(data, KindView, 1);
        Assert.That(def1.Aggregate.ExecutionCount, Is.EqualTo(3));
        Assert.That(def1.Aggregate.TotalWallNs, Is.EqualTo(30_000)); // 5+10+15 µs → 30k ns

        var def2 = FindDefinition(data, KindEcs, 2);
        Assert.That(def2.Aggregate.ExecutionCount, Is.EqualTo(2));
        Assert.That(def2.Aggregate.TotalWallNs, Is.EqualTo(50_000)); // 20+30 µs → 50k ns
    }

    [Test]
    public void QueryArgsEvent_AttachesToMostRecentExecution()
    {
        var events = new List<TraceEventDto>
        {
            Describe(KindView, 5, 0, 0, 0, (1, 0), (2, 1)),
            QueryPlanSpan(KindView, 5, spanId: 1000),
            Args(42L, 100L),
        };

        var data = QueryCatalogBuilder.BuildFromEventsForTest(events);
        Assert.That(data.Executions, Has.Length.EqualTo(1));
        Assert.That(data.Executions[0].Args, Is.EqualTo(new long[] { 42, 100 }));
    }

    [Test]
    public void ExecutionWithoutDescribe_IsSkipped()
    {
        // v8 traces have QueryPlan spans but no QueryInstanceKind/LocalId on them, AND no
        // QueryDefinitionDescribe events. Builder must not crash and must not surface phantom executions.
        var events = new List<TraceEventDto>
        {
            // QueryPlan span with kind/localId = 0 (the v8 sentinel) → builder should skip.
            QueryPlanSpan(kind: 0, localId: 0, spanId: 1000),
        };

        var data = QueryCatalogBuilder.BuildFromEventsForTest(events);
        Assert.That(data.Definitions, Is.Empty);
        Assert.That(data.Executions, Is.Empty);
    }

    [Test]
    public void SourceLocationStrings_AreResolved()
    {
        var strings = new string[] { string.Empty, "/_/src/SystemX.cs", "Configure" };
        var events = new List<TraceEventDto>
        {
            Describe(KindView, 1, fileId: 1, line: 42, methodId: 2, evaluators: (5, 3)),
        };

        var data = QueryCatalogBuilder.BuildFromEventsForTest(events, strings);
        Assert.That(data.Definitions, Has.Length.EqualTo(1));
        Assert.That(data.Definitions[0].UserSource.File, Is.EqualTo("/_/src/SystemX.cs"));
        Assert.That(data.Definitions[0].UserSource.Line, Is.EqualTo(42));
        Assert.That(data.Definitions[0].UserSource.Method, Is.EqualTo("Configure"));
    }

    [Test]
    public void SourceLocationStrings_FallBackGracefully_WhenIdsOutOfRange()
    {
        var strings = new string[] { string.Empty }; // only sentinel
        var events = new List<TraceEventDto>
        {
            Describe(KindView, 1, fileId: 99, line: 42, methodId: 88, evaluators: (5, 3)),
        };

        var data = QueryCatalogBuilder.BuildFromEventsForTest(events, strings);
        Assert.That(data.Definitions[0].UserSource.File, Is.EqualTo(string.Empty));
        Assert.That(data.Definitions[0].UserSource.Method, Is.EqualTo(string.Empty));
        Assert.That(data.Definitions[0].UserSource.Line, Is.EqualTo(42), "Line is the raw value — only string IDs require resolution");
    }

    [Test]
    public void Percentiles_ComputedCorrectly()
    {
        // 5 executions with wall times 10, 20, 30, 40, 50 µs → wallNs values 10000…50000.
        // p50 (n=5, idx=floor(4*0.5)=2) → sorted[2]=30000.
        // p95 (idx=floor(4*0.95)=3) → sorted[3]=40000.
        // p99 (idx=floor(4*0.99)=3) → sorted[3]=40000.
        var events = new List<TraceEventDto>
        {
            Describe(KindView, 1),
            QueryPlanSpan(KindView, 1, spanId: 1, durationUs: 10),
            QueryPlanSpan(KindView, 1, spanId: 2, durationUs: 20),
            QueryPlanSpan(KindView, 1, spanId: 3, durationUs: 30),
            QueryPlanSpan(KindView, 1, spanId: 4, durationUs: 40),
            QueryPlanSpan(KindView, 1, spanId: 5, durationUs: 50),
        };

        var data = QueryCatalogBuilder.BuildFromEventsForTest(events);
        var stats = data.Definitions[0].Aggregate;
        Assert.That(stats.ExecutionCount, Is.EqualTo(5));
        Assert.That(stats.AvgWallNs, Is.EqualTo(30_000));
        Assert.That(stats.P50WallNs, Is.EqualTo(30_000));
        Assert.That(stats.P95WallNs, Is.EqualTo(40_000));
        Assert.That(stats.P99WallNs, Is.EqualTo(40_000));
    }

    [Test]
    public void Selectivity_ComputedFromIterateAndCount()
    {
        // Scan 100 rows, return 25 → selectivity = 1 - (25/100) = 0.75
        var events = new List<TraceEventDto>
        {
            Describe(KindView, 1),
            QueryPlanSpan(KindView, 1, spanId: 100),
            IterateSpan(spanId: 101, parentSpanId: 100, entryCount: 100),
            new QueryCountEventDto
            {
                ThreadSlot = 0,
                TickNumber = 1,
                TimestampUs = 3.0,
                DurationUs = 0.1,
                SpanId = "102",
                ParentSpanId = "100",
                TraceIdHi = "0",
                TraceIdLo = "0",
                ResultCount = 25,
            },
        };

        var data = QueryCatalogBuilder.BuildFromEventsForTest(events);
        Assert.That(data.Definitions[0].Aggregate.TotalRowsScanned, Is.EqualTo(100));
        Assert.That(data.Definitions[0].Aggregate.TotalRowsReturned, Is.EqualTo(25));
        Assert.That(data.Definitions[0].Aggregate.AvgSelectivity, Is.EqualTo(0.75).Within(1e-9));
    }

    // ── Helpers ──

    private static QueryDefinitionDto FindDefinition(QueryCatalogData data, byte kind, uint localId)
    {
        foreach (var d in data.Definitions)
        {
            if (d.InstanceId.Kind == kind && d.InstanceId.LocalId == localId) return d;
        }
        return null;
    }
}
