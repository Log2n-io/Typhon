using System;
using System.Collections.Generic;
using NUnit.Framework;

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

/// <summary>What the fixture does between writing the data and asserting it.</summary>
public enum ReopenKind
{
    None,
    Clean,
    Crash,
    CleanThenCrash,
}

/// <summary>One cell of the engine's option matrix.</summary>
public readonly record struct Cell(StorageShape Shape, DurabilityMode Durability, IndexShape Index, ReopenKind Reopen)
{
    public override string ToString() => $"{Shape}_{Durability}_{Index}_{Reopen}";
}

/// <summary>
/// Covering-array sources over Typhon's option axes, so a fixture can state which axes it varies instead of hard-coding a handful of hand-picked combinations.
/// </summary>
/// <remarks>
/// <para>
/// The problem this solves: the option space is multiplicative — storage mode × durability discipline × index kind × reopen kind is 6·3·3·4 = 216 cells before
/// any workload axis — while the defects found on this branch were each a SINGLE untested cell of it, not a missing test of a well-covered path. Example-based
/// fixtures grow linearly and the space grows multiplicatively, so line coverage stays high while the matrix stays mostly empty. That is the shape of the
/// problem, and enumerating all 216 is not the answer either: it is slow and most cells are redundant.
/// </para>
/// <para>
/// Pairwise (2-wise) covering is the standard answer, and the empirical result behind it is that the large majority of defects are triggered by ONE parameter
/// or by an INTERACTION OF TWO, so covering every pair catches most of what exhaustive enumeration would, at a fraction of the size — ~18 cases here rather
/// than 216. <see cref="Triplewise"/> exists for a nightly run where the extra confidence is worth ~60 cases.
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

    /// <summary>
    /// Cells that cannot exist, as opposed to cells that merely are not interesting. A Transient component has no persisted bytes and no index home of its own
    /// on a pure-Transient archetype, so pairing it with an index or a reopen describes nothing the engine can do — generating those rows would spend cases on
    /// assertions the fixture would have to skip anyway.
    /// </summary>
    public static bool IsValid(in Cell c) =>
        !(c.Shape == StorageShape.PureTransient && c.Index != IndexShape.None)
        && !(c.Shape == StorageShape.PureTransient && c.Reopen != ReopenKind.None);

    /// <summary>Every pair of axis values appears in at least one case (~18 cases). The default source for a converted fixture.</summary>
    public static IEnumerable<TestCaseData> Pairwise() => Generate(strength: 2, filter: null);

    /// <summary>Every triple appears in at least one case (~60 cases). For a nightly or pre-merge run, not the inner loop.</summary>
    public static IEnumerable<TestCaseData> Triplewise() => Generate(strength: 3, filter: null);

    /// <summary>
    /// Pairwise over a restricted axis set. A fixture that genuinely cannot express some shapes should narrow the axis HERE and say so, rather than silently
    /// receiving cells it skips at run time — a skipped cell looks like coverage in the test count and is not.
    /// </summary>
    public static IEnumerable<TestCaseData> PairwiseWhere(Func<Cell, bool> filter) => Generate(strength: 2, filter);

    private static IEnumerable<TestCaseData> Generate(int strength, Func<Cell, bool> filter)
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
                        var cell = new Cell(shape, dur, ix, reopen);
                        if (IsValid(cell) && (filter == null || filter(cell)))
                        {
                            all.Add(cell);
                        }
                    }
                }
            }
        }

        // Uncovered t-tuples, each encoded as "axisIndex:value|axisIndex:value|...". Encoding as strings keeps the generator axis-count-agnostic; the arrays
        // here are tiny (216 rows worst case) so the allocation is irrelevant against the cost of running even one of the resulting tests.
        var uncovered = new HashSet<string>();
        foreach (var cell in all)
        {
            foreach (var t in Tuples(cell, strength))
            {
                uncovered.Add(t);
            }
        }

        var chosen = new List<Cell>();
        while (uncovered.Count > 0)
        {
            Cell best = default;
            var bestGain = -1;
            foreach (var cell in all)
            {
                var gain = 0;
                foreach (var t in Tuples(cell, strength))
                {
                    if (uncovered.Contains(t))
                    {
                        gain++;
                    }
                }

                if (gain > bestGain)
                {
                    bestGain = gain;
                    best = cell;
                }
            }

            if (bestGain <= 0)
            {
                break; // nothing left any candidate can cover — only reachable if `all` is empty
            }

            foreach (var t in Tuples(best, strength))
            {
                uncovered.Remove(t);
            }

            chosen.Add(best);
        }

        foreach (var cell in chosen)
        {
            yield return new TestCaseData(cell).SetName($"{{m}}_{cell}");
        }
    }

    private static IEnumerable<string> Tuples(Cell cell, int strength)
    {
        string[] values = [$"0:{cell.Shape}", $"1:{cell.Durability}", $"2:{cell.Index}", $"3:{cell.Reopen}"];
        return Combinations(values, strength);
    }

    private static IEnumerable<string> Combinations(string[] values, int k)
    {
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

            yield return string.Join("|", parts);

            var pos = k - 1;
            while (pos >= 0 && idx[pos] == values.Length - k + pos)
            {
                pos--;
            }

            if (pos < 0)
            {
                yield break;
            }

            idx[pos]++;
            for (var i = pos + 1; i < k; i++)
            {
                idx[i] = idx[i - 1] + 1;
            }
        }
    }
}
