using System.Collections.Generic;
using NUnit.Framework;

namespace SwgTatooine.Tests;

/// <summary>
/// PRV-04, the counted half: at SWG scale the serial per-realm replication cost follows the realms that are <b>served</b>, and registering two thousand
/// more realms adds nothing to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half a stopwatch cannot settle.</b> D-7 is written as a microsecond threshold, and a microsecond figure is exactly what would hide the
/// failure it is meant to catch: a machine fast enough reports a small number for work that is really being done, and a machine loaded enough reports a
/// large one for work that is not. What the claim actually says is structural — every serial stage walks the served set — so it is counted here and timed
/// separately, in <see cref="RealmScaleBench"/>, which is <c>[Explicit]</c> for the reason above.
/// </para>
/// <para>
/// <b>The counter is engine-wide and cumulative</b> (<c>RuntimeStatsSnapshot.RealmPassSteps</c>): each of the serial stages adds the number of realms
/// served when it ran. So a stage that walked the realm <i>registry</i> instead would leave the served set unchanged while multiplying this number by the
/// registered count — a failure no assertion over the served set itself could see, which is why the counter exists rather than the check being written
/// against <c>Served</c>.
/// </para>
/// <para>
/// <b>Rates, and why the tolerance is what it is.</b> The tick and the counter come from one snapshot, but the tick advances while the snapshot is built,
/// so a rate over a window of ticks carries at most one tick of skew. Every band below is far tighter than the difference the failure mode would make: a
/// stage walking the registry would move the per-served rate from about eight to several hundred. The bands catch a stage that stopped running as well as
/// one that grew, which a one-sided assertion would not.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class RealmScaleChecks
{
    /// <summary>Ticks each rate is measured over. Long enough that one tick of read skew is under a percent of the window.</summary>
    private const int Window = 120;

    /// <summary>
    /// The tick rate these cases run their galaxies at.
    /// </summary>
    /// <remarks>
    /// <b>Five times the demo's own 40 Hz, because every quantity here is per TICK.</b> A 120-tick window costs three seconds at 40 Hz and 0.6 at 200, and the
    /// three cases together were about a minute of a shared fifteen-minute CI job spent asleep. Unpaced would be wrong rather than merely faster — the ticks
    /// would outrun the keepalive the spin predicates send and the sessions would be closed for silence mid-measurement. The D-7 bench keeps 40 Hz, because its
    /// quantity is microseconds and its rate is part of what was measured.
    /// </remarks>
    private const int Hz = 200;

    /// <summary>
    /// AC-2a: the same number of occupied interiors costs the same serial passes per tick whether the galaxy registers about 1 200 realms or about 2 500.
    /// </summary>
    /// <remarks>
    /// <b>Two galaxies, because registration is fixed when a world is built.</b> Everything else is held: the same population scale, the same number of
    /// god cameras, the same interiors occupied (planet 0's first four, which exist in both), the same tick window. The only difference is that the second
    /// world has four planets instead of two, so it registers one realm per enterable building twice over.
    /// </remarks>
    [Test]
    public void OccupiedInteriorsSetTheSerialCostAndRegisteredOnesDoNot()
    {
        const int Occupied = 4;

        using var small = RealmScaleGalaxy.Create(planets: 2, occupied: Occupied, tickRateHz: Hz);
        var smallRegistered = small.ReadRegisteredRealms();
        var (smallBefore, smallAfter) = small.Over(Window);
        var smallRate = smallAfter.PassStepsPerTickSince(smallBefore);

        using var large = RealmScaleGalaxy.Create(planets: 4, occupied: Occupied, tickRateHz: Hz);
        var largeRegistered = large.ReadRegisteredRealms();
        var (largeBefore, largeAfter) = large.Over(Window);
        var largeRate = largeAfter.PassStepsPerTickSince(largeBefore);

        TestContext.Out.WriteLine(
            $"PRV-04 AC-2a: {smallRegistered} registered / {smallAfter.Served} served -> {smallRate:F2} passes/tick; " +
            $"{largeRegistered} registered / {largeAfter.Served} served -> {largeRate:F2} passes/tick");

        Assert.Multiple(() =>
        {
            // The premise first: if the two worlds did not really differ in registration, everything below would pass against nothing.
            Assert.That(
                largeRegistered,
                Is.GreaterThan(smallRegistered * 3 / 2),
                "precondition: four planets must register substantially more realms than two");
            Assert.That(smallAfter.Served, Is.EqualTo(Occupied + 1), "realm 0 plus the occupied interiors, and nothing else, are served");
            Assert.That(largeAfter.Served, Is.EqualTo(Occupied + 1), "the larger galaxy serves the same set: the extra planets hold no session");

            // The claim. A registry walk in any stage would put the larger galaxy's rate hundreds of times above the smaller one's, so 5 % is a wide
            // margin around the thing being asserted and a narrow one around the way it fails.
            Assert.That(largeRate, Is.EqualTo(smallRate).Within(5).Percent,
                $"registering {largeRegistered - smallRegistered} more realms changed the serial per-realm passes per tick");
        });
    }

    /// <summary>
    /// AC-2b: the serial passes per tick are proportional to the served realms — one world, sessions added, the rate per served realm unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The constant is measured, not written down.</b> How many serial stages run in a tick is a property of the pipeline (a few are conditional on
    /// sessions being bound, and the unplaced sweep runs one tick in sixty-four), so a hard-coded eight would be a number to go stale rather than a claim.
    /// The first reading establishes the per-served rate and the later ones are held to it.
    /// </para>
    /// <para>
    /// <b>Both directions matter.</b> Super-linear growth would mean a stage whose cost is quadratic in the served count; sub-linear would mean stages
    /// quietly skipping realms, which is the same bug as a session being served nothing.
    /// </para>
    /// </remarks>
    [Test]
    public void TheSerialPassesPerServedRealmAreTheSameAtOneInteriorAndAtSixteen()
    {
        using var galaxy = RealmScaleGalaxy.Create(planets: 2, occupied: 1, tickRateHz: Hz);

        var perServed = new List<(int Served, double Rate, double PerServed)>();
        foreach (var occupied in new[] { 1, 2, 4, 8, 16 })
        {
            galaxy.Occupy(occupied - galaxy.Occupied);
            var (before, after) = galaxy.Over(Window);
            var rate = after.PassStepsPerTickSince(before);
            var row = (Served: after.Served, Rate: rate, PerServed: rate / after.Served);
            perServed.Add(row);

            // <b>Printed as it is measured, before the served-count assertion below can end the case.</b> Asserting first and printing afterwards meant a
            // mismatch at the third stage reported none of the readings, which is the evidence a reader needs most.
            TestContext.Out.WriteLine($"PRV-04 AC-2b: {row.Served} served -> {row.Rate:F2} passes/tick, {row.PerServed:F3} per served realm");
            Assert.That(after.Served, Is.EqualTo(occupied + 1), $"realm 0 plus {occupied} interiors are served");
        }

        // <b>The widest pair against each other, not everything against the first reading.</b> Holding the others to <c>perServed[0]</c> compared that
        // reading with itself and gave the two-served stage a privilege it has no claim to; min against max asks the question directly, and names both ends
        // when it fails.
        var lowest = perServed[0];
        var highest = perServed[0];
        foreach (var row in perServed)
        {
            lowest = row.PerServed < lowest.PerServed ? row : lowest;
            highest = row.PerServed > highest.PerServed ? row : highest;
        }

        Assert.Multiple(() =>
        {
            Assert.That(
                lowest.PerServed,
                Is.GreaterThan(1.0),
                "precondition: a served realm is walked by several serial stages per tick, so the rate is not a rounding of zero");
            Assert.That(
                highest.PerServed,
                Is.EqualTo(lowest.PerServed).Within(5).Percent,
                $"the serial passes per served realm are not flat: {lowest.PerServed:F3} at {lowest.Served} served against "
                + $"{highest.PerServed:F3} at {highest.Served}");
        });
    }

    /// <summary>
    /// AC-3: realm policy is evaluated exactly once per tick, whatever the registered count — the one term that is allowed to scale with registration, and
    /// it must not also scale with anything else.
    /// </summary>
    /// <remarks>
    /// <c>RealmTable.EvaluatePolicy</c> is <c>O(registered realms)</c> by design, and rightly: a dormant realm is precisely one whose state has to be
    /// re-decided in case a session arrived. What would be wrong is a second evaluation per dispatch or per realm, which would turn one walk of the
    /// registry into hundreds. One per tick is a number, so it is asserted as one rather than as a band.
    /// </remarks>
    [Test]
    public void PolicyIsEvaluatedOncePerTickHoweverManyRealmsAreRegistered()
    {
        using var galaxy = RealmScaleGalaxy.Create(planets: 4, occupied: 2, tickRateHz: Hz);
        var registered = galaxy.ReadRegisteredRealms();
        var (before, after) = galaxy.Over(Window);
        var rate = after.EvaluationsPerTickSince(before);

        TestContext.Out.WriteLine(
            $"PRV-04 AC-3: {registered} registered realms, {after.PolicyEvaluations - before.PolicyEvaluations} evaluations over " +
            $"{after.Tick - before.Tick} ticks -> {rate:F3} per tick");

        Assert.Multiple(() =>
        {
            Assert.That(registered, Is.GreaterThan(1_000), "precondition: this is a galaxy, not a handful of realms");

            // Not a band: one evaluation per tick is exact. The window's ends can each be off by a tick, so the count is allowed to differ from the tick
            // count by one at each end and by nothing in between.
            Assert.That(
                after.PolicyEvaluations - before.PolicyEvaluations,
                Is.EqualTo(after.Tick - before.Tick).Within(2),
                "policy was not evaluated exactly once per tick");
        });
    }
}
