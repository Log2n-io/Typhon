using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>The storage-mode composition of the archetype under test.</summary>
public enum StorageShape
{
    PureSv,
    PureVersioned,
    PureTransient,
    SvPlusVersioned,
    SvPlusTransient,
    VerPlusTransient,
}

/// <summary>Whether the component under test carries a secondary index, and of which kind.</summary>
public enum IndexShape
{
    None,
    Unique,
    AllowMultiple,
}

/// <summary>Whether the archetype under test carries a <c>ComponentCollection</c> field (a variable-size payload behind a VSBS descriptor).</summary>
public enum CollectionShape
{
    None,
    Present,
}

/// <summary>Whether the archetype under test carries a <c>[SpatialIndex]</c> field, and so participates in the engine-wide spatial grid.</summary>
public enum SpatialShape
{
    None,
    Present,
}

/// <summary>What the fixture does between writing the data and asserting it.</summary>
public enum ReopenKind
{
    None,
    Clean,
    Crash,
    CleanThenCrash,
}

/// <summary>One cell of the engine's option matrix.</summary>
/// <remarks>
/// <para>
/// The four original axes (shape, durability, index, reopen) are the ones a fixture is most likely to vary, so they lead. The three added by #704 —
/// <see cref="Discipline"/>, <see cref="Collection"/>, <see cref="Spatial"/> — trail them and, crucially, contribute NOTHING to <see cref="ToString"/> when
/// they hold their default value. A case name is the repro (see <see cref="EngineAxes"/>), so a fixture that does not vary the new axes keeps exactly the
/// names it had, and a name grows a suffix only when the extra axis is actually part of what failed.
/// </para>
/// </remarks>
public readonly record struct Cell(
    StorageShape Shape,
    DurabilityMode Durability,
    IndexShape Index,
    ReopenKind Reopen,
    CommitDiscipline Discipline = CommitDiscipline.TickFence,
    CollectionShape Collection = CollectionShape.None,
    SpatialShape Spatial = SpatialShape.None)
{
    /// <summary>Whether this shape contains at least one <see cref="StorageMode.SingleVersion"/> component.</summary>
    public bool HasSingleVersion =>
        Shape is StorageShape.PureSv or StorageShape.SvPlusVersioned or StorageShape.SvPlusTransient;

    /// <summary>Whether this shape contains at least one non-Transient component, i.e. anything the engine can persist.</summary>
    public bool HasDurableComponent => Shape != StorageShape.PureTransient;

    /// <summary>Whether this shape contains at least one <see cref="StorageMode.Transient"/> component.</summary>
    public bool HasTransient =>
        Shape is StorageShape.PureTransient or StorageShape.SvPlusTransient or StorageShape.VerPlusTransient;

    public override string ToString()
    {
        var name = $"{Shape}_{Durability}_{Index}_{Reopen}";
        if (Discipline != CommitDiscipline.TickFence)
        {
            name += $"_{Discipline}";
        }

        if (Collection != CollectionShape.None)
        {
            name += "_Coll";
        }

        if (Spatial != SpatialShape.None)
        {
            name += "_Spatial";
        }

        return name;
    }
}

/// <summary>
/// Covering-array sources over Typhon's option axes, so a fixture can state which axes it varies instead of hard-coding a handful of hand-picked combinations.
/// </summary>
/// <remarks>
/// <para>
/// The problem this solves: the option space is multiplicative — storage mode × durability × discipline × index kind × collection × spatial × reopen kind is
/// 6·3·2·3·2·2·4 = 1,728 cells before any workload axis — while the defects found on this branch were each a SINGLE untested cell of it, not a missing test of
/// a well-covered path. Example-based fixtures grow linearly and the space grows multiplicatively, so line coverage stays high while the matrix stays mostly
/// empty. That is the shape of the problem, and enumerating all 1,728 is not the answer either: it is slow and most cells are redundant.
/// </para>
/// <para>
/// Pairwise (2-wise) covering is the standard answer, and the empirical result behind it is that the large majority of defects are triggered by ONE parameter
/// or by an INTERACTION OF TWO, so covering every pair catches most of what exhaustive enumeration would, at a fraction of the size. The case count is bounded
/// by the product of the two LARGEST axes (shape 6 × reopen 4 = 24), not by the product of all of them — which is why #704 could add three axes for
/// essentially no extra cases. <see cref="Triplewise"/> exists for a nightly run where the extra confidence is worth the extra cases.
/// </para>
/// <para>
/// Generation is the standard greedy algorithm: hold the set of not-yet-covered t-tuples, repeatedly pick the candidate row covering the most of them, stop
/// when none remain. Greedy is not minimal — optimal covering-array construction is NP-hard — but it is deterministic here (candidates are enumerated in a
/// fixed order and ties break on first-seen), which matters more than minimality: a test source that returns a different set per run makes a failure
/// impossible to reproduce.
/// </para>
/// <para>
/// <b>Every case is named.</b> A covering-array failure reported as "case 13 of 18" is useless — the name IS the repro, and without it the array is worse than
/// the hand-written tests it replaces.
/// </para>
/// <para>
/// This generalises <c>TestBase.BuildNoiseCasesL1/L2</c>, which is already this idea hard-coded for two axes.
/// </para>
/// </remarks>
public static class EngineAxes
{
    private static readonly StorageShape[] Shapes = (StorageShape[])Enum.GetValues(typeof(StorageShape));
    private static readonly DurabilityMode[] Durabilities = [DurabilityMode.Deferred, DurabilityMode.GroupCommit, DurabilityMode.Immediate];
    private static readonly IndexShape[] Indexes = (IndexShape[])Enum.GetValues(typeof(IndexShape));
    private static readonly ReopenKind[] Reopens = (ReopenKind[])Enum.GetValues(typeof(ReopenKind));
    private static readonly CommitDiscipline[] Disciplines = [CommitDiscipline.TickFence, CommitDiscipline.Commit];
    private static readonly CollectionShape[] Collections = (CollectionShape[])Enum.GetValues(typeof(CollectionShape));
    private static readonly SpatialShape[] Spatials = (SpatialShape[])Enum.GetValues(typeof(SpatialShape));

    /// <summary>
    /// Cells that cannot exist, as opposed to cells that merely are not interesting. Every rule below cites the production site that REJECTS the combination —
    /// an impossibility filter written from intuition silently deletes a region of the matrix, which is the failure this whole class exists to prevent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#704 correction, worth stating.</b> This filter used to also exclude <c>PureTransient</c> + an index, on the reasoning that a Transient component has
    /// "no index home of its own". That is wrong: a pure-Transient archetype with an indexed field is cluster-backed and queryable, and it is the explicit
    /// subject of #655 — <c>ClusterPureTransientIndexTests</c> and <c>TransientIndexTests</c> cover it today. The filter was excluding a region the engine
    /// supports and a bug had already lived in. It is now allowed.
    /// </para>
    /// </remarks>
    public static bool IsValid(in Cell c)
    {
        // A pure-Transient archetype is heap-only, so nothing of it survives ANY reopen — there is no state to assert on the far side.
        if (c.Shape == StorageShape.PureTransient && c.Reopen != ReopenKind.None)
        {
            return false;
        }

        // CommitDiscipline.Commit is defined only for the SingleVersion layout: "Versioned is always commit-scoped and Transient is never durable"
        // (CommitDiscipline.cs:11-13). On a shape with no SV component the knob names nothing.
        if (c.Discipline == CommitDiscipline.Commit && !c.HasSingleVersion)
        {
            return false;
        }

        // Registration throws for a Transient component carrying a ComponentCollection field (DatabaseEngine.cs:2411-2422) or a [SpatialIndex] field
        // (DatabaseDefinitions.cs:357-360). Both constraints are per-COMPONENT, so a mixed shape stays valid — its SV/Versioned member carries the payload.
        if (c.Shape == StorageShape.PureTransient && (c.Collection != CollectionShape.None || c.Spatial != SpatialShape.None))
        {
            return false;
        }

        return true;
    }

    /// <summary>Every pair of axis values appears in at least one case. The default source for a converted fixture.</summary>
    public static IEnumerable<TestCaseData> Pairwise() => Generate(strength: 2, filter: null);

    /// <summary>Every triple appears in at least one case. For a nightly or pre-merge run, not the inner loop.</summary>
    public static IEnumerable<TestCaseData> Triplewise() => Generate(strength: 3, filter: null);

    /// <summary>
    /// Pairwise over a restricted axis set. A fixture that genuinely cannot express some shapes should narrow the axis HERE and say so, rather than silently
    /// receiving cells it skips at run time — a skipped cell looks like coverage in the test count and is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Narrow every axis the fixture does not consume, not just the ones it fails on.</b> This went wrong the first time within #704 itself: adding the
    /// collection and spatial axes left <c>SchemaEvolutionMatrixTests</c>' filter untouched, so it began receiving cells named <c>…_Coll</c> and
    /// <c>…_Spatial</c> that its body simply ignored. Nothing failed — the cases passed, the count went up, and the NAMES claimed coverage that never
    /// happened. An ignored axis is worse than a missing one, because it reports success.
    /// </para>
    /// </remarks>
    public static IEnumerable<TestCaseData> PairwiseWhere(Func<Cell, bool> filter) => Generate(strength: 2, filter);

    /// <summary>Triplewise over a restricted axis set — the nightly counterpart of <see cref="PairwiseWhere"/>.</summary>
    public static IEnumerable<TestCaseData> TriplewiseWhere(Func<Cell, bool> filter) => Generate(strength: 3, filter);

    /// <summary>
    /// Pairwise over the engine axes CROSSED WITH one fixture-local axis, which becomes a full participant in the covering computation rather than a
    /// multiplier on top of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Some axes are not engine-wide and must not enter <see cref="Cell"/>: a query terminal (<c>Execute</c> / <c>Count</c> / <c>Any</c> /
    /// <c>ExecuteOrdered</c> / <c>ToView</c>) means nothing to a schema-migration fixture, and putting it in the shared cell would inflate every other
    /// fixture's candidate set. But it must still be COVERED — #590/#592 were exactly a terminal × predicate-shape interaction, three cells of a 2×2 tested
    /// and the bug in the fourth.
    /// </para>
    /// <para>
    /// Folding the extra axis into the same greedy pass is what keeps this cheap. A naive cross product multiplies the case count by the axis size; treating
    /// it as one more axis leaves the count bounded by the two largest axes, exactly as adding <see cref="Cell"/>'s three new axes was.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The fixture-local axis type. Its <c>ToString()</c> becomes part of the case name, so it must be readable.</typeparam>
    /// <param name="extraAxis">The values of the fixture-local axis. Must be non-empty.</param>
    /// <param name="filter">Optional validity filter over the combined cell, applied on top of <see cref="IsValid"/>.</param>
    public static IEnumerable<TestCaseData> PairwiseWith<T>(T[] extraAxis, Func<Cell, T, bool> filter = null) =>
        GenerateWith(strength: 2, extraAxis, filter);

    /// <summary>Triplewise counterpart of <see cref="PairwiseWith{T}"/> — for the nightly tier.</summary>
    public static IEnumerable<TestCaseData> TriplewiseWith<T>(T[] extraAxis, Func<Cell, T, bool> filter = null) =>
        GenerateWith(strength: 3, extraAxis, filter);

    private static List<Cell> Candidates(Func<Cell, bool> filter)
    {
        var all = new List<Cell>();
        foreach (var shape in Shapes)
        {
            foreach (var dur in Durabilities)
            {
                foreach (var ix in Indexes)
                {
                    foreach (var reopen in Reopens)
                    {
                        foreach (var discipline in Disciplines)
                        {
                            foreach (var collection in Collections)
                            {
                                foreach (var spatial in Spatials)
                                {
                                    var cell = new Cell(shape, dur, ix, reopen, discipline, collection, spatial);
                                    if (IsValid(cell) && (filter == null || filter(cell)))
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

    private static string[] AxisValues(in Cell cell) =>
    [
        $"0:{cell.Shape}",
        $"1:{cell.Durability}",
        $"2:{cell.Index}",
        $"3:{cell.Reopen}",
        $"4:{cell.Discipline}",
        $"5:{cell.Collection}",
        $"6:{cell.Spatial}",
    ];

    private static IEnumerable<TestCaseData> Generate(int strength, Func<Cell, bool> filter)
    {
        var all = Candidates(filter);
        var rows = new List<string[]>(all.Count);
        foreach (var cell in all)
        {
            rows.Add(AxisValues(cell));
        }

        foreach (var index in GreedyCover(rows, strength))
        {
            var cell = all[index];
            yield return new TestCaseData(cell).SetName($"{{m}}_{cell}");
        }
    }

    // Argument validation is eager, not deferred into the iterator below: a bad axis surfaces where the fixture wrote the call, not at NUnit's discovery pass
    // where the failure reads as "this fixture has no cases".
    private static IEnumerable<TestCaseData> GenerateWith<T>(int strength, T[] extraAxis, Func<Cell, T, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(extraAxis);
        if (extraAxis.Length == 0)
        {
            throw new ArgumentException(
                "The fixture-local axis must have at least one value; an empty axis would silently yield zero cases.",
                nameof(extraAxis));
        }

        return GenerateWithCore(strength, extraAxis, filter);
    }

    private static IEnumerable<TestCaseData> GenerateWithCore<T>(int strength, T[] extraAxis, Func<Cell, T, bool> filter)
    {
        var cells = Candidates(null);
        var combos = new List<(Cell Cell, T Extra)>();
        var rows = new List<string[]>();
        foreach (var cell in cells)
        {
            foreach (var extra in extraAxis)
            {
                if (filter != null && !filter(cell, extra))
                {
                    continue;
                }

                combos.Add((cell, extra));
                var values = AxisValues(cell);
                rows.Add([.. values, $"7:{extra}"]);
            }
        }

        foreach (var index in GreedyCover(rows, strength))
        {
            var (cell, extra) = combos[index];
            yield return new TestCaseData(cell, extra).SetName($"{{m}}_{cell}_{extra}");
        }
    }

    /// <summary>
    /// The greedy set-cover pass, factored away from what a row MEANS so it can be unit-tested directly and reused by both the plain and the crossed sources.
    /// Returns the indices of the chosen rows, in the order chosen.
    /// </summary>
    private static List<int> GreedyCover(List<string[]> rows, int strength)
    {
        var chosen = new List<int>();
        if (rows.Count == 0)
        {
            return chosen;
        }

        // Uncovered t-tuples, each encoded as "axisIndex:value|axisIndex:value|...". Encoding as strings keeps the generator axis-count-agnostic; the arrays
        // here are small (a few thousand rows worst case) so the allocation is irrelevant against the cost of running even one of the resulting tests.
        var uncovered = new HashSet<string>();
        var perRow = new List<string[]>(rows.Count);
        foreach (var row in rows)
        {
            var tuples = Combinations(row, strength);
            perRow.Add(tuples);
            foreach (var t in tuples)
            {
                uncovered.Add(t);
            }
        }

        while (uncovered.Count > 0)
        {
            var best = -1;
            var bestGain = 0;
            for (var i = 0; i < perRow.Count; i++)
            {
                var gain = 0;
                foreach (var t in perRow[i])
                {
                    if (uncovered.Contains(t))
                    {
                        gain++;
                    }
                }

                if (gain > bestGain)
                {
                    bestGain = gain;
                    best = i;
                }
            }

            if (best < 0)
            {
                break; // nothing left any candidate can cover — unreachable while `uncovered` is non-empty and every tuple came from some row
            }

            foreach (var t in perRow[best])
            {
                uncovered.Remove(t);
            }

            chosen.Add(best);
        }

        return chosen;
    }

    /// <summary>
    /// Every combination of <paramref name="k"/> of the row's axis values, joined into one comparable key. Returns an empty set when k &gt; the arity.
    /// </summary>
    private static string[] Combinations(string[] values, int k)
    {
        if (k > values.Length)
        {
            return [];
        }

        var result = new List<string>();
        var idx = new int[k];
        for (var i = 0; i < k; i++)
        {
            idx[i] = i;
        }

        while (true)
        {
            var parts = new string[k];
            for (var i = 0; i < k; i++)
            {
                parts[i] = values[idx[i]];
            }

            result.Add(string.Join("|", parts));

            var pos = k - 1;
            while (pos >= 0 && idx[pos] == values.Length - k + pos)
            {
                pos--;
            }

            if (pos < 0)
            {
                return [.. result];
            }

            idx[pos]++;
            for (var i = pos + 1; i < k; i++)
            {
                idx[i] = idx[i - 1] + 1;
            }
        }
    }
}
