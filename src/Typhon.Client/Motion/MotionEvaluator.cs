using System;

namespace Typhon.Client;

/// <summary>
/// Evaluates motion segments (03-wire-protocol § 6) in 2 or 3 dimensions: where an entity is at render time, and how fast it is going.
/// </summary>
/// <remarks>
/// <para>
/// <b>Time is integer ticks plus a fraction.</b> Render time arrives as <c>renderTick</c> (an integer) and <c>frac</c> in [0, 1), and a segment's <c>t0</c> is
/// an integer tick. <c>dt = (renderTick − t0) + frac</c> subtracts the integers first, so no precision is lost however long the server has been running —
/// unlike an absolute tick held in a <see cref="float"/>, whose resolution degrades to a quarter tick after three days at 10 Hz. Velocities are metres per
/// tick, so a changing tick period never distorts motion.
/// </para>
/// <para>
/// <b>Which segment.</b> The newest whose start tick render time has reached; before all of them, the oldest one held. An epoch change (a teleport) is never
/// crossed.
/// </para>
/// <para>
/// <b>Per model.</b>
/// <list type="bullet">
/// <item><description><c>linear</c>: <c>p(τ) = p0 + v · (τ − t0)</c>, extrapolated past the newest segment and backwards before the oldest.</description></item>
/// <item><description><c>none</c>: segments are position samples. Between a sample and the next one of the same epoch the position is interpolated and the
/// velocity is the one between them; otherwise the sample holds, with zero velocity.</description></item>
/// <item><description><c>static</c>: the position entered with, zero velocity.</description></item>
/// </list>
/// </para>
/// <para>
/// This is a port of the TypeScript SDK's <c>motion/motion.ts</c>, operation for operation and in the same order, so both SDKs return the same double from the
/// same segments. Nothing here allocates.
/// </para>
/// </remarks>
public static class MotionEvaluator
{
    /// <summary>The largest <see cref="ArchetypeStore.MotionStride"/>: a scratch buffer this long fits any archetype's evaluated motion.</summary>
    public const int MaxStride = 6;

    /// <summary>The ring entry of the segment that applies at <paramref name="renderTick"/>.</summary>
    /// <param name="store">The archetype's store.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="renderTick">Render time's integer tick.</param>
    /// <returns>The ring entry.</returns>
    public static int EntryAt(ArchetypeStore store, int slot, long renderTick)
    {
        ArgumentNullException.ThrowIfNull(store);
        return EntryOf(store.Segments, slot, renderTick);
    }

    /// <summary>The motion epoch in force at <paramref name="renderTick"/>: a change between two render times means the entity teleported between them.</summary>
    /// <param name="store">The archetype's store.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="renderTick">Render time's integer tick.</param>
    /// <returns>The epoch.</returns>
    /// <exception cref="InvalidOperationException">The archetype has no position.</exception>
    public static byte EpochAt(ArchetypeStore store, int slot, long renderTick)
    {
        RequirePosition(store);
        return store.Segments.Epoch(slot, EntryOf(store.Segments, slot, renderTick));
    }

    /// <summary>Position and velocity of one slot at render time: <c>p[Dims]</c> then <c>v[Dims]</c>.</summary>
    /// <param name="store">The archetype's store.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="renderTick">Render time's integer tick.</param>
    /// <param name="frac">The fraction of a tick past it, in [0, 1).</param>
    /// <param name="destination">At least <see cref="ArchetypeStore.MotionStride"/> doubles; only that many are written.</param>
    /// <exception cref="InvalidOperationException">The archetype has no position.</exception>
    /// <exception cref="ArgumentException">The destination is too short.</exception>
    public static void EvaluateSlot(ArchetypeStore store, int slot, long renderTick, double frac, Span<double> destination)
    {
        RequirePosition(store);
        if (destination.Length < store.MotionStride)
        {
            throw new ArgumentException($"an evaluated slot needs {store.MotionStride} doubles, not {destination.Length}", nameof(destination));
        }

        var ring = store.Segments;
        Evaluate(ring, store.Linear || !store.Moving, slot, EntryOf(ring, slot, renderTick), renderTick, frac, destination);
    }

    /// <summary>
    /// Evaluates every live entity of an archetype, in live order: entry <c>i</c> describes <see cref="ArchetypeStore.Live"/><c>[i]</c>.
    /// </summary>
    /// <param name="store">The archetype's store.</param>
    /// <param name="renderTick">Render time's integer tick.</param>
    /// <param name="frac">The fraction of a tick past it, in [0, 1).</param>
    /// <param name="destination"><see cref="ArchetypeStore.LiveCount"/> × <see cref="ArchetypeStore.MotionStride"/> doubles.</param>
    /// <exception cref="InvalidOperationException">The archetype has no position.</exception>
    /// <exception cref="ArgumentException">The destination is too short.</exception>
    public static void EvaluateLive(ArchetypeStore store, long renderTick, double frac, Span<double> destination)
    {
        RequirePosition(store);
        var stride = store.MotionStride;
        var count = store.LiveCount;
        if (destination.Length < count * stride)
        {
            throw new ArgumentException($"evaluating {count} live entities needs {count * stride} doubles, not {destination.Length}", nameof(destination));
        }

        var ring = store.Segments;
        var extrapolating = store.Linear || !store.Moving;
        var live = store.Live;
        for (var i = 0; i < count; i++)
        {
            var slot = live[i];
            Evaluate(ring, extrapolating, slot, EntryOf(ring, slot, renderTick), renderTick, frac, destination.Slice(i * stride, stride));
        }
    }

    /// <summary>
    /// Heading in radians of a velocity in a plane, <c>atan2(u, v)</c>: 0 along +<paramref name="v"/>, π/2 along +<paramref name="u"/>.
    /// </summary>
    /// <param name="u">The component the heading turns towards.</param>
    /// <param name="v">The component the heading is measured from.</param>
    /// <param name="fallback">Returned when both components are zero.</param>
    /// <returns>The heading, or the fallback.</returns>
    /// <remarks>Which evaluated axes are <paramref name="u"/> and <paramref name="v"/> is the application's convention, not the protocol's.</remarks>
    public static double HeadingOf(double u, double v, double fallback) => u == 0 && v == 0 ? fallback : Math.Atan2(u, v);

    // Newest to oldest, wrapping by a branch rather than a remainder; the oldest held applies when render time precedes them all.
    private static int EntryOf(SegmentRing ring, int slot, long renderTick)
    {
        var last = ring.Depth - 1;
        var held = ring.Held(slot);
        var entry = ring.Head(slot);
        for (var k = 1; k < held; k++)
        {
            if (renderTick >= ring.T0(slot, entry))
            {
                return entry;
            }

            entry = entry == 0 ? last : entry - 1;
        }

        return entry;
    }

    private static void Evaluate(SegmentRing ring, bool extrapolating, int slot, int entry, long renderTick, double frac, Span<double> destination)
    {
        var dims = ring.Dims;
        var segment = ring.Segment(slot, entry);
        var t0 = ring.T0(slot, entry);
        var dt = (renderTick - t0) + frac;

        if (extrapolating)
        {
            for (var a = 0; a < dims; a++)
            {
                var v = segment[dims + a];
                destination[a] = segment[a] + (v * dt);
                destination[dims + a] = v;
            }

            return;
        }

        // Samples: interpolate toward the next newer sample of the same epoch, when one is held and render time has left t0.
        var next = entry == ring.Depth - 1 ? 0 : entry + 1;
        var t1 = ring.T0(slot, next);
        if (entry != ring.Head(slot) && t1 > t0 && dt >= 0 && ring.Epoch(slot, next) == ring.Epoch(slot, entry))
        {
            var span = t1 - t0;
            var u = dt < span ? dt / span : 1;
            var to = ring.Position(slot, next);
            for (var a = 0; a < dims; a++)
            {
                var p0 = segment[a];
                var delta = to[a] - p0;
                destination[a] = p0 + (delta * u);
                destination[dims + a] = dt < span ? delta / span : 0;
            }

            return;
        }

        for (var a = 0; a < dims; a++)
        {
            destination[a] = segment[a];
            destination[dims + a] = 0;
        }
    }

    private static void RequirePosition(ArchetypeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.Dims == 0)
        {
            throw new InvalidOperationException($"archetype '{store.Plan.Name}' has no position");
        }
    }
}
