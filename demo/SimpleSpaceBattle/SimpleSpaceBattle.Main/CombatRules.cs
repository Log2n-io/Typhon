using System.Runtime.CompilerServices;

namespace SimpleSpaceBattle;

/// <summary>
/// The combat rules, as pure functions of <c>(entity ids, tick)</c>.
///
/// <para>This is the heart of why the tick parallelises. A mutable cooldown would be a cross-entity read of a
/// concurrently-written field; deriving the cadence from a hash removes both the component and the hazard. Any ship
/// can evaluate any other ship's behaviour with <b>no memory access at all</b>, which is what lets the defender
/// compute the damage it receives instead of the attacker pushing it (DESIGN.md §6.1, §6.3).</para>
/// </summary>
internal static class CombatRules
{
    /// <summary>
    /// splitmix64 finalizer. Used as a cheap, well-distributed per-ship phase offset so the fleet does not fire in
    /// unison — not as a random number generator.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mix(long key)
    {
        ulong z = (ulong)key + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>
    /// Whether the ship fires this tick. <paramref name="intervalMask"/> is <c>FireIntervalTicks - 1</c>, which is
    /// why the interval is required to be a power of two.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Fires(long rawId, ulong tick, int intervalMask) => ((tick + Mix(rawId)) & (ulong)intervalMask) == 0UL;

    /// <summary>
    /// Whether a shot connects. Certain at point blank, ~50 % at maximum range, linear in <c>distSq</c>.
    /// <para>
    /// Deliberately symmetric in the pair — <c>shooter ^ target</c> — so attacker and defender would compute an
    /// identical roll. Only the defender actually evaluates it, but the symmetry is what makes the pull formulation
    /// provably equivalent to a push (test <c>PullEqualsPush</c>).
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Hits(long shooterRawId, long targetRawId, ulong tick, float distSq, float weaponRangeSq)
    {
        ulong roll = Mix(shooterRawId ^ targetRawId ^ (long)tick) & 1023UL;
        float threshold = 1024f - 512f * distSq / weaponRangeSq;
        return roll < (ulong)(int)threshold;
    }
}
