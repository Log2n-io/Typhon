using System;

namespace SwgTatooine;

/// <summary>
/// A small deterministic generator, passed by <c>ref</c> so a whole world build draws from one stream and is reproducible
/// from the seed alone.
/// </summary>
/// <remarks>
/// xorshift32 rather than <see cref="Random"/> because the world build draws millions of numbers at high population
/// multipliers and this is a handful of instructions with no allocation and no virtual call. Determinism is the point,
/// not statistical quality: two arms of a partitioning sweep must see the identical world or the comparison says nothing.
/// </remarks>
public struct Rng
{
    private uint _state;

    public Rng(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;

    /// <summary>Next raw 32-bit value.</summary>
    public uint Next()
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return _state;
    }

    /// <summary>Uniform in [0, 1).</summary>
    public float NextFloat() => (Next() >> 8) * (1f / 16_777_216f);

    /// <summary>Uniform in [min, max).</summary>
    public float NextRange(float min, float max) => min + ((max - min) * NextFloat());

    /// <summary>Uniform integer in [min, max).</summary>
    public int NextInt(int min, int max) => max <= min ? min : min + (int)(Next() % (uint)(max - min));

    /// <summary>
    /// A point uniform BY AREA inside a disc. The square root is what makes it uniform — sampling the radius linearly
    /// would pile two thirds of a city into its middle third and give every city a false density gradient.
    /// </summary>
    public (float X, float Z) PointInDisc(float cx, float cz, float radius)
    {
        var a = NextFloat() * MathF.PI * 2f;
        var r = radius * MathF.Sqrt(NextFloat());
        return (cx + (MathF.Cos(a) * r), cz + (MathF.Sin(a) * r));
    }
}
