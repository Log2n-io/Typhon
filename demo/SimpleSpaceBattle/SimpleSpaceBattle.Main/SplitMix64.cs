using System.Runtime.CompilerServices;

namespace SimpleSpaceBattle;

/// <summary>
/// splitmix64 — a 64-bit state, one multiply-xor-shift chain per draw. Used only at bootstrap; nothing in the tick
/// loop draws from a stateful generator, because a shared generator would be either a contention point or a source of
/// worker-count-dependent output. Runtime "randomness" is <see cref="CombatRules.Mix"/>, a pure function.
/// </summary>
internal struct SplitMix64
{
    private ulong _state;

    public SplitMix64(ulong seed) => _state = seed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Next()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1). Takes the top 24 bits so the mantissa is filled without a bias correction.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextFloat() => (Next() >> 40) * (1f / 16777216f);

    /// <summary>
    /// Uniform on the unit sphere. Uses the cylindrical-projection identity (Archimedes) rather than rejection
    /// sampling: sample z uniformly in [-1,1] and the azimuth uniformly, which is exactly uniform in area and takes a
    /// fixed two draws.
    /// </summary>
    public void NextUnitVector(out float x, out float y, out float z)
    {
        float cosTheta = NextFloat() * 2f - 1f;
        float phi = NextFloat() * 2f * MathF.PI;
        float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));

        x = sinTheta * MathF.Cos(phi);
        y = sinTheta * MathF.Sin(phi);
        z = cosTheta;
    }
}
