using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// T12 (impl/1418 §8.2, AC-A2, BKP-11): over an archived workload that crosses a clean reopen, a hard crash and an offline point, a restore to an LSN opens
/// to exactly the committed state at <c>L'</c> — the last frame ending at or below the target — for frame ends and for LSNs inside a frame; a restore to a
/// sampled instant opens to the state at the sample's LSN and holds no commit durable after it.
/// </summary>
[NonParallelizable]
[Category("Seeded")]
class RestoreToLsnOracleTests : ArchiveTestBase<RestoreToLsnOracleTests>
{
    private const string Marker = "BKP-11: a restore to an LSN";

    [Test]
    [CancelAfter(120_000)]
    [VerifiesRule("BKP-11")]
    public void EveryTarget_RestoresToTheCommittedStateAtTheLastFrameEndingAtOrBelowIt()
    {
        var run = RunArchivedWorkload(TestSeed.For("bkp-archive-oracle"));
        var failures = new List<string>();
        var bases = new HashSet<uint>();
        var forced = 0;
        foreach (var (target, expectedCut) in Targets(run, 40))
        {
            var (result, versioned, singleVersion, crashPath) = RestoreAndRead(PointSelector.AtLsn(target), "l" + target);
            bases.Add(result.Point.BackupId);
            forced += result.ForcedCrashRecovery ? 1 : 0;
            if (result.ForcedCrashRecovery && !crashPath)
            {
                failures.Add($"LSN {target}: a clean base restored past its WAL opened on the clean path");
            }

            if (result.EffectiveLsn != expectedCut)
            {
                failures.Add($"LSN {target}: L' is {result.EffectiveLsn}, the last frame end at or below it is {expectedCut}");
                continue;
            }

            var diff = Diff(run.Expected(expectedCut), versioned, singleVersion);
            if (diff != null)
            {
                failures.Add($"{Marker} {target} (L' {expectedCut}, base #{result.Point.BackupId}): {diff}");
            }
        }

        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures.Take(10)));
        Assert.That(bases, Is.EquivalentTo(run.Points), "both the online point and the offline clean point served as bases");
        Assert.That(forced, Is.GreaterThan(0), "a clean base restored past its own WAL (WA-12) was exercised");
    }

    /// <summary>The verifier must tell adjacent frames apart: a restore one frame past the target is not the target's state.</summary>
    [Test]
    [CancelAfter(120_000)]
    [RuleMutant("BKP-11")]
    public void Mutant_ARestoreOneFramePastTheTarget_IsReported()
    {
        var run = RunArchivedWorkload(TestSeed.For("bkp-archive-oracle"));
        var commits = run.History.Select(h => h.Lsn).Distinct().ToList();
        var (target, past) = run.Frames.Select(f => f.LastLsn).Zip(run.Frames.Skip(1).Select(f => f.LastLsn))
            .First(p => p.First > run.History[2].Lsn && Diff(run.Expected(p.First), run.Expected(p.Second).Versioned, run.Expected(p.Second).SingleVersion) != null
                                                     && commits.Contains(p.Second));
        var (_, versioned, singleVersion, _) = RestoreAndRead(PointSelector.AtLsn(past), "mutant");
        RuleMutants.AssertDetects("BKP-11", Marker, () =>
            Assert.That(Diff(run.Expected(target), versioned, singleVersion), Is.Null, $"{Marker} {target}: the restored state is not the target's"));
    }

    [Test]
    [CancelAfter(120_000)]
    [VerifiesRule("BKP-11")]
    public void EverySampledInstant_RestoresToItsSample_AndNoCommitDurableAfterIt()
    {
        var run = RunArchivedWorkload(TestSeed.For("bkp-archive-oracle-time"));
        var pointP = run.History[1].Lsn;
        var samples = run.Samples.Where(s => s.Lsn >= pointP).ToList();
        Assert.That(samples.Count, Is.GreaterThan(4), "premise: samples to restore to");
        var failures = new List<string>();
        foreach (var s in samples.Where((_, i) => i % Math.Max(1, samples.Count / 6) == 0))
        {
            var at = new DateTime(s.UtcTicks, DateTimeKind.Utc);
            var (result, versioned, singleVersion, _) = RestoreAndRead(PointSelector.AtTime(at), "t" + s.Lsn);
            if (result.TargetLsn != s.Lsn)
            {
                failures.Add($"time {at:O}: resolved to LSN {result.TargetLsn}, the sample says {s.Lsn}");
                continue;
            }

            var diff = Diff(run.Expected(result.EffectiveLsn), versioned, singleVersion);
            if (diff != null || result.EffectiveLsn > s.Lsn)
            {
                failures.Add($"{Marker} at {at:O} (LSN {s.Lsn}, L' {result.EffectiveLsn}): {diff ?? "past the sample"}");
            }
        }

        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures.Take(10)));
    }

    /// <summary>
    /// The targets: every frame end past the first point's P, and an LSN inside each frame of several records, thinned to <paramref name="max"/> evenly
    /// spread; with the frame end at or below each (the expected <c>L'</c>).
    /// </summary>
    private static List<(long Target, long Cut)> Targets(ArchivedWorkloadResult run, int max)
    {
        var floor = run.History[1].Lsn;   // the first point's P
        var ends = run.Frames.Select(f => f.LastLsn).Where(l => l >= floor).Distinct().OrderBy(l => l).ToList();
        var all = new List<(long, long)>();
        foreach (var f in run.Frames.Where(f => f.LastLsn >= floor))
        {
            all.Add((f.LastLsn, f.LastLsn));
            if (f.Records > 1 && f.FirstLsn > floor)
            {
                var previous = ends.LastOrDefault(e => e < f.LastLsn);
                all.Add((f.FirstLsn, previous));
            }
        }

        all = all.Distinct().OrderBy(t => t.Item1).ToList();
        var step = Math.Max(1, all.Count / max);
        return all.Where((_, i) => i % step == 0 || i == all.Count - 1).ToList();
    }
}
