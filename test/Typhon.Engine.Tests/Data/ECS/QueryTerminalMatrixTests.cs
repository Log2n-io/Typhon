using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Every one-shot query terminal, crossed with every storage shape and both index flavours, checked against a closed-form model and against each other
/// (#704 T2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this shape exists to catch.</b> #590/#592 were a terminal × predicate-shape interaction: <c>Execute()</c> returned a strict subset,
/// <c>Count()</c> under-reported and <c>Any()</c> could answer false with matches present, while <c>ToView()</c> — the one path the fixture happened to
/// exercise — was correct throughout. Three cells of a 2×2 tested, the bug in the fourth, and nothing threw. The terminal is an axis; it was being treated as
/// an implementation detail.
/// </para>
/// <para>
/// <b>Why the terminal is crossed in rather than added to <see cref="Cell"/>.</b> It is fixture-local — a schema-migration fixture has no terminal — so it
/// enters through <see cref="EngineAxes.PairwiseWith{T}"/>, which folds it into the same greedy covering pass. A naive nested loop would multiply the case
/// count by four; folding leaves it bounded by the two largest axes.
/// </para>
/// <para>
/// <b>Each index flavour is queried through the field it actually indexes.</b> <c>WhereField</c> rejects a predicate on a non-indexed field, so the unique
/// cells are queried on <c>Key</c> and the AllowMultiple cells on <c>Bucket</c>. That is not a workaround: a point lookup on a unique tree and a lookup into
/// a multi-value element buffer are different plan paths, so a terminal can be right on one and wrong on the other — which is the 2×2 the bugs lived in.
/// <c>IndexShape.None</c> cells are excluded because they have no indexed field to query at all.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class QueryTerminalMatrixTests : TestBase<QueryTerminalMatrixTests>
{
    private const int EntityCount = 40;

    private static readonly QueryTerminal[] Terminals =
        [QueryTerminal.Execute, QueryTerminal.Count, QueryTerminal.Any, QueryTerminal.ToView];

    private long _tick;

    [SetUp]
    public void ResetTick() => _tick = 0;

    /// <summary>AllowMultiple cells × terminal — the multi-value element-buffer plan path.</summary>
    public static IEnumerable<TestCaseData> MultiCases() =>
        EngineAxes.PairwiseWith(Terminals,
            (c, _) => AxisArchetypes.SupportsBase(c) && c.Reopen == ReopenKind.None && c.Index == IndexShape.AllowMultiple);

    /// <summary>Unique cells × terminal — the point-lookup plan path.</summary>
    public static IEnumerable<TestCaseData> UniqueCases() =>
        EngineAxes.PairwiseWith(Terminals,
            (c, _) => AxisArchetypes.SupportsBase(c) && c.Reopen == ReopenKind.None && c.Index == IndexShape.Unique);

    /// <summary>AllowMultiple cells, without the terminal axis — the agreement test compares all four itself.</summary>
    public static IEnumerable<TestCaseData> MultiCells() =>
        EngineAxes.PairwiseWhere(c => AxisArchetypes.SupportsBase(c) && c.Reopen == ReopenKind.None && c.Index == IndexShape.AllowMultiple);

    /// <summary>Unique cells, without the terminal axis.</summary>
    public static IEnumerable<TestCaseData> UniqueCells() =>
        EngineAxes.PairwiseWhere(c => AxisArchetypes.SupportsBase(c) && c.Reopen == ReopenKind.None && c.Index == IndexShape.Unique);

    private DatabaseEngine Open(Cell cell)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        AxisArchetypes.Register(dbe, cell);
        dbe.InitializeArchetypes();
        return dbe;
    }

    private EntityId[] Seed(DatabaseEngine dbe, Cell cell, int count)
    {
        var ids = new EntityId[count];
        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            for (var i = 0; i < count; i++)
            {
                ids[i] = AxisArchetypes.Spawn(t, cell, i);
            }

            Assert.That(t.Commit(), Is.True, $"{cell}: spawn commit");
        }

        // Index maintenance for SV and Transient fields lands at the fence; an indexed query before it reads a half-built tree.
        dbe.WriteTickFence(++_tick);
        return ids;
    }

    /// <summary>
    /// <c>Any()</c> collapses to 0/1 by construction, so it is checked for agreement on EMPTINESS rather than on the exact count. Stated explicitly so a
    /// reader does not mistake it for an accidentally weaker assertion — an <c>Any()</c> answering false while matches existed is exactly #590's third face.
    /// </summary>
    private static void AssertAgrees(int got, int expected, Cell cell, QueryTerminal terminal, string when)
    {
        if (terminal == QueryTerminal.Any)
        {
            Assert.That(got > 0, Is.EqualTo(expected > 0), $"{cell} / {terminal}: {when} — Any() must agree on whether ANY entity matches");
            return;
        }

        Assert.That(got, Is.EqualTo(expected), $"{cell} / {terminal}: {when}");
    }

    // ── Against the model ───────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [TestCaseSource(nameof(MultiCases))]
    public void MultiValueLookup_EveryTerminal_AgreesWithTheModel(Cell cell, QueryTerminal terminal)
    {
        using var dbe = Open(cell);
        Seed(dbe, cell, EntityCount);

        using var t = dbe.CreateQuickTransaction();
        for (var bucket = 0; bucket < AxisArchetypes.BucketCount; bucket++)
        {
            var expected = AxisArchetypes.ExpectedInBucket(EntityCount, bucket);
            Assert.That(expected, Is.GreaterThan(0), "precondition: the model expects this bucket to be populated");
            AssertAgrees(AxisArchetypes.QueryByBucket(t, cell, bucket, terminal), expected, cell, terminal,
                $"bucket {bucket} of {EntityCount} entities");
        }
    }

    [Test]
    [TestCaseSource(nameof(UniqueCases))]
    public void PointLookup_EveryTerminal_FindsExactlyTheOneEntity(Cell cell, QueryTerminal terminal)
    {
        using var dbe = Open(cell);
        Seed(dbe, cell, EntityCount);

        using var t = dbe.CreateQuickTransaction();
        foreach (var i in new[] { 0, 1, EntityCount / 2, EntityCount - 1 })
        {
            AssertAgrees(AxisArchetypes.QueryByKey(t, cell, AxisArchetypes.KeyOf(i), terminal), 1, cell, terminal,
                $"the unique key of entity {i}");
        }
    }

    [Test]
    [TestCaseSource(nameof(MultiCases))]
    public void MultiValueLookup_EveryTerminal_ReportsNothing_ForAKeyNoEntityHolds(Cell cell, QueryTerminal terminal)
    {
        // The empty answer is its own case: a terminal that returns the whole set when the predicate matches nothing fails here and nowhere else, and Any()
        // has no other way to be checked for a false positive.
        using var dbe = Open(cell);
        Seed(dbe, cell, EntityCount);

        using var t = dbe.CreateQuickTransaction();
        AssertAgrees(AxisArchetypes.QueryByBucket(t, cell, AxisArchetypes.BucketCount + 7, terminal), 0, cell, terminal, "a bucket no entity holds");
    }

    [Test]
    [TestCaseSource(nameof(UniqueCases))]
    public void PointLookup_EveryTerminal_ReportsNothing_ForAKeyNoEntityHolds(Cell cell, QueryTerminal terminal)
    {
        using var dbe = Open(cell);
        Seed(dbe, cell, EntityCount);

        using var t = dbe.CreateQuickTransaction();
        AssertAgrees(AxisArchetypes.QueryByKey(t, cell, AxisArchetypes.KeyOf(EntityCount + 500), terminal), 0, cell, terminal, "a key no entity holds");
    }

    [Test]
    [TestCaseSource(nameof(MultiCases))]
    public void MultiValueLookup_EveryTerminal_SeesEntitiesMoveBetweenBuckets(Cell cell, QueryTerminal terminal)
    {
        // #590's shape was a terminal disagreeing AFTER the data moved, not on a static set. Shifting every payload by one moves each entity one bucket along,
        // so a terminal reading a stale plan reports the pre-update population.
        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, EntityCount);

        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            for (var i = 0; i < ids.Length; i++)
            {
                AxisArchetypes.Update(t, cell, ids[i], i + 1);
            }

            Assert.That(t.Commit(), Is.True, $"{cell}: update commit");
        }

        dbe.WriteTickFence(++_tick);

        using var read = dbe.CreateQuickTransaction();
        for (var bucket = 0; bucket < AxisArchetypes.BucketCount; bucket++)
        {
            // Entity i now sits in bucket (i+1) % BucketCount, so bucket b holds what bucket b-1 held.
            var expected = AxisArchetypes.ExpectedInBucket(EntityCount, (bucket + AxisArchetypes.BucketCount - 1) % AxisArchetypes.BucketCount);
            AssertAgrees(AxisArchetypes.QueryByBucket(read, cell, bucket, terminal), expected, cell, terminal,
                $"bucket {bucket} after every entity moved one bucket along");
        }
    }

    // ── The differential: terminals against each other ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Every terminal must give the SAME answer as every other on the same data and predicate.
    /// </summary>
    /// <remarks>
    /// This is the assertion that would have caught #590/#592 without anyone guessing which terminal was wrong. A per-terminal expected value only catches the
    /// terminal you thought to check; agreement catches whichever one disagrees — and it needs no model, so it holds even on cells whose right answer the
    /// fixture could not compute.
    /// </remarks>
    [Test]
    [TestCaseSource(nameof(MultiCells))]
    public void MultiValueLookup_AllTerminalsAgreeWithEachOther(Cell cell)
    {
        using var dbe = Open(cell);
        Seed(dbe, cell, EntityCount);

        using var t = dbe.CreateQuickTransaction();
        for (var bucket = 0; bucket < AxisArchetypes.BucketCount; bucket++)
        {
            AssertAllTerminalsAgree(
                AxisArchetypes.QueryByBucket(t, cell, bucket, QueryTerminal.Execute),
                AxisArchetypes.QueryByBucket(t, cell, bucket, QueryTerminal.Count),
                AxisArchetypes.QueryByBucket(t, cell, bucket, QueryTerminal.ToView),
                AxisArchetypes.QueryByBucket(t, cell, bucket, QueryTerminal.Any),
                cell, $"bucket {bucket}");
        }
    }

    [Test]
    [TestCaseSource(nameof(UniqueCells))]
    public void PointLookup_AllTerminalsAgreeWithEachOther(Cell cell)
    {
        using var dbe = Open(cell);
        Seed(dbe, cell, EntityCount);

        using var t = dbe.CreateQuickTransaction();
        foreach (var i in new[] { 0, EntityCount - 1, EntityCount + 500 })
        {
            var key = AxisArchetypes.KeyOf(i);
            AssertAllTerminalsAgree(
                AxisArchetypes.QueryByKey(t, cell, key, QueryTerminal.Execute),
                AxisArchetypes.QueryByKey(t, cell, key, QueryTerminal.Count),
                AxisArchetypes.QueryByKey(t, cell, key, QueryTerminal.ToView),
                AxisArchetypes.QueryByKey(t, cell, key, QueryTerminal.Any),
                cell, $"the key of entity {i}");
        }
    }

    private static void AssertAllTerminalsAgree(int execute, int count, int view, int any, Cell cell, string when)
    {
        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(execute), $"{cell}: {when} — Count() disagrees with Execute()");
            Assert.That(view, Is.EqualTo(execute), $"{cell}: {when} — ToView() disagrees with Execute()");
            Assert.That(any > 0, Is.EqualTo(execute > 0), $"{cell}: {when} — Any() disagrees with Execute()");
        });
    }
}
