namespace SwgTatooine;

/// <summary>
/// What ground that rises costs to cross — terrain rung (c).
/// </summary>
/// <remarks>
/// <para>
/// One function, called by all three movers, and a pure function of the grade. It is deliberately not a method on
/// anything: a speed curve that lives on the bridge acquires a dependency on the bridge's state the first time someone
/// wants to make it depend on the creature, and then it is no longer testable as a curve.
/// </para>
/// <para>
/// <b>Downhill is not faster than flat.</b> A speed-up on descent is the half of this that produces creatures
/// accelerating off the edges of mesas, and nothing asked for it.
/// </para>
/// </remarks>
internal static class SlopeSpeed
{
    /// <summary>
    /// Grade below which a rise costs nothing: gentle ground is still walked at full speed. About six degrees.
    /// </summary>
    /// <remarks>
    /// Set against the terrain rather than guessed. Over a tick's step on this planet the uphill grades run p50 0.069,
    /// p90 0.176, p99 0.314, with a tail to 1.12 — so a threshold of 0.2 would have engaged the top seven per cent of
    /// climbs and a floor at 0.6 would have been reached by almost nothing. The feature would have existed and not
    /// happened. These two numbers put the slowdown on roughly the steepest sixth of climbs and the floor on the tail.
    /// </remarks>
    internal const float FlatGrade = 0.15f;

    /// <summary>Grade at which the slowdown reaches its floor, and past which nothing changes. About twenty-four degrees.</summary>
    internal const float SteepGrade = 0.45f;

    /// <summary>
    /// The slowest an entity may be made to walk, as a fraction of its own speed.
    /// </summary>
    /// <remarks>
    /// Not zero, and not nearly zero. A wander leg terminates when the entity ARRIVES, so an entity whose speed the
    /// terrain can take to zero never arrives, never re-decides, and sits against the hill until something else moves it.
    /// A floor is what keeps a behaviour out of the terrain's reach.
    /// </remarks>
    internal const float MinFactor = 0.35f;

    /// <summary>
    /// The fraction of its speed an entity keeps while crossing ground of this grade.
    /// </summary>
    /// <param name="grade">Rise over run along the direction of travel: positive uphill, negative down.</param>
    /// <returns>Between <see cref="MinFactor"/> and 1, inclusive.</returns>
    internal static float FactorFor(float grade)
    {
        // NaN is not reachable from a finite step over a finite field, but a factor of NaN would put the entity nowhere
        // and the placement codec would carry it, so the comparison is written to let NaN fall through to 1.
        if (!(grade > FlatGrade))
        {
            return 1f;
        }

        if (grade >= SteepGrade)
        {
            return MinFactor;
        }

        var t = (grade - FlatGrade) / (SteepGrade - FlatGrade);
        return 1f - (t * (1f - MinFactor));
    }

    /// <summary>
    /// The factor for a step, from the ground it starts on and the ground its full-speed end lands on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On flat ground this reads the heightfield zero extra times, which is the design.</b> Rung (b) guarantees that a
    /// placement's stored <c>Y</c> is the ground under its stored <c>(x, z)</c> — <c>TerrainAltitudeChecks</c> holds it to
    /// a millimetre over every planet placement — and the mover already samples the ground at the step's end to write the
    /// new <c>Y</c>. Both ends of the step are therefore already in hand, and the rise over the run is arithmetic.
    /// </para>
    /// <para>
    /// A <b>second</b> sample is taken only where the answer is not 1, because a shortened step ends somewhere else and
    /// rung (b)'s invariant is that <c>Y</c> is the ground at the position actually written. The extra read is paid
    /// exactly where the feature does something, and the flat majority of the planet pays nothing at all.
    /// </para>
    /// <para>
    /// Asking the field for a gradient instead — the obvious shape, and <c>Heightfield.GradientAt</c> already exists —
    /// costs <b>four</b> more bilinear reads of a 67 MB array per moved entity per tick, everywhere, on a phase where one
    /// such read was measured this session at 21 % of its time.
    /// </para>
    /// </remarks>
    /// <param name="fromGroundY">Ground height where the step begins — the placement's stored <c>Y</c>.</param>
    /// <param name="toGroundY">Ground height at the full-speed end, which the caller has just sampled.</param>
    /// <param name="runM">Horizontal distance of that step, in metres.</param>
    /// <returns>The factor to scale the step by; 1 for a step too short to have a direction.</returns>
    internal static float FactorForStep(float fromGroundY, float toGroundY, float runM)
    {
        // A step shorter than a millimetre has no meaningful grade: the division would amplify float noise in the two
        // heights into an arbitrary factor, and an entity that is not going anywhere does not need slowing down.
        if (!(runM > 0.001f))
        {
            return 1f;
        }

        return FactorFor((toGroundY - fromGroundY) / runM);
    }
}
