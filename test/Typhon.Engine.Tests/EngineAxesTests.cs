using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Self-tests for the covering-array generator (#704 AC4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a generator needs its own tests.</b> After #704 more than thirty fixtures take their cases from <see cref="EngineAxes"/>. A silent defect here —
/// a pair that is never emitted, a non-deterministic ordering, an impossibility filter that quietly deletes a region — does not fail anything. It removes
/// coverage while every affected fixture still reports green, which is the exact class of false confidence #703 and #704 exist to remove. So the properties
/// the array CLAIMS are asserted rather than assumed: every pair covered, the same set every run, every emitted cell valid, every case uniquely named.
/// </para>
/// <para>
/// These are pure computations over enums — no <c>DatabaseEngine</c>, no I/O — so the whole fixture runs in a few milliseconds.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class EngineAxesTests
{
    /// <summary>A fixture-local axis, standing in for the query-terminal axis that motivated <see cref="EngineAxes.PairwiseWith{T}"/>.</summary>
    private enum Terminal
    {
        Execute,
        Count,
        Any,
        ToView,
    }

    private static List<Cell> CellsOf(IEnumerable<TestCaseData> cases)
    {
        var cells = new List<Cell>();
        foreach (var c in cases)
        {
            cells.Add((Cell)c.Arguments[0]);
        }

        return cells;
    }

    private static List<string> NamesOf(IEnumerable<TestCaseData> cases)
    {
        var names = new List<string>();
        foreach (var c in cases)
        {
            names.Add(c.TestName);
        }

        return names;
    }

    /// <summary>Every valid cell of the whole matrix — the reference set the generated subset is judged against.</summary>
    private static List<Cell> AllValidCells()
    {
        var all = new List<Cell>();
        foreach (StorageShape shape in Enum.GetValues(typeof(StorageShape)))
        {
            foreach (var dur in new[] { DurabilityMode.Deferred, DurabilityMode.GroupCommit, DurabilityMode.Immediate })
            {
                foreach (IndexShape ix in Enum.GetValues(typeof(IndexShape)))
                {
                    foreach (ReopenKind reopen in Enum.GetValues(typeof(ReopenKind)))
                    {
                        foreach (var disc in new[] { DurabilityDiscipline.TickFence, DurabilityDiscipline.Commit })
                        {
                            foreach (CollectionShape coll in Enum.GetValues(typeof(CollectionShape)))
                            {
                                foreach (SpatialShape sp in Enum.GetValues(typeof(SpatialShape)))
                                {
                                    var cell = new Cell(shape, dur, ix, reopen, disc, coll, sp);
                                    if (EngineAxes.IsValid(cell))
                                    {
                                        all.Add(cell);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        return all;
    }

    private static string[] AxisValues(in Cell c) =>
    [
        c.Shape.ToString(),
        c.Durability.ToString(),
        c.Index.ToString(),
        c.Reopen.ToString(),
        c.Discipline.ToString(),
        c.Collection.ToString(),
        c.Spatial.ToString(),
    ];

    /// <summary>Every (axisA=valueA, axisB=valueB) that occurs in at least one VALID cell, keyed for set comparison.</summary>
    private static HashSet<string> PairsIn(IEnumerable<Cell> cells)
    {
        var pairs = new HashSet<string>();
        foreach (var cell in cells)
        {
            var v = AxisValues(cell);
            for (var a = 0; a < v.Length; a++)
            {
                for (var b = a + 1; b < v.Length; b++)
                {
                    pairs.Add($"{a}:{v[a]}|{b}:{v[b]}");
                }
            }
        }

        return pairs;
    }

    // ── The covering property itself ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Pairwise_CoversEveryPairThatAnyValidCellCanExpress()
    {
        var required = PairsIn(AllValidCells());
        var got = PairsIn(CellsOf(EngineAxes.Pairwise()));

        required.ExceptWith(got);
        Assert.That(required, Is.Empty, "a pair reachable by some valid cell was never emitted — that region of the matrix is silently untested");
    }

    [Test]
    public void Triplewise_CoversEveryPairwiseCaseThePairwiseSourceDoes()
    {
        // Strength 3 subsumes strength 2 by construction; asserting it catches a regression where the strength argument stops being honoured.
        var pairwise = PairsIn(CellsOf(EngineAxes.Pairwise()));
        var triplewise = PairsIn(CellsOf(EngineAxes.Triplewise()));

        pairwise.ExceptWith(triplewise);
        Assert.That(pairwise, Is.Empty, "the triplewise set must cover at least everything the pairwise set covers");
    }

    [Test]
    public void Triplewise_CostsMoreThanPairwise_AndIsTheReasonItIsNightlyOnly()
    {
        var pairwise = CellsOf(EngineAxes.Pairwise()).Count;
        var triplewise = CellsOf(EngineAxes.Triplewise()).Count;

        Assert.That(triplewise, Is.GreaterThan(pairwise),
            "if triplewise were not dearer than pairwise there would be no reason to keep it out of the PR gate");
    }

    /// <summary>
    /// The standing guard on #704's case budget. Every converted fixture multiplies its cost by these numbers, so an axis added later without narrowing —
    /// or an <see cref="EngineAxes.IsValid"/> rule dropped — shows up here as a hard failure rather than as a gate that quietly got four minutes slower.
    /// The ceilings are deliberately loose (roughly 2x the measured value): this is a blast-radius alarm, not a golden-file test that churns on every tweak.
    /// </summary>
    [Test]
    public void CaseCounts_StayWithinTheBudgetTheGateWasSizedFor()
    {
        var pairwise = CellsOf(EngineAxes.Pairwise()).Count;
        var triplewise = CellsOf(EngineAxes.Triplewise()).Count;
        TestContext.Out.WriteLine($"EngineAxes case counts — pairwise: {pairwise}, triplewise: {triplewise}, valid cells: {AllValidCells().Count}");

        Assert.Multiple(() =>
        {
            Assert.That(pairwise, Is.LessThanOrEqualTo(60),
                "pairwise is the PR-gate source; past ~60 cases a single converted fixture costs more than the budget #704 set for all of them");
            Assert.That(triplewise, Is.LessThanOrEqualTo(400),
                "triplewise is nightly-only and may be dear, but not unbounded");
        });
    }

    [Test]
    public void EveryEmittedCell_IsValid()
    {
        Assert.Multiple(() =>
        {
            foreach (var cell in CellsOf(EngineAxes.Pairwise()))
            {
                Assert.That(EngineAxes.IsValid(cell), Is.True, $"{cell} was emitted but IsValid rejects it");
            }

            foreach (var cell in CellsOf(EngineAxes.Triplewise()))
            {
                Assert.That(EngineAxes.IsValid(cell), Is.True, $"{cell} was emitted but IsValid rejects it");
            }
        });
    }

    // ── Determinism: a case set that varies per run makes a failure unreproducible ────────────────────────────────────────────────────────────────────────

    [Test]
    public void Generation_IsDeterministic_AcrossRepeatedCalls()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NamesOf(EngineAxes.Pairwise()), Is.EqualTo(NamesOf(EngineAxes.Pairwise())),
                "pairwise case order and content must not vary between calls");
            Assert.That(NamesOf(EngineAxes.Triplewise()), Is.EqualTo(NamesOf(EngineAxes.Triplewise())), "triplewise must not vary between calls");
        });
    }

    [Test]
    public void EveryCaseName_IsUnique_AndNamesItsCell()
    {
        var names = new HashSet<string>();
        Assert.Multiple(() =>
        {
            foreach (var c in EngineAxes.Pairwise())
            {
                var cell = (Cell)c.Arguments[0];
                Assert.That(names.Add(c.TestName), Is.True,
                    $"duplicate case name '{c.TestName}' — the name is the repro, so two cells sharing one is a defect");
                Assert.That(c.TestName, Does.Contain(cell.ToString()), "a case name must carry its cell or a nightly failure cannot be reproduced");
            }
        });
    }

    // ── The impossibility filter — every rule cites a production throw site, so each is pinned here ───────────────────────────────────────────────────────

    [Test]
    public void IsValid_RejectsReopenOnAPureTransientShape()
    {
        // Heap-only: there is no state on the far side of a reopen to assert against.
        Assert.That(EngineAxes.IsValid(new Cell(StorageShape.PureTransient, DurabilityMode.Deferred, IndexShape.None, ReopenKind.Clean)), Is.False);
        Assert.That(EngineAxes.IsValid(new Cell(StorageShape.PureTransient, DurabilityMode.Deferred, IndexShape.None, ReopenKind.None)), Is.True);
    }

    [Test]
    public void IsValid_RejectsCommitDisciplineOnAShapeWithNoSingleVersionComponent()
    {
        // DurabilityDiscipline.cs:11-13 — the discipline applies only to the SingleVersion layout.
        Assert.Multiple(() =>
        {
            Assert.That(
                EngineAxes.IsValid(new Cell(StorageShape.PureVersioned, DurabilityMode.Deferred, IndexShape.None, ReopenKind.None,
                    DurabilityDiscipline.Commit)),
                Is.False,
                "Versioned is always commit-scoped — the knob names nothing there");
            Assert.That(
                EngineAxes.IsValid(new Cell(StorageShape.PureTransient, DurabilityMode.Deferred, IndexShape.None, ReopenKind.None,
                    DurabilityDiscipline.Commit)),
                Is.False,
                "Transient is never durable");
            Assert.That(
                EngineAxes.IsValid(new Cell(StorageShape.PureSv, DurabilityMode.Deferred, IndexShape.None, ReopenKind.None, DurabilityDiscipline.Commit)),
                Is.True);
            Assert.That(
                EngineAxes.IsValid(new Cell(StorageShape.SvPlusTransient, DurabilityMode.Deferred, IndexShape.None, ReopenKind.None,
                    DurabilityDiscipline.Commit)),
                Is.True,
                "a mixed shape has an SV member, so the discipline is meaningful");
        });
    }

    [Test]
    public void IsValid_RejectsCollectionAndSpatialOnlyOnAPureTransientShape()
    {
        // DatabaseEngine.cs:2411-2422 (collection) and DatabaseDefinitions.cs:357-360 (spatial) both throw at registration. Both constraints are per-COMPONENT,
        // so a mixed shape stays valid — its SV/Versioned member is what carries the payload.
        Assert.Multiple(() =>
        {
            Assert.That(
                EngineAxes.IsValid(new Cell(StorageShape.PureTransient, DurabilityMode.Deferred, IndexShape.None, ReopenKind.None,
                    Collection: CollectionShape.Present)),
                Is.False);
            Assert.That(
                EngineAxes.IsValid(new Cell(StorageShape.PureTransient, DurabilityMode.Deferred, IndexShape.None, ReopenKind.None,
                    Spatial: SpatialShape.Present)),
                Is.False);
            Assert.That(
                EngineAxes.IsValid(new Cell(StorageShape.SvPlusTransient, DurabilityMode.Deferred, IndexShape.None, ReopenKind.None,
                    Collection: CollectionShape.Present)),
                Is.True);
            Assert.That(
                EngineAxes.IsValid(new Cell(StorageShape.VerPlusTransient, DurabilityMode.Deferred, IndexShape.None, ReopenKind.None,
                    Spatial: SpatialShape.Present)),
                Is.True);
        });
    }

    [Test]
    public void IsValid_AllowsAnIndexOnAPureTransientShape_TheRegionTheOldFilterDeleted()
    {
        // #704's correction to the filter. A pure-Transient archetype with an indexed field is cluster-backed and queryable — that is the whole subject of
        // #655, covered today by ClusterPureTransientIndexTests and TransientIndexTests. The previous filter excluded it as "impossible", which removed a
        // region the engine supports and a bug had already lived in. If this ever flips back to false, that coverage disappears silently again.
        Assert.Multiple(() =>
        {
            Assert.That(
                EngineAxes.IsValid(new Cell(StorageShape.PureTransient, DurabilityMode.Deferred, IndexShape.AllowMultiple, ReopenKind.None)),
                Is.True);
            Assert.That(
                EngineAxes.IsValid(new Cell(StorageShape.PureTransient, DurabilityMode.Deferred, IndexShape.Unique, ReopenKind.None)),
                Is.True);
        });
    }

    // ── Narrowing ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void PairwiseWhere_EmitsOnlyCellsTheFilterAccepts()
    {
        var cells = CellsOf(EngineAxes.PairwiseWhere(c => c.Shape == StorageShape.PureSv && c.Reopen != ReopenKind.None));

        Assert.That(cells, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            foreach (var cell in cells)
            {
                Assert.That(cell.Shape, Is.EqualTo(StorageShape.PureSv), $"{cell} escaped the narrowing filter");
                Assert.That(cell.Reopen, Is.Not.EqualTo(ReopenKind.None), $"{cell} escaped the narrowing filter");
            }
        });
    }

    [Test]
    public void PairwiseWhere_FilterMatchingNothing_YieldsNoCases()
    {
        // Deliberately pinned: an over-narrow filter must produce ZERO cases rather than a silently degenerate set. A fixture whose source dries up is caught
        // by NUnit (a TestCaseSource yielding nothing is an error), which is the loud outcome; the quiet one would be a single arbitrary cell.
        Assert.That(CellsOf(EngineAxes.PairwiseWhere(_ => false)), Is.Empty);
    }

    // ── The crossed source ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void PairwiseWith_CoversEveryExtraAxisValue()
    {
        var seen = new HashSet<Terminal>();
        foreach (var c in EngineAxes.PairwiseWith((Terminal[])Enum.GetValues(typeof(Terminal))))
        {
            seen.Add((Terminal)c.Arguments[1]);
        }

        Assert.That(seen, Has.Count.EqualTo(Enum.GetValues(typeof(Terminal)).Length),
            "every value of the fixture-local axis must appear — that axis is the one #590/#592 hid in");
    }

    [Test]
    public void PairwiseWith_CoversEveryPairBetweenTheExtraAxisAndEachEngineAxis()
    {
        var terminals = (Terminal[])Enum.GetValues(typeof(Terminal));
        var got = new HashSet<string>();
        foreach (var c in EngineAxes.PairwiseWith(terminals))
        {
            var cell = (Cell)c.Arguments[0];
            var extra = (Terminal)c.Arguments[1];
            var v = AxisValues(cell);
            for (var a = 0; a < v.Length; a++)
            {
                got.Add($"{a}:{v[a]}|7:{extra}");
            }
        }

        var required = new HashSet<string>();
        foreach (var cell in AllValidCells())
        {
            var v = AxisValues(cell);
            foreach (var extra in terminals)
            {
                for (var a = 0; a < v.Length; a++)
                {
                    required.Add($"{a}:{v[a]}|7:{extra}");
                }
            }
        }

        required.ExceptWith(got);
        Assert.That(required, Is.Empty, "an (engine axis, fixture axis) pair was never emitted — the cross is the point of this source");
    }

    [Test]
    public void PairwiseWith_FoldsTheExtraAxisIn_RatherThanMultiplyingByIt()
    {
        // The whole reason PairwiseWith exists rather than a nested loop: a cross product would multiply the case count by the axis size. Folding the axis into
        // the same greedy pass keeps the count bounded by the two LARGEST axes, so a 4-value extra axis must not cost 4x.
        var plain = CellsOf(EngineAxes.Pairwise()).Count;
        var crossed = 0;
        foreach (var _ in EngineAxes.PairwiseWith((Terminal[])Enum.GetValues(typeof(Terminal))))
        {
            crossed++;
        }

        Assert.That(crossed, Is.LessThan(plain * 2),
            $"crossing a 4-value axis produced {crossed} cases against a plain {plain} — that is a cross product, not a covering array");
    }

    [Test]
    public void PairwiseWith_EmptyAxis_ThrowsRatherThanYieldingNothing()
    {
        // An empty axis would make the source return zero cases, which reads as "this fixture has no cells" rather than "this call is wrong".
        var ex = Assert.Throws<ArgumentException>(() => EngineAxes.PairwiseWith<Terminal>([]));

        Assert.That(ex.Message, Does.Contain("at least one value"));
    }

    [Test]
    public void PairwiseWith_HonoursItsFilter()
    {
        var cases = EngineAxes.PairwiseWith((Terminal[])Enum.GetValues(typeof(Terminal)), (c, t) => t != Terminal.ToView && c.Shape == StorageShape.PureSv);

        var any = false;
        Assert.Multiple(() =>
        {
            foreach (var c in cases)
            {
                any = true;
                Assert.That((Terminal)c.Arguments[1], Is.Not.EqualTo(Terminal.ToView));
                Assert.That(((Cell)c.Arguments[0]).Shape, Is.EqualTo(StorageShape.PureSv));
            }
        });

        Assert.That(any, Is.True);
    }

    // ── Case naming: the property that keeps existing fixture case names stable across #704's axis additions ──────────────────────────────────────────────

    [Test]
    public void CellToString_OmitsTheNewAxesWhenTheyHoldTheirDefault()
    {
        var plain = new Cell(StorageShape.PureSv, DurabilityMode.Deferred, IndexShape.Unique, ReopenKind.Clean);

        Assert.That(plain.ToString(), Is.EqualTo("PureSv_Deferred_Unique_Clean"),
            "a fixture that does not vary the #704 axes must keep exactly the case names it had, or every stored repro and filter breaks");
    }

    [Test]
    public void CellToString_NamesTheNewAxesWhenTheyDiffer()
    {
        var rich = new Cell(StorageShape.PureSv, DurabilityMode.Deferred, IndexShape.Unique, ReopenKind.Clean,
            DurabilityDiscipline.Commit, CollectionShape.Present, SpatialShape.Present);

        Assert.That(rich.ToString(), Is.EqualTo("PureSv_Deferred_Unique_Clean_Commit_Coll_Spatial"));
    }

    [Test]
    public void CellToString_IsInjective_OverTheWholeValidMatrix()
    {
        // The "omit the default" naming is only safe if it still separates every pair of distinct cells.
        var names = new HashSet<string>();
        Assert.Multiple(() =>
        {
            foreach (var cell in AllValidCells())
            {
                Assert.That(names.Add(cell.ToString()), Is.True, $"two distinct cells share the name '{cell}'");
            }
        });
    }
}
