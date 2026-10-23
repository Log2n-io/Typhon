using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace SwgTatooine.Tests;

/// <summary>
/// PRV-04, the timed half: <b>Realms D-7 measured on the demo</b> — microseconds of serial replication per active realm per tick, at SWG scale.
/// </summary>
/// <remarks>
/// <para>
/// <b>D-7, as written</b> (<c>Realms/README.md</c>, <c>12-realms § 8</c>): if the many-realm bench exceeds <b>about 1 µs of serial cost per active
/// realm</b>, the per-realm replication index is replaced by a shared realm-major one. The only evidence before this was
/// <c>Typhon.Engine.Tests.Realms.ManyRealmReplicationBench</c> — 250 synthetic one-cell rooms holding one entity and one session each, printing a figure
/// and asserting nothing about it. This runs the same comparison on the galaxy the engine is actually meant to host: about 2 500 registered interiors, a
/// live simulation on planet 0, real archetypes and a real projection.
/// </para>
/// <para>
/// <b>Paired, and the pairing is what makes it a measurement.</b> Each pair runs the same number of god cameras twice: once with all of them in <i>one</i>
/// interior, once with each in an interior of its own. Session count, entity count, archetypes, tick rate, worker count, registered realms — all
/// identical; the only difference is how many realms have to be served. The difference between the two prologue costs, over the realms that difference
/// added, is the per-realm fixed cost and nothing else. Reporting one arm alone would report the cost of sixty-four sessions and call it the cost of
/// sixty-four realms.
/// </para>
/// <para>
/// <b>Interleaved, and the median reported.</b> The two arms of a pair run back to back and the pairs run one after another, so a machine that slows down
/// half way through spoils both arms of a pair equally instead of one arm of the run. The SWG demo's CPU is bimodal run to run, which is why this reports
/// the median of the pairs and prints every one of them rather than a single mean.
/// </para>
/// <para>
/// <b><c>[Explicit]</c> and <c>Manual</c>, like its synthetic predecessor and for its reason:</b> this is a timing, and a CI runner's timing would be read
/// as a result it is not. The counted claims — that the serial cost follows the served realms and not the registered ones — are in <see
/// cref="RealmScaleChecks"/> and do run in CI, because they are checks.
/// </para>
/// <para>
/// <b>Run it with:</b> <c>dotnet test demo/SwgTatooine.Tests/SwgTatooine.Tests.csproj -c Release --filter "FullyQualifiedName~RealmScaleBench" --
/// NUnit.DisplayName=FullName</c> (add <c>--logger "console;verbosity=normal"</c> to see the per-pair lines). It takes several minutes: twelve galaxies of
/// 2 472 realms, each measured over a window of ticks. Every world is deleted as its galaxy is disposed.
/// </para>
/// </remarks>
[TestFixture]
[Explicit("A measurement, not a check: run by hand (Realms D-7, PRV-04). The counted half is RealmScaleChecks.")]
[Category("Manual")]
[NonParallelizable]
public sealed class RealmScaleBench
{
    /// <summary>
    /// God cameras per arm. The per-realm cost is the pair's difference over <c>Cameras - 1</c>, so a larger number is a finer measurement.
    /// </summary>
    private const int Cameras = 64;

    /// <summary>Planets, which is what sets the registered realm count: four gives about 2 472 realms, the scale PRV-04 asks for.</summary>
    private const int Planets = 4;

    /// <summary>Ticks each arm is measured over, after its cameras have arrived. At 40 Hz this is about ten seconds per arm.</summary>
    private const int Window = 400;

    /// <summary>Pairs. Six is this repo's floor for a CPU claim on the SWG demo, whose cost is bimodal run to run.</summary>
    private const int Pairs = 6;

    [Test]
    public void SerialCostPerActiveRealm_DecidesD7()
    {
        // <b>One arm thrown away first.</b> The first galaxy in the process pays the JIT for the whole replication pipeline and charges it to
        // whichever arm runs first, so the first measured pair would otherwise be a pair with one inflated half.
        var warmup = Arm(spread: false);
        TestContext.Out.WriteLine($"  warm-up (discarded): {warmup.Served} served, {warmup.MsPerTick * 1000d:F1} us/tick serial");

        var togetherUs = new List<double>();
        var apartUs = new List<double>();
        var perRealmUs = new List<double>();
        var rows = new List<string>();

        for (var pair = 0; pair < Pairs; pair++)
        {
            var together = Arm(spread: false);
            var apart = Arm(spread: true);
            togetherUs.Add(together.MsPerTick * 1000d);
            apartUs.Add(apart.MsPerTick * 1000d);

            // Per ADDED realm: the shared arm already serves realm 0 and one interior, so spreading the same cameras adds Cameras - 1 served realms.
            var us = (apart.MsPerTick - together.MsPerTick) * 1000d / (Cameras - 1);
            perRealmUs.Add(us);
            rows.Add(
                $"  pair {pair + 1}: together {together.Served} served, {together.MsPerTick * 1000d:F1} us/tick serial | " +
                $"apart {apart.Served} served, {apart.MsPerTick * 1000d:F1} us/tick serial | per added realm {us:F3} us");
        }

        TestContext.Out.WriteLine($"PRV-04 / Realms D-7 on the SWG demo: {Cameras} god cameras, {Planets} planets, {Window} ticks per arm, {Pairs} pairs");
        foreach (var row in rows)
        {
            TestContext.Out.WriteLine(row);
        }

        // <b>The headline is the median of the PER-PAIR differences, and which estimator to trust was settled by measurement rather than by
        // argument.</b>
        //
        // <b>Either arm is bimodal, about one arm in twelve.</b> Most arms land where their served count says they should — near 38-42 us/tick
        // shared, 50-56 apart — and one in twelve lands far above it: 65-79 shared, and in one run 98.7 apart. That is the SWG demo's known
        // run-to-run bimodality, not a warm-up effect: across five runs it appeared in the first, fourth, fifth and sixth pairs, of runs whose first
        // arm was already discarded, and on both arms. An earlier version of this comment blamed the shared arm alone; the fifth run put it on the
        // other one.
        //
        // <b>Pairing is what makes an outlier cheap.</b> A high arm drags its own pair's difference away from the truth, and a median over six pairs
        // throws that pair away whole. Taking each ARM's median first does worse, because the outlier still shifts that arm's median by half a rank
        // and the difference of the two medians moves with it. Measured over five runs — per-pair medians 0.222 / 0.199 / 0.239 / 0.202 / 0.198 us
        // against differences-of-medians 0.222 / 0.199 / 0.242 / 0.165 / 0.202 — the per-pair figure is the stable one. This comment asserted the
        // opposite first; run four refuted it.
        //
        // Both are printed anyway, because the two disagreeing by more than the spread would itself be the finding.
        var perRealm = Median(perRealmUs);
        var perPair = (Median(apartUs) - Median(togetherUs)) / (Cameras - 1);

        TestContext.Out.WriteLine(
            $"  median serial: together {Median(togetherUs):F1} us/tick over 2 served, apart {Median(apartUs):F1} us/tick over {Cameras + 1} served");
        TestContext.Out.WriteLine($"  PER ACTIVE REALM: {perRealm:F3} us  (D-7 threshold: ~1 us -> {(perRealm <= 1.0 ? "HOLDS" : "EXCEEDED")})");
        TestContext.Out.WriteLine(
            $"  cross-check, difference of the arm medians: {perPair:F3} us; " +
            $"pairs spanned {Min(perRealmUs):F3} .. {Max(perRealmUs):F3} us");

        // The only assertions are that the measurement measured something: the arms really differed in served realms (asserted in Arm) and every pair
        // produced a figure. The number itself is reported, not gated — a threshold here would be this machine's threshold, and D-7 is a design
        // decision taken from the figure.
        Assert.That(perRealmUs, Has.Count.EqualTo(Pairs));
    }

    private static double Median(List<double> values)
    {
        var sorted = new List<double>(values);
        sorted.Sort();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2d;
    }

    private static double Min(List<double> values)
    {
        var min = values[0];
        foreach (var v in values)
        {
            min = Math.Min(min, v);
        }

        return min;
    }

    private static double Max(List<double> values)
    {
        var max = values[0];
        foreach (var v in values)
        {
            max = Math.Max(max, v);
        }

        return max;
    }

    private readonly record struct ArmResult(int Served, double MsPerTick);

    /// <summary>
    /// One arm: a galaxy with <see cref="Cameras"/> god cameras, spread over interiors or sharing one, measured over <see cref="Window"/> ticks.
    /// </summary>
    /// <remarks>
    /// The prologue readings are differenced, so the ticks spent building the world and admitting the cameras are excluded — the engine also keeps this
    /// figure as a mean over the whole run, and that mean would charge this arm for a setup in which almost nothing was served.
    /// </remarks>
    private static ArmResult Arm(bool spread)
    {
        using var galaxy = RealmScaleGalaxy.Create(Planets, Cameras, spread: spread, phaseTiming: true);
        var before = galaxy.ReadPrologue();
        galaxy.Run(Window);
        var after = galaxy.ReadPrologue();

        var ticks = after.Ticks - before.Ticks;
        Assert.That(ticks, Is.GreaterThan(Window / 2), "phase timing produced too few busy ticks to measure: is SubscriptionsPhaseTiming on?");

        var served = galaxy.Read().Served;
        Assert.That(served, Is.EqualTo(spread ? Cameras + 1 : 2), spread ? "one realm per camera plus realm 0" : "one shared interior plus realm 0");

        return new ArmResult(served, (after.Ms - before.Ms) / ticks);
    }
}
